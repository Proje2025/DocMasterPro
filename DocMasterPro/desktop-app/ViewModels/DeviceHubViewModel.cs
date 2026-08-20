using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Models;
using DocConverter.Services;
using Microsoft.Win32;

namespace DocConverter.ViewModels
{
    public partial class DeviceHubViewModel : ObservableObject
    {
        private readonly DeviceDiscoveryService _discovery = new();
        private readonly DriverManagementService _driverService = new();
        private readonly ScannerService _scannerService = new();
        private readonly PrinterService _printerService = new();
        private readonly BlankPageDetector _blankDetector = new();

        private CancellationTokenSource? _cts;

        // Callback to send scanned PDF directly to PDF Studio or Office View
        public Action<string>? OnSendToPdfStudioRequested { get; set; }

        // ==================== Cihaz Listesi ve Durum ====================
        [ObservableProperty]
        private ObservableCollection<DeviceInfo> devices = new();

        [ObservableProperty]
        private DeviceInfo? selectedDevice;

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private string statusMessage = "Cihazlar taranmaya hazır.";

        [ObservableProperty]
        private int selectedHubTabIndex = 0; // 0 = Cihazlar, 1 = Tarayıcı Stüdyosu, 2 = Yazıcı Stüdyosu, 3 = Boş Sayfa & Ayarlar

        // ==================== Manuel Ağ Cihazı Ekleme ====================
        [ObservableProperty]
        private string manualIpAddress = string.Empty;

        [ObservableProperty]
        private int manualPort = 9100;

        [ObservableProperty]
        private string manualDeviceName = string.Empty;

        // ==================== Tarayıcı Stüdyosu ====================
        [ObservableProperty]
        private DeviceInfo? selectedScanner;

        [ObservableProperty]
        private ScanOptions scanOptions = new();

        [ObservableProperty]
        private ObservableCollection<ScannedPageItem> scannedPages = new();

        [ObservableProperty]
        private ScannedPageItem? selectedScannedPage;

        [ObservableProperty]
        private int scanProgress;

        // ==================== Yazıcı Stüdyosu ====================
        [ObservableProperty]
        private DeviceInfo? selectedPrinter;

        [ObservableProperty]
        private string selectedPrintFilePath = string.Empty;

        [ObservableProperty]
        private PrintJobOptions printOptions = new();

        // ==================== Boş Sayfa Temizleme & Ayarlar ====================
        [ObservableProperty]
        private string cleanPdfSourcePath = string.Empty;

        [ObservableProperty]
        private double blankPageSensitivity = 98.5;

        public DeviceHubViewModel(bool autoLoad = true)
        {
            if (autoLoad)
            {
                // İlk açılışta kaydedilmiş cihazları yükle
                _ = LoadSavedDevicesAsync();
            }
        }

        public async Task LoadSavedDevicesAsync()
        {
            var saved = await _driverService.LoadSavedDevicesAsync();
            if (saved.Count > 0)
            {
                foreach (var d in saved)
                {
                    if (!Devices.Any(x => x.Id == d.Id))
                        Devices.Add(d);
                }
                UpdateSelectedPointers();
            }
            else
            {
                // İlk açılışta otomatik hızlı tarama yap
                await DiscoverDevicesAsync(false);
            }
        }

        private void UpdateSelectedPointers()
        {
            if (SelectedScanner == null || !Devices.Contains(SelectedScanner))
            {
                SelectedScanner = Devices.FirstOrDefault(d => d.IsFujitsuSpecial || d.Type == DeviceType.Scanner)
                                  ?? Devices.FirstOrDefault(d => d.Type == DeviceType.MultiFunction)
                                  ?? Devices.FirstOrDefault();
            }

            if (SelectedPrinter == null || !Devices.Contains(SelectedPrinter))
            {
                SelectedPrinter = Devices.FirstOrDefault(d => d.IsRicohSpecial || (d.IsDefault && d.Type != DeviceType.Scanner))
                                  ?? Devices.FirstOrDefault(d => d.Type == DeviceType.Printer || d.Type == DeviceType.MultiFunction)
                                  ?? Devices.FirstOrDefault();
            }
        }

        // ==================== Cihaz Keşfi ve Sürücü Kurulumu ====================
        [RelayCommand]
        public async Task DiscoverDevicesQuickAsync()
        {
            await DiscoverDevicesAsync(false);
        }

        [RelayCommand]
        public async Task DiscoverDevicesFullAsync()
        {
            await DiscoverDevicesAsync(true);
        }

        [RelayCommand]
        public async Task DiscoverDevicesAsync(bool scanFullNetwork = true)
        {
            if (IsBusy) return;

            IsBusy = true;
            StatusMessage = scanFullNetwork ? "Yerel ve ağdaki tüm aygıtlar taranıyor..." : "Bağlı cihazlar taranıyor...";
            _cts = new CancellationTokenSource();

            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var list = await _discovery.DiscoverAllDevicesAsync(scanFullNetwork, _cts.Token, progress);

                Devices.Clear();
                foreach (var dev in list)
                {
                    Devices.Add(dev);
                }

                UpdateSelectedPointers();

                // Eksik sürücüleri arka planda kontrol et
                foreach (var d in Devices.Where(x => x.DriverState == DriverState.Missing))
                {
                    d.DriverState = await _driverService.CheckDeviceDriverStatusAsync(d, _cts.Token);
                }

                StatusMessage = $"Tarama tamamlandı: {Devices.Count} aygıt bulundu.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverDevicesAsync Error", ex);
                StatusMessage = $"Tarama sırasında hata: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task AutoSetupDriverAsync(DeviceInfo? device)
        {
            device ??= SelectedDevice;
            if (device == null)
            {
                MessageBox.Show("Lütfen yapılandırılacak bir cihaz seçin.", "Cihaz Seçimi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            device.DriverState = DriverState.Configuring;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var result = await _driverService.AutoConfigureDeviceAsync(device, progress, _cts.Token);
                if (result.Success)
                {
                    StatusMessage = result.Message;
                    MessageBox.Show(result.Message, "Sürücü / Aygıt Kurulumu Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = $"Kurulum başarısız: {result.Message}";
                    MessageBox.Show(result.Message, "Kurulum Uyarısı", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("AutoSetupDriverAsync Error", ex);
                StatusMessage = $"Hata: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task AddManualNetworkDeviceAsync()
        {
            if (string.IsNullOrWhiteSpace(ManualIpAddress))
            {
                MessageBox.Show("Lütfen geçerli bir IP adresi girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            StatusMessage = $"{ManualIpAddress} adresi test ediliyor...";

            try
            {
                bool portOpen = await DeviceDiscoveryService.IsPortOpenAsync(ManualIpAddress, ManualPort, 1000, CancellationToken.None);
                string devName = string.IsNullOrWhiteSpace(ManualDeviceName) ? $"Ağ Cihazı ({ManualIpAddress})" : ManualDeviceName.Trim();

                var dev = new DeviceInfo
                {
                    Id = $"NET_{ManualIpAddress.Replace(".", "_")}_{ManualPort}",
                    Name = devName,
                    Manufacturer = devName.Contains("Ricoh", StringComparison.OrdinalIgnoreCase) ? "Ricoh" : "Ağ Üreticisi",
                    ModelName = devName,
                    Type = devName.Contains("4510", StringComparison.OrdinalIgnoreCase) ? DeviceType.MultiFunction : DeviceType.Printer,
                    ConnectionType = DeviceConnectionType.NetworkIP,
                    IpAddress = ManualIpAddress.Trim(),
                    Port = ManualPort,
                    DriverState = portOpen ? DriverState.Ready : DriverState.Missing,
                    IsOnline = portOpen,
                    StatusMessage = portOpen ? "Ağ Bağlantısı Doğrulandı (Port 9100)" : "IP Adresine Ulaşılamadı",
                    PresetModel = DeviceDiscoveryService.IdentifyPresetModelByName(devName)
                };

                var existing = Devices.FirstOrDefault(x => x.IpAddress == ManualIpAddress);
                if (existing != null) Devices.Remove(existing);

                Devices.Add(dev);
                SelectedDevice = dev;
                SelectedPrinter = dev;

                await _driverService.SaveConfiguredDeviceAsync(dev);
                StatusMessage = $"{dev.Name} başarıyla listeye eklendi ve kaydedildi.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("AddManualNetworkDeviceAsync Error", ex);
                StatusMessage = $"Cihaz ekleme hatası: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ==================== Tarayıcı Stüdyosu Komutları ====================
        [RelayCommand]
        public async Task StartScanAsync()
        {
            if (SelectedScanner == null)
            {
                MessageBox.Show("Lütfen tarama yapılacak bir tarayıcı seçin (örn. Fujitsu fi-6230).", "Tarayıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            ScanProgress = 0;
            StatusMessage = $"{SelectedScanner.Name} üzerinden taranıyor...";
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var newPages = await _scannerService.ScanDocumentsAsync(SelectedScanner, ScanOptions, _cts.Token, progress);
                foreach (var p in newPages)
                {
                    p.PageNumber = ScannedPages.Count + 1;
                    ScannedPages.Add(p);
                }

                if (ScannedPages.Count > 0)
                {
                    SelectedScannedPage = ScannedPages.Last();
                }

                StatusMessage = $"Tarama tamamlandı: Toplam {ScannedPages.Count} sayfa stüdyoda.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("StartScanAsync Error", ex);
                StatusMessage = $"Tarama hatası: {ex.Message}";
                MessageBox.Show($"Tarama sırasında hata oluştu:\n{ex.Message}", "Tarama Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                ScanProgress = 100;
            }
        }

        [RelayCommand]
        public async Task OpenWiaNativeScanDialogAsync()
        {
            IsBusy = true;
            ScanProgress = 0;
            StatusMessage = "Windows yerel tarayıcı penceresi açılıyor...";
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var newPages = await _scannerService.ScanViaWiaNativeDialogAsync(ScanOptions, _cts.Token, progress);
                foreach (var p in newPages)
                {
                    p.PageNumber = ScannedPages.Count + 1;
                    ScannedPages.Add(p);
                }

                if (ScannedPages.Count > 0)
                {
                    SelectedScannedPage = ScannedPages.Last();
                    StatusMessage = $"Windows yerel tarama tamamlandı: {newPages.Count} sayfa eklendi.";
                }
                else
                {
                    StatusMessage = "Tarama iptal edildi veya görüntü alınamadı.";
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("OpenWiaNativeScanDialogAsync Error", ex);
                StatusMessage = $"Yerel tarama hatası: {ex.Message}";
                MessageBox.Show($"Tarama sırasında hata oluştu:\n{ex.Message}", "Tarama Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                ScanProgress = 100;
            }
        }

        [RelayCommand]
        public void ClearScannedPages()
        {
            ScannedPages.Clear();
            SelectedScannedPage = null;
            StatusMessage = "Taranan sayfalar temizlendi.";
        }

        [RelayCommand]
        public void RotateSelectedScannedPage()
        {
            if (SelectedScannedPage == null) return;
            SelectedScannedPage.Rotation = (SelectedScannedPage.Rotation + 90) % 360;
        }

        [RelayCommand]
        public void DeleteSelectedScannedPage(ScannedPageItem? page)
        {
            page ??= SelectedScannedPage;
            if (page == null) return;

            ScannedPages.Remove(page);
            for (int i = 0; i < ScannedPages.Count; i++)
            {
                ScannedPages[i].PageNumber = i + 1;
            }
        }

        [RelayCommand]
        public async Task SaveScannedPagesAsPdfAsync()
        {
            if (ScannedPages.Count == 0)
            {
                MessageBox.Show("Kaydedilecek taranmış sayfa bulunmuyor.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Taranan Belgeleri PDF Olarak Kaydet",
                Filter = "PDF Belgesi (*.pdf)|*.pdf",
                FileName = $"Tarama_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };

            if (dlg.ShowDialog() == true)
            {
                IsBusy = true;
                StatusMessage = "PDF oluşturuluyor...";
                var progress = new Progress<int>(pct => ScanProgress = pct);

                try
                {
                    await _scannerService.SaveScannedPagesToPdfAsync(ScannedPages.ToList(), dlg.FileName, CancellationToken.None, progress);
                    StatusMessage = $"PDF başarıyla kaydedildi: {Path.GetFileName(dlg.FileName)}";
                    MessageBox.Show($"Taranan belgeler başarıyla kaydedildi:\n{dlg.FileName}", "PDF Kaydedildi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("SaveScannedPagesAsPdfAsync Error", ex);
                    StatusMessage = $"PDF kaydetme hatası: {ex.Message}";
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        [RelayCommand]
        public async Task SendScannedToPdfStudioAsync()
        {
            if (ScannedPages.Count == 0)
            {
                MessageBox.Show("Aktarılacak taranmış sayfa bulunmuyor.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string tempPdf = Path.Combine(Path.GetTempPath(), $"DocMaster_Scanned_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            IsBusy = true;
            StatusMessage = "Taranan sayfalar PDF Studio'ya aktarılıyor...";

            try
            {
                await _scannerService.SaveScannedPagesToPdfAsync(ScannedPages.ToList(), tempPdf);
                OnSendToPdfStudioRequested?.Invoke(tempPdf);
                StatusMessage = "Taranan belge PDF Studio'da açıldı.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("SendScannedToPdfStudioAsync Error", ex);
                StatusMessage = $"Aktarım hatası: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ==================== Yazıcı Stüdyosu Komutları ====================
        [RelayCommand]
        public void SelectPrintFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Yazdırılacak Belgeyi Seçin",
                Filter = "Desteklenen Belgeler (*.pdf;*.png;*.jpg;*.docx)|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.docx|PDF Dosyaları (*.pdf)|*.pdf|Resim Dosyaları (*.png;*.jpg)|*.png;*.jpg;*.jpeg;*.bmp|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedPrintFilePath = dlg.FileName;
                StatusMessage = $"Yazdırma için dosya seçildi: {Path.GetFileName(dlg.FileName)}";
            }
        }

        [RelayCommand]
        public async Task PrintSelectedFileAsync()
        {
            if (SelectedPrinter == null)
            {
                MessageBox.Show("Lütfen bir yazıcı seçin (örn. Ricoh SP 4510SF).", "Yazıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(SelectedPrintFilePath) || !File.Exists(SelectedPrintFilePath))
            {
                MessageBox.Show("Lütfen geçerli bir yazdırılacak dosya seçin.", "Dosya Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var res = await _printerService.PrintDocumentAsync(SelectedPrinter, SelectedPrintFilePath, PrintOptions, _cts.Token, progress);
                StatusMessage = res.Message;

                if (res.Success)
                {
                    MessageBox.Show(res.Message, "Yazdırma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(res.Message, "Yazdırma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintSelectedFileAsync Error", ex);
                StatusMessage = $"Yazdırma hatası: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task PrintTestPageAsync()
        {
            if (SelectedPrinter == null)
            {
                MessageBox.Show("Lütfen bir yazıcı seçin.", "Yazıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var res = await _printerService.PrintTestPageAsync(SelectedPrinter, _cts.Token, progress);
                StatusMessage = res.Message;

                if (res.Success)
                {
                    MessageBox.Show($"{SelectedPrinter.Name} için test sayfası başarıyla yazıcıya gönderildi.", "Test Sayfası Gönderildi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(res.Message, "Test Sayfası Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintTestPageAsync Error", ex);
                StatusMessage = $"Test sayfası hatası: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ==================== Boş Sayfa Temizleme & Hızlı Araçlar ====================
        [RelayCommand]
        public void SelectCleanPdfFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Boş Sayfaları Temizlenecek PDF'i Seçin",
                Filter = "PDF Dosyaları (*.pdf)|*.pdf"
            };

            if (dlg.ShowDialog() == true)
            {
                CleanPdfSourcePath = dlg.FileName;
            }
        }

        [RelayCommand]
        public async Task CleanBlankPagesFromPdfAsync()
        {
            if (string.IsNullOrEmpty(CleanPdfSourcePath) || !File.Exists(CleanPdfSourcePath))
            {
                MessageBox.Show("Lütfen geçerli bir PDF dosyası seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Temizlenmiş PDF'i Kaydet",
                Filter = "PDF Dosyası (*.pdf)|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(CleanPdfSourcePath) + "_temiz.pdf"
            };

            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            ScanProgress = 0;
            StatusMessage = "Boş sayfalar tespit edilip ayıklanıyor...";

            try
            {
                var progressReporter = new Progress<int>(pct => ScanProgress = pct);
                var (outPath, removed, total) = await _blankDetector.RemoveBlankPagesFromPdfAsync(
                    CleanPdfSourcePath, dlg.FileName, BlankPageSensitivity, progressReporter);

                StatusMessage = $"Temizleme tamamlandı: {total} sayfadan {removed} boş sayfa silindi.";
                MessageBox.Show(
                    $"İşlem tamamlandı!\n\nToplam Sayfa: {total}\nSilinen Boş Sayfa: {removed}\nKalan Sayfa: {total - removed}\n\nKaydedilen dosya:\n{dlg.FileName}",
                    "Boş Sayfalar Temizlendi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("CleanBlankPagesFromPdfAsync Error", ex);
                MessageBox.Show($"Temizleme hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                ScanProgress = 100;
            }
        }
    }
}
