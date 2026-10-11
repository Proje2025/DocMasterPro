using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DocConverter.Models;
using Velopack;
using Velopack.Sources;

namespace DocConverter.Services
{
    public sealed class UpdateService
    {
        private const string PrimaryRepoOwner = "Proje2025";
        private const string PrimaryRepoName = "DocMasterPro";

        public static string PrimaryRepoUrl => $"https://github.com/{PrimaryRepoOwner}/{PrimaryRepoName}";

        /// <summary>Kurulum dosyası yalnızca resmî deponun sürüm dosyaları arasından indirilir.</summary>
        private static string TrustedDownloadPrefix => $"{PrimaryRepoUrl}/releases/download/";

        private readonly HttpClient _httpClient;

        public UpdateService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DocMasterPro", "1.2.5"));
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
        }

        public string GetCurrentVersion()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version != null)
            {
                // Trim trailing zero build/revision if 0 (e.g. 1.1.0.0 -> 1.1.0)
                if (version.Revision > 0)
                    return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                if (version.Build > 0)
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                return $"{version.Major}.{version.Minor}.0";
            }
            return "1.2.5";
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            string currentVer = GetCurrentVersion();
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVer,
                LatestVersion = currentVer
            };

            // 1. GitHub Releases API'den en son sürüm bilgilerini al
            // Yalnızca resmî depo sorgulanır. Başka bir hesaptaki kopyaya düşülmez: o hesabı yöneten kişi
            // kullanıcılara kurulum dosyası dağıtabilirdi.
            GitHubReleaseDto? release = await FetchLatestGitHubReleaseAsync(PrimaryRepoOwner, PrimaryRepoName, cancellationToken);

            if (release != null)
            {
                string tagVer = (release.TagName ?? "").TrimStart('v', 'V').Trim();
                result.LatestVersion = !string.IsNullOrWhiteSpace(tagVer) ? tagVer : currentVer;
                result.ReleaseTitle = release.Name ?? $"DocMaster Pro v{result.LatestVersion}";
                result.ReleaseNotes = !string.IsNullOrWhiteSpace(release.Body) 
                    ? release.Body 
                    : "Bu sürüm için detaylı değişiklik notu bulunmamaktadır.";
                result.PublishedAt = release.PublishedAt;
                result.HtmlUrl = release.HtmlUrl ?? $"{PrimaryRepoUrl}/releases";

                if (SelectInstaller(release) is { } installer)
                {
                    result.SetupDownloadUrl = installer.BrowserDownloadUrl;
                    result.SetupFileSize = installer.Size;
                    result.SetupSha256 = ParseSha256Digest(installer.Digest);
                }
            }

            // 2. Velopack Yöneticisini kontrol et
            try
            {
                var manager = new UpdateManager(new GithubSource(PrimaryRepoUrl, accessToken: null, prerelease: false));
                if (manager.IsInstalled)
                {
                    var velopackUpdate = await manager.CheckForUpdatesAsync();
                    if (velopackUpdate != null)
                    {
                        result.VelopackUpdate = velopackUpdate;
                        result.LatestVersion = velopackUpdate.TargetFullRelease.Version.ToString();
                        result.RecommendedMethod = UpdateInstallMethod.Velopack;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("VelopackCheckError", ex);
            }

            // 3. Sürüm Karşılaştırması
            result.HasUpdate = IsNewerVersion(result.LatestVersion, result.CurrentVersion);

            // Eğer Velopack yoksa veya Setup varsa doğrudan indirme yöntemini seç
            if (result.VelopackUpdate == null)
            {
                result.RecommendedMethod = UpdateInstallMethod.DirectInstaller;
            }

            return result;
        }

        public async Task DownloadAndInstallUpdateAsync(
            UpdateCheckResult updateResult,
            IProgress<UpdateProgressInfo> progress,
            CancellationToken cancellationToken = default)
        {
            if (updateResult == null)
                throw new ArgumentNullException(nameof(updateResult));

            // Katman 1: Velopack ile Güncelleme
            if (updateResult.RecommendedMethod == UpdateInstallMethod.Velopack && updateResult.VelopackUpdate != null)
            {
                try
                {
                    var manager = new UpdateManager(new GithubSource(PrimaryRepoUrl, accessToken: null, prerelease: false));
                    if (manager.IsInstalled)
                    {
                        progress.Report(new UpdateProgressInfo
                        {
                            Percentage = 5,
                            StageTitle = "1/3 Güncelleme Paketi İndiriliyor...",
                            StageDescription = "Velopack güncelleme akışı başlatıldı."
                        });

                        await manager.DownloadUpdatesAsync(updateResult.VelopackUpdate, p =>
                        {
                            progress.Report(new UpdateProgressInfo
                            {
                                Percentage = Math.Min(90, Math.Max(5, p)),
                                StageTitle = "1/3 Güncelleme Paketi İndiriliyor...",
                                StageDescription = $"Paket indiriliyor: %{p}"
                            });
                        }, cancelToken: cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();

                        progress.Report(new UpdateProgressInfo
                        {
                            Percentage = 95,
                            StageTitle = "2/3 Paket Doğrulanıyor...",
                            StageDescription = "İndirilen paket bütünlüğü kontrol ediliyor..."
                        });

                        await Task.Delay(500, cancellationToken);

                        progress.Report(new UpdateProgressInfo
                        {
                            Percentage = 100,
                            StageTitle = "3/3 Güncelleme Uygulanıyor...",
                            StageDescription = "DocMaster Pro yeniden başlatılıyor..."
                        });

                        await Task.Delay(600, cancellationToken);
                        manager.ApplyUpdatesAndRestart(updateResult.VelopackUpdate);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("VelopackUpdateFailedFallbackToDirect", ex);
                    // Velopack başarısız olursa (ör. 404 nupkg veya izin hatası) doğrudan Setup fallback'e devam et
                }
            }

            // Katman 2: Doğrudan Kurulum Dosyası (Setup.exe) İndirip Çalıştırma
            if (string.IsNullOrWhiteSpace(updateResult.SetupDownloadUrl) || !IsTrustedInstallerUrl(updateResult.SetupDownloadUrl))
            {
                throw new InvalidOperationException(
                    "Güncelleme kurulum dosyası sunucuda bulunamadı. Lütfen GitHub sürüm sayfasından manuel olarak indiriniz.");
            }

            if (string.IsNullOrWhiteSpace(updateResult.SetupSha256))
            {
                // Özeti bilinmeyen bir dosya doğrulanamaz; doğrulanamayan dosya da çalıştırılmaz.
                throw new InvalidOperationException(
                    "Kurulum dosyasının doğrulama bilgisi alınamadı. Lütfen GitHub sürüm sayfasından manuel olarak indiriniz.");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "DocMasterProUpdates");
            Directory.CreateDirectory(tempDir);
            string destinationPath = Path.Combine(tempDir, "DocMasterPro-Setup.exe");

            progress.Report(new UpdateProgressInfo
            {
                Percentage = 0,
                StageTitle = "1/3 Kurulum Dosyası İndiriliyor...",
                StageDescription = "İndirme bağlantısı kuruluyor...",
                TotalBytesToReceive = updateResult.SetupFileSize
            });

            await DownloadFileWithProgressAsync(
                updateResult.SetupDownloadUrl,
                destinationPath,
                updateResult.SetupFileSize,
                progress,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            progress.Report(new UpdateProgressInfo
            {
                Percentage = 96,
                StageTitle = "2/3 Dosya Doğrulanıyor...",
                StageDescription = "İndirilen dosyanın yayınlanan dosyayla aynı olduğu denetleniyor...",
                BytesReceived = updateResult.SetupFileSize ?? 0,
                TotalBytesToReceive = updateResult.SetupFileSize
            });

            // Okumaya açık tutulur: doğrulanan dosya, çalıştırılana kadar değiştirilemez.
            using var verified = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await MatchesAsync(verified, updateResult.SetupFileSize, updateResult.SetupSha256, cancellationToken))
            {
                verified.Dispose();
                File.Delete(destinationPath);
                throw new InvalidOperationException(
                    "İndirilen kurulum dosyası doğrulanamadı; dosya silindi ve çalıştırılmadı. Lütfen yeniden deneyin.");
            }

            progress.Report(new UpdateProgressInfo
            {
                Percentage = 100,
                StageTitle = "3/3 Kurulum Başlatılıyor...",
                StageDescription = "Yeni sürüm kurulumu açılıyor ve uygulama kapatılıyor..."
            });

            await Task.Delay(800, cancellationToken);

            // Kurulumu başlat ve mevcut uygulamayı kapat
            var psi = new ProcessStartInfo
            {
                FileName = destinationPath,
                UseShellExecute = true
            };
            Process.Start(psi);

            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
        }

        /// <summary>Sürümün "…Setup.exe" adlı ve resmî depodan indirilen dosyası; yoksa null.</summary>
        internal static GitHubReleaseAssetDto? SelectInstaller(GitHubReleaseDto release)
        {
            if (release.Assets == null)
                return null;

            foreach (GitHubReleaseAssetDto asset in release.Assets)
            {
                if (!string.IsNullOrWhiteSpace(asset.Name) &&
                    asset.Name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase) &&
                    IsTrustedInstallerUrl(asset.BrowserDownloadUrl))
                    return asset;
            }

            return null;
        }

        internal static bool IsTrustedInstallerUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url) &&
            url.StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal) &&
            Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            parsed.AbsolutePath.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase);

        /// <summary>GitHub'ın dosya için bildirdiği "sha256:…" değerinden 64 haneli özeti çıkarır.</summary>
        internal static string? ParseSha256Digest(string? digest)
        {
            const string prefix = "sha256:";
            if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            string hex = digest.Substring(prefix.Length).Trim();
            return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
        }

        internal static async Task<bool> MatchesAsync(Stream file, long? expectedSize, string expectedSha256, CancellationToken cancellationToken)
        {
            if (expectedSize is { } size && file.Length != size)
                return false;

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hash = await sha256.ComputeHashAsync(file, cancellationToken);
            return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }

        private async Task DownloadFileWithProgressAsync(
            string downloadUrl,
            string destinationFilePath,
            long? expectedTotalBytes,
            IProgress<UpdateProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? expectedTotalBytes ?? -1;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(
                destinationFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            var stopwatch = Stopwatch.StartNew();
            var lastReportTime = DateTime.UtcNow;
            long lastReportBytes = 0;

            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                var now = DateTime.UtcNow;
                var elapsed = (now - lastReportTime).TotalSeconds;

                if (elapsed >= 0.1 || totalRead == totalBytes)
                {
                    double speed = elapsed > 0 ? (totalRead - lastReportBytes) / elapsed : 0;
                    int percent = totalBytes > 0 ? (int)Math.Min(95, (totalRead * 95) / totalBytes) : 0;

                    progress.Report(new UpdateProgressInfo
                    {
                        Percentage = percent,
                        StageTitle = "1/3 Güncelleme Paketi İndiriliyor...",
                        StageDescription = $"İndiriliyor: %{percent}",
                        BytesReceived = totalRead,
                        TotalBytesToReceive = totalBytes > 0 ? totalBytes : null,
                        DownloadSpeedBytesPerSec = speed
                    });

                    lastReportTime = now;
                    lastReportBytes = totalRead;
                }
            }

            stopwatch.Stop();
        }

        private async Task<GitHubReleaseDto?> FetchLatestGitHubReleaseAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken)
        {
            try
            {
                string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    // If latest returns 404, try querying releases list
                    string listUrl = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=5";
                    using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
                    using var listResp = await _httpClient.SendAsync(listReq, cancellationToken);
                    if (!listResp.IsSuccessStatusCode) return null;

                    string listJson = await listResp.Content.ReadAsStringAsync(cancellationToken);
                    var releases = JsonSerializer.Deserialize<GitHubReleaseDto[]>(listJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (releases != null && releases.Length > 0)
                    {
                        foreach (var rel in releases)
                        {
                            if (!rel.Draft) return rel;
                        }
                    }
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<GitHubReleaseDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"FetchGitHubRelease_{owner}_{repo}", ex);
                return null;
            }
        }

        public static bool IsNewerVersion(string latestVersionStr, string currentVersionStr)
        {
            if (string.IsNullOrWhiteSpace(latestVersionStr) || string.IsNullOrWhiteSpace(currentVersionStr))
                return false;

            string cleanLatest = NormalizeVersionString(latestVersionStr);
            string cleanCurrent = NormalizeVersionString(currentVersionStr);

            if (Version.TryParse(cleanLatest, out var latestVer) && Version.TryParse(cleanCurrent, out var currentVer))
            {
                return latestVer > currentVer;
            }

            // String based fallback
            return string.Compare(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
        }

        private static string NormalizeVersionString(string version)
        {
            version = version.Trim().TrimStart('v', 'V');
            int dashIndex = version.IndexOf('-');
            if (dashIndex >= 0)
                version = version.Substring(0, dashIndex);

            var parts = version.Split('.');
            if (parts.Length == 1) return $"{parts[0]}.0.0";
            if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
            return version;
        }
    }
}
