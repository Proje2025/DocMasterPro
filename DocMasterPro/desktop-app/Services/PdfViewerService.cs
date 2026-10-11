using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Drawing.Image;
using DocConverter.Helpers;
using DocConverter.Models;
using PdfiumViewer.Core;
using PdfiumViewer.Enums;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Size = System.Windows.Size;

namespace DocConverter.Services;

public class PdfViewerService
{
    public const double ScreenDpi = 96d;
    public const double PdfPointDpi = 72d;

    /// <summary>
    /// Tek sayfa için üretilecek en büyük bitmap (piksel). 16 MP ≈ 64 MB; A4 sayfada ~400 DPI eder.
    /// Bunun üzerindeki yakınlaştırmalarda sayfa bu çözünürlükte çizilip ekranda gerilir.
    /// </summary>
    public const int MaxRenderPixels = 16_000_000;

    private const double PrintDpi = 300d;
    private const int MaxPrintPixels = 40_000_000;

    public async Task<PdfRenderedPage> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        double zoomPercent,
        CancellationToken cancellationToken = default)
    {
        using var session = OpenSession(pdfPath);
        return await session.RenderPageAsync(pageIndex, zoomPercent, cancellationToken);
    }

    public PdfViewerSession OpenSession(string pdfPath)
    {
        PdfiumNativeLoader.EnsureLoaded();
        return new PdfViewerSession(PdfDocument.Load(pdfPath));
    }

    public int GetPageCount(string pdfPath)
    {
        using var session = OpenSession(pdfPath);
        return session.PageCount;
    }

    public SizeF GetPageSize(string pdfPath, int pageIndex)
    {
        using var session = OpenSession(pdfPath);
        return session.GetPageSize(pageIndex);
    }

    public IReadOnlyList<SizeF> GetPageSizes(string pdfPath)
    {
        using var session = OpenSession(pdfPath);
        return session.GetPageSizes();
    }

    /// <summary>
    /// Bir sayfanın ekranda kaplayacağı boyutu (DIP) ve bunun için çizilecek bitmap boyutunu (piksel) hesaplar.
    /// Bitmap, ekranın fiziksel piksellerine birebir denk gelecek şekilde <paramref name="pixelScale"/>
    /// (Windows ölçek oranı: %125 → 1.25) ile büyütülür; aksi halde yüksek DPI ekranlarda sayfa bulanık görünür.
    /// </summary>
    public static (int PixelWidth, int PixelHeight, double ViewWidth, double ViewHeight) MeasurePage(
        double sourceWidth,
        double sourceHeight,
        double zoomPercent,
        double pixelScale)
    {
        double zoom = Math.Max(0.05, zoomPercent / 100d);
        double scale = Math.Clamp(pixelScale, 0.5, 4.0);

        // Ekran boyutu tam fiziksel piksele yuvarlanır ki bitmap gerilmeden çizilsin.
        double viewWidth = Math.Max(1, Math.Round(sourceWidth * zoom * ScreenDpi / PdfPointDpi * scale)) / scale;
        double viewHeight = Math.Max(1, Math.Round(sourceHeight * zoom * ScreenDpi / PdfPointDpi * scale)) / scale;

        double pixelWidth = viewWidth * scale;
        double pixelHeight = viewHeight * scale;
        double pixels = pixelWidth * pixelHeight;

        if (pixels > MaxRenderPixels)
        {
            double shrink = Math.Sqrt(MaxRenderPixels / pixels);
            pixelWidth *= shrink;
            pixelHeight *= shrink;
        }

        return (
            Math.Max(1, (int)Math.Round(pixelWidth)),
            Math.Max(1, (int)Math.Round(pixelHeight)),
            viewWidth,
            viewHeight);
    }

    /// <summary>
    /// PDF'i yazıcının çözünürlüğüne uygun (300 DPI) olarak, sayfa sayfa üreterek yazdırır.
    /// Kullanıcı iletişim kutusunu iptal ederse false döner.
    /// </summary>
    public bool Print(string pdfPath)
    {
        PdfiumNativeLoader.EnsureLoaded();

        using var document = PdfDocument.Load(pdfPath);
        if (document.PageCount == 0)
            return false;

        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = (uint)document.PageCount
        };

        if (dialog.ShowDialog() != true)
            return false;

        int first = 0;
        int last = document.PageCount - 1;
        if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
        {
            first = Math.Clamp(dialog.PageRange.PageFrom - 1, 0, last);
            last = Math.Clamp(dialog.PageRange.PageTo - 1, first, last);
        }

        var paper = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        var paginator = new PdfPrintPaginator(document, paper, first, last);
        dialog.PrintDocument(paginator, "DocMaster Pro PDF Studio");
        return true;
    }

    /// <summary>
    /// Sayfaları istenildikçe üretir; bütün belgeyi bellekte bitmap olarak tutmaz.
    /// </summary>
    private sealed class PdfPrintPaginator : DocumentPaginator
    {
        private readonly PdfDocument _document;
        private readonly int _first;
        private readonly int _last;
        private Size _paper;

        public PdfPrintPaginator(PdfDocument document, Size paper, int first, int last)
        {
            _document = document;
            _paper = paper;
            _first = first;
            _last = last;
        }

        public override bool IsPageCountValid => true;

        public override int PageCount => _last - _first + 1;

        public override Size PageSize
        {
            get => _paper;
            set => _paper = value;
        }

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            int pageIndex = _first + pageNumber;
            SizeF size = PdfViewerService.GetPageSize(_document, pageIndex);

            double pageWidth = size.Width * ScreenDpi / PdfPointDpi;
            double pageHeight = size.Height * ScreenDpi / PdfPointDpi;

            // Yatay sayfa dikey kağıda (ya da tersi) gidiyorsa döndürerek kağıdı doldur.
            bool rotate = (pageWidth > pageHeight) != (_paper.Width > _paper.Height)
                          && Math.Abs(pageWidth - pageHeight) > 1;
            double fitWidth = rotate ? pageHeight : pageWidth;
            double fitHeight = rotate ? pageWidth : pageHeight;

            // Kağıda sığmıyorsa küçült; küçük sayfaları büyütme.
            double scale = Math.Min(1d, Math.Min(_paper.Width / fitWidth, _paper.Height / fitHeight));

            int pixelWidth = Math.Max(1, (int)Math.Round(pageWidth * scale / ScreenDpi * PrintDpi));
            int pixelHeight = Math.Max(1, (int)Math.Round(pageHeight * scale / ScreenDpi * PrintDpi));
            double pixels = (double)pixelWidth * pixelHeight;
            if (pixels > MaxPrintPixels)
            {
                double shrink = Math.Sqrt(MaxPrintPixels / pixels);
                pixelWidth = Math.Max(1, (int)(pixelWidth * shrink));
                pixelHeight = Math.Max(1, (int)(pixelHeight * shrink));
            }

            BitmapSource source;
            using (var image = _document.Render(
                       pageIndex,
                       pixelWidth,
                       pixelHeight,
                       (float)PrintDpi,
                       (float)PrintDpi,
                       PdfRenderFlags.Annotations | PdfRenderFlags.ForPrinting))
            {
                source = ConvertToBitmapSource(image, PrintDpi);
            }

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                double drawWidth = pageWidth * scale;
                double drawHeight = pageHeight * scale;
                var center = new System.Windows.Point(_paper.Width / 2, _paper.Height / 2);

                if (rotate)
                    context.PushTransform(new RotateTransform(90, center.X, center.Y));

                context.DrawImage(
                    source,
                    new Rect(center.X - drawWidth / 2, center.Y - drawHeight / 2, drawWidth, drawHeight));

                if (rotate)
                    context.Pop();
            }

            var bounds = new Rect(_paper);
            return new DocumentPage(visual, _paper, bounds, bounds);
        }
    }

    public sealed class PdfViewerSession : IDisposable
    {
        private readonly PdfDocument _document;
        private readonly object _syncRoot = new();
        private bool _disposed;

        internal PdfViewerSession(PdfDocument document)
        {
            _document = document;
        }

        public int PageCount
        {
            get
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                    return _document.PageCount;
                }
            }
        }

        public SizeF GetPageSize(int pageIndex)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return PdfViewerService.GetPageSize(_document, pageIndex);
            }
        }

        public IReadOnlyList<SizeF> GetPageSizes()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                var sizes = new List<SizeF>(_document.PageCount);

                for (int pageIndex = 0; pageIndex < _document.PageCount; pageIndex++)
                    sizes.Add(PdfViewerService.GetPageSize(_document, pageIndex));

                return sizes;
            }
        }

        public Task<PdfRenderedPage> RenderPageAsync(
            int pageIndex,
            double zoomPercent,
            CancellationToken cancellationToken = default)
        {
            return RenderPageAsync(pageIndex, zoomPercent, 1d, cancellationToken);
        }

        /// <summary>
        /// Sayfayı verilen yakınlaştırma ve ekran ölçeği için fiziksel piksel çözünürlüğünde çizer.
        /// </summary>
        public Task<PdfRenderedPage> RenderPageAsync(
            int pageIndex,
            double zoomPercent,
            double pixelScale,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_syncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed();

                    SizeF size = PdfViewerService.GetPageSize(_document, pageIndex);
                    var (pixelWidth, pixelHeight, viewWidth, viewHeight) =
                        MeasurePage(size.Width, size.Height, zoomPercent, pixelScale);

                    // Bitmap'in DPI'ı, doğal boyutu ekrandaki DIP boyutuna eşit olacak şekilde ayarlanır.
                    double dpi = ScreenDpi * pixelWidth / viewWidth;

                    using var image = _document.Render(
                        pageIndex,
                        pixelWidth,
                        pixelHeight,
                        (float)dpi,
                        (float)dpi,
                        PdfRenderFlags.Annotations);

                    BitmapSource source = ConvertToBitmapSource(image, dpi);
                    return new PdfRenderedPage(
                        source, pixelWidth, pixelHeight, size.Width, size.Height, viewWidth, viewHeight);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Küçük resim paneli için sayfayı verilen piksel genişliğinde çizer.
        /// </summary>
        public Task<BitmapSource> RenderThumbnailAsync(
            int pageIndex,
            int pixelWidth,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_syncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed();

                    SizeF size = PdfViewerService.GetPageSize(_document, pageIndex);
                    int width = Math.Clamp(pixelWidth, 16, 1024);
                    int height = Math.Max(1, (int)Math.Round(width * size.Height / Math.Max(1f, size.Width)));

                    // Çok uzun sayfalar (fiş, afiş) küçük resim panelini taşırmasın.
                    if (height > width * 4)
                    {
                        height = width * 4;
                        width = Math.Max(1, (int)Math.Round(height * size.Width / Math.Max(1f, size.Height)));
                    }

                    using var image = _document.Render(
                        pageIndex,
                        width,
                        height,
                        (float)ScreenDpi,
                        (float)ScreenDpi,
                        PdfRenderFlags.Annotations);

                    return ConvertToBitmapSource(image, ScreenDpi);
                }
            }, cancellationToken);
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                _document.Dispose();
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// PDFium'un ürettiği GDI+ bitmap'ini tek kopyayla dondurulmuş bir WPF bitmap'ine çevirir.
    /// </summary>
    private static BitmapSource ConvertToBitmapSource(Image image, double dpi)
    {
        Bitmap? owned = null;
        Bitmap bitmap = image as Bitmap ?? (owned = new Bitmap(image));

        try
        {
            var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                // PDFium sayfayı opak beyaz zemin üzerine çizer; alfa kanalı yok sayılır.
                BitmapSource source = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    dpi,
                    dpi,
                    PixelFormats.Bgr32,
                    null,
                    data.Scan0,
                    data.Stride * bitmap.Height,
                    data.Stride);

                source.Freeze();
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static SizeF GetPageSize(PdfDocument document, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "Sayfa indeksi geçersiz.");

        return document.PageSizes[pageIndex] is SizeF size && size.Width > 0 && size.Height > 0
            ? size
            : new SizeF(595, 842);
    }
}
