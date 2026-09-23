using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Services;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit;

namespace DocConverter.Tests;

public class LocalVerificationTests
{
    [Fact]
    public async Task RunFullLocalPipeline_GeneratesRealFilesAndVerifiesResults()
    {
        string outputFolder = @"D:\Proje\Proje Versiyonlari\Dosaya Dönüştürme\Dosaya Dönüştürme\Test_Ciktilari";
        Directory.CreateDirectory(outputFolder);

        string samplePdfPath = Path.Combine(outputFolder, "ornek_test_belgesi.pdf");
        string pngOutputDir = Path.Combine(outputFolder, "PNG_Ciktilari");
        string jpgOutputDir = Path.Combine(outputFolder, "JPG_Ciktilari");
        string cleanedPdfPath = Path.Combine(outputFolder, "bos_sayfalari_temizlenmis.pdf");

        Directory.CreateDirectory(pngOutputDir);
        Directory.CreateDirectory(jpgOutputDir);

        // 1. Örnek 3 sayfalık bir PDF oluştur (Sayfa 1: Renkli/Metin, Sayfa 2: Tamamen Boş, Sayfa 3: Grafik)
        using (var doc = new PdfDocument())
        {
            // Sayfa 1: Başlık ve Metin
            var page1 = doc.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page1))
            {
                gfx.DrawRectangle(XBrushes.DarkSlateBlue, 40, 40, 500, 60);
                gfx.DrawRectangle(XBrushes.WhiteSmoke, 40, 110, 500, 400);
                gfx.DrawRectangle(XBrushes.SteelBlue, 60, 130, 200, 80);
                gfx.DrawRectangle(XBrushes.Goldenrod, 280, 130, 240, 80);
            }

            // Sayfa 2: Tamamen Boş Sayfa
            doc.AddPage();

            // Sayfa 3: Grafik ve Çizimler
            var page3 = doc.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page3))
            {
                gfx.DrawRectangle(XBrushes.ForestGreen, 40, 40, 500, 60);
                gfx.DrawEllipse(XBrushes.Crimson, 100, 150, 120, 120);
                gfx.DrawRectangle(XBrushes.Teal, 260, 150, 200, 120);
            }

            doc.Save(samplePdfPath);
        }

        File.Exists(samplePdfPath).Should().BeTrue("Örnek PDF dosyası oluşturulmalıdır.");

        // 2. ConverterService ile PNG'ye Dönüştürme (PDFium Engine)
        var converter = new ConverterService();
        await converter.ConvertPdfToImagesAsync(samplePdfPath, pngOutputDir, "png", CancellationToken.None);

        string png1 = Path.Combine(pngOutputDir, "ornek_test_belgesi_sayfa1.png");
        string png2 = Path.Combine(pngOutputDir, "ornek_test_belgesi_sayfa2.png");
        string png3 = Path.Combine(pngOutputDir, "ornek_test_belgesi_sayfa3.png");

        File.Exists(png1).Should().BeTrue();
        File.Exists(png2).Should().BeTrue();
        File.Exists(png3).Should().BeTrue();

        new FileInfo(png1).Length.Should().BeGreaterThan(1000);
        new FileInfo(png2).Length.Should().BeGreaterThan(100);
        new FileInfo(png3).Length.Should().BeGreaterThan(1000);

        // 3. ConverterService ile JPG'ye Dönüştürme (PDFium Engine)
        await converter.ConvertPdfToImagesAsync(samplePdfPath, jpgOutputDir, "jpg", CancellationToken.None);

        string jpg1 = Path.Combine(jpgOutputDir, "ornek_test_belgesi_sayfa1.jpg");
        string jpg2 = Path.Combine(jpgOutputDir, "ornek_test_belgesi_sayfa2.jpg");
        string jpg3 = Path.Combine(jpgOutputDir, "ornek_test_belgesi_sayfa3.jpg");

        File.Exists(jpg1).Should().BeTrue();
        File.Exists(jpg2).Should().BeTrue();
        File.Exists(jpg3).Should().BeTrue();

        // 4. BlankPageDetector ile Boş Sayfa Tespiti ve Temizleme (PDFium Engine)
        var blankDetector = new BlankPageDetector();
        var result = await blankDetector.RemoveBlankPagesFromPdfAsync(samplePdfPath, cleanedPdfPath);

        result.TotalOriginalPages.Should().Be(3);
        result.RemovedPages.Should().Be(1, "2. sayfa boş olduğu için tespit edilip silinmeli.");
        File.Exists(cleanedPdfPath).Should().BeTrue();

        using (var cleanedDoc = PdfSharp.Pdf.IO.PdfReader.Open(cleanedPdfPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            cleanedDoc.PageCount.Should().Be(2, "Boş sayfa silindikten sonra 2 sayfa kalmalıdır.");
        }
    }
}
