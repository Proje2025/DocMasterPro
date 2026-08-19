using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    public partial class AppSettings : ObservableObject
    {
        [ObservableProperty]
        private bool duplexScan = false;

        [ObservableProperty]
        private bool duplexPrint = false;

        [ObservableProperty]
        private bool autoDeleteBlankPages = true;

        [ObservableProperty]
        private double blankPageSensitivity = 98.5; // % eşik

        [ObservableProperty]
        private int defaultScanResolution = 300;

        [ObservableProperty]
        private string defaultScanColorMode = "Renkli";

        [ObservableProperty]
        private string defaultPrintOrientation = "Dikey";

        [ObservableProperty]
        private bool fitToPageOnPrint = true;
    }
}
