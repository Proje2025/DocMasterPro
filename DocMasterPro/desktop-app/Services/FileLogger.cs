using System;
using System.IO;

namespace DocConverter.Services
{
    public static class FileLogger
    {
        private static readonly object _lock = new();
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocMasterPro", "logs");
        private static readonly string _logFile = Path.Combine(_logDir, "app.log");

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(_logDir);
                    File.AppendAllText(_logFile,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Loglama hatası uygulamayı çökertmemeli
            }
        }

        public static void LogInfo(string message) => Write("INFO", message);

        public static void LogError(string context, Exception ex)
            => Write("ERROR", $"{context}: {ex.Message}");
    }
}
