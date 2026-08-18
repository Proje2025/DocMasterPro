using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Models;
using DocConverter.Services;

namespace DocConverter.ViewModels
{
    public partial class UpdateViewModel : ObservableObject
    {
        private readonly UpdateService _updateService;
        private CancellationTokenSource? _cts;
        private UpdateCheckResult? _checkResult;

        public Action? RequestClose { get; set; }

        [ObservableProperty]
        private UpdateState state = UpdateState.Checking;

        [ObservableProperty]
        private string currentVersion = "1.1.0";

        [ObservableProperty]
        private string latestVersion = "1.1.0";

        [ObservableProperty]
        private string releaseTitle = "";

        [ObservableProperty]
        private string releaseNotes = "";

        [ObservableProperty]
        private string publishedDateText = "";

        [ObservableProperty]
        private string stageTitle = "Güncelleme Kontrol Ediliyor...";

        [ObservableProperty]
        private string stageDescription = "GitHub sunucularına bağlanılıyor ve son sürüm kontrol ediliyor...";

        [ObservableProperty]
        private int progressPercentage = 0;

        [ObservableProperty]
        private string bytesDetailText = "";

        [ObservableProperty]
        private string speedText = "";

        [ObservableProperty]
        private string timeRemainingText = "";

        [ObservableProperty]
        private string errorMessage = "";

        [ObservableProperty]
        private bool isBusy = false;

        [ObservableProperty]
        private bool canCancel = false;

        public UpdateViewModel(UpdateService? updateService = null)
        {
            _updateService = updateService ?? new UpdateService();
            CurrentVersion = _updateService.GetCurrentVersion();
        }

        [RelayCommand]
        public async Task CheckUpdates()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            State = UpdateState.Checking;
            IsBusy = true;
            CanCancel = true;
            StageTitle = "Güncellemeler Denetleniyor...";
            StageDescription = "GitHub ve güncelleme sunucusuna bağlanılıyor...";
            ProgressPercentage = 0;
            ErrorMessage = "";

            try
            {
                var result = await _updateService.CheckForUpdatesAsync(_cts.Token);
                _checkResult = result;

                CurrentVersion = result.CurrentVersion;
                LatestVersion = result.LatestVersion;
                ReleaseTitle = result.ReleaseTitle;
                ReleaseNotes = result.ReleaseNotes;
                PublishedDateText = result.PublishedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";

                if (result.HasUpdate)
                {
                    State = UpdateState.UpdateAvailable;
                    StageTitle = "Yeni Bir Sürüm Mevcut!";
                    StageDescription = $"DocMaster Pro v{LatestVersion} indirilebilir.";
                }
                else
                {
                    State = UpdateState.UpToDate;
                    StageTitle = "Uygulamanız Güncel";
                    StageDescription = $"En güncel sürümü (v{CurrentVersion}) kullanıyorsunuz.";
                }
            }
            catch (OperationCanceledException)
            {
                State = UpdateState.Idle;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("UpdateCheckViewModel", ex);
                State = UpdateState.Error;
                ErrorMessage = $"Güncelleme denetlenirken bir hata oluştu:\n{ex.Message}";
                StageTitle = "Bağlantı Hatası";
                StageDescription = "Sunucuya erişilemedi.";
            }
            finally
            {
                IsBusy = false;
                CanCancel = false;
            }
        }

        [RelayCommand]
        public async Task StartUpdate()
        {
            if (_checkResult == null) return;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            State = UpdateState.Downloading;
            IsBusy = true;
            CanCancel = true;
            ProgressPercentage = 0;
            BytesDetailText = "İndirme başlatılıyor...";
            SpeedText = "";
            TimeRemainingText = "";
            StageTitle = "1/3 Güncelleme Paketi İndiriliyor...";
            StageDescription = "Bağlantı kuruluyor...";

            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                ProgressPercentage = info.Percentage;
                StageTitle = info.StageTitle;
                StageDescription = info.StageDescription;
                BytesDetailText = info.FormattedBytesText;
                SpeedText = info.FormattedSpeedText;
                TimeRemainingText = info.FormattedTimeRemaining;
            });

            try
            {
                await _updateService.DownloadAndInstallUpdateAsync(_checkResult, progress, _cts.Token);
                State = UpdateState.Success;
                StageTitle = "Güncelleme Tamamlandı!";
                StageDescription = "Uygulama yeniden başlatılıyor...";
            }
            catch (OperationCanceledException)
            {
                State = UpdateState.UpdateAvailable;
                StageTitle = "İndirme İptal Edildi";
                StageDescription = "Güncelleme işlemi kullanıcı tarafından durduruldu.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("UpdateDownloadInstallViewModel", ex);
                State = UpdateState.Error;
                ErrorMessage = $"Güncelleme indirilirken veya kurulurken bir sorun oluştu:\n{ex.Message}\n\nGitHub üzerinden doğrudan kurulum dosyasını indirebilirsiniz.";
                StageTitle = "Güncelleme Başarısız Oldu";
                StageDescription = "İşlem tamamlanamadı.";
            }
            finally
            {
                IsBusy = false;
                CanCancel = false;
            }
        }

        [RelayCommand]
        public void Cancel()
        {
            _cts?.Cancel();
            if (State == UpdateState.Downloading || State == UpdateState.Checking)
            {
                State = _checkResult?.HasUpdate == true ? UpdateState.UpdateAvailable : UpdateState.Idle;
            }
        }

        [RelayCommand]
        public void OpenGitHubReleases()
        {
            try
            {
                string url = _checkResult?.HtmlUrl ?? UpdateService.PrimaryRepoUrl + "/releases";
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                FileLogger.LogError("OpenGitHubReleases", ex);
            }
        }

        [RelayCommand]
        public void Close()
        {
            _cts?.Cancel();
            RequestClose?.Invoke();
        }
    }
}
