using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace DocConverter.Tests;

public class PdfStudioViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"StudioViewer_{Guid.NewGuid():N}");

    public PdfStudioViewerTests()
    {
        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.FontResolver = new WindowsFontResolver();

        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MeasurePage_BitmapMatchesPhysicalPixelsOfTheDisplay(double displayScale)
    {
        // A4, %100: Windows ölçeği ne olursa olsun bitmap ekran pikseliyle birebir olmalı (bulanıklık olmamalı).
        var (pixelWidth, pixelHeight, viewWidth, viewHeight) = PdfViewerService.MeasurePage(595, 842, 100, displayScale);

        (viewWidth * displayScale).Should().BeApproximately(pixelWidth, 0.01);
        (viewHeight * displayScale).Should().BeApproximately(pixelHeight, 0.01);
        viewWidth.Should().BeApproximately(595 * 96d / 72d, 1.0);
    }

    [Fact]
    public void MeasurePage_CapsBitmapSizeAtExtremeZoom()
    {
        var (pixelWidth, pixelHeight, viewWidth, _) = PdfViewerService.MeasurePage(595, 842, 800, 2.0);

        ((long)pixelWidth * pixelHeight).Should().BeLessThanOrEqualTo(PdfViewerService.MaxRenderPixels + 10_000);
        viewWidth.Should().BeApproximately(595 * 8 * 96d / 72d, 1.0, "ekrandaki boyut küçülmez, yalnızca bitmap sınırlanır");
    }

    [Fact]
    public async Task RenderPageAsync_UsesDisplayScaleForSharpness()
    {
        string pdf = CreatePdf("scale.pdf", 1);
        using var session = new PdfViewerService().OpenSession(pdf);

        var normal = await session.RenderPageAsync(0, 100, 1.0);
        var highDpi = await session.RenderPageAsync(0, 100, 1.5);

        highDpi.Image.PixelWidth.Should().BeCloseTo((int)(normal.Image.PixelWidth * 1.5), 2);
        highDpi.ViewWidth.Should().BeApproximately(normal.ViewWidth, 1.0);
        // Bitmap'in doğal boyutu (DIP) ekrandaki boyuta eşit olmalı.
        highDpi.Image.Width.Should().BeApproximately(highDpi.ViewWidth, 0.5);
    }

    [Fact]
    public async Task ZoomChange_KeepsOldImageUntilSharpOneIsReady()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("zoom.pdf", 3))).Should().BeTrue();
        var page = vm.Pages[0];
        int widthAt100 = page.Image!.PixelWidth;
        page.IsImageExact.Should().BeTrue();

        vm.ZoomPercent = 200;

        // Yakınlaştırma anında sayfa boşalmaz: eski görüntü gerilerek gösterilir.
        page.Image.Should().NotBeNull();
        page.IsImageExact.Should().BeFalse();
        page.ViewWidth.Should().BeApproximately(595 * 2 * 96d / 72d, 1.0);

        await vm.EnsurePageWindowRenderedAsync(0);

        page.IsImageExact.Should().BeTrue();
        page.Image!.PixelWidth.Should().BeCloseTo(widthAt100 * 2, 2);
    }

    [Fact]
    public async Task RapidZoomChanges_EndWithSharpPageAtFinalZoom()
    {
        // Ctrl + tekerlek: art arda gelen istekler birbirinin sonucunu çöpe atmamalı.
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("rapid.pdf", 2))).Should().BeTrue();

        for (int i = 0; i < 12; i++)
        {
            vm.ZoomBy(1.1);
            await Task.Delay(15);
        }

        double finalZoom = vm.ZoomPercent;
        var page = vm.Pages[0];
        for (int i = 0; i < 100 && !page.IsImageExact; i++)
            await Task.Delay(50);

        page.IsImageExact.Should().BeTrue("son yakınlaştırma değeri için net görüntü üretilmeli");
        page.Image!.PixelWidth.Should().BeCloseTo((int)Math.Round(595 * finalZoom / 100 * 96d / 72d), 2);
    }

    [Fact]
    public async Task FitWidth_UsesViewportWidthInsteadOfFixedHundredPercent()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("fit.pdf", 2))).Should().BeTrue();

        vm.SetViewportSize(1000, 700);
        vm.FitWidthCommand.Execute(null);

        vm.ZoomMode.Should().Be(PdfZoomMode.FitWidth);
        double pageWidthAtFit = 595 * vm.ZoomPercent / 100 * 96d / 72d;
        pageWidthAtFit.Should().BeInRange(900, 1000, "sayfa görünür alanın genişliğini doldurmalı");

        vm.FitPageCommand.Execute(null);
        vm.ZoomMode.Should().Be(PdfZoomMode.FitPage);
        (842 * vm.ZoomPercent / 100 * 96d / 72d).Should().BeLessThanOrEqualTo(700);

        // Pencere küçülünce sığdırma yeniden hesaplanır.
        double before = vm.ZoomPercent;
        vm.SetViewportSize(1000, 500);
        vm.ZoomPercent.Should().BeLessThan(before);
    }

    [Fact]
    public async Task ZoomCommands_StepThroughStopsAndRespectLimits()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("steps.pdf", 1))).Should().BeTrue();

        vm.ZoomPercent = 100;
        vm.ZoomInCommand.Execute(null);
        vm.ZoomPercent.Should().Be(110);
        vm.ZoomMode.Should().Be(PdfZoomMode.Custom);

        vm.ZoomOutCommand.Execute(null);
        vm.ZoomOutCommand.Execute(null);
        vm.ZoomPercent.Should().Be(90);

        vm.ZoomPercent = PdfStudioViewModel.MaxZoomPercent;
        vm.ZoomInCommand.CanExecute(null).Should().BeFalse();

        vm.ZoomBy(100);
        vm.ZoomPercent.Should().Be(PdfStudioViewModel.MaxZoomPercent);
    }

    [Theory]
    [InlineData("150", 150)]
    [InlineData("75%", 75)]
    [InlineData(" 125 % ", 125)]
    [InlineData("5000", PdfStudioViewModel.MaxZoomPercent)]
    [InlineData("Gerçek Boyut", 100)]
    public async Task ZoomText_AcceptsTypedValues(string typed, double expected)
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("text.pdf", 1))).Should().BeTrue();
        vm.ZoomPercent = 60;

        vm.ZoomText = typed;

        vm.ZoomPercent.Should().Be(expected);
        vm.ZoomText.Should().Be($"{expected:0}%");
    }

    [Fact]
    public async Task ZoomText_IgnoresGarbage()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("garbage.pdf", 1))).Should().BeTrue();
        vm.ZoomPercent = 80;

        vm.ZoomText = "abc";

        vm.ZoomPercent.Should().Be(80);
        vm.ZoomText.Should().Be("80%");
    }

    [Fact]
    public async Task MixedPageSizes_AreKnownBeforeRendering()
    {
        string pdf = Path.Combine(_dir, "mixed.pdf");
        using (var doc = new PdfDocument())
        {
            var portrait = doc.AddPage();
            portrait.Width = XUnit.FromPoint(595);
            portrait.Height = XUnit.FromPoint(842);
            var landscape = doc.AddPage();
            landscape.Width = XUnit.FromPoint(842);
            landscape.Height = XUnit.FromPoint(595);
            doc.Save(pdf);
        }

        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(pdf)).Should().BeTrue();

        // İkinci sayfa henüz çizilmeden de doğru oranda yer kaplamalı; aksi halde kaydırırken yerleşim zıplar.
        vm.Pages[1].ViewWidth.Should().BeGreaterThan(vm.Pages[1].ViewHeight);
        vm.Pages[0].ViewHeight.Should().BeGreaterThan(vm.Pages[0].ViewWidth);
    }

    [Fact]
    public async Task Thumbnails_AreRenderedOnDemand()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("thumbs.pdf", 4))).Should().BeTrue();
        vm.Pages.Should().OnlyContain(p => p.Thumbnail == null);

        vm.RequestThumbnail(vm.Pages[2]);
        for (int i = 0; i < 100 && vm.Pages[2].Thumbnail == null; i++)
            await Task.Delay(30);

        vm.Pages[2].Thumbnail.Should().NotBeNull();
        vm.Pages[2].Thumbnail!.PixelWidth.Should().BeInRange(100, 420);
        vm.Pages[0].Thumbnail.Should().BeNull("yalnızca görünür olan sayfaların küçük resmi üretilir");
    }

    [Fact]
    public async Task HighlightSelection_DoesNotChangeColourOfNextTextAnnotation()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("colour.pdf", 1))).Should().BeTrue();

        vm.SelectedTool = PdfStudioTool.Highlight;
        vm.AddAnnotationAt(0, 100, 100);
        vm.SelectedTool = PdfStudioTool.Text;
        vm.AddAnnotationAt(0, 100, 200);

        vm.Annotations[1].Color.Should().Be("#2563EB", "vurgunun sarısı metne bulaşmamalı");
    }

    [Fact]
    public async Task PageNavigation_KeepsPageNumberBoxInSync()
    {
        using var vm = CreateViewModel();
        (await vm.OpenPdfPathAsync(CreatePdf("nav.pdf", 5))).Should().BeTrue();
        vm.JumpToPageText.Should().Be("1");

        vm.SetCurrentPageFromView(3);
        vm.JumpToPageText.Should().Be("4");
        vm.PageCountLabel.Should().Be("/ 5");

        vm.JumpToPageText = "99";
        vm.GoToPageCommand.Execute(null);
        vm.CurrentPageIndex.Should().Be(4, "aralık dışı numara son sayfaya gider");
        vm.JumpToPageText.Should().Be("5");
    }

    private PdfStudioViewModel CreateViewModel()
    {
        return new PdfStudioViewModel(
            new PdfSessionService(Path.Combine(_dir, "sessions")),
            new PdfViewerService(),
            new PdfTextSearchService(),
            new PdfAnnotationService());
    }

    private string CreatePdf(string name, int pages)
    {
        string path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        var font = new XFont("Arial", 14);

        for (int i = 0; i < pages; i++)
        {
            // PDFsharp'ın varsayılan sayfa boyutu bölgeye göre A4 ya da Letter'dır; testler A4 varsayar.
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Sayfa {i + 1}", font, XBrushes.Black, new XPoint(72, 96));
        }

        doc.Save(path);
        return path;
    }
}
