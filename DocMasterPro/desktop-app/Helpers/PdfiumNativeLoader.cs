using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DocConverter.Helpers;

/// <summary>
/// PDFium yerel kütüphanesini (pdfium.dll) işlemcinin mimarisine uygun olarak yükler ve uygulamadaki
/// bütün PDFium çağrılarının bu dosyaya gitmesini sağlar.
/// </summary>
public static class PdfiumNativeLoader
{
    private const string ImportName = "pdfium";

    private static readonly object NativeLoadLock = new();
    private static IntPtr _pdfiumHandle;

    /// <summary>Yüklenen kütüphanenin tam yolu; varsayılan arama yoluyla bulunduysa yalnızca adı.</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>
    /// pdfium.dll'in yüklü olduğunu garanti eder. Önce uygulama klasörüne (yayımlanmış sürüm), sonra
    /// runtimes\win-x64\native gibi NuGet klasörüne (geliştirme derlemesi ve testler) bakar.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_pdfiumHandle != IntPtr.Zero)
            return;

        lock (NativeLoadLock)
        {
            if (_pdfiumHandle != IntPtr.Zero)
                return;

            string fileName = OperatingSystem.IsWindows() ? "pdfium.dll" : "libpdfium.so";
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeFolder(), "native", fileName)
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    Use(NativeLibrary.Load(candidate), candidate);
                    return;
                }
            }

            // Tek dosya yayında yerel kütüphaneler pakete gömülüyse .NET onları kendi klasörüne çıkarır.
            if (NativeLibrary.TryLoad(fileName, typeof(PdfiumNativeLoader).Assembly, null, out IntPtr handle))
            {
                Use(handle, fileName);
                return;
            }

            throw new DllNotFoundException(
                $"PDF motoru (PDFium) bulunamadı: {candidates[0]}. Uygulamayı yeniden kurmayı deneyin.");
        }
    }

    /// <summary>
    /// [DllImport("pdfium")] bildirimlerinin başka bir pdfium.dll'e değil, burada yüklenen dosyaya bağlanmasını sağlar.
    /// Çözücü yükleme bittikten sonra (bir kez) kaydedilir; böylece yükleme sırasında kendini çağırmaz.
    /// </summary>
    private static void Use(IntPtr handle, string path)
    {
        NativeLibrary.SetDllImportResolver(typeof(PdfiumNativeLoader).Assembly, Resolve);
        LoadedPath = path;
        _pdfiumHandle = handle;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ImportName, StringComparison.Ordinal))
            return IntPtr.Zero;

        EnsureLoaded();
        return _pdfiumHandle;
    }

    private static string RuntimeFolder()
    {
        string os = OperatingSystem.IsWindows() ? "win" : "linux";
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };

        return $"{os}-{architecture}";
    }
}
