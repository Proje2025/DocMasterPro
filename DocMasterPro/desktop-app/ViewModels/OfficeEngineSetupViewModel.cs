using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Models;
using DocConverter.Services;

namespace DocConverter.ViewModels
{
    public enum EngineSetupState
    {
        /// <summary>Kullanıcıya ne indirileceği gösterilir ve onayı beklenir.</summary>
        Consent,
        Downloading,
        Verifying,
        Preparing,
        Completed,
        Failed
    }

    /// <summary>
    /// "Microsoft Office bulunamadı" penceresinin durumu: onay, indirme, doğrulama, hazırlama ve hata ekranları.
    /// </summary>
    public partial class OfficeEngineSetupViewModel : ObservableObject
    {
        private readonly IOfficeEngineInstaller _installer;
        private CancellationTokenSource? _cts;

        public OfficeEngineSetupViewModel(IOfficeEngineInstaller installer)
        {
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
            Requirements = _installer.GetRequirements();
        }

        /// <summary>Pencerenin kapanması istendiğinde tetiklenir; true ise motor kullanıma hazırdır.</summary>
        public event EventHandler<bool>? CloseRequested;

        public EngineSetupRequirements Requirements { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConsent))]
        [NotifyPropertyChangedFor(nameof(IsWorking))]
        [NotifyPropertyChangedFor(nameof(IsFailed))]
        [NotifyPropertyChangedFor(nameof(IsCompleted))]
        [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
        [NotifyPropertyChangedFor(nameof(StageTitle))]
        [NotifyPropertyChangedFor(nameof(StageDescription))]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private EngineSetupState state = EngineSetupState.Consent;

        [ObservableProperty]
        private double progressPercent;

        [ObservableProperty]
        private string progressText = "";

        [ObservableProperty]
        private string errorTitle = "";

        [ObservableProperty]
        private string errorMessage = "";

        /// <summary>Hata, yeniden denemekle düzelebilecek türdense true.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool canRetry;

        public bool IsConsent => State == EngineSetupState.Consent;

        public bool IsWorking => State is EngineSetupState.Downloading or EngineSetupState.Verifying or EngineSetupState.Preparing;

        public bool IsFailed => State == EngineSetupState.Failed;

        public bool IsCompleted => State == EngineSetupState.Completed;

        /// <summary>İndirme dışındaki aşamalarda ilerleme yüzdesi bilinmez.</summary>
        public bool IsProgressIndeterminate => State is EngineSetupState.Verifying or EngineSetupState.Preparing;

        public string DownloadSizeText => SizeText.Format(Requirements.DownloadBytes);

        public string RequiredSpaceText => SizeText.Format(Requirements.RequiredFreeBytes);

        public string AvailableSpaceText =>
            $"{Requirements.TargetDrive} sürücüsünde {SizeText.Format(Requirements.AvailableFreeBytes)} boş";

        public bool HasEnoughSpace => Requirements.HasEnoughSpace;

        public string VersionText => "LibreOffice " + Requirements.Version;

        public string PublisherText => $"{Requirements.Publisher} — dijital imzası doğrulanır";

        public string StageTitle => State switch
        {
            EngineSetupState.Downloading => "LibreOffice indiriliyor…",
            EngineSetupState.Verifying => "Dijital imza doğrulanıyor…",
            EngineSetupState.Preparing => "Dosyalar hazırlanıyor…",
            EngineSetupState.Completed => "Hazır",
            _ => ""
        };

        public string StageDescription => State switch
        {
            EngineSetupState.Downloading => "İndirme bittiğinde dosyanın gerçekten LibreOffice'in yayıncısına ait olduğu denetlenecek.",
            EngineSetupState.Verifying => "İmza geçersizse dosya silinir ve hiçbir şey çalıştırılmaz.",
            EngineSetupState.Preparing => "Bu adım birkaç dakika sürebilir. Lütfen pencereyi kapatmayın.",
            EngineSetupState.Completed => "Dönüştürme motoru hazır. Belgeleriniz dönüştürülüyor.",
            _ => ""
        };

        [RelayCommand(CanExecute = nameof(CanStart))]
        private Task Start() => RunAsync();

        [RelayCommand(CanExecute = nameof(CanRetryNow))]
        private Task Retry() => RunAsync();

        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void Cancel()
        {
            _cts?.Cancel();
        }

        [RelayCommand]
        private void Close()
        {
            _cts?.Cancel();
            CloseRequested?.Invoke(this, State == EngineSetupState.Completed);
        }

        private bool CanStart() => State == EngineSetupState.Consent && HasEnoughSpace;

        private bool CanRetryNow() => State == EngineSetupState.Failed && CanRetry;

        private bool CanCancel() => IsWorking;

        private async Task RunAsync()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ProgressPercent = 0;
            ProgressText = "";
            ErrorTitle = "";
            ErrorMessage = "";
            State = EngineSetupState.Downloading;

            var progress = new Progress<EngineSetupProgress>(OnProgress);

            try
            {
                await _installer.InstallAsync(progress, _cts.Token);
                State = EngineSetupState.Completed;
                CloseRequested?.Invoke(this, true);
            }
            catch (OperationCanceledException)
            {
                // İptal: kullanıcı onay ekranına döner, yarım kalan dosyalar kurulum servisi tarafından silinir.
                State = EngineSetupState.Consent;
            }
            catch (EngineSetupException ex)
            {
                ShowError(ex.Error, ex.Message);
            }
            catch (Exception ex)
            {
                // Beklenmeyen hatanın ayrıntısı günlüğe yazılır; kullanıcıya ham sistem iletisi gösterilmez.
                FileLogger.LogError("OfficeEngineSetup", ex);
                ShowError(EngineSetupError.Unknown, "");
            }
        }

        private void OnProgress(EngineSetupProgress value)
        {
            // İptalden ya da hatadan sonra gecikmeli gelen bildirimler ekranı geri değiştirmesin.
            if (!IsWorking)
                return;

            State = value.Stage switch
            {
                EngineSetupStage.Verifying => EngineSetupState.Verifying,
                EngineSetupStage.Preparing => EngineSetupState.Preparing,
                _ => EngineSetupState.Downloading
            };

            if (value.Stage == EngineSetupStage.Downloading && value.BytesTotal > 0)
            {
                ProgressPercent = Math.Clamp(value.BytesDone * 100d / value.BytesTotal, 0, 100);
                ProgressText = $"{SizeText.Format(value.BytesDone)} / {SizeText.Format(value.BytesTotal)}  •  %{ProgressPercent:0}";
            }
            else
            {
                ProgressText = "";
            }
        }

        private void ShowError(EngineSetupError error, string technicalMessage)
        {
            (ErrorTitle, ErrorMessage, CanRetry) = error switch
            {
                EngineSetupError.DownloadFailed => (
                    "İndirilemedi",
                    "İnternet bağlantısı kurulamadı ya da indirme yarıda kesildi. Bağlantınızı kontrol edip yeniden deneyin.",
                    true),
                EngineSetupError.SignatureInvalid => (
                    "Güvenlik doğrulaması başarısız",
                    "İndirilen dosyanın dijital imzası doğrulanamadı. Dosya silindi ve hiçbir şey çalıştırılmadı. " +
                    "Farklı bir ağ üzerinden yeniden deneyebilirsiniz.",
                    true),
                EngineSetupError.DiskFull => (
                    "Yeterli disk alanı yok",
                    $"Hazırlık için {Requirements.TargetDrive} sürücüsünde en az {RequiredSpaceText} boş alan gerekir. " +
                    "Yer açtıktan sonra yeniden deneyin.",
                    true),
                _ => (
                    "Hazırlanamadı",
                    string.IsNullOrWhiteSpace(technicalMessage) ? "Beklenmeyen bir hata oluştu." : technicalMessage,
                    true)
            };

            // Boş alan ya da sürüm bilgisi değişmiş olabilir.
            Requirements = _installer.GetRequirements();
            OnPropertyChanged(nameof(Requirements));
            OnPropertyChanged(nameof(AvailableSpaceText));
            OnPropertyChanged(nameof(HasEnoughSpace));

            State = EngineSetupState.Failed;
        }
    }
}
