using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DocConverter.Helpers;

/// <summary>
/// PDFium yerel kütüphanesinin (pdfium.dll) mimariye (x64 / x86) uygun olarak güvenle yüklenmesini sağlar.
/// </summary>
public static class PdfiumNativeLoader
{
    private static readonly object NativeLoadLock = new();
    private static IntPtr _pdfiumHandle;

    /// <summary>
    /// pdfium.dll'in yüklü olduğunu garanti eder.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_pdfiumHandle != IntPtr.Zero)
            return;

        lock (NativeLoadLock)
        {
            if (_pdfiumHandle != IntPtr.Zero)
                return;

            string architectureFolder = Environment.Is64BitProcess ? "x64" : "x86";
            string nativePath = Path.Combine(AppContext.BaseDirectory, architectureFolder, "pdfium.dll");
            if (!File.Exists(nativePath))
                nativePath = Path.Combine(AppContext.BaseDirectory, "pdfium.dll");

            if (!File.Exists(nativePath))
            {
                throw new DllNotFoundException(
                    $"PDFium native runtime bulunamadı: {nativePath}. PdfiumViewer native paketlerinin çıktı klasörüne kopyalandığını doğrulayın.");
            }

            _pdfiumHandle = NativeLibrary.Load(nativePath);
        }
    }
}
