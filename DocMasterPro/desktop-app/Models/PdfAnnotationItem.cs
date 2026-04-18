using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models;

public partial class PdfAnnotationItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private int pageIndex;

    [ObservableProperty]
    private PdfAnnotationType type;

    [ObservableProperty]
    private double x;

    [ObservableProperty]
    private double y;

    [ObservableProperty]
    private double width;

    [ObservableProperty]
    private double height;

    [ObservableProperty]
    private string text = "";

    [ObservableProperty]
    private string color = "#F8D84A";

    [ObservableProperty]
    private double opacity = 0.45;

    [ObservableProperty]
    private double strokeWidth = 2;

    [ObservableProperty]
    private double fontSize = 16;

    [ObservableProperty]
    private DateTime createdAt = DateTime.Now;

    [ObservableProperty]
    private double viewX;

    [ObservableProperty]
    private double viewY;

    [ObservableProperty]
    private double viewWidth;

    [ObservableProperty]
    private double viewHeight;

    [ObservableProperty]
    private bool isSelected;

    public string ToolLabel => Type switch
    {
        PdfAnnotationType.Text => "Metin",
        PdfAnnotationType.Highlight => "Vurgula",
        PdfAnnotationType.Note => "Not",
        PdfAnnotationType.Ink => "Çizim",
        PdfAnnotationType.Rectangle => "Kutu",
        _ => Type.ToString()
    };

    public string Summary
    {
        get
        {
            var content = string.IsNullOrWhiteSpace(Text) ? ToolLabel : Text.Trim();
            return string.Create(CultureInfo.InvariantCulture, $"{ToolLabel} - Sayfa {PageIndex + 1}: {content}");
        }
    }
}
