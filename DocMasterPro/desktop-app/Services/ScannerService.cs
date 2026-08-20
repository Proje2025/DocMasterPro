using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DocConverter.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocConverter.Services
{
    public class ScannerService
    {
        private static readonly string ScanTempDir = Path.Combine(Path.GetTempPath(), "DocMaster_Scans");
        private readonly BlankPageDetector _blankDetector = new();

        public ScannerService()
        {
            if (!Directory.Exists(ScanTempDir))
            {
                Directory.CreateDirectory(ScanTempDir);
            }
        }

        /// <summary>
        /// Seçili tarayıcıdan (Fujitsu fi-6230, WIA cihazı vb.) gerçek donanım üzerinden belge tarama işlemini gerçekleştirir.
        /// Kesinlikle sahte/mock veri üretmez.
        /// </summary>
        public async Task<List<ScannedPageItem>> ScanDocumentsAsync(
            DeviceInfo scanner,
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            if (scanner == null)
            {
                throw new InvalidOperationException("Lütfen tarama yapılacak bir tarayıcı seçin.");
            }

            progress?.Report($"{scanner.Name} üzerinden gerçek tarama başlatılıyor...");

            var scannedPages = new List<ScannedPageItem>();
            string sessionFolder = Path.Combine(ScanTempDir, $"Scan_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(sessionFolder);

            await Task.Run(() =>
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    throw new PlatformNotSupportedException("Tarayıcı işlemleri sadece Windows işletim sisteminde desteklenmektedir.");
                }

                bool success = PerformWiaScan(scanner, options, sessionFolder, scannedPages, progress, cancellationToken);
                if (!success && scannedPages.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"'{scanner.Name}' cihazından görüntü alınamadı.\n\n" +
                        "Olası nedenler:\n" +
                        "1. Tarayıcının USB veya ağ kablosu takılı olmayabilir.\n" +
                        "2. Tarayıcı kapalı veya uyku modunda olabilir.\n" +
                        "3. ADF (Otomatik Belge Besleyici) seçiliyse besleyicide kağıt bulunmuyor olabilir.\n" +
                        "4. Tarayıcı sürücüsü (WIA/TWAIN) Windows tarafından tanınmamış olabilir.");
                }
            }, cancellationToken);

            progress?.Report($"Tarama tamamlandı: {scannedPages.Count} sayfa başarıyla tarandı.");
            return scannedPages;
        }

        private bool PerformWiaScan(
            DeviceInfo scanner,
            ScanOptions options,
            string outputDir,
            List<ScannedPageItem> scannedPages,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            Type? deviceManagerType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (deviceManagerType == null)
            {
                throw new InvalidOperationException("Windows WIA (Windows Image Acquisition) servisi sistemde bulunamadı.");
            }

            dynamic? deviceManager = Activator.CreateInstance(deviceManagerType);
            if (deviceManager == null)
            {
                throw new InvalidOperationException("WIA DeviceManager başlatılamadı.");
            }

            dynamic deviceInfos = deviceManager.DeviceInfos;
            dynamic? targetDeviceInfo = null;

            int count = (int)deviceInfos.Count;
            var availableWiaNames = new List<string>();

            for (int i = 1; i <= count; i++)
            {
                dynamic info = deviceInfos[i];
                string id = (string)info.DeviceID;
                string pName = (string)info.Properties["Name"].Value;
                availableWiaNames.Add(pName);

                if (id == scanner.SerialOrHardwareId ||
                    pName.Contains(scanner.Name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(scanner.ModelName) && pName.Contains(scanner.ModelName, StringComparison.OrdinalIgnoreCase)) ||
                    (scanner.IsFujitsuSpecial && (pName.Contains("6230", StringComparison.OrdinalIgnoreCase) || pName.Contains("Fujitsu", StringComparison.OrdinalIgnoreCase))) ||
                    (scanner.IsRicohSpecial && (pName.Contains("4510", StringComparison.OrdinalIgnoreCase) || pName.Contains("Ricoh", StringComparison.OrdinalIgnoreCase))))
                {
                    targetDeviceInfo = info;
                    break;
                }
            }

            if (targetDeviceInfo == null)
            {
                if (count == 0)
                {
                    throw new InvalidOperationException(
                        $"Windows'ta kayıtlı hiçbir WIA tarayıcı cihazı bulunamadı.\n\n" +
                        $"'{scanner.Name}' cihazını tarayıcı olarak kullanabilmek için:\n" +
                        "1. Tarayıcının USB veya ağ kablosunun takılı ve açık olduğunu kontrol edin.\n" +
                        "2. Cihazın Windows WIA veya Network TWAIN sürücüsünün kurulu olduğundan emin olun.\n" +
                        "3. Çok fonksiyonlu Ricoh veya Fujitsu cihazınız için 'Windows Tarama Penceresini Aç' seçeneğini kullanabilirsiniz.");
                }

                if (scanner.Type == DeviceType.Scanner)
                {
                    targetDeviceInfo = deviceInfos[1];
                }
                else
                {
                    throw new InvalidOperationException(
                        $"'{scanner.Name}' aygıtı bir tarayıcı (WIA) olarak tanınamadı.\n\n" +
                        $"Sistemdeki mevcut aktif tarayıcılar:\n• {string.Join("\n• ", availableWiaNames)}\n\n" +
                        "Lütfen listeden geçerli bir tarayıcı seçin veya 'Windows Tarama Penceresini Aç' butonunu kullanın.");
                }
            }

            dynamic device = targetDeviceInfo.Connect();
            dynamic item = device.Items[1];

            // WIA Parametrelerini ayarla (DPI, Renk Modu, Sayfa Kaynağı, Duplex)
            ConfigureWiaItemProperties(item, device, options);

            int pageIndex = 1;
            int validPageNumber = 1;
            bool hasMorePages = true;

            while (hasMorePages && !cancellationToken.IsCancellationRequested)
            {
                progress?.Report($"Sayfa {pageIndex} taranıyor...");

                try
                {
                    Type? commonDialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
                    dynamic? commonDialog = commonDialogType != null ? Activator.CreateInstance(commonDialogType) : null;

                    dynamic imageFile;
                    if (commonDialog != null)
                    {
                        // Format: wiaFormatPNG = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"
                        imageFile = commonDialog.ShowTransfer(item, "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}", false);
                    }
                    else
                    {
                        imageFile = item.Transfer("{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}");
                    }

                    if (imageFile != null)
                    {
                        string pagePath = Path.Combine(outputDir, $"page_{pageIndex:D3}.png");
                        if (File.Exists(pagePath)) File.Delete(pagePath);
                        imageFile.SaveFile(pagePath);

                        // Boş sayfa kontrolü
                        if (options.RemoveBlankPages && _blankDetector.IsImageBlank(pagePath, 98.5))
                        {
                            progress?.Report($"Sayfa {pageIndex} boş olduğu için otomatik olarak atlandı.");
                            try { File.Delete(pagePath); } catch { }
                        }
                        else
                        {
                            var pageItem = LoadScannedPageItem(pagePath, validPageNumber, (int)options.Resolution);
                            scannedPages.Add(pageItem);
                            validPageNumber++;
                        }

                        pageIndex++;

                        // Flatbed (Cam) ise tek sayfa biter, ADF ise besleyici bitene kadar devam eder
                        if (options.Source == ScanSource.Flatbed)
                        {
                            hasMorePages = false;
                        }
                    }
                    else
                    {
                        hasMorePages = false;
                    }
                }
                catch (COMException comEx)
                {
                    // 0x80210003 = WIA_ERROR_PAPER_EMPTY (ADF'de kağıt bitti)
                    if ((uint)comEx.ErrorCode == 0x80210003)
                    {
                        hasMorePages = false;
                    }
                    else
                    {
                        FileLogger.LogError($"WIA COM Transfer Error at page {pageIndex}", comEx);
                        hasMorePages = false;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogError($"WIA Page Scan Error at page {pageIndex}", ex);
                    hasMorePages = false;
                }
            }

            return scannedPages.Count > 0;
        }

        /// <summary>
        /// Windows yerel tarama iletişim kutusunu (WIA Common Dialog) açarak doğrudan tarama yaptırır.
        /// </summary>
        public async Task<List<ScannedPageItem>> ScanViaWiaNativeDialogAsync(
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            var scannedPages = new List<ScannedPageItem>();
            string sessionFolder = Path.Combine(ScanTempDir, $"Scan_Native_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(sessionFolder);

            progress?.Report("Windows yerel tarayıcı penceresi açılıyor...");

            await Task.Run(() =>
            {
                Type? commonDialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
                if (commonDialogType == null)
                {
                    throw new InvalidOperationException("Windows WIA CommonDialog bileşeni sistemde bulunamadı.");
                }

                dynamic? commonDialog = Activator.CreateInstance(commonDialogType);
                if (commonDialog == null)
                {
                    throw new InvalidOperationException("WIA CommonDialog başlatılamadı.");
                }

                // 1 = ScannerDeviceType, 1 = ColorIntent, 0 = MaximizeQuality, wiaFormatPNG
                dynamic? imageFile = commonDialog.ShowAcquireImage(
                    1, // WiaDeviceType.ScannerDeviceType
                    options.ColorMode == ScanColorMode.BlackAndWhite ? 4 : (options.ColorMode == ScanColorMode.Grayscale ? 2 : 1),
                    0,
                    "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}",
                    false,
                    true,
                    false);

                if (imageFile != null)
                {
                    string pagePath = Path.Combine(sessionFolder, "native_page_001.png");
                    if (File.Exists(pagePath)) File.Delete(pagePath);
                    imageFile.SaveFile(pagePath);

                    var pageItem = LoadScannedPageItem(pagePath, 1, (int)options.Resolution);
                    scannedPages.Add(pageItem);
                }
            }, cancellationToken);

            return scannedPages;
        }

        private static void ConfigureWiaItemProperties(dynamic item, dynamic device, ScanOptions options)
        {
            try
            {
                // WIA Item Properties:
                // 6146 = Current Intent (1 = Color, 2 = Grayscale, 4 = Text/B&W)
                // 6147 = Horizontal Resolution (DPI)
                // 6148 = Vertical Resolution (DPI)
                int intent = options.ColorMode switch
                {
                    ScanColorMode.Color => 1,
                    ScanColorMode.Grayscale => 2,
                    ScanColorMode.BlackAndWhite => 4,
                    _ => 1
                };

                SetWiaProperty(item.Properties, 6146, intent);
                SetWiaProperty(item.Properties, 6147, (int)options.Resolution);
                SetWiaProperty(item.Properties, 6148, (int)options.Resolution);

                // Document Handling (Device Property 3088)
                // 1 = Feeder (ADF), 2 = Flatbed, 4 = Duplex (1 | 4 = 5 ADF Duplex)
                int handling = options.Source switch
                {
                    ScanSource.FeederSingle => 1,
                    ScanSource.FeederDuplex => 1 | 4,
                    _ => 2 // Flatbed
                };

                SetWiaProperty(device.Properties, 3088, handling);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ConfigureWiaItemProperties warning", ex);
            }
        }

        private static void SetWiaProperty(dynamic properties, int propId, object value)
        {
            try
            {
                dynamic prop = properties[propId.ToString()];
                if (prop != null)
                {
                    prop.Value = value;
                }
            }
            catch
            {
                // Ignore if property is read-only or unsupported
            }
        }

        private static ScannedPageItem LoadScannedPageItem(string filePath, int pageNumber, int dpi)
        {
            var fileInfo = new FileInfo(filePath);
            BitmapSource? bitmap = null;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.DecodePixelWidth = 600; // Thumbnail optimization
                bmp.EndInit();
                bmp.Freeze();
                bitmap = bmp;
            }
            catch
            {
                // Fallback
            }

            return new ScannedPageItem
            {
                PageNumber = pageNumber,
                FilePath = filePath,
                PreviewImage = bitmap,
                ResolutionDpi = dpi,
                FileSize = fileInfo.Length,
                Rotation = 0
            };
        }

        /// <summary>
        /// Taranan sayfaları tek bir PDF dosyasında birleştirir
        /// </summary>
        public async Task<string> SaveScannedPagesToPdfAsync(
            List<ScannedPageItem> pages,
            string outputPath,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            return await Task.Run(() =>
            {
                using var doc = new PdfDocument();
                doc.Info.Title = "DocMaster Pro — Taranan Belge";
                doc.Info.Creator = "DocMaster Pro Scanner Studio";

                for (int i = 0; i < pages.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var p = pages[i];
                    if (!File.Exists(p.FilePath)) continue;

                    using var xImg = XImage.FromFile(p.FilePath);
                    var page = doc.AddPage();
                    page.Width = xImg.PointWidth;
                    page.Height = xImg.PointHeight;

                    using var gfx = XGraphics.FromPdfPage(page);

                    // Sayfa döndürme ayarı
                    if (p.Rotation == 90 || p.Rotation == 180 || p.Rotation == 270)
                    {
                        page.Orientation = (p.Rotation == 90 || p.Rotation == 270) ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
                    }

                    gfx.DrawImage(xImg, 0, 0, page.Width, page.Height);

                    int pct = (int)(((i + 1) / (double)pages.Count) * 100);
                    progress?.Report(pct);
                }

                doc.Save(outputPath);
                return outputPath;
            }, cancellationToken);
        }
    }
}
