using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    public enum DeviceType
    {
        Printer,
        Scanner,
        MultiFunction
    }

    public enum DeviceConnectionType
    {
        USB,
        NetworkIP,
        SharedWsd,
        Virtual
    }

    public enum DriverState
    {
        Ready,
        Missing,
        Installing,
        Configuring,
        NotSupported,
        Error
    }

    public enum DevicePresetModel
    {
        None,
        RicohSP4510SF,
        FujitsuFi6230,
        GenericWiaScanner,
        GenericPclPrinter
    }

    public partial class DeviceInfo : ObservableObject
    {
        [ObservableProperty]
        private string id = Guid.NewGuid().ToString();

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string manufacturer = string.Empty;

        [ObservableProperty]
        private string modelName = string.Empty;

        [ObservableProperty]
        private DeviceType type = DeviceType.Printer;

        [ObservableProperty]
        private DeviceConnectionType connectionType = DeviceConnectionType.USB;

        [ObservableProperty]
        private string ipAddress = string.Empty;

        [ObservableProperty]
        private int port = 9100;

        [ObservableProperty]
        private DriverState driverState = DriverState.Missing;

        [ObservableProperty]
        private string driverName = string.Empty;

        [ObservableProperty]
        private bool isOnline = true;

        [ObservableProperty]
        private bool isDefault;

        [ObservableProperty]
        private DevicePresetModel presetModel = DevicePresetModel.None;

        [ObservableProperty]
        private string statusMessage = "Hazır";

        [ObservableProperty]
        private string serialOrHardwareId = string.Empty;

        [ObservableProperty]
        private string location = string.Empty;

        public bool IsRicohSpecial => PresetModel == DevicePresetModel.RicohSP4510SF ||
                                      Name.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                                      ModelName.Contains("4510", StringComparison.OrdinalIgnoreCase);

        public bool IsFujitsuSpecial => PresetModel == DevicePresetModel.FujitsuFi6230 ||
                                        Name.Contains("6230", StringComparison.OrdinalIgnoreCase) ||
                                        ModelName.Contains("6230", StringComparison.OrdinalIgnoreCase);

        public string TypeIcon => Type switch
        {
            DeviceType.Printer => "🖨️",
            DeviceType.Scanner => "📑",
            DeviceType.MultiFunction => "📠",
            _ => "🔌"
        };

        public string ConnectionDescription => ConnectionType switch
        {
            DeviceConnectionType.USB => "USB Bağlantılı",
            DeviceConnectionType.NetworkIP => $"Ağ Cihazı ({IpAddress}:{Port})",
            DeviceConnectionType.SharedWsd => "WSD / Paylaşılan Aygıt",
            DeviceConnectionType.Virtual => "Sanal Yazıcı / PDF",
            _ => "Yerel Bağlantı"
        };

        public string DriverStatusDescription => DriverState switch
        {
            DriverState.Ready => "Sürücü Yüklü & Hazır",
            DriverState.Missing => "Sürücü Eksik (Kurulum Gerekli)",
            DriverState.Installing => "Sürücü Kuruluyor...",
            DriverState.Configuring => "Aygıt Yapılandırılıyor...",
            DriverState.NotSupported => "Manuel Sürücü Gerekli",
            DriverState.Error => "Bağlantı/Sürücü Hatası",
            _ => "Bilinmiyor"
        };

        public string DriverStatusBadgeColor => DriverState switch
        {
            DriverState.Ready => "#28A745",
            DriverState.Missing => "#E06A3B",
            DriverState.Installing or DriverState.Configuring => "#3B82F6",
            DriverState.Error => "#DC3545",
            _ => "#6C757D"
        };
    }
}
