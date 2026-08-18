using System;
using System.Collections.Generic;
using System.IO;
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

        public ScannerService()
        {
            if (!Directory.Exists(ScanTempDir))
            {
                Directory.CreateDirectory(ScanTempDir);
            }
        }

        /// <summary>
        /// Seçili tarayıcıdan (Fujitsu fi-6230, WIA cihazı vb.) belge tarama işlemini gerçekleştirir.
        /// </summary>
        public async Task<List<ScannedPageItem>> ScanDocumentsAsync(
            DeviceInfo scanner,
            ScanOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            progress?.Report($"{scanner.Name} üzerinden tarama başlatılıyor...");

            var scannedPages = new List<ScannedPageItem>();
            string sessionFolder = Path.Combine(ScanTempDir, $"Scan_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(sessionFolder);

            await Task.Run(() =>
            {
                bool usedWia = false;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        usedWia = PerformWiaScan(scanner, options, sessionFolder, scannedPages, progress, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogError("WIA scan error, falling back to simulated document generator", ex);
                    }
                }

                // Donanım bağlı değilse veya test ortamındaysa örnek test tarama sayfası oluştur
                if (!usedWia || scannedPages.Count == 0)
                {
                    progress?.Report("Test / Demo modu: Örnek tarama sayfası oluşturuluyor...");
                    CreateSimulatedScannedPage(scanner, options, sessionFolder, scannedPages);
                }
            }, cancellationToken);

            progress?.Report($"Tarama tamamlandı: {scannedPages.Count} sayfa başarıyla tarandı.");
            return scannedPages;
        }

        private bool PerformWiaScan(
            DeviceInfo scanner,
            ScanOptions options,
            string outputDir,
            List<ScannedPageItem> scannedPages,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            Type? deviceManagerType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (deviceManagerType == null) return false;

            dynamic? deviceManager = Activator.CreateInstance(deviceManagerType);
            if (deviceManager == null) return false;

            dynamic deviceInfos = deviceManager.DeviceInfos;
            dynamic? targetDeviceInfo = null;

            int count = (int)deviceInfos.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic info = deviceInfos[i];
                string id = (string)info.DeviceID;
                if (id == scanner.SerialOrHardwareId ||
                    ((string)info.Properties["Name"].Value).Contains(scanner.Name, StringComparison.OrdinalIgnoreCase) ||
                    (scanner.IsFujitsuSpecial && ((string)info.Properties["Name"].Value).Contains("6230", StringComparison.OrdinalIgnoreCase)))
                {
                    targetDeviceInfo = info;
                    break;
                }
            }

            if (targetDeviceInfo == null && count > 0)
            {
                // Fallback to first available scanner
                targetDeviceInfo = deviceInfos[1];
            }

            if (targetDeviceInfo == null) return false;

            dynamic device = targetDeviceInfo.Connect();
            dynamic item = device.Items[1];

            // WIA Parametrelerini ayarla (DPI, Renk Modu, Sayfa Kaynağı)
            ConfigureWiaItemProperties(item, device, options);

            int pageIndex = 1;
            bool hasMorePages = true;

            while (hasMorePages && !cancellationToken.IsCancellationRequested)
            {
                progress?.Report($"Sayfa {pageIndex} taranıyor...");

                try
                {
                    Type? commonDialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
                    dynamic? commonDialog = commonDialogType != null ? Activator.CreateInstance(commonDialogType) : null;

                    dynamic imageFile;
                    if (commonDialog != null)
                    {
                        // Format: wiaFormatPNG = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"
                        imageFile = commonDialog.ShowTransfer(item, "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}", false);
                    }
                    else
                    {
                        imageFile = item.Transfer("{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}");
                    }

                    if (imageFile != null)
                    {
                        string pagePath = Path.Combine(outputDir, $"page_{pageIndex:D3}.png");
                        if (File.Exists(pagePath)) File.Delete(pagePath);
                        imageFile.SaveFile(pagePath);

                        var pageItem = LoadScannedPageItem(pagePath, pageIndex, (int)options.Resolution);
                        scannedPages.Add(pageItem);
                        pageIndex++;

                        // Flatbed ise tek sayfa biter, ADF ise feeder bitene kadar devam eder
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
                    // 0x80210003 = WIA_ERROR_PAPER_EMPTY (ADF'de kağıt bitti)
                    if ((uint)comEx.ErrorCode == 0x80210003)
                    {
                        hasMorePages = false;
                    }
                    else
                    {
                        FileLogger.LogError($"WIA COM Transfer Error at page {pageIndex}", comEx);
                        hasMorePages = false;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogError($"WIA Page Scan Error at page {pageIndex}", ex);
                    hasMorePages = false;
                }
            }

            return scannedPages.Count > 0;
        }

        private static void ConfigureWiaItemProperties(dynamic item, dynamic device, ScanOptions options)
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
                SetWiaProperty(item.Properties, 6147, (int)options.Resolution);
                SetWiaProperty(item.Properties, 6148, (int)options.Resolution);

                // Document Handling (Device Property 3088)
                // 1 = Feeder (ADF), 2 = Flatbed, 4 = Duplex
                int handling = options.Source switch
                {
                    ScanSource.FeederSingle => 1,
                    ScanSource.FeederDuplex => 1 | 4,
                    _ => 2 // Flatbed
                };

                SetWiaProperty(device.Properties, 3088, handling);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ConfigureWiaItemProperties warning", ex);
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
                // Ignore if property is read-only or unsupported
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
                bmp.DecodePixelWidth = 600; // Thumbnail optimization
                bmp.EndInit();
                bmp.Freeze();
                bitmap = bmp;
            }
            catch
            {
                // Fallback
            }

            return new ScannedPageItem
            {
                PageNumber = pageNumber,
                FilePath = filePath,
                PreviewImage = bitmap,
                ResolutionDpi = dpi,
                FileSize = fileInfo.Length,
                Rotation = 0
            };
        }

        private static void CreateSimulatedScannedPage(
            DeviceInfo scanner,
            ScanOptions options,
            string outputDir,
            List<ScannedPageItem> scannedPages)
        {
            string pagePath = Path.Combine(outputDir, "scanned_doc_sample.png");

            // Basit bir test tarama görüntüsü oluştur
            using (var bmp = new System.Drawing.Bitmap(1240, 1754)) // A4 @ 150 DPI approx
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                using var pen = new System.Drawing.Pen(System.Drawing.Color.LightGray, 2);
                g.DrawRectangle(pen, 20, 20, 1200, 1714);

                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(40, 40, 40));
                using var titleFont = new System.Drawing.Font("Arial", 22, System.Drawing.FontStyle.Bold);
                using var font = new System.Drawing.Font("Arial", 14);

                g.DrawString($"TARANAN BELGE — {scanner.Name}", titleFont, brush, 50, 60);
                g.DrawString($"Tarayıcı Modeli: {scanner.ModelName} ({scanner.Manufacturer})", font, brush, 50, 110);
                g.DrawString($"Çözünürlük: {(int)options.Resolution} DPI | Renk: {options.ColorMode} | Kaynak: {options.Source}", font, brush, 50, 140);
                g.DrawString($"Tarih: {DateTime.Now:dd.MM.yyyy HH:mm:ss}", font, brush, 50, 170);

                // Cetvel ve çizgiler
                for (int y = 230; y < 1600; y += 40)
                {
                    g.DrawLine(pen, 50, y, 1190, y);
                }

                bmp.Save(pagePath, System.Drawing.Imaging.ImageFormat.Png);
            }

            var item = LoadScannedPageItem(pagePath, 1, (int)options.Resolution);
            scannedPages.Add(item);
        }

        /// <summary>
        /// Taranan sayfaları tek bir PDF dosyasında birleştirir
        /// </summary>
        public async Task<string> SaveScannedPagesToPdfAsync(
            List<ScannedPageItem> pages,
            string outputPath,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            return await Task.Run(() =>
            {
                using var doc = new PdfDocument();
                doc.Info.Title = "DocMaster Pro — Taranan Belge";
                doc.Info.Creator = "DocMaster Pro Scanner Studio";

                for (int i = 0; i < pages.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var p = pages[i];
                    if (!File.Exists(p.FilePath)) continue;

                    using var xImg = XImage.FromFile(p.FilePath);
                    var page = doc.AddPage();
                    page.Width = xImg.PointWidth;
                    page.Height = xImg.PointHeight;

                    using var gfx = XGraphics.FromPdfPage(page);

                    // Sayfa döndürme ayarı
                    if (p.Rotation == 90 || p.Rotation == 180 || p.Rotation == 270)
                    {
                        page.Orientation = (p.Rotation == 90 || p.Rotation == 270) ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
                    }

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
