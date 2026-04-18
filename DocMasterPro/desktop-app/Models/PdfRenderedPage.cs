using System.Windows.Media.Imaging;

namespace DocConverter.Models;

public sealed class PdfRenderedPage
{
    public PdfRenderedPage(BitmapSource image, double width, double height)
    {
        Image = image;
        Width = width;
        Height = height;
    }

    public BitmapSource Image { get; }

    public double Width { get; }

    public double Height { get; }
}
