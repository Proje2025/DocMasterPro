using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace DocConverter.Services
{
    /// <summary>
    /// Bir çıktı kalitesinin somut sıkıştırma parametreleri.
    /// </summary>
    public sealed record ScanPdfProfile(int MaxDpi, int JpegQuality, bool ChromaSubsampling, bool Lossless)
    {
        public static ScanPdfProfile For(ScanOutputQuality quality) => quality switch
        {
            ScanOutputQuality.Small => new ScanPdfProfile(150, 55, true, false),
            ScanOutputQuality.High => new ScanPdfProfile(300, 85, false, false),
            ScanOutputQuality.Lossless => new ScanPdfProfile(int.MaxValue, 100, false, true),
            _ => new ScanPdfProfile(200, 72, true, false)
        };
    }

    public sealed class ScanPdfOptions
    {
        public ScanOutputQuality Quality { get; init; } = ScanOutputQuality.Balanced;

        /// <summary>Sayfadaki eğriliği ölçüp düzeltir.</summary>
        public bool Deskew { get; init; }

        /// <summary>Sayfanın çevresinde kalan koyu tarayıcı zeminini kırpar.</summary>
        public bool CropDarkBorders { get; init; }

        public string Title { get; init; } = "DocMaster Pro — Taranan Belge";
    }

    public readonly record struct ScanPageSource(string FilePath, int Rotation, int FallbackDpi);

    public sealed record ScanPdfResult(string OutputPath, int PageCount, int SkippedPages, long FileSizeBytes);

    /// <summary>
    /// Taranmış sayfa görüntülerini sıkıştırarak tek bir PDF'e yazar.
    /// Renkli ve gri sayfalar JPEG (DCT), siyah-beyaz sayfalar 1-bit CCITT G4 olarak gömülür;
    /// çözünürlük profilin üst sınırına indirgenir. Sayfanın fiziksel boyutu korunur.
    /// </summary>
    public static class ScannedPdfBuilder
    {
        private const double UnknownDpiCeiling = 100;
        private const int BilevelMinDpi = 300;

        private enum PageKind { Color, Gray, Bilevel }

        private sealed record EncodedPage(byte[] Data, double WidthPt, double HeightPt);

        public static ScanPdfResult Build(
            IReadOnlyList<ScanPageSource> pages,
            string outputPath,
            ScanPdfOptions options,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(pages);
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Çıktı dosya yolu boş olamaz.", nameof(outputPath));
            if (pages.Count == 0)
                throw new InvalidOperationException("Kaydedilecek taranmış sayfa yok.");

            var profile = ScanPdfProfile.For(options.Quality);
            int window = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            int written = 0;
            int skipped = 0;

            string fullOutput = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Yarım kalmış bir PDF hedef dosyanın üzerine yazılmasın diye önce geçici dosyaya kaydedilir.
            string tempOutput = fullOutput + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var doc = new PdfDocument())
                {
                    doc.Info.Title = options.Title;
                    doc.Info.Creator = "DocMaster Pro Scanner Studio";
                    doc.Options.EnableCcittCompressionForBilevelImages = true;
                    doc.Options.CompressContentStreams = true;

                    for (int start = 0; start < pages.Count; start += window)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int count = Math.Min(window, pages.Count - start);
                        var encoded = new EncodedPage?[count];
                        int offset = start;

                        Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
                        {
                            encoded[i] = TryEncodePage(pages[offset + i], profile, options);
                        });

                        foreach (var page in encoded)
                        {
                            if (page == null)
                            {
                                skipped++;
                                continue;
                            }

                            AppendPage(doc, page);
                            written++;
                        }

                        progress?.Report((int)((start + count) * 95.0 / pages.Count));
                    }

                    if (written == 0)
                        throw new InvalidOperationException("Taranan sayfaların hiçbiri okunamadı; PDF oluşturulamadı.");

                    doc.Save(tempOutput);
                }

                File.Move(tempOutput, fullOutput, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
            }

            progress?.Report(100);
            return new ScanPdfResult(fullOutput, written, skipped, new FileInfo(fullOutput).Length);
        }

        private static void AppendPage(PdfDocument doc, EncodedPage encoded)
        {
            using var stream = new MemoryStream(encoded.Data, writable: false);
            using var image = XImage.FromStream(stream);

            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(encoded.WidthPt);
            page.Height = XUnit.FromPoint(encoded.HeightPt);

            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(image, 0, 0, encoded.WidthPt, encoded.HeightPt);
        }

        private static EncodedPage? TryEncodePage(ScanPageSource source, ScanPdfProfile profile, ScanPdfOptions options)
        {
            try
            {
                if (!File.Exists(source.FilePath))
                    return null;

                return EncodePage(source, profile, options);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"ScannedPdfBuilder sayfa atlandı ({source.FilePath})", ex);
                return null;
            }
        }

        private static EncodedPage EncodePage(ScanPageSource source, ScanPdfProfile profile, ScanPdfOptions options)
        {
            using var image = Image.Load<Rgb24>(source.FilePath);
            double dpi = ResolveDpi(image.Metadata, source.FallbackDpi);
            PageKind kind = Classify(image);

            if (options.Deskew)
                ScanImageCleanup.Deskew(image);

            if (options.CropDarkBorders)
                ScanImageCleanup.CropDarkBorders(image);

            switch (((source.Rotation % 360) + 360) % 360)
            {
                case 90: image.Mutate(c => c.Rotate(RotateMode.Rotate90)); break;
                case 180: image.Mutate(c => c.Rotate(RotateMode.Rotate180)); break;
                case 270: image.Mutate(c => c.Rotate(RotateMode.Rotate270)); break;
            }

            // Fiziksel boyut, yeniden örneklemeden önceki piksel sayısından hesaplanır.
            double widthPt = image.Width / dpi * 72d;
            double heightPt = image.Height / dpi * 72d;

            if (!profile.Lossless)
            {
                // 1-bit görüntü zaten çok küçüktür; 300 DPI altına indirmek yalnızca metni bozar.
                int maxDpi = kind == PageKind.Bilevel ? Math.Max(profile.MaxDpi, BilevelMinDpi) : profile.MaxDpi;
                if (dpi > maxDpi + 1)
                {
                    double scale = maxDpi / dpi;
                    int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                    image.Mutate(c => c.Resize(width, height, KnownResamplers.Lanczos3));
                }
            }

            using var buffer = new MemoryStream();
            switch (kind)
            {
                case PageKind.Bilevel:
                    image.Mutate(c => c.BinaryThreshold(0.5f));
                    image.Save(buffer, new BmpEncoder
                    {
                        BitsPerPixel = BmpBitsPerPixel.Pixel1,
                        Quantizer = new PaletteQuantizer(
                            new[] { Color.Black, Color.White },
                            new QuantizerOptions { Dither = null })
                    });
                    break;

                case PageKind.Gray when profile.Lossless:
                    image.Save(buffer, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 });
                    break;

                case PageKind.Gray:
                    image.Save(buffer, new JpegEncoder
                    {
                        Quality = profile.JpegQuality,
                        ColorType = JpegEncodingColor.Luminance,
                        SkipMetadata = true
                    });
                    break;

                default:
                    if (profile.Lossless)
                    {
                        image.Save(buffer, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
                    }
                    else
                    {
                        image.Save(buffer, new JpegEncoder
                        {
                            Quality = profile.JpegQuality,
                            ColorType = profile.ChromaSubsampling
                                ? JpegEncodingColor.YCbCrRatio420
                                : JpegEncodingColor.YCbCrRatio444,
                            SkipMetadata = true
                        });
                    }
                    break;
            }

            return new EncodedPage(buffer.ToArray(), widthPt, heightPt);
        }

        /// <summary>
        /// Görüntünün kendi DPI bilgisini okur. Bilgi yoksa (ImageSharp 96/72 varsayar) taramada
        /// istenen çözünürlüğe düşer; aksi halde sayfa metrelerce genişlikte hesaplanırdı.
        /// </summary>
        internal static double ResolveDpi(ImageMetadata metadata, int fallbackDpi)
        {
            double dpi = metadata.ResolutionUnits switch
            {
                PixelResolutionUnit.PixelsPerInch => metadata.HorizontalResolution,
                PixelResolutionUnit.PixelsPerCentimeter => metadata.HorizontalResolution * 2.54,
                PixelResolutionUnit.PixelsPerMeter => metadata.HorizontalResolution * 0.0254,
                _ => 0
            };

            if (dpi >= UnknownDpiCeiling && dpi <= 2400)
                return dpi;

            return fallbackDpi >= 50 && fallbackDpi <= 2400 ? fallbackDpi : 300;
        }

        /// <summary>
        /// Seyrek bir piksel ızgarasına bakarak sayfanın renkli, gri ya da siyah-beyaz olduğunu belirler.
        /// </summary>
        private static PageKind Classify(Image<Rgb24> image)
        {
            bool gray = true;
            bool bilevel = true;
            int step = Math.Max(1, Math.Min(image.Width, image.Height) / 400);

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height && gray; y += step)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x += step)
                    {
                        ref readonly Rgb24 p = ref row[x];
                        if (Math.Abs(p.R - p.G) > 2 || Math.Abs(p.G - p.B) > 2)
                        {
                            gray = false;
                            break;
                        }

                        if (p.G > 16 && p.G < 239)
                            bilevel = false;
                    }
                }
            });

            if (!gray)
                return PageKind.Color;

            return bilevel ? PageKind.Bilevel : PageKind.Gray;
        }
    }
}
