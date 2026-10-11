using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DocConverter.Models;
using Microsoft.Win32;

namespace DocConverter.Services
{
    /// <summary>Bilgisayarda hangi dönüştürme motorlarının bulunduğunu söyler.</summary>
    public interface IOfficeEngineProbe
    {
        bool IsOfficeAppInstalled(OfficeApp app);

        /// <summary>Kullanıcının kurduğu LibreOffice'i, yoksa DocMaster Pro'nun indirdiği kopyayı döndürür.</summary>
        LibreOfficeInstallation? FindLibreOffice();
    }

    /// <summary>DocMaster Pro'nun kendi indirdiği LibreOffice kopyasının diskteki yeri.</summary>
    public static class DownloadedEngine
    {
        /// <summary>Açma işlemi eksiksiz bittiğinde yazılan dosya; yoksa klasör yarım kalmış sayılır ve kullanılmaz.</summary>
        public const string ReadyMarker = "engine.ready";

        /// <summary>
        /// Kurulum kökünün (%LOCALAPPDATA%\DocMasterPro) altındadır: güncellemeler yalnızca "current" klasörünü
        /// değiştirdiği için korunur, uygulama kaldırıldığında kökle birlikte silinir.
        /// </summary>
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocMasterPro", "engine");

        public static string DirectoryFor(string version) => Path.Combine(Root, "libreoffice-" + version);
    }

    public sealed class OfficeEngineProbe : IOfficeEngineProbe
    {
        private readonly string _downloadRoot;
        private readonly Func<IEnumerable<string>> _installedProgramDirectories;

        public OfficeEngineProbe()
            : this(DownloadedEngine.Root, InstalledProgramDirectories)
        {
        }

        internal OfficeEngineProbe(string downloadRoot, Func<IEnumerable<string>> installedProgramDirectories)
        {
            _downloadRoot = downloadRoot;
            _installedProgramDirectories = installedProgramDirectories;
        }

        /// <summary>
        /// Uygulamayı başlatmadan, yalnızca kayıt defterine bakarak denetler (Word'ü açıp kapatmak saniyeler sürer).
        /// </summary>
        public bool IsOfficeAppInstalled(OfficeApp app)
        {
            string progId = app switch
            {
                OfficeApp.Word => "Word.Application",
                OfficeApp.Excel => "Excel.Application",
                _ => "PowerPoint.Application"
            };

            try
            {
                using RegistryKey? progIdKey = Registry.ClassesRoot.OpenSubKey(progId + @"\CLSID");
                if (progIdKey?.GetValue(null) is not string clsid || string.IsNullOrWhiteSpace(clsid))
                    return false;

                // Kaldırılmış bir Office'in kayıtları geride kalmış olabilir: sunucu dosyası bulunabiliyorsa varlığına bak.
                string? server = ReadLocalServer(clsid, RegistryView.Registry64) ?? ReadLocalServer(clsid, RegistryView.Registry32);
                return server == null || File.Exists(server);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        public LibreOfficeInstallation? FindLibreOffice()
        {
            // Kullanıcının kendi kurduğu kopya önceliklidir; güncellemelerini kendisi alır.
            foreach (string programDirectory in _installedProgramDirectories())
            {
                LibreOfficeInstallation? installed = FromProgramDirectory(programDirectory, isDownloaded: false);
                if (installed != null)
                    return installed;
            }

            return FindDownloaded();
        }

        private LibreOfficeInstallation? FindDownloaded()
        {
            try
            {
                if (!Directory.Exists(_downloadRoot))
                    return null;

                return Directory.EnumerateDirectories(_downloadRoot, "libreoffice-*")
                    .Where(directory => File.Exists(Path.Combine(directory, DownloadedEngine.ReadyMarker)))
                    .Select(directory => FromProgramDirectory(Path.Combine(directory, "program"), isDownloaded: true))
                    .Where(installation => installation != null)
                    .OrderByDescending(installation => ParseVersion(installation!.Version))
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static LibreOfficeInstallation? FromProgramDirectory(string programDirectory, bool isDownloaded)
        {
            try
            {
                // soffice.exe yalnızca başlatıcıdır; asıl program soffice.bin'dir.
                string launcher = Path.Combine(programDirectory, "soffice.exe");
                if (!File.Exists(launcher) || !File.Exists(Path.Combine(programDirectory, "soffice.bin")))
                    return null;

                string console = Path.Combine(programDirectory, "soffice.com");
                return new LibreOfficeInstallation(
                    File.Exists(console) ? console : launcher,
                    ReadVersion(programDirectory),
                    isDownloaded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Sürümü soffice.bin'in dosya sürümünden okur ("26.2.6.3" → "26.2.6"). Dosyada sürüm bilgisi yoksa eski
        /// LibreOffice sürümlerinin program\version.ini dosyasına yazdığı "MsiProductVersion" satırına bakar.
        /// </summary>
        internal static string? ReadVersion(string programDirectory)
        {
            try
            {
                string binary = Path.Combine(programDirectory, "soffice.bin");
                if (File.Exists(binary) && ShortVersion(FileVersionInfo.GetVersionInfo(binary).FileVersion) is { } fromFile)
                    return fromFile;

                string iniPath = Path.Combine(programDirectory, "version.ini");
                if (!File.Exists(iniPath))
                    return null;

                foreach (string line in File.ReadLines(iniPath))
                {
                    const string key = "MsiProductVersion=";
                    if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        return ShortVersion(line.Substring(key.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }

        private static string? ShortVersion(string? version)
        {
            if (!Version.TryParse(version?.Trim(), out Version? parsed))
                return null;

            return parsed.Build >= 0 ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build}" : $"{parsed.Major}.{parsed.Minor}";
        }

        private static Version ParseVersion(string? version) =>
            Version.TryParse(version, out Version? parsed) ? parsed : new Version(0, 0);

        private static string? ReadLocalServer(string clsid, RegistryView view)
        {
            using RegistryKey classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using RegistryKey? serverKey = classes.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
            if (serverKey?.GetValue(null) is not string command || string.IsNullOrWhiteSpace(command))
                return null;

            // Değer '"C:\...\WINWORD.EXE" /Automation' ya da tırnaksız 'C:\...\EXCEL.EXE /automation' biçimindedir.
            command = command.Trim();
            if (command.StartsWith('"'))
            {
                int closing = command.IndexOf('"', 1);
                return closing > 1 ? command.Substring(1, closing - 1) : null;
            }

            int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? command.Substring(0, exe + 4) : null;
        }

        private static IEnumerable<string> InstalledProgramDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    string? path = null;
                    try
                    {
                        using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                        using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
                        path = key?.GetValue(null) as string;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                    }

                    if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                        yield return path;
                }
            }

            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string programFiles = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(programFiles))
                    continue;

                string path = Path.Combine(programFiles, "LibreOffice", "program");
                if (seen.Add(path))
                    yield return path;
            }
        }
    }
}
