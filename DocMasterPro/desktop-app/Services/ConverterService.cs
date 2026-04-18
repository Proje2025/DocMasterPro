using System;
using System.IO;
using System.Threading;
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
    /// <summary>
    /// Görüntü ve PDF dönüştürme işlemleri için performans optimize edilmiş servis.
    /// Paralel işleme, CancellationToken ve memory pooling desteği sunar.
    /// </summary>
    public class ConverterService
    {
        private static readonly int MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1);

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
                using var imageSharp = SixLabors.ImageSharp.Image.Load(imagePath);

                using var document = new PdfDocument();
                document.PageLayout = PdfPageLayout.SinglePage;

                var page = document.AddPage();
                page.Width = XUnit.FromPoint(imageSharp.Width);
                page.Height = XUnit.FromPoint(imageSharp.Height);

                using var xImage = XImage.FromFile(imagePath);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);

                document.Save(output);

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
        /// Paralel işleme ve CancellationToken desteği sunar.
        /// </summary>
        public async Task ConvertPdfToImagesAsync(
            string pdfPath,
            string outputDir,
            string format = "png",
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            if (!IsGhostscriptAvailable())
            {
                throw new Exception(
                    "PDF görüntüye dönüştürülemedi.\n\n" +
                    "Bu işlem için Ghostscript gereklidir.\n" +
                    "Lütfen Ghostscript'i yükleyin:\n" +
                    "https://ghostscript.com/releases/gsdnld.html\n\n" +
                    "Kurulumdan sonra uygulamayı yeniden başlatın.");
            }

            Directory.CreateDirectory(outputDir);
            string baseName = Path.GetFileNameWithoutExtension(pdfPath);

            await Task.Run(() =>
            {
                using var images = new MagickImageCollection();

                try
                {
                    images.Read(pdfPath);
                }
                catch (MagickCorruptImageErrorException ex)
                {
                    throw new Exception($"PDF dosyası bozuk veya okunamıyor: {ex.Message}", ex);
                }
                catch (Exception ex) when (ex.Message.Contains("PDF") || ex.Message.Contains("ghostscript") ||
                                           ex.Message.Contains("delegate") || ex.Message.Contains("gswin"))
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

                int completed = 0;
                int total = images.Count;
                var lockObj = new object();

                Parallel.For(
                    0,
                    total,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                        CancellationToken = cancellationToken
                    },
                    index =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var image = images[index];

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

                        string outputPath = Path.Combine(outputDir, $"{baseName}_sayfa{index + 1}.{format}");
                        image.Write(outputPath);

                        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                        {
                            throw new Exception($"Sayfa {index + 1} kaydedilemedi: {outputPath}");
                        }

                        int current;
                        lock (lockObj)
                        {
                            completed++;
                            current = completed;
                        }
                        progress?.Report((current * 100) / total);
                    });
            }, cancellationToken);
        }

        /// <summary>
        /// PDF'in her sayfasını ayrı görüntü dosyasına dönüştürür (geriye uyumlu eski versiyon).
        /// </summary>
        [Obsolete("Use overload with CancellationToken instead")]
        public async Task ConvertPdfToImagesAsync(string pdfPath, string outputDir, string format)
        {
            await ConvertPdfToImagesAsync(pdfPath, outputDir, format, CancellationToken.None, null);
        }

        /// <summary>
        /// Ghostscript'in sistemde yüklü olup olmadığını kontrol eder.
        /// </summary>
        private bool IsGhostscriptAvailable()
        {
            try
            {
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

                process.WaitForExit(3000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
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

        /// <summary>
        /// Görüntüyü byte dizisi olarak yükler.
        /// </summary>
        public async ValueTask<byte[]> LoadImageToMemoryAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            return await File.ReadAllBytesAsync(imagePath, cancellationToken);
        }
    }
}
