using System.Windows.Media.Imaging;

namespace DocConverter.Models;

public sealed class PdfRenderedPage
{
    public PdfRenderedPage(BitmapSource image, double width, double height)
        : this(image, width, height, width, height)
    {
    }

    public PdfRenderedPage(BitmapSource image, double width, double height, double sourceWidth, double sourceHeight)
    {
        Image = image;
        Width = width;
        Height = height;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public BitmapSource Image { get; }

    public double Width { get; }

    public double Height { get; }

    public double SourceWidth { get; }

    public double SourceHeight { get; }
}
