using System.Runtime.InteropServices;
using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// Motor seçimi: belgenin Office uygulaması kuruluysa Microsoft Office, değilse LibreOffice, ikisi de yoksa açık hata.
/// Gerçek Office ya da LibreOffice çalıştırılmaz.
/// </summary>
public class OfficeToPdfConverterTests
{
    private static readonly LibreOfficeInstallation Installed = new(@"C:\Program Files\LibreOffice\program\soffice.com", "26.2.6", IsDownloaded: false);
    private static readonly LibreOfficeInstallation Downloaded = new(@"C:\Users\x\AppData\Local\DocMasterPro\engine\libreoffice-26.2.6\program\soffice.com", "26.2.6", IsDownloaded: true);

    [Theory]
    [InlineData("rapor.docx")]
    [InlineData("tablo.xlsx")]
    [InlineData("sunum.pptx")]
    public async Task OfficeInstalled_UsesMicrosoftOfficeEvenWhenLibreOfficeExists(string file)
    {
        var probe = new FakeProbe { Word = true, Excel = true, PowerPoint = true, LibreOffice = Installed };
        var office = new RecordingEngine();
        var libre = new RecordingEngine();
        var converter = new OfficeToPdfConverter(probe, office, _ => libre);

        await converter.ConvertAsync(file, "cikti.pdf");

        office.Calls.Should().ContainSingle().Which.Should().Be((file, "cikti.pdf"));
        libre.Calls.Should().BeEmpty("Office kuruluyken kalite bugünküyle aynı kalmalı");
    }

    [Fact]
    public async Task NoOffice_UsesLibreOffice()
    {
        var probe = new FakeProbe { LibreOffice = Downloaded };
        var office = new RecordingEngine();
        var libre = new RecordingEngine();
        LibreOfficeInstallation? used = null;
        var converter = new OfficeToPdfConverter(probe, office, installation => { used = installation; return libre; });

        await converter.ConvertAsync("rapor.docx", "cikti.pdf");

        office.Calls.Should().BeEmpty();
        libre.Calls.Should().ContainSingle();
        used.Should().Be(Downloaded);
    }

    [Fact]
    public async Task OnlyWordInstalled_ExcelFilesGoToLibreOffice()
    {
        var probe = new FakeProbe { Word = true, LibreOffice = Installed };
        var office = new RecordingEngine();
        var libre = new RecordingEngine();
        var converter = new OfficeToPdfConverter(probe, office, _ => libre);

        await converter.ConvertAsync("sozlesme.docx", "a.pdf");
        await converter.ConvertAsync("tablo.xlsx", "b.pdf");

        office.Calls.Should().ContainSingle().Which.Input.Should().Be("sozlesme.docx");
        libre.Calls.Should().ContainSingle().Which.Input.Should().Be("tablo.xlsx");
        converter.ResolveEngine(".docx").Should().Be(OfficeEngineKind.MicrosoftOffice);
        converter.ResolveEngine(".xlsx").Should().Be(OfficeEngineKind.LibreOfficeInstalled);
    }

    [Fact]
    public async Task NoEngine_ThrowsAClearErrorNamingWhatIsNeeded()
    {
        var converter = new OfficeToPdfConverter(new FakeProbe(), new RecordingEngine(), _ => new RecordingEngine());

        var act = () => converter.ConvertAsync("sunum.pptx", "cikti.pdf");

        var error = (await act.Should().ThrowAsync<OfficeEngineMissingException>()).Which;
        error.App.Should().Be(OfficeApp.PowerPoint);
        error.Message.Should().Contain("PowerPoint").And.Contain("LibreOffice");
        converter.HasEngineFor(".pptx").Should().BeFalse();
    }

    [Fact]
    public async Task UnsupportedExtension_IsRejectedInsteadOfSilentlySkipped()
    {
        var probe = new FakeProbe { Word = true, Excel = true, PowerPoint = true };
        var converter = new OfficeToPdfConverter(probe, new RecordingEngine(), _ => new RecordingEngine());

        var act = () => converter.ConvertAsync("arsiv.zip", "cikti.pdf");

        await act.Should().ThrowAsync<NotSupportedException>();
        converter.HasEngineFor(".zip").Should().BeFalse();
    }

    [Theory]
    [InlineData(unchecked((int)0x80040154))] // sınıf kayıtlı değil
    [InlineData(unchecked((int)0x80080005))] // sunucu başlatılamadı
    public async Task BrokenOfficeInstall_FallsBackToLibreOffice(int hresult)
    {
        var probe = new FakeProbe { Word = true, LibreOffice = Installed };
        var office = new RecordingEngine { Error = new COMException("Office başlatılamadı", hresult) };
        var libre = new RecordingEngine();
        var converter = new OfficeToPdfConverter(probe, office, _ => libre);

        await converter.ConvertAsync("rapor.docx", "cikti.pdf");

        libre.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task OfficeFailsOnTheDocumentItself_IsNotHiddenByAFallback()
    {
        var probe = new FakeProbe { Word = true, LibreOffice = Installed };
        var office = new RecordingEngine { Error = new COMException("Dosya bozuk", unchecked((int)0x800A1436)) };
        var libre = new RecordingEngine();
        var converter = new OfficeToPdfConverter(probe, office, _ => libre);

        var act = () => converter.ConvertAsync("rapor.docx", "cikti.pdf");

        await act.Should().ThrowAsync<COMException>();
        libre.Calls.Should().BeEmpty("çıktı sessizce farklı bir motorla üretilmemeli");
    }

    [Fact]
    public async Task Cancellation_IsPassedToTheEngine()
    {
        var probe = new FakeProbe { LibreOffice = Installed };
        var libre = new RecordingEngine();
        var converter = new OfficeToPdfConverter(probe, new RecordingEngine(), _ => libre);
        using var cts = new CancellationTokenSource();

        await converter.ConvertAsync("rapor.docx", "cikti.pdf", cts.Token);

        libre.Tokens.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    [Fact]
    public void Status_ReflectsWhatIsOnTheMachine()
    {
        Status(new FakeProbe { Word = true, LibreOffice = Installed }).Kind.Should().Be(OfficeEngineKind.MicrosoftOffice);

        OfficeEngineStatus installed = Status(new FakeProbe { LibreOffice = Installed });
        installed.Kind.Should().Be(OfficeEngineKind.LibreOfficeInstalled);
        installed.Version.Should().Be("26.2.6");
        installed.CanConvertPdfToWord.Should().BeFalse();

        OfficeEngineStatus downloaded = Status(new FakeProbe { LibreOffice = Downloaded });
        downloaded.Kind.Should().Be(OfficeEngineKind.LibreOfficeDownloaded);
        downloaded.CanRemove.Should().BeTrue();

        Status(new FakeProbe()).Kind.Should().Be(OfficeEngineKind.None);

        static OfficeEngineStatus Status(FakeProbe probe) =>
            new OfficeToPdfConverter(probe, new RecordingEngine(), _ => new RecordingEngine()).GetStatus();
    }

    [Theory]
    [InlineData(".docx", OfficeApp.Word)]
    [InlineData(".DOC", OfficeApp.Word)]
    [InlineData(".rtf", OfficeApp.Word)]
    [InlineData(".txt", OfficeApp.Word)]
    [InlineData(".html", OfficeApp.Word)]
    [InlineData(".xls", OfficeApp.Excel)]
    [InlineData(".XLSX", OfficeApp.Excel)]
    [InlineData(".ppt", OfficeApp.PowerPoint)]
    [InlineData(".pptx", OfficeApp.PowerPoint)]
    public void AppFor_MapsExtensionsToTheirOfficeApp(string extension, OfficeApp expected)
    {
        OfficeToPdfConverter.AppFor(extension).Should().Be(expected);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".png")]
    [InlineData("")]
    public void AppFor_ReturnsNullForOtherFiles(string extension)
    {
        OfficeToPdfConverter.AppFor(extension).Should().BeNull();
    }

    private sealed class FakeProbe : IOfficeEngineProbe
    {
        public bool Word { get; init; }
        public bool Excel { get; init; }
        public bool PowerPoint { get; init; }
        public LibreOfficeInstallation? LibreOffice { get; init; }

        public bool IsOfficeAppInstalled(OfficeApp app) => app switch
        {
            OfficeApp.Word => Word,
            OfficeApp.Excel => Excel,
            _ => PowerPoint
        };

        public LibreOfficeInstallation? FindLibreOffice() => LibreOffice;
    }

    private sealed class RecordingEngine : IDocumentPdfEngine
    {
        public List<(string Input, string Output)> Calls { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        public Exception? Error { get; init; }

        public Task ConvertToPdfAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        {
            Calls.Add((inputPath, outputPath));
            Tokens.Add(cancellationToken);
            return Error == null ? Task.CompletedTask : Task.FromException(Error);
        }
    }
}
