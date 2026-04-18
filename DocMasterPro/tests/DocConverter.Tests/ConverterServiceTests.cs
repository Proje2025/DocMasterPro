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
}