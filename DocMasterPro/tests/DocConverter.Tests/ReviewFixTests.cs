using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// Kod incelemesinde bulunan hataların düzeltmelerini doğrular.
/// </summary>
public class ReviewFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ReviewFix_{Guid.NewGuid():N}");
    private readonly List<string> _produced = new();

    public ReviewFixTests()
    {
        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.FontResolver = new WindowsFontResolver();

        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (string file in _produced)
        {
            try { File.Delete(file); } catch { }
        }

        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------------- Görüntü → PDF ----------------

    [Theory]
    [InlineData("gif")]
    [InlineData("tiff")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("png")]
    [InlineData("jpg")]
    public void ConvertImageToPdf_SupportsEveryAdvertisedFormat(string extension)
    {
        // Arayüz GIF, TIFF ve WEBP'yi de kabul ediyor; PDFsharp bunları doğrudan okuyamıyordu.
        string path = Path.Combine(_dir, "resim." + extension);
        using (var image = new Image<Rgba32>(400, 300, new Rgba32(30, 120, 200)))
            image.Save(path);

        string pdf = Track(new ConverterService().ConvertImageToPdf(path));

        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void ConvertImageToPdf_FitsLargePhotoOnA4InsteadOfMetreWidePage()
    {
        string path = Path.Combine(_dir, "foto.jpg");
        using (var image = new Image<Rgb24>(3000, 2000, new Rgb24(90, 140, 60)))
            image.Save(path, new JpegEncoder { Quality = 80 });

        string pdf = Track(new ConverterService().ConvertImageToPdf(path));

        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        // Eskiden sayfa 3000 x 2000 pt (106 x 71 cm) çıkıyordu.
        doc.Pages[0].Width.Point.Should().BeApproximately(841.9, 1.0);
        doc.Pages[0].Height.Point.Should().BeApproximately(561.3, 1.0);
    }

    [Fact]
    public void ConvertImageToPdf_UsesScanResolutionForPageSize()
    {
        // 300 DPI A4 tarama: sayfa tam A4 olmalı.
        string path = Path.Combine(_dir, "tarama.png");
        using (var image = new Image<Rgb24>(2480, 3508, new Rgb24(250, 250, 250)))
        {
            image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            image.Metadata.HorizontalResolution = 300;
            image.Metadata.VerticalResolution = 300;
            image.Save(path);
        }

        string pdf = Track(new ConverterService().ConvertImageToPdf(path));

        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        doc.Pages[0].Width.Point.Should().BeApproximately(595.2, 1.0);
        doc.Pages[0].Height.Point.Should().BeApproximately(841.9, 1.0);
    }

    [Fact]
    public void ConvertImageToPdf_HonoursExifOrientation()
    {
        // Telefonla dik çekilen fotoğraf: pikseller yatay, EXIF "90° döndür" der.
        string path = Path.Combine(_dir, "telefon.jpg");
        using (var image = new Image<Rgb24>(400, 200, new Rgb24(200, 200, 200)))
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
            image.Save(path, new JpegEncoder());
        }

        string pdf = Track(new ConverterService().ConvertImageToPdf(path));

        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        doc.Pages[0].Height.Point.Should().BeGreaterThan(doc.Pages[0].Width.Point, "fotoğraf dik görünmeli");
    }

    // ---------------- Boş sayfa algılama ----------------

    [Fact]
    public void BlankPage_WhitePageIsBlank()
    {
        new BlankPageDetector().IsImageBlank(SavePage("bos.png", 250, lines: 0)).Should().BeTrue();
    }

    [Fact]
    public void BlankPage_OffWhiteScannedPaperIsBlank()
    {
        // Taranan kağıt nadiren saf beyazdır; sabit 240 eşiği böyle sayfaları hiç boş saymıyordu.
        new BlankPageDetector().IsImageBlank(SavePage("gri-kagit.png", 228, lines: 0, noise: 5)).Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void BlankPage_PageWithAFewLinesIsNotBlank(int lines)
    {
        // %98,6'sı beyaz olan üç satırlık sayfa eskiden "boş" sayılıp siliniyordu.
        new BlankPageDetector().IsImageBlank(SavePage($"satir{lines}.png", 250, lines)).Should().BeFalse();
    }

    [Fact]
    public void BlankPage_DustSpecksDoNotMakeAPageNonBlank()
    {
        string path = Path.Combine(_dir, "toz.png");
        using (var image = new Image<Rgb24>(1240, 1754, new Rgb24(248, 248, 248)))
        {
            var random = new Random(9);
            for (int i = 0; i < 12; i++)
            {
                int x = random.Next(10, 1220), y = random.Next(10, 1740);
                image[x, y] = new Rgb24(20, 20, 20);
                image[x + 1, y] = new Rgb24(20, 20, 20);
            }

            image.SaveAsPng(path);
        }

        new BlankPageDetector().IsImageBlank(path).Should().BeTrue();
    }

    [Fact]
    public void BlankPage_DarkPageIsNotBlank()
    {
        string path = Path.Combine(_dir, "koyu.png");
        using (var image = new Image<Rgb24>(600, 800, new Rgb24(15, 15, 15)))
            image.SaveAsPng(path);

        new BlankPageDetector().IsImageBlank(path).Should().BeFalse();
    }

    // ---------------- Yerinde kayıt ----------------

    [Fact]
    public void SaveInPlace_ReplacesFileWithoutLeavingTempFiles()
    {
        string path = Path.Combine(_dir, "belge.pdf");
        CreatePdf(path, pages: 3);

        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            doc.Pages.RemoveAt(0);
            PdfService.SaveInPlace(doc, path);
        }

        using var reopened = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        reopened.PageCount.Should().Be(2);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void SaveInPlace_KeepsOriginalWhenTargetIsLocked()
    {
        string path = Path.Combine(_dir, "kilitli.pdf");
        CreatePdf(path, pages: 2);
        long originalLength = new FileInfo(path).Length;

        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            doc.Pages.RemoveAt(0);
            Action act = () => PdfService.SaveInPlace(doc, path);
            act.Should().Throw<Exception>();
        }

        new FileInfo(path).Length.Should().Be(originalLength, "kayıt başarısız olursa özgün dosya bozulmamalı");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    // ---------------- Cihazlar / yazıcı / güncelleme ----------------

    [Fact]
    public void StableHash_IsDeterministicAndCaseInsensitive()
    {
        // string.GetHashCode() her çalıştırmada değişir; kayıtlı cihaz kimlikleri eşleşmiyordu.
        DeviceDiscoveryService.StableHash("RICOH SP 4510SF PCL 6").Should().Be(DeviceDiscoveryService.StableHash("ricoh sp 4510sf pcl 6"));
        DeviceDiscoveryService.StableHash("fi-6230dj").Should().MatchRegex("^[0-9A-F]{8}$");
        DeviceDiscoveryService.StableHash("A").Should().NotBe(DeviceDiscoveryService.StableHash("B"));
        // FNV-1a("A") bilinen değer: işlem tohumundan bağımsız olduğunu gösterir.
        DeviceDiscoveryService.StableHash("A").Should().Be("C40BF6CC");
    }

    [Theory]
    [InlineData("IP_192.168.1.10", "192.168.1.10", true)]
    [InlineData("192.168.1.10", "192.168.1.10", true)]
    [InlineData("IP_192.168.1.10", "192.168.1.1", false)]
    [InlineData("IP_192.168.1.100", "192.168.1.10", false)]
    [InlineData("IP_10.192.168.1.10", "192.168.1.10", false)]
    [InlineData("WSD-1234", "192.168.1.10", false)]
    public void ContainsIpAddress_MatchesWholeAddressOnly(string port, string ip, bool expected)
    {
        PrinterService.ContainsIpAddress(port, ip).Should().Be(expected);
    }

    [Fact]
    public void ResolveWindowsPrinterName_ReportsWhenItFallsBackToAnotherPrinter()
    {
        var unknown = new DeviceInfo { Name = "Kurulu-Olmayan-Yazici-" + Guid.NewGuid().ToString("N"), Type = DeviceType.Printer };

        PrinterService.ResolveWindowsPrinterName(unknown, out bool matched);

        matched.Should().BeFalse("eşleşmeyen cihaz için varsayılan yazıcıya düşüldüğü bildirilmeli");
    }

    [Fact]
    public void UpdateViewModel_ShowCheckResult_MakesUpdateButtonUsable()
    {
        // Açılıştaki güncelleme penceresinde "Şimdi Güncelle" hiçbir şey yapmıyordu: sonuç saklanmıyordu.
        var vm = new UpdateViewModel(new UpdateService());
        var result = new UpdateCheckResult
        {
            HasUpdate = true,
            CurrentVersion = "1.2.4",
            LatestVersion = "9.9.9",
            ReleaseTitle = "Test",
            HtmlUrl = "https://example.invalid/release"
        };

        vm.ShowCheckResult(result);

        vm.State.Should().Be(UpdateState.UpdateAvailable);
        vm.LatestVersion.Should().Be("9.9.9");
        vm.StageDescription.Should().Contain("9.9.9");

        var field = typeof(UpdateViewModel).GetField("_checkResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.GetValue(vm).Should().BeSameAs(result);
    }

    [Fact]
    public void ScanFailedException_IsAnInvalidOperationException()
    {
        // Mevcut çağıranlar "tarayıcı yok" durumunu InvalidOperationException olarak yakalıyor.
        new ScanFailedException("x").Should().BeAssignableTo<InvalidOperationException>();
    }

    private string Track(string path)
    {
        _produced.Add(path);
        return path;
    }

    private string SavePage(string name, byte paper, int lines, int noise = 0)
    {
        string path = Path.Combine(_dir, name);
        var random = new Random(3);

        using var image = new Image<Rgb24>(1240, 1754);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                Span<Rgb24> row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    int v = paper + (noise > 0 ? random.Next(-noise, noise + 1) : 0);
                    byte b = (byte)Math.Clamp(v, 0, 255);
                    row[x] = new Rgb24(b, b, b);
                }
            }

            // 150 DPI'da ~11 punto metin satırı: 14 piksel yüksek, kelimelere bölünmüş.
            for (int line = 0; line < lines; line++)
            {
                int top = 220 + line * 34;
                for (int y = top; y < top + 14; y++)
                {
                    Span<Rgb24> row = rows.GetRowSpan(y);
                    for (int x = 150; x < 1090; x++)
                    {
                        // Harf boşlukları: çizginin yaklaşık üçte biri mürekkep.
                        if ((x / 6) % 3 == 0 && (x / 70) % 6 != 5)
                            row[x] = new Rgb24(25, 25, 25);
                    }
                }
            }
        });

        image.SaveAsPng(path);
        return path;
    }

    private static void CreatePdf(string path, int pages)
    {
        using var doc = new PdfDocument();
        var font = new XFont("Arial", 12);
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Sayfa {i + 1}", font, XBrushes.Black, new XPoint(72, 96));
        }

        doc.Save(path);
    }
}
