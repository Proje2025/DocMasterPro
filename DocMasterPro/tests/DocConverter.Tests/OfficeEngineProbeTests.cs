using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>LibreOffice'in diskte bulunması: kurulu kopya, DocMaster Pro'nun indirdiği kopya, yarım kalmış klasörler.</summary>
public sealed class OfficeEngineProbeTests : IDisposable
{
    private readonly string _root = OfficeTestEnvironment.NewTempDirectory("probe-");

    public void Dispose() => OfficeTestEnvironment.DeleteDirectory(_root);

    [Fact]
    public void NothingOnDisk_FindsNothing()
    {
        var probe = new OfficeEngineProbe(Path.Combine(_root, "engine"), () => new[] { Path.Combine(_root, "yok") });

        probe.FindLibreOffice().Should().BeNull();
    }

    [Fact]
    public void InstalledCopy_IsFoundWithItsVersion()
    {
        string program = FakeProgramDirectory(Path.Combine(_root, "LibreOffice"), "26.2.6.2");
        var probe = new OfficeEngineProbe(Path.Combine(_root, "engine"), () => new[] { program });

        LibreOfficeInstallation? found = probe.FindLibreOffice();

        found.Should().NotBeNull();
        found!.IsDownloaded.Should().BeFalse();
        found.Version.Should().Be("26.2.6");
        found.SofficePath.Should().Be(Path.Combine(program, "soffice.com"), "çıktısı okunabildiği için konsol başlatıcısı tercih edilir");
    }

    [Fact]
    public void DownloadedCopy_IsUsedOnlyWhenItsReadyMarkerExists()
    {
        string engineRoot = Path.Combine(_root, "engine");
        string directory = Path.Combine(engineRoot, "libreoffice-26.2.6");
        FakeProgramDirectory(directory, "26.2.6.2");
        var probe = new OfficeEngineProbe(engineRoot, () => Array.Empty<string>());

        probe.FindLibreOffice().Should().BeNull("açma işlemi yarıda kalmış bir klasör kullanılmamalı");

        File.WriteAllText(Path.Combine(directory, DownloadedEngine.ReadyMarker), "26.2.6");
        LibreOfficeInstallation? found = probe.FindLibreOffice();

        found.Should().NotBeNull();
        found!.IsDownloaded.Should().BeTrue();
        found.Version.Should().Be("26.2.6");
    }

    [Fact]
    public void InstalledCopy_IsPreferredOverTheDownloadedOne()
    {
        string program = FakeProgramDirectory(Path.Combine(_root, "LibreOffice"), "25.8.1.1");
        string engineRoot = Path.Combine(_root, "engine");
        string downloaded = Path.Combine(engineRoot, "libreoffice-26.2.6");
        FakeProgramDirectory(downloaded, "26.2.6.2");
        File.WriteAllText(Path.Combine(downloaded, DownloadedEngine.ReadyMarker), "26.2.6");
        var probe = new OfficeEngineProbe(engineRoot, () => new[] { program });

        probe.FindLibreOffice()!.IsDownloaded.Should().BeFalse();
    }

    [Fact]
    public void NewestDownloadedVersion_Wins()
    {
        string engineRoot = Path.Combine(_root, "engine");
        foreach (string version in new[] { "25.8.4", "26.2.6", "26.2.10" })
        {
            string directory = Path.Combine(engineRoot, "libreoffice-" + version);
            FakeProgramDirectory(directory, version + ".1");
            File.WriteAllText(Path.Combine(directory, DownloadedEngine.ReadyMarker), version);
        }
        var probe = new OfficeEngineProbe(engineRoot, () => Array.Empty<string>());

        probe.FindLibreOffice()!.Version.Should().Be("26.2.10", "sürümler metin olarak değil sayı olarak karşılaştırılır");
    }

    [Fact]
    public void FolderWithoutTheRealProgram_IsNotMistakenForLibreOffice()
    {
        string program = Path.Combine(_root, "LibreOffice", "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "soffice.exe"), "");

        OfficeEngineProbe.FromProgramDirectory(program, isDownloaded: false).Should().BeNull("soffice.bin yok");
    }

    [Fact]
    public void MissingVersionFile_StillFindsTheEngine()
    {
        string program = FakeProgramDirectory(Path.Combine(_root, "LibreOffice"), version: null);

        LibreOfficeInstallation? found = OfficeEngineProbe.FromProgramDirectory(program, isDownloaded: false);

        found.Should().NotBeNull();
        found!.Version.Should().BeNull();
    }

    [Fact]
    public void Version_ComesFromTheProgramFileWhenTheIniHasNone()
    {
        // Güncel LibreOffice sürümlerinin version.ini dosyasında sürüm satırı yoktur.
        string program = FakeProgramDirectory(Path.Combine(_root, "LibreOffice"), version: null);
        File.Copy(typeof(OfficeEngineProbe).Assembly.Location, Path.Combine(program, "soffice.bin"), overwrite: true);
        Version expected = typeof(OfficeEngineProbe).Assembly.GetName().Version!;

        OfficeEngineProbe.ReadVersion(program).Should().Be($"{expected.Major}.{expected.Minor}.{expected.Build}");
    }

    [Fact]
    public void OfficeDetection_DoesNotThrowAndIsConsistent()
    {
        var probe = new OfficeEngineProbe();

        foreach (OfficeApp app in Enum.GetValues<OfficeApp>())
            probe.IsOfficeAppInstalled(app).Should().Be(probe.IsOfficeAppInstalled(app));
    }

    private static string FakeProgramDirectory(string installDirectory, string? version)
    {
        string program = Path.Combine(installDirectory, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "soffice.exe"), "");
        File.WriteAllText(Path.Combine(program, "soffice.com"), "");
        File.WriteAllText(Path.Combine(program, "soffice.bin"), "");
        if (version != null)
            File.WriteAllText(Path.Combine(program, "version.ini"), $"[Version]\r\nAllLanguages=en-US tr\r\nMsiProductVersion={version}\r\nUpdateChannel=\r\n");
        return program;
    }
}
