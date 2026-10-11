using System.Diagnostics;
using System.Globalization;
using System.Text;
using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// LibreOffice'in başsız çalıştırılması: komut satırı, yalıtılmış profil, süre aşımı, iptal ve hata durumları.
/// soffice yerine küçük betikler kullanılır; gerçek dönüştürmeler <see cref="LibreOfficeRealConversionTests"/> içindedir.
/// </summary>
[Collection(OfficeTestEnvironment.Collection)]
public sealed class LibreOfficeConverterTests : IDisposable
{
    private readonly string _work = OfficeTestEnvironment.NewTempDirectory("lo-");

    public void Dispose() => OfficeTestEnvironment.DeleteDirectory(_work);

    [Fact]
    public void Arguments_RunHeadlessWithAnIsolatedProfile()
    {
        const string source = @"C:\Temp\job\belge.docx";

        var arguments = LibreOfficeConverter.BuildArguments(@"C:\Temp\Çalışma Klasörü\profile", @"C:\Temp\job\out", source, ".docx");

        arguments.Should().Contain(new[] { "--headless", "--norestore", "--nolockcheck", "--nofirststartwizard" });
        arguments.Should().Contain("-env:UserInstallation=file:///C:/Temp/%C3%87al%C4%B1%C5%9Fma%20Klas%C3%B6r%C3%BC/profile",
            "kullanıcının kendi LibreOffice profiline dokunulmamalı");
        arguments.Should().ContainInOrder("--convert-to", "pdf", "--outdir", @"C:\Temp\job\out", source);
        arguments.Should().NotContain(argument => argument.StartsWith("--infilter"));
    }

    [Fact]
    public void Arguments_PlainText_IsReadAsUtf8()
    {
        var arguments = LibreOfficeConverter.BuildArguments(@"C:\p", @"C:\o", @"C:\j\belge.txt", ".txt");

        arguments.Should().Contain("--infilter=Text (encoded):UTF8");
    }

    [Fact]
    public void Profile_DisablesMacrosAndExternalLinks_AndIsWrittenOnce()
    {
        string profile = Path.Combine(_work, "profile");

        LibreOfficeConverter.EnsureProfile(profile);

        string settingsPath = Path.Combine(profile, "user", "registrymodifications.xcu");
        string settings = File.ReadAllText(settingsPath);
        settings.Should().Contain("oor:name=\"DisableMacrosExecution\" oor:op=\"fuse\"><value>true</value>");
        settings.Should().Contain("oor:name=\"MacroSecurityLevel\" oor:op=\"fuse\"><value>3</value>");
        settings.Should().Contain("oor:name=\"BlockUntrustedRefererLinks\" oor:op=\"fuse\"><value>true</value>");

        // LibreOffice ilk çalıştırmada bu dosyaya kendi ayarlarını ekler; ikinci çağrı üzerine yazmamalı.
        File.AppendAllText(settingsPath, "<!-- libreoffice -->");
        LibreOfficeConverter.EnsureProfile(profile);
        File.ReadAllText(settingsPath).Should().EndWith("<!-- libreoffice -->");
    }

    [Fact]
    public void PlainText_IsDecodedWhateverItsEncoding()
    {
        const string text = "Şişli'de çağrı öğün ığdır";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            LibreOfficeConverter.DecodeText(Encoding.UTF8.GetBytes(text)).Should().Be(text, "BOM'suz UTF-8");
            LibreOfficeConverter.DecodeText(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()).Should().Be(text, "BOM'lu UTF-8");
            LibreOfficeConverter.DecodeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()).Should().Be(text, "UTF-16");
            LibreOfficeConverter.DecodeText(Encoding.GetEncoding(1254).GetBytes(text)).Should().Be(text, "Not Defteri'nin eski ANSI kaydı (Windows-1254)");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Success_PutsThePdfWhereRequested_AndLeavesNothingBehind()
    {
        string stub = Stub("ok.cmd",
            "@echo off",
            ":next",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"--outdir\" set \"OUT=%~2\"",
            "shift",
            "goto next",
            ":done",
            "echo %%PDF-1.4 sahte> \"%OUT%\\belge.pdf\"");
        string sourceDirectory = Path.Combine(_work, "Belgelerim");
        Directory.CreateDirectory(sourceDirectory);
        string input = Path.Combine(sourceDirectory, "Hizmet Sözleşmesi (son).docx");
        File.WriteAllText(input, "icerik");
        File.SetAttributes(input, FileAttributes.ReadOnly);
        string output = Path.Combine(_work, "Çıktılar", "sözleşme.pdf");
        string workRoot = Path.Combine(_work, "work");

        try
        {
            await new LibreOfficeConverter(stub, workRoot).ConvertToPdfAsync(input, output);
        }
        finally
        {
            File.SetAttributes(input, FileAttributes.Normal);
        }

        File.ReadAllText(output).Should().StartWith("%PDF-1.4");
        Directory.GetFiles(sourceDirectory).Should().ContainSingle("belgenin klasörüne kilit ya da geçici dosya yazılmamalı");
        Directory.GetDirectories(workRoot, "job-*").Should().BeEmpty();
    }

    [Fact]
    public async Task NoOutput_IsReportedAsAFailure()
    {
        string stub = Stub("bos.cmd", "@exit /b 0");
        string input = Input("bozuk.docx");
        string output = Path.Combine(_work, "cikti.pdf");

        var act = () => new LibreOfficeConverter(stub, Path.Combine(_work, "work")).ConvertToPdfAsync(input, output);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("dönüştüremedi");
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task Timeout_StopsLibreOfficeAndSaysSo()
    {
        DateTime started = DateTime.Now;
        string stub = Stub("takilan.cmd", "@ping -n 60 127.0.0.1 >nul");
        var converter = new LibreOfficeConverter(stub, Path.Combine(_work, "work")) { Timeout = TimeSpan.FromSeconds(2) };
        var watch = Stopwatch.StartNew();

        var act = () => converter.ConvertToPdfAsync(Input("uzun.docx"), Path.Combine(_work, "cikti.pdf"));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("yanıt vermedi");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        StartedSince("PING", started).Should().BeEmpty("alt işlemler de sonlandırılmalı");
    }

    [Fact]
    public async Task Cancel_StopsLibreOffice()
    {
        DateTime started = DateTime.Now;
        string stub = Stub("takilan.cmd", "@ping -n 60 127.0.0.1 >nul");
        var converter = new LibreOfficeConverter(stub, Path.Combine(_work, "work"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var act = () => converter.ConvertToPdfAsync(Input("uzun.docx"), Path.Combine(_work, "cikti.pdf"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        StartedSince("PING", started).Should().BeEmpty();
    }

    [Fact]
    public async Task MissingDocumentOrEngine_FailsBeforeStartingAnything()
    {
        string stub = Stub("bos.cmd", "@exit /b 0");
        string output = Path.Combine(_work, "cikti.pdf");

        var missingInput = () => new LibreOfficeConverter(stub, _work).ConvertToPdfAsync(Path.Combine(_work, "yok.docx"), output);
        var missingEngine = () => new LibreOfficeConverter(Path.Combine(_work, "soffice.com"), _work).ConvertToPdfAsync(Input("a.docx"), output);

        await missingInput.Should().ThrowAsync<FileNotFoundException>();
        await missingEngine.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void LeftoverWorkFolders_AreRecognisedByTheProcessThatOwnsThem()
    {
        LibreOfficeConverter.IsOwnedByRunningInstance(Path.Combine(_work, "lo-" + Environment.ProcessId))
            .Should().BeTrue("çalışan oturumun klasörü silinmemeli");
        LibreOfficeConverter.IsOwnedByRunningInstance(Path.Combine(_work, "lo-2147483000"))
            .Should().BeFalse("sahibi artık çalışmayan klasör temizlenir");
        LibreOfficeConverter.IsOwnedByRunningInstance(Path.Combine(_work, "lo-4"))
            .Should().BeFalse("numarası başka bir programa (System) ait olan klasör temizlenir");
        LibreOfficeConverter.IsOwnedByRunningInstance(Path.Combine(_work, "lo-bozuk"))
            .Should().BeFalse();
    }

    private string Stub(string name, params string[] lines)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllLines(path, lines, Encoding.ASCII);
        return path;
    }

    private string Input(string name)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "icerik");
        return path;
    }

    /// <summary>Bu testin başlamasından sonra açılmış ve hâlâ çalışan işlemler.</summary>
    private static List<int> StartedSince(string processName, DateTime since)
    {
        var alive = new List<int>();
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!process.HasExited && process.StartTime >= since)
                    alive.Add(process.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return alive;
    }
}

/// <summary>Gerçek LibreOffice ile dönüştürme. LibreOffice bulunamazsa atlanır.</summary>
[Collection(OfficeTestEnvironment.Collection)]
public sealed class LibreOfficeRealConversionTests : IClassFixture<LibreOfficeRealConversionTests.WorkFolder>
{
    private readonly WorkFolder _folder;

    public LibreOfficeRealConversionTests(WorkFolder folder) => _folder = folder;

    [RequiresLibreOfficeFact]
    public void Version_IsReadFromTheRealInstallation()
    {
        OfficeTestEnvironment.LibreOffice!.Version.Should().MatchRegex("^[0-9]+[.][0-9]+[.][0-9]+$");
    }

    [RequiresLibreOfficeFact]
    public async Task Word_KeepsTurkishTextAndProducesAllPages()
    {
        string output = await ConvertAsync(OfficeTestEnvironment.Sample("sozlesme.docx"));

        (int pages, string text) = OfficeTestEnvironment.ReadPdf(output);
        pages.Should().BeGreaterThanOrEqualTo(2);
        text.Should().Contain("Sözleşmesi").And.Contain("Yükümlülükler");
    }

    [RequiresLibreOfficeFact]
    public async Task Excel_ProducesAPdfWithTheSheetContent()
    {
        string output = await ConvertAsync(OfficeTestEnvironment.Sample("satis_raporu.xlsx"));

        (int pages, string text) = OfficeTestEnvironment.ReadPdf(output);
        pages.Should().BeGreaterThanOrEqualTo(1);
        text.Should().Contain("Satış Raporu").And.Contain("Toplam");
    }

    [RequiresLibreOfficeFact]
    public async Task PowerPoint_ProducesOnePagePerSlide()
    {
        string output = await ConvertAsync(OfficeTestEnvironment.Sample("tanitim.pptx"));

        OfficeTestEnvironment.ReadPdf(output).Pages.Should().Be(3);
    }

    [RequiresLibreOfficeFact]
    public async Task PlainText_SavedInTheOldAnsiEncoding_KeepsTurkishCharacters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string input = Path.Combine(_folder.Path, "Not Defteri kaydı.txt");
        await File.WriteAllBytesAsync(input, Encoding.GetEncoding(1254).GetBytes("Şişli çağrı öğün ığdır"));

        string output;
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            output = await ConvertAsync(input);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        OfficeTestEnvironment.ReadPdf(output).Text.Should().Contain("Şişli").And.Contain("ığdır");
    }

    [RequiresLibreOfficeFact]
    public async Task SourceFolder_IsLeftUntouched()
    {
        string sourceDirectory = Path.Combine(_folder.Path, "Müşteri Belgeleri");
        Directory.CreateDirectory(sourceDirectory);
        string input = Path.Combine(sourceDirectory, "Hizmet Sözleşmesi (son).docx");
        File.Copy(OfficeTestEnvironment.Sample("sozlesme.docx"), input, overwrite: true);

        await ConvertAsync(input);

        Directory.GetFileSystemEntries(sourceDirectory).Should().ContainSingle("kilit dosyası ya da çıktı belgenin yanına yazılmamalı");
    }

    private async Task<string> ConvertAsync(string input)
    {
        string output = Path.Combine(_folder.Path, "çıktı", Guid.NewGuid().ToString("N") + ".pdf");
        var converter = new LibreOfficeConverter(OfficeTestEnvironment.LibreOffice!.SofficePath, Path.Combine(_folder.Path, "çalışma klasörü"));

        await converter.ConvertToPdfAsync(input, output);

        new FileInfo(output).Length.Should().BeGreaterThan(1000);
        return output;
    }

    /// <summary>Testler aynı profili paylaşır; ilk çalıştırmanın profil oluşturma süresi bir kez ödenir.</summary>
    public sealed class WorkFolder : IDisposable
    {
        public string Path { get; } = OfficeTestEnvironment.NewTempDirectory("lo-gerçek-");

        public void Dispose() => OfficeTestEnvironment.DeleteDirectory(Path);
    }
}
