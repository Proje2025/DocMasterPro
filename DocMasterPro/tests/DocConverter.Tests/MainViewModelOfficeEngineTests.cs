using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>Ana ekrandaki motor göstergesi bilgisayarın gerçek durumunu yansıtır.</summary>
public class MainViewModelOfficeEngineTests
{
    [Fact]
    public void WithWord_ShowsMicrosoftOffice_AndPdfToWordIsAvailable()
    {
        using var scope = new Scope(new Probe { Word = true });

        scope.ViewModel.OfficeEngine.Kind.Should().Be(OfficeEngineKind.MicrosoftOffice);
        scope.ViewModel.ShowWordRequiredNotice.Should().BeFalse();
    }

    [Fact]
    public void WithoutOffice_ShowsLibreOffice_AndExplainsThatPdfToWordNeedsWord()
    {
        using var scope = new Scope(new Probe
        {
            LibreOffice = new LibreOfficeInstallation(@"C:\Program Files\LibreOffice\program\soffice.com", "26.2.6", IsDownloaded: false)
        });

        scope.ViewModel.OfficeEngine.Kind.Should().Be(OfficeEngineKind.LibreOfficeInstalled);
        scope.ViewModel.OfficeEngine.Title.Should().Contain("LibreOffice 26.2.6");
        scope.ViewModel.ShowWordRequiredNotice.Should().BeTrue();
    }

    [Fact]
    public void Refresh_PicksUpAnEngineInstalledWhileTheAppIsOpen()
    {
        var probe = new Probe();
        using var scope = new Scope(probe);
        scope.ViewModel.OfficeEngine.Kind.Should().Be(OfficeEngineKind.None);

        probe.LibreOffice = new LibreOfficeInstallation(@"C:\engine\program\soffice.com", "26.2.6", IsDownloaded: true);
        scope.ViewModel.RefreshOfficeEngine();

        scope.ViewModel.OfficeEngine.Kind.Should().Be(OfficeEngineKind.LibreOfficeDownloaded);
        scope.ViewModel.OfficeEngine.CanRemove.Should().BeTrue();
    }

    private sealed class Scope : IDisposable
    {
        public Scope(Probe probe)
        {
            ViewModel = new MainViewModel(new OfficeToPdfConverter(probe, new NoEngine(), _ => new NoEngine()));
        }

        public MainViewModel ViewModel { get; }

        public void Dispose() => ViewModel.PdfStudio.Dispose();
    }

    private sealed class Probe : IOfficeEngineProbe
    {
        public bool Word { get; set; }
        public LibreOfficeInstallation? LibreOffice { get; set; }

        public bool IsOfficeAppInstalled(OfficeApp app) => app == OfficeApp.Word && Word;

        public LibreOfficeInstallation? FindLibreOffice() => LibreOffice;
    }

    private sealed class NoEngine : IDocumentPdfEngine
    {
        public Task ConvertToPdfAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Bu testte dönüştürme yapılmaz."));
    }
}
