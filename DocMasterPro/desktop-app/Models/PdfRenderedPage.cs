using System.Windows.Media.Imaging;

namespace DocConverter.Models;

public sealed class PdfRenderedPage
{
    public PdfRenderedPage(BitmapSource image, double width, double height)
        : this(image, width, height, width, height)
    {
    }

    public PdfRenderedPage(BitmapSource image, double width, double height, double sourceWidth, double sourceHeight)
        : this(image, width, height, sourceWidth, sourceHeight, width, height)
    {
    }

    public PdfRenderedPage(
        BitmapSource image,
        double width,
        double height,
        double sourceWidth,
        double sourceHeight,
        double viewWidth,
        double viewHeight)
    {
        Image = image;
        Width = width;
        Height = height;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        ViewWidth = viewWidth;
        ViewHeight = viewHeight;
    }

    public BitmapSource Image { get; }

    /// <summary>Bitmap genişliği (fiziksel piksel).</summary>
    public double Width { get; }

    /// <summary>Bitmap yüksekliği (fiziksel piksel).</summary>
    public double Height { get; }

    /// <summary>Sayfa genişliği (PDF noktası, 1/72 inç).</summary>
    public double SourceWidth { get; }

    /// <summary>Sayfa yüksekliği (PDF noktası, 1/72 inç).</summary>
    public double SourceHeight { get; }

    /// <summary>Ekranda kaplanacak genişlik (cihazdan bağımsız birim, 1/96 inç).</summary>
    public double ViewWidth { get; }

    /// <summary>Ekranda kaplanacak yükseklik (cihazdan bağımsız birim, 1/96 inç).</summary>
    public double ViewHeight { get; }
}
