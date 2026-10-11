using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Helpers;
using PdfiumDoc = PdfiumViewer.Core.PdfDocument;
using PdfiumViewer.Enums;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharpDoc = PdfSharp.Pdf.PdfDocument;
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
                using var document = new PdfSharpDoc();
                document.PageLayout = PdfPageLayout.SinglePage;

                var (data, widthPt, heightPt) = PrepareImageForPdf(imagePath);
                using (data)
                using (var xImage = XImage.FromStream(data))
                {
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(widthPt);
                    page.Height = XUnit.FromPoint(heightPt);

                    using var gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawImage(xImage, 0, 0, widthPt, heightPt);
                }

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
        /// Görüntüyü PDFsharp'ın okuyabileceği biçime getirir ve sayfa boyutunu hesaplar.
        /// PDFsharp yalnızca JPEG, PNG ve BMP okur; GIF, TIFF ve WEBP önce PNG'ye çevrilir.
        /// Sayfa boyutu piksel sayısından değil fiziksel boyuttan (DPI) hesaplanır ve A4'ü aşıyorsa
        /// A4'e sığdırılır; eskiden 3000 piksellik bir fotoğraf 1 metrelik sayfa üretiyordu.
        /// </summary>
        private static (Stream Data, double WidthPt, double HeightPt) PrepareImageForPdf(string imagePath)
        {
            const double a4Short = 595.28;
            const double a4Long = 841.89;

            SixLabors.ImageSharp.ImageInfo info = SixLabors.ImageSharp.Image.Identify(imagePath);
            bool isJpeg = info.Metadata.DecodedImageFormat is SixLabors.ImageSharp.Formats.Jpeg.JpegFormat;

            // Telefon ve kamera fotoğrafları yan çekilip EXIF ile "döndür" diye işaretlenir.
            ushort orientation = 1;
            if (info.Metadata.ExifProfile != null &&
                info.Metadata.ExifProfile.TryGetValue(
                    SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, out var orientationValue) &&
                orientationValue != null)
            {
                orientation = orientationValue.Value;
            }

            int width = info.Width;
            int height = info.Height;
            Stream data;

            if (isJpeg && orientation <= 1)
            {
                // JPEG olduğu gibi gömülür: yeniden sıkıştırma yok, kalite kaybı yok.
                data = File.OpenRead(imagePath);
            }
            else
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imagePath);
                image.Mutate(c => c.AutoOrient().BackgroundColor(SixLabors.ImageSharp.Color.White));
                width = image.Width;
                height = image.Height;

                var buffer = new MemoryStream();
                if (isJpeg)
                {
                    image.Save(buffer, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
                }
                else
                {
                    image.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder
                    {
                        ColorType = SixLabors.ImageSharp.Formats.Png.PngColorType.Rgb,
                        BitDepth = SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8
                    });
                }

                buffer.Position = 0;
                data = buffer;
            }

            double dpi = info.Metadata.ResolutionUnits switch
            {
                SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch => info.Metadata.HorizontalResolution,
                SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerCentimeter => info.Metadata.HorizontalResolution * 2.54,
                SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerMeter => info.Metadata.HorizontalResolution * 0.0254,
                _ => 0
            };

            // 72 / 96 DPI çoğunlukla "bilinmiyor" demektir; ekran çözünürlüğü varsayılır.
            if (dpi < 100 || dpi > 2400)
                dpi = 96;

            double widthPt = width / dpi * 72d;
            double heightPt = height / dpi * 72d;

            bool landscape = widthPt > heightPt;
            double maxWidth = landscape ? a4Long : a4Short;
            double maxHeight = landscape ? a4Short : a4Long;
            double scale = Math.Min(1d, Math.Min(maxWidth / widthPt, maxHeight / heightPt));

            return (data, Math.Max(1, widthPt * scale), Math.Max(1, heightPt * scale));
        }

        /// <summary>
        /// PDF'in her sayfasını ayrı görüntü dosyasına dönüştürür.
        /// Google PDFium motoru kullanılarak yüksek performanslı ve harici kurulum gerektirmeksizin çalışır.
        /// </summary>
        public async Task ConvertPdfToImagesAsync(
            string pdfPath,
            string outputDir,
            string format = "png",
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                throw new FileNotFoundException($"PDF dosyası bulunamadı: {pdfPath}");
            }

            Directory.CreateDirectory(outputDir);
            string baseName = Path.GetFileNameWithoutExtension(pdfPath);

            await Task.Run(() =>
            {
                PdfiumNativeLoader.EnsureLoaded();

                using var pdfDoc = PdfiumDoc.Load(pdfPath);
                int total = pdfDoc.PageCount;

                if (total == 0)
                {
                    throw new Exception("PDF dosyasında hiç sayfa bulunamadı veya sayfalar okunamadı.");
                }

                const float dpi = 300f;

                for (int index = 0; index < total; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var pageSize = pdfDoc.PageSizes[index] is System.Drawing.SizeF sz ? sz : new System.Drawing.SizeF(595, 842);
                    int renderWidth = Math.Max(1, (int)Math.Round(pageSize.Width * (dpi / 72.0f)));
                    int renderHeight = Math.Max(1, (int)Math.Round(pageSize.Height * (dpi / 72.0f)));

                    using var pageImage = pdfDoc.Render(
                        index,
                        renderWidth,
                        renderHeight,
                        dpi,
                        dpi,
                        PdfRenderFlags.Annotations);

                    string outputPath = Path.Combine(outputDir, $"{baseName}_sayfa{index + 1}.{format.ToLowerInvariant()}");
                    SaveRenderedImage(pageImage, outputPath, format);

                    progress?.Report(((index + 1) * 100) / total);
                }
            }, cancellationToken);
        }

        private static void SaveRenderedImage(System.Drawing.Image image, string outputPath, string format)
        {
            string ext = format.ToLowerInvariant().TrimStart('.');
            switch (ext)
            {
                case "jpg" or "jpeg":
                    image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                    break;
                case "png":
                    image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                    break;
                case "bmp":
                    image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Bmp);
                    break;
                case "gif":
                    image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Gif);
                    break;
                case "tiff" or "tif":
                    image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Tiff);
                    break;
                case "webp":
                    using (var ms = new MemoryStream())
                    {
                        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        using var sharpImg = SixLabors.ImageSharp.Image.Load(ms);
                        sharpImg.SaveAsWebp(outputPath);
                    }
                    break;
                default:
                    image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                    break;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new IOException($"Sayfa kaydedilemedi: {outputPath}");
            }
        }

        /// <summary>
        /// PDF'in her sayfasını ayrı görüntü dosyasına dönüştürür (ilerleme raporlamalı overload).
        /// </summary>
        public Task ConvertPdfToImagesAsync(string pdfPath, string outputDir, string format, IProgress<int>? progress)
        {
            return ConvertPdfToImagesAsync(pdfPath, outputDir, format, CancellationToken.None, progress);
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
        /// Geriye uyumluluk için tutulmaktadır. PDF dönüştürme artık yerel PDFium ile çalıştığı için Ghostscript gerektirmez.
        /// </summary>
        public bool IsGhostscriptAvailable()
        {
            return true;
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
