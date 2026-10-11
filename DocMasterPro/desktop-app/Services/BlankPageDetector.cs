using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Helpers;
using PdfiumDoc = PdfiumViewer.Core.PdfDocument;
using PdfiumViewer.Enums;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DocConverter.Services
{
    public class BlankPageDetector
    {
        /// <summary>
        /// Bir sayfanın boş sayılabilmesi için koyu (mürekkep) piksellerin en fazla oranı (%).
        /// Toz, delgeç deliği gölgesi ve arka yüzden vuran yazı bu sınırın altında kalır.
        /// </summary>
        private const double MaxInkPercentForBlank = 0.02;

        /// <summary>
        /// Resim dosyasının boş/beyaz olup olmadığını piksel yoğunluğu analiziyle kontrol eder.
        /// </summary>
        /// <param name="imagePath">Analiz edilecek resmin yolu</param>
        /// <param name="whiteThresholdPercent">Sayfanın boş kabul edilmesi için minimum beyaz/açık piksel oranı (örn: 98.5)</param>
        /// <param name="luminanceThreshold">Beyaz/arka plan sayılması için minimum parlaklık (0-255, varsayılan 240)</param>
        /// <returns>Sayfa boş ise true, dolu ise false</returns>
        public bool IsImageBlank(string imagePath, double whiteThresholdPercent = 98.5, byte luminanceThreshold = 240)
        {
            if (!File.Exists(imagePath)) return false;

            try
            {
                using var image = Image.Load<Rgb24>(imagePath);
                return IsImageSharpBlank(image, whiteThresholdPercent, luminanceThreshold);
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"IsImageBlank ({imagePath})", ex);
                return false;
            }
        }

        /// <summary>
        /// Bir ImageSharp resminin piksellerini analiz ederek boş olup olmadığını belirler.
        /// </summary>
        private static bool IsImageSharpBlank(Image<Rgb24> image, double whiteThresholdPercent, byte luminanceThreshold)
        {
            long totalPixels = (long)image.Width * image.Height;
            if (totalPixels == 0) return true;

            // 1. geçiş: parlaklık dağılımı. Kağıdın rengi buradan ölçülür.
            var histogram = new long[256];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        ref readonly var pixel = ref row[x];
                        // Parlaklık / Gri ton hesaplama (BT.601 standardı)
                        histogram[(pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000]++;
                    }
                }
            });

            // Kağıt rengi = medyan parlaklık. Taranan kağıt nadiren saf beyazdır (240'ın altında olabilir);
            // sabit eşik kullanıldığında böyle sayfalar hiçbir zaman boş sayılmıyordu.
            int paper = 255;
            long cumulative = 0;
            for (int level = 0; level < 256; level++)
            {
                cumulative += histogram[level];
                if (cumulative * 2 >= totalPixels)
                {
                    paper = level;
                    break;
                }
            }

            // Çoğunluğu koyu olan sayfa (fotoğraf, koyu kapak) boş değildir.
            if (paper < 128)
                return false;

            int whiteLevel = Math.Min(luminanceThreshold, paper - 16);
            int inkLevel = Math.Min(160, paper - 70);

            long whitePixelCount = 0;
            long inkPixelCount = 0;
            for (int level = 0; level < 256; level++)
            {
                if (level >= whiteLevel) whitePixelCount += histogram[level];
                if (level < inkLevel) inkPixelCount += histogram[level];
            }

            // Mürekkep oranı: tek satır yazı bile sayfanın ~%0,05'ini kaplar. Yalnızca "beyaz oranına"
            // bakmak, üç satırlık bir sayfayı ya da imza sayfasını (%98,6 beyaz) boş sayıp siliyordu.
            double inkRatio = (double)inkPixelCount / totalPixels * 100.0;
            if (inkRatio >= MaxInkPercentForBlank)
                return false;

            double whiteRatio = (double)whitePixelCount / totalPixels * 100.0;
            return whiteRatio >= whiteThresholdPercent;
        }

        /// <summary>
        /// PDF dosyasındaki tüm boş sayfaları otomatik olarak ayıklar ve yeni bir PDF olarak kaydeder.
        /// Google PDFium motoruyla sayfaları bellek içinde analiz eder; harici yazılım (Ghostscript) veya geçici disk dosyaları gerektirmez.
        /// </summary>
        public async Task<(string OutputPath, int RemovedPages, int TotalOriginalPages)> RemoveBlankPagesFromPdfAsync(
            string inputPdfPath,
            string outputPdfPath,
            double whiteThresholdPercent = 98.5,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputPdfPath))
                throw new FileNotFoundException("Kaynak PDF bulunamadı.", inputPdfPath);

            int removedCount = 0;
            int totalOriginalPages = 0;

            return await Task.Run(() =>
            {
                PdfiumNativeLoader.EnsureLoaded();

                using var pdfDoc = PdfiumDoc.Load(inputPdfPath);
                totalOriginalPages = pdfDoc.PageCount;

                if (totalOriginalPages == 0)
                {
                    File.Copy(inputPdfPath, outputPdfPath, true);
                    return (outputPdfPath, 0, 0);
                }

                var nonBlankPageIndices = new System.Collections.Generic.List<int>();

                for (int i = 0; i < totalOriginalPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var pageSize = pdfDoc.PageSizes[i] is System.Drawing.SizeF sz ? sz : new System.Drawing.SizeF(595, 842);
                    // Hızlı analiz için 100 DPI yeterlidir
                    int renderWidth = Math.Max(1, (int)Math.Round(pageSize.Width * (100f / 72.0f)));
                    int renderHeight = Math.Max(1, (int)Math.Round(pageSize.Height * (100f / 72.0f)));

                    bool isBlank = false;
                    try
                    {
                        using var pageImage = pdfDoc.Render(i, renderWidth, renderHeight, 100f, 100f, PdfRenderFlags.None);
                        using var ms = new MemoryStream();
                        pageImage.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        ms.Position = 0;

                        using var sharpImage = Image.Load<Rgb24>(ms);
                        isBlank = IsImageSharpBlank(sharpImage, whiteThresholdPercent, 240);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogError($"BlankPageDetector sayfa {i + 1}", ex);
                        isBlank = false;
                    }

                    if (isBlank)
                    {
                        removedCount++;
                    }
                    else
                    {
                        nonBlankPageIndices.Add(i);
                    }

                    int pct = (int)((i + 1) * 80.0 / totalOriginalPages);
                    progress?.Report(pct);
                }

                // Hiç dolu sayfa kalmadıysa ilk sayfayı koru
                if (nonBlankPageIndices.Count == 0 && totalOriginalPages > 0)
                {
                    nonBlankPageIndices.Add(0);
                    removedCount = Math.Max(0, totalOriginalPages - 1);
                }

                // Seçilen sayfaları yeni PDF'e aktar
                using (var sourceDoc = PdfReader.Open(inputPdfPath, PdfDocumentOpenMode.Import))
                using (var outDoc = new PdfSharp.Pdf.PdfDocument())
                {
                    foreach (int idx in nonBlankPageIndices)
                    {
                        if (idx < sourceDoc.PageCount)
                        {
                            outDoc.AddPage(sourceDoc.Pages[idx]);
                        }
                    }

                    outDoc.Save(outputPdfPath);
                }

                progress?.Report(100);
                return (outputPdfPath, removedCount, totalOriginalPages);
            }, cancellationToken);
        }
    }
}
