using System.Runtime.InteropServices;

namespace DocConverter.Services;

/// <summary>
/// PDFium'un (pdfium.dll) uygulamanın kullandığı işlevleri. İmzalar bblanchon.PDFium paketindeki
/// fpdfview.h ve fpdf_formfill.h başlıklarıyla birebir aynıdır; 32 bit Windows derlemesi de cdecl kullanır.
/// PDFium iş parçacığı güvenli değildir: her çağrı <see cref="Sync"/> kilidi altında yapılmalıdır.
/// </summary>
internal static class PdfiumNative
{
    public const string LibraryName = "pdfium";

    /// <summary>Bütün belgeler için ortak kilit; PDFium aynı anda tek iş parçacığından çağrılabilir.</summary>
    public static readonly object Sync = new();

    public const int FPDFBitmap_BGRA = 4;

    public const int FPDF_ERR_FILE = 2;
    public const int FPDF_ERR_FORMAT = 3;
    public const int FPDF_ERR_PASSWORD = 4;
    public const int FPDF_ERR_SECURITY = 5;
    public const int FPDF_ERR_PAGE = 6;

    /// <summary>FPDF_FILEACCESS: belge, dosyayı bu yapıdaki geri çağırmayla parça parça okur.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeFileAccess
    {
        public CULong FileLength;
        public IntPtr GetBlock;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeSize
    {
        public float Width;
        public float Height;
    }

    /// <summary>int m_GetBlock(void* param, unsigned long position, unsigned char* pBuf, unsigned long size)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetBlockCallback(IntPtr param, CULong position, IntPtr buffer, CULong size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadCustomDocument(ref NativeFileAccess fileAccess, IntPtr password);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern CULong FPDF_GetLastError();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, out NativeSize size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_FFLDraw(
        IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}
