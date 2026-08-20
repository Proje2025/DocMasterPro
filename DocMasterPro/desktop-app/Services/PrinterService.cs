using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;
using PdfiumViewer.Core;
using PdfiumViewer.Enums;
using Image = System.Drawing.Image;

namespace DocConverter.Services
{
    public class PrintResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int PagesSent { get; set; }
        public string JobId { get; set; } = string.Empty;
    }

    public class PrinterService
    {
        private readonly OfficeConverterService _officeConverter = new();

        /// <summary>
        /// Seçili yazıcıya belge (PDF, Resim, Word/Office Dokümanı) yazdırır.
        /// Windows GDI Spooler ve PdfiumViewer render motorunu kullanarak PCL/EMF standartlarında temiz çıktı üretir.
        /// </summary>
        public async Task<PrintResult> PrintDocumentAsync(
            DeviceInfo printer,
            string filePath,
            PrintJobOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            if (printer == null)
            {
                return new PrintResult { Success = false, Message = "Lütfen bir hedef yazıcı seçin." };
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return new PrintResult { Success = false, Message = "Yazdırılacak dosya bulunamadı: " + filePath };
            }

            string actualPrinterName = ResolveWindowsPrinterName(printer);
            progress?.Report($"{actualPrinterName} yazıcısına gönderiliyor ({Path.GetFileName(filePath)})...");

            return await Task.Run(async () =>
            {
                try
                {
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();

                    // 1. Resim Dosyaları (.png, .jpg, .jpeg, .bmp, .tif, .tiff, .gif)
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif")
                    {
                        return PrintImageFile(actualPrinterName, filePath, options);
                    }

                    // 2. Office Dosyaları (.docx, .doc, .xlsx, .xls, .pptx, .ppt, .txt, .rtf)
                    if (ext is ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt" or ".txt" or ".rtf")
                    {
                        progress?.Report("Belge yazdırma için PDF formatına dönüştürülüyor...");
                        string tempPdf = Path.Combine(Path.GetTempPath(), $"DocMaster_Print_{Guid.NewGuid():N}.pdf");
                        try
                        {
                            if (ext is ".docx" or ".doc")
                                await _officeConverter.ConvertWordToPdfAsync(filePath, tempPdf, cancellationToken);
                            else if (ext is ".xlsx" or ".xls")
                                await _officeConverter.ConvertExcelToPdfAsync(filePath, tempPdf, cancellationToken);
                            else if (ext is ".pptx" or ".ppt")
                                await _officeConverter.ConvertPowerPointToPdfAsync(filePath, tempPdf, cancellationToken);
                            else
                                await _officeConverter.ConvertTxtToPdfAsync(filePath, tempPdf, cancellationToken);

                            if (File.Exists(tempPdf))
                            {
                                return PrintPdfFile(actualPrinterName, tempPdf, options, progress);
                            }
                        }
                        finally
                        {
                            try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
                        }
                    }

                    // 3. PDF Dosyaları (veya varsayılan PDF olarak işleme)
                    return PrintPdfFile(actualPrinterName, filePath, options, progress);
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("PrintDocumentAsync Exception", ex);
                    return new PrintResult
                    {
                        Success = false,
                        Message = $"Yazdırma hatası: {ex.Message}"
                    };
                }
            }, cancellationToken);
        }

        /// <summary>
        /// PDF dosyasını yüksek çözünürlüklü sayfa render ederek Windows GDI Yazdırma Kuyruğuna (Spooler) basar.
        /// </summary>
        public static PrintResult PrintPdfFile(
            string printerName,
            string pdfPath,
            PrintJobOptions options,
            IProgress<string>? progress = null)
        {
            try
            {
                using var pd = new PrintDocument();
                if (!string.IsNullOrEmpty(printerName))
                {
                    pd.PrinterSettings.PrinterName = printerName;
                }

                if (!pd.PrinterSettings.IsValid)
                {
                    return new PrintResult
                    {
                        Success = false,
                        Message = $"'{printerName}' yazıcısı Windows sisteminde geçerli bir yazıcı kuyruğu olarak bulunamadı. Lütfen Windows Aygıtlar ve Yazıcılar bölümünden yazıcınızın adını doğrulayın."
                    };
                }

                pd.PrinterSettings.Copies = (short)Math.Max(1, options.Copies);
                pd.DefaultPageSettings.Landscape = (options.Orientation == PrintOrientation.Landscape);

                // Çift taraflı (Duplex) yazdırma ayarları
                if (pd.PrinterSettings.CanDuplex)
                {
                    pd.PrinterSettings.Duplex = options.Duplex switch
                    {
                        PrintDuplex.DuplexShortEdge => Duplex.Horizontal,
                        PrintDuplex.DuplexLongEdge => Duplex.Vertical,
                        _ => Duplex.Simplex
                    };
                }

                using var pdfDoc = PdfDocument.Load(pdfPath);
                int totalPages = pdfDoc.PageCount;
                int currentPageIndex = 0;

                pd.PrintPage += (s, ev) =>
                {
                    if (ev.Graphics == null || currentPageIndex >= totalPages)
                    {
                        ev.HasMorePages = false;
                        return;
                    }

                    progress?.Report($"Sayfa {currentPageIndex + 1} / {totalPages} yazıcıya gönderiliyor...");

                    var printableArea = ev.MarginBounds;
                    float printDpi = 300f;

                    var pageSize = pdfDoc.PageSizes[currentPageIndex] is SizeF sz ? sz : new SizeF(595, 842);
                    int renderWidth = Math.Max(1, (int)Math.Round(pageSize.Width * (printDpi / 72.0f)));
                    int renderHeight = Math.Max(1, (int)Math.Round(pageSize.Height * (printDpi / 72.0f)));

                    using var pageImage = pdfDoc.Render(
                        currentPageIndex,
                        renderWidth,
                        renderHeight,
                        printDpi,
                        printDpi,
                        PdfRenderFlags.Annotations);

                    if (options.FitToPage)
                    {
                        float scale = Math.Min((float)printableArea.Width / pageImage.Width, (float)printableArea.Height / pageImage.Height);
                        int drawW = Math.Max(1, (int)(pageImage.Width * scale));
                        int drawH = Math.Max(1, (int)(pageImage.Height * scale));
                        int drawX = printableArea.Left + (printableArea.Width - drawW) / 2;
                        int drawY = printableArea.Top + (printableArea.Height - drawH) / 2;
                        ev.Graphics.DrawImage(pageImage, drawX, drawY, drawW, drawH);
                    }
                    else
                    {
                        ev.Graphics.DrawImage(pageImage, printableArea.Left, printableArea.Top);
                    }

                    currentPageIndex++;
                    ev.HasMorePages = (currentPageIndex < totalPages);
                };

                pd.Print();

                return new PrintResult
                {
                    Success = true,
                    PagesSent = totalPages,
                    Message = $"{Path.GetFileName(pdfPath)} ({totalPages} sayfa) başarıyla {printerName} yazıcısına gönderildi."
                };
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintPdfFile Error", ex);
                return new PrintResult
                {
                    Success = false,
                    Message = $"PDF yazdırma hatası: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Resim dosyasını PrintDocument ile temiz basar
        /// </summary>
        public static PrintResult PrintImageFile(string printerName, string imagePath, PrintJobOptions options)
        {
            try
            {
                using var pd = new PrintDocument();
                if (!string.IsNullOrEmpty(printerName))
                {
                    pd.PrinterSettings.PrinterName = printerName;
                }

                if (!pd.PrinterSettings.IsValid)
                {
                    return new PrintResult
                    {
                        Success = false,
                        Message = $"'{printerName}' yazıcısı Windows sisteminde bulunamadı."
                    };
                }

                pd.PrinterSettings.Copies = (short)Math.Max(1, options.Copies);
                pd.DefaultPageSettings.Landscape = (options.Orientation == PrintOrientation.Landscape);

                if (pd.PrinterSettings.CanDuplex)
                {
                    pd.PrinterSettings.Duplex = options.Duplex switch
                    {
                        PrintDuplex.DuplexShortEdge => Duplex.Horizontal,
                        PrintDuplex.DuplexLongEdge => Duplex.Vertical,
                        _ => Duplex.Simplex
                    };
                }

                pd.PrintPage += (s, ev) =>
                {
                    if (ev.Graphics == null) return;

                    using var img = Image.FromFile(imagePath);
                    var marginBounds = ev.MarginBounds;

                    if (options.FitToPage)
                    {
                        float scale = Math.Min((float)marginBounds.Width / img.Width, (float)marginBounds.Height / img.Height);
                        int w = Math.Max(1, (int)(img.Width * scale));
                        int h = Math.Max(1, (int)(img.Height * scale));
                        int x = marginBounds.Left + (marginBounds.Width - w) / 2;
                        int y = marginBounds.Top + (marginBounds.Height - h) / 2;
                        ev.Graphics.DrawImage(img, x, y, w, h);
                    }
                    else
                    {
                        ev.Graphics.DrawImage(img, marginBounds.Left, marginBounds.Top);
                    }

                    ev.HasMorePages = false;
                };

                pd.Print();

                return new PrintResult
                {
                    Success = true,
                    PagesSent = 1,
                    Message = $"{Path.GetFileName(imagePath)} yazıcıya başarıyla gönderildi."
                };
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintImageFile Error", ex);
                return new PrintResult { Success = false, Message = $"Resim yazdırma hatası: {ex.Message}" };
            }
        }

        /// <summary>
        /// Yazıcıya profesyonel test sayfası gönderir
        /// </summary>
        public async Task<PrintResult> PrintTestPageAsync(
            DeviceInfo printer,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            progress?.Report($"{printer.Name} için test sayfası oluşturuluyor...");

            string tempTestImage = Path.Combine(Path.GetTempPath(), $"DocMaster_TestPage_{DateTime.Now:yyyyMMddHHmmss}.png");

            await Task.Run(() =>
            {
                using var bmp = new Bitmap(1600, 2200); // Yüksek çözünürlüklü A4
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.White);

                using var borderPen = new Pen(Color.FromArgb(37, 99, 235), 4);
                g.DrawRectangle(borderPen, 30, 30, 1540, 2140);

                using var headerBrush = new SolidBrush(Color.FromArgb(16, 24, 39));
                using var titleFont = new Font("Segoe UI", 24, FontStyle.Bold);
                using var subTitleFont = new Font("Segoe UI", 15, FontStyle.Bold);
                using var textFont = new Font("Segoe UI", 12);
                using var smallFont = new Font("Segoe UI", 10);

                g.DrawString("DOCMASTER PRO — YAZICI TEST SAYFASI", titleFont, headerBrush, 60, 60);
                g.DrawString($"Cihaz: {printer.Name}", subTitleFont, headerBrush, 60, 120);

                using var grayPen = new Pen(Color.LightGray, 1);
                g.DrawLine(grayPen, 60, 160, 1500, 160);

                int y = 180;
                void DrawLine(string label, string val)
                {
                    using var b1 = new SolidBrush(Color.FromArgb(70, 70, 70));
                    using var b2 = new SolidBrush(Color.FromArgb(10, 10, 10));
                    g.DrawString(label, subTitleFont, b1, 60, y);
                    g.DrawString(val, subTitleFont, b2, 350, y);
                    y += 45;
                }

                DrawLine("Model Adı:", printer.ModelName);
                DrawLine("Üretici:", printer.Manufacturer);
                DrawLine("Bağlantı Türü:", printer.ConnectionDescription);
                if (!string.IsNullOrEmpty(printer.IpAddress))
                    DrawLine("IP Adresi & Port:", $"{printer.IpAddress}:{printer.Port}");
                DrawLine("Sürücü Durumu:", printer.DriverStatusDescription);
                DrawLine("Çift Taraflı Yazdırma:", "Destekleniyor (Aktif)");
                DrawLine("Test Tarihi:", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

                y += 20;
                g.DrawLine(grayPen, 60, y, 1500, y);
                y += 30;

                g.DrawString("Yazdırma Kalitesi ve Gradyan Testi:", textFont, headerBrush, 60, y);
                y += 35;

                // Renk / Gri tonlama şeritleri
                for (int i = 0; i <= 10; i++)
                {
                    int grayVal = (int)(i * 25.5);
                    using var brush = new SolidBrush(Color.FromArgb(grayVal, grayVal, grayVal));
                    g.FillRectangle(brush, 60 + (i * 125), y, 120, 60);
                    g.DrawRectangle(borderPen, 60 + (i * 125), y, 120, 60);
                }

                y += 90;
                g.DrawString("Metin Netlik Testi (Farklı Font Boyutları):", textFont, headerBrush, 60, y);
                y += 30;

                int[] sizes = { 8, 10, 12, 14, 18, 22 };
                foreach (int s in sizes)
                {
                    using var f = new Font("Arial", s);
                    g.DrawString($"{s}pt: DocMaster Pro Ricoh SP 4510SF & Fujitsu fi-6230 Hızlı Belge ve Yazdırma Sistemi", f, headerBrush, 60, y);
                    y += s + 14;
                }

                g.DrawString("Bu test sayfası DocMaster Pro tarafından başarıyla üretilmiş ve yazdırılmıştır.", smallFont, Brushes.Gray, 60, 2100);

                bmp.Save(tempTestImage, System.Drawing.Imaging.ImageFormat.Png);
            }, cancellationToken);

            var options = new PrintJobOptions { Copies = 1, Orientation = PrintOrientation.Portrait, FitToPage = true };
            return await PrintDocumentAsync(printer, tempTestImage, options, cancellationToken, progress);
        }

        /// <summary>
        /// DeviceInfo nesnesine karşılık gelen geçerli Windows Spooler Yazıcı Adını bulur.
        /// </summary>
        public static string ResolveWindowsPrinterName(DeviceInfo printer)
        {
            if (printer == null) return string.Empty;

            var installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();

            // 1. Doğrudan kurulu yazıcı adı ile birebir eşleşme
            var exact = installed.FirstOrDefault(p => p.Equals(printer.Name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(exact)) return exact;

            // 2. IP adresine göre kurulu yazıcı portlarından eşleşme
            if (!string.IsNullOrEmpty(printer.IpAddress) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, PortName FROM Win32_Printer");
                    using var collection = searcher.Get();
                    foreach (ManagementObject p in collection)
                    {
                        string pName = p["Name"]?.ToString() ?? "";
                        string port = p["PortName"]?.ToString() ?? "";
                        if (port.Contains(printer.IpAddress, StringComparison.OrdinalIgnoreCase) ||
                            pName.Contains(printer.IpAddress, StringComparison.OrdinalIgnoreCase))
                        {
                            return pName;
                        }
                    }
                }
                catch
                {
                    // Fallback
                }
            }

            // 3. Ricoh veya özel model adına göre eşleşme
            if (printer.IsRicohSpecial || printer.Name.Contains("Ricoh", StringComparison.OrdinalIgnoreCase))
            {
                var ricohMatch = installed.FirstOrDefault(p =>
                    p.Contains("Ricoh", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("4510", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Aficio", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(ricohMatch)) return ricohMatch;
            }

            // 4. İçeren isim araması
            var partial = installed.FirstOrDefault(p => p.Contains(printer.Name, StringComparison.OrdinalIgnoreCase) ||
                                                        (!string.IsNullOrEmpty(printer.ModelName) && p.Contains(printer.ModelName, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrEmpty(partial)) return partial;

            // 5. Varsayılan sistem yazıcısı
            try
            {
                var settings = new PrinterSettings();
                if (!string.IsNullOrEmpty(settings.PrinterName) && installed.Contains(settings.PrinterName))
                    return settings.PrinterName;
            }
            catch { }

            // 6. İlk kurulu yazıcı veya printer.Name
            return installed.FirstOrDefault() ?? printer.Name;
        }
    }
}
