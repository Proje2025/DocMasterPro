using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DocConverter.ViewModels
{
    public partial class HomeHubViewModel : ObservableObject
    {
        public Action? OnNavigateToOfficeRequested { get; set; }
        public Action? OnNavigateToDevicesRequested { get; set; }
        public Action? OnNavigateToScannerStudioRequested { get; set; }
        public Action? OnNavigateToPrinterStudioRequested { get; set; }

        [ObservableProperty]
        private string appVersion = "v1.1.0 Pro";

        [ObservableProperty]
        private int readyDevicesCount = 0;

        [ObservableProperty]
        private string systemStatusMessage = "Sistem ve donanım servisleri aktif";

        [RelayCommand]
        public void OpenOfficeSuite()
        {
            OnNavigateToOfficeRequested?.Invoke();
        }

        [RelayCommand]
        public void OpenDeviceHub()
        {
            OnNavigateToDevicesRequested?.Invoke();
        }

        [RelayCommand]
        public void OpenScannerStudio()
        {
            OnNavigateToScannerStudioRequested?.Invoke();
        }

        [RelayCommand]
        public void OpenPrinterStudio()
        {
            OnNavigateToPrinterStudioRequested?.Invoke();
        }
    }
}
