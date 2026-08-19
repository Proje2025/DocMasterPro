using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DocConverter.Services
{
    public class BlankPageDetector
    {
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
                int totalPixels = image.Width * image.Height;
                if (totalPixels == 0) return true;

                long whitePixelCount = 0;

                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            ref readonly var pixel = ref row[x];
                            // Parlaklık / Gri ton hesaplama (BT.601 standardı)
                            int luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                            if (luminance >= luminanceThreshold)
                            {
                                whitePixelCount++;
                            }
                        }
                    }
                });

                double whiteRatio = (double)whitePixelCount / totalPixels * 100.0;
                return whiteRatio >= whiteThresholdPercent;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"IsImageBlank ({imagePath})", ex);
                return false;
            }
        }

        /// <summary>
        /// PDF dosyasındaki tüm boş sayfaları otomatik olarak ayıklar ve yeni bir PDF olarak kaydeder.
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

            string tempDir = Path.Combine(Path.GetTempPath(), $"DocMaster_BlankDetect_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            int removedCount = 0;
            int totalOriginalPages = 0;

            try
            {
                var nonBlankPageIndices = new System.Collections.Generic.List<int>();

                using (var doc = PdfReader.Open(inputPdfPath, PdfDocumentOpenMode.Import))
                {
                    totalOriginalPages = doc.PageCount;
                }

                if (totalOriginalPages == 0)
                {
                    File.Copy(inputPdfPath, outputPdfPath, true);
                    return (outputPdfPath, 0, 0);
                }

                // Magick.NET ile sayfaları render edip boşluk testi yap
                var readSettings = new MagickReadSettings
                {
                    Density = new Density(100) // Hızlı analiz için 100 DPI yeterlidir
                };

                for (int i = 0; i < totalOriginalPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    readSettings.FrameIndex = (uint)i;
                    readSettings.FrameCount = 1;

                    string tempPageImage = Path.Combine(tempDir, $"page_{i}.png");

                    using (var collection = new MagickImageCollection())
                    {
                        collection.Read(inputPdfPath, readSettings);
                        if (collection.Count > 0)
                        {
                            collection[0].Write(tempPageImage, MagickFormat.Png);
                        }
                    }

                    bool isBlank = IsImageBlank(tempPageImage, whiteThresholdPercent);

                    if (isBlank)
                    {
                        removedCount++;
                    }
                    else
                    {
                        nonBlankPageIndices.Add(i);
                    }

                    if (File.Exists(tempPageImage))
                    {
                        try { File.Delete(tempPageImage); } catch { }
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
                using (var outDoc = new PdfDocument())
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
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }
    }
}
