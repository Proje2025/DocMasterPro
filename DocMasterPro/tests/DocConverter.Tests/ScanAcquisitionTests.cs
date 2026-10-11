using System.Runtime.InteropServices;
using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// Gerçek tarayıcı olmadan tarama döngüsünü doğrular: sahte oturum, bir fi serisi tarayıcının
/// uykudan uyanırken verdiği hataları taklit eder.
/// </summary>
public class ScanAcquisitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ScanAcq_{Guid.NewGuid():N}");
    private readonly DeviceInfo _scanner = new() { Name = "fi-6230", Type = DeviceType.Scanner };
    private readonly List<TimeSpan> _waits = new();

    public ScanAcquisitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static COMException Wia(uint code) => new($"WIA 0x{code:X8}", unchecked((int)code));

    private ScanAcquisitionOutcome Run(ScriptedScanner device, ScanOptions? options = null, CancellationToken token = default)
    {
        return ScanAcquisition.Run(
            device.CreateSession,
            _scanner,
            options ?? new ScanOptions(),
            index => Path.Combine(_dir, $"page_{index:D4}.png"),
            (path, _) => device.DeliveredPages.Add(path),
            progress: null,
            cancellationToken: token,
            wait: _waits.Add);
    }

    [Fact]
    public void FirstConnectFails_ThenSucceeds_ScansWithoutSecondClick()
    {
        // Kullanıcının bildirdiği durum: ilk "Tara" hata veriyor, ikincisi çalışıyor.
        var device = new ScriptedScanner { PagesInFeeder = 3 };
        device.OpenFailures.Enqueue(Wia(WiaErrors.Offline));

        var outcome = Run(device);

        outcome.PagesAcquired.Should().Be(3);
        outcome.Cancelled.Should().BeFalse();
        outcome.Warning.Should().BeNull();
        device.SessionsCreated.Should().Be(2, "ilk bağlantı bırakılıp baştan kurulmalı");
        device.SessionsDisposed.Should().Be(2);
        _waits.Should().Equal(ScanAcquisition.RetryDelays[0]);
    }

    [Theory]
    [InlineData(WiaErrors.Busy)]
    [InlineData(WiaErrors.WarmingUp)]
    [InlineData(WiaErrors.DeviceCommunication)]
    [InlineData(0x80004005u)] // E_FAIL: sürücülerin en sık döndürdüğü belirsiz hata
    public void FirstTransferFailsTransiently_IsRetriedOnFreshConnection(uint code)
    {
        var device = new ScriptedScanner { PagesInFeeder = 2 };
        device.TransferFailures.Enqueue(Wia(code));

        var outcome = Run(device);

        outcome.PagesAcquired.Should().Be(2);
        device.SessionsCreated.Should().Be(2);
        _waits.Should().ContainSingle();
    }

    [Fact]
    public void DeviceNeverResponds_FailsAfterAllAttemptsWithReadableReason()
    {
        var device = new ScriptedScanner { PagesInFeeder = 5 };
        for (int i = 0; i < 10; i++)
            device.OpenFailures.Enqueue(Wia(WiaErrors.Offline));

        Action act = () => Run(device);

        act.Should().Throw<ScanFailedException>()
            .Which.Message.Should().Contain("çevrimdışı").And.Contain("0x80210005");
        device.SessionsCreated.Should().Be(ScanAcquisition.RetryDelays.Length + 1);
        _waits.Should().Equal(ScanAcquisition.RetryDelays);
        device.SessionsDisposed.Should().Be(device.SessionsCreated, "başarısız bağlantılar açık bırakılmamalı");
    }

    [Fact]
    public void FeederReportsEmptyWhileWakingUp_IsRetriedOnce()
    {
        var device = new ScriptedScanner { PagesInFeeder = 2 };
        device.TransferFailures.Enqueue(Wia(WiaErrors.PaperEmpty));

        var outcome = Run(device);

        outcome.PagesAcquired.Should().Be(2);
        _waits.Should().Equal(ScanAcquisition.PaperEmptyRetryDelay);
    }

    [Fact]
    public void FeederReallyEmpty_ReportsNoPaperInsteadOfGenericError()
    {
        var device = new ScriptedScanner { PagesInFeeder = 0 };

        Action act = () => Run(device);

        act.Should().Throw<ScanFailedException>().Which.Message.Should().Contain("kağıt");
        device.SessionsCreated.Should().Be(2, "yalnızca bir kez yeniden denenmeli");
    }

    [Theory]
    [InlineData(WiaErrors.PaperJam)]
    [InlineData(WiaErrors.CoverOpen)]
    [InlineData(WiaErrors.MultiFeed)]
    public void ProblemThatNeedsTheUser_IsNotRetried(uint code)
    {
        var device = new ScriptedScanner { PagesInFeeder = 3 };
        device.TransferFailures.Enqueue(Wia(code));

        Action act = () => Run(device);

        act.Should().Throw<ScanFailedException>();
        device.SessionsCreated.Should().Be(1);
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void FeederRunsOut_EndsNormallyWithoutWarning()
    {
        var device = new ScriptedScanner { PagesInFeeder = 4 };

        var outcome = Run(device);

        outcome.PagesAcquired.Should().Be(4);
        outcome.Warning.Should().BeNull();
        device.DeliveredPages.Should().HaveCount(4).And.OnlyContain(p => File.Exists(p));
        device.SessionsCreated.Should().Be(1);
    }

    [Fact]
    public void JamInTheMiddle_KeepsScannedPagesAndWarns()
    {
        var device = new ScriptedScanner { PagesInFeeder = 10 };
        device.FailAtTransfer[3] = Wia(WiaErrors.PaperJam);

        var outcome = Run(device);

        outcome.PagesAcquired.Should().Be(2);
        outcome.Warning.Should().Contain("2. sayfadan sonra").And.Contain("sıkıştı");
        device.SessionsCreated.Should().Be(1, "sayfa alındıktan sonra yeniden bağlanıp baştan taranmamalı");
    }

    [Fact]
    public void Flatbed_ScansExactlyOnePage()
    {
        var device = new ScriptedScanner { PagesInFeeder = 99, Source = ScanSource.Flatbed };

        var outcome = Run(device, new ScanOptions { Source = ScanSource.Flatbed });

        outcome.PagesAcquired.Should().Be(1);
        device.Transfers.Should().Be(1);
    }

    [Fact]
    public void FeederRequestedButDeviceFellBackToFlatbed_DoesNotLoopForever()
    {
        // Besleyicisi olmayan bir tarayıcıda "kağıt bitti" hatası hiç gelmez.
        var device = new ScriptedScanner { PagesInFeeder = int.MaxValue, Source = ScanSource.Flatbed, Notice = "cam" };

        var outcome = Run(device, new ScanOptions { Source = ScanSource.FeederSingle });

        outcome.PagesAcquired.Should().Be(1);
        outcome.Notice.Should().Be("cam");
    }

    [Fact]
    public void CancellationBetweenPages_StopsAndKeepsPages()
    {
        using var cts = new CancellationTokenSource();
        var device = new ScriptedScanner { PagesInFeeder = 50 };
        device.AfterTransfer = count => { if (count == 3) cts.Cancel(); };

        var outcome = Run(device, token: cts.Token);

        outcome.Cancelled.Should().BeTrue();
        outcome.PagesAcquired.Should().Be(3);
    }

    [Fact]
    public void UserCancelsInDriverDialog_IsNotAnError()
    {
        var device = new ScriptedScanner { PagesInFeeder = 5 };
        device.TransferFailures.Enqueue(Wia(WiaErrors.UserCancelled));

        var outcome = Run(device);

        outcome.Cancelled.Should().BeTrue();
        outcome.PagesAcquired.Should().Be(0);
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void NoScannerInstalled_FailsImmediatelyWithoutRetrying()
    {
        var device = new ScriptedScanner();
        device.OpenFailures.Enqueue(new ScanFailedException("Windows'ta kayıtlı hiçbir WIA tarayıcı bulunamadı."));

        Action act = () => Run(device);

        act.Should().Throw<ScanFailedException>().WithMessage("*WIA tarayıcı bulunamadı*");
        device.SessionsCreated.Should().Be(1);
        _waits.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WiaErrors.PaperEmpty, WiaFailureKind.PaperEmpty)]
    [InlineData(WiaErrors.UserCancelled, WiaFailureKind.Cancelled)]
    [InlineData(WiaErrors.PaperJam, WiaFailureKind.NeedsUser)]
    [InlineData(WiaErrors.Offline, WiaFailureKind.Transient)]
    [InlineData(WiaErrors.DeviceLocked, WiaFailureKind.Transient)]
    [InlineData(0x800706BAu, WiaFailureKind.Transient)] // RPC sunucusu kullanılamıyor
    public void WiaErrors_ClassifiesHResults(uint code, WiaFailureKind expected)
    {
        WiaErrors.Classify(Wia(code)).Should().Be(expected);
        WiaErrors.Describe(Wia(code)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ScannerService_WithFakeDevice_ReturnsPagesAndReportsThemLive()
    {
        var device = new ScriptedScanner { PagesInFeeder = 3, Dpi = 200 };
        device.OpenFailures.Enqueue(Wia(WiaErrors.Offline));
        var service = new ScannerService(device.CreateSession);
        var live = new List<int>();
        var pageProgress = new SynchronousProgress<ScannedPageItem>(page => live.Add(page.PageNumber));

        // Yeniden deneme beklemesini kısaltmak için iptal belirteci kullanılmaz; ilk gecikme 1,5 sn'dir.
        var result = await service.ScanAsync(_scanner, new ScanOptions(), CancellationToken.None, null, pageProgress);

        try
        {
            result.Pages.Should().HaveCount(3);
            result.Pages.Select(p => p.PageNumber).Should().Equal(1, 2, 3);
            result.Pages.Should().OnlyContain(p => p.ResolutionDpi == 200 && p.PreviewImage != null && File.Exists(p.FilePath));
            live.Should().Equal(1, 2, 3);
        }
        finally
        {
            service.DeletePageFiles(result.Pages);
        }

        result.Pages.Should().OnlyContain(p => !File.Exists(p.FilePath), "temizlenen sayfaların dosyaları silinmeli");
    }

    [Fact]
    public async Task ScannerService_RemovesBlankPagesWhenAsked()
    {
        var device = new ScriptedScanner { PagesInFeeder = 4 };
        device.BlankPages.Add(2);
        device.BlankPages.Add(4);
        var service = new ScannerService(device.CreateSession);

        var result = await service.ScanAsync(_scanner, new ScanOptions { RemoveBlankPages = true });

        try
        {
            result.Pages.Should().HaveCount(2);
            result.BlankPagesSkipped.Should().Be(2);
            result.Pages.Select(p => p.PageNumber).Should().Equal(1, 2);
        }
        finally
        {
            service.DeletePageFiles(result.Pages);
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    /// <summary>Betiklenmiş sahte tarayıcı: hangi çağrının hangi hatayı vereceği önceden belirlenir.</summary>
    private sealed class ScriptedScanner
    {
        public Queue<Exception> OpenFailures { get; } = new();
        public Queue<Exception> TransferFailures { get; } = new();
        public Dictionary<int, Exception> FailAtTransfer { get; } = new();
        public HashSet<int> BlankPages { get; } = new();
        public List<string> DeliveredPages { get; } = new();
        public Action<int>? AfterTransfer { get; set; }
        public int PagesInFeeder { get; set; }
        public ScanSource Source { get; set; } = ScanSource.FeederSingle;
        public string? Notice { get; set; }
        public int Dpi { get; set; } = 300;
        public int SessionsCreated { get; private set; }
        public int SessionsDisposed { get; private set; }
        public int Transfers { get; private set; }

        public IScanSession CreateSession()
        {
            SessionsCreated++;
            return new Session(this);
        }

        private sealed class Session : IScanSession
        {
            private readonly ScriptedScanner _owner;
            private bool _disposed;

            public Session(ScriptedScanner owner) => _owner = owner;

            public int EffectiveDpi => _owner.Dpi;
            public ScanSource EffectiveSource => _owner.Source;
            public string? Notice => _owner.Notice;

            public void Open(DeviceInfo scanner, ScanOptions options)
            {
                if (_owner.OpenFailures.Count > 0)
                    throw _owner.OpenFailures.Dequeue();
            }

            public void TransferNextPage(string filePath)
            {
                if (_owner.TransferFailures.Count > 0)
                    throw _owner.TransferFailures.Dequeue();

                int number = _owner.Transfers + 1;
                if (_owner.FailAtTransfer.TryGetValue(number, out var scripted))
                    throw scripted;

                if (_owner.PagesInFeeder <= 0)
                    throw Wia(WiaErrors.PaperEmpty);

                _owner.PagesInFeeder--;
                _owner.Transfers = number;

                using (var image = new Image<Rgb24>(120, 160, new Rgb24(255, 255, 255)))
                {
                    if (!_owner.BlankPages.Contains(number))
                    {
                        image.ProcessPixelRows(rows =>
                        {
                            for (int y = 20; y < 140; y += 6)
                                rows.GetRowSpan(y).Slice(10, 100).Fill(new Rgb24(0, 0, 0));
                        });
                    }

                    image.SaveAsPng(filePath);
                }

                _owner.AfterTransfer?.Invoke(number);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _owner.SessionsDisposed++;
            }
        }
    }
}
