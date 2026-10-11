using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// "Microsoft Office bulunamadı" penceresinin durum geçişleri ve motor göstergesinin metinleri.
/// Gerçek indirme yapılmaz; sahte kurulum servisi kullanılır.
/// </summary>
public class OfficeEngineSetupTests
{
    private const long MB = 1024L * 1024L;

    private static MockOfficeEngineInstaller FastInstaller() => new() { StepDelay = TimeSpan.Zero };

    [Fact]
    public async Task Start_RunsInstallerAndReportsEngineReady()
    {
        var installer = FastInstaller();
        var vm = new OfficeEngineSetupViewModel(installer);
        bool? closedWith = null;
        vm.CloseRequested += (_, ready) => closedWith = ready;

        vm.IsConsent.Should().BeTrue();
        await vm.StartCommand.ExecuteAsync(null);

        vm.State.Should().Be(EngineSetupState.Completed);
        closedWith.Should().BeTrue();
        installer.InstallCalls.Should().Be(1);
    }

    [Fact]
    public void Consent_ShowsWhatWillBeDownloaded()
    {
        var vm = new OfficeEngineSetupViewModel(FastInstaller());

        vm.DownloadSizeText.Should().Be("373 MB");
        vm.RequiredSpaceText.Should().Be("2,0 GB");
        vm.VersionText.Should().Be("LibreOffice 26.2.6");
        vm.PublisherText.Should().Contain("The Document Foundation");
        vm.AvailableSpaceText.Should().Contain("C:").And.Contain("boş");
    }

    [Fact]
    public void NotEnoughDiskSpace_BlocksTheDownloadButton()
    {
        var installer = FastInstaller();
        installer.Requirements = installer.Requirements with { AvailableFreeBytes = 1200 * MB };
        var vm = new OfficeEngineSetupViewModel(installer);

        vm.HasEnoughSpace.Should().BeFalse();
        vm.StartCommand.CanExecute(null).Should().BeFalse("yer yokken indirmeye başlamak yarım kalan dosyalar bırakır");
    }

    [Theory]
    [InlineData(EngineSetupError.DownloadFailed, EngineSetupStage.Downloading, "İndirilemedi")]
    [InlineData(EngineSetupError.SignatureInvalid, EngineSetupStage.Verifying, "Güvenlik doğrulaması başarısız")]
    [InlineData(EngineSetupError.DiskFull, EngineSetupStage.Preparing, "Yeterli disk alanı yok")]
    public async Task Failure_ShowsAnExplanationAndDoesNotReportReady(EngineSetupError error, EngineSetupStage stage, string title)
    {
        var installer = FastInstaller();
        installer.SimulatedError = error;
        installer.FailAt = stage;
        var vm = new OfficeEngineSetupViewModel(installer);
        bool? closedWith = null;
        vm.CloseRequested += (_, ready) => closedWith = ready;

        await vm.StartCommand.ExecuteAsync(null);

        vm.State.Should().Be(EngineSetupState.Failed);
        vm.ErrorTitle.Should().Be(title);
        vm.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        closedWith.Should().BeNull("hata durumunda pencere açık kalır");
    }

    [Fact]
    public async Task SignatureFailure_SaysNothingWasExecuted()
    {
        var installer = FastInstaller();
        installer.SimulatedError = EngineSetupError.SignatureInvalid;
        installer.FailAt = EngineSetupStage.Verifying;
        var vm = new OfficeEngineSetupViewModel(installer);

        await vm.StartCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Contain("silindi").And.Contain("çalıştırılmadı");
    }

    [Fact]
    public async Task UnexpectedError_ShowsAGenericMessageNotTheRawExceptionText()
    {
        var installer = new ThrowingInstaller(
            new UnauthorizedAccessException(@"Access to the path 'C:\Users\x\AppData\Local\DocMasterPro\engine' is denied."));
        var vm = new OfficeEngineSetupViewModel(installer);
        bool? closedWith = null;
        vm.CloseRequested += (_, ready) => closedWith = ready;

        await vm.StartCommand.ExecuteAsync(null);

        vm.State.Should().Be(EngineSetupState.Failed);
        vm.ErrorTitle.Should().Be("Hazırlanamadı");
        vm.ErrorMessage.Should().Be("Beklenmeyen bir hata oluştu.", "ham sistem iletisi kullanıcıya gösterilmez, günlüğe yazılır");
        vm.RetryCommand.CanExecute(null).Should().BeTrue();
        closedWith.Should().BeNull();
    }

    [Fact]
    public async Task Retry_AfterFailure_RunsAgainAndCanSucceed()
    {
        var installer = FastInstaller();
        installer.SimulatedError = EngineSetupError.DownloadFailed;
        var vm = new OfficeEngineSetupViewModel(installer);
        bool? closedWith = null;
        vm.CloseRequested += (_, ready) => closedWith = ready;

        await vm.StartCommand.ExecuteAsync(null);
        vm.RetryCommand.CanExecute(null).Should().BeTrue();

        installer.SimulatedError = null;
        await vm.RetryCommand.ExecuteAsync(null);

        vm.State.Should().Be(EngineSetupState.Completed);
        installer.InstallCalls.Should().Be(2);
        closedWith.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_ReturnsToConsentWithoutReportingReady()
    {
        var installer = new HoldingInstaller();
        var vm = new OfficeEngineSetupViewModel(installer);
        bool? closedWith = null;
        vm.CloseRequested += (_, ready) => closedWith = ready;

        Task run = vm.StartCommand.ExecuteAsync(null);
        await installer.Started.Task;
        vm.IsWorking.Should().BeTrue();
        vm.CancelCommand.CanExecute(null).Should().BeTrue();

        vm.CancelCommand.Execute(null);
        await run;

        vm.State.Should().Be(EngineSetupState.Consent);
        closedWith.Should().BeNull();
    }

    [Fact]
    public async Task Close_WhileWorking_CancelsTheDownload()
    {
        var installer = new HoldingInstaller();
        var vm = new OfficeEngineSetupViewModel(installer);
        bool? closedWith = null;
        vm.CloseRequested += (_, ready) => closedWith = ready;

        Task run = vm.StartCommand.ExecuteAsync(null);
        await installer.Started.Task;

        vm.CloseCommand.Execute(null);
        await run;

        closedWith.Should().BeFalse();
        installer.WasCancelled.Should().BeTrue("pencere kapanınca indirme arka planda sürmemeli");
    }

    [Fact]
    public async Task DownloadProgress_IsShownAsSizeAndPercent()
    {
        var installer = new HoldingInstaller { Report = new EngineSetupProgress(EngineSetupStage.Downloading, 124 * MB, 373 * MB) };
        var vm = new OfficeEngineSetupViewModel(installer);

        Task run = vm.StartCommand.ExecuteAsync(null);
        await installer.Started.Task;
        for (int i = 0; i < 100 && vm.ProgressText.Length == 0; i++)
            await Task.Delay(20);

        vm.ProgressText.Should().Be("124 MB / 373 MB  •  %33");
        vm.ProgressPercent.Should().BeApproximately(33.2, 0.2);
        vm.IsProgressIndeterminate.Should().BeFalse();

        vm.CancelCommand.Execute(null);
        await run;
    }

    [Theory]
    [InlineData(OfficeEngineKind.MicrosoftOffice, true, true, false, false)]
    [InlineData(OfficeEngineKind.LibreOfficeInstalled, true, false, true, false)]
    [InlineData(OfficeEngineKind.LibreOfficeDownloaded, true, false, true, true)]
    [InlineData(OfficeEngineKind.None, false, false, false, false)]
    public void EngineStatus_DescribesWhatEachEngineCanDo(
        OfficeEngineKind kind, bool toPdf, bool pdfToWord, bool isLibreOffice, bool canRemove)
    {
        var status = new OfficeEngineStatus(kind, "26.2.6");

        status.CanConvertToPdf.Should().Be(toPdf);
        status.CanConvertPdfToWord.Should().Be(pdfToWord, "PDF → Word yalnızca Word'ün kendi dönüştürücüsüyle yapılır");
        status.IsLibreOffice.Should().Be(isLibreOffice);
        status.CanRemove.Should().Be(canRemove, "yalnızca uygulamanın indirdiği kopya kaldırılabilir");
        status.Title.Should().NotBeNullOrWhiteSpace();
        status.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EngineStatus_LibreOfficeWarnsAboutChartDifferences()
    {
        new OfficeEngineStatus(OfficeEngineKind.LibreOfficeDownloaded, "26.2.6").Detail.Should().Contain("grafik");
        new OfficeEngineStatus(OfficeEngineKind.LibreOfficeDownloaded, "26.2.6").Title.Should().Contain("26.2.6");
    }

    [Theory]
    [InlineData(373L * 1024 * 1024, "373 MB")]
    [InlineData(2048L * 1024 * 1024, "2,0 GB")]
    [InlineData(1536L * 1024 * 1024, "1,5 GB")]
    [InlineData(0L, "0 MB")]
    public void SizeText_UsesTurkishFormatting(long bytes, string expected)
    {
        SizeText.Format(bytes).Should().Be(expected);
    }

    /// <summary>Öngörülmeyen bir hatayla (EngineSetupException olmayan) başarısız olan kurulum.</summary>
    private sealed class ThrowingInstaller : IOfficeEngineInstaller
    {
        private readonly Exception _error;

        public ThrowingInstaller(Exception error) => _error = error;

        public EngineSetupRequirements GetRequirements() =>
            new("26.2.6", 373 * MB, 2048 * MB, 100L * 1024 * MB, "C:", "The Document Foundation");

        public Task InstallAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken) =>
            Task.FromException(_error);
    }

    /// <summary>İptal edilene kadar bekleyen kurulum; ara durumları sınamak için.</summary>
    private sealed class HoldingInstaller : IOfficeEngineInstaller
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EngineSetupProgress? Report { get; init; }
        public bool WasCancelled { get; private set; }

        public EngineSetupRequirements GetRequirements() =>
            new("26.2.6", 373 * MB, 2048 * MB, 100L * 1024 * MB, "C:", "The Document Foundation");

        public async Task InstallAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            if (Report is { } report)
                progress.Report(report);

            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
