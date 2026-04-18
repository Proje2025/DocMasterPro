using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocConverter.Models;
using PdfiumViewer.Core;
using PdfiumViewer.Enums;
using Image = System.Drawing.Image;

namespace DocConverter.Services;

public class PdfViewerService
{
    private const double ScreenDpi = 96d;
    private const double PdfPointDpi = 72d;
    private static readonly object NativeLoadLock = new();
    private static IntPtr pdfiumHandle;

    public Task<PdfRenderedPage> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        double zoomPercent,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePdfiumLoaded();

            using var document = PdfDocument.Load(pdfPath);
            if (pageIndex < 0 || pageIndex >= document.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "Sayfa indeksi geçersiz.");

            SizeF size = GetPageSize(document, pageIndex);
            double scale = Math.Max(0.25, zoomPercent / 100d) * ScreenDpi / PdfPointDpi;
            int width = Math.Max(1, (int)Math.Round(size.Width * scale));
            int height = Math.Max(1, (int)Math.Round(size.Height * scale));

            using var image = document.Render(
                pageIndex,
                width,
                height,
                (float)ScreenDpi,
                (float)ScreenDpi,
                PdfRenderFlags.Annotations);

            BitmapSource source = ConvertToBitmapSource(image);
            source.Freeze();
            return new PdfRenderedPage(source, width, height);
        }, cancellationToken);
    }

    public int GetPageCount(string pdfPath)
    {
        EnsurePdfiumLoaded();
        using var document = PdfDocument.Load(pdfPath);
        return document.PageCount;
    }

    public SizeF GetPageSize(string pdfPath, int pageIndex)
    {
        EnsurePdfiumLoaded();
        using var document = PdfDocument.Load(pdfPath);
        return GetPageSize(document, pageIndex);
    }

    public void Print(string pdfPath)
    {
        EnsurePdfiumLoaded();
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        using var document = PdfDocument.Load(pdfPath);
        var fixedDocument = new FixedDocument();

        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            SizeF size = GetPageSize(document, pageIndex);
            double width = size.Width * ScreenDpi / PdfPointDpi;
            double height = size.Height * ScreenDpi / PdfPointDpi;

            using var image = document.Render(
                pageIndex,
                Math.Max(1, (int)Math.Round(width)),
                Math.Max(1, (int)Math.Round(height)),
                (float)ScreenDpi,
                (float)ScreenDpi,
                PdfRenderFlags.Annotations);

            BitmapSource source = ConvertToBitmapSource(image);
            source.Freeze();

            var fixedPage = new FixedPage
            {
                Width = dialog.PrintableAreaWidth,
                Height = dialog.PrintableAreaHeight,
                Background = System.Windows.Media.Brushes.White
            };

            double scale = Math.Min(dialog.PrintableAreaWidth / width, dialog.PrintableAreaHeight / height);
            var pageImage = new System.Windows.Controls.Image
            {
                Source = source,
                Width = width * scale,
                Height = height * scale,
                Stretch = Stretch.Fill
            };

            FixedPage.SetLeft(pageImage, (dialog.PrintableAreaWidth - pageImage.Width) / 2);
            FixedPage.SetTop(pageImage, (dialog.PrintableAreaHeight - pageImage.Height) / 2);
            fixedPage.Children.Add(pageImage);

            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(fixedPage);
            fixedDocument.Pages.Add(pageContent);
        }

        dialog.PrintDocument(fixedDocument.DocumentPaginator, "DocMaster Pro PDF Studio");
    }

    private static BitmapSource ConvertToBitmapSource(Image image)
    {
        using var bitmap = new Bitmap(image);
        IntPtr hBitmap = bitmap.GetHbitmap();

        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            _ = DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static void EnsurePdfiumLoaded()
    {
        if (pdfiumHandle != IntPtr.Zero)
            return;

        lock (NativeLoadLock)
        {
            if (pdfiumHandle != IntPtr.Zero)
                return;

            string architectureFolder = Environment.Is64BitProcess ? "x64" : "x86";
            string nativePath = Path.Combine(AppContext.BaseDirectory, architectureFolder, "pdfium.dll");
            if (!File.Exists(nativePath))
                nativePath = Path.Combine(AppContext.BaseDirectory, "pdfium.dll");

            if (!File.Exists(nativePath))
            {
                throw new DllNotFoundException(
                    $"PDFium native runtime bulunamadı: {nativePath}. PdfiumViewer native paketlerinin çıktı klasörüne kopyalandığını doğrulayın.");
            }

            pdfiumHandle = NativeLibrary.Load(nativePath);
        }
    }

    private static SizeF GetPageSize(PdfDocument document, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "Sayfa indeksi geçersiz.");

        return document.PageSizes[pageIndex] is SizeF size
            ? size
            : new SizeF(595, 842);
    }
}
