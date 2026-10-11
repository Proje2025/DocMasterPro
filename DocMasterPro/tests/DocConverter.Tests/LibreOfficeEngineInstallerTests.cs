using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// LibreOffice'in indirilmesi, doğrulanması ve hazırlanması. Ağ, imza denetimi ve dosya açma sahtedir;
/// gerçek imza denetimi <see cref="AuthenticodeVerifierTests"/>, gerçek indirme <see cref="EngineInstallEndToEndTests"/> içindedir.
/// </summary>
public sealed class LibreOfficeEngineInstallerTests : IDisposable
{
    private const long MB = 1024L * 1024L;

    private readonly string _root = OfficeTestEnvironment.NewTempDirectory("engine-");
    private readonly byte[] _installer = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 + i / 7)).ToArray();
    private readonly FakeServer _server;
    private readonly FakeVerifier _verifier = new();
    private readonly FakeExtractor _extractor = new();
    private readonly List<EngineSetupProgress> _progress = new();
    private long _freeSpace = 50L * 1024 * MB;

    public LibreOfficeEngineInstallerTests()
    {
        _server = new FakeServer(_installer);
    }

    public void Dispose() => OfficeTestEnvironment.DeleteDirectory(_root);

    private string EngineRoot => Path.Combine(_root, "engine");

    private string PartialFile => Path.Combine(EngineRoot, "download", "libreoffice-26.2.6.msi.part");

    private LibreOfficeRelease Release(byte[]? expectedContent = null) => new(
        Version: "26.2.6",
        Sources: new[] { new Uri("https://birincil.example/lo.msi"), new Uri("https://arsiv.example/lo.msi") },
        SizeBytes: _installer.Length,
        Sha256: Convert.ToHexString(SHA256.HashData(expectedContent ?? _installer)).ToLowerInvariant(),
        Publisher: "The Document Foundation",
        RequiredFreeBytes: 2560 * MB);

    private LibreOfficeEngineInstaller Installer(LibreOfficeRelease? release = null) =>
        new(release ?? Release(), EngineRoot, _server, _verifier, _extractor, _ => _freeSpace);

    private Task InstallAsync(LibreOfficeEngineInstaller? installer = null, CancellationToken cancellationToken = default) =>
        (installer ?? Installer()).InstallAsync(new SynchronousProgress(_progress), cancellationToken);

    private LibreOfficeInstallation? FoundEngine() =>
        new OfficeEngineProbe(EngineRoot, () => Array.Empty<string>()).FindLibreOffice();

    [Fact]
    public async Task Install_DownloadsVerifiesAndPreparesTheEngine()
    {
        await InstallAsync();

        LibreOfficeInstallation? engine = FoundEngine();
        engine.Should().NotBeNull();
        engine!.IsDownloaded.Should().BeTrue();
        _verifier.Checked.Should().ContainSingle().Which.Publisher.Should().Be("The Document Foundation");
        _extractor.Calls.Should().Be(1);

        _progress.Select(p => p.Stage).Distinct().Should().Equal(
            EngineSetupStage.Downloading, EngineSetupStage.Verifying, EngineSetupStage.Preparing);
        _progress.Last(p => p.Stage == EngineSetupStage.Downloading).BytesDone.Should().Be(_installer.Length);

        Directory.Exists(Path.Combine(EngineRoot, "download")).Should().BeFalse("indirilen kurulum dosyası iş bitince silinir");
        string engineDirectory = Path.Combine(EngineRoot, "libreoffice-26.2.6");
        Directory.GetFiles(engineDirectory, "*.msi").Should().BeEmpty("açma işleminin bıraktığı kurulum kopyası silinir");
        Directory.Exists(Path.Combine(engineDirectory, "help")).Should().BeFalse();
    }

    [Fact]
    public void Requirements_DescribeThePinnedReleaseAndTheTargetDrive()
    {
        _freeSpace = 3 * 1024 * MB;

        EngineSetupRequirements requirements = Installer().GetRequirements();

        requirements.Version.Should().Be("26.2.6");
        requirements.DownloadBytes.Should().Be(_installer.Length);
        requirements.Publisher.Should().Be("The Document Foundation");
        requirements.AvailableFreeBytes.Should().Be(3 * 1024 * MB);
        requirements.TargetDrive.Should().Be(Path.GetPathRoot(EngineRoot)!.TrimEnd('\\'));
        requirements.HasEnoughSpace.Should().BeTrue();
    }

    [Fact]
    public void PinnedRelease_PointsAtTheOfficialHttpsSources()
    {
        LibreOfficeRelease release = LibreOfficeRelease.Current;

        release.Sources.Should().NotBeEmpty();
        release.Sources.Should().OnlyContain(source => source.Scheme == "https" && source.Host.EndsWith(".documentfoundation.org"));
        release.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        release.Publisher.Should().Be("The Document Foundation");
        release.RequiredFreeBytes.Should().BeGreaterThan(release.SizeBytes * 4);
    }

    [Fact]
    public async Task InterruptedDownload_ResumesWhereItStopped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PartialFile)!);
        await File.WriteAllBytesAsync(PartialFile, _installer.AsSpan(0, 120_000).ToArray());

        await InstallAsync();

        _server.Requests.Should().ContainSingle().Which.RangeFrom.Should().Be(120_000);
        FoundEngine().Should().NotBeNull();
        _progress.First().BytesDone.Should().Be(120_000, "ilerleme sıfırdan değil kalınan yerden başlar");
    }

    [Fact]
    public async Task ServerIgnoringResume_RestartsFromTheBeginning()
    {
        _server.SupportsRange = false;
        Directory.CreateDirectory(Path.GetDirectoryName(PartialFile)!);
        await File.WriteAllBytesAsync(PartialFile, new byte[50_000]); // yanlış içerikli yarım dosya

        await InstallAsync();

        FoundEngine().Should().NotBeNull("dosya baştan yazıldığı için özet tutar");
    }

    [Fact]
    public async Task FirstSourceDown_FallsBackToTheArchive()
    {
        _server.FailingHosts.Add("birincil.example");

        await InstallAsync();

        _server.Requests.Select(r => r.Host).Should().Equal("birincil.example", "arsiv.example");
        FoundEngine().Should().NotBeNull();
    }

    [Fact]
    public async Task NoConnection_ReportsDownloadFailed_AndKeepsWhatWasDownloaded()
    {
        _server.CutAfterBytes = 100_000;

        var act = () => InstallAsync();

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.DownloadFailed);
        new FileInfo(PartialFile).Length.Should().BeGreaterThan(0, "yeniden denemede kaldığı yerden sürmesi için");
        _extractor.Calls.Should().Be(0);
        FoundEngine().Should().BeNull();
    }

    [Fact]
    public async Task TamperedFile_IsDeletedAndNothingIsRun()
    {
        byte[] other = _installer.ToArray();
        other[1000] ^= 0xFF;

        var act = () => InstallAsync(Installer(Release(expectedContent: other)));

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.SignatureInvalid);
        _verifier.Checked.Should().BeEmpty("özeti tutmayan dosyanın imzasına bakmaya gerek yok");
        _extractor.Calls.Should().Be(0, "doğrulanmayan dosya çalıştırılmaz");
        Directory.GetFiles(Path.Combine(EngineRoot, "download")).Should().BeEmpty("güvenilmeyen dosya diskte bırakılmaz");
        FoundEngine().Should().BeNull();
    }

    [Fact]
    public async Task WrongPublisherOrBrokenSignature_IsDeletedAndNothingIsRun()
    {
        _verifier.Trusted = false;

        var act = () => InstallAsync();

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.SignatureInvalid);
        _extractor.Calls.Should().Be(0);
        Directory.GetFiles(Path.Combine(EngineRoot, "download")).Should().BeEmpty();
        FoundEngine().Should().BeNull();
    }

    [Fact]
    public async Task WrongSizeOnServer_IsNotDownloaded()
    {
        var act = () => InstallAsync(Installer(Release() with { SizeBytes = _installer.Length + 10 }));

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.DownloadFailed);
        _extractor.Calls.Should().Be(0);
    }

    [Fact]
    public async Task NotEnoughSpace_FailsBeforeDownloadingAnything()
    {
        _freeSpace = 1024 * MB;

        var act = () => InstallAsync();

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.DiskFull);
        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractionFailure_LeavesNoHalfPreparedEngine()
    {
        _extractor.Error = new EngineSetupException(EngineSetupError.Unknown, "açılamadı");

        var act = () => InstallAsync();

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.Unknown);
        FoundEngine().Should().BeNull();
        Directory.GetDirectories(EngineRoot, "libreoffice-*").Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractionFailure_WithNoSpaceLeft_IsReportedAsDiskFull()
    {
        _extractor.Error = new EngineSetupException(EngineSetupError.Unknown, "açılamadı");
        _extractor.OnExtract = () => _freeSpace = 5 * MB;

        var act = () => InstallAsync();

        (await act.Should().ThrowAsync<EngineSetupException>()).Which.Error.Should().Be(EngineSetupError.DiskFull);
    }

    [Fact]
    public async Task ExtractionWithoutLibreOffice_IsRejected()
    {
        _extractor.CreateProgram = false;

        var act = () => InstallAsync();

        await act.Should().ThrowAsync<EngineSetupException>();
        FoundEngine().Should().BeNull("içinde soffice olmayan klasör motor sayılmaz");
    }

    [Fact]
    public async Task Cancel_DuringDownload_StopsAndKeepsThePartialFileForLater()
    {
        using var cts = new CancellationTokenSource();
        _server.OnBytesSent = sent => { if (sent >= 100_000) cts.Cancel(); };

        var act = () => InstallAsync(cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(PartialFile).Should().BeTrue();
        _extractor.Calls.Should().Be(0);
        FoundEngine().Should().BeNull();
    }

    [Fact]
    public async Task Cancel_DuringExtraction_LeavesNoHalfPreparedEngine_AndSkipsTheDownloadNextTime()
    {
        using var cts = new CancellationTokenSource();
        _extractor.OnExtract = cts.Cancel;

        var cancelled = () => InstallAsync(cancellationToken: cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        FoundEngine().Should().BeNull();

        _extractor.OnExtract = null;
        _server.Requests.Clear();
        await InstallAsync();

        _server.Requests.Should().BeEmpty("indirilmiş dosya yeniden indirilmez, yalnızca yeniden doğrulanır");
        _verifier.Checked.Should().HaveCount(2);
        FoundEngine().Should().NotBeNull();
    }

    [Fact]
    public async Task Remove_DeletesTheEngineAndFreesTheSpace()
    {
        await InstallAsync();
        FoundEngine().Should().NotBeNull();

        await Installer().RemoveAsync();

        FoundEngine().Should().BeNull();
        Directory.GetDirectories(EngineRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_WhenNothingIsInstalled_DoesNothing()
    {
        await Installer().RemoveAsync();

        Directory.Exists(EngineRoot).Should().BeFalse();
    }

    // ==================== Sahte bileşenler ====================

    /// <summary>İlerlemeyi çağrıldığı anda kaydeder (Progress&lt;T&gt; bildirimleri sıraya koyar).</summary>
    private sealed class SynchronousProgress : IProgress<EngineSetupProgress>
    {
        private readonly List<EngineSetupProgress> _sink;

        public SynchronousProgress(List<EngineSetupProgress> sink) => _sink = sink;

        public void Report(EngineSetupProgress value)
        {
            lock (_sink)
                _sink.Add(value);
        }
    }

    private sealed class FakeVerifier : IFileSignatureVerifier
    {
        public bool Trusted { get; set; } = true;
        public List<(string Path, string Publisher)> Checked { get; } = new();

        public bool IsSignedBy(string filePath, string expectedPublisher)
        {
            Checked.Add((filePath, expectedPublisher));
            return Trusted;
        }
    }

    /// <summary>msiexec /a çıktısını taklit eder: program klasörü, kurulum kopyası ve yardım dosyaları.</summary>
    private sealed class FakeExtractor : IEngineExtractor
    {
        public int Calls { get; private set; }
        public bool CreateProgram { get; set; } = true;
        public Exception? Error { get; set; }
        public Action? OnExtract { get; set; }

        public Task ExtractAsync(string installerPath, string targetDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            File.Exists(installerPath).Should().BeTrue();

            Directory.CreateDirectory(Path.Combine(targetDirectory, "help"));
            File.WriteAllText(Path.Combine(targetDirectory, "help", "main.html"), "yardım");
            File.Copy(installerPath, Path.Combine(targetDirectory, "LibreOffice.msi"));
            File.SetAttributes(Path.Combine(targetDirectory, "LibreOffice.msi"), FileAttributes.ReadOnly);

            if (CreateProgram)
            {
                string program = Path.Combine(targetDirectory, "program");
                Directory.CreateDirectory(program);
                foreach (string name in new[] { "soffice.exe", "soffice.com", "soffice.bin" })
                    File.WriteAllText(Path.Combine(program, name), "");
            }

            OnExtract?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Error == null ? Task.CompletedTask : Task.FromException(Error);
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly byte[] _content;

        public FakeServer(byte[] content) => _content = content;

        public bool SupportsRange { get; set; } = true;
        public HashSet<string> FailingHosts { get; } = new();
        public int? CutAfterBytes { get; set; }
        public Action<long>? OnBytesSent { get; set; }
        public List<(string Host, long? RangeFrom)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            Requests.Add((host, from));

            if (FailingHosts.Contains(host))
                throw new HttpRequestException("Sunucuya ulaşılamıyor.");

            long start = SupportsRange && from is { } value ? value : 0;
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new ServedStream(_content, start, CutAfterBytes, OnBytesSent)),
            };
            response.Content.Headers.ContentLength = _content.Length - start;
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, _content.Length - 1, _content.Length);
            return Task.FromResult(response);
        }
    }

    /// <summary>İçeriği küçük parçalar halinde verir; istenirse ortasında bağlantıyı keser.</summary>
    private sealed class ServedStream : Stream
    {
        private readonly byte[] _content;
        private readonly int? _cutAfter;
        private readonly Action<long>? _onSent;
        private long _position;
        private long _sent;

        public ServedStream(byte[] content, long start, int? cutAfter, Action<long>? onSent)
        {
            _content = content;
            _position = start;
            _cutAfter = cutAfter;
            _onSent = onSent;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_cutAfter is { } cut && _sent >= cut)
                throw new IOException("Bağlantı kesildi.");

            int read = (int)Math.Min(Math.Min(count, 16_384), _content.Length - _position);
            if (read <= 0)
                return 0;

            Array.Copy(_content, _position, buffer, offset, read);
            _position += read;
            _sent += read;
            _onSent?.Invoke(_sent);
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] temporary = new byte[buffer.Length];
            int read = Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Windows'un gerçek imza denetimi.</summary>
public sealed class AuthenticodeVerifierTests : IDisposable
{
    private readonly string _work = OfficeTestEnvironment.NewTempDirectory("imza-");

    public void Dispose() => OfficeTestEnvironment.DeleteDirectory(_work);

    /// <summary>Gömülü imza taşıyan, her .NET kurulumunda bulunan bir dosya (.NET çalışma zamanının kendisi).</summary>
    private static string SignedFile => typeof(object).Assembly.Location;

    /// <summary>O dosyayı imzalayan sertifikanın adı (.NET 8'de ".NET").</summary>
    private static string SignerOfSignedFile
    {
        get
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(SignedFile));
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
    }

    [Fact]
    public void ValidSignature_FromTheExpectedPublisher_IsAccepted()
    {
        new AuthenticodeVerifier().IsSignedBy(SignedFile, SignerOfSignedFile).Should().BeTrue();
    }

    [Fact]
    public void ValidSignature_FromAnotherPublisher_IsRejected()
    {
        new AuthenticodeVerifier().IsSignedBy(SignedFile, "The Document Foundation")
            .Should().BeFalse("geçerli imza yetmez; imzalayan beklenen yayıncı olmalı");
    }

    [Fact]
    public void ModifiedFile_IsRejected()
    {
        string copy = Path.Combine(_work, "degistirilmis.dll");
        byte[] bytes = File.ReadAllBytes(SignedFile);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);

        new AuthenticodeVerifier().IsSignedBy(copy, SignerOfSignedFile).Should().BeFalse();
    }

    [RequiresLocalInstallerFact]
    public void RealLibreOfficeInstaller_IsAcceptedOnlyForItsOwnPublisher()
    {
        string installer = OfficeTestEnvironment.LocalInstaller!;
        var verifier = new AuthenticodeVerifier();

        verifier.IsSignedBy(installer, LibreOfficeRelease.Current.Publisher).Should().BeTrue();
        verifier.IsSignedBy(installer, SignerOfSignedFile).Should().BeFalse();
    }

    [Fact]
    public void UnsignedOrMissingFile_IsRejectedWithoutThrowing()
    {
        string unsigned = Path.Combine(_work, "imzasiz.msi");
        File.WriteAllText(unsigned, "imzasız içerik");
        var verifier = new AuthenticodeVerifier();

        verifier.IsSignedBy(unsigned, "The Document Foundation").Should().BeFalse();
        verifier.IsSignedBy(Path.Combine(_work, "yok.msi"), "The Document Foundation").Should().BeFalse();
        verifier.IsSignedBy(SignedFile, "").Should().BeFalse();
    }
}

/// <summary>
/// Gerçek indirme, doğrulama ve açma (356 MB indirir, birkaç dakika sürer). Yalnızca DOCMASTER_E2E_ENGINE ortam
/// değişkeni bir klasör gösterdiğinde çalışır; kurulum o klasöre yapılır.
/// </summary>
[Collection(OfficeTestEnvironment.Collection)]
public sealed class EngineInstallEndToEndTests
{
    private static string? TargetRoot => Environment.GetEnvironmentVariable("DOCMASTER_E2E_ENGINE");

    public sealed class EndToEndFactAttribute : FactAttribute
    {
        public EndToEndFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(TargetRoot))
                Skip = "Gerçek indirme denemesi: DOCMASTER_E2E_ENGINE ortam değişkenine boş bir klasör verin.";
        }
    }

    [EndToEndFact]
    public async Task RealDownload_IsVerifiedPreparedAndConvertsADocument()
    {
        string root = Path.Combine(TargetRoot!, "engine");
        var installer = new LibreOfficeEngineInstaller(
            LibreOfficeRelease.Current, root, new HttpClientHandler(), new AuthenticodeVerifier(), new MsiEngineExtractor());
        var stages = new List<EngineSetupStage>();
        var progress = new Progress<EngineSetupProgress>(value => { lock (stages) stages.Add(value.Stage); });

        await installer.InstallAsync(progress, CancellationToken.None);

        LibreOfficeInstallation? engine = new OfficeEngineProbe(root, () => Array.Empty<string>()).FindLibreOffice();
        engine.Should().NotBeNull();
        engine!.Version.Should().Be(LibreOfficeRelease.Current.Version);
        Directory.Exists(Path.Combine(root, "download")).Should().BeFalse();

        string output = Path.Combine(TargetRoot!, "sozlesme.pdf");
        await new LibreOfficeConverter(engine.SofficePath, Path.Combine(TargetRoot!, "work"))
            .ConvertToPdfAsync(OfficeTestEnvironment.Sample("sozlesme.docx"), output);
        OfficeTestEnvironment.ReadPdf(output).Text.Should().Contain("Sözleşmesi");

        await installer.RemoveAsync();
        new OfficeEngineProbe(root, () => Array.Empty<string>()).FindLibreOffice().Should().BeNull();
    }
}
