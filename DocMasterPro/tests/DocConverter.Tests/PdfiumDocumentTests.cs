using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DocConverter.Helpers;
using DocConverter.Services;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace DocConverter.Tests;

/// <summary>
/// PDFium sarmalayıcısı: güncel motorun yüklendiği, sayfa boyutları, çizim, form alanları, notlar ve hatalar.
/// </summary>
public class PdfiumDocumentTests : IDisposable
{
    private readonly string _tempDir;

    public PdfiumDocumentTests()
    {
        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.FontResolver = new WindowsFontResolver();

        _tempDir = Path.Combine(Path.GetTempPath(), "DocMasterPro.Tests", "pdfium-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_UsesCurrentPdfiumBuild()
    {
        string source = CreatePdf("motor.pdf", (595, 842, 0));
        using var document = PdfiumDocument.Load(source);

        PdfiumNativeLoader.LoadedPath.Should().NotBeNullOrEmpty();
        IntPtr handle = NativeLibrary.Load(PdfiumNativeLoader.LoadedPath!);
        try
        {
            // FPDF_AddRef yalnızca 2018'deki eski motorda vardı; güncel motorda FPDF_GetPageSizeByIndexF bulunur.
            NativeLibrary.TryGetExport(handle, "FPDF_AddRef", out _).Should().BeFalse();
            NativeLibrary.TryGetExport(handle, "FPDF_GetPageSizeByIndexF", out _).Should().BeTrue();
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void GetPageSize_ReturnsPointsWithPageRotationApplied()
    {
        string source = CreatePdf("boyutlar.pdf", (595, 842, 0), (595, 842, 90), (792, 612, 0));

        using var document = PdfiumDocument.Load(source);

        document.PageCount.Should().Be(3);
        document.GetPageSize(0).Should().Be(new SizeF(595, 842));
        document.GetPageSize(1).Should().Be(new SizeF(842, 595));
        document.GetPageSize(2).Should().Be(new SizeF(792, 612));
    }

    [Fact]
    public void Render_DrawsPageContentOnOpaqueWhite()
    {
        string source = CreatePdf("cizim.pdf", (595, 842, 0));

        using var document = PdfiumDocument.Load(source);
        using Bitmap image = document.Render(0, 595, 842, 72, 72, PdfRenderOptions.Annotations);

        image.Width.Should().Be(595);
        image.HorizontalResolution.Should().BeApproximately(72, 0.01f);
        image.GetPixel(10, 10).ToArgb().Should().Be(Color.White.ToArgb());

        // CreatePdf her sayfaya (100, 100) noktasından başlayan 200x100'lük siyah bir dikdörtgen çizer.
        Color inside = image.GetPixel(200, 150);
        inside.A.Should().Be(255);
        inside.R.Should().BeLessThan(30);
    }

    [Fact]
    public void Render_DrawsFilledFormFieldsOnlyWithAnnotations()
    {
        using var document = PdfiumDocument.Load(TestData("form_dolu.pdf"));
        static int Px(double points) => (int)Math.Round(points * 100 / 72);
        SizeF size = document.GetPageSize(0);
        int width = Px(size.Width), height = Px(size.Height);

        // Metin alanı PDF'te x 72-372, y 700-730 noktasında (y aşağıdan yukarı); içinde "DOLDURULMUS DEGER" yazılı.
        var field = Rectangle.FromLTRB(Px(72), height - Px(730), Px(372), height - Px(700));

        int plain = CountDarkPixels(document, width, height, PdfRenderOptions.None, field);
        int shown = CountDarkPixels(document, width, height, PdfRenderOptions.Annotations, field);
        int printed = CountDarkPixels(document, width, height, PdfRenderOptions.Annotations | PdfRenderOptions.ForPrinting, field);

        shown.Should().BeGreaterThan(plain + 1000, "doldurulmuş form alanı boş görünmemeli");
        printed.Should().Be(shown);
    }

    [Fact]
    public void Render_DrawsMarkupAnnotationsOnlyWithAnnotations()
    {
        using var document = PdfiumDocument.Load(TestData("not_kirmizi_kare.pdf"));

        // Kırmızı kare notu PDF'te (100, 400)-(400, 600) noktasında; sayfa 72 DPI'da çizilir.
        using Bitmap without = document.Render(0, 595, 842, 72, 72, PdfRenderOptions.None);
        using Bitmap with = document.Render(0, 595, 842, 72, 72, PdfRenderOptions.Annotations);

        without.GetPixel(250, 342).ToArgb().Should().Be(Color.White.ToArgb());
        Color note = with.GetPixel(250, 342);
        note.R.Should().BeGreaterThan(200);
        note.G.Should().BeLessThan(60);
        note.B.Should().BeLessThan(60);
    }

    [Fact]
    public void Load_PasswordProtectedPdf_ThrowsClearMessage()
    {
        Action open = () => PdfiumDocument.Load(TestData("parolali.pdf")).Dispose();

        open.Should().Throw<PdfiumException>()
            .Where(ex => ex.ErrorCode == 4 && ex.Message.Contains("parola"));
    }

    [Fact]
    public void Load_FileThatIsNotPdf_ThrowsFormatError()
    {
        string path = Path.Combine(_tempDir, "bozuk.pdf");
        File.WriteAllText(path, "%PDF-1.7\nbu bir PDF değil");

        Action open = () => PdfiumDocument.Load(path).Dispose();

        open.Should().Throw<PdfiumException>().Where(ex => ex.ErrorCode == 3);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        Action open = () => PdfiumDocument.Load(Path.Combine(_tempDir, "yok.pdf")).Dispose();

        open.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_TurkishFileName_Opens()
    {
        string source = CreatePdf("Çalışma Öğütücü şğü İı.pdf", (595, 842, 0), (595, 842, 0));

        using var document = PdfiumDocument.Load(source);

        document.PageCount.Should().Be(2);
    }

    [Fact]
    public void Dispose_ReleasesFileAndRejectsFurtherUse()
    {
        string source = CreatePdf("kapat.pdf", (595, 842, 0));
        var document = PdfiumDocument.Load(source);

        // Belge açıkken dosya başka okuyuculara (örneğin metin arama) açık kalır.
        using (var reader = File.OpenRead(source))
            reader.ReadByte().Should().Be('%');

        document.Dispose();
        document.Dispose();
        File.Delete(source);

        File.Exists(source).Should().BeFalse();
        Action use = () => document.GetPageSize(0);
        use.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Render_RejectsInvalidPageAndSize()
    {
        string source = CreatePdf("sinir.pdf", (595, 842, 0));
        using var document = PdfiumDocument.Load(source);

        FluentActions.Invoking(() => document.Render(1, 10, 10, 72, 72, PdfRenderOptions.None))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => document.Render(0, 0, 10, 72, 72, PdfRenderOptions.None))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => document.GetPageSize(-1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Render_FromManyThreads_MatchesSequentialOutput()
    {
        string first = CreatePdf("paralel-1.pdf", (595, 842, 0), (842, 595, 0));
        string second = CreatePdf("paralel-2.pdf", (595, 842, 90));

        var expected = new Dictionary<(string, int), string>();
        foreach (string path in new[] { first, second })
        {
            using var document = PdfiumDocument.Load(path);
            for (int page = 0; page < document.PageCount; page++)
                expected[(path, page)] = RenderHash(document, page);
        }

        using var shared = PdfiumDocument.Load(first);
        var mismatches = new ConcurrentBag<string>();
        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            if (i % 2 == 0)
            {
                int page = i % shared.PageCount;
                if (RenderHash(shared, page) != expected[(first, page)])
                    mismatches.Add($"shared {page}");
            }
            else
            {
                using var own = PdfiumDocument.Load(second);
                if (RenderHash(own, 0) != expected[(second, 0)])
                    mismatches.Add("own 0");
            }
        });

        mismatches.Should().BeEmpty();
    }

    private static string TestData(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    private string CreatePdf(string fileName, params (double Width, double Height, int Rotate)[] pages)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var document = new PdfDocument();

        foreach (var (width, height, rotate) in pages)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);

            using (var gfx = XGraphics.FromPdfPage(page))
            {
                gfx.DrawRectangle(XBrushes.Black, 100, 100, 200, 100);
                gfx.DrawString("PDFium", new XFont("Arial", 14), XBrushes.Black, new XPoint(100, 250));
            }

            page.Rotate = rotate;
        }

        document.Save(path);
        return path;
    }

    private static string RenderHash(PdfiumDocument document, int page)
    {
        SizeF size = document.GetPageSize(page);
        using Bitmap image = document.Render(
            page, (int)size.Width, (int)size.Height, 72, 72, PdfRenderOptions.Annotations);
        return Convert.ToHexString(SHA256.HashData(ReadPixels(image)));
    }

    private static int CountDarkPixels(PdfiumDocument document, int width, int height, PdfRenderOptions options, Rectangle area)
    {
        using Bitmap image = document.Render(0, width, height, 100, 100, options);
        byte[] pixels = ReadPixels(image);
        int count = 0;

        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int i = (y * width + x) * 4;
                if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200)
                    count++;
            }
        }

        return count;
    }

    private static byte[] ReadPixels(Bitmap image)
    {
        var data = image.LockBits(
            new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[image.Width * image.Height * 4];
            for (int y = 0; y < image.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * image.Width * 4, image.Width * 4);

            return pixels;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }
}
