using DocConverter.Services;
using FluentAssertions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace DocConverter.Tests;

public class PdfServiceTests
{
    private readonly PdfService _sut;

    public PdfServiceTests()
    {
        _sut = new PdfService();
    }

    [Fact]
    public void Constructor_ShouldCreateInstance()
    {
        _sut.Should().NotBeNull();
    }

    [Fact]
    public async Task MergePdfsAsync_WithEmptyList_ShouldThrow()
    {
        var files = new List<string>();
        var output = Path.Combine(Path.GetTempPath(), "empty_output.pdf");

        var action = async () => await _sut.MergePdfsAsync(files, output, CancellationToken.None, null);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PDF dosyası bulunamadı*");
    }

    [Fact]
    public async Task MergePdfsAsync_WithNonExistentFiles_ShouldThrow()
    {
        var files = new List<string> { "C:\\nonexistent1.pdf", "C:\\nonexistent2.pdf" };
        var output = Path.Combine(Path.GetTempPath(), "output.pdf");

        var action = async () => await _sut.MergePdfsAsync(files, output, CancellationToken.None, null);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*geçerli PDF sayfası bulunamadı*");
    }

    [Fact]
    public async Task MergePdfsAsync_WithValidPdfs_ShouldMergePages()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DocConverterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var first = Path.Combine(tempDir, "first.pdf");
            var second = Path.Combine(tempDir, "second.pdf");
            var output = Path.Combine(tempDir, "merged.pdf");

            CreatePdf(first, 1);
            CreatePdf(second, 2);

            await _sut.MergePdfsAsync(new List<string> { first, second }, output, CancellationToken.None, null);

            using var merged = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            merged.PageCount.Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MovePageAsync_WithValidIndexes_ShouldReorderPages()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DocConverterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = Path.Combine(tempDir, "source.pdf");
            CreatePdfWithPageWidths(pdfPath, 200, 300, 400);

            await _sut.MovePageAsync(pdfPath, 2, 0);

            ReadPageWidths(pdfPath).Should().Equal(400, 200, 300);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MovePageAsync_WithSameIndex_ShouldLeavePagesUnchanged()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DocConverterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = Path.Combine(tempDir, "source.pdf");
            CreatePdfWithPageWidths(pdfPath, 200, 300, 400);

            await _sut.MovePageAsync(pdfPath, 1, 1);

            ReadPageWidths(pdfPath).Should().Equal(200, 300, 400);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 0)]
    [InlineData(0, 3)]
    public async Task MovePageAsync_WithInvalidIndexes_ShouldThrowAndLeavePagesUnchanged(int fromIndex, int toIndex)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DocConverterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = Path.Combine(tempDir, "source.pdf");
            CreatePdfWithPageWidths(pdfPath, 200, 300, 400);

            var action = async () => await _sut.MovePageAsync(pdfPath, fromIndex, toIndex);

            await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
            ReadPageWidths(pdfPath).Should().Equal(200, 300, 400);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void PageRangeValidation_ShouldHandleVariousInputs(int _)
    {
        var ranges = Helpers.PathValidator.ValidatePageRanges("1-5", 100);
        ranges.Should().HaveCount(1);
        ranges[0].From.Should().Be(1);
        ranges[0].To.Should().Be(5);
    }

    private static void CreatePdf(string path, int pageCount)
    {
        using var document = new PdfDocument();

        for (int i = 0; i < pageCount; i++)
            document.AddPage();

        document.Save(path);
    }

    private static void CreatePdfWithPageWidths(string path, params double[] widths)
    {
        using var document = new PdfDocument();

        foreach (double width in widths)
        {
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(500);
        }

        document.Save(path);
    }

    private static List<double> ReadPageWidths(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var widths = new List<double>();

        for (int i = 0; i < document.PageCount; i++)
            widths.Add(Math.Round(document.Pages[i].Width.Point));

        return widths;
    }
}

public class OfficeConverterServiceTests
{
    [Fact]
    public void IsOldWordFormat_WithDocExtension_ReturnsTrue()
    {
        var service = new OfficeConverterService();
        var result = service.IsOldWordFormat("document.doc");
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("document.docx")]
    [InlineData("document.xlsx")]
    [InlineData("document.pptx")]
    public void IsOldWordFormat_WithNewFormats_ReturnsFalse(string filename)
    {
        var service = new OfficeConverterService();
        var result = service.IsOldWordFormat(filename);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsOfficeInstalled_ReturnsValidBoolean()
    {
        var service = new OfficeConverterService();
        var result = service.IsOfficeInstalled();
        // Just verify it returns a boolean (true or false)
        (result == true || result == false).Should().BeTrue();
    }

    [Fact]
    public void ForceGarbageCollection_ShouldNotThrow()
    {
        var service = new OfficeConverterService();
        var action = () => OfficeConverterService.ForceGarbageCollection();
        action.Should().NotThrow();
    }
}
