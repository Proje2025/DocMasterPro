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
    /// </summary>
    public class OfficeConverterService
    {
        /// <summary>
        /// Word dosyasını (.docx/.doc) PDF'e dönüştürür - yapıyı korur.
        /// </summary>
        public async System.Threading.Tasks.Task ConvertWordToPdfAsync(string inputPath, string outputPath)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                Word.Application? wordApp = null;
                Word.Document? wordDoc = null;

                try
                {
                    // Word uygulamasını başlat
                    wordApp = new Word.Application
                    {
                        Visible = false,
                        DisplayAlerts = Word.WdAlertLevel.wdAlertsNone
                    };

                    // Belgeyi aç
                    wordDoc = wordApp.Documents.Open(
                        FileName: inputPath,
                        ReadOnly: true,
                        AddToRecentFiles: false,
                        Visible: false
                    );

                    // PDF olarak kaydet
                    wordDoc.SaveAs2(
                        FileName: outputPath,
                        FileFormat: Word.WdSaveFormat.wdFormatPDF,
                        AddToRecentFiles: false
                    );
                }
                finally
                {
                    // COM objelerini temizle
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
                    // Ek bekleme - dosya serbest bırakılana kadar
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(1000); // 1 saniye bekle
                }
            });
        }

        /// <summary>
        /// Excel dosyasını (.xlsx/.xls) PDF'e dönüştürür.
        /// </summary>
        public async System.Threading.Tasks.Task ConvertExcelToPdfAsync(string inputPath, string outputPath)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                Excel.Application? excelApp = null;
                Excel.Workbook? workbook = null;

                try
                {
                    // Excel uygulamasını başlat
                    excelApp = new Excel.Application
                    {
                        Visible = false,
                        DisplayAlerts = false
                    };

                    // Çalışma kitabını aç
                    workbook = excelApp.Workbooks.Open(
                        Filename: inputPath,
                        ReadOnly: true,
                        AddToMru: false
                    );

                    // PDF olarak kaydet
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
                    // COM objelerini temizle
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
                    // Ek bekleme - dosya serbest bırakılana kadar
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(1000); // 1 saniye bekle
                }
            });
        }

        /// <summary>
        /// PowerPoint dosyasını (.pptx/.ppt) PDF'e dönüştürür.
        /// </summary>
        public async System.Threading.Tasks.Task ConvertPowerPointToPdfAsync(string inputPath, string outputPath)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                PowerPoint.Application? pptApp = null;
                PowerPoint.Presentation? presentation = null;

                try
                {
                    // PowerPoint uygulamasını başlat
                    pptApp = new PowerPoint.Application
                    {
                        Visible = Microsoft.Office.Core.MsoTriState.msoFalse
                    };

                    // Sunumu aç
                    presentation = pptApp.Presentations.Open(
                        FileName: inputPath,
                        ReadOnly: Microsoft.Office.Core.MsoTriState.msoTrue,
                        WithWindow: Microsoft.Office.Core.MsoTriState.msoFalse
                    );

                    // PDF olarak kaydet
                    presentation.SaveAs(
                        FileName: outputPath,
                        FileFormat: PowerPoint.PpSaveAsFileType.ppSaveAsPDF
                    );
                }
                finally
                {
                    // COM objelerini temizle
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
                    // Ek bekleme - dosya serbest bırakılana kadar
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(1000); // 1 saniye bekle
                }
            });
        }

        /// <summary>
        /// Metin dosyasını (.txt/.rtf) PDF'e dönüştürür.
        /// </summary>
        public async System.Threading.Tasks.Task ConvertTxtToPdfAsync(string inputPath, string outputPath)
        {
            // TXT dosyalarını Word ile aç ve PDF'e çevir
            await ConvertWordToPdfAsync(inputPath, outputPath);
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
                // Word COM nesnesi oluşturmayı dene
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
    }
}
