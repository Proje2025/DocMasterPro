using System.IO;
using DocConverter.Models;
using PdfSharp.Pdf.IO;

namespace DocConverter.Services;

public class PdfSessionService
{
    private readonly string _sessionRoot;

    public PdfSessionService()
        : this(Path.Combine(Path.GetTempPath(), "DocMasterPro", "Sessions"))
    {
    }

    public PdfSessionService(string sessionRoot)
    {
        _sessionRoot = sessionRoot;
    }

    public Task<PdfDocumentSession> OpenPdfSessionAsync(
        string sourcePath,
        int? knownPageCount = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("PDF dosya yolu boş olamaz.", nameof(sourcePath));

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("PDF dosyası bulunamadı.", sourcePath);

        if (!string.Equals(Path.GetExtension(sourcePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sadece PDF dosyaları açılabilir.");

        cancellationToken.ThrowIfCancellationRequested();

        int pageCount = knownPageCount ?? GetPageCount(sourcePath);
        if (pageCount <= 0)
            throw new InvalidOperationException("PDF açılamadı veya sayfa bulunamadı. Dosya bozuk, şifreli veya erişim kısıtlı olabilir.");

        string sessionFolder = Path.Combine(_sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);

        var session = new PdfDocumentSession(sourcePath, sourcePath, sessionFolder, pageCount, DateTime.Now);
        return Task.FromResult(session);
    }

    public async Task<string> EnsureWorkingCopyAsync(PdfDocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));

        if (session.IsWorkingCopyReady && File.Exists(session.WorkingPath))
            return session.WorkingPath;

        await session.WorkingCopyGate.WaitAsync(cancellationToken);
        try
        {
            if (session.IsWorkingCopyReady && File.Exists(session.WorkingPath))
                return session.WorkingPath;

            Directory.CreateDirectory(session.SessionFolder);

            string workingPath = Path.Combine(session.SessionFolder, Path.GetFileName(session.OriginalPath));
            string tempPath = Path.Combine(session.SessionFolder, $"{Path.GetFileNameWithoutExtension(session.OriginalPath)}.{Guid.NewGuid():N}.tmp.pdf");

            try
            {
                await using (var input = File.Open(session.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await using (var output = File.Create(tempPath))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                    throw new IOException("GeÃ§ici PDF Ã§alÄ±ÅŸma kopyasÄ± doÄŸrulanamadÄ±.");

                if (File.Exists(workingPath))
                    File.Delete(workingPath);

                File.Move(tempPath, workingPath);
                session.WorkingPath = workingPath;
                session.IsWorkingCopyReady = true;

                return workingPath;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }
        finally
        {
            session.WorkingCopyGate.Release();
        }
    }

    public async Task AtomicSaveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Kaydedilecek PDF çıktısı bulunamadı.", sourcePath);

        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Hedef dosya yolu boş olamaz.", nameof(destinationPath));

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string targetDirectory = directory ?? Directory.GetCurrentDirectory();
        string tempPath = Path.Combine(targetDirectory, $"{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.tmp.pdf");
        string backupPath = Path.Combine(targetDirectory, $"{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.bak");

        await using (var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            throw new IOException("Geçici PDF çıktısı doğrulanamadı.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
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
    }

    public void CleanupSession(PdfDocumentSession? session)
    {
        if (session == null)
            return;

        try
        {
            if (Directory.Exists(session.SessionFolder))
                Directory.Delete(session.SessionFolder, recursive: true);
        }
        catch (Exception ex)
        {
            FileLogger.LogError("PdfSessionCleanup", ex);
        }
    }

    private static int GetPageCount(string pdfPath)
    {
        try
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            return doc.PageCount;
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException("PDF okunamadı. Dosya şifreli, bozuk veya kısıtlı olabilir.", ex);
        }
    }
}
