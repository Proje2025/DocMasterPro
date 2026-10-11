using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;

namespace DocConverter.Services
{
    /// <summary>
    /// Office belgelerini PDF'e çevirirken motoru seçer: belgenin kendi Microsoft Office uygulaması kuruluysa o
    /// (çıktı özgün düzenle birebir), değilse LibreOffice; ikisi de yoksa <see cref="OfficeEngineMissingException"/>.
    /// </summary>
    public class OfficeToPdfConverter
    {
        private const int ClassNotRegistered = unchecked((int)0x80040154);
        private const int ServerExecutionFailed = unchecked((int)0x80080005);

        private readonly IOfficeEngineProbe _probe;
        private readonly IDocumentPdfEngine _office;
        private readonly Func<LibreOfficeInstallation, IDocumentPdfEngine> _libreOffice;

        public OfficeToPdfConverter()
            : this(new OfficeEngineProbe(), new OfficeConverterService(), installation => new LibreOfficeConverter(installation.SofficePath))
        {
        }

        public OfficeToPdfConverter(
            IOfficeEngineProbe probe,
            IDocumentPdfEngine office,
            Func<LibreOfficeInstallation, IDocumentPdfEngine> libreOffice)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _office = office ?? throw new ArgumentNullException(nameof(office));
            _libreOffice = libreOffice ?? throw new ArgumentNullException(nameof(libreOffice));
        }

        /// <summary>Uzantıyı açan Office uygulaması; desteklenmeyen uzantıda null.</summary>
        public static OfficeApp? AppFor(string extension) => extension.ToLowerInvariant() switch
        {
            ".docx" or ".doc" or ".rtf" or ".txt" or ".html" or ".htm" => OfficeApp.Word,
            ".xlsx" or ".xls" => OfficeApp.Excel,
            ".pptx" or ".ppt" => OfficeApp.PowerPoint,
            _ => null
        };

        /// <summary>PDF → Word yalnızca Word'ün kendi dönüştürücüsüyle yapılır.</summary>
        public bool IsWordInstalled => _probe.IsOfficeAppInstalled(OfficeApp.Word);

        /// <summary>Arayüzdeki motor göstergesi için özet. Word, Microsoft Office'in kurulu olduğunun ölçütüdür.</summary>
        public OfficeEngineStatus GetStatus()
        {
            if (_probe.IsOfficeAppInstalled(OfficeApp.Word))
                return new OfficeEngineStatus(OfficeEngineKind.MicrosoftOffice);

            return _probe.FindLibreOffice() is { } libreOffice
                ? new OfficeEngineStatus(KindOf(libreOffice), libreOffice.Version)
                : new OfficeEngineStatus(OfficeEngineKind.None);
        }

        /// <summary>Bu uzantıdaki bir belge için kullanılacak motor.</summary>
        public OfficeEngineKind ResolveEngine(string extension)
        {
            if (AppFor(extension) is not { } app)
                return OfficeEngineKind.None;

            if (_probe.IsOfficeAppInstalled(app))
                return OfficeEngineKind.MicrosoftOffice;

            return _probe.FindLibreOffice() is { } libreOffice ? KindOf(libreOffice) : OfficeEngineKind.None;
        }

        public bool HasEngineFor(string extension) => ResolveEngine(extension) != OfficeEngineKind.None;

        public async Task ConvertAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        {
            string extension = Path.GetExtension(inputPath);
            OfficeApp app = AppFor(extension)
                ?? throw new NotSupportedException($"'{extension}' türündeki dosyalar PDF'e dönüştürülemiyor.");

            LibreOfficeInstallation? libreOffice = null;

            if (_probe.IsOfficeAppInstalled(app))
            {
                try
                {
                    await _office.ConvertToPdfAsync(inputPath, outputPath, cancellationToken);
                    return;
                }
                catch (COMException ex) when (IsOfficeUnavailable(ex) && (libreOffice = _probe.FindLibreOffice()) != null)
                {
                    // Kayıtları duran ama başlatılamayan (bozuk ya da yarım kaldırılmış) bir Office kurulumu.
                    FileLogger.LogError($"Microsoft {app} başlatılamadı, LibreOffice ile deneniyor", ex);
                }
            }

            libreOffice ??= _probe.FindLibreOffice();
            if (libreOffice == null)
                throw new OfficeEngineMissingException(app);

            await _libreOffice(libreOffice).ConvertToPdfAsync(inputPath, outputPath, cancellationToken);
        }

        private static OfficeEngineKind KindOf(LibreOfficeInstallation installation) =>
            installation.IsDownloaded ? OfficeEngineKind.LibreOfficeDownloaded : OfficeEngineKind.LibreOfficeInstalled;

        private static bool IsOfficeUnavailable(COMException ex) =>
            ex.HResult is ClassNotRegistered or ServerExecutionFailed;
    }
}
