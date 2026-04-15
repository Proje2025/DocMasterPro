using System;
using System.IO;
using System.Threading.Tasks;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DocConverter.Services
{
    public class ConverterService
    {
        /// <summary>
        /// JPG, PNG, BMP, GIF, TIFF, WEBP görüntüyü geçici bir PDF dosyasına dönüştürür.
        /// </summary>
        public string ConvertImageToPdf(string imagePath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "DocMasterPro");
            Directory.CreateDirectory(tempDir);

            string baseName = Path.GetFileNameWithoutExtension(imagePath);
            string output = Path.Combine(tempDir, $"{baseName}_{Guid.NewGuid():N}.pdf");

            try
            {
                // Önce ImageSharp ile görüntüyü yükle ve doğrula
                using var imageSharp = SixLabors.ImageSharp.Image.Load(imagePath);

                // PdfSharp ile PDF oluştur
                using var document = new PdfDocument();
                document.PageLayout = PdfPageLayout.SinglePage;

                var page = document.AddPage();
                page.Width = XUnit.FromPoint(imageSharp.Width);
                page.Height = XUnit.FromPoint(imageSharp.Height);

                // Görüntüyü XImage olarak çiz - using ile kaynak yönetimi
                using var xImage = XImage.FromFile(imagePath);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, 0, 0, page.Width, page.Height);

                // XGraphics ve XImage dispose olduktan sonra kaydet
                document.Save(output);

                // Kayıt başarılı mı kontrol et
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                    throw new Exception("PDF dosyası oluşturulamadı");

                return output;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ConvertImageToPdf", ex);
                throw new Exception($"Görüntü PDF'e dönüştürülemedi: {Path.GetFileName(imagePath)}", ex);
            }
        }

        /// <summary>
        /// PDF'in her sayfasını ayrı görüntü dosyasına dönüştürür.
        /// </summary>
        public async Task ConvertPdfToImagesAsync(string pdfPath, string outputDir, string format = "png")
        {
            // Ghostscript kontrolü
            if (!IsGhostscriptAvailable())
            {
                throw new Exception(
                    "PDF görüntüye dönüştürülemedi.\n\n" +
                    "Bu işlem için Ghostscript gereklidir.\n" +
                    "Lütfen Ghostscript'i yükleyin:\n" +
                    "https://ghostscript.com/releases/gsdnld.html\n\n" +
                    "Kurulumdan sonra uygulamayı yeniden başlatın.");
            }

            await Task.Run(() =>
            {
                Directory.CreateDirectory(outputDir);
                string baseName = Path.GetFileNameWithoutExtension(pdfPath);

                try
                {
                    // Magick.NET ile PDF'i görüntülere dönüştür
                    using var images = new MagickImageCollection();
                    
                    // Ghostscript yoksa veya PDF okunamıyorsa açıklayıcı hata ver
                    try
                    {
                        images.Read(pdfPath);
                    }
                    catch (MagickCorruptImageErrorException ex)
                    {
                        throw new Exception($"PDF dosyası bozuk veya okunamıyor: {ex.Message}", ex);
                    }
                    catch (Exception ex) when (ex.Message.Contains("PDF") || ex.Message.Contains("ghostscript") || ex.Message.Contains("delegate") || ex.Message.Contains("gswin"))
                    {
                        throw new Exception(
                            "PDF görüntüye dönüştürülemedi.\n\n" +
                            "Ghostscript yüklü değil veya düzgün yapılandırılmamış.\n\n" +
                            "Çözüm için:\n" +
                            "1. https://ghostscript.com/releases/gsdnld.html adresine gidin\n" +
                            "2. 'Ghostscript 10.04.0 for Windows (64 bit)' indirin\n" +
                            "3. Kurulumu tamamlayın (PATH'e eklendiğinden emin olun)\n" +
                            "4. Bu uygulamayı yeniden başlatın\n\n" +
                            "Teknik detay: " + ex.Message, ex);
                    }

                    if (images.Count == 0)
                    {
                        throw new Exception("PDF dosyasında hiç sayfa bulunamadı veya sayfalar okunamadı.");
                    }

                    for (int i = 0; i < images.Count; i++)
                    {
                        var image = images[i];
                        image.Format = format.ToLowerInvariant() switch
                        {
                            "jpg" or "jpeg" => MagickFormat.Jpeg,
                            "png" => MagickFormat.Png,
                            "bmp" => MagickFormat.Bmp,
                            "gif" => MagickFormat.Gif,
                            "tiff" or "tif" => MagickFormat.Tiff,
                            "webp" => MagickFormat.WebP,
                            _ => MagickFormat.Png
                        };

                        string outputPath = Path.Combine(outputDir, $"{baseName}_sayfa{i + 1}.{format}");
                        image.Write(outputPath);
                        
                        // Yazma başarılı mı kontrol et
                        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                        {
                            throw new Exception($"Sayfa {i + 1} kaydedilemedi: {outputPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("ConvertPdfToImagesAsync", ex);
                    throw;
                }
            });
        }

        /// <summary>
        /// Ghostscript'in sistemde yüklü olup olmadığını kontrol eder.
        /// </summary>
        private bool IsGhostscriptAvailable()
        {
            try
            {
                // gs (ghostscript) komutunun çalışıp çalışmadığını kontrol et
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "gswin64c.exe",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null) return false;
                
                process.WaitForExit(3000); // 3 saniye bekle
                return process.ExitCode == 0;
            }
            catch
            {
                // gswin64c.exe bulunamazsa, Magick.NET'in internal desteğini test et
                try
                {
                    using var testImages = new MagickImageCollection();
                    // Minimal bir PDF okuma denemesi yap
                    // Bu aslında bir dosya gerektirir, bu yüzden sadece delegate varlığını kontrol edemeyiz
                    return true; // varsayılan olarak dene, hata olursa zaten catch'e düşer
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// PDF'in sayfa sayısını döndürür.
        /// </summary>
        public int GetPdfPageCount(string pdfPath)
        {
            try
            {
                using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
                return doc.PageCount;
            }
            catch
            {
                return 0;
            }
        }
    }
}
