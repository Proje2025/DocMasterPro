using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    public enum PrintOrientation
    {
        Portrait,
        Landscape
    }

    public partial class PrintJobOptions : ObservableObject
    {
        [ObservableProperty]
        private int copies = 1;

        [ObservableProperty]
        private PrintOrientation orientation = PrintOrientation.Portrait;

        [ObservableProperty]
        private bool fitToPage = true;

        [ObservableProperty]
        private bool duplexPrint = false;

        [ObservableProperty]
        private string duplexMode = "Çift Taraflı (Uzun Kenar)";

        [ObservableProperty]
        private string pageRange = "Tümü";
    }
}
