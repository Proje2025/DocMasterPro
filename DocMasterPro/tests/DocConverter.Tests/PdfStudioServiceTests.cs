using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace DocConverter.Tests;

public class PdfStudioServiceTests
{
    public PdfStudioServiceTests()
    {
        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.FontResolver = new WindowsFontResolver();
    }

    [Fact]
    public async Task OpenPdfSessionAsync_CreatesWorkingCopyWithoutChangingSource()
    {
        string tempDir = CreateTempDir();
        try
        {
            string source = Path.Combine(tempDir, "source.pdf");
            CreateTextPdf(source, "Original document");
            byte[] originalBytes = await File.ReadAllBytesAsync(source);

            var service = new PdfSessionService(Path.Combine(tempDir, "sessions"));
            var session = await service.OpenPdfSessionAsync(source);

            session.OriginalPath.Should().Be(source);
            session.WorkingPath.Should().NotBe(source);
            File.Exists(session.WorkingPath).Should().BeTrue();
            session.PageCount.Should().Be(1);
            (await File.ReadAllBytesAsync(source)).Should().Equal(originalBytes);

            service.CleanupSession(session);
            Directory.Exists(session.SessionFolder).Should().BeFalse();
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task AtomicSaveAsync_ReplacesTargetOnlyAfterTempOutputExists()
    {
        string tempDir = CreateTempDir();
        try
        {
            string source = Path.Combine(tempDir, "new.pdf");
            string target = Path.Combine(tempDir, "target.pdf");
            CreateTextPdf(source, "New content");
            CreateTextPdf(target, "Old content");

            var service = new PdfSessionService(Path.Combine(tempDir, "sessions"));
            await service.AtomicSaveAsync(source, target);

            using var pdf = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
            new FileInfo(target).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task SearchAsync_FindsTextLayerWords()
    {
        string tempDir = CreateTempDir();
        try
        {
            string source = Path.Combine(tempDir, "searchable.pdf");
            CreateTextPdf(source, "Needle in a PDF document");

            var service = new PdfTextSearchService();
            var results = await service.SearchAsync(source, "Needle");

            results.Should().ContainSingle();
            results[0].PageIndex.Should().Be(0);
            results[0].Text.Should().Contain("Needle");
            results[0].Width.Should().BeGreaterThan(0);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task RenderPageAsync_UsesPdfiumAndReturnsBitmap()
    {
        string tempDir = CreateTempDir();
        try
        {
            string source = Path.Combine(tempDir, "render.pdf");
            CreateTextPdf(source, "Renderable page");

            var service = new PdfViewerService();
            var rendered = await service.RenderPageAsync(source, 0, 100);

            rendered.Image.Should().NotBeNull();
            rendered.Width.Should().BeGreaterThan(0);
            rendered.Height.Should().BeGreaterThan(0);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task PdfStudioViewModel_OpenLargePdf_RendersOnlyNearbyPages()
    {
        string tempDir = CreateTempDir();
        try
        {
            string source = Path.Combine(tempDir, "large.pdf");
            CreateTextPdf(source, "Large document", pageCount: 50);

            using var viewModel = new PdfStudioViewModel(
                new PdfSessionService(Path.Combine(tempDir, "sessions")),
                new PdfViewerService(),
                new PdfTextSearchService(),
                new PdfAnnotationService());

            bool opened = await viewModel.OpenPdfPathAsync(source);
            opened.Should().BeTrue();
            viewModel.Pages.Should().HaveCount(50);
            viewModel.Pages.Count(page => page.Image != null).Should().BeLessThanOrEqualTo(5);

            viewModel.SetCurrentPageFromView(25);
            await viewModel.EnsurePageWindowRenderedAsync(25);

            viewModel.CurrentPageIndex.Should().Be(25);
            viewModel.Pages.Count(page => page.Image != null).Should().BeLessThanOrEqualTo(8);
            viewModel.Pages[25].Image.Should().NotBeNull();
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task ApplyAnnotationsAsync_FlattensOverlayIntoOutputPdf()
    {
        string tempDir = CreateTempDir();
        try
        {
            string source = Path.Combine(tempDir, "source.pdf");
            string output = Path.Combine(tempDir, "output.pdf");
            CreateTextPdf(source, "Base page");

            var annotations = new[]
            {
                new PdfAnnotationItem
                {
                    PageIndex = 0,
                    Type = PdfAnnotationType.Text,
                    Text = "Overlay text",
                    X = 72,
                    Y = 120,
                    Width = 220,
                    Height = 40,
                    Color = "#2563EB",
                    FontSize = 14,
                    Opacity = 1
                },
                new PdfAnnotationItem
                {
                    PageIndex = 0,
                    Type = PdfAnnotationType.Highlight,
                    X = 72,
                    Y = 160,
                    Width = 180,
                    Height = 22,
                    Color = "#F8D84A",
                    Opacity = 0.45
                }
            };

            var service = new PdfAnnotationService();
            await service.ApplyAnnotationsAsync(source, output, annotations);

            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(new FileInfo(source).Length);

            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"DocMasterProStudioTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void DeleteTempDir(string tempDir)
    {
        if (!Directory.Exists(tempDir))
            return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
    }

    private static void CreateTextPdf(string path, string text, int pageCount = 1)
    {
        using var document = new PdfDocument();

        for (int index = 0; index < pageCount; index++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);

            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{text} {index + 1}", new XFont("Arial", 14), XBrushes.Black, new XPoint(72, 100));
        }

        document.Save(path);
    }
}
