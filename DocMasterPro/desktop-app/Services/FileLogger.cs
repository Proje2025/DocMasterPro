using System;
using System.IO;
using System.Text;

namespace DocConverter.Services
{
    public static class FileLogger
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private const string LogFileName = "app.log";

        private static readonly object _lock = new();
        private static string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocMasterPro", "logs");

        /// <summary>Günlük dosyasının tam yolu (hata iletilerinde kullanıcıya gösterilir).</summary>
        public static string LogFilePath
        {
            get
            {
                lock (_lock)
                    return Path.Combine(_logDir, LogFileName);
            }
        }

        /// <summary>
        /// Günlüğü başka bir klasöre yönlendirir. Otomatik testler, kullanıcının gerçek günlüğünü
        /// sahte tarayıcı hatalarıyla doldurmamak için bunu kullanır.
        /// </summary>
        public static void RedirectTo(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Günlük klasörü boş olamaz.", nameof(directory));

            lock (_lock)
                _logDir = directory;
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(_logDir);
                    string logFile = Path.Combine(_logDir, LogFileName);
                    RotateIfNeeded(logFile);
                    File.AppendAllText(logFile,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Loglama hatası uygulamayı çökertmemeli
            }
        }

        private static void RotateIfNeeded(string logFile)
        {
            var info = new FileInfo(logFile);
            if (!info.Exists || info.Length < MaxLogBytes)
                return;

            string previous = Path.Combine(_logDir, "app.previous.log");
            File.Move(logFile, previous, overwrite: true);
        }

        public static void LogInfo(string message) => Write("INFO", message);

        /// <summary>
        /// Hatayı türü, HRESULT kodu ve çağrı yığınıyla kaydeder; yalnızca mesaj metni
        /// (ör. "Belirtilmemiş hata") donanım sorunlarını teşhis etmeye yetmez.
        /// </summary>
        public static void LogError(string context, Exception ex)
        {
            var text = new StringBuilder();
            text.Append(context).Append(": ");

            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (!ReferenceEquals(current, ex))
                    text.Append(" <- ");

                text.Append(current.GetType().Name)
                    .Append(" (0x").Append(current.HResult.ToString("X8")).Append("): ")
                    .Append(current.Message);
            }

            if (!string.IsNullOrEmpty(ex.StackTrace))
                text.Append(Environment.NewLine).Append(ex.StackTrace);

            Write("ERROR", text.ToString());
        }
    }
}
