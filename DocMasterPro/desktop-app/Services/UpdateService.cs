using System.Reflection;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace DocConverter.Services;

public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/Proje2025/DocMasterPro";

    public async Task CheckForUpdatesAsync(bool notifyWhenCurrent, CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                if (notifyWhenCurrent)
                {
                    MessageBox.Show(
                        "Güncelleme denetimi yalnızca Velopack ile kurulan sürümlerde çalışır.",
                        "DocMaster Pro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (update == null)
            {
                if (notifyWhenCurrent)
                {
                    MessageBox.Show(
                        $"Güncel sürüm kullanılıyor: {GetCurrentVersion()}",
                        "DocMaster Pro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            var result = MessageBox.Show(
                $"Yeni sürüm hazır: {update.TargetFullRelease.Version}\n\nİndirip güncellemek ister misiniz?",
                "DocMaster Pro Güncelleme",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
                return;

            await manager.DownloadUpdatesAsync(update);
            cancellationToken.ThrowIfCancellationRequested();
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.LogError("UpdateCheck", ex);
            if (notifyWhenCurrent)
            {
                MessageBox.Show(
                    $"Güncelleme denetlenemedi: {ex.Message}",
                    "DocMaster Pro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private static string GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "bilinmiyor";
}
