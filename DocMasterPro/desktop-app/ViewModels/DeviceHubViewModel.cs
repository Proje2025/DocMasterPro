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
using DocConverter.Helpers;
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
        private Task _activeDiscovery = Task.CompletedTask;

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

        [ObservableProperty]
        private bool isScanning;

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
            // Kayıtlı cihaz olsa da bağlı cihazlar aranır. Eskiden tek bir cihaz kaydedilince keşif
            // atlanıyor, sonraki açılışta tarayıcı listede hiç görünmüyordu.
            await DiscoverDevicesAsync(false);
        }

        /// <summary>
        /// Elle eklenip kaydedilmiş, ancak keşifte görünmeyen cihazları (ör. IP ile eklenen ağ yazıcısı) listeye geri ekler.
        /// </summary>
        private async Task MergeSavedDevicesAsync()
        {
            List<DeviceInfo> saved;
            try
            {
                saved = await _driverService.LoadSavedDevicesAsync();
            }
            catch (Exception ex)
            {
                FileLogger.LogError("MergeSavedDevicesAsync", ex);
                return;
            }

            foreach (var device in saved)
            {
                bool known = Devices.Any(x =>
                    x.Id == device.Id ||
                    string.Equals(x.Name, device.Name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(device.IpAddress) && x.IpAddress == device.IpAddress && x.Port == device.Port));

                if (!known)
                    Devices.Add(device);
            }
        }

        private void UpdateSelectedPointers()
        {
            if (SelectedScanner == null || !Devices.Contains(SelectedScanner))
            {
                // Tarayıcı yoksa seçim boş kalır; bir yazıcıyı "tarayıcı" diye seçmek ilk taramada hataya yol açar.
                // Tara düğmesi, seçim boşsa Windows'taki tarayıcıyı kendisi bulur.
                SelectedScanner = Devices.FirstOrDefault(d => d.Type == DeviceType.Scanner)
                                  ?? Devices.FirstOrDefault(d => d.Type == DeviceType.MultiFunction);
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
            var discoveryDone = new TaskCompletionSource();
            _activeDiscovery = discoveryDone.Task;
            StatusMessage = scanFullNetwork ? "Yerel ve ağdaki tüm aygıtlar taranıyor..." : "Bağlı cihazlar taranıyor...";
            _cts = new CancellationTokenSource();

            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var list = await _discovery.DiscoverAllDevicesAsync(scanFullNetwork, _cts.Token, progress);

                // Kullanıcının seçimi yeniden taramada kaybolmasın.
                string? scannerName = SelectedScanner?.Name;
                string? printerName = SelectedPrinter?.Name;

                Devices.Clear();
                foreach (var dev in list)
                {
                    Devices.Add(dev);
                }

                await MergeSavedDevicesAsync();

                SelectedScanner = Devices.FirstOrDefault(d => d.Name == scannerName);
                SelectedPrinter = Devices.FirstOrDefault(d => d.Name == printerName);
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
                discoveryDone.TrySetResult();
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
            // Açılıştaki cihaz keşfi sürerken basılan "Tara" yok sayılmasın: keşfin bitmesi beklenir.
            if (IsBusy && !_activeDiscovery.IsCompleted)
            {
                StatusMessage = "Cihaz keşfi tamamlanıyor, tarama hemen başlayacak...";
                await _activeDiscovery;
            }

            if (IsBusy)
            {
                StatusMessage = "Devam eden işlem bitmeden yeni tarama başlatılamaz.";
                return;
            }

            if (!await EnsureScannerSelectedAsync())
            {
                MessageBox.Show(
                    "Windows'ta kullanılabilir bir tarayıcı bulunamadı.\n\n" +
                    "Tarayıcının açık ve bağlı olduğunu kontrol edip yeniden deneyin.",
                    "Tarayıcı Bulunamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DeviceInfo scanner = SelectedScanner!;
            int pagesBefore = ScannedPages.Count;

            IsBusy = true;
            IsScanning = true;
            StatusMessage = $"{scanner.Name} hazırlanıyor...";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var progress = new Progress<string>(msg => StatusMessage = msg);
            var pageProgress = new Progress<ScannedPageItem>(AddScannedPage);

            try
            {
                var result = await _scannerService.ScanAsync(scanner, ScanOptions, _cts.Token, progress, pageProgress);

                // Progress<T> geri çağrıları kuyruğa gönderilir; henüz işlenmemiş olanlar burada tamamlanır.
                foreach (var page in result.Pages)
                    AddScannedPage(page);

                int added = ScannedPages.Count - pagesBefore;

                var summary = new List<string>
                {
                    result.Cancelled
                        ? $"Tarama iptal edildi: {added} sayfa eklendi."
                        : $"Tarama tamamlandı: {added} sayfa eklendi (toplam {ScannedPages.Count})."
                };
                if (result.BlankPagesSkipped > 0)
                    summary.Add($"{result.BlankPagesSkipped} boş sayfa atlandı.");
                if (!string.IsNullOrEmpty(result.Notice))
                    summary.Add(result.Notice);

                StatusMessage = string.Join(" ", summary);

                if (!string.IsNullOrEmpty(result.Warning))
                {
                    MessageBox.Show(
                        $"{result.Warning}\n\nO ana kadar taranan {added} sayfa korundu.",
                        "Tarama Yarıda Kesildi", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("StartScanAsync Error", ex);
                StatusMessage = "Tarama başarısız.";
                MessageBox.Show(
                    $"Tarama yapılamadı:\n\n{ex.Message}",
                    "Tarama Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsScanning = false;
                IsBusy = false;
            }
        }

        private void AddScannedPage(ScannedPageItem page)
        {
            if (ScannedPages.Contains(page))
                return;

            page.PageNumber = ScannedPages.Count + 1;
            ScannedPages.Add(page);
            SelectedScannedPage = page;
        }

        [RelayCommand]
        public void CancelScan()
        {
            if (!IsScanning)
                return;

            _cts?.Cancel();
            StatusMessage = "Tarama iptal ediliyor (geçerli sayfa bittiğinde duracak)...";
        }

        /// <summary>
        /// Seçili tarayıcı yoksa WIA'ya sorup ilk tarayıcıyı seçer; kullanıcının önce
        /// "Cihazları Tara"ya basmasını gerektirmez.
        /// </summary>
        private async Task<bool> EnsureScannerSelectedAsync()
        {
            if (SelectedScanner != null && SelectedScanner.Type != DeviceType.Printer)
                return true;

            StatusMessage = "Tarayıcı aranıyor...";
            List<DeviceInfo> found;
            try
            {
                found = await Task.Run(() => _discovery.DiscoverWiaScanners());
            }
            catch (Exception ex)
            {
                FileLogger.LogError("EnsureScannerSelectedAsync", ex);
                found = new List<DeviceInfo>();
            }

            DeviceInfo? firstScanner = null;
            foreach (var scanner in found)
            {
                var existing = Devices.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d.SerialOrHardwareId) && d.SerialOrHardwareId == scanner.SerialOrHardwareId);

                if (existing == null)
                {
                    Devices.Add(scanner);
                    existing = scanner;
                }

                firstScanner ??= existing;
            }

            if (firstScanner != null)
                SelectedScanner = firstScanner;

            // Bulunamadıysa ve bir yazıcı seçiliyse tarama denenmez: yanlış aygıta bağlanmak
            // yerine kullanıcıya açık bir mesaj gösterilir.
            return SelectedScanner != null && SelectedScanner.Type != DeviceType.Printer;
        }

        [RelayCommand]
        public async Task OpenWiaNativeScanDialogAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;
            StatusMessage = "Windows tarama penceresi açılıyor...";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var newPages = await _scannerService.ScanViaWiaNativeDialogAsync(ScanOptions, _cts.Token, progress);
                foreach (var page in newPages)
                    AddScannedPage(page);

                StatusMessage = newPages.Count > 0
                    ? $"Windows tarama tamamlandı: {newPages.Count} sayfa eklendi."
                    : "Tarama iptal edildi.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("OpenWiaNativeScanDialogAsync Error", ex);
                StatusMessage = "Tarama başarısız.";
                MessageBox.Show(
                    $"Tarama yapılamadı:\n\n{ex.Message}",
                    "Tarama Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public void ClearScannedPages()
        {
            if (ScannedPages.Count == 0 || IsBusy)
                return;

            var answer = MessageBox.Show(
                $"Taranan {ScannedPages.Count} sayfa silinecek. Kaydedilmemiş taramalar geri getirilemez.\n\nDevam edilsin mi?",
                "Sayfaları Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            var removed = ScannedPages.ToList();
            ScannedPages.Clear();
            SelectedScannedPage = null;
            _scannerService.DeletePageFiles(removed);
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
            if (page == null || IsBusy) return;

            int index = ScannedPages.IndexOf(page);
            if (index < 0) return;

            ScannedPages.RemoveAt(index);
            for (int i = 0; i < ScannedPages.Count; i++)
            {
                ScannedPages[i].PageNumber = i + 1;
            }

            SelectedScannedPage = ScannedPages.Count == 0
                ? null
                : ScannedPages[Math.Min(index, ScannedPages.Count - 1)];

            _scannerService.DeletePageFiles(new[] { page });
        }

        private ScanPdfOptions CreatePdfOptions() => new()
        {
            Quality = ScanOptions.OutputQuality,
            Deskew = ScanOptions.AutoDeskew,
            CropDarkBorders = ScanOptions.AutoCrop
        };

        [RelayCommand]
        public async Task SaveScannedPagesAsPdfAsync()
        {
            if (IsBusy)
                return;

            if (ScannedPages.Count == 0)
            {
                MessageBox.Show("Kaydedilecek taranmış sayfa bulunmuyor.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Taranan Belgeleri PDF Olarak Kaydet",
                Filter = "PDF Belgesi (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = $"Tarama_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };

            if (dlg.ShowDialog() != true)
                return;

            IsBusy = true;
            ScanProgress = 0;
            StatusMessage = "PDF oluşturuluyor...";
            var progress = new Progress<int>(pct => ScanProgress = pct);

            try
            {
                var result = await _scannerService.SaveScannedPagesAsync(
                    ScannedPages.ToList(), dlg.FileName, CreatePdfOptions(), CancellationToken.None, progress);

                string size = PathValidator.FormatFileSize(result.FileSizeBytes);
                StatusMessage = $"PDF kaydedildi: {Path.GetFileName(result.OutputPath)} — {result.PageCount} sayfa, {size}";

                string skipped = result.SkippedPages > 0
                    ? $"\n\nUyarı: {result.SkippedPages} sayfa okunamadığı için PDF'e eklenemedi."
                    : string.Empty;

                MessageBox.Show(
                    $"Taranan belgeler kaydedildi:\n{result.OutputPath}\n\n{result.PageCount} sayfa — {size}{skipped}",
                    "PDF Kaydedildi", MessageBoxButton.OK,
                    result.SkippedPages > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("SaveScannedPagesAsPdfAsync Error", ex);
                StatusMessage = "PDF kaydedilemedi.";

                string reason = ex is IOException or UnauthorizedAccessException
                    ? $"{ex.Message}\n\nDosya başka bir programda (ör. PDF görüntüleyici) açık olabilir ya da klasöre yazma izniniz olmayabilir."
                    : ex.Message;

                MessageBox.Show(
                    $"PDF kaydedilemedi:\n\n{reason}",
                    "Kaydetme Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task SendScannedToPdfStudioAsync()
        {
            if (IsBusy)
                return;

            if (ScannedPages.Count == 0)
            {
                MessageBox.Show("Aktarılacak taranmış sayfa bulunmuyor.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string tempPdf = Path.Combine(Path.GetTempPath(), $"DocMaster_Scanned_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            IsBusy = true;
            ScanProgress = 0;
            StatusMessage = "Taranan sayfalar PDF Studio'ya aktarılıyor...";
            var progress = new Progress<int>(pct => ScanProgress = pct);

            try
            {
                await _scannerService.SaveScannedPagesAsync(
                    ScannedPages.ToList(), tempPdf, CreatePdfOptions(), CancellationToken.None, progress);
                OnSendToPdfStudioRequested?.Invoke(tempPdf);
                StatusMessage = "Taranan belge PDF Studio'da açıldı. Kalıcı olması için 'Farklı Kaydet' kullanın.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("SendScannedToPdfStudioAsync Error", ex);
                StatusMessage = "Aktarım başarısız.";
                MessageBox.Show(
                    $"Taranan sayfalar PDF Studio'ya aktarılamadı:\n\n{ex.Message}",
                    "Aktarım Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
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

            if (!await ConfirmPrinterTargetAsync(SelectedPrinter))
                return;

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

        /// <summary>
        /// Seçilen cihaz Windows'ta kurulu bir yazıcıyla eşleşmiyorsa belge sessizce başka bir yazıcıya
        /// gitmesin diye kullanıcıya hedef yazıcıyı gösterip onay ister.
        /// </summary>
        private static async Task<bool> ConfirmPrinterTargetAsync(DeviceInfo printer)
        {
            bool matched = true;
            string target = await Task.Run(() => PrinterService.ResolveWindowsPrinterName(printer, out matched));

            if (matched)
                return true;

            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(
                    "Windows'ta kurulu bir yazıcı bulunamadı.",
                    "Yazıcı Bulunamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var answer = MessageBox.Show(
                $"'{printer.Name}' Windows'ta kurulu bir yazıcıyla eşleşmedi.\n\n" +
                $"Belge şu yazıcıya gönderilecek:\n{target}\n\nDevam edilsin mi?",
                "Yazıcı Eşleşmedi", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            return answer == MessageBoxResult.Yes;
        }

        [RelayCommand]
        public async Task PrintTestPageAsync()
        {
            if (SelectedPrinter == null)
            {
                MessageBox.Show("Lütfen bir yazıcı seçin.", "Yazıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!await ConfirmPrinterTargetAsync(SelectedPrinter))
                return;

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
