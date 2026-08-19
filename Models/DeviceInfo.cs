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
        private string driverName = string.Empty;

        [ObservableProperty]
        private bool isOnline = true;

        [ObservableProperty]
        private bool isDefault;

        [ObservableProperty]
        private bool supportsDuplex = true;

        [ObservableProperty]
        private string statusMessage = "Hazır";

        [ObservableProperty]
        private string serialOrHardwareId = string.Empty;

        public string TypeIcon => Type switch
        {
            DeviceType.Printer => "🖨️",
            DeviceType.Scanner => "📑",
            DeviceType.MultiFunction => "📠",
            _ => "🔌"
        };

        public string DisplayName => IsDefault ? $"{Name} (Varsayılan)" : Name;
    }
}
