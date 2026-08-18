using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    public enum ScanSource
    {
        Flatbed,       // Cam (Düz Yatak)
        FeederSingle,  // Otomatik Belge Besleyici (ADF Tek Taraflı)
        FeederDuplex   // Otomatik Belge Besleyici (ADF Çift Taraflı)
    }

    public enum ScanColorMode
    {
        Color,          // 24-bit Renkli
        Grayscale,      // 8-bit Gri Tonlama
        BlackAndWhite   // 1-bit Siyah-Beyaz (Metin/Belge)
    }

    public enum ScanResolution
    {
        Dpi150 = 150,
        Dpi200 = 200,
        Dpi300 = 300,
        Dpi600 = 600
    }

    public enum ScanPaperSize
    {
        A4,
        A5,
        Letter,
        Legal,
        Auto
    }

    public partial class ScanOptions : ObservableObject
    {
        [ObservableProperty]
        private ScanSource source = ScanSource.FeederSingle;

        [ObservableProperty]
        private ScanColorMode colorMode = ScanColorMode.Color;

        [ObservableProperty]
        private ScanResolution resolution = ScanResolution.Dpi300;

        [ObservableProperty]
        private ScanPaperSize paperSize = ScanPaperSize.A4;

        [ObservableProperty]
        private int brightness = 0; // -100 .. 100

        [ObservableProperty]
        private int contrast = 0;   // -100 .. 100

        [ObservableProperty]
        private bool autoDeskew = true;

        [ObservableProperty]
        private bool removeBlankPages;

        [ObservableProperty]
        private bool autoCrop = true;
    }

    public partial class ScannedPageItem : ObservableObject
    {
        [ObservableProperty]
        private int pageNumber;

        [ObservableProperty]
        private string filePath = string.Empty;

        [ObservableProperty]
        private BitmapSource? previewImage;

        [ObservableProperty]
        private int rotation; // 0, 90, 180, 270

        [ObservableProperty]
        private int resolutionDpi = 300;

        [ObservableProperty]
        private long fileSize;

        [ObservableProperty]
        private bool isSelected;

        public string DisplayName => $"Sayfa {PageNumber}";
        public string FileSizeFormatted => FormatFileSize(FileSize);

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
