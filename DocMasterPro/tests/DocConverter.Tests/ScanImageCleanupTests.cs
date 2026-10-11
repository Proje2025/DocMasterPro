using DocConverter.Services;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace DocConverter.Tests;

public class ScanImageCleanupTests
{
    private static readonly Rgb24 Paper = new(246, 245, 240);
    private static readonly Rgb24 Ink = new(30, 30, 30);
    private static readonly Rgb24 Bed = new(14, 14, 14);

    [Theory]
    [InlineData(2.0)]
    [InlineData(-2.0)]
    [InlineData(4.5)]
    [InlineData(-6.0)]
    [InlineData(0.8)]
    public void EstimateSkewAngle_FindsTheRotationOfTextLines(double degrees)
    {
        using var page = CreateTextPage(1200, 1600);
        using var skewed = Rotate(page, degrees, Paper);

        double estimate = ScanImageCleanup.EstimateSkewAngle(skewed);

        estimate.Should().BeApproximately(degrees, 0.3);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-3.0)]
    [InlineData(5.0)]
    public void Deskew_StraightensThePage(double degrees)
    {
        using var page = CreateTextPage(1200, 1600);
        using var skewed = Rotate(page, degrees, Paper);

        double applied = ScanImageCleanup.Deskew(skewed);

        applied.Should().BeApproximately(-degrees, 0.3);
        Math.Abs(ScanImageCleanup.EstimateSkewAngle(skewed)).Should().BeLessThan(0.3, "düzeltmeden sonra eğim kalmamalı");
    }

    [Fact]
    public void Deskew_SmallAngleKeepsPageDimensions()
    {
        using var page = CreateTextPage(1200, 1600);
        using var skewed = Rotate(page, 1.5, Paper);
        skewed.Mutate(c => c.Crop(new Rectangle((skewed.Width - 1200) / 2, (skewed.Height - 1600) / 2, 1200, 1600)));

        ScanImageCleanup.Deskew(skewed);

        skewed.Width.Should().Be(1200, "A4 taranan sayfa düzeltmeden sonra da A4 kalmalı");
        skewed.Height.Should().Be(1600);
    }

    [Fact]
    public void Deskew_LeavesStraightPageUntouched()
    {
        using var page = CreateTextPage(1200, 1600);
        using var copy = page.Clone();

        double applied = ScanImageCleanup.Deskew(page);

        applied.Should().Be(0);
        PixelsEqual(page, copy).Should().BeTrue();
    }

    [Fact]
    public void Deskew_IgnoresPagesWithoutLines()
    {
        // Fotoğraf benzeri içerik: belirgin bir satır yönü yok.
        var random = new Random(5);
        using var image = new Image<Rgb24>(900, 1200);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                Span<Rgb24> row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    byte v = (byte)random.Next(0, 256);
                    row[x] = new Rgb24(v, v, v);
                }
            }
        });

        ScanImageCleanup.Deskew(image).Should().Be(0);
    }

    [Fact]
    public void CropDarkBorders_RemovesScannerBedAroundThePage()
    {
        // Koyu zemin üzerinde, tarama alanından küçük bir sayfa (ör. A5 belge, A4 tarama alanı).
        using var page = CreateTextPage(800, 1100);
        using var scan = new Image<Rgb24>(1300, 1700, Bed);
        Paste(scan, page, 210, 260);

        bool cropped = ScanImageCleanup.CropDarkBorders(scan);

        cropped.Should().BeTrue();
        scan.Width.Should().BeInRange(790, 812);
        scan.Height.Should().BeInRange(1090, 1112);
    }

    [Fact]
    public void CropDarkBorders_DoesNotTouchWhiteMargins()
    {
        // Beyaz kenar boşluklarını kırpmak sayfa boyutunu bozar; yalnızca koyu zemin kırpılmalı.
        using var page = CreateTextPage(1200, 1600);

        bool cropped = ScanImageCleanup.CropDarkBorders(page);

        cropped.Should().BeFalse();
        page.Width.Should().Be(1200);
        page.Height.Should().Be(1600);
    }

    [Fact]
    public void CropDarkBorders_DoesNotTouchDarkPages()
    {
        using var dark = new Image<Rgb24>(1000, 1400, Bed);

        ScanImageCleanup.CropDarkBorders(dark).Should().BeFalse();
        dark.Width.Should().Be(1000);
    }

    [Fact]
    public void DeskewThenCrop_RecoversASkewedPageOnTheScannerBed()
    {
        using var page = CreateTextPage(800, 1100);
        using var scan = new Image<Rgb24>(1300, 1700, Bed);
        Paste(scan, page, 250, 300);
        using var skewed = Rotate(scan, 3.0, Bed);

        ScanImageCleanup.Deskew(skewed);
        bool cropped = ScanImageCleanup.CropDarkBorders(skewed);

        cropped.Should().BeTrue();
        skewed.Width.Should().BeInRange(780, 840);
        skewed.Height.Should().BeInRange(1080, 1140);
    }

    /// <summary>Metin satırlarını andıran, kelimelere bölünmüş koyu çizgilerden oluşan sayfa.</summary>
    private static Image<Rgb24> CreateTextPage(int width, int height)
    {
        var image = new Image<Rgb24>(width, height, Paper);
        var random = new Random(42);

        image.ProcessPixelRows(rows =>
        {
            int margin = width / 9;
            for (int top = margin; top < height - margin; top += 26)
            {
                int x = margin;
                int lineEnd = width - margin - random.Next(0, width / 5);

                while (x < lineEnd)
                {
                    int word = random.Next(30, 110);
                    int end = Math.Min(lineEnd, x + word);

                    for (int y = top; y < top + 9; y++)
                        rows.GetRowSpan(y).Slice(x, end - x).Fill(Ink);

                    x = end + 12;
                }
            }
        });

        return image;
    }

    /// <summary>Saat yönünde (pozitif) döndürür; açıkta kalan köşeleri verilen renkle doldurur.</summary>
    private static Image<Rgb24> Rotate(Image<Rgb24> source, double degrees, Rgb24 fill)
    {
        using var rgba = source.CloneAs<Rgba32>();
        rgba.Mutate(c => c.Rotate((float)degrees, KnownResamplers.Bicubic).BackgroundColor(Color.FromRgb(fill.R, fill.G, fill.B)));
        return rgba.CloneAs<Rgb24>();
    }

    private static void Paste(Image<Rgb24> target, Image<Rgb24> source, int left, int top)
    {
        target.ProcessPixelRows(source, (to, from) =>
        {
            for (int y = 0; y < from.Height; y++)
                from.GetRowSpan(y).CopyTo(to.GetRowSpan(top + y).Slice(left));
        });
    }

    private static bool PixelsEqual(Image<Rgb24> first, Image<Rgb24> second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
            return false;

        bool equal = true;
        first.ProcessPixelRows(second, (a, b) =>
        {
            for (int y = 0; y < a.Height && equal; y++)
                equal = a.GetRowSpan(y).SequenceEqual(b.GetRowSpan(y));
        });
        return equal;
    }
}
