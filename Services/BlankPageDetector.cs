using System;
using System.Collections.Generic;
using System.IO;
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
        /// Verilen görüntü dosyasının boş (beyaz/tek renk) olup olmadığını kontrol eder.
        /// threshold: Boşluk eşiği (%90 - %100 arası). Varsayılan %98.5
        /// </summary>
        public bool IsImageBlank(string imagePath, double threshold = 98.5)
        {
            if (!File.Exists(imagePath)) return false;

            try
            {
                using var image = Image.Load<Rgb24>(imagePath);
                return IsImageSharpBlank(image, threshold);
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"IsImageBlank Error ({imagePath})", ex);
                return false;
            }
        }

        /// <summary>
        /// ImageSharp Rgb24 nesnesi üzerinde piksel analizi yapar.
        /// </summary>
        public bool IsImageSharpBlank(Image<Rgb24> image, double threshold = 98.5)
        {
            try
            {
                int width = image.Width;
                int height = image.Height;
                long totalPixels = (long)width * height;
                if (totalPixels == 0) return true;

                // Performans için örnekleme adımı
                int step = 1;
                if (totalPixels > 2_000_000) step = 3;
                else if (totalPixels > 500_000) step = 2;

                long sampledPixels = 0;
                long whiteOrNearWhitePixels = 0;
                const byte whiteThreshold = 240; // RGB >= 240

                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y += step)
                    {
                        var pixelRow = accessor.GetRowSpan(y);
                        for (int x = 0; x < accessor.Width; x += step)
                        {
                            sampledPixels++;
                            var pixel = pixelRow[x];

                            if (pixel.R >= whiteThreshold && pixel.G >= whiteThreshold && pixel.B >= whiteThreshold)
                            {
                                whiteOrNearWhitePixels++;
                            }
                        }
                    }
                });

                if (sampledPixels == 0) return true;

                double whiteRatio = (double)whiteOrNearWhitePixels / sampledPixels * 100.0;
                return whiteRatio >= threshold;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("IsImageSharpBlank Error", ex);
                return false;
            }
        }

        /// <summary>
        /// PDF dosyasından boş sayfaları temizleyip yeni bir PDF olarak kaydeder.
        /// Kaldırılan sayfa sayısını ve yeni dosya yolunu döndürür.
        /// </summary>
        public async Task<(string OutputPath, int RemovedPagesCount, int TotalPages)> RemoveBlankPagesFromPdfAsync(
            string inputPdfPath,
            string outputPdfPath,
            double threshold = 98.5,
            IProgress<int>? progress = null)
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(inputPdfPath))
                    throw new FileNotFoundException("PDF dosyası bulunamadı", inputPdfPath);

                using var sourceDoc = PdfReader.Open(inputPdfPath, PdfDocumentOpenMode.Import);
                int totalPages = sourceDoc.PageCount;
                if (totalPages == 0)
                    throw new InvalidOperationException("PDF dosyasında sayfa bulunmuyor.");

                using var targetDoc = new PdfDocument();
                int removedCount = 0;

                for (int i = 0; i < totalPages; i++)
                {
                    bool isBlank = false;

                    try
                    {
                        var readSettings = new MagickReadSettings
                        {
                            FrameIndex = (uint)i,
                            FrameCount = 1U,
                            Density = new Density(72)
                        };

                        using var images = new MagickImageCollection();
                        images.Read(inputPdfPath, readSettings);

                        if (images.Count > 0)
                        {
                            var img = images[0];
                            using var ms = new MemoryStream();
                            img.Write(ms, MagickFormat.Png);
                            ms.Position = 0;

                            using var sharpImg = Image.Load<Rgb24>(ms);
                            isBlank = IsImageSharpBlank(sharpImg, threshold);
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogError($"RemoveBlankPages check page {i + 1}", ex);
                        isBlank = false;
                    }

                    if (isBlank)
                    {
                        removedCount++;
                    }
                    else
                    {
                        targetDoc.AddPage(sourceDoc.Pages[i]);
                    }

                    progress?.Report((i + 1) * 100 / totalPages);
                }

                if (targetDoc.PageCount == 0 && totalPages > 0)
                {
                    targetDoc.AddPage(sourceDoc.Pages[0]);
                    removedCount = Math.Max(0, totalPages - 1);
                }

                targetDoc.Save(outputPdfPath);
                return (outputPdfPath, removedCount, totalPages);
            });
        }
    }
}
