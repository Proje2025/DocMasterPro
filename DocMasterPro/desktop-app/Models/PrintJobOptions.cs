using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    public enum PrintDuplex
    {
        Simplex,         // Tek Taraflı
        DuplexLongEdge,  // Çift Taraflı (Uzun Kenardan Çevir)
        DuplexShortEdge  // Çift Taraflı (Kısa Kenardan Çevir)
    }

    public enum PrintOrientation
    {
        Portrait,
        Landscape
    }

    public enum PrintColorMode
    {
        Monochrome,
        Color
    }

    public partial class PrintJobOptions : ObservableObject
    {
        [ObservableProperty]
        private int copies = 1;

        [ObservableProperty]
        private PrintDuplex duplex = PrintDuplex.Simplex;

        [ObservableProperty]
        private PrintOrientation orientation = PrintOrientation.Portrait;

        [ObservableProperty]
        private PrintColorMode colorMode = PrintColorMode.Monochrome;

        [ObservableProperty]
        private string paperSize = "A4";

        [ObservableProperty]
        private bool collate = true;

        [ObservableProperty]
        private string pageRange = "All"; // "All", "1-5", "1,3,5"

        [ObservableProperty]
        private bool fitToPage = true;
    }
}
