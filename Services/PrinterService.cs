using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;

namespace DocConverter.Services
{
    public class PrintResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class PrinterService
    {
        /// <summary>
        /// Sistemde kurulu tüm yerel, ağ ve sanal yazıcıları listeler.
        /// </summary>
        public List<DeviceInfo> DiscoverPrinters()
        {
            var printers = new List<DeviceInfo>();
            string defaultPrinterName = string.Empty;

            try
            {
                var settings = new PrinterSettings();
                defaultPrinterName = settings.PrinterName ?? string.Empty;
            }
            catch
            {
                // default printer not set
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return printers;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
                using var collection = searcher.Get();

                foreach (ManagementObject printer in collection)
                {
                    string name = printer["Name"]?.ToString() ?? "Bilinmeyen Yazıcı";
                    string portName = printer["PortName"]?.ToString() ?? "";
                    string driverName = printer["DriverName"]?.ToString() ?? "";
                    bool isNetwork = (bool?)printer["Network"] ?? false;
                    bool isDefault = (bool?)printer["Default"] ?? (name.Equals(defaultPrinterName, StringComparison.OrdinalIgnoreCase));

                    var connType = DeviceConnectionType.USB;
                    string ip = "";

                    if (portName.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
                        portName.StartsWith("192.", StringComparison.OrdinalIgnoreCase) ||
                        portName.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
                        portName.StartsWith("172.", StringComparison.OrdinalIgnoreCase))
                    {
                        connType = DeviceConnectionType.NetworkIP;
                        ip = portName.Replace("IP_", "", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (name.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("XPS", StringComparison.OrdinalIgnoreCase) ||
                             portName.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase))
                    {
                        connType = DeviceConnectionType.Virtual;
                    }
                    else if (isNetwork || portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
                    {
                        connType = DeviceConnectionType.SharedWsd;
                    }

                    bool canDuplex = true;
                    try
                    {
                        var ps = new PrinterSettings { PrinterName = name };
                        canDuplex = ps.CanDuplex;
                    }
                    catch { }

                    printers.Add(new DeviceInfo
                    {
                        Id = $"PRN_{name.GetHashCode():X8}",
                        Name = name,
                        DriverName = driverName,
                        Type = DeviceType.Printer,
                        ConnectionType = connType,
                        IpAddress = ip,
                        IsDefault = isDefault,
                        SupportsDuplex = canDuplex,
                        IsOnline = true,
                        StatusMessage = "Hazır",
                        Manufacturer = GetManufacturerFromName(name)
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DiscoverPrinters WMI Error, falling back to PrinterSettings", ex);
                try
                {
                    foreach (string pName in PrinterSettings.InstalledPrinters)
                    {
                        bool canDuplex = true;
                        try
                        {
                            var ps = new PrinterSettings { PrinterName = pName };
                            canDuplex = ps.CanDuplex;
                        }
                        catch { }

                        printers.Add(new DeviceInfo
                        {
                            Id = $"PRN_{pName.GetHashCode():X8}",
                            Name = pName,
                            Type = DeviceType.Printer,
                            ConnectionType = DeviceConnectionType.USB,
                            IsDefault = pName.Equals(defaultPrinterName, StringComparison.OrdinalIgnoreCase),
                            SupportsDuplex = canDuplex,
                            IsOnline = true,
                            StatusMessage = "Hazır",
                            Manufacturer = GetManufacturerFromName(pName)
                        });
                    }
                }
                catch
                {
                    // Fallback
                }
            }

            return printers;
        }

        private static string GetManufacturerFromName(string name)
        {
            if (name.Contains("Ricoh", StringComparison.OrdinalIgnoreCase)) return "Ricoh";
            if (name.Contains("Fujitsu", StringComparison.OrdinalIgnoreCase)) return "Fujitsu";
            if (name.Contains("HP", StringComparison.OrdinalIgnoreCase) || name.Contains("Hewlett", StringComparison.OrdinalIgnoreCase)) return "HP";
            if (name.Contains("Canon", StringComparison.OrdinalIgnoreCase)) return "Canon";
            if (name.Contains("Epson", StringComparison.OrdinalIgnoreCase)) return "Epson";
            if (name.Contains("Brother", StringComparison.OrdinalIgnoreCase)) return "Brother";
            if (name.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung";
            if (name.Contains("Xerox", StringComparison.OrdinalIgnoreCase)) return "Xerox";
            if (name.Contains("Kyocera", StringComparison.OrdinalIgnoreCase)) return "Kyocera";
            if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
            if (name.Contains("Adobe", StringComparison.OrdinalIgnoreCase)) return "Adobe";
            return "Yazıcı";
        }

        /// <summary>
        /// Belirtilen dosyayı seçili yazıcıya gönderir.
        /// </summary>
        public async Task<PrintResult> PrintDocumentAsync(
            DeviceInfo printer,
            string filePath,
            PrintJobOptions options,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            if (!File.Exists(filePath))
            {
                return new PrintResult { Success = false, Message = "Yazdırılacak dosya bulunamadı." };
            }

            progress?.Report($"{printer.Name} cihazına gönderiliyor ({Path.GetFileName(filePath)})...");

            return await Task.Run(() =>
            {
                try
                {
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();

                    // Resim dosyası ise PrintDocument ile bas
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif")
                    {
                        return PrintImageFile(printer.Name, filePath, options);
                    }

                    // PDF veya diğer dosyalar için Windows Spooler / Shell
                    return PrintViaWindowsShell(printer.Name, filePath);
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
        /// Yazıcıya resmi test sayfası gönderir.
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
                using var bmp = new Bitmap(1600, 2200);
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.White);

                using var borderPen = new Pen(Color.FromArgb(40, 120, 220), 4);
                g.DrawRectangle(borderPen, 30, 30, 1540, 2140);

                using var headerBrush = new SolidBrush(Color.FromArgb(20, 30, 50));
                using var titleFont = new Font("Segoe UI", 24, FontStyle.Bold);
                using var subTitleFont = new Font("Segoe UI", 14, FontStyle.Bold);
                using var textFont = new Font("Segoe UI", 12);
                using var smallFont = new Font("Segoe UI", 10);

                g.DrawString("DOCMASTER PRO — YAZICI TEST SAYFASI", titleFont, headerBrush, 60, 60);
                g.DrawString($"Cihaz: {printer.Name}", subTitleFont, headerBrush, 60, 115);

                using var grayPen = new Pen(Color.LightGray, 1);
                g.DrawLine(grayPen, 60, 155, 1500, 155);

                int y = 180;
                void DrawRow(string label, string val)
                {
                    using var b1 = new SolidBrush(Color.FromArgb(90, 90, 90));
                    using var b2 = new SolidBrush(Color.FromArgb(10, 10, 10));
                    g.DrawString(label, subTitleFont, b1, 60, y);
                    g.DrawString(val, subTitleFont, b2, 350, y);
                    y += 45;
                }

                DrawRow("Yazıcı Adı:", printer.Name);
                DrawRow("Üretici:", printer.Manufacturer);
                DrawRow("Bağlantı Türü:", printer.ConnectionType.ToString());
                if (!string.IsNullOrEmpty(printer.IpAddress))
                    DrawRow("IP Adresi:", printer.IpAddress);
                DrawRow("Çift Taraflı Yazdırma (Duplex):", printer.SupportsDuplex ? "Destekleniyor (Aktif)" : "Desteklenmiyor");
                DrawRow("Test Tarihi:", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));

                y += 20;
                g.DrawLine(grayPen, 60, y, 1500, y);
                y += 30;

                g.DrawString("Gri Tonlama ve Gradyan Kalite Testi:", textFont, headerBrush, 60, y);
                y += 35;

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
                    using var f = new Font("Segoe UI", s);
                    g.DrawString($"{s}pt: DocMaster Pro — Hızlı ve Güvenilir Belge Dönüştürme & Yazdırma Sistemi", f, headerBrush, 60, y);
                    y += s + 14;
                }

                g.DrawString("Bu test sayfası DocMaster Pro tarafından başarıyla oluşturulmuş ve yazdırılmıştır.", smallFont, Brushes.Gray, 60, 2100);

                bmp.Save(tempTestImage, System.Drawing.Imaging.ImageFormat.Png);
            }, cancellationToken);

            var options = new PrintJobOptions { Copies = 1, Orientation = PrintOrientation.Portrait, FitToPage = true };
            return await PrintDocumentAsync(printer, tempTestImage, options, cancellationToken, progress);
        }

        private static PrintResult PrintImageFile(string printerName, string imagePath, PrintJobOptions options)
        {
            try
            {
                using var pd = new PrintDocument();
                if (!string.IsNullOrEmpty(printerName))
                {
                    pd.PrinterSettings.PrinterName = printerName;
                }

                pd.PrinterSettings.Copies = (short)Math.Max(1, options.Copies);
                pd.DefaultPageSettings.Landscape = (options.Orientation == PrintOrientation.Landscape);

                // Çift taraflı yazdırma (Duplex) ayarı
                if (options.DuplexPrint && pd.PrinterSettings.CanDuplex)
                {
                    pd.PrinterSettings.Duplex = options.DuplexMode.Contains("Kısa", StringComparison.OrdinalIgnoreCase)
                        ? Duplex.Horizontal
                        : Duplex.Vertical;
                }
                else
                {
                    pd.PrinterSettings.Duplex = Duplex.Simplex;
                }

                pd.PrintPage += (s, ev) =>
                {
                    if (ev.Graphics == null) return;

                    using var img = Image.FromFile(imagePath);
                    var marginBounds = ev.MarginBounds;

                    if (options.FitToPage)
                    {
                        float scale = Math.Min((float)marginBounds.Width / img.Width, (float)marginBounds.Height / img.Height);
                        int w = (int)(img.Width * scale);
                        int h = (int)(img.Height * scale);
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
                    Message = $"{Path.GetFileName(imagePath)} yazıcıya ({printerName}) başarıyla gönderildi."
                };
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintImageFile Error", ex);
                return new PrintResult { Success = false, Message = $"Resim yazdırma hatası: {ex.Message}" };
            }
        }

        private static PrintResult PrintViaWindowsShell(string printerName, string filePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    Verb = "printto",
                    Arguments = $"\"{printerName}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };

                using var proc = Process.Start(psi);
                return new PrintResult
                {
                    Success = true,
                    Message = $"{Path.GetFileName(filePath)} yazıcıya ({printerName}) başarıyla gönderildi."
                };
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintViaWindowsShell Error", ex);
                return new PrintResult { Success = false, Message = $"Windows Spooler yazdırma hatası: {ex.Message}" };
            }
        }
    }
}
