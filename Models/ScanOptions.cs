using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    public enum ScanSource
    {
        Flatbed,       // Cam (Düz Yatak)
        FeederSingle,  // ADF (Tek Taraflı)
        FeederDuplex   // ADF (Çift Taraflı)
    }

    public enum ScanColorMode
    {
        Color,          // Renkli
        Grayscale,      // Gri Tonlama
        BlackAndWhite   // Siyah-Beyaz
    }

    public partial class ScanOptions : ObservableObject
    {
        [ObservableProperty]
        private ScanSource source = ScanSource.FeederSingle;

        [ObservableProperty]
        private ScanColorMode colorMode = ScanColorMode.Color;

        [ObservableProperty]
        private int resolution = 300;

        [ObservableProperty]
        private bool duplexScan = false;

        [ObservableProperty]
        private bool removeBlankPages = true;

        [ObservableProperty]
        private double blankPageThreshold = 98.5; // % beyazlık eşiği
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

        [ObservableProperty]
        private bool isBlank;

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
