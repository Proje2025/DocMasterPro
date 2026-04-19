using DocConverter.Helpers;
using FluentAssertions;
using Xunit;

namespace DocConverter.Tests;

public class PathValidatorTests
{
    [Theory]
    [InlineData(".pdf", true)]
    [InlineData(".jpg", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".png", true)]
    [InlineData(".bmp", true)]
    [InlineData(".gif", true)]
    [InlineData(".tiff", true)]
    [InlineData(".tif", true)]
    [InlineData(".webp", true)]
    [InlineData(".docx", true)]
    [InlineData(".doc", true)]
    [InlineData(".xlsx", true)]
    [InlineData(".xls", true)]
    [InlineData(".pptx", true)]
    [InlineData(".ppt", true)]
    [InlineData(".txt", true)]
    [InlineData(".rtf", true)]
    [InlineData(".html", true)]
    [InlineData(".htm", true)]
    [InlineData(".exe", false)]
    [InlineData(".bat", false)]
    [InlineData(".cmd", false)]
    [InlineData(".ps1", false)]
    [InlineData("", false)]
    public void IsSupportedExtension_ReturnsExpectedResult(string extension, bool expected)
    {
        var result = PathValidator.IsSupportedExtension(extension);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(".PDF", true)]
    [InlineData(".JPG", true)]
    [InlineData(".DocX", true)]
    public void IsSupportedExtension_CaseInsensitive(string extension, bool expected)
    {
        var result = PathValidator.IsSupportedExtension(extension);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("C:\\Users\\Test\\Documents\\file.pdf", true)]
    [InlineData("D:\\Projects\\report.jpg", true)]
    [InlineData("..\\relative\\path\\file.pdf", false)]
    [InlineData("..\\..\\etc\\passwd", false)]
    public void IsPathSafe_ReturnsExpectedResult(string path, bool expected)
    {
        var result = PathValidator.IsPathSafe(path);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsPathSafe_NullOrEmpty_ReturnsFalse(string? path, bool expected)
    {
        var result = PathValidator.IsPathSafe(path!);
        result.Should().Be(expected);
    }

    [Fact]
    public void TryResolveExistingPdfPath_ReturnsFullPathForExistingPdf()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"DocMasterProPathTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = Path.Combine(tempDir, "sample.PDF");

        try
        {
            File.WriteAllText(pdfPath, "placeholder");

            var result = PathValidator.TryResolveExistingPdfPath(pdfPath, out string fullPath);

            result.Should().BeTrue();
            fullPath.Should().Be(Path.GetFullPath(pdfPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing.pdf")]
    [InlineData("sample.txt")]
    public void TryResolveExistingPdfPath_RejectsInvalidPdfPath(string path)
    {
        var result = PathValidator.TryResolveExistingPdfPath(path, out string fullPath);

        result.Should().BeFalse();
        fullPath.Should().BeEmpty();
    }

    public static TheoryData<string, int, List<(int From, int To)>> ValidPageRanges => new()
    {
        { "1", 10, new List<(int From, int To)> { (1, 1) } },
        { "1-3", 10, new List<(int From, int To)> { (1, 3) } },
        { "1,3,5", 10, new List<(int From, int To)> { (1, 1), (3, 3), (5, 5) } },
        { "1-3,5-7", 10, new List<(int From, int To)> { (1, 3), (5, 7) } },
        { "1-15", 10, new List<(int From, int To)> { (1, 10) } }
    };

    [Theory]
    [MemberData(nameof(ValidPageRanges))]
    public void ValidatePageRanges_ReturnsExpectedResult(
        string input,
        int maxPage,
        List<(int From, int To)> expected)
    {
        var result = PathValidator.ValidatePageRanges(input, maxPage);

        result.Should().Equal(expected);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("invalid", 10)]
    [InlineData("1-0", 10)]
    [InlineData("0-5", 10)]
    [InlineData("11-15", 10)]
    public void ValidatePageRanges_InvalidInput_ReturnsEmpty(string input, int maxPage)
    {
        var result = PathValidator.ValidatePageRanges(input, maxPage);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1572864, "1.50 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(1610612736, "1.50 GB")]
    public void FormatFileSize_ReturnsHumanReadableFormat(long bytes, string expected)
    {
        var result = PathValidator.FormatFileSize(bytes);
        result.Should().Be(expected);
    }
}
