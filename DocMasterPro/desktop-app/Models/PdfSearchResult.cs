using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models;

public partial class PdfSearchResult : ObservableObject
{
    [ObservableProperty]
    private int pageIndex;

    [ObservableProperty]
    private int resultIndex;

    [ObservableProperty]
    private string text = "";

    [ObservableProperty]
    private double x;

    [ObservableProperty]
    private double y;

    [ObservableProperty]
    private double width;

    [ObservableProperty]
    private double height;

    [ObservableProperty]
    private double viewX;

    [ObservableProperty]
    private double viewY;

    [ObservableProperty]
    private double viewWidth;

    [ObservableProperty]
    private double viewHeight;

    [ObservableProperty]
    private bool isActive;

    public string DisplayText => $"Sayfa {PageIndex + 1}: {Text}";
}
