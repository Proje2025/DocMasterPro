using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DocConverter.Helpers;

namespace DocConverter.Services;

/// <summary>
/// PDFium çizim seçenekleri. Değerler PDFium'daki FPDF_ANNOT ve FPDF_PRINTING ile aynıdır.
/// </summary>
[Flags]
public enum PdfRenderOptions
{
    None = 0,

    /// <summary>Notları (vurgu, mürekkep, damga…) ve doldurulmuş form alanlarını da çizer.</summary>
    Annotations = 0x01,

    /// <summary>Yazıcı için çizer: yalnızca ekranda görünmesi istenen notlar atlanır.</summary>
    ForPrinting = 0x800
}

/// <summary>PDFium bir belgeyi açamadığında ya da bir sayfayı çizemediğinde fırlatılır.</summary>
public sealed class PdfiumException : Exception
{
    public PdfiumException()
    {
    }

    public PdfiumException(string message)
        : base(message)
    {
    }

    public PdfiumException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PdfiumException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>PDFium'un FPDF_GetLastError kodu; bilinmiyorsa 0.</summary>
    public int ErrorCode { get; }
}

/// <summary>
/// PDFium üzerinden açılmış bir PDF: sayfa sayısı, sayfa boyutları ve sayfayı görüntüye çizme.
/// Belgedeki JavaScript hiçbir zaman çalıştırılmaz (kullanılan PDFium derlemesinde JavaScript motoru yoktur
/// ve belge/sayfa açılış eylemleri tetiklenmez). Bütün PDFium çağrıları ortak bir kilit altında yapılır;
/// aynı nesne birden çok iş parçacığından kullanılabilir.
/// </summary>
public sealed class PdfiumDocument : IDisposable
{
    private const uint OpaqueWhite = 0xFFFFFFFF;

    /// <summary>
    /// FPDF_FORMFILLINFO için ayrılan sıfırlanmış bellek. Yapının bütün geri çağırmaları boş bırakılır;
    /// PDFium boş olanları çağırmaz. Yapının bugünkü boyutu 64 bit'te 400 baytın altındadır.
    /// </summary>
    private const int FormFillInfoSize = 1024;

    private static readonly PdfiumNative.GetBlockCallback ReadBlockCallback = ReadBlock;
    private static readonly IntPtr ReadBlockPointer = Marshal.GetFunctionPointerForDelegate(ReadBlockCallback);
    private static bool _libraryInitialized;

    private readonly Stream _stream;
    private readonly GCHandle _streamHandle;
    private readonly IntPtr _document;
    private readonly IntPtr _formInfo;
    private readonly IntPtr _form;
    private readonly SizeF?[] _pageSizes;
    private bool _disposed;

    private PdfiumDocument(Stream stream)
    {
        _stream = stream;
        if (stream.Length > uint.MaxValue)
            throw new PdfiumException("4 GB'tan büyük PDF dosyaları açılamıyor.");

        _streamHandle = GCHandle.Alloc(stream);
        try
        {
            lock (PdfiumNative.Sync)
            {
                EnsureLibraryInitialized();

                var fileAccess = new PdfiumNative.NativeFileAccess
                {
                    FileLength = new CULong((uint)stream.Length),
                    GetBlock = ReadBlockPointer,
                    Param = GCHandle.ToIntPtr(_streamHandle)
                };

                _document = PdfiumNative.FPDF_LoadCustomDocument(ref fileAccess, IntPtr.Zero);
                if (_document == IntPtr.Zero)
                {
                    int error = (int)PdfiumNative.FPDF_GetLastError().Value;
                    throw new PdfiumException(DescribeLoadError(error), error);
                }

                PageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(_document));
                _pageSizes = new SizeF?[PageCount];

                // Form ortamı yalnızca doldurulmuş form alanlarını çizmek için kurulur; JavaScript ve açılış
                // eylemleri (FORM_DoDocumentJSAction / FORM_DoDocumentOpenAction) bilerek çağrılmaz.
                _formInfo = Marshal.AllocHGlobal(FormFillInfoSize);
                Marshal.Copy(new byte[FormFillInfoSize], 0, _formInfo, FormFillInfoSize);
                Marshal.WriteInt32(_formInfo, 0, 1);
                _form = PdfiumNative.FPDFDOC_InitFormFillEnvironment(_document, _formInfo);
            }
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    public int PageCount { get; }

    /// <summary>Dosyayı salt okunur açar; dosya belge kapatılana kadar açık kalır.</summary>
    public static PdfiumDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new PdfiumDocument(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sayfanın sayfa döndürmesi uygulanmış boyutu (punto, 1/72 inç). Boyutu okunamayan sayfalar için A4 döner.
    /// </summary>
    public SizeF GetPageSize(int pageIndex)
    {
        ValidatePageIndex(pageIndex);

        lock (PdfiumNative.Sync)
        {
            ThrowIfDisposed();

            if (_pageSizes[pageIndex] is SizeF cached)
                return cached;

            SizeF size = PdfiumNative.FPDF_GetPageSizeByIndexF(_document, pageIndex, out var native) != 0
                         && native.Width > 0 && native.Height > 0
                ? new SizeF(native.Width, native.Height)
                : new SizeF(595, 842);

            _pageSizes[pageIndex] = size;
            return size;
        }
    }

    /// <summary>
    /// Sayfayı beyaz zemin üzerine <paramref name="width"/> × <paramref name="height"/> piksellik bir bitmap'e
    /// çizer. DPI değerleri yalnızca bitmap'in çözünürlük bilgisine yazılır.
    /// </summary>
    public Bitmap Render(int pageIndex, int width, int height, float dpiX, float dpiY, PdfRenderOptions flags)
    {
        ValidatePageIndex(pageIndex);
        ValidateSize(width, height);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            bitmap.SetResolution(dpiX, dpiY);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                RenderTo(pageIndex, data.Scan0, width, height, data.Stride, flags);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sayfayı çağıranın ayırdığı 32 bit BGRA belleğe çizer (satır başına <paramref name="stride"/> bayt).
    /// </summary>
    public void RenderTo(int pageIndex, IntPtr buffer, int width, int height, int stride, PdfRenderOptions flags)
    {
        ValidatePageIndex(pageIndex);
        ValidateSize(width, height);
        if (buffer == IntPtr.Zero)
            throw new ArgumentNullException(nameof(buffer));
        if (stride < width * 4L)
            throw new ArgumentOutOfRangeException(nameof(stride), "Satır uzunluğu genişliğin dört katından küçük olamaz.");

        lock (PdfiumNative.Sync)
        {
            ThrowIfDisposed();

            IntPtr page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero)
                throw new PdfiumException($"PDF'in {pageIndex + 1}. sayfası okunamadı.", PdfiumNative.FPDF_ERR_PAGE);

            bool drawForms = _form != IntPtr.Zero && (flags & PdfRenderOptions.Annotations) != 0;
            try
            {
                if (drawForms)
                    PdfiumNative.FORM_OnAfterLoadPage(page, _form);

                IntPtr bitmap = PdfiumNative.FPDFBitmap_CreateEx(
                    width, height, PdfiumNative.FPDFBitmap_BGRA, buffer, stride);
                if (bitmap == IntPtr.Zero)
                    throw new PdfiumException("Sayfa görüntüsü için bellek ayrılamadı.");

                try
                {
                    if (PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, OpaqueWhite) == 0)
                        throw new PdfiumException("Sayfa görüntüsü hazırlanamadı.");

                    PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, (int)flags);

                    // FPDF_RenderPageBitmap form alanlarını (widget) çizmez; doldurulmuş formlar boş görünmesin
                    // diye ayrıca çizilir. FPDF_ANNOT verilmez: o bayrak fareyle üzerine gelinen notların açılır
                    // pencerelerini çizdirir, dışa aktarılan ya da basılan sayfada istenmez.
                    if (drawForms)
                    {
                        PdfiumNative.FPDF_FFLDraw(
                            _form, bitmap, page, 0, 0, width, height, 0,
                            (int)(flags & ~PdfRenderOptions.Annotations));
                    }
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }
            finally
            {
                if (drawForms)
                    PdfiumNative.FORM_OnBeforeClosePage(page, _form);

                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        lock (PdfiumNative.Sync)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        ReleaseNativeResources();
    }

    private void ReleaseNativeResources()
    {
        lock (PdfiumNative.Sync)
        {
            if (_form != IntPtr.Zero)
                PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_form);

            if (_document != IntPtr.Zero)
                PdfiumNative.FPDF_CloseDocument(_document);
        }

        if (_formInfo != IntPtr.Zero)
            Marshal.FreeHGlobal(_formInfo);

        if (_streamHandle.IsAllocated)
            _streamHandle.Free();

        _stream.Dispose();
    }

    private static void EnsureLibraryInitialized()
    {
        if (_libraryInitialized)
            return;

        PdfiumNativeLoader.EnsureLoaded();
        PdfiumNative.FPDF_InitLibrary();
        _libraryInitialized = true;
    }

    internal static string DescribeLoadError(int error) => error switch
    {
        PdfiumNative.FPDF_ERR_FILE => "PDF dosyası okunamadı.",
        PdfiumNative.FPDF_ERR_FORMAT => "Dosya geçerli bir PDF değil ya da bozuk.",
        PdfiumNative.FPDF_ERR_PASSWORD => "PDF parola korumalı; parolalı dosyalar açılamıyor.",
        PdfiumNative.FPDF_ERR_SECURITY => "PDF'in şifreleme yöntemi desteklenmiyor.",
        PdfiumNative.FPDF_ERR_PAGE => "PDF'in sayfaları okunamadı.",
        _ => $"PDF açılamadı (PDFium hata kodu {error})."
    };

    /// <summary>
    /// PDFium'un dosya okuma geri çağırması. Hata yerel koda taşınamayacağı için yakalanıp 0 döndürülür;
    /// PDFium bunu okuma hatası sayar.
    /// </summary>
    private static int ReadBlock(IntPtr param, CULong position, IntPtr buffer, CULong size)
    {
        byte[]? chunk = null;
        try
        {
            if (GCHandle.FromIntPtr(param).Target is not Stream stream)
                return 0;

            long remaining = (long)size.Value;
            stream.Position = (long)position.Value;
            chunk = ArrayPool<byte>.Shared.Rent((int)Math.Min(remaining, 81920));

            long offset = 0;
            while (remaining > 0)
            {
                int read = stream.Read(chunk, 0, (int)Math.Min(remaining, chunk.Length));
                if (read <= 0)
                    return 0;

                Marshal.Copy(chunk, 0, buffer + (nint)offset, read);
                offset += read;
                remaining -= read;
            }

            return 1;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException
                                       or InvalidOperationException or ArgumentException)
        {
            return 0;
        }
        finally
        {
            if (chunk != null)
                ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private void ValidatePageIndex(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "Sayfa indeksi geçersiz.");
    }

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Genişlik sıfırdan büyük olmalı.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Yükseklik sıfırdan büyük olmalı.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
