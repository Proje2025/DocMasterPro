using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DocConverter.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocConverter.Services
{
    public class ScannerService
    {
        private static readonly string ScanTempDir = Path.Combine(Path.GetTempPath(), "DocMaster_Scans");
        private readonly BlankPageDetector _blankDetector = new();

        public ScannerService()
        {
            if (!Directory.Exists(ScanTempDir))
            {
                Directory.CreateDirectory(ScanTempDir);
            }
        }

        /// <summary>
        /// Sistemde kayıtlı ve bağlı WIA tarayıcıları listeler.
        /// Kesinlikle sahte/mock veri içermez.
        /// </summary>
        public List<DeviceInfo> DiscoverScanners()
        {
            var scanners = new List<DeviceInfo>();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return scanners;

            try
            {
                Type? deviceManagerType = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (deviceManagerType != null)
                {
                    dynamic? deviceManager = Activator.CreateInstance(deviceManagerType);
                    if (deviceManager != null)
                    {
                        dynamic deviceInfos = deviceManager.DeviceInfos;
                        int count = (int)deviceInfos.Count;

                        for (int i = 1; i <= count; i++)
                        {
                            dynamic info = deviceInfos[i];
                            int type = (int)info.Type;

                            // WIA: 1 = ScannerDeviceType, 2 = CameraDeviceType, 3 = VideoDeviceType
                            if (type == 1)
                            {
                                string id = (string)info.DeviceID;
                                dynamic properties = info.Properties;

                                string name = GetWiaPropertyValue(properties, "Name") ?? "WIA Tarayıcı";
                                string desc = GetWiaPropertyValue(properties, "Description") ?? name;
                                string mfg = GetWiaPropertyValue(properties, "Manufacturer") ?? "Tarayıcı";

                                var dev = new DeviceInfo
                                {
                                    Id = $"WIA_{id.GetHashCode():X8}",
                                    Name = name,
                                    Manufacturer = mfg,
                                    ModelName = desc,
                                    Type = DeviceType.Scanner,
                                    ConnectionType = DeviceConnectionType.USB,
                                    SerialOrHardwareId = id,
                                    StatusMessage = "Taramaya Hazır (WIA)"
                                };

                                scanners.Add(dev);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverScanners WIA Error", ex);
            }

            // WMI üzerinden ek PnP tarayıcıları kontrol et (eğer WIA listesinde yoksa)
            try
            {
                using var pnpSearcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, Description, PNPClass, Status FROM Win32_PnPEntity WHERE " +
                    "PNPClass = 'Image' OR Description LIKE '%Scanner%' OR Description LIKE '%Tarayıcı%'");

                using var pnpCollection = pnpSearcher.Get();
                foreach (ManagementObject obj in pnpCollection)
                {
                    string name = obj["Name"]?.ToString() ?? obj["Description"]?.ToString() ?? "";
                    string deviceId = obj["DeviceID"]?.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(name) && !scanners.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        scanners.Add(new DeviceInfo
                        {
                            Id = $"PNP_{deviceId.GetHashCode():X8}",
                            Name = name,
                            Manufacturer = "Tarayıcı Aygıtı",
                            ModelName = name,
                            Type = DeviceType.Scanner,
                            ConnectionType = DeviceConnectionType.USB,
                            SerialOrHardwareId = deviceId,
                            StatusMessage = "Takılı Tarayıcı"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverScanners WMI Error", ex);
            }

            return scanners;
        }

        private static string? GetWiaPropertyValue(dynamic properties, string propName)
        {
            try
            {
                dynamic prop = properties[propName];
                return prop?.Value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Seçili tarayıcıdan belge tarama işlemini gerçekleştirir.
        /// Mock/sahte veri KULLANMAZ; gerçek tarayıcı ile iletişim kurar.
        /// Çift taraflı tarama ve boş sayfa otomatik silme ayarlarını uygular.
        /// </summary>
        public async Task<List<ScannedPageItem>> ScanDocumentsAsync(
            DeviceInfo scanner,
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            if (scanner == null)
                throw new ArgumentNullException(nameof(scanner), "Lütfen geçerli bir tarayıcı seçin.");

            progress?.Report($"{scanner.Name} üzerinden tarama başlatılıyor...");

            var scannedPages = new List<ScannedPageItem>();
            string sessionFolder = Path.Combine(ScanTempDir, $"Scan_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(sessionFolder);

            await Task.Run(() =>
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    throw new PlatformNotSupportedException("Tarama özelliği sadece Windows işletim sisteminde desteklenmektedir.");
                }

                PerformRealWiaScan(scanner, options, sessionFolder, scannedPages, progress, cancellationToken);
            }, cancellationToken);

            progress?.Report($"Tarama tamamlandı: {scannedPages.Count} sayfa aktarıldı.");
            return scannedPages;
        }

        private void PerformRealWiaScan(
            DeviceInfo scanner,
            ScanOptions options,
            string outputDir,
            List<ScannedPageItem> scannedPages,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            Type? deviceManagerType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (deviceManagerType == null)
            {
                throw new Exception("Windows Image Acquisition (WIA) servisi sistemde bulunamadı.");
            }

            dynamic? deviceManager = Activator.CreateInstance(deviceManagerType);
            if (deviceManager == null)
            {
                throw new Exception("WIA DeviceManager örneği oluşturulamadı.");
            }

            dynamic deviceInfos = deviceManager.DeviceInfos;
            dynamic? targetDeviceInfo = null;

            int count = (int)deviceInfos.Count;
            if (count == 0)
            {
                throw new Exception("Sistemde bağlı hiçbir tarayıcı bulunamadı.\nLütfen tarayıcının açık ve bilgisayara bağlı olduğundan emin olun.");
            }

            for (int i = 1; i <= count; i++)
            {
                dynamic info = deviceInfos[i];
                string id = (string)info.DeviceID;
                string name = GetWiaPropertyValue(info.Properties, "Name") ?? "";

                if (id == scanner.SerialOrHardwareId ||
                    name.Contains(scanner.Name, StringComparison.OrdinalIgnoreCase) ||
                    scanner.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    targetDeviceInfo = info;
                    break;
                }
            }

            // Eşleşme bulunamadıysa ilk uygun tarayıcıyı kullan
            if (targetDeviceInfo == null)
            {
                targetDeviceInfo = deviceInfos[1];
            }

            dynamic device;
            try
            {
                device = targetDeviceInfo.Connect();
            }
            catch (Exception ex)
            {
                throw new Exception($"Tarayıcıya bağlanılamadı ({scanner.Name}): {ex.Message}\nLütfen cihazın açık olduğunu ve başka bir program tarafından kullanılmadığını kontrol edin.", ex);
            }

            if (device.Items.Count == 0)
            {
                throw new Exception("Tarayıcı öğesi (WIA Item) bulunamadı.");
            }

            dynamic item = device.Items[1];

            // WIA Parametrelerini Yapılandır (DPI, Renk, Çift Taraflı / Duplex)
            ConfigureWiaProperties(item, device, options);

            int pageIndex = 1;
            int savedPageIndex = 1;
            bool hasMorePages = true;

            Type? commonDialogType = Type.GetTypeFromProgID("WIA.CommonDialog");

            while (hasMorePages && !cancellationToken.IsCancellationRequested)
            {
                progress?.Report($"Sayfa {pageIndex} taranıyor...");

                try
                {
                    dynamic? commonDialog = commonDialogType != null ? Activator.CreateInstance(commonDialogType) : null;
                    dynamic imageFile;

                    // wiaFormatPNG = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"
                    const string wiaFormatPNG = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";

                    if (commonDialog != null)
                    {
                        imageFile = commonDialog.ShowTransfer(item, wiaFormatPNG, false);
                    }
                    else
                    {
                        imageFile = item.Transfer(wiaFormatPNG);
                    }

                    if (imageFile != null)
                    {
                        string pagePath = Path.Combine(outputDir, $"page_{pageIndex:D4}.png");
                        if (File.Exists(pagePath)) File.Delete(pagePath);
                        imageFile.SaveFile(pagePath);

                        // Boş sayfa otomatik silme kontrolü
                        bool isBlank = false;
                        if (options.RemoveBlankPages)
                        {
                            isBlank = _blankDetector.IsImageBlank(pagePath, options.BlankPageThreshold);
                        }

                        if (isBlank)
                        {
                            progress?.Report($"Sayfa {pageIndex} boş olduğu için otomatik olarak atlandı.");
                            try { if (File.Exists(pagePath)) File.Delete(pagePath); } catch { }
                        }
                        else
                        {
                            var pageItem = LoadScannedPageItem(pagePath, savedPageIndex, options.Resolution);
                            scannedPages.Add(pageItem);
                            savedPageIndex++;
                        }

                        pageIndex++;

                        // Cam / Flatbed seçildiyse tek sayfada sonlandır
                        if (options.Source == ScanSource.Flatbed)
                        {
                            hasMorePages = false;
                        }
                    }
                    else
                    {
                        hasMorePages = false;
                    }
                }
                catch (COMException comEx)
                {
                    // 0x80210003 = WIA_ERROR_PAPER_EMPTY (ADF Besleyicide kağıt bitti)
                    if ((uint)comEx.ErrorCode == 0x80210003)
                    {
                        hasMorePages = false;
                    }
                    else
                    {
                        FileLogger.LogError($"WIA COM Transfer Error (page {pageIndex})", comEx);
                        hasMorePages = false;
                        if (scannedPages.Count == 0)
                        {
                            throw new Exception($"Tarama hatası (0x{comEx.ErrorCode:X}): {comEx.Message}", comEx);
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogError($"WIA Scan Exception (page {pageIndex})", ex);
                    hasMorePages = false;
                    if (scannedPages.Count == 0)
                    {
                        throw new Exception($"Tarama işlemi sırasında hata oluştu: {ex.Message}", ex);
                    }
                }
            }

            if (scannedPages.Count == 0 && !cancellationToken.IsCancellationRequested)
            {
                throw new Exception("Tarama tamamlandı ancak kaydedilebilir sayfa elde edilemedi (tüm sayfalar boş olabilir veya besleyicide kağıt bulunamadı).");
            }
        }

        private static void ConfigureWiaProperties(dynamic item, dynamic device, ScanOptions options)
        {
            try
            {
                // WIA Item Properties:
                // 6146 = Current Intent (1 = Color, 2 = Grayscale, 4 = Text/B&W)
                // 6147 = Horizontal Resolution (DPI)
                // 6148 = Vertical Resolution (DPI)
                int intent = options.ColorMode switch
                {
                    ScanColorMode.Color => 1,
                    ScanColorMode.Grayscale => 2,
                    ScanColorMode.BlackAndWhite => 4,
                    _ => 1
                };

                SetWiaProperty(item.Properties, 6146, intent);
                SetWiaProperty(item.Properties, 6147, options.Resolution);
                SetWiaProperty(item.Properties, 6148, options.Resolution);

                // Document Handling (Device Property 3088)
                // 1 = Feeder (ADF), 2 = Flatbed, 4 = Duplex (1 | 4 = 5 ADF Duplex)
                int handling;
                if (options.DuplexScan || options.Source == ScanSource.FeederDuplex)
                {
                    handling = 1 | 4; // ADF Duplex (Çift Taraflı)
                }
                else if (options.Source == ScanSource.FeederSingle)
                {
                    handling = 1; // ADF Single (Tek Taraflı)
                }
                else
                {
                    handling = 2; // Flatbed (Cam)
                }

                SetWiaProperty(device.Properties, 3088, handling);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ConfigureWiaProperties warning", ex);
            }
        }

        private static void SetWiaProperty(dynamic properties, int propId, object value)
        {
            try
            {
                dynamic prop = properties[propId.ToString()];
                if (prop != null)
                {
                    prop.Value = value;
                }
            }
            catch
            {
                // Desteklenmeyen veya salt-okunur özellik durumunda yoksay
            }
        }

        private static ScannedPageItem LoadScannedPageItem(string filePath, int pageNumber, int dpi)
        {
            var fileInfo = new FileInfo(filePath);
            BitmapSource? bitmap = null;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.DecodePixelWidth = 800; // Önizleme performansı
                bmp.EndInit();
                bmp.Freeze();
                bitmap = bmp;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("LoadScannedPageItem Bitmap Load", ex);
            }

            return new ScannedPageItem
            {
                PageNumber = pageNumber,
                FilePath = filePath,
                PreviewImage = bitmap,
                ResolutionDpi = dpi,
                FileSize = fileInfo.Exists ? fileInfo.Length : 0,
                Rotation = 0
            };
        }

        /// <summary>
        /// Taranan sayfaları PDF belgesi olarak kaydeder.
        /// </summary>
        public async Task<string> SaveScannedPagesToPdfAsync(
            List<ScannedPageItem> pages,
            string outputPath,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            return await Task.Run(() =>
            {
                if (pages.Count == 0)
                    throw new InvalidOperationException("Kaydedilecek taranmış sayfa bulunmuyor.");

                using var doc = new PdfDocument();
                doc.Info.Title = "DocMaster Pro — Taranan Belge";
                doc.Info.Creator = "DocMaster Pro";

                for (int i = 0; i < pages.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var p = pages[i];
                    if (!File.Exists(p.FilePath)) continue;

                    using var xImg = XImage.FromFile(p.FilePath);
                    var page = doc.AddPage();
                    page.Width = xImg.PointWidth;
                    page.Height = xImg.PointHeight;

                    // Sayfa döndürme
                    if (p.Rotation == 90 || p.Rotation == 270)
                    {
                        page.Orientation = PdfSharp.PageOrientation.Landscape;
                    }
                    else if (p.Rotation == 180)
                    {
                        page.Orientation = PdfSharp.PageOrientation.Portrait;
                    }

                    using var gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawImage(xImg, 0, 0, page.Width, page.Height);

                    int pct = (int)(((i + 1) / (double)pages.Count) * 100);
                    progress?.Report(pct);
                }

                doc.Save(outputPath);
                return outputPath;
            }, cancellationToken);
        }
    }
}
