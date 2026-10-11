using System.Text;
using DocConverter.Models;
using DocConverter.Services;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace DocConverter.Tests;

public class ScannedPdfBuilderTests : IDisposable
{
    // A4 @ 300 DPI
    private const int A4Width = 2480;
    private const int A4Height = 3508;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ScanPdf_{Guid.NewGuid():N}");

    public ScannedPdfBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void LowQualityScan_IsFarSmallerThanEmbeddingThePngAsIs()
    {
        // Bildirilen hata: düşük kalite seçilse bile PDF çok büyük çıkıyordu, çünkü PNG kayıpsız gömülüyordu.
        string page = CreateScannedPage("colour.png", A4Width, A4Height, 300, colour: true);
        long legacy = BuildLegacyPdf(page);

        var small = Build(ScanOutputQuality.Small, page);
        var balanced = Build(ScanOutputQuality.Balanced, page);
        var high = Build(ScanOutputQuality.High, page);
        var lossless = Build(ScanOutputQuality.Lossless, page);

        small.FileSizeBytes.Should().BeLessThan(legacy / 10, "küçük profil eski çıktının onda birinden küçük olmalı");
        balanced.FileSizeBytes.Should().BeLessThan(legacy / 5);
        small.FileSizeBytes.Should().BeLessThan(balanced.FileSizeBytes);
        balanced.FileSizeBytes.Should().BeLessThan(high.FileSizeBytes);
        high.FileSizeBytes.Should().BeLessThan(lossless.FileSizeBytes);

        ReadRaw(small.OutputPath).Should().Contain("/DCTDecode", "renkli sayfa JPEG olarak gömülmeli");
    }

    [Theory]
    [InlineData(ScanOutputQuality.Small)]
    [InlineData(ScanOutputQuality.Balanced)]
    [InlineData(ScanOutputQuality.High)]
    [InlineData(ScanOutputQuality.Lossless)]
    public void PhysicalPageSize_IsPreservedWhateverTheQuality(ScanOutputQuality quality)
    {
        string page = CreateScannedPage("a4.png", A4Width, A4Height, 300, colour: true);

        var result = Build(quality, page);

        using var pdf = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        pdf.PageCount.Should().Be(1);
        pdf.Pages[0].Width.Point.Should().BeApproximately(595.2, 1.0);
        pdf.Pages[0].Height.Point.Should().BeApproximately(841.9, 1.0);
    }

    [Fact]
    public void RotatedPage_IsActuallyRotatedNotStretched()
    {
        string page = CreateScannedPage("rot.png", A4Width, A4Height, 300, colour: true);

        var result = ScannedPdfBuilder.Build(
            new[] { new ScanPageSource(page, 90, 300) },
            Path.Combine(_dir, "rotated.pdf"),
            new ScanPdfOptions());

        using var pdf = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        pdf.Pages[0].Width.Point.Should().BeApproximately(841.9, 1.0);
        pdf.Pages[0].Height.Point.Should().BeApproximately(595.2, 1.0);
    }

    [Fact]
    public void BlackAndWhiteScan_IsStoredAsOneBitFax()
    {
        // 1-bit gri PNG'yi PDFsharp doğrudan okuyamıyor ("Unsupported image format"); oluşturucu bunu dönüştürmeli.
        string page = CreateScannedPage("bw.png", A4Width, A4Height, 300, colour: false, bilevel: true);

        var result = Build(ScanOutputQuality.Small, page);

        string raw = ReadRaw(result.OutputPath);
        raw.Should().Contain("/CCITTFaxDecode");
        raw.Should().Contain("/BitsPerComponent 1");
        result.FileSizeBytes.Should().BeLessThan(200 * 1024);
    }

    [Fact]
    public void GrayscaleScan_IsStoredAsGrayJpeg()
    {
        string page = CreateScannedPage("gray.png", 1240, 1754, 150, colour: false);

        var result = Build(ScanOutputQuality.Balanced, page);

        string raw = ReadRaw(result.OutputPath);
        raw.Should().Contain("/DCTDecode");
        raw.Should().Contain("/DeviceGray");
    }

    [Fact]
    public void ImageWithoutDpiInformation_UsesTheScanResolution()
    {
        string page = CreateScannedPage("nodpi.png", 1240, 1754, dpi: 0, colour: true);

        var result = ScannedPdfBuilder.Build(
            new[] { new ScanPageSource(page, 0, 150) },
            Path.Combine(_dir, "nodpi.pdf"),
            new ScanPdfOptions());

        using var pdf = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        pdf.Pages[0].Width.Point.Should().BeApproximately(595.2, 1.0, "96 DPI varsayılırsa sayfa 930 pt çıkardı");
    }

    [Fact]
    public void MissingPages_AreSkippedAndCounted()
    {
        string good = CreateScannedPage("good.png", 620, 877, 150, colour: true);
        string missing = Path.Combine(_dir, "silinmis.png");

        var result = ScannedPdfBuilder.Build(
            new[]
            {
                new ScanPageSource(good, 0, 150),
                new ScanPageSource(missing, 0, 150),
                new ScanPageSource(good, 0, 150)
            },
            Path.Combine(_dir, "partial.pdf"),
            new ScanPdfOptions());

        result.PageCount.Should().Be(2);
        result.SkippedPages.Should().Be(1);
    }

    [Fact]
    public void NothingReadable_ThrowsAndLeavesExistingFileUntouched()
    {
        string output = Path.Combine(_dir, "existing.pdf");
        File.WriteAllText(output, "önceki içerik");

        Action act = () => ScannedPdfBuilder.Build(
            new[] { new ScanPageSource(Path.Combine(_dir, "yok.png"), 0, 300) },
            output,
            new ScanPdfOptions());

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(output).Should().Be("önceki içerik", "başarısız kayıt hedef dosyayı bozmamalı");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void ManyPages_KeepTheirOrder()
    {
        var sources = new List<ScanPageSource>();
        for (int i = 0; i < 9; i++)
        {
            // Her sayfanın genişliği farklı: sıra, PDF'teki sayfa boyutlarından okunabilir.
            string page = CreateScannedPage($"p{i}.png", 300 + i * 30, 400, 150, colour: true);
            sources.Add(new ScanPageSource(page, 0, 150));
        }

        var result = ScannedPdfBuilder.Build(sources, Path.Combine(_dir, "order.pdf"), new ScanPdfOptions());

        using var pdf = PdfReader.Open(result.OutputPath, PdfDocumentOpenMode.Import);
        pdf.PageCount.Should().Be(9);
        for (int i = 0; i < 9; i++)
            pdf.Pages[i].Width.Point.Should().BeApproximately((300 + i * 30) / 150.0 * 72, 0.6);
    }

    [Fact]
    public async Task ScannerService_SaveUsesSelectedQuality()
    {
        string page = CreateScannedPage("svc.png", A4Width, A4Height, 300, colour: true);
        var service = new ScannerService(() => throw new InvalidOperationException("tarama yapılmamalı"));
        var pages = new List<ScannedPageItem> { new() { PageNumber = 1, FilePath = page, ResolutionDpi = 300 } };

        var small = await service.SaveScannedPagesAsync(
            pages, Path.Combine(_dir, "svc-small.pdf"), new ScanPdfOptions { Quality = ScanOutputQuality.Small });
        var high = await service.SaveScannedPagesAsync(
            pages, Path.Combine(_dir, "svc-high.pdf"), new ScanPdfOptions { Quality = ScanOutputQuality.High });

        small.PageCount.Should().Be(1);
        small.FileSizeBytes.Should().BeLessThan(high.FileSizeBytes / 2);
    }

    private ScanPdfResult Build(ScanOutputQuality quality, string page)
    {
        return ScannedPdfBuilder.Build(
            new[] { new ScanPageSource(page, 0, 300) },
            Path.Combine(_dir, $"{Path.GetFileNameWithoutExtension(page)}-{quality}.pdf"),
            new ScanPdfOptions { Quality = quality });
    }

    /// <summary>Düzeltmeden önceki davranış: PNG, PDFsharp'a olduğu gibi verilir.</summary>
    private long BuildLegacyPdf(string pngPath)
    {
        string output = Path.Combine(_dir, "legacy.pdf");
        using (var doc = new PdfDocument())
        using (var image = XImage.FromFile(pngPath))
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(image.PointWidth);
            page.Height = XUnit.FromPoint(image.PointHeight);
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            doc.Save(output);
        }

        return new FileInfo(output).Length;
    }

    private static string ReadRaw(string pdfPath) => Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));

    /// <summary>
    /// Taranmış sayfayı andıran bir görüntü üretir: metin satırları ve tarayıcı algılayıcısının gürültüsü.
    /// Gürültü önemlidir; gürültüsüz sentetik görüntü PNG ile de iyi sıkışır ve sorunu gizler.
    /// </summary>
    private string CreateScannedPage(string name, int width, int height, int dpi, bool colour, bool bilevel = false)
    {
        string path = Path.Combine(_dir, name);
        var random = new Random(1234);

        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(rows =>
        {
            int margin = width / 10;
            int lineHeight = Math.Max(4, height / 220);
            int linePitch = lineHeight * 3;

            for (int y = 0; y < rows.Height; y++)
            {
                Span<Rgb24> row = rows.GetRowSpan(y);
                bool textRow = y > margin && y < height - margin && (y - margin) % linePitch < lineHeight;

                for (int x = 0; x < row.Length; x++)
                {
                    bool ink = textRow && x > margin && x < width - margin && (x / (lineHeight * 2)) % 5 != 4;

                    if (bilevel)
                    {
                        byte v = ink ? (byte)0 : (byte)255;
                        row[x] = new Rgb24(v, v, v);
                        continue;
                    }

                    int baseValue = ink ? 40 : 244;
                    int r = Math.Clamp(baseValue + random.Next(-10, 11), 0, 255);
                    if (colour)
                    {
                        int g = Math.Clamp(baseValue + random.Next(-10, 11), 0, 255);
                        int b = Math.Clamp(baseValue - 6 + random.Next(-10, 11), 0, 255);
                        row[x] = new Rgb24((byte)r, (byte)g, (byte)b);
                    }
                    else
                    {
                        row[x] = new Rgb24((byte)r, (byte)r, (byte)r);
                    }
                }
            }
        });

        if (dpi > 0)
        {
            image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            image.Metadata.HorizontalResolution = dpi;
            image.Metadata.VerticalResolution = dpi;
        }

        var encoder = bilevel
            ? new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit1 }
            : colour
                ? new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 }
                : new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 };

        image.Save(path, encoder);
        return path;
    }
}
