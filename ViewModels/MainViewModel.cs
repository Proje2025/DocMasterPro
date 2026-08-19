using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.Services;
using ImageMagick;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DocConverter.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly PdfService _pdf = new();
        private readonly ConverterService _conv = new();
        private readonly OfficeConverterService _officeConv = new();
        private readonly ScannerService _scannerService = new();
        private readonly PrinterService _printerService = new();
        private readonly BlankPageDetector _blankDetector = new();

        private CancellationTokenSource? _scanCts;

        // ==================== Ortak Özellikler ====================
        [ObservableProperty]
        private int progress;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        [NotifyCanExecuteChangedFor(nameof(SplitCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConvertImagesToPdfCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExportPdfToImagesCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConvertOfficeToPdfCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
        [NotifyCanExecuteChangedFor(nameof(PrintFileCommand))]
        [NotifyCanExecuteChangedFor(nameof(PrintTestPageCommand))]
        [NotifyCanExecuteChangedFor(nameof(CleanBlankPagesFromPdfCommand))]
        private bool isBusy;

        [ObservableProperty]
        private string statusMessage = "Hazır";

        // ==================== Tab 1: PDF Birleştirme ====================
        [ObservableProperty]
        private ObservableCollection<DocumentItem> mergeDocuments = new();

        // ==================== Tab 2: PDF Bölme ====================
        [ObservableProperty]
        private string splitPdfPath = "";

        [ObservableProperty]
        private string splitOutputFolder = "";

        [ObservableProperty]
        private string pageRangeText = "";

        [ObservableProperty]
        private ObservableCollection<string> splitRanges = new();

        [ObservableProperty]
        private int splitPdfPageCount = 0;

        public string SplitPdfPageCountText =>
            SplitPdfPageCount > 0 ? $"({SplitPdfPageCount} sayfa)" : "";

        // ==================== Tab 3: Görüntü → PDF ====================
        [ObservableProperty]
        private ObservableCollection<DocumentItem> imageDocuments = new();

        // ==================== Tab 4: PDF → Görüntü ====================
        [ObservableProperty]
        private string exportPdfPath = "";

        [ObservableProperty]
        private string exportOutputFolder = "";

        [ObservableProperty]
        private string selectedImageFormat = "PNG";

        public string[] ImageOutputFormats { get; } = { "PNG", "JPG", "BMP", "TIFF" };

        // ==================== Tab 5: Office → PDF ====================
        [ObservableProperty]
        private ObservableCollection<DocumentItem> officeDocuments = new();

        // ==================== Tab 6: PDF Düzenleme ====================
        [ObservableProperty]
        private string editPdfPath = "";

        [ObservableProperty]
        private ObservableCollection<PdfPageInfo> pdfPages = new();

        [ObservableProperty]
        private PdfPageInfo? selectedPage;

        [ObservableProperty]
        private string watermarkText = "";

        [ObservableProperty]
        private int selectedRotation = 90;

        public int[] RotationAngles { get; } = { 90, 180, 270 };

        // ==================== Tab 7: Yazıcı & Tarayıcı ====================
        [ObservableProperty]
        private ObservableCollection<DeviceInfo> availableScanners = new();

        [ObservableProperty]
        private DeviceInfo? selectedScanner;

        [ObservableProperty]
        private ObservableCollection<DeviceInfo> availablePrinters = new();

        [ObservableProperty]
        private DeviceInfo? selectedPrinter;

        [ObservableProperty]
        private ScanOptions scanOptions = new();

        [ObservableProperty]
        private ObservableCollection<ScannedPageItem> scannedPages = new();

        [ObservableProperty]
        private ScannedPageItem? selectedScannedPage;

        [ObservableProperty]
        private string selectedPrintFilePath = "";

        [ObservableProperty]
        private PrintJobOptions printOptions = new();

        public int[] ScanResolutions { get; } = { 150, 200, 300, 600 };
        public string[] ScanColorModes { get; } = { "Renkli", "Gri Tonlama", "Siyah-Beyaz" };
        public string[] ScanSourceOptions { get; } = { "Cam (Düz Yatak)", "ADF (Tek Taraflı)", "ADF (Çift Taraflı)" };
        public string[] DuplexPrintModes { get; } = { "Çift Taraflı (Uzun Kenar)", "Çift Taraflı (Kısa Kenar)" };

        // ==================== Tab 8: Ayarlar ====================
        [ObservableProperty]
        private bool autoDeleteBlankPages = true;

        [ObservableProperty]
        private bool duplexScanEnabled = false;

        [ObservableProperty]
        private bool duplexPrintEnabled = false;

        [ObservableProperty]
        private double blankPageSensitivity = 98.5; // % eşik

        [ObservableProperty]
        private int defaultScanResolution = 300;

        [ObservableProperty]
        private string defaultScanColorMode = "Renkli";

        [ObservableProperty]
        private string cleanPdfSourcePath = "";

        public string GhostscriptStatusText =>
            IsGhostscriptAvailable() ? "✓ Ghostscript Yüklü (Aktif)" : "✗ Ghostscript Yüklü Değil (İsteğe Bağlı)";

        public string OfficeStatusText =>
            _officeConv.IsOfficeInstalled() ? "✓ Microsoft Office Yüklü (Aktif)" : "✗ Microsoft Office Bulunamadı";

        // ==================== Constructor ====================
        public MainViewModel()
        {
            MergeDocuments.CollectionChanged += (_, _) => MergeCommand.NotifyCanExecuteChanged();
            ImageDocuments.CollectionChanged += (_, _) => ConvertImagesToPdfCommand.NotifyCanExecuteChanged();
            OfficeDocuments.CollectionChanged += (_, _) => ConvertOfficeToPdfCommand.NotifyCanExecuteChanged();

            // İlk açılışta cihazları arka planda keşfet
            _ = InitializeDevicesAsync();
        }

        private async Task InitializeDevicesAsync()
        {
            try
            {
                await RefreshDevicesAsync();
            }
            catch (Exception ex)
            {
                FileLogger.LogError("InitializeDevicesAsync", ex);
            }
        }

        // ==================== Ortak Metodlar ====================
        private DocumentItem CreateDocumentItem(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            int? pageCount = null;

            if (ext == ".pdf")
            {
                var conv = new ConverterService();
                pageCount = conv.GetPdfPageCount(filePath);
            }

            return new DocumentItem
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                Extension = ext,
                FileSize = fileInfo.Length,
                FileSizeFormatted = PathValidator.FormatFileSize(fileInfo.Length),
                PageCount = pageCount
            };
        }

        // ==================== Tab 1: PDF Birleştirme Komutları ====================
        [RelayCommand]
        public void AddFiles()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Tüm Desteklenen Dosyalar|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff;*.tif;*.webp;*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.txt;*.rtf|" +
                         "PDF Dosyaları|*.pdf|Görüntü Dosyaları|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff;*.tif;*.webp|" +
                         "Word Dosyaları|*.docx;*.doc|Excel Dosyaları|*.xlsx;*.xls|PowerPoint Dosyaları|*.pptx;*.ppt",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var f in dlg.FileNames)
            {
                if (!PathValidator.IsPathSafe(f)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (!PathValidator.IsSupportedExtension(ext)) continue;

                MergeDocuments.Add(CreateDocumentItem(f));
            }
        }

        [RelayCommand(CanExecute = nameof(CanMerge))]
        public async Task Merge()
        {
            if (MergeDocuments.Count == 0) return;

            bool hasOfficeFiles = MergeDocuments.Any(d =>
                PathValidator.OfficeExtensions.Contains(d.Extension));
            if (hasOfficeFiles && !_officeConv.IsOfficeInstalled())
            {
                MessageBox.Show(
                    "Listede Office dosyaları var ancak Microsoft Office kurulu değil.\n\n" +
                    "Office dosyaları atlanacak. Devam edilsin mi?",
                    "Office Kurulu Değil", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var saveDlg = new SaveFileDialog
            {
                Filter = "PDF Dosyası|*.pdf",
                FileName = "birlesik.pdf",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (saveDlg.ShowDialog() != true) return;

            IsBusy = true;
            Progress = 0;

            var pdfPaths = new List<string>();
            var tempFiles = new List<string>();

            try
            {
                int total = MergeDocuments.Count;
                int current = 0;

                foreach (var doc in MergeDocuments)
                {
                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 50) / total;

                    try
                    {
                        string? pdfPath = await ConvertToPdfAsync(doc);
                        if (pdfPath != null)
                        {
                            pdfPaths.Add(pdfPath);
                            if (pdfPath != doc.FilePath)
                                tempFiles.Add(pdfPath);
                            doc.Status = "Done";
                        }
                        else
                        {
                            doc.Status = "Error";
                        }
                    }
                    catch (Exception ex)
                    {
                        doc.Status = "Error";
                        FileLogger.LogError($"Merge ({doc.FileName})", ex);
                    }
                }

                var reporter = new Progress<int>(v => Progress = 50 + (v / 2));
                await _pdf.MergePdfsAsync(pdfPaths, saveDlg.FileName, reporter);

                var result = MessageBox.Show("Birleştirme tamamlandı!\n\nDosya konumu açılsın mı?", "DocMaster Pro",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDlg.FileName}\"");
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                IsBusy = false;
                Progress = 100;
            }
        }

        private bool CanMerge() => !IsBusy && MergeDocuments.Count > 0;

        [RelayCommand]
        public void RemoveFile(DocumentItem item)
        {
            if (item == null) return;
            MergeDocuments.Remove(item);
            ImageDocuments.Remove(item);
            OfficeDocuments.Remove(item);
        }

        [RelayCommand]
        public void ClearMerge()
        {
            MergeDocuments.Clear();
        }

        // ==================== Tab 2: PDF Bölme Komutları ====================
        [RelayCommand]
        public void OpenPdfForSplit()
        {
            var dlg = new OpenFileDialog { Filter = "PDF Dosyası|*.pdf" };
            if (dlg.ShowDialog() == true)
            {
                SplitPdfPath = dlg.FileName;
                SplitPdfPageCount = _pdf.GetPageCount(dlg.FileName);
                OnPropertyChanged(nameof(SplitPdfPageCountText));

                var folderDlg = new OpenFolderDialog { Title = "Çıkış klasörünü seçin" };
                if (folderDlg.ShowDialog() == true)
                    SplitOutputFolder = folderDlg.FolderName;
            }
        }

        partial void OnPageRangeTextChanged(string value)
        {
            SplitRanges.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;

            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var dash = part.Split('-');
                if (dash.Length == 1)
                    SplitRanges.Add($"Sayfa {dash[0].Trim()}");
                else if (dash.Length == 2)
                    SplitRanges.Add($"Sayfa {dash[0].Trim()} - {dash[1].Trim()}");
            }

            SplitCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanSplit))]
        public async Task Split()
        {
            if (string.IsNullOrWhiteSpace(SplitPdfPath)) return;

            int maxPage = SplitPdfPageCount > 0 ? SplitPdfPageCount : 9999;
            var ranges = PathValidator.ValidatePageRanges(PageRangeText, maxPage);
            if (ranges.Count == 0)
            {
                MessageBox.Show("Geçerli bir sayfa aralığı girin.\nÖrnek: 1-3, 5-7",
                    "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(SplitOutputFolder))
            {
                var folderDlg = new OpenFolderDialog { Title = "Çıkış klasörünü seçin" };
                if (folderDlg.ShowDialog() != true) return;
                SplitOutputFolder = folderDlg.FolderName;
            }

            IsBusy = true;
            Progress = 0;

            try
            {
                var reporter = new Progress<int>(v => Progress = v);
                await _pdf.SplitPdfAsync(SplitPdfPath, SplitOutputFolder, ranges, reporter);
                Progress = 100;

                var result = MessageBox.Show("PDF bölme tamamlandı!\n\nÇıkış klasörü açılsın mı?", "DocMaster Pro",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", SplitOutputFolder);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("SplitPdf", ex);
                MessageBox.Show($"Hata: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanSplit() => !IsBusy && !string.IsNullOrWhiteSpace(SplitPdfPath) && !string.IsNullOrWhiteSpace(PageRangeText);

        // ==================== Tab 3: Görüntü → PDF Komutları ====================
        [RelayCommand]
        public void AddImages()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Görüntü Dosyaları|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff;*.tif;*.webp|Tüm Dosyalar|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var f in dlg.FileNames)
            {
                if (!PathValidator.IsPathSafe(f)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (!PathValidator.ImageExtensions.Contains(ext))
                    continue;

                ImageDocuments.Add(CreateDocumentItem(f));
            }
        }

        [RelayCommand(CanExecute = nameof(CanConvertImages))]
        public async Task ConvertImagesToPdf()
        {
            if (ImageDocuments.Count == 0) return;

            var saveDlg = new SaveFileDialog
            {
                Filter = "PDF Dosyası|*.pdf",
                FileName = "goruntuler.pdf",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (saveDlg.ShowDialog() != true) return;

            IsBusy = true;
            Progress = 0;

            var tempFiles = new List<string>();

            try
            {
                int total = ImageDocuments.Count;
                int current = 0;

                var pdfPaths = new List<string>();
                var failedItems = new List<string>();

                foreach (var doc in ImageDocuments)
                {
                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 50) / total;

                    try
                    {
                        string tmp = _conv.ConvertImageToPdf(doc.FilePath);
                        tempFiles.Add(tmp);
                        pdfPaths.Add(tmp);
                        doc.Status = "Done";
                    }
                    catch (Exception ex)
                    {
                        doc.Status = "Error";
                        failedItems.Add(doc.FileName);
                        FileLogger.LogError($"ImageToPdf ({doc.FileName})", ex);
                    }
                }

                if (pdfPaths.Count == 0)
                {
                    MessageBox.Show("Hiçbir görüntü PDF'e dönüştürülemedi.\nDetaylar için log dosyasını kontrol edin.",
                        "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (failedItems.Count > 0)
                {
                    var result = MessageBox.Show(
                        $"Bazı dosyalar dönüştürülemedi: {string.Join(", ", failedItems)}\n\nDevam edilsin mi?",
                        "Uyarı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                        return;
                }

                var reporter = new Progress<int>(v => Progress = 50 + (v / 2));
                await _pdf.MergePdfsAsync(pdfPaths, saveDlg.FileName, reporter);

                var openResult = MessageBox.Show("PDF oluşturuldu!\n\nDosya konumu açılsın mı?", "DocMaster Pro",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (openResult == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDlg.FileName}\"");
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                IsBusy = false;
                Progress = 100;
            }
        }

        private bool CanConvertImages() => !IsBusy && ImageDocuments.Count > 0;

        [RelayCommand]
        public void RemoveImageFile(DocumentItem item)
        {
            if (item != null) ImageDocuments.Remove(item);
        }

        [RelayCommand]
        public void ClearImages()
        {
            ImageDocuments.Clear();
        }

        // ==================== Tab 4: PDF → Görüntü Komutları ====================
        [RelayCommand]
        public void OpenPdfForExport()
        {
            var dlg = new OpenFileDialog { Filter = "PDF Dosyası|*.pdf" };
            if (dlg.ShowDialog() == true)
            {
                ExportPdfPath = dlg.FileName;

                var folderDlg = new OpenFolderDialog { Title = "Çıkış klasörünü seçin" };
                if (folderDlg.ShowDialog() == true)
                    ExportOutputFolder = folderDlg.FolderName;

                ExportPdfToImagesCommand.NotifyCanExecuteChanged();
            }
        }

        [RelayCommand(CanExecute = nameof(CanExportPdfToImages))]
        public async Task ExportPdfToImages()
        {
            if (string.IsNullOrWhiteSpace(ExportPdfPath)) return;

            if (string.IsNullOrWhiteSpace(ExportOutputFolder))
            {
                var folderDlg = new OpenFolderDialog { Title = "Çıkış klasörünü seçin" };
                if (folderDlg.ShowDialog() != true) return;
                ExportOutputFolder = folderDlg.FolderName;
            }

            IsBusy = true;
            Progress = 0;

            try
            {
                var reporter = new Progress<int>(v => Progress = v);
                await _conv.ConvertPdfToImagesAsync(ExportPdfPath, ExportOutputFolder, SelectedImageFormat.ToLower(), reporter);
                Progress = 100;

                var result = MessageBox.Show("Dönüştürme tamamlandı!\n\nÇıkış klasörü açılsın mı?", "DocMaster Pro",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", ExportOutputFolder);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ExportPdfToImages", ex);
                MessageBox.Show($"Hata: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanExportPdfToImages() => !IsBusy && !string.IsNullOrWhiteSpace(ExportPdfPath);

        // ==================== Tab 5: Office → PDF Komutları ====================
        [RelayCommand]
        public void AddOfficeFiles()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Office Dosyaları|*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.txt;*.rtf|" +
                         "Word Dosyaları|*.docx;*.doc|" +
                         "Excel Dosyaları|*.xlsx;*.xls|" +
                         "PowerPoint Dosyaları|*.pptx;*.ppt|" +
                         "Metin Dosyaları|*.txt;*.rtf",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var f in dlg.FileNames)
            {
                if (!PathValidator.IsPathSafe(f)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (!PathValidator.OfficeExtensions.Contains(ext)) continue;
                OfficeDocuments.Add(CreateDocumentItem(f));
            }
        }

        [RelayCommand(CanExecute = nameof(CanConvertOffice))]
        public async Task ConvertOfficeToPdf()
        {
            if (OfficeDocuments.Count == 0) return;

            if (!_officeConv.IsOfficeInstalled())
            {
                MessageBox.Show(
                    "Bu işlem için Microsoft Office kurulu olmalıdır.\n\n" +
                    "Word, Excel veya PowerPoint dosyalarını dönüştürmek için Microsoft Office gereklidir.",
                    "Office Kurulu Değil", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var folderDlg = new OpenFolderDialog { Title = "PDF'lerin kaydedileceği klasörü seçin" };
            if (folderDlg.ShowDialog() != true) return;

            IsBusy = true;
            Progress = 0;

            try
            {
                int total = OfficeDocuments.Count;
                int current = 0;

                foreach (var doc in OfficeDocuments)
                {
                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 100) / total;

                    try
                    {
                        string outputPath = Path.Combine(folderDlg.FolderName,
                            Path.GetFileNameWithoutExtension(doc.FileName) + ".pdf");

                        await ConvertOfficeFileToPdfAsync(doc.FilePath, outputPath, doc.Extension);
                        doc.Status = "Done";
                    }
                    catch (Exception ex)
                    {
                        doc.Status = "Error";
                        FileLogger.LogError($"OfficeToPdf ({doc.FileName})", ex);
                    }
                }

                var result = MessageBox.Show("Dönüştürme tamamlandı!\n\nÇıkış klasörü açılsın mı?", "DocMaster Pro",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", folderDlg.FolderName);
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
            }
        }

        private bool CanConvertOffice() => !IsBusy && OfficeDocuments.Count > 0;

        [RelayCommand]
        public void RemoveOfficeFile(DocumentItem item)
        {
            if (item != null) OfficeDocuments.Remove(item);
        }

        [RelayCommand]
        public void ClearOffice()
        {
            OfficeDocuments.Clear();
        }

        // ==================== Tab 6: PDF Düzenleme Komutları ====================
        [RelayCommand]
        public void OpenPdfForEdit()
        {
            var dlg = new OpenFileDialog { Filter = "PDF Dosyası|*.pdf" };
            if (dlg.ShowDialog() == true)
            {
                EditPdfPath = dlg.FileName;
                LoadPdfPages(dlg.FileName);
            }
        }

        private void LoadPdfPages(string pdfPath)
        {
            PdfPages.Clear();

            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                MessageBox.Show($"PDF dosyası bulunamadı: {pdfPath}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                if (!IsGhostscriptAvailable())
                {
                    LoadPdfPagesBasic(pdfPath);
                    return;
                }

                LoadPdfPagesWithThumbnails(pdfPath);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("LoadPdfPages", ex);
                try
                {
                    LoadPdfPagesBasic(pdfPath);
                }
                catch { }
            }
        }

        private void LoadPdfPagesBasic(string pdfPath)
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);

            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                PdfPages.Add(new PdfPageInfo
                {
                    PageNumber = i + 1,
                    PageIndex = i,
                    Info = $"{page.Width:F0} x {page.Height:F0} pt",
                    Rotation = page.Rotate,
                    Thumbnail = null
                });
            }
        }

        private void LoadPdfPagesWithThumbnails(string pdfPath)
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            using var images = new MagickImageCollection();
            images.Read(pdfPath);

            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                BitmapSource? thumbnail = null;

                if (i < images.Count)
                {
                    thumbnail = CreateThumbnail(images[i], 1200);
                }

                PdfPages.Add(new PdfPageInfo
                {
                    PageNumber = i + 1,
                    PageIndex = i,
                    Info = $"{page.Width:F0} x {page.Height:F0} pt",
                    Rotation = page.Rotate,
                    Thumbnail = thumbnail
                });
            }
        }

        private BitmapSource? CreateThumbnail(IMagickImage image, int maxWidth)
        {
            try
            {
                using var ms = new MemoryStream();
                image.Write(ms, MagickFormat.Png);
                ms.Position = 0;

                using var thumb = new MagickImage(ms);
                thumb.FilterType = FilterType.Lanczos;
                thumb.Resize(maxWidth, 0);
                thumb.Format = MagickFormat.Png;

                var bytes = thumb.ToByteArray();
                var bitmap = new BitmapImage();
                using var outputMs = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = outputMs;
                bitmap.EndInit();
                bitmap.Freeze();

                return bitmap;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("CreateThumbnail", ex);
                return null;
            }
        }

        private bool IsGhostscriptAvailable()
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "gswin64c.exe",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null) return false;

                process.WaitForExit(3000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        [RelayCommand]
        public void DeletePage(PdfPageInfo? page)
        {
            if (page == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            var result = MessageBox.Show($"Sayfa {page.PageNumber} silinsin mi?",
                "DocMaster Pro", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                using var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify);
                if (page.PageIndex < doc.PageCount)
                {
                    doc.Pages.RemoveAt(page.PageIndex);
                }
                doc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DeletePage", ex);
                MessageBox.Show($"Sayfa silinemedi: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Bug fix: Tek sayfa döndürme (sağ paneldeki SelectedRotation seçeneğini kullanır).
        /// </summary>
        [RelayCommand]
        public void RotatePage(PdfPageInfo? page)
        {
            if (page == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            try
            {
                using var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify);
                if (page.PageIndex < doc.PageCount)
                {
                    var pdfPage = doc.Pages[page.PageIndex];
                    int angle = SelectedRotation > 0 ? SelectedRotation : 90;
                    pdfPage.Rotate = (pdfPage.Rotate + angle) % 360;
                }
                doc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("RotatePage", ex);
                MessageBox.Show($"Sayfa döndürülemedi: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public void MovePageUp()
        {
            if (SelectedPage == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            int index = PdfPages.IndexOf(SelectedPage);
            if (index <= 0) return;

            try
            {
                var pagesList = PdfPages.ToList();
                (pagesList[index - 1], pagesList[index]) = (pagesList[index], pagesList[index - 1]);

                using var newDoc = new PdfDocument();
                using var sourceDoc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Import);

                foreach (var pageInfo in pagesList)
                {
                    newDoc.AddPage(sourceDoc.Pages[pageInfo.PageIndex]);
                }

                newDoc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);
                SelectedPage = PdfPages[index - 1];
            }
            catch (Exception ex)
            {
                FileLogger.LogError("MovePageUp", ex);
                MessageBox.Show($"Sayfa taşınamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public void MovePageDown()
        {
            if (SelectedPage == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            int index = PdfPages.IndexOf(SelectedPage);
            if (index < 0 || index >= PdfPages.Count - 1) return;

            try
            {
                var pagesList = PdfPages.ToList();
                (pagesList[index + 1], pagesList[index]) = (pagesList[index], pagesList[index + 1]);

                using var newDoc = new PdfDocument();
                using var sourceDoc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Import);

                foreach (var pageInfo in pagesList)
                {
                    newDoc.AddPage(sourceDoc.Pages[pageInfo.PageIndex]);
                }

                newDoc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);
                SelectedPage = PdfPages[index + 1];
            }
            catch (Exception ex)
            {
                FileLogger.LogError("MovePageDown", ex);
                MessageBox.Show($"Sayfa taşınamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public void ExtractPage()
        {
            if (SelectedPage == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            var saveDlg = new SaveFileDialog
            {
                Filter = "PDF Dosyası|*.pdf",
                FileName = $"sayfa_{SelectedPage.PageNumber}.pdf",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (saveDlg.ShowDialog() != true) return;

            try
            {
                using var sourceDoc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Import);
                using var newDoc = new PdfDocument();
                newDoc.AddPage(sourceDoc.Pages[SelectedPage.PageIndex]);
                newDoc.Save(saveDlg.FileName);

                MessageBox.Show("Sayfa çıkarıldı!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ExtractPage", ex);
                MessageBox.Show($"Sayfa çıkarılamadı: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public void RotateAllPages()
        {
            if (string.IsNullOrWhiteSpace(EditPdfPath)) return;

            try
            {
                using var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify);
                foreach (PdfPage page in doc.Pages)
                {
                    page.Rotate = (page.Rotate + SelectedRotation) % 360;
                }
                doc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);

                MessageBox.Show("Tüm sayfalar döndürüldü!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("RotateAllPages", ex);
                MessageBox.Show($"Döndürme başarısız: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public void AddWatermark()
        {
            if (string.IsNullOrWhiteSpace(EditPdfPath) || string.IsNullOrWhiteSpace(WatermarkText)) return;

            try
            {
                using var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify);
                foreach (PdfPage page in doc.Pages)
                {
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    var font = new XFont("Arial", 48);
                    var brush = new XSolidBrush(XColor.FromArgb(80, 128, 128, 128));

                    gfx.TranslateTransform(page.Width / 2, page.Height / 2);
                    gfx.RotateTransform(-45);
                    gfx.DrawString(WatermarkText, font, brush, 0, 0, XStringFormats.Center);
                }
                doc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);

                MessageBox.Show("Filigran eklendi!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("AddWatermark", ex);
                MessageBox.Show($"Filigran eklenemedi: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public void SaveEditedPdf()
        {
            if (string.IsNullOrWhiteSpace(EditPdfPath)) return;

            var saveDlg = new SaveFileDialog
            {
                Filter = "PDF Dosyası|*.pdf",
                FileName = Path.GetFileName(EditPdfPath),
                InitialDirectory = Path.GetDirectoryName(EditPdfPath)
            };

            if (saveDlg.ShowDialog() == true)
            {
                try
                {
                    File.Copy(EditPdfPath, saveDlg.FileName, true);
                    var openResult = MessageBox.Show("PDF kaydedildi!\n\nDosya konumu açılsın mı?", "DocMaster Pro",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (openResult == MessageBoxResult.Yes)
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDlg.FileName}\"");
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("SaveEditedPdf", ex);
                    MessageBox.Show($"Kayıt başarısız: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ==================== Tab 7: Yazıcı & Tarayıcı Komutları ====================
        [RelayCommand]
        public async Task RefreshDevicesAsync()
        {
            IsBusy = true;
            StatusMessage = "Aygıtlar taranıyor...";

            try
            {
                await Task.Run(() =>
                {
                    var scanners = _scannerService.DiscoverScanners();
                    var printers = _printerService.DiscoverPrinters();

                    App.Current.Dispatcher.Invoke(() =>
                    {
                        AvailableScanners.Clear();
                        foreach (var sc in scanners) AvailableScanners.Add(sc);
                        if (AvailableScanners.Count > 0 && SelectedScanner == null)
                            SelectedScanner = AvailableScanners[0];

                        AvailablePrinters.Clear();
                        foreach (var pr in printers) AvailablePrinters.Add(pr);
                        if (AvailablePrinters.Count > 0 && SelectedPrinter == null)
                            SelectedPrinter = AvailablePrinters.FirstOrDefault(p => p.IsDefault) ?? AvailablePrinters[0];
                    });
                });

                StatusMessage = $"Keşif tamamlandı: {AvailableScanners.Count} tarayıcı, {AvailablePrinters.Count} yazıcı bulundu.";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("RefreshDevicesAsync", ex);
                StatusMessage = $"Aygıtlar aranırken hata: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanStartScan))]
        public async Task StartScanAsync()
        {
            if (SelectedScanner == null)
            {
                MessageBox.Show("Lütfen tarama yapılacak bir tarayıcı seçin.\nTarayıcı bağlı değilse 'Aygıtları Yenile' butonuna basın.",
                    "Tarayıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            Progress = 0;
            StatusMessage = $"{SelectedScanner.Name} üzerinden taranıyor...";
            _scanCts = new CancellationTokenSource();

            var progressReporter = new Progress<string>(msg => StatusMessage = msg);

            // Ayarlar tabından gelen ayarları eşitle
            ScanOptions.DuplexScan = DuplexScanEnabled;
            ScanOptions.RemoveBlankPages = AutoDeleteBlankPages;
            ScanOptions.BlankPageThreshold = BlankPageSensitivity;

            try
            {
                var pages = await _scannerService.ScanDocumentsAsync(SelectedScanner, ScanOptions, _scanCts.Token, progressReporter);

                foreach (var p in pages)
                {
                    p.PageNumber = ScannedPages.Count + 1;
                    ScannedPages.Add(p);
                }

                if (ScannedPages.Count > 0)
                {
                    SelectedScannedPage = ScannedPages.Last();
                }

                StatusMessage = $"Tarama tamamlandı: {pages.Count} sayfa eklendi (Toplam: {ScannedPages.Count} sayfa).";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("StartScanAsync", ex);
                StatusMessage = $"Tarama hatası: {ex.Message}";
                MessageBox.Show($"Tarama sırasında hata oluştu:\n\n{ex.Message}", "Tarama Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
            }
        }

        private bool CanStartScan() => !IsBusy;

        [RelayCommand]
        public void ClearScannedPages()
        {
            ScannedPages.Clear();
            SelectedScannedPage = null;
            StatusMessage = "Taranan sayfalar temizlendi.";
        }

        [RelayCommand]
        public void RotateScannedPage()
        {
            if (SelectedScannedPage == null) return;
            SelectedScannedPage.Rotation = (SelectedScannedPage.Rotation + 90) % 360;
        }

        [RelayCommand]
        public void DeleteScannedPage(ScannedPageItem? page)
        {
            page ??= SelectedScannedPage;
            if (page == null) return;

            ScannedPages.Remove(page);
            for (int i = 0; i < ScannedPages.Count; i++)
            {
                ScannedPages[i].PageNumber = i + 1;
            }
            SelectedScannedPage = ScannedPages.FirstOrDefault();
        }

        [RelayCommand]
        public async Task SaveScannedToPdfAsync()
        {
            if (ScannedPages.Count == 0)
            {
                MessageBox.Show("Kaydedilecek taranmış sayfa bulunmuyor.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var saveDlg = new SaveFileDialog
            {
                Title = "Taranan Belgeleri PDF Olarak Kaydet",
                Filter = "PDF Dosyası|*.pdf",
                FileName = $"Tarama_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (saveDlg.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = "PDF oluşturuluyor...";
            var progress = new Progress<int>(pct => Progress = pct);

            try
            {
                await _scannerService.SaveScannedPagesToPdfAsync(ScannedPages.ToList(), saveDlg.FileName, CancellationToken.None, progress);
                StatusMessage = $"PDF kaydedildi: {Path.GetFileName(saveDlg.FileName)}";

                var openResult = MessageBox.Show("Taranan sayfalar PDF olarak kaydedildi!\n\nDosya konumu açılsın mı?", "DocMaster Pro",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (openResult == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDlg.FileName}\"");
            }
            catch (Exception ex)
            {
                FileLogger.LogError("SaveScannedToPdfAsync", ex);
                MessageBox.Show($"PDF kaydetme hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
            }
        }

        [RelayCommand]
        public void SelectPrintFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Yazdırılacak Belgeyi Seçin",
                Filter = "Tüm Yazdırılabilir Belgeler|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.docx;*.doc;*.txt;*.rtf|" +
                         "PDF Dosyaları|*.pdf|Görüntü Dosyaları|*.png;*.jpg;*.jpeg;*.bmp;*.tiff|" +
                         "Word Dosyaları|*.docx;*.doc|Tüm Dosyalar|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedPrintFilePath = dlg.FileName;
                StatusMessage = $"Yazdırma için dosya seçildi: {Path.GetFileName(dlg.FileName)}";
                PrintFileCommand.NotifyCanExecuteChanged();
            }
        }

        [RelayCommand(CanExecute = nameof(CanPrintFile))]
        public async Task PrintFileAsync()
        {
            if (SelectedPrinter == null)
            {
                MessageBox.Show("Lütfen bir yazıcı seçin.", "Yazıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(SelectedPrintFilePath) || !File.Exists(SelectedPrintFilePath))
            {
                MessageBox.Show("Lütfen geçerli bir dosya seçin.", "Dosya Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            StatusMessage = $"{SelectedPrinter.Name} yazıcısına gönderiliyor...";
            var progress = new Progress<string>(msg => StatusMessage = msg);

            // Çift taraflı yazdırma ayarını senkronize et
            PrintOptions.DuplexPrint = DuplexPrintEnabled;

            try
            {
                var result = await _printerService.PrintDocumentAsync(SelectedPrinter, SelectedPrintFilePath, PrintOptions, CancellationToken.None, progress);
                StatusMessage = result.Message;

                if (result.Success)
                {
                    MessageBox.Show(result.Message, "Yazdırma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(result.Message, "Yazdırma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintFileAsync", ex);
                MessageBox.Show($"Yazdırma sırasında hata: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanPrintFile() => !IsBusy && !string.IsNullOrEmpty(SelectedPrintFilePath) && File.Exists(SelectedPrintFilePath);

        [RelayCommand(CanExecute = nameof(CanPrintTestPage))]
        public async Task PrintTestPageAsync()
        {
            if (SelectedPrinter == null)
            {
                MessageBox.Show("Lütfen bir yazıcı seçin.", "Yazıcı Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            StatusMessage = $"{SelectedPrinter.Name} için test sayfası hazırlanıyor...";
            var progress = new Progress<string>(msg => StatusMessage = msg);

            try
            {
                var result = await _printerService.PrintTestPageAsync(SelectedPrinter, CancellationToken.None, progress);
                StatusMessage = result.Message;

                if (result.Success)
                {
                    MessageBox.Show($"{SelectedPrinter.Name} için test sayfası başarıyla yazıcıya gönderildi.", "Test Sayfası", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(result.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PrintTestPageAsync", ex);
                MessageBox.Show($"Test sayfası hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanPrintTestPage() => !IsBusy && SelectedPrinter != null;

        // ==================== Tab 8: Ayarlar Komutları ====================
        [RelayCommand]
        public void SelectCleanPdfFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Boş Sayfaları Temizlenecek PDF'i Seçin",
                Filter = "PDF Dosyaları|*.pdf"
            };

            if (dlg.ShowDialog() == true)
            {
                CleanPdfSourcePath = dlg.FileName;
            }
        }

        [RelayCommand(CanExecute = nameof(CanCleanBlankPages))]
        public async Task CleanBlankPagesFromPdfAsync()
        {
            if (string.IsNullOrEmpty(CleanPdfSourcePath) || !File.Exists(CleanPdfSourcePath))
            {
                MessageBox.Show("Lütfen geçerli bir PDF dosyası seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDlg = new SaveFileDialog
            {
                Title = "Temizlenmiş PDF'i Kaydet",
                Filter = "PDF Dosyası|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(CleanPdfSourcePath) + "_temiz.pdf",
                InitialDirectory = Path.GetDirectoryName(CleanPdfSourcePath)
            };

            if (saveDlg.ShowDialog() != true) return;

            IsBusy = true;
            Progress = 0;
            StatusMessage = "Boş sayfalar tespit edilip siliniyor...";

            try
            {
                var progressReporter = new Progress<int>(pct => Progress = pct);
                var (outPath, removed, total) = await _blankDetector.RemoveBlankPagesFromPdfAsync(
                    CleanPdfSourcePath, saveDlg.FileName, BlankPageSensitivity, progressReporter);

                StatusMessage = $"Temizleme tamamlandı: {total} sayfadan {removed} boş sayfa silindi.";

                var openResult = MessageBox.Show(
                    $"İşlem tamamlandı!\n\nToplam Sayfa: {total}\nSilinen Boş Sayfa: {removed}\nKalan Sayfa: {total - removed}\n\nDosya konumu açılsın mı?",
                    "Boş Sayfalar Temizlendi", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (openResult == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDlg.FileName}\"");
            }
            catch (Exception ex)
            {
                FileLogger.LogError("CleanBlankPagesFromPdfAsync", ex);
                MessageBox.Show($"Temizleme hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
            }
        }

        private bool CanCleanBlankPages() => !IsBusy && !string.IsNullOrEmpty(CleanPdfSourcePath) && File.Exists(CleanPdfSourcePath);

        // ==================== Yardımcı Dönüştürme Metodları ====================
        private async Task<string?> ConvertToPdfAsync(DocumentItem doc)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "DocMasterPro");
            Directory.CreateDirectory(tempDir);

            if (doc.Extension == ".pdf")
                return doc.FilePath;

            if (PathValidator.ImageExtensions.Contains(doc.Extension))
                return _conv.ConvertImageToPdf(doc.FilePath);

            string outputPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.pdf");

            if (doc.Extension is ".docx" or ".doc")
            {
                await _officeConv.ConvertWordToPdfAsync(doc.FilePath, outputPath);
                return outputPath;
            }
            else if (doc.Extension is ".xlsx" or ".xls")
            {
                await _officeConv.ConvertExcelToPdfAsync(doc.FilePath, outputPath);
                return outputPath;
            }
            else if (doc.Extension is ".pptx" or ".ppt")
            {
                await _officeConv.ConvertPowerPointToPdfAsync(doc.FilePath, outputPath);
                return outputPath;
            }
            else if (doc.Extension is ".txt" or ".rtf" or ".html" or ".htm")
            {
                await _officeConv.ConvertTxtToPdfAsync(doc.FilePath, outputPath);
                return outputPath;
            }

            return null;
        }

        private async Task ConvertOfficeFileToPdfAsync(string inputPath, string outputPath, string extension)
        {
            if (extension is ".docx" or ".doc")
                await _officeConv.ConvertWordToPdfAsync(inputPath, outputPath);
            else if (extension is ".xlsx" or ".xls")
                await _officeConv.ConvertExcelToPdfAsync(inputPath, outputPath);
            else if (extension is ".pptx" or ".ppt")
                await _officeConv.ConvertPowerPointToPdfAsync(inputPath, outputPath);
            else if (extension is ".txt" or ".rtf" or ".html" or ".htm")
                await _officeConv.ConvertTxtToPdfAsync(inputPath, outputPath);
            else
                throw new NotSupportedException($"Desteklenmeyen dosya formatı: {extension}");
        }
    }

    // ==================== PDF Sayfa Bilgisi ====================
    public partial class PdfPageInfo : ObservableObject
    {
        [ObservableProperty]
        private int pageNumber;

        [ObservableProperty]
        private int pageIndex;

        [ObservableProperty]
        private string info = "";

        [ObservableProperty]
        private int rotation;

        [ObservableProperty]
        private BitmapSource? thumbnail;
    }
}
