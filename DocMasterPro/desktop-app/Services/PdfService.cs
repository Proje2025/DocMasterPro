using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DocConverter.Services
{
    public class PdfService
    {
        /// <summary>
        /// Birden fazla PDF dosyasını tek bir çıktı dosyasında birleştirir.
        /// </summary>
        public async Task MergePdfsAsync(
            List<string> files,
            string output,
            IProgress<int>? progress = null)
        {
            await Task.Run(() =>
            {
                using var doc = new PdfDocument();
                int total = files.Count;
                var failedFiles = new List<string>();

                for (int idx = 0; idx < total; idx++)
                {
                    var f = files[idx];
                    PdfDocument? input = null;

                    // Retry mekanizması - dosya kilidi serbest bırakılana kadar dene
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            input = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                            break;
                        }
                        catch (IOException) when (retry < 4)
                        {
                            Thread.Sleep(200); // 200ms bekle ve tekrar dene
                        }
                        catch (PdfReaderException) when (retry < 4)
                        {
                            Thread.Sleep(200);
                        }
                    }

                    if (input == null)
                    {
                        failedFiles.Add(Path.GetFileName(f));
                        continue;
                    }

                    try
                    {
                        for (int p = 0; p < input.PageCount; p++)
                            doc.AddPage(input.Pages[p]);
                    }
                    catch (Exception ex)
                    {
                        failedFiles.Add($"{Path.GetFileName(f)} ({ex.Message})");
                    }
                    finally
                    {
                        input.Dispose();
                    }

                    progress?.Report((idx + 1) * 100 / total);
                }

                // Başarısız dosya varsa raporla
                if (failedFiles.Count > 0)
                {
                    FileLogger.LogError("MergePdfs", new Exception($"Birleştirilemeyen dosyalar: {string.Join(", ", failedFiles)}"));
                }

                // PDF'i kaydet ve doğrula
                doc.Save(output);

                // Kayıt başarılı mı kontrol et
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    throw new Exception("PDF dosyası oluşturulamadı");
                }
            });
        }

        /// <summary>
        /// Bir PDF dosyasını sayfa aralığına göre böler.
        /// </summary>
        public async Task SplitPdfAsync(
            string sourcePath,
            string outputDir,
            List<(int From, int To)> pageRanges)
        {
            await Task.Run(() =>
            {
                PdfDocument? source = null;

                // Retry mekanizması
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                        break;
                    }
                    catch (IOException) when (retry < 4)
                    {
                        Thread.Sleep(200);
                    }
                    catch (PdfReaderException) when (retry < 4)
                    {
                        Thread.Sleep(200);
                    }
                }

                if (source == null)
                {
                    FileLogger.LogError("SplitPdf", new Exception($"PDF dosyası açılamadı: {Path.GetFileName(sourcePath)}"));
                    return;
                }

                Directory.CreateDirectory(outputDir);
                string baseName = Path.GetFileNameWithoutExtension(sourcePath);

                try
                {
                    foreach (var (from, to) in pageRanges)
                    {
                        if (from < 1 || to < from || from > source.PageCount)
                        {
                            FileLogger.LogError("SplitPdf",
                                new ArgumentOutOfRangeException($"Geçersiz aralık: {from}-{to} (toplam sayfa: {source.PageCount})"));
                            continue;
                        }

                        int safeTo = Math.Min(to, source.PageCount);
                        using var part = new PdfDocument();

                        try
                        {
                            for (int p = from - 1; p < safeTo; p++)
                                part.AddPage(source.Pages[p]);

                            string outPath = Path.Combine(outputDir, $"{baseName}_sayfa{from}-{safeTo}.pdf");
                            part.Save(outPath);

                            // Her sayfa için doğrulama
                            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                            {
                                FileLogger.LogError("SplitPdf", new Exception($"Sayfa aralığı kaydedilemedi: {from}-{safeTo}"));
                            }
                        }
                        catch (Exception ex)
                        {
                            FileLogger.LogError("SplitPdf", new Exception($"Sayfa aralığı işlenemedi: {from}-{safeTo}", ex));
                        }
                    }
                }
                finally
                {
                    source.Dispose();
                }
            });
        }
    }
}
