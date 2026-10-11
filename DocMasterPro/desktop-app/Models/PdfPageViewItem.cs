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

    public double SourceWidth { get; private set; }

    public double SourceHeight { get; private set; }

    [ObservableProperty]
    private double viewWidth;

    [ObservableProperty]
    private double viewHeight;

    [ObservableProperty]
    private BitmapSource? image;

    /// <summary>
    /// Görüntü, şu anki yakınlaştırma için piksel piksel üretildiyse true. Yakınlaştırma değişince
    /// yeni görüntü hazırlanana kadar eskisi gerilerek gösterilir ve bu değer false olur.
    /// </summary>
    [ObservableProperty]
    private bool isImageExact;

    [ObservableProperty]
    private BitmapSource? thumbnail;

    [ObservableProperty]
    private bool isRendering;

    [ObservableProperty]
    private string renderStatus = "";

    [ObservableProperty]
    private bool isActive;

    /// <summary>Görüntünün üretildiği ölçek anahtarı; farklıysa sayfa yeniden çizilmelidir.</summary>
    public string RenderedKey { get; set; } = "";

    /// <summary>Görüntü, üretildiği ölçekte ekran pikselleriyle birebir eşleşiyor mu (piksel sınırına takılmadı mı).</summary>
    public bool RenderedExact { get; set; }

    /// <summary>Şu anda çizilmekte olan ölçek anahtarı; aynı ölçek iki kez çizilmesin diye tutulur.</summary>
    public string PendingKey { get; set; } = "";

    public bool IsThumbnailRequested { get; set; }

    public ObservableCollection<PdfAnnotationItem> Annotations { get; } = new();

    public ObservableCollection<PdfSearchResult> SearchResults { get; } = new();

    public string PageLabel => $"Sayfa {PageNumber}";

    public void UpdateSourceSize(double sourceWidth, double sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return;

        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }
}
