using System.Runtime.CompilerServices;
using DocConverter.Services;

namespace DocConverter.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Testler kasıtlı olarak hata üretir (olmayan dosyalar, sahte tarayıcı arızaları). Bunlar kullanıcının
    /// gerçek günlüğüne (%LOCALAPPDATA%\DocMasterPro\logs) yazılırsa gerçek bir sorunu ararken yanıltır.
    /// </summary>
    [ModuleInitializer]
    internal static void RedirectLogs()
    {
        FileLogger.RedirectTo(Path.Combine(Path.GetTempPath(), "DocMasterPro.Tests", "logs"));
    }
}
