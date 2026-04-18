using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Polly;
using Polly.Retry;

namespace DocConverter.Services
{
    /// <summary>
    /// PDF işlemleri için performans optimize edilmiş async servis.
    /// Polly ile retry mekanizması ve CancellationToken desteği sunar.
    /// </summary>
    public class PdfService
    {
        private static readonly AsyncRetryPolicy RetryPolicy = Policy
            .Handle<IOException>()
            .Or<PdfReaderException>()
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(200 * retryAttempt),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    FileLogger.LogError("PdfService Retry", new Exception(
                        $"Retry {retryCount} after {timeSpan.TotalMilliseconds}ms: {exception.Message}"));
                });

        /// <summary>
        /// Birden fazla PDF dosyasını tek bir çıktı dosyasında birleştirir.
        /// CancellationToken ve IProgress desteği sunar.
        /// </summary>
        public async Task MergePdfsAsync(
            List<string> files,
            string output,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            await MergePdfsAsync(files, output, progress, cancellationToken);
        }

        /// <summary>
        /// Birden fazla PDF dosyasını tek bir çıktı dosyasında birleştirir.
        /// </summary>
        public async Task MergePdfsAsync(
            List<string> files,
            string output,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (files.Count == 0)
                throw new InvalidOperationException("Birleştirilecek PDF dosyası bulunamadı.");

            await Task.Run(() =>
            {
                using var doc = new PdfDocument();
                int total = files.Count;
                var failedFiles = new List<string>();

                for (int idx = 0; idx < total; idx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var f = files[idx];

                    if (!File.Exists(f))
                    {
                        failedFiles.Add(Path.GetFileName(f));
                        progress?.Report((idx + 1) * 100 / total);
                        continue;
                    }

                    var retryResult = RetryPolicy.ExecuteAndCaptureAsync(() =>
                        Task.FromResult(PdfReader.Open(f, PdfDocumentOpenMode.Import))).GetAwaiter().GetResult();

                    if (retryResult.Outcome == OutcomeType.Failure)
                    {
                        failedFiles.Add(Path.GetFileName(f));
                        continue;
                    }

                    using var input = retryResult.Result!;

                    try
                    {
                        for (int p = 0; p < input.PageCount; p++)
                            doc.AddPage(input.Pages[p]);
                    }
                    catch (Exception ex)
                    {
                        failedFiles.Add($"{Path.GetFileName(f)} ({ex.Message})");
                    }

                    progress?.Report((idx + 1) * 100 / total);
                }

                if (failedFiles.Count > 0)
                {
                    FileLogger.LogError("MergePdfs", new Exception(
                        $"Birleştirilemeyen dosyalar: {string.Join(", ", failedFiles)}"));
                }

                if (doc.PageCount == 0)
                    throw new InvalidOperationException("Birleştirilecek geçerli PDF sayfası bulunamadı.");

                doc.Save(output);

                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    throw new Exception("PDF dosyası oluşturulamadı");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Bir PDF dosyasını sayfa aralığına göre böler.
        /// CancellationToken ve IProgress desteği sunar.
        /// </summary>
        public async Task SplitPdfAsync(
            string sourcePath,
            string outputDir,
            List<(int From, int To)> pageRanges,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            await Task.Run(() =>
            {
                PdfDocument? source = null;

                var retryResult = RetryPolicy.ExecuteAndCaptureAsync(() =>
                {
                    source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                    return Task.FromResult(source);
                }).GetAwaiter().GetResult();

                if (retryResult.Outcome == OutcomeType.Failure || source == null)
                {
                    FileLogger.LogError("SplitPdf", new Exception(
                        $"PDF dosyası açılamadı: {Path.GetFileName(sourcePath)}"));
                    return;
                }

                using var sourceDoc = source!;
                Directory.CreateDirectory(outputDir);
                string baseName = Path.GetFileNameWithoutExtension(sourcePath);

                int rangeIndex = 0;
                int totalRanges = pageRanges.Count;

                foreach (var (from, to) in pageRanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (from < 1 || to < from || from > sourceDoc.PageCount)
                    {
                        FileLogger.LogError("SplitPdf",
                            new ArgumentOutOfRangeException(
                                $"Geçersiz aralık: {from}-{to} (toplam sayfa: {sourceDoc.PageCount})"));
                        continue;
                    }

                    int safeTo = Math.Min(to, sourceDoc.PageCount);

                    using var part = new PdfDocument();

                    try
                    {
                        for (int p = from - 1; p < safeTo; p++)
                            part.AddPage(sourceDoc.Pages[p]);

                        string outPath = Path.Combine(outputDir, $"{baseName}_sayfa{from}-{safeTo}.pdf");
                        part.Save(outPath);

                        if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                        {
                            FileLogger.LogError("SplitPdf",
                                new Exception($"Sayfa aralığı kaydedilemedi: {from}-{safeTo}"));
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogError("SplitPdf",
                            new Exception($"Sayfa aralığı işlenemedi: {from}-{safeTo}", ex));
                    }

                    rangeIndex++;
                    progress?.Report((rangeIndex * 100) / totalRanges);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// SplitPdfAsync'in eski versiyonu (geriye uyumlu).
        /// </summary>
        [Obsolete("Use overload with CancellationToken instead")]
        public async Task SplitPdfAsync(
            string sourcePath,
            string outputDir,
            List<(int From, int To)> pageRanges)
        {
            await SplitPdfAsync(sourcePath, outputDir, pageRanges, CancellationToken.None, null);
        }

        /// <summary>
        /// PDF'in belirtilen sayfalarını çıkarır ve yeni bir PDF olarak kaydeder.
        /// </summary>
        public async Task ExtractPagesAsync(
            string sourcePath,
            string outputPath,
            List<int> pageNumbers,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                using var sourceDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                using var newDoc = new PdfDocument();

                foreach (var pageNum in pageNumbers)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (pageNum < 1 || pageNum > sourceDoc.PageCount)
                        continue;

                    newDoc.AddPage(sourceDoc.Pages[pageNum - 1]);
                }

                newDoc.Save(outputPath);

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    throw new Exception("Sayfa çıkarılamadı: çıktı dosyası oluşturulamadı");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// PDF'in tüm sayfalarını döndürür.
        /// </summary>
        public async Task RotateAllPagesAsync(
            string sourcePath,
            int degrees,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                using var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);

                foreach (PdfPage page in doc.Pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    page.Rotate = (page.Rotate + degrees) % 360;
                }

                doc.Save(sourcePath);
            }, cancellationToken);
        }

        /// <summary>
        /// PDF iÃ§indeki bir sayfayÄ± yeni pozisyona taÅŸÄ±r.
        /// </summary>
        public async Task MovePageAsync(
            string pdfPath,
            int fromIndex,
            int toIndex,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pdfPath))
                throw new ArgumentException("PDF dosya yolu boÅŸ olamaz.", nameof(pdfPath));

            if (!File.Exists(pdfPath))
                throw new FileNotFoundException("PDF dosyasÄ± bulunamadÄ±.", pdfPath);

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                string directory = Path.GetDirectoryName(pdfPath) ?? Directory.GetCurrentDirectory();
                string fileName = Path.GetFileNameWithoutExtension(pdfPath);
                string tempPath = Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp.pdf");
                string backupPath = Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.bak");

                try
                {
                    int pageCount;

                    using (var sourceDoc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
                    {
                        pageCount = sourceDoc.PageCount;
                        if (fromIndex < 0 || fromIndex >= pageCount)
                            throw new ArgumentOutOfRangeException(nameof(fromIndex), "Kaynak sayfa indeksi geÃ§ersiz.");

                        if (toIndex < 0 || toIndex >= pageCount)
                            throw new ArgumentOutOfRangeException(nameof(toIndex), "Hedef sayfa indeksi geÃ§ersiz.");

                        if (fromIndex == toIndex)
                            return;

                        var order = new List<int>();
                        for (int i = 0; i < pageCount; i++)
                            order.Add(i);

                        int movedPage = order[fromIndex];
                        order.RemoveAt(fromIndex);
                        order.Insert(toIndex, movedPage);

                        using var newDoc = new PdfDocument();
                        foreach (int pageIndex in order)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            newDoc.AddPage(sourceDoc.Pages[pageIndex]);
                        }

                        newDoc.Save(tempPath);
                    }

                    if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                        throw new IOException("TaÅŸÄ±nan PDF kaydedilemedi.");

                    cancellationToken.ThrowIfCancellationRequested();
                    File.Replace(tempPath, pdfPath, backupPath, ignoreMetadataErrors: true);

                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
                }
                finally
                {
                    try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                }
            }, cancellationToken);
        }
    }
}
