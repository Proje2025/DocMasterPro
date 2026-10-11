using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DocConverter.Services
{
    /// <summary>Bir belgeyi PDF'e çeviren motor (Microsoft Office ya da LibreOffice).</summary>
    public interface IDocumentPdfEngine
    {
        Task ConvertToPdfAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Belgeleri penceresiz çalışan LibreOffice ile PDF'e çevirir.
    /// <list type="bullet">
    /// <item>Belge geçici bir klasöre kopyalanır; kullanıcının klasörüne kilit dosyası ya da yarım çıktı yazılmaz.</item>
    /// <item>Kendi profilini kullanır: kullanıcının LibreOffice ayarlarına dokunmaz, açık LibreOffice ile çakışmaz.</item>
    /// <item>Profilde makrolar kapalıdır ve belgenin dış bağlantıları yüklenmez.</item>
    /// <item>Süre aşımında ve iptalde LibreOffice işlemleri sonlandırılır.</item>
    /// </list>
    /// </summary>
    public sealed class LibreOfficeConverter : IDocumentPdfEngine
    {
        // Tek profil kullanıldığı için aynı anda tek dönüştürme yapılır; ikinci bir soffice aynı profille başlatılırsa
        // işi ilkine devredip hemen çıkar ve sonucu izlenemez.
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private const string JobFileName = "belge";

        private readonly string _sofficePath;
        private readonly string _workRoot;

        public LibreOfficeConverter(string sofficePath, string? workRoot = null)
        {
            _sofficePath = sofficePath ?? throw new ArgumentNullException(nameof(sofficePath));
            _workRoot = workRoot ?? DefaultWorkRoot;
        }

        /// <summary>Tek bir belge için tanınan en uzun süre.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>Bu çalıştırmaya özel geçici klasör: profil ve iş dosyaları burada tutulur.</summary>
        public static string DefaultWorkRoot { get; } =
            Path.Combine(Path.GetTempPath(), "DocMasterPro", "lo-" + Environment.ProcessId);

        public async Task ConvertToPdfAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        {
            inputPath = Path.GetFullPath(inputPath);
            outputPath = Path.GetFullPath(outputPath);

            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Belge bulunamadı.", inputPath);
            if (!File.Exists(_sofficePath))
                throw new FileNotFoundException("LibreOffice bulunamadı.", _sofficePath);

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string jobDirectory = Path.Combine(_workRoot, "job-" + Guid.NewGuid().ToString("N"));
            try
            {
                string outDirectory = Path.Combine(jobDirectory, "out");
                string extension = Path.GetExtension(inputPath).ToLowerInvariant();
                string source = Path.Combine(jobDirectory, JobFileName + extension);
                string profileDirectory = Path.Combine(_workRoot, "profile");

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(outDirectory);
                    PrepareSource(inputPath, source, extension);
                    EnsureProfile(profileDirectory);
                }, cancellationToken).ConfigureAwait(false);

                (int exitCode, string output) = await RunAsync(
                    BuildArguments(profileDirectory, outDirectory, source, extension), jobDirectory, cancellationToken).ConfigureAwait(false);

                string produced = Path.Combine(outDirectory, JobFileName + ".pdf");
                if (!File.Exists(produced) || new FileInfo(produced).Length == 0)
                {
                    // Sonraki denemenin bozulmuş olabilecek bir profille başlamaması için.
                    TryDeleteDirectory(profileDirectory);
                    FileLogger.LogInfo($"LibreOffice çıktı üretmedi ({Path.GetFileName(inputPath)}), çıkış kodu {exitCode}: {Shorten(output)}");
                    throw new InvalidOperationException(
                        "LibreOffice belgeyi PDF'e dönüştüremedi. Belge bozuk ya da parola korumalı olabilir.");
                }

                string? targetDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);
                File.Move(produced, outputPath, overwrite: true);
            }
            finally
            {
                TryDeleteDirectory(jobDirectory);
                Gate.Release();
            }
        }

        /// <summary>Uygulama kapanırken bu oturumun geçici profilini ve iş klasörlerini siler.</summary>
        public static void CleanupWorkFiles() => TryDeleteDirectory(DefaultWorkRoot);

        /// <summary>
        /// Uygulama açılırken, çökme ya da zorla kapatma sonrası geride kalmış oturum klasörlerini siler; içlerinde
        /// dönüştürülmekte olan belgelerin kopyaları kalmış olabilir. Hâlâ çalışan başka bir DocMaster Pro
        /// penceresinin klasörüne dokunulmaz.
        /// </summary>
        public static void CleanupStaleWorkFiles()
        {
            try
            {
                string parent = Path.GetDirectoryName(DefaultWorkRoot)!;
                if (!Directory.Exists(parent))
                    return;

                foreach (string directory in Directory.EnumerateDirectories(parent, "lo-*"))
                {
                    if (!IsOwnedByRunningInstance(directory))
                        TryDeleteDirectory(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        internal static bool IsOwnedByRunningInstance(string workDirectory)
        {
            string name = Path.GetFileName(workDirectory);
            if (!name.StartsWith("lo-", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(3), out int processId))
                return false;

            if (processId == Environment.ProcessId)
                return true;

            try
            {
                using Process owner = Process.GetProcessById(processId);
                using Process current = Process.GetCurrentProcess();
                return string.Equals(owner.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // İşlem artık çalışmıyor.
                return false;
            }
        }

        internal static IReadOnlyList<string> BuildArguments(string profileDirectory, string outDirectory, string source, string extension)
        {
            var arguments = new List<string>
            {
                "--headless",
                "--invisible",
                "--nologo",
                "--norestore",
                "--nodefault",
                "--nolockcheck",
                "--nofirststartwizard",
                "-env:UserInstallation=" + new Uri(profileDirectory).AbsoluteUri,
            };

            // Düz metin, PrepareSource tarafından UTF-8'e çevrilmiştir; LibreOffice'in kodlamayı tahmin etmesine bırakılmaz.
            if (extension == ".txt")
                arguments.Add("--infilter=Text (encoded):UTF8");

            arguments.Add("--convert-to");
            arguments.Add("pdf");
            arguments.Add("--outdir");
            arguments.Add(outDirectory);
            arguments.Add(source);
            return arguments;
        }

        /// <summary>
        /// Belgeyi iş klasörüne kopyalar. Düz metin dosyaları UTF-8'e çevrilir: BOM'u olmayan ve geçerli UTF-8
        /// olmayan dosyalar Windows'un yerel kod sayfasında (Türkçe sistemlerde 1254) kabul edilir.
        /// </summary>
        internal static void PrepareSource(string inputPath, string source, string extension)
        {
            if (extension != ".txt")
            {
                File.Copy(inputPath, source, overwrite: true);
                // Salt okunur bir dosyanın kopyası da salt okunur olur ve iş klasörü silinemez.
                File.SetAttributes(source, FileAttributes.Normal);
                return;
            }

            byte[] bytes = File.ReadAllBytes(inputPath);
            File.WriteAllText(source, DecodeText(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        internal static string DecodeText(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                int codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return Encoding.GetEncoding(codePage > 0 ? codePage : 1254).GetString(bytes);
            }
        }

        /// <summary>
        /// Profil ilk kez oluşturulurken güvenlik ayarlarını yazar: makrolar hiç çalışmaz ve belgedeki dış
        /// bağlantılar (uzak resimler vb.) yüklenmez; güvenilmeyen bir belge dönüştürülürken internete erişilmez.
        /// </summary>
        internal static void EnsureProfile(string profileDirectory)
        {
            string userDirectory = Path.Combine(profileDirectory, "user");
            string settings = Path.Combine(userDirectory, "registrymodifications.xcu");
            if (File.Exists(settings))
                return;

            Directory.CreateDirectory(userDirectory);
            File.WriteAllText(settings,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                Item("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3") +
                Item("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution", "true") +
                Item("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks", "true") +
                Item("/org.openoffice.Office.Common/Misc", "FirstRun", "false") +
                "</oor:items>\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            static string Item(string path, string name, string value) =>
                $"<item oor:path=\"{path}\"><prop oor:name=\"{name}\" oor:op=\"fuse\"><value>{value}</value></prop></item>\n";
        }

        private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _sofficePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Akışlar okunmazsa arabellek dolduğunda işlem kilitlenir.
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                TryDeleteDirectory(Path.Combine(_workRoot, "profile"));

                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"LibreOffice {Timeout.TotalMinutes:0.#} dakika içinde yanıt vermedi; dönüştürme durduruldu.");
            }

            string output = await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false);
            return (process.ExitCode, output);
        }

        /// <summary>soffice yalnızca başlatıcıdır; asıl iş alt işlemde (soffice.bin) yürür, bu yüzden tüm ağaç sonlandırılır.</summary>
        private static void Kill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            // LibreOffice kapanırken dosyaları birkaç yüz milisaniye daha tutabilir.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }

        private static string Shorten(string text)
        {
            text = text.Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
        }
    }
}
