using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Word = Microsoft.Office.Interop.Word;
using Excel = Microsoft.Office.Interop.Excel;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace DocConverter.Services
{
    /// <summary>
    /// Office formatlarını PDF'e dönüştüren servis.
    /// Microsoft Office kullanarak orijinal format ve yapıyı korur.
    /// SemaphoreSlim ile concurrent Office instance limiti (max 3).
    /// </summary>
    public class OfficeConverterService
    {
        private static readonly SemaphoreSlim _semaphore = new(3, 3);

        /// <summary>
        /// Word dosyasını (.docx/.doc) PDF'e dönüştürür - yapıyı korur.
        /// </summary>
        public async Task ConvertWordToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    Word.Application? wordApp = null;
                    Word.Document? wordDoc = null;

                    try
                    {
                        wordApp = new Word.Application
                        {
                            Visible = false,
                            DisplayAlerts = Word.WdAlertLevel.wdAlertsNone
                        };

                        wordDoc = wordApp.Documents.Open(
                            FileName: inputPath,
                            ReadOnly: true,
                            AddToRecentFiles: false,
                            Visible: false
                        );

                        wordDoc.SaveAs2(
                            FileName: outputPath,
                            FileFormat: Word.WdSaveFormat.wdFormatPDF,
                            AddToRecentFiles: false
                        );
                    }
                    finally
                    {
                        if (wordDoc != null)
                        {
                            wordDoc.Close(SaveChanges: false);
                            Marshal.ReleaseComObject(wordDoc);
                        }
                        if (wordApp != null)
                        {
                            wordApp.Quit();
                            Marshal.ReleaseComObject(wordApp);
                        }
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Thread.Sleep(500);
                    }
                }, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Excel dosyasını (.xlsx/.xls) PDF'e dönüştürür.
        /// </summary>
        public async Task ConvertExcelToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    Excel.Application? excelApp = null;
                    Excel.Workbook? workbook = null;

                    try
                    {
                        excelApp = new Excel.Application
                        {
                            Visible = false,
                            DisplayAlerts = false
                        };

                        workbook = excelApp.Workbooks.Open(
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
                        {
                            workbook.Close(SaveChanges: false);
                            Marshal.ReleaseComObject(workbook);
                        }
                        if (excelApp != null)
                        {
                            excelApp.Quit();
                            Marshal.ReleaseComObject(excelApp);
                        }
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Thread.Sleep(500);
                    }
                }, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// PowerPoint dosyasını (.pptx/.ppt) PDF'e dönüştürür.
        /// </summary>
        public async Task ConvertPowerPointToPdfAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    PowerPoint.Application? pptApp = null;
                    PowerPoint.Presentation? presentation = null;

                    try
                    {
                        pptApp = new PowerPoint.Application
                        {
                            Visible = Microsoft.Office.Core.MsoTriState.msoFalse
                        };

                        presentation = pptApp.Presentations.Open(
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
                        {
                            presentation.Close();
                            Marshal.ReleaseComObject(presentation);
                        }
                        if (pptApp != null)
                        {
                            pptApp.Quit();
                            Marshal.ReleaseComObject(pptApp);
                        }
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Thread.Sleep(500);
                    }
                }, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }
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

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    Word.Application? wordApp = null;
                    Word.Document? wordDoc = null;

                    try
                    {
                        wordApp = new Word.Application
                        {
                            Visible = false,
                            DisplayAlerts = Word.WdAlertLevel.wdAlertsNone
                        };

                        wordDoc = wordApp.Documents.Open(
                            FileName: inputPath,
                            ReadOnly: true,
                            AddToRecentFiles: false,
                            Visible: false
                        );

                        wordDoc.SaveAs2(
                            FileName: outputPath,
                            FileFormat: Word.WdSaveFormat.wdFormatXMLDocument,
                            AddToRecentFiles: false
                        );
                    }
                    finally
                    {
                        if (wordDoc != null)
                        {
                            wordDoc.Close(SaveChanges: false);
                            Marshal.ReleaseComObject(wordDoc);
                        }
                        if (wordApp != null)
                        {
                            wordApp.Quit();
                            Marshal.ReleaseComObject(wordApp);
                        }
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Thread.Sleep(500);
                    }
                }, cancellationToken);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "PDF Word dosyasına dönüştürülemedi. Bu özellik için Microsoft Word 2013 veya daha yeni bir sürüm kurulu olmalıdır.",
                    ex);
            }
            finally
            {
                _semaphore.Release();
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
        /// Microsoft Office kurulu mu kontrol eder.
        /// </summary>
        public bool IsOfficeInstalled()
        {
            try
            {
                var wordApp = new Word.Application();
                wordApp.Quit();
                Marshal.ReleaseComObject(wordApp);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Tüm Office COM objelerini temizler (emergency cleanup).
        /// </summary>
        public static void ForceGarbageCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
