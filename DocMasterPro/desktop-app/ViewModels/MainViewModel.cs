using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.Services;
using ImageMagick;
using Microsoft.Win32;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;

namespace DocConverter.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly PdfService _pdf = new();
        private readonly ConverterService _conv = new();
        private readonly OfficeConverterService _officeConv = new();
        private readonly UpdateService _updateService = new();
        private readonly SemaphoreSlim _previewRenderGate = new(1, 1);
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _previewLoadCts;
        private bool _ghostscriptWarningShown;

        private const int PdfPreviewDensityDpi = 144;
        private const int PdfPreviewMaxWidth = 1200;

        public PdfStudioViewModel PdfStudio { get; } = new();

        // ==================== Ortak Özellikler ====================
        [ObservableProperty]
        private int progress;

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private int selectedWorkspaceIndex;

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

        // ==================== Tab 6: PDF → Word ====================
        [ObservableProperty]
        private ObservableCollection<DocumentItem> pdfToWordDocuments = new();

        // ==================== Tab 7: PDF Düzenleme ====================
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

        [RelayCommand]
        public void SelectWorkspace(string? indexText)
        {
            if (int.TryParse(indexText, out int index))
                SelectedWorkspaceIndex = index;
        }

        [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
        public async Task CheckForUpdates()
        {
            await _updateService.CheckForUpdatesAsync(notifyWhenCurrent: true);
        }

        public async Task CheckForUpdatesOnStartupAsync()
        {
            await _updateService.CheckForUpdatesAsync(notifyWhenCurrent: false);
        }

        private bool CanCheckForUpdates() => !IsBusy;

        // ==================== Constructor ====================
        public MainViewModel()
        {
            MergeDocuments.CollectionChanged += (_, _) => MergeCommand.NotifyCanExecuteChanged();
            ImageDocuments.CollectionChanged += (_, _) => ConvertImagesToPdfCommand.NotifyCanExecuteChanged();
            OfficeDocuments.CollectionChanged += (_, _) => ConvertOfficeToPdfCommand.NotifyCanExecuteChanged();
            PdfToWordDocuments.CollectionChanged += (_, _) => ConvertPdfToWordCommand.NotifyCanExecuteChanged();
            PdfPages.CollectionChanged += (_, _) => NotifyEditCommandStates();
        }

        partial void OnIsBusyChanged(bool value)
        {
            MergeCommand.NotifyCanExecuteChanged();
            SplitCommand.NotifyCanExecuteChanged();
            ConvertImagesToPdfCommand.NotifyCanExecuteChanged();
            ExportPdfToImagesCommand.NotifyCanExecuteChanged();
            ConvertOfficeToPdfCommand.NotifyCanExecuteChanged();
            ConvertPdfToWordCommand.NotifyCanExecuteChanged();
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
            NotifyEditCommandStates();
        }

        partial void OnEditPdfPathChanged(string value)
        {
            NotifyEditCommandStates();
        }

        partial void OnSelectedPageChanged(PdfPageInfo? value)
        {
            NotifyEditCommandStates();
        }

        partial void OnWatermarkTextChanged(string value)
        {
            AddWatermarkCommand.NotifyCanExecuteChanged();
        }

        private void NotifyEditCommandStates()
        {
            OpenPdfForEditCommand.NotifyCanExecuteChanged();
            DeletePageCommand.NotifyCanExecuteChanged();
            RotatePageCommand.NotifyCanExecuteChanged();
            MovePageUpCommand.NotifyCanExecuteChanged();
            MovePageDownCommand.NotifyCanExecuteChanged();
            ExtractPageCommand.NotifyCanExecuteChanged();
            RotateAllPagesCommand.NotifyCanExecuteChanged();
            AddWatermarkCommand.NotifyCanExecuteChanged();
            SaveEditedPdfCommand.NotifyCanExecuteChanged();
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

        private void CancelCurrentOperation()
        {
            _cts?.Cancel();
            StatusMessage = "İptal ediliyor...";
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

            var saveDlg = new SaveFileDialog
            {
                Filter = "PDF Dosyası|*.pdf",
                FileName = "birlesik.pdf",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (saveDlg.ShowDialog() != true) return;

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "Dönüştürülüyor...";

            var pdfPaths = new List<string>();
            var tempFiles = new List<string>();

            try
            {
                int total = MergeDocuments.Count;
                int current = 0;

                foreach (var doc in MergeDocuments)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 50) / total;
                    StatusMessage = $"Dönüştürülüyor: {doc.FileName}";

                    try
                    {
                        string? pdfPath = await ConvertToPdfAsync(doc, _cts.Token);
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
                    catch (OperationCanceledException)
                    {
                        doc.Status = "Ready";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        doc.Status = "Error";
                        FileLogger.LogError($"Merge ({doc.FileName})", ex);
                    }
                }

                StatusMessage = "PDF birleştiriliyor...";
                if (pdfPaths.Count == 0)
                {
                    StatusMessage = "Hata";
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show("Birleştirilecek geçerli PDF oluşturulamadı.\nDetaylar için log dosyasını kontrol edin.",
                            "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }

                var reporter = new Progress<int>(v =>
                {
                    Progress = 50 + (v / 2);
                });
                await _pdf.MergePdfsAsync(pdfPaths, saveDlg.FileName, _cts.Token, reporter);

                StatusMessage = "Tamamlandı";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("Birleştirme tamamlandı!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "İptal edildi";
            }
            catch (Exception ex)
            {
                StatusMessage = "Hata";
                FileLogger.LogError("Merge", ex);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Birleştirme başarısız: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                IsBusy = false;
                Progress = 100;
                _cts?.Dispose();
                _cts = null;
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
            PdfToWordDocuments.Remove(item);
        }

        [RelayCommand]
        public void ClearMerge()
        {
            MergeDocuments.Clear();
        }

        [RelayCommand]
        public void CancelOperation()
        {
            CancelCurrentOperation();
        }

        // ==================== Tab 2: PDF Bölme Komutları ====================
        [RelayCommand]
        public void OpenPdfForSplit()
        {
            var dlg = new OpenFileDialog { Filter = "PDF Dosyası|*.pdf" };
            if (dlg.ShowDialog() == true)
            {
                SplitPdfPath = dlg.FileName;

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

            int maxPage = _conv.GetPdfPageCount(SplitPdfPath);
            if (maxPage <= 0)
            {
                MessageBox.Show("PDF dosyası açılamadı veya sayfa bulunamadı.",
                    "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "PDF böliniyor...";

            try
            {
                var reporter = new Progress<int>(v => Progress = v);
                await _pdf.SplitPdfAsync(SplitPdfPath, SplitOutputFolder, ranges, _cts.Token, reporter);

                StatusMessage = "Tamamlandı";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("PDF bölme tamamlandı!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "İptal edildi";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("SplitPdf", ex);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Hata: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
                _cts?.Dispose();
                _cts = null;
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

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "Görüntüler dönüştürülüyor...";

            var tempFiles = new List<string>();

            try
            {
                int total = ImageDocuments.Count;
                int current = 0;

                var pdfPaths = new List<string>();
                var failedItems = new List<string>();

                foreach (var doc in ImageDocuments)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 100) / total;
                    StatusMessage = $"İşleniyor: {doc.FileName}";

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
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show("Hiçbir görüntü PDF'e dönüştürülemedi.\nDetaylar için log dosyasını kontrol edin.",
                            "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }

                if (failedItems.Count > 0)
                {
                    var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                        MessageBox.Show(
                            $"Bazı dosyalar dönüştürülemedi: {string.Join(", ", failedItems)}\n\nDevam edilsin mi?",
                            "Uyarı", MessageBoxButton.YesNo, MessageBoxImage.Warning));

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                StatusMessage = "PDF birleştiriliyor...";
                var reporter = new Progress<int>(v => Progress = v);
                await _pdf.MergePdfsAsync(pdfPaths, saveDlg.FileName, _cts.Token, reporter);

                StatusMessage = "Tamamlandı";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("PDF oluşturuldu!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "İptal edildi";
            }
            catch (Exception ex)
            {
                StatusMessage = "Hata";
                FileLogger.LogError("ConvertImagesToPdf", ex);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"PDF oluşturulamadı: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                IsBusy = false;
                Progress = 100;
                _cts?.Dispose();
                _cts = null;
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

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "PDF görüntülere dönüştürülüyor...";

            try
            {
                var reporter = new Progress<int>(v => Progress = v);
                await _conv.ConvertPdfToImagesAsync(
                    ExportPdfPath,
                    ExportOutputFolder,
                    SelectedImageFormat.ToLowerInvariant(),
                    _cts.Token,
                    reporter);

                StatusMessage = "Tamamlandı";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("Dönüştürme tamamlandı!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "İptal edildi";
            }
            catch (Exception ex)
            {
                FileLogger.LogError("ExportPdfToImages", ex);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Hata: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private bool CanExportPdfToImages() => !IsBusy && !string.IsNullOrWhiteSpace(ExportPdfPath);

        // ==================== Tab 5: Office → PDF Komutları ====================
        [RelayCommand]
        public void AddOfficeFiles()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Office Dosyaları|*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt|Tüm Dosyalar|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var f in dlg.FileNames)
            {
                if (!PathValidator.IsPathSafe(f)) continue;
                OfficeDocuments.Add(CreateDocumentItem(f));
            }
        }

        [RelayCommand(CanExecute = nameof(CanConvertOffice))]
        public async Task ConvertOfficeToPdf()
        {
            if (OfficeDocuments.Count == 0) return;

            var folderDlg = new OpenFolderDialog { Title = "PDF'lerin kaydedileceği klasörü seçin" };
            if (folderDlg.ShowDialog() != true) return;

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "Office dosyaları dönüştürülüyor...";

            try
            {
                int total = OfficeDocuments.Count;
                int current = 0;

                foreach (var doc in OfficeDocuments)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 100) / total;
                    StatusMessage = $"Dönüştürülüyor: {doc.FileName}";

                    try
                    {
                        string outputPath = Path.Combine(folderDlg.FolderName,
                            Path.GetFileNameWithoutExtension(doc.FileName) + ".pdf");

                        await ConvertOfficeFileToPdfAsync(doc.FilePath, outputPath, doc.Extension, _cts.Token);
                        doc.Status = "Done";
                    }
                    catch (OperationCanceledException)
                    {
                        doc.Status = "Ready";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        doc.Status = "Error";
                        FileLogger.LogError($"OfficeToPdf ({doc.FileName})", ex);
                    }
                }

                StatusMessage = "Tamamlandı";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("Dönüştürme tamamlandı!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "İptal edildi";
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
                _cts?.Dispose();
                _cts = null;
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

        // ==================== Tab 6: PDF → Word Komutları ====================
        [RelayCommand]
        public void AddPdfToWordFiles()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF Dosyaları|*.pdf|Tüm Dosyalar|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var f in dlg.FileNames)
            {
                if (!PathValidator.IsPathSafe(f)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".pdf") continue;

                PdfToWordDocuments.Add(CreateDocumentItem(f));
            }
        }

        [RelayCommand(CanExecute = nameof(CanConvertPdfToWord))]
        public async Task ConvertPdfToWord()
        {
            if (PdfToWordDocuments.Count == 0) return;

            if (!_officeConv.IsOfficeInstalled())
            {
                MessageBox.Show(
                    "PDF'den Word'e dönüştürme için Microsoft Word 2013 veya daha yeni bir sürüm kurulu olmalıdır.",
                    "DocMaster Pro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var folderDlg = new OpenFolderDialog { Title = "DOCX dosyalarının kaydedileceği klasörü seçin" };
            if (folderDlg.ShowDialog() != true) return;

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "PDF dosyaları Word'e dönüştürülüyor...";

            try
            {
                int total = PdfToWordDocuments.Count;
                int current = 0;

                foreach (var doc in PdfToWordDocuments)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    doc.Status = "Converting";
                    current++;
                    Progress = (current * 100) / total;
                    StatusMessage = $"Dönüştürülüyor: {doc.FileName}";

                    try
                    {
                        string outputPath = Path.Combine(folderDlg.FolderName,
                            Path.GetFileNameWithoutExtension(doc.FileName) + ".docx");

                        await _officeConv.ConvertPdfToWordAsync(doc.FilePath, outputPath, _cts.Token);
                        doc.Status = "Done";
                    }
                    catch (OperationCanceledException)
                    {
                        doc.Status = "Ready";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        doc.Status = "Error";
                        FileLogger.LogError($"PdfToWord ({doc.FileName})", ex);
                    }
                }

                StatusMessage = "Tamamlandı";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("PDF'den Word'e dönüştürme tamamlandı!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "İptal edildi";
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private bool CanConvertPdfToWord() => !IsBusy && PdfToWordDocuments.Count > 0;

        [RelayCommand]
        public void RemovePdfToWordFile(DocumentItem item)
        {
            if (item != null) PdfToWordDocuments.Remove(item);
        }

        [RelayCommand]
        public void ClearPdfToWord()
        {
            PdfToWordDocuments.Clear();
        }

        // ==================== Tab 6: PDF Düzenleme Komutları ====================
        [RelayCommand(CanExecute = nameof(CanOpenPdfForEdit))]
        public async Task OpenPdfForEdit()
        {
            var dlg = new OpenFileDialog { Filter = "PDF Dosyası|*.pdf" };
            if (dlg.ShowDialog() == true)
            {
                EditPdfPath = dlg.FileName;
                await LoadPdfPagesAsync(dlg.FileName);
            }
        }

        private bool CanOpenPdfForEdit() => !IsBusy;

        private async Task LoadPdfPagesAsync(string pdfPath, int? selectedPageIndex = null)
        {
            var previewToken = ResetPreviewLoad();
            PdfPages.Clear();
            SelectedPage = null;

            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                MessageBox.Show("PDF dosya yolu boş.", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(pdfPath))
            {
                MessageBox.Show($"PDF dosyası bulunamadı: {pdfPath}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                IsBusy = true;
                StatusMessage = "PDF yükleniyor...";

                bool ghostscriptAvailable = IsGhostscriptAvailable();
                var pages = await Task.Run(() => LoadPdfPageMetadata(pdfPath, ghostscriptAvailable, previewToken), previewToken);

                foreach (var page in pages)
                    PdfPages.Add(page);

                if (selectedPageIndex is >= 0 && selectedPageIndex < PdfPages.Count)
                    SelectedPage = PdfPages[selectedPageIndex.Value];

                if (!ghostscriptAvailable && !_ghostscriptWarningShown)
                {
                    _ghostscriptWarningShown = true;
                    MessageBox.Show(
                        "PDF sayfa önizlemeleri için Ghostscript önerilir.\n" +
                        "Sayfa bilgileri gösteriliyor.\n\n" +
                        "Önizleme için: https://ghostscript.com/releases/gsdnld.html",
                        "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                FileLogger.LogError("LoadPdfPages", ex);
                try
                {
                    var pages = LoadPdfPageMetadata(pdfPath, canLoadPreview: false, CancellationToken.None);
                    foreach (var page in pages)
                        PdfPages.Add(page);
                }
                catch { }
                MessageBox.Show($"PDF açılırken hata oluştu: {ex.Message}\n\nSayfa bilgileri gösteriliyor.",
                    "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        private List<PdfPageInfo> LoadPdfPageMetadata(string pdfPath, bool canLoadPreview, CancellationToken cancellationToken)
        {
            var pages = new List<PdfPageInfo>();
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);

            if (doc.PageCount == 0)
            {
                throw new InvalidOperationException("PDF dosyasında sayfa bulunamadı.");
            }

            for (int i = 0; i < doc.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = doc.Pages[i];
                pages.Add(new PdfPageInfo
                {
                    PageNumber = i + 1,
                    PageIndex = i,
                    Info = $"{page.Width:F0} x {page.Height:F0} pt",
                    Rotation = page.Rotate,
                    Thumbnail = null,
                    CanLoadPreview = canLoadPreview,
                    PreviewStatus = canLoadPreview
                        ? "Önizleme hazırlanıyor..."
                        : "Önizleme için Ghostscript gerekli."
                });
            }

            return pages;
        }

        public async Task EnsurePagePreviewAsync(PdfPageInfo? page)
        {
            if (page == null || string.IsNullOrWhiteSpace(EditPdfPath))
                return;

            if (!page.CanLoadPreview || page.IsPreviewLoaded || page.IsPreviewLoading)
                return;

            var token = _previewLoadCts?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested)
                return;

            bool gateAcquired = false;
            page.IsPreviewLoading = true;
            page.PreviewError = "";
            page.PreviewStatus = "Önizleme yükleniyor...";

            try
            {
                await _previewRenderGate.WaitAsync(token);
                gateAcquired = true;

                if (page.IsPreviewLoaded || token.IsCancellationRequested)
                    return;

                string pdfPath = EditPdfPath;
                int pageIndex = page.PageIndex;
                var thumbnail = await Task.Run(
                    () => RenderPdfPagePreview(pdfPath, pageIndex, token),
                    token);

                token.ThrowIfCancellationRequested();

                if (thumbnail == null)
                {
                    page.PreviewError = "Önizleme oluşturulamadı.";
                    page.PreviewStatus = page.PreviewError;
                    return;
                }

                page.Thumbnail = thumbnail;
                page.IsPreviewLoaded = true;
                page.PreviewStatus = "";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                page.PreviewError = "Önizleme oluşturulamadı.";
                page.PreviewStatus = page.PreviewError;
                FileLogger.LogError("EnsurePagePreview", ex);
            }
            finally
            {
                if (gateAcquired)
                    _previewRenderGate.Release();

                page.IsPreviewLoading = false;
            }
        }

        private BitmapSource? RenderPdfPagePreview(string pdfPath, int pageIndex, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var settings = new MagickReadSettings
                {
                    Density = new Density(PdfPreviewDensityDpi),
                    FrameIndex = (uint)pageIndex,
                    FrameCount = 1
                };

                using var images = new MagickImageCollection();
                images.Read(pdfPath, settings);

                if (images.Count == 0)
                    return null;

                var image = images[0];
                image.FilterType = FilterType.Lanczos;
                image.Quality = 90;

                if (image.Width > PdfPreviewMaxWidth)
                    image.Resize(PdfPreviewMaxWidth, 0);

                cancellationToken.ThrowIfCancellationRequested();
                var bytes = image.ToByteArray(MagickFormat.Png);

                var bitmap = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                return bitmap;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("RenderPdfPagePreview", ex);
                return null;
            }
        }

        private CancellationToken ResetPreviewLoad()
        {
            _previewLoadCts?.Cancel();
            _previewLoadCts = new CancellationTokenSource();
            return _previewLoadCts.Token;
        }

        private async Task WaitForPreviewRenderIdleAsync()
        {
            _previewLoadCts?.Cancel();

            await _previewRenderGate.WaitAsync();
            _previewRenderGate.Release();
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

        private bool HasEditablePdf() =>
            !IsBusy && !string.IsNullOrWhiteSpace(EditPdfPath) && PdfPages.Count > 0;

        private bool CanEditSelectedPage() => HasEditablePdf() && SelectedPage != null;

        private bool CanEditPage(PdfPageInfo? page) => HasEditablePdf() && page != null;

        private bool CanMovePageUp() =>
            CanEditSelectedPage() && SelectedPage != null && PdfPages.IndexOf(SelectedPage) > 0;

        private bool CanMovePageDown()
        {
            if (!CanEditSelectedPage() || SelectedPage == null)
                return false;

            int index = PdfPages.IndexOf(SelectedPage);
            return index >= 0 && index < PdfPages.Count - 1;
        }

        private bool CanAddWatermark() => HasEditablePdf() && !string.IsNullOrWhiteSpace(WatermarkText);

        [RelayCommand(CanExecute = nameof(CanEditPage))]
        public async Task DeletePage(PdfPageInfo? page)
        {
            if (page == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            var result = MessageBox.Show($"Sayfa {page.PageNumber} silinsin mi?",
                "DocMaster Pro", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                IsBusy = true;
                StatusMessage = "Sayfa siliniyor...";
                await WaitForPreviewRenderIdleAsync();

                int nextSelectedIndex;
                using (var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify))
                {
                    int selectedIndex = page.PageIndex;
                    if (doc.PageCount <= 1)
                        throw new InvalidOperationException("PDF dosyasında en az bir sayfa kalmalı.");

                    if (page.PageIndex < 0 || page.PageIndex >= doc.PageCount)
                        throw new ArgumentOutOfRangeException(nameof(page), "Sayfa indeksi geçersiz.");

                    doc.Pages.RemoveAt(page.PageIndex);
                    doc.Save(EditPdfPath);
                    nextSelectedIndex = Math.Min(selectedIndex, doc.PageCount - 1);
                }

                await LoadPdfPagesAsync(EditPdfPath, nextSelectedIndex);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("DeletePage", ex);
                MessageBox.Show($"Sayfa silinemedi: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        [RelayCommand(CanExecute = nameof(CanEditPage))]
        public async Task RotatePage(PdfPageInfo? page)
        {
            if (page == null || string.IsNullOrWhiteSpace(EditPdfPath)) return;

            try
            {
                IsBusy = true;
                StatusMessage = "Sayfa döndürülüyor...";
                await WaitForPreviewRenderIdleAsync();

                using (var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify))
                {
                    if (page.PageIndex < 0 || page.PageIndex >= doc.PageCount)
                        throw new ArgumentOutOfRangeException(nameof(page), "Sayfa indeksi geçersiz.");

                    var pdfPage = doc.Pages[page.PageIndex];
                    pdfPage.Rotate = (pdfPage.Rotate + 90) % 360;
                    doc.Save(EditPdfPath);
                }

                await LoadPdfPagesAsync(EditPdfPath, page.PageIndex);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("RotatePage", ex);
                MessageBox.Show($"Sayfa döndürülemedi: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        [RelayCommand(CanExecute = nameof(CanMovePageUp))]
        public async Task MovePageUp()
        {
            if (SelectedPage == null || string.IsNullOrWhiteSpace(EditPdfPath))
            {
                MessageBox.Show("Önce bir sayfa seçin.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int index = PdfPages.IndexOf(SelectedPage);
            if (index <= 0)
            {
                MessageBox.Show("Bu sayfa zaten en üstte.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await MoveSelectedPageAsync(index, index - 1);
        }

        [RelayCommand(CanExecute = nameof(CanMovePageDown))]
        public async Task MovePageDown()
        {
            if (SelectedPage == null || string.IsNullOrWhiteSpace(EditPdfPath))
            {
                MessageBox.Show("Önce bir sayfa seçin.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int index = PdfPages.IndexOf(SelectedPage);
            if (index < 0 || index >= PdfPages.Count - 1)
            {
                MessageBox.Show("Bu sayfa zaten en altta.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await MoveSelectedPageAsync(index, index + 1);
        }

        private async Task MoveSelectedPageAsync(int fromIndex, int toIndex)
        {
            if (string.IsNullOrWhiteSpace(EditPdfPath))
                return;

            try
            {
                IsBusy = true;
                StatusMessage = "Sayfa taşınıyor...";
                await WaitForPreviewRenderIdleAsync();

                await _pdf.MovePageAsync(EditPdfPath, fromIndex, toIndex);
                await LoadPdfPagesAsync(EditPdfPath, toIndex);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("MovePage", ex);
                MessageBox.Show($"Sayfa taşınamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        [RelayCommand(CanExecute = nameof(CanEditSelectedPage))]
        public async Task ExtractPage()
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
                IsBusy = true;
                StatusMessage = "Sayfa çıkarılıyor...";
                await WaitForPreviewRenderIdleAsync();

                using var sourceDoc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Import);
                if (SelectedPage.PageIndex < 0 || SelectedPage.PageIndex >= sourceDoc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(SelectedPage), "Sayfa indeksi geçersiz.");

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
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        [RelayCommand(CanExecute = nameof(HasEditablePdf))]
        public async Task RotateAllPages()
        {
            if (string.IsNullOrWhiteSpace(EditPdfPath)) return;

            try
            {
                IsBusy = true;
                StatusMessage = "Tüm sayfalar döndürülüyor...";
                await WaitForPreviewRenderIdleAsync();

                int? selectedIndex = SelectedPage?.PageIndex;
                using (var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify))
                {
                    foreach (PdfPage page in doc.Pages)
                    {
                        page.Rotate = (page.Rotate + SelectedRotation) % 360;
                    }
                    doc.Save(EditPdfPath);
                }
                await LoadPdfPagesAsync(EditPdfPath, selectedIndex);

                MessageBox.Show("Tüm sayfalar döndürüldü!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("RotateAllPages", ex);
                MessageBox.Show($"Döndürme başarısız: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        [RelayCommand(CanExecute = nameof(CanAddWatermark))]
        public async Task AddWatermark()
        {
            if (string.IsNullOrWhiteSpace(EditPdfPath) || string.IsNullOrWhiteSpace(WatermarkText)) return;

            try
            {
                IsBusy = true;
                StatusMessage = "Filigran uygulanıyor...";
                await WaitForPreviewRenderIdleAsync();

                int? selectedIndex = SelectedPage?.PageIndex;
                using (var doc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Modify))
                {
                    foreach (PdfPage page in doc.Pages)
                    {
                        var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                        var font = new XFont("Arial", 48);
                        var brush = new XSolidBrush(XColor.FromArgb(80, 128, 128, 128));

                        gfx.TranslateTransform(page.Width.Point / 2, page.Height.Point / 2);
                        gfx.RotateTransform(-45);
                        gfx.DrawString(WatermarkText, font, brush, 0, 0, XStringFormats.Center);
                    }
                    doc.Save(EditPdfPath);
                }
                await LoadPdfPagesAsync(EditPdfPath, selectedIndex);

                MessageBox.Show("Filigran eklendi!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("AddWatermark", ex);
                MessageBox.Show($"Filigran eklenemedi: {ex.Message}", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                StatusMessage = "Hazır";
            }
        }

        [RelayCommand(CanExecute = nameof(HasEditablePdf))]
        public async Task SaveEditedPdf()
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
                    IsBusy = true;
                    StatusMessage = "PDF kaydediliyor...";
                    await WaitForPreviewRenderIdleAsync();

                    File.Copy(EditPdfPath, saveDlg.FileName, true);
                    MessageBox.Show("PDF kaydedildi!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("SaveEditedPdf", ex);
                    MessageBox.Show($"Kayıt başarısız: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    IsBusy = false;
                    StatusMessage = "Hazır";
                }
            }
        }

        // ==================== Yardımcı Metodlar ====================
        private async Task<string?> ConvertToPdfAsync(DocumentItem doc, CancellationToken cancellationToken = default)
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
                await _officeConv.ConvertWordToPdfAsync(doc.FilePath, outputPath, cancellationToken);
                return outputPath;
            }
            else if (doc.Extension is ".xlsx" or ".xls")
            {
                await _officeConv.ConvertExcelToPdfAsync(doc.FilePath, outputPath, cancellationToken);
                return outputPath;
            }
            else if (doc.Extension is ".pptx" or ".ppt")
            {
                await _officeConv.ConvertPowerPointToPdfAsync(doc.FilePath, outputPath, cancellationToken);
                return outputPath;
            }
            else if (doc.Extension is ".txt" or ".rtf" or ".html" or ".htm")
            {
                await _officeConv.ConvertTxtToPdfAsync(doc.FilePath, outputPath, cancellationToken);
                return outputPath;
            }

            return null;
        }

        private async Task ConvertOfficeFileToPdfAsync(string inputPath, string outputPath, string extension, CancellationToken cancellationToken = default)
        {
            if (extension is ".docx" or ".doc")
                await _officeConv.ConvertWordToPdfAsync(inputPath, outputPath, cancellationToken);
            else if (extension is ".xlsx" or ".xls")
                await _officeConv.ConvertExcelToPdfAsync(inputPath, outputPath, cancellationToken);
            else if (extension is ".pptx" or ".ppt")
                await _officeConv.ConvertPowerPointToPdfAsync(inputPath, outputPath, cancellationToken);
            else if (extension is ".txt" or ".rtf")
                await _officeConv.ConvertTxtToPdfAsync(inputPath, outputPath, cancellationToken);
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

        [ObservableProperty]
        private bool canLoadPreview;

        [ObservableProperty]
        private bool isPreviewLoading;

        [ObservableProperty]
        private bool isPreviewLoaded;

        [ObservableProperty]
        private string previewError = "";

        [ObservableProperty]
        private string previewStatus = "";
    }
}
