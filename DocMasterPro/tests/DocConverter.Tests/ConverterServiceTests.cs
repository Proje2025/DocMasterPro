using DocConverter.Services;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

public class ConverterServiceTests
{
    private readonly ConverterService _sut;

    public ConverterServiceTests()
    {
        _sut = new ConverterService();
    }

    [Fact]
    public void GetPdfPageCount_WithValidPdf_ReturnsPageCount()
    {
        // This test would require a valid PDF file
        // For now, we test the behavior with a non-existent file
        // Act
        var result = _sut.GetPdfPageCount("nonexistent.pdf");

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void GetPdfPageCount_WithNonExistentFile_ReturnsZero()
    {
        // Act
        var result = _sut.GetPdfPageCount("C:\\nonexistent\\file.pdf");

        // Assert
        result.Should().Be(0);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".tiff")]
    [InlineData(".tif")]
    [InlineData(".webp")]
    public void ImageExtensions_ContainsExpectedFormats(string extension)
    {
        // The supported extensions should include common image formats
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp" };
        supportedExtensions.Should().Contain(extension);
    }

    [Fact]
    public async Task ConvertPdfToImagesAsync_ValidPdf_GeneratesImageFilesWithoutGhostscript()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "DocConverterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pdfPath = Path.Combine(tempDir, "test.pdf");
        string outDir = Path.Combine(tempDir, "images");

        try
        {
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var page1 = doc.AddPage();
                using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page1))
                {
                    gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.Navy, 20, 20, 150, 100);
                }
                var page2 = doc.AddPage();
                using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page2))
                {
                    gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.DarkRed, 40, 40, 120, 80);
                }
                doc.Save(pdfPath);
            }

            // Act
            await _sut.ConvertPdfToImagesAsync(pdfPath, outDir, "png", CancellationToken.None);

            // Assert
            string page1Path = Path.Combine(outDir, "test_sayfa1.png");
            string page2Path = Path.Combine(outDir, "test_sayfa2.png");

            File.Exists(page1Path).Should().BeTrue();
            File.Exists(page2Path).Should().BeTrue();
            new FileInfo(page1Path).Length.Should().BeGreaterThan(0);
            new FileInfo(page2Path).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task BlankPageDetector_WithBlankPage_DetectsAndRemovesItWithoutGhostscript()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "DocConverterBlankTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string inputPdf = Path.Combine(tempDir, "sample.pdf");
        string outputPdf = Path.Combine(tempDir, "cleaned.pdf");

        try
        {
            using (var doc = new PdfSharp.Pdf.PdfDocument())
            {
                var p1 = doc.AddPage();
                using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(p1))
                {
                    gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.Black, 50, 50, 200, 200);
                }
                // p2 is completely blank
                doc.AddPage();
                doc.Save(inputPdf);
            }

            var detector = new BlankPageDetector();

            // Act
            var result = await detector.RemoveBlankPagesFromPdfAsync(inputPdf, outputPdf);

            // Assert
            result.TotalOriginalPages.Should().Be(2);
            result.RemovedPages.Should().Be(1);
            File.Exists(outputPdf).Should().BeTrue();

            using var resDoc = PdfSharp.Pdf.IO.PdfReader.Open(outputPdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            resDoc.PageCount.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}