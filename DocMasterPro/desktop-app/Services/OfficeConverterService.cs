using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Helpers;
using DocConverter.Models;
using Word = Microsoft.Office.Interop.Word;
using Excel = Microsoft.Office.Interop.Excel;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace DocConverter.Services
{
    /// <summary>
    /// Office formatlarını PDF'e dönüştüren servis.
    /// Microsoft Office kullanarak orijinal format ve yapıyı korur.
    /// SemaphoreSlim ile concurrent Office instance limiti (max 3).
    /// <para>
    /// Otomasyon çağrıları kendi STA iş parçacığında yürür, kullanılan her COM nesnesi (ara koleksiyonlar dahil)
    /// açıkça serbest bırakılır ve Office'e her zaman tam, ters eğik çizgili yol verilir. Aksi halde Office
    /// uygulaması dönüştürmeden sonra gizli bir işlem olarak açık kalabiliyor, PowerPoint de '/' içeren yolu reddediyor.
    /// </para>
    /// </summary>
    public class OfficeConverterService : IDocumentPdfEngine
    {
        private const string ThreadName = "DocMaster Office";

        private static readonly SemaphoreSlim _semaphore = new(3, 3);

        /// <summary>Belgeyi uzantısına göre ilgili Office uygulamasıyla PDF'e dönüştürür.</summary>
        public Task ConvertToPdfAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        {
            return Path.GetExtension(inputPath).ToLowerInvariant() switch
            {
                ".xlsx" or ".xls" => ConvertExcelToPdfAsync(inputPath, outputPath, cancellationToken),
                ".pptx" or ".ppt" => ConvertPowerPointToPdfAsync(inputPath, outputPath, cancellationToken),
                _ => ConvertWordToPdfAsync(inputPath, outputPath, cancellationToken)
            };
        }

        /// <summary>
        /// Word dosyasını (.docx/.doc) PDF'e dönüştürür - yapıyı korur.
        /// </summary>
        public Task ConvertWordToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return RunAsync(inputPath, outputPath,
                (input, output) => SaveWithWord(input, output, Word.WdSaveFormat.wdFormatPDF), cancellationToken);
        }

        /// <summary>
        /// Excel dosyasını (.xlsx/.xls) PDF'e dönüştürür.
        /// </summary>
        public Task ConvertExcelToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return RunAsync(inputPath, outputPath, ExportWithExcel, cancellationToken);
        }

        /// <summary>
        /// PowerPoint dosyasını (.pptx/.ppt) PDF'e dönüştürür.
        /// </summary>
        public Task ConvertPowerPointToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return RunAsync(inputPath, outputPath, ExportWithPowerPoint, cancellationToken);
        }

        /// <summary>
        /// Metin dosyasını (.txt/.rtf) PDF'e dönüştürür.
        /// </summary>
        public async Task ConvertTxtToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            await ConvertWordToPdfAsync(inputPath, outputPath, cancellationToken);
        }

        /// <summary>
        /// PDF dosyasını Microsoft Word motoruyla düzenlenebilir DOCX dosyasına dönüştürür.
        /// </summary>
        public async Task ConvertPdfToWordAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("PDF dosyası bulunamadı.", inputPath);

            try
            {
                await RunAsync(inputPath, outputPath,
                    (input, output) => SaveWithWord(input, output, Word.WdSaveFormat.wdFormatXMLDocument), cancellationToken);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "PDF Word dosyasına dönüştürülemedi. Bu özellik için Microsoft Word 2013 veya daha yeni bir sürüm kurulu olmalıdır.",
                    ex);
            }
        }

        /// <summary>
        /// Eski .doc formatı için uyarı mesajı döndürür.
        /// </summary>
        public bool IsOldWordFormat(string inputPath)
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            return ext == ".doc";
        }

        /// <summary>
        /// Microsoft Word kurulu mu? Word'ü başlatmadan, kayıt defterinden denetlenir.
        /// </summary>
        public bool IsOfficeInstalled() => new OfficeEngineProbe().IsOfficeAppInstalled(OfficeApp.Word);

        /// <summary>
        /// Tüm Office COM objelerini temizler (emergency cleanup).
        /// </summary>
        public static void ForceGarbageCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static async Task RunAsync(string inputPath, string outputPath, Action<string, string> work, CancellationToken cancellationToken)
        {
            inputPath = Path.GetFullPath(inputPath);
            outputPath = Path.GetFullPath(outputPath);

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await StaThread.RunAsync(() =>
                {
                    try
                    {
                        work(inputPath, outputPath);
                    }
                    finally
                    {
                        // Dönüştürme yöntemi bittikten sonra çağrılır: o yöntemin geçici COM başvuruları artık
                        // erişilemez olduğundan burada kesin olarak serbest kalır ve Office işlemi kapanabilir.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Thread.Sleep(500);
                    }
                }, ThreadName, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static void SaveWithWord(string inputPath, string outputPath, Word.WdSaveFormat format)
        {
            Word.Application? application = null;
            Word.Documents? documents = null;
            Word.Document? document = null;

            try
            {
                // Özellikler nesne başlatıcısında atanmaz: atama hata verirse değişken null kalır ve Quit çağrılamaz.
                application = new Word.Application();
                application.Visible = false;
                application.DisplayAlerts = Word.WdAlertLevel.wdAlertsNone;

                documents = application.Documents;
                document = documents.Open(
                    FileName: inputPath,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false
                );

                document.SaveAs2(
                    FileName: outputPath,
                    FileFormat: format,
                    AddToRecentFiles: false
                );
            }
            finally
            {
                if (document != null)
                    Quietly(() => document.Close(SaveChanges: false));
                Release(document);
                Release(documents);

                if (application != null)
                    Quietly(() => application.Quit(SaveChanges: false));
                Release(application);
            }
        }

        private static void ExportWithExcel(string inputPath, string outputPath)
        {
            Excel.Application? application = null;
            Excel.Workbooks? workbooks = null;
            Excel.Workbook? workbook = null;

            try
            {
                application = new Excel.Application();
                application.Visible = false;
                application.DisplayAlerts = false;

                workbooks = application.Workbooks;
                workbook = workbooks.Open(
                    Filename: inputPath,
                    ReadOnly: true,
                    AddToMru: false
                );

                workbook.ExportAsFixedFormat(
                    Type: Excel.XlFixedFormatType.xlTypePDF,
                    Filename: outputPath,
                    Quality: Excel.XlFixedFormatQuality.xlQualityStandard,
                    IncludeDocProperties: true,
                    IgnorePrintAreas: false
                );
            }
            finally
            {
                if (workbook != null)
                    Quietly(() => workbook.Close(SaveChanges: false));
                Release(workbook);
                Release(workbooks);

                if (application != null)
                    Quietly(application.Quit);
                Release(application);
            }
        }

        private static void ExportWithPowerPoint(string inputPath, string outputPath)
        {
            PowerPoint.Application? application = null;
            PowerPoint.Presentations? presentations = null;
            PowerPoint.Presentation? presentation = null;

            try
            {
                // PowerPoint, Visible = msoFalse atamasını reddeder ("Hiding the application window is not allowed").
                // Sunum WithWindow: msoFalse ile açıldığı için pencere zaten görünmez.
                application = new PowerPoint.Application();

                presentations = application.Presentations;
                presentation = presentations.Open(
                    FileName: inputPath,
                    ReadOnly: Microsoft.Office.Core.MsoTriState.msoTrue,
                    WithWindow: Microsoft.Office.Core.MsoTriState.msoFalse
                );

                presentation.SaveAs(
                    FileName: outputPath,
                    FileFormat: PowerPoint.PpSaveAsFileType.ppSaveAsPDF
                );
            }
            finally
            {
                if (presentation != null)
                    Quietly(presentation.Close);
                Release(presentation);

                if (application != null)
                {
                    // PowerPoint tek örnek çalışır: kullanıcının açık bir sunumu varsa uygulamayı kapatma.
                    Quietly(() =>
                    {
                        if (presentations == null || presentations.Count == 0)
                            application.Quit();
                    });
                }

                Release(presentations);
                Release(application);
            }
        }

        /// <summary>Kapatma adımlarından biri hata verse de kalanların çalışması ve asıl hatanın gizlenmemesi için.</summary>
        private static void Quietly(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
            {
            }
        }

        private static void Release(object? comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }
    }
}
