using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DocConverter.Services
{
    /// <summary>
    /// Taranmış sayfalar için eğrilik düzeltme ve koyu zemin kırpma.
    /// Her iki işlem de emin olunmadığında görüntüye dokunmaz.
    /// </summary>
    public static class ScanImageCleanup
    {
        private const double SearchRangeDegrees = 10;
        private const double CoarseStepDegrees = 0.25;
        private const double FineStepDegrees = 0.05;
        private const double MinCorrectionDegrees = 0.3;
        private const double KeepSizeBelowDegrees = 3;
        private const double MinPeakRatio = 1.15;
        private const int AnalysisSize = 800;
        private const int EdgeThreshold = 40;
        private const int MaxEdgePoints = 200_000;

        private const int PaperLuma = 110;
        private const int DarkBedLuma = 70;
        private const double PaperRowFraction = 0.12;

        /// <summary>
        /// Satırların eğimini ölçer ve sayfayı düzeltir. Uygulanan düzeltme açısını (derece) döndürür;
        /// güvenilir bir eğim bulunamazsa 0 döner ve görüntü değişmez.
        /// </summary>
        public static double Deskew(Image<Rgb24> image)
        {
            ArgumentNullException.ThrowIfNull(image);

            double skew = EstimateSkewAngle(image);
            if (Math.Abs(skew) < MinCorrectionDegrees)
                return 0;

            int width = image.Width;
            int height = image.Height;
            Color fill = SampleFrameColor(image);
            bool keepSize = Math.Abs(skew) <= KeepSizeBelowDegrees;

            using var rotated = image.CloneAs<Rgba32>();
            rotated.Mutate(c =>
            {
                c.Rotate((float)-skew, KnownResamplers.Bicubic).BackgroundColor(fill);

                if (keepSize)
                {
                    Size size = c.GetCurrentSize();
                    c.Crop(new Rectangle((size.Width - width) / 2, (size.Height - height) / 2, width, height));
                }
            });

            if (!keepSize)
                image.Mutate(c => c.Resize(rotated.Width, rotated.Height, KnownResamplers.NearestNeighbor));

            image.ProcessPixelRows(rotated, (target, source) =>
            {
                for (int y = 0; y < target.Height; y++)
                {
                    Span<Rgb24> to = target.GetRowSpan(y);
                    Span<Rgba32> from = source.GetRowSpan(y);
                    for (int x = 0; x < to.Length; x++)
                        to[x] = new Rgb24(from[x].R, from[x].G, from[x].B);
                }
            });

            return -skew;
        }

        /// <summary>
        /// Metin satırlarının yatayla yaptığı açıyı derece olarak döndürür
        /// (pozitif = içerik saat yönünde dönük). Belirgin bir yön yoksa 0 döner.
        /// </summary>
        public static double EstimateSkewAngle(Image<Rgb24> image)
        {
            ArgumentNullException.ThrowIfNull(image);

            byte[] gray = Downsample(image, out int width, out int height);
            if (width < 32 || height < 32)
                return 0;

            // Yatay kenarlar (satırların alt/üst sınırı, sayfa kenarı): dikey yönde parlaklık sıçraması.
            var xs = new float[MaxEdgePoints];
            var ys = new float[MaxEdgePoints];
            int count = 0;
            float centerX = width / 2f;
            float centerY = height / 2f;

            for (int y = 1; y < height - 1 && count < MaxEdgePoints; y++)
            {
                int above = (y - 1) * width;
                int below = (y + 1) * width;
                for (int x = 0; x < width && count < MaxEdgePoints; x++)
                {
                    if (Math.Abs(gray[below + x] - gray[above + x]) < EdgeThreshold)
                        continue;

                    xs[count] = x - centerX;
                    ys[count] = y - centerY;
                    count++;
                }
            }

            if (count < 200)
                return 0;

            int bins = (int)Math.Ceiling(Math.Sqrt(width * (double)width + height * (double)height)) + 4;
            var histogram = new int[bins];

            double Score(double degrees)
            {
                Array.Clear(histogram);
                double radians = degrees * Math.PI / 180d;
                float cos = (float)Math.Cos(radians);
                float sin = (float)Math.Sin(radians);
                float middle = bins / 2f;

                for (int i = 0; i < count; i++)
                {
                    int bin = (int)(ys[i] * cos - xs[i] * sin + middle);
                    if ((uint)bin < (uint)bins)
                        histogram[bin]++;
                }

                double sum = 0;
                for (int i = 0; i < bins; i++)
                    sum += (double)histogram[i] * histogram[i];
                return sum;
            }

            double best = 0;
            double bestScore = double.MinValue;
            double total = 0;
            int samples = 0;

            for (double angle = -SearchRangeDegrees; angle <= SearchRangeDegrees + 1e-9; angle += CoarseStepDegrees)
            {
                double score = Score(angle);
                total += score;
                samples++;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = angle;
                }
            }

            // Tepe belirgin değilse (fotoğraf, boş sayfa) ya da arama aralığının ucundaysa güvenme.
            if (bestScore < total / samples * MinPeakRatio || Math.Abs(best) > SearchRangeDegrees - CoarseStepDegrees)
                return 0;

            double coarse = best;
            for (double angle = coarse - CoarseStepDegrees; angle <= coarse + CoarseStepDegrees + 1e-9; angle += FineStepDegrees)
            {
                double score = Score(angle);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = angle;
                }
            }

            return Math.Round(best, 2);
        }

        /// <summary>
        /// Sayfayı çevreleyen koyu tarayıcı zeminini kırpar. Zemin koyu değilse (beyaz kenar boşlukları)
        /// hiçbir şey yapmaz. Kırpma yapıldıysa true döner.
        /// </summary>
        public static bool CropDarkBorders(Image<Rgb24> image)
        {
            ArgumentNullException.ThrowIfNull(image);

            int width = image.Width;
            int height = image.Height;
            if (width < 200 || height < 200)
                return false;

            int step = Math.Max(1, Math.Min(width, height) / 600);
            int columns = (width + step - 1) / step;
            int rows = (height + step - 1) / step;
            var paperInRow = new int[rows];
            var paperInColumn = new int[columns];
            var lumaInRow = new long[rows];
            var lumaInColumn = new long[columns];

            image.ProcessPixelRows(accessor =>
            {
                for (int r = 0; r < rows; r++)
                {
                    Span<Rgb24> row = accessor.GetRowSpan(r * step);
                    for (int c = 0; c < columns; c++)
                    {
                        ref readonly Rgb24 p = ref row[c * step];
                        int luma = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                        lumaInRow[r] += luma;
                        lumaInColumn[c] += luma;

                        if (luma > PaperLuma)
                        {
                            paperInRow[r]++;
                            paperInColumn[c]++;
                        }
                    }
                }
            });

            int top = FirstPaperIndex(paperInRow, columns, fromEnd: false);
            int bottom = FirstPaperIndex(paperInRow, columns, fromEnd: true);
            int left = FirstPaperIndex(paperInColumn, rows, fromEnd: false);
            int right = FirstPaperIndex(paperInColumn, rows, fromEnd: true);

            if (top < 0 || left < 0 || bottom <= top || right <= left)
                return false;

            // Kırpılacak şeritler gerçekten koyu zemin mi? Değilse (ör. açık gri kenar) dokunma.
            if (!IsDarkStrip(lumaInRow, 0, top, columns) ||
                !IsDarkStrip(lumaInRow, bottom + 1, rows, columns) ||
                !IsDarkStrip(lumaInColumn, 0, left, rows) ||
                !IsDarkStrip(lumaInColumn, right + 1, columns, rows))
            {
                return false;
            }

            var crop = Rectangle.FromLTRB(
                left * step,
                top * step,
                Math.Min(width, (right + 1) * step),
                Math.Min(height, (bottom + 1) * step));

            bool trimsSomething = crop.Width <= width * 0.99 || crop.Height <= height * 0.99;
            bool keepsEnough = (double)crop.Width * crop.Height >= (double)width * height * 0.25
                               && crop.Width >= 100 && crop.Height >= 100;

            if (!trimsSomething || !keepsEnough)
                return false;

            image.Mutate(c => c.Crop(crop));
            return true;
        }

        private static int FirstPaperIndex(int[] paperCounts, int samplesPerLine, bool fromEnd)
        {
            int needed = Math.Max(1, (int)(samplesPerLine * PaperRowFraction));

            if (!fromEnd)
            {
                for (int i = 0; i < paperCounts.Length; i++)
                    if (paperCounts[i] >= needed) return i;
            }
            else
            {
                for (int i = paperCounts.Length - 1; i >= 0; i--)
                    if (paperCounts[i] >= needed) return i;
            }

            return -1;
        }

        private static bool IsDarkStrip(long[] lumaSums, int from, int to, int samplesPerLine)
        {
            if (to <= from)
                return true;

            long sum = 0;
            for (int i = from; i < to; i++)
                sum += lumaSums[i];

            return sum / ((double)(to - from) * samplesPerLine) < DarkBedLuma;
        }

        private static byte[] Downsample(Image<Rgb24> image, out int width, out int height)
        {
            int step = Math.Max(1, (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)AnalysisSize));
            int outWidth = image.Width / step;
            int outHeight = image.Height / step;
            var gray = new byte[Math.Max(1, outWidth * outHeight)];

            image.ProcessPixelRows(accessor =>
            {
                var sums = new int[outWidth];

                for (int oy = 0; oy < outHeight; oy++)
                {
                    Array.Clear(sums);

                    for (int dy = 0; dy < step; dy++)
                    {
                        Span<Rgb24> row = accessor.GetRowSpan(oy * step + dy);
                        for (int ox = 0; ox < outWidth; ox++)
                        {
                            int baseX = ox * step;
                            int sum = 0;
                            for (int dx = 0; dx < step; dx++)
                            {
                                ref readonly Rgb24 p = ref row[baseX + dx];
                                sum += (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                            }
                            sums[ox] += sum;
                        }
                    }

                    int area = step * step;
                    for (int ox = 0; ox < outWidth; ox++)
                        gray[oy * outWidth + ox] = (byte)(sums[ox] / area);
                }
            });

            width = outWidth;
            height = outHeight;
            return gray;
        }

        /// <summary>Dönme sonrası açıkta kalan köşeleri doldurmak için dış çerçevenin ortalama rengi.</summary>
        private static Color SampleFrameColor(Image<Rgb24> image)
        {
            long r = 0, g = 0, b = 0, n = 0;
            int step = Math.Max(1, Math.Max(image.Width, image.Height) / 400);

            image.ProcessPixelRows(accessor =>
            {
                void Add(in Rgb24 p) { r += p.R; g += p.G; b += p.B; n++; }

                Span<Rgb24> first = accessor.GetRowSpan(0);
                Span<Rgb24> last = accessor.GetRowSpan(accessor.Height - 1);
                for (int x = 0; x < first.Length; x += step)
                {
                    Add(first[x]);
                    Add(last[x]);
                }

                for (int y = 0; y < accessor.Height; y += step)
                {
                    Span<Rgb24> row = accessor.GetRowSpan(y);
                    Add(row[0]);
                    Add(row[^1]);
                }
            });

            return n == 0
                ? Color.White
                : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
    }
}
