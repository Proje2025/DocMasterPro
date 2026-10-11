using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;

namespace DocConverter.Services
{
    /// <summary>
    /// İndirilecek LibreOffice sürümü. Sürüm, boyut ve SHA-256 özeti uygulamaya gömülüdür: sunucu ya da yansı ne
    /// gönderirse göndersin yalnızca tam olarak bu dosya kabul edilir. Yeni sürüme geçiş uygulama güncellemesiyle olur.
    /// </summary>
    public sealed record LibreOfficeRelease(
        string Version,
        IReadOnlyList<Uri> Sources,
        long SizeBytes,
        string Sha256,
        string Publisher,
        long RequiredFreeBytes)
    {
        private const long Megabyte = 1024L * 1024L;

        public static LibreOfficeRelease Current { get; } = new(
            Version: "26.2.6",
            Sources: new[]
            {
                // Resmî indirme adresi; en yakın yansıya yönlendirir.
                new Uri("https://download.documentfoundation.org/libreoffice/stable/26.2.6/win/x86_64/LibreOffice_26.2.6_Win_x86-64.msi"),
                // Yeni sürüm çıktığında üstteki adres kaldırılır; aynı dosyanın kalıcı arşiv adresi.
                new Uri("https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.6.3/win/x86_64/LibreOffice_26.2.6.3_Win_x86-64.msi"),
            },
            SizeBytes: 373_252_096,
            Sha256: "f9877032fd908beb9c0ddf06df4af5c2e85f419c42e14876c4cce5aae5fb2660",
            Publisher: "The Document Foundation",
            // İndirilen dosya (356 MB) + açılan dosyalar (1,5 GB) + açma sırasında oluşan kurulum kopyası (356 MB).
            RequiredFreeBytes: 2560 * Megabyte);
    }

    /// <summary>Doğrulanmış kurulum dosyasını bir klasöre açar.</summary>
    public interface IEngineExtractor
    {
        Task ExtractAsync(string installerPath, string targetDirectory, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Windows Installer'ın "yönetici kurulumu" kipiyle (msiexec /a) dosyaları yalnızca hedef klasöre açar:
    /// yönetici izni gerekmez, bilgisayara program kurulmaz, kayıt defterine ve Başlat menüsüne dokunulmaz.
    /// </summary>
    public sealed class MsiEngineExtractor : IEngineExtractor
    {
        private const int AnotherInstallationRunning = 1618;

        public async Task ExtractAsync(string installerPath, string targetDirectory, CancellationToken cancellationToken)
        {
            // msiexec, ÖZELLİK="değer" biçimini kendisi ayrıştırır; bu yüzden komut satırı elle kurulur. Windows
            // yollarında tırnak bulunamaz; kapanış tırnağından önceki ters eğik çizgi ise tırnağı geçersiz kılardı.
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            string installer = Path.GetFullPath(installerPath);

            var startInfo = new ProcessStartInfo
            {
                // Tam yol: arama yolundaki başka bir msiexec.exe çalıştırılamaz.
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = $"/a \"{installer}\" /qn /norestart TARGETDIR=\"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                throw;
            }

            if (process.ExitCode == AnotherInstallationRunning)
                throw new EngineSetupException(EngineSetupError.Unknown,
                    "Bilgisayarda şu anda başka bir kurulum sürüyor. Bittikten sonra yeniden deneyin.");

            if (process.ExitCode != 0)
                throw new EngineSetupException(EngineSetupError.Unknown,
                    $"LibreOffice dosyaları açılamadı (Windows Installer kodu {process.ExitCode}).");
        }
    }

    /// <summary>
    /// LibreOffice'i güvenli biçimde indirir, doğrular ve DocMaster Pro'nun kendi klasörüne hazırlar.
    /// <list type="number">
    /// <item>Sabit sürüm, resmî adresten indirilir; yarıda kalan indirme kaldığı yerden sürer.</item>
    /// <item>Dosyanın boyutu ve SHA-256 özeti gömülü değerle, dijital imzası yayıncıyla karşılaştırılır. Tutmazsa
    /// dosya silinir ve hiçbir şey çalıştırılmaz.</item>
    /// <item>Doğrulanan dosya, doğrulama ile açma arasında değiştirilemesin diye yazmaya kilitli tutulur.</item>
    /// <item>Dosyalar geçici bir klasöre açılır; her şey tamamsa klasör yerine taşınır ve "hazır" işareti yazılır.</item>
    /// </list>
    /// </summary>
    public sealed class LibreOfficeEngineInstaller : IOfficeEngineInstaller
    {
        private const int DiskFull = unchecked((int)0x80070070);
        private const int HandleDiskFull = unchecked((int)0x80070027);

        /// <summary>Açma hatasından sonra boş alan bunun altındaysa neden disk doluluğu sayılır.</summary>
        private const long MinimumWorkingSpace = 200L * 1024 * 1024;

        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

        private readonly LibreOfficeRelease _release;
        private readonly string _root;
        private readonly HttpClient _http;
        private readonly IFileSignatureVerifier _verifier;
        private readonly IEngineExtractor _extractor;
        private readonly Func<string, long> _freeSpace;

        public LibreOfficeEngineInstaller()
            : this(LibreOfficeRelease.Current, DownloadedEngine.Root, new HttpClientHandler(), new AuthenticodeVerifier(), new MsiEngineExtractor(), FreeSpaceOf)
        {
        }

        public LibreOfficeEngineInstaller(
            LibreOfficeRelease release,
            string root,
            HttpMessageHandler httpHandler,
            IFileSignatureVerifier verifier,
            IEngineExtractor extractor,
            Func<string, long>? freeSpace = null)
        {
            _release = release ?? throw new ArgumentNullException(nameof(release));
            _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _freeSpace = freeSpace ?? FreeSpaceOf;

            // Süre sınırları indirme döngüsünde uygulanır; yüzlerce megabaytlık indirme tek bir sınıra sığmaz.
            _http = new HttpClient(httpHandler ?? throw new ArgumentNullException(nameof(httpHandler))) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DocMasterPro");
        }

        private string DownloadDirectory => Path.Combine(_root, "download");

        /// <summary>İndirilmekte olan dosya; indirme yarıda kalırsa kaldığı yerden sürdürülür.</summary>
        private string PartialFile => InstallerFile + ".part";

        /// <summary>İndirmesi tamamlanmış kurulum dosyası.</summary>
        private string InstallerFile => Path.Combine(DownloadDirectory, $"libreoffice-{_release.Version}.msi");

        private string EngineDirectory => Path.Combine(_root, "libreoffice-" + _release.Version);

        public EngineSetupRequirements GetRequirements()
        {
            long available;
            try
            {
                available = _freeSpace(_root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                available = 0;
            }

            return new EngineSetupRequirements(
                _release.Version,
                _release.SizeBytes,
                _release.RequiredFreeBytes,
                available,
                (Path.GetPathRoot(_root) ?? _root).TrimEnd(Path.DirectorySeparatorChar),
                _release.Publisher);
        }

        public async Task InstallAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            try
            {
                await Task.Run(() => InstallCoreAsync(progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (ex.HResult is DiskFull or HandleDiskFull)
            {
                throw new EngineSetupException(EngineSetupError.DiskFull, "Diskte yeterli yer kalmadı.", ex);
            }
        }

        public Task RemoveAsync() => Task.Run(() =>
        {
            if (!Directory.Exists(_root))
                return;

            // Önce "hazır" işaretleri silinir: silme yarıda kalsa bile yarım klasör motor olarak kullanılmaz.
            foreach (string directory in Directory.GetDirectories(_root, "libreoffice-*"))
            {
                File.Delete(Path.Combine(directory, DownloadedEngine.ReadyMarker));
                DeleteDirectory(directory);
            }

            DeleteDirectory(DownloadDirectory);
        });

        private async Task InstallCoreAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(DownloadDirectory);

            // Aynı anda iki pencere aynı klasöre kurmaya çalışmasın.
            FileStream installLock;
            try
            {
                installLock = new FileStream(Path.Combine(_root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new EngineSetupException(EngineSetupError.Unknown, "Hazırlık başka bir DocMaster Pro penceresinde sürüyor.");
            }

            using (installLock)
            {
                RemoveLeftovers();

                long alreadyDownloaded = Math.Min(DownloadedBytes(), _release.SizeBytes);
                if (_freeSpace(_root) < _release.RequiredFreeBytes - alreadyDownloaded)
                    throw new EngineSetupException(EngineSetupError.DiskFull, "Diskte yeterli yer yok.");

                progress.Report(new EngineSetupProgress(EngineSetupStage.Downloading, alreadyDownloaded, _release.SizeBytes));
                if (!File.Exists(InstallerFile))
                {
                    await DownloadAsync(progress, cancellationToken).ConfigureAwait(false);
                    File.Move(PartialFile, InstallerFile, overwrite: true);
                }

                progress.Report(new EngineSetupProgress(EngineSetupStage.Verifying, 0, 0));

                // Yazmaya kilitli tutulur: doğrulanan dosya, açma bitene kadar değiştirilemez.
                using (var verified = new FileStream(InstallerFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await VerifyAsync(verified, cancellationToken).ConfigureAwait(false);

                    progress.Report(new EngineSetupProgress(EngineSetupStage.Preparing, 0, 0));
                    await PrepareAsync(cancellationToken).ConfigureAwait(false);
                }

                DeleteDirectory(DownloadDirectory);
            }

            try
            {
                File.Delete(Path.Combine(_root, "install.lock"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private long DownloadedBytes()
        {
            if (File.Exists(InstallerFile))
                return new FileInfo(InstallerFile).Length;

            return File.Exists(PartialFile) ? new FileInfo(PartialFile).Length : 0;
        }

        // ==================== İndirme ====================

        private async Task DownloadAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            Exception? lastError = null;

            foreach (Uri source in _release.Sources)
            {
                try
                {
                    await DownloadFromAsync(source, progress, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (IsDownloadFailure(ex, cancellationToken))
                {
                    lastError = ex;
                    FileLogger.LogError($"LibreOffice indirilemedi ({source.Host})", ex);
                }
            }

            throw new EngineSetupException(EngineSetupError.DownloadFailed, "LibreOffice indirilemedi.", lastError);
        }

        private static bool IsDownloadFailure(Exception ex, CancellationToken cancellationToken)
        {
            if (ex is IOException { HResult: DiskFull or HandleDiskFull })
                return false;

            // Kullanıcının iptali indirme hatası değildir; bağlantının takılması (iç süre sınırı) ise hatadır.
            if (ex is OperationCanceledException)
                return !cancellationToken.IsCancellationRequested;

            return ex is HttpRequestException or IOException;
        }

        private async Task DownloadFromAsync(Uri source, IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            long existing = File.Exists(PartialFile) ? new FileInfo(PartialFile).Length : 0;
            if (existing > _release.SizeBytes)
            {
                File.Delete(PartialFile);
                existing = 0;
            }

            if (existing == _release.SizeBytes)
                return;

            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(ResponseTimeout);
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, responseTimeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Yarım dosya bu sunucudaki dosyayla uyuşmuyor: baştan indirilmek üzere at.
                File.Delete(PartialFile);
                throw new HttpRequestException("Sunucu indirmenin sürdürülmesini kabul etmedi.");
            }

            response.EnsureSuccessStatusCode();

            // Sunucu aralık isteğini yok sayıp dosyanın tamamını gönderiyorsa baştan yazılır.
            bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed)
                existing = 0;

            if (response.Content.Headers.ContentLength is { } length && existing + length != _release.SizeBytes)
                throw new HttpRequestException($"Sunucudaki dosyanın boyutu beklenenden farklı ({existing + length} bayt).");

            await using var file = new FileStream(
                PartialFile, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = new byte[1 << 16];
            long done = existing;
            var sinceReport = Stopwatch.StartNew();

            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stall.CancelAfter(StallTimeout);
                int read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                done += read;
                if (done > _release.SizeBytes)
                    throw new HttpRequestException("Sunucu beklenenden büyük bir dosya gönderiyor.");

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (sinceReport.ElapsedMilliseconds >= 200)
                {
                    progress.Report(new EngineSetupProgress(EngineSetupStage.Downloading, done, _release.SizeBytes));
                    sinceReport.Restart();
                }
            }

            if (done != _release.SizeBytes)
                throw new HttpRequestException("İndirme tamamlanmadan bağlantı kesildi.");

            progress.Report(new EngineSetupProgress(EngineSetupStage.Downloading, done, _release.SizeBytes));
        }

        // ==================== Doğrulama ====================

        private async Task VerifyAsync(FileStream file, CancellationToken cancellationToken)
        {
            bool trusted = false;
            try
            {
                if (file.Length == _release.SizeBytes)
                {
                    using var sha256 = SHA256.Create();
                    byte[] hash = await sha256.ComputeHashAsync(file, cancellationToken).ConfigureAwait(false);

                    trusted = string.Equals(Convert.ToHexString(hash), _release.Sha256, StringComparison.OrdinalIgnoreCase)
                        && _verifier.IsSignedBy(file.Name, _release.Publisher);
                }
            }
            finally
            {
                if (!trusted && !cancellationToken.IsCancellationRequested)
                {
                    // Güvenilmeyen dosya diskte bırakılmaz; yeniden denemede baştan indirilir.
                    file.Dispose();
                    File.Delete(file.Name);
                }
            }

            if (!trusted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new EngineSetupException(EngineSetupError.SignatureInvalid,
                    "İndirilen dosya beklenen LibreOffice kurulum dosyası değil.");
            }
        }

        // ==================== Hazırlama ====================

        private async Task PrepareAsync(CancellationToken cancellationToken)
        {
            string staging = EngineDirectory + ".tmp";
            DeleteDirectory(staging);
            Directory.CreateDirectory(staging);

            try
            {
                try
                {
                    await _extractor.ExtractAsync(InstallerFile, staging, cancellationToken).ConfigureAwait(false);
                }
                catch (EngineSetupException) when (_freeSpace(_root) < MinimumWorkingSpace)
                {
                    throw new EngineSetupException(EngineSetupError.DiskFull, "Dosyalar açılırken diskte yer kalmadı.");
                }

                if (OfficeEngineProbe.FromProgramDirectory(Path.Combine(staging, "program"), isDownloaded: true) == null)
                    throw new EngineSetupException(EngineSetupError.Unknown, "Açılan dosyalar arasında LibreOffice bulunamadı.");

                RemoveUnneededFiles(staging);

                DeleteDirectory(EngineDirectory);
                Directory.Move(staging, EngineDirectory);
                File.WriteAllText(Path.Combine(EngineDirectory, DownloadedEngine.ReadyMarker), _release.Version);
            }
            catch
            {
                DeleteDirectory(staging);
                throw;
            }
        }

        /// <summary>Açma işleminin klasöre bıraktığı kurulum dosyası kopyası (356 MB) ve yardım dosyaları dönüştürme için gerekmez.</summary>
        private static void RemoveUnneededFiles(string directory)
        {
            foreach (string installer in Directory.GetFiles(directory, "*.msi"))
            {
                File.SetAttributes(installer, FileAttributes.Normal);
                File.Delete(installer);
            }

            DeleteDirectory(Path.Combine(directory, "help"));
            DeleteDirectory(Path.Combine(directory, "readmes"));
        }

        /// <summary>Yarıda kalmış açma klasörlerini ve başka bir sürüme ait yarım indirmeleri siler.</summary>
        private void RemoveLeftovers()
        {
            foreach (string staging in Directory.GetDirectories(_root, "libreoffice-*.tmp"))
                DeleteDirectory(staging);

            foreach (string file in Directory.GetFiles(DownloadDirectory))
            {
                if (!string.Equals(file, PartialFile, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file, InstallerFile, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }

        private static void DeleteDirectory(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            // Kurulum kopyaları salt okunur dosyalar içerebilir.
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(directory, recursive: true);
        }

        private static long FreeSpaceOf(string path)
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
    }
}
