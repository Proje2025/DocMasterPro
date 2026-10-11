using System;
using System.Globalization;

namespace DocConverter.Models
{
    /// <summary>Office belgelerini PDF'e çevirmek için kullanılabilecek motor.</summary>
    public enum OfficeEngineKind
    {
        /// <summary>Ne Microsoft Office ne de LibreOffice var.</summary>
        None,

        /// <summary>Microsoft Office kurulu: her zaman ilk tercih, çıktı özgün düzenle birebir.</summary>
        MicrosoftOffice,

        /// <summary>Kullanıcının kendi kurduğu LibreOffice.</summary>
        LibreOfficeInstalled,

        /// <summary>DocMaster Pro'nun kendi klasörüne indirdiği LibreOffice.</summary>
        LibreOfficeDownloaded
    }

    /// <summary>
    /// Şu anda hangi dönüştürme motorunun kullanılacağını ve arayüzde nasıl gösterileceğini tanımlar.
    /// </summary>
    public sealed record OfficeEngineStatus(OfficeEngineKind Kind, string? Version = null)
    {
        public bool CanConvertToPdf => Kind != OfficeEngineKind.None;

        /// <summary>PDF → Word yalnızca Word'ün kendi dönüştürücüsüyle yapılır.</summary>
        public bool CanConvertPdfToWord => Kind == OfficeEngineKind.MicrosoftOffice;

        public bool IsLibreOffice =>
            Kind is OfficeEngineKind.LibreOfficeInstalled or OfficeEngineKind.LibreOfficeDownloaded;

        /// <summary>İndirilen kopya kullanıcı isterse kaldırılabilir.</summary>
        public bool CanRemove => Kind == OfficeEngineKind.LibreOfficeDownloaded;

        public string Title => Kind switch
        {
            OfficeEngineKind.MicrosoftOffice => "Dönüştürme motoru: Microsoft Office",
            OfficeEngineKind.LibreOfficeInstalled => $"Dönüştürme motoru: LibreOffice{VersionSuffix} (bilgisayarınızda kurulu)",
            OfficeEngineKind.LibreOfficeDownloaded => $"Dönüştürme motoru: LibreOffice{VersionSuffix} (DocMaster Pro indirdi)",
            _ => "Dönüştürme motoru bulunamadı"
        };

        public string Detail => Kind switch
        {
            OfficeEngineKind.MicrosoftOffice =>
                "Belgeler özgün düzenleriyle birebir dönüştürülür.",
            OfficeEngineKind.LibreOfficeInstalled or OfficeEngineKind.LibreOfficeDownloaded =>
                "Word ve PowerPoint belgeleri Office ile neredeyse aynı çıkar; Excel grafikleri farklı görünebilir.",
            _ =>
                "Microsoft Office kurulu değil. İlk dönüştürmede ücretsiz LibreOffice'i indirmek isteyip istemediğiniz sorulur."
        };

        private string VersionSuffix => string.IsNullOrWhiteSpace(Version) ? "" : " " + Version;
    }

    /// <summary>Bir belge türünü açan Microsoft Office uygulaması.</summary>
    public enum OfficeApp
    {
        Word,
        Excel,
        PowerPoint
    }

    /// <summary>Bulunan bir LibreOffice kopyası.</summary>
    /// <param name="SofficePath">Çalıştırılacak dosya (varsa çıktısı okunabilen soffice.com, yoksa soffice.exe).</param>
    /// <param name="Version">Ör. "26.2.6"; okunamadıysa null.</param>
    /// <param name="IsDownloaded">DocMaster Pro'nun kendi klasörüne indirdiği kopya ise true.</param>
    public sealed record LibreOfficeInstallation(string SofficePath, string? Version, bool IsDownloaded);

    /// <summary>Belgeyi dönüştürebilecek ne Microsoft Office uygulaması ne de LibreOffice bulunduğunda fırlatılır.</summary>
    public class OfficeEngineMissingException : InvalidOperationException
    {
        public OfficeEngineMissingException(OfficeApp app)
            : base($"Bu belgeyi dönüştürmek için Microsoft {app} ya da LibreOffice gerekir; bilgisayarda ikisi de bulunamadı.")
        {
            App = app;
        }

        public OfficeApp App { get; }
    }

    /// <summary>İndirme öncesinde kullanıcıya gösterilen gereksinimler.</summary>
    public sealed record EngineSetupRequirements(
        string Version,
        long DownloadBytes,
        long RequiredFreeBytes,
        long AvailableFreeBytes,
        string TargetDrive,
        string Publisher)
    {
        public bool HasEnoughSpace => AvailableFreeBytes >= RequiredFreeBytes;
    }

    public enum EngineSetupStage
    {
        Downloading,
        Verifying,
        Preparing
    }

    public readonly record struct EngineSetupProgress(EngineSetupStage Stage, long BytesDone, long BytesTotal);

    public enum EngineSetupError
    {
        /// <summary>İnternet yok ya da indirme yarıda kesildi.</summary>
        DownloadFailed,

        /// <summary>Dijital imza geçersiz ya da beklenen yayıncıya ait değil. Dosya çalıştırılmaz.</summary>
        SignatureInvalid,

        /// <summary>Hedef sürücüde yeterli boş alan yok.</summary>
        DiskFull,

        Unknown
    }

    public class EngineSetupException : Exception
    {
        public EngineSetupException(EngineSetupError error, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            Error = error;
        }

        public EngineSetupError Error { get; }
    }

    /// <summary>Arayüz metinlerinde boyutları Türkçe biçimde yazar (ör. "373 MB", "2,0 GB").</summary>
    public static class SizeText
    {
        private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

        public static string Format(long bytes)
        {
            const double megabyte = 1024d * 1024d;
            const double gigabyte = megabyte * 1024d;

            if (bytes >= gigabyte)
                return (bytes / gigabyte).ToString("0.0", Turkish) + " GB";

            return Math.Round(bytes / megabyte).ToString("0", Turkish) + " MB";
        }
    }
}
