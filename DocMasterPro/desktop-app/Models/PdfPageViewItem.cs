using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models;

public partial class PdfPageViewItem : ObservableObject
{
    public PdfPageViewItem(int pageIndex, double sourceWidth, double sourceHeight)
    {
        PageIndex = pageIndex;
        PageNumber = pageIndex + 1;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public int PageIndex { get; }

    public int PageNumber { get; }

    public double SourceWidth { get; }

    public double SourceHeight { get; }

    [ObservableProperty]
    private double viewWidth;

    [ObservableProperty]
    private double viewHeight;

    [ObservableProperty]
    private BitmapSource? image;

    [ObservableProperty]
    private bool isRendering;

    [ObservableProperty]
    private string renderStatus = "Sayfa beklemede";

    [ObservableProperty]
    private bool isActive;

    public ObservableCollection<PdfAnnotationItem> Annotations { get; } = new();

    public ObservableCollection<PdfSearchResult> SearchResults { get; } = new();

    public string PageLabel => $"Sayfa {PageNumber}";
}
