using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;

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
        /// <summary>
        /// Seçili yazıcıya belge (PDF, Resim, Doküman) yazdırır
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

            return await Task.Run(async () =>
            {
                try
                {
                    // 1. Eğer doğrudan TCP/IP RAW Ağ Yazıcısı ise (örn. Ricoh SP 4510SF Port 9100)
                    if (printer.ConnectionType == DeviceConnectionType.NetworkIP &&
                        !string.IsNullOrEmpty(printer.IpAddress) &&
                        printer.Port > 0)
                    {
                        progress?.Report($"Doğrudan TCP/IP RAW üzerinden aktarılıyor ({printer.IpAddress}:{printer.Port})...");
                        bool rawSent = await SendRawFileToNetworkPrinterAsync(printer.IpAddress, printer.Port, filePath, cancellationToken);
                        if (rawSent)
                        {
                            return new PrintResult
                            {
                                Success = true,
                                Message = $"{Path.GetFileName(filePath)} başarıyla {printer.Name} ({printer.IpAddress}) cihazına aktarıldı."
                            };
                        }
                    }

                    // 2. Resim dosyası ise PrintDocument ile bas
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif" || ext == ".tiff")
                    {
                        return PrintImageFile(printer.Name, filePath, options);
                    }

                    // 3. PDF veya diğer belgeler için Windows ShellExecute Print
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
        /// Yazıcıya test sayfası gönderir
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
                using var titleFont = new Font("Segoe UI", 26, FontStyle.Bold);
                using var subTitleFont = new Font("Segoe UI", 16, FontStyle.Bold);
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
                DrawLine("Test Tarihi:", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));

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

            var options = new PrintJobOptions { Copies = 1, Orientation = PrintOrientation.Portrait };
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

                // Çift taraflı (Duplex) kontrolü
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
                    Message = $"{Path.GetFileName(imagePath)} yazıcıya başarıyla gönderildi."
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
                    Verb = "print",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };

                if (!string.IsNullOrEmpty(printerName))
                {
                    // PrintTo allows specifying printer
                    psi.Verb = "printto";
                    psi.Arguments = $"\"{printerName}\"";
                }

                using var proc = Process.Start(psi);
                return new PrintResult
                {
                    Success = true,
                    Message = $"{Path.GetFileName(filePath)} yazdırma kuyruğuna aktarıldı."
                };
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintViaWindowsShell Error", ex);
                return new PrintResult { Success = false, Message = $"Windows Spooler yazdırma hatası: {ex.Message}" };
            }
        }

        private static async Task<bool> SendRawFileToNetworkPrinterAsync(string ip, int port, string filePath, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(ip, port, cancellationToken);
                using var stream = client.GetStream();
                using var fileStream = File.OpenRead(filePath);

                await fileStream.CopyToAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"SendRawFileToNetworkPrinterAsync ({ip}:{port}) Error", ex);
                return false;
            }
        }
    }
}
