using DocConverter.Models;
using DocConverter.Services;
using UglyToad.PdfPig;
using Xunit;

namespace DocConverter.Tests;

/// <summary>Gerçek LibreOffice gerektiren testler; bulunamazsa atlanır (ör. CI makinesinde).</summary>
public sealed class RequiresLibreOfficeFactAttribute : FactAttribute
{
    public RequiresLibreOfficeFactAttribute()
    {
        if (OfficeTestEnvironment.LibreOffice == null)
            Skip = "LibreOffice bulunamadı (DOCMASTER_TEST_SOFFICE ile program klasörü verilebilir).";
    }
}

/// <summary>Gerçek Microsoft Office uygulaması gerektiren testler; kurulu değilse atlanır.</summary>
public sealed class RequiresOfficeFactAttribute : FactAttribute
{
    public RequiresOfficeFactAttribute(OfficeApp app)
    {
        if (!new OfficeEngineProbe().IsOfficeAppInstalled(app))
            Skip = $"Microsoft {app} kurulu değil.";
    }
}

/// <summary>Daha önce indirilmiş gerçek LibreOffice kurulum dosyasını gerektiren testler; yoksa atlanır.</summary>
public sealed class RequiresLocalInstallerFactAttribute : FactAttribute
{
    public RequiresLocalInstallerFactAttribute()
    {
        if (OfficeTestEnvironment.LocalInstaller == null)
            Skip = "Test_Ciktilari/lo-test/download altında LibreOffice kurulum dosyası yok.";
    }
}

internal static class OfficeTestEnvironment
{
    /// <summary>
    /// Gerçek Office ve LibreOffice çalıştıran testlerin koleksiyonu: sırayla koşarlar, böylece süre ve işlem
    /// sayısı ölçümleri birbirini bozmaz.
    /// </summary>
    public const string Collection = "Office motorları";

    /// <summary>
    /// Sırasıyla: DOCMASTER_TEST_SOFFICE ortam değişkenindeki program klasörü, bilgisayarda bulunan LibreOffice,
    /// depo kökündeki Test_Ciktilari\lo-test\engine (yerel deneme kopyası).
    /// </summary>
    public static LibreOfficeInstallation? LibreOffice { get; } = FindLibreOffice();

    /// <summary>Depo kökündeki Test_Ciktilari/lo-test/download klasöründe duran kurulum dosyası (varsa).</summary>
    public static string? LocalInstaller { get; } = FindLocalInstaller();

    public static string Sample(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    public static string NewTempDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), "DocMasterPro.Tests", prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static (int Pages, string Text) ReadPdf(string path)
    {
        using PdfDocument document = PdfDocument.Open(path);
        return (document.NumberOfPages, string.Join("\n", document.GetPages().Select(page => page.Text)));
    }

    private static string? FindLocalInstaller()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string download = Path.Combine(directory.FullName, "Test_Ciktilari", "lo-test", "download");
            if (Directory.Exists(download))
                return Directory.GetFiles(download, "*.msi").FirstOrDefault();
        }

        return null;
    }

    private static LibreOfficeInstallation? FindLibreOffice()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("DOCMASTER_TEST_SOFFICE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) &&
            OfficeEngineProbe.FromProgramDirectory(fromEnvironment, isDownloaded: false) is { } configured)
            return configured;

        if (new OfficeEngineProbe().FindLibreOffice() is { } found)
            return found;

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Test_Ciktilari", "lo-test", "engine", "program");
            if (OfficeEngineProbe.FromProgramDirectory(candidate, isDownloaded: true) is { } local)
                return local;
        }

        return null;
    }
}
