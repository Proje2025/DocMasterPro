using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DocConverter.Models;

namespace DocConverter.Services
{
    /// <summary>
    /// Bir tarama oturumunun sonucu.
    /// </summary>
    public sealed record ScanSessionResult(
        List<ScannedPageItem> Pages,
        bool Cancelled,
        int BlankPagesSkipped,
        string? Warning,
        string? Notice);

    public class ScannerService
    {
        private static readonly string ScanTempDir = Path.Combine(Path.GetTempPath(), "DocMaster_Scans");
        private static readonly TimeSpan StaleSessionAge = TimeSpan.FromDays(7);
        private const double BlankPageThreshold = 98.5;

        private readonly BlankPageDetector _blankDetector = new();
        private readonly Func<IScanSession> _sessionFactory;

        public ScannerService()
            : this(() => new WiaScanSession())
        {
        }

        /// <summary>Testlerde gerçek donanım yerine sahte bir oturum vermek için.</summary>
        public ScannerService(Func<IScanSession> sessionFactory)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            Directory.CreateDirectory(ScanTempDir);
            PurgeStaleTempFiles();
        }

        /// <summary>
        /// Seçili tarayıcıdan (Fujitsu fi serisi, WIA cihazı vb.) gerçek donanım üzerinden belge tarar.
        /// </summary>
        public async Task<List<ScannedPageItem>> ScanDocumentsAsync(
            DeviceInfo scanner,
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            var result = await ScanAsync(scanner, options, cancellationToken, progress);
            return result.Pages;
        }

        /// <summary>
        /// Belge tarar; her sayfa hazır olduğunda <paramref name="pageProgress"/> ile bildirir.
        /// Hiç sayfa alınamazsa (iptal dışında) <see cref="ScanFailedException"/> fırlatır.
        /// </summary>
        public Task<ScanSessionResult> ScanAsync(
            DeviceInfo scanner,
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null,
            IProgress<ScannedPageItem>? pageProgress = null)
        {
            if (scanner == null)
                throw new InvalidOperationException("Lütfen tarama yapılacak bir tarayıcı seçin.");

            ArgumentNullException.ThrowIfNull(options);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Tarayıcı işlemleri yalnızca Windows'ta desteklenir.");

            string sessionFolder = CreateSessionFolder("Scan");
            progress?.Report($"{scanner.Name} hazırlanıyor...");

            return RunOnStaThreadAsync(() =>
            {
                var pages = new List<ScannedPageItem>();
                int blankSkipped = 0;

                var outcome = ScanAcquisition.Run(
                    _sessionFactory,
                    scanner,
                    options,
                    index => Path.Combine(sessionFolder, $"page_{index:D4}.png"),
                    (path, session) =>
                    {
                        if (options.RemoveBlankPages && _blankDetector.IsImageBlank(path, BlankPageThreshold))
                        {
                            blankSkipped++;
                            TryDelete(path);
                            progress?.Report($"Boş sayfa atlandı ({blankSkipped}).");
                            return;
                        }

                        var page = LoadScannedPageItem(path, pages.Count + 1, session.EffectiveDpi);
                        pages.Add(page);
                        pageProgress?.Report(page);
                        progress?.Report($"Sayfa {pages.Count} tarandı ({session.EffectiveDpi} DPI).");
                    },
                    progress,
                    cancellationToken);

                if (pages.Count == 0 && blankSkipped == 0)
                    TryDeleteDirectory(sessionFolder);

                return new ScanSessionResult(pages, outcome.Cancelled, blankSkipped, outcome.Warning, outcome.Notice);
            });
        }

        /// <summary>
        /// Windows'un kendi tarama penceresini (WIA Common Dialog) açar; ayarları kullanıcı o pencerede seçer.
        /// </summary>
        public Task<List<ScannedPageItem>> ScanViaWiaNativeDialogAsync(
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            string sessionFolder = CreateSessionFolder("Scan_Native");
            progress?.Report("Windows tarama penceresi açılıyor...");

            return RunOnStaThreadAsync(() =>
            {
                var pages = new List<ScannedPageItem>();

                Type? dialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
                if (dialogType == null)
                    throw new ScanFailedException("Windows WIA CommonDialog bileşeni sistemde bulunamadı.");

                dynamic dialog = Activator.CreateInstance(dialogType)
                                 ?? throw new ScanFailedException("WIA CommonDialog başlatılamadı.");
                dynamic? imageFile = null;

                try
                {
                    int intent = options.ColorMode switch
                    {
                        ScanColorMode.Grayscale => 2,
                        ScanColorMode.BlackAndWhite => 4,
                        _ => 1
                    };

                    // ShowAcquireImage(DeviceType = Scanner, Intent, Bias, Format, AlwaysSelectDevice, UseCommonUI, CancelError)
                    imageFile = dialog.ShowAcquireImage(1, intent, 0, WiaScanSession.FormatPng, false, true, false);
                    if (imageFile == null)
                        return pages;

                    string pagePath = Path.Combine(sessionFolder, "page_0001.png");
                    if (File.Exists(pagePath))
                        File.Delete(pagePath);
                    imageFile.SaveFile(pagePath);

                    int dpi = (int)options.Resolution;
                    try
                    {
                        int reported = (int)Math.Round((double)imageFile.HorizontalResolution);
                        if (reported >= 50 && reported <= 2400)
                            dpi = reported;
                    }
                    catch
                    {
                        // Görüntü DPI bildirmiyor.
                    }

                    pages.Add(LoadScannedPageItem(pagePath, 1, dpi));
                    return pages;
                }
                catch (Exception ex) when (ex is not ScanFailedException)
                {
                    if (WiaErrors.Classify(ex) == WiaFailureKind.Cancelled)
                        return pages;

                    FileLogger.LogError("WIA yerel tarama penceresi", ex);

                    // 0x80210015: sistemde kullanılabilir WIA cihazı yok.
                    string message = WiaErrors.CodeOf(ex) == 0x80210015
                        ? "Windows'ta kullanılabilir bir WIA tarayıcı bulunamadı. Tarayıcının açık ve bağlı olduğunu kontrol edin."
                        : WiaErrors.Describe(ex);
                    throw new ScanFailedException(message, ex);
                }
                finally
                {
                    WiaScanSession.Release(imageFile);
                    WiaScanSession.Release(dialog);

                    if (pages.Count == 0)
                        TryDeleteDirectory(sessionFolder);
                }
            });
        }

        /// <summary>
        /// Taranan sayfaları varsayılan (dengeli) sıkıştırmayla tek bir PDF'te birleştirir.
        /// </summary>
        public async Task<string> SaveScannedPagesToPdfAsync(
            List<ScannedPageItem> pages,
            string outputPath,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            var result = await SaveScannedPagesAsync(pages, outputPath, new ScanPdfOptions(), cancellationToken, progress);
            return result.OutputPath;
        }

        /// <summary>
        /// Taranan sayfaları seçilen kalite profiliyle sıkıştırıp tek bir PDF'e yazar.
        /// </summary>
        public Task<ScanPdfResult> SaveScannedPagesAsync(
            IReadOnlyList<ScannedPageItem> pages,
            string outputPath,
            ScanPdfOptions options,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(pages);

            // Koleksiyon arayüz iş parçacığına ait olabilir; arka plana geçmeden önce kopyalanır.
            var sources = pages
                .Select(p => new ScanPageSource(p.FilePath, p.Rotation, p.ResolutionDpi))
                .ToList();

            return Task.Run(
                () => ScannedPdfBuilder.Build(sources, outputPath, options, cancellationToken, progress),
                cancellationToken);
        }

        /// <summary>Artık kullanılmayan sayfa görüntülerini diskten siler.</summary>
        public void DeletePageFiles(IEnumerable<ScannedPageItem> pages)
        {
            ArgumentNullException.ThrowIfNull(pages);

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in pages)
            {
                if (string.IsNullOrEmpty(page.FilePath) || !IsInsideScanTemp(page.FilePath))
                    continue;

                TryDelete(page.FilePath);

                string? folder = Path.GetDirectoryName(page.FilePath);
                if (!string.IsNullOrEmpty(folder))
                    folders.Add(folder);
            }

            foreach (string folder in folders)
            {
                try
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                catch
                {
                    // Klasör başka bir işlem tarafından kullanılıyor; bir sonraki temizlikte silinir.
                }
            }
        }

        private static bool IsInsideScanTemp(string path)
        {
            try
            {
                string root = Path.GetFullPath(ScanTempDir) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string CreateSessionFolder(string prefix)
        {
            // Aynı saniyede açılan iki oturum çakışmasın diye kısa bir rastgele ek.
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string folder = Path.Combine(ScanTempDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{suffix}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Önceki oturumlardan kalan sayfa görüntülerini ve PDF Studio'ya aktarım kopyalarını temizler.
        /// 300 DPI renkli bir sayfa ~20 MB tuttuğu için temizlenmezse geçici klasör hızla dolar.
        /// </summary>
        private static void PurgeStaleTempFiles()
        {
            try
            {
                DateTime limit = DateTime.Now - StaleSessionAge;

                foreach (string folder in Directory.EnumerateDirectories(ScanTempDir))
                {
                    try
                    {
                        if (Directory.GetLastWriteTime(folder) < limit)
                            Directory.Delete(folder, recursive: true);
                    }
                    catch
                    {
                        // Kullanımda olabilir.
                    }
                }

                foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), "DocMaster_Scanned_*.pdf"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < limit)
                            File.Delete(file);
                    }
                    catch
                    {
                        // Kullanımda olabilir.
                    }
                }
            }
            catch
            {
                // Temizlik en iyi çaba ile yapılır; taramayı engellememeli.
            }
        }

        /// <summary>
        /// WIA Automation nesneleri apartment-threaded'dir ve tarama penceresi ileti döngüsü ister;
        /// bu yüzden tarama iş parçacığı havuzunda (MTA) değil, kendi STA iş parçacığında yürütülür.
        /// </summary>
        private static Task<T> RunOnStaThreadAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "DocMaster WIA Scan"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        private static ScannedPageItem LoadScannedPageItem(string filePath, int pageNumber, int dpi)
        {
            BitmapSource? preview = null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.DecodePixelWidth = 900;
                bitmap.EndInit();
                bitmap.Freeze();
                preview = bitmap;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"Tarama önizlemesi yüklenemedi ({filePath})", ex);
            }

            return new ScannedPageItem
            {
                PageNumber = pageNumber,
                FilePath = filePath,
                PreviewImage = preview,
                ResolutionDpi = dpi,
                FileSize = new FileInfo(filePath).Length,
                Rotation = 0
            };
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Dosya kullanımda; eski oturum temizliği daha sonra siler.
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Bir sonraki temizlikte silinir.
            }
        }
    }
}
