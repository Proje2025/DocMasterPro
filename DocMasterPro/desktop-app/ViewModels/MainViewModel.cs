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
using PdfiumDoc = PdfiumViewer.Core.PdfDocument;
using PdfiumViewer.Enums;
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
        private readonly OfficeToPdfConverter _officeToPdf;
        private readonly UpdateService _updateService = new();
        private readonly SemaphoreSlim _previewRenderGate = new(1, 1);
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _previewLoadCts;

        private const int PdfPreviewDensityDpi = 144;
        private const int PdfPreviewMaxWidth = 1200;

        public PdfStudioViewModel PdfStudio { get; } = new();
        public HomeHubViewModel HomeHub { get; } = new();
        public DeviceHubViewModel DeviceHub { get; } = new();

        // ==================== Mod ve Bölüm Seçimi ====================
        [ObservableProperty]
        private int selectedAppSection = 0; // 0 = HomeHub, 1 = OfficeSuite, 2 = DeviceHub, 3 = PdfStudio

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

        // ==================== Navigation Commands ====================
        [RelayCommand]
        public void NavigateToHome() => SelectedAppSection = 0;

        [RelayCommand]
        public void NavigateToOffice()
        {
            SelectedAppSection = 1;
            SelectedWorkspaceIndex = 0;
        }

        [RelayCommand]
        public void NavigateToDevices() => SelectedAppSection = 2;

        [RelayCommand]
        public void NavigateToPdfStudio() => SelectedAppSection = 3;

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
        public void CheckForUpdates()
        {
            var dialog = new DocConverter.Views.UpdateDialog();
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            dialog.ShowDialog();
        }

        public async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                var result = await _updateService.CheckForUpdatesAsync();
                if (result.HasUpdate)
                {
                    // Denetim sonucu görünüm modeline verilmezse "Şimdi Güncelle" düğmesi hiçbir şey yapmaz.
                    var updateVm = new UpdateViewModel(_updateService);
                    updateVm.ShowCheckResult(result);

                    var dialog = new DocConverter.Views.UpdateDialog(updateVm);
                    if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
                    {
                        dialog.Owner = Application.Current.MainWindow;
                    }
                    dialog.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("CheckForUpdatesOnStartupAsync", ex);
            }
        }

        private bool CanCheckForUpdates() => !IsBusy;

        // ==================== Office dönüştürme motoru ====================
        /// <summary>
        /// Office belgelerini PDF'e çevirmek için kullanılacak motor. Arayüzde durum çubuğu olarak gösterilir.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowWordRequiredNotice))]
        private OfficeEngineStatus officeEngine = new(OfficeEngineKind.None);

        /// <summary>PDF → Word yalnızca Microsoft Word ile yapılır; Word yoksa sekmede açıklama gösterilir.</summary>
        public bool ShowWordRequiredNotice => !OfficeEngine.CanConvertPdfToWord;

        /// <summary>
        /// LibreOffice'i indirip hazırlayan servis. Null dönerse indirme önerilmez; motor bulunamadığında
        /// kullanıcıya ne kurması gerektiği söylenir.
        /// </summary>
        internal Func<IOfficeEngineInstaller?> EngineInstallerFactory { get; set; } = () => new LibreOfficeEngineInstaller();

        /// <summary>Motor göstergesini bilgisayarın şu anki durumuna göre yeniler.</summary>
        public void RefreshOfficeEngine()
        {
            try
            {
                OfficeEngine = _officeToPdf.GetStatus();
            }
            catch (Exception ex)
            {
                FileLogger.LogError("OfficeEngine", ex);
            }
        }

        /// <summary>
        /// Verilen uzantılardaki belgeler için bir dönüştürme motoru olduğundan emin olur. Yoksa LibreOffice'in
        /// indirilmesini önerir; indirme kullanılamıyorsa ne yapılması gerektiğini söyler.
        /// </summary>
        /// <returns>Dönüştürmeye devam edilebiliyorsa true.</returns>
        private bool EnsureOfficeEngine(IEnumerable<string> extensions)
        {
            RefreshOfficeEngine();

            var missing = extensions
                .Where(extension => OfficeToPdfConverter.AppFor(extension) != null && !_officeToPdf.HasEngineFor(extension))
                .Distinct()
                .ToList();
            if (missing.Count == 0)
                return true;

            IOfficeEngineInstaller? installer = EngineInstallerFactory();
            if (installer == null)
            {
                MessageBox.Show(
                    "Bu belgeleri PDF'e dönüştürmek için Microsoft Office ya da LibreOffice gerekir; bilgisayarınızda ikisi de bulunamadı.\n\n" +
                    "Ücretsiz LibreOffice'i libreoffice.org adresinden kurduktan sonra yeniden deneyin. DocMaster Pro onu otomatik olarak kullanır.",
                    "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var dialog = new Views.OfficeEngineSetupDialog(new OfficeEngineSetupViewModel(installer))
            {
                Owner = Application.Current?.MainWindow
            };
            bool ready = dialog.ShowDialog() == true;
            RefreshOfficeEngine();
            return ready && missing.All(_officeToPdf.HasEngineFor);
        }

        [RelayCommand]
        private async Task RemoveDownloadedEngine()
        {
            if (!OfficeEngine.CanRemove || IsBusy)
                return;

            var answer = MessageBox.Show(
                "DocMaster Pro'nun indirdiği LibreOffice kopyası silinsin mi?\n\n" +
                "Disk alanı boşalır. Microsoft Office kurulu değilse bir sonraki Office → PDF dönüştürmesinde yeniden indirmeniz istenir.",
                "DocMaster Pro", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes || EngineInstallerFactory() is not { } installer)
                return;

            try
            {
                await installer.RemoveAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLogger.LogError("LibreOffice kopyası silinemedi", ex);
                MessageBox.Show(
                    "LibreOffice kopyasının tamamı silinemedi; şu anda bir dönüştürmede kullanılıyor olabilir. Biraz sonra yeniden deneyin.",
                    "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RefreshOfficeEngine();
        }

        /// <summary>
        /// Toplu dönüştürmenin gerçek sonucunu gösterir; hepsi başarısız olduğunda "tamamlandı" demez.
        /// </summary>
        private static void ShowBatchResult(string operation, int total, int failed)
        {
            if (failed == 0)
            {
                MessageBox.Show($"{operation}: {total} dosya dönüştürüldü.", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string detail = failed == total
                ? "Dosyaların hiçbiri dönüştürülemedi."
                : $"{total - failed} dosya dönüştürüldü, {failed} dosya dönüştürülemedi.";

            MessageBox.Show(
                $"{operation}: {detail}\n\nBaşarısız dosyalar listede kırmızı görünür. " +
                "Dosyaların bozuk ya da parola korumalı olmadığını ve başka bir programda açık olmadığını kontrol edin.",
                "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ==================== Constructor ====================
        public MainViewModel()
            : this(new OfficeToPdfConverter())
        {
        }

        internal MainViewModel(OfficeToPdfConverter officeToPdf)
        {
            _officeToPdf = officeToPdf ?? throw new ArgumentNullException(nameof(officeToPdf));
            RefreshOfficeEngine();

            HomeHub.OnNavigateToOfficeRequested = () =>
            {
                SelectedAppSection = 1;
                SelectedWorkspaceIndex = 0;
            };
            HomeHub.OnNavigateToPdfStudioRequested = () => SelectedAppSection = 3;
            HomeHub.OnNavigateToDevicesRequested = () => SelectedAppSection = 2;
            HomeHub.OnNavigateToScannerStudioRequested = () => { SelectedAppSection = 2; DeviceHub.SelectedHubTabIndex = 1; };
            HomeHub.OnNavigateToPrinterStudioRequested = () => { SelectedAppSection = 2; DeviceHub.SelectedHubTabIndex = 2; };
            DeviceHub.OnSendToPdfStudioRequested = async pdfPath =>
            {
                SelectedAppSection = 3;
                await PdfStudio.OpenPdfPathAsync(pdfPath, promptForUnsavedChanges: true);
            };

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

            if (!EnsureOfficeEngine(MergeDocuments.Select(doc => doc.Extension)))
                return;

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

                SplitCommand.NotifyCanExecuteChanged();
            }
        }

        partial void OnPageRangeTextChanged(string value)
        {
            SplitRanges.Clear();
            SplitCommand.NotifyCanExecuteChanged();
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

            if (!EnsureOfficeEngine(OfficeDocuments.Select(doc => doc.Extension)))
                return;

            var folderDlg = new OpenFolderDialog { Title = "PDF'lerin kaydedileceği klasörü seçin" };
            if (folderDlg.ShowDialog() != true) return;

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "Office dosyaları dönüştürülüyor...";

            try
            {
                // Liste, dönüştürme sürerken kullanıcı tarafından değiştirilebilir; kopyası üzerinde dönülür.
                var documents = OfficeDocuments.ToList();
                int total = documents.Count;
                int current = 0;
                int failed = 0;

                foreach (var doc in documents)
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

                        await _officeToPdf.ConvertAsync(doc.FilePath, outputPath, _cts.Token);
                        doc.Status = "Done";
                    }
                    catch (OperationCanceledException)
                    {
                        doc.Status = "Ready";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        doc.Status = "Error";
                        FileLogger.LogError($"OfficeToPdf ({doc.FileName})", ex);
                    }
                }

                StatusMessage = failed == 0 ? "Tamamlandı" : $"Tamamlandı — {failed} dosya dönüştürülemedi";
                ShowBatchResult("Office → PDF", total, failed);
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

            RefreshOfficeEngine();
            if (!_officeToPdf.IsWordInstalled)
            {
                MessageBox.Show(
                    "PDF'den Word'e dönüştürme için Microsoft Word 2013 veya daha yeni bir sürüm gerekir; bu bilgisayarda Word bulunamadı.\n\n" +
                    "Dönüştürücü Word'ün kendisidir. LibreOffice ile sonuç düşük kaliteli olduğu için bu dönüşüm onunla yapılmaz.",
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
                var documents = PdfToWordDocuments.ToList();
                int total = documents.Count;
                int current = 0;
                int failed = 0;

                foreach (var doc in documents)
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
                        failed++;
                        doc.Status = "Error";
                        FileLogger.LogError($"PdfToWord ({doc.FileName})", ex);
                    }
                }

                StatusMessage = failed == 0 ? "Tamamlandı" : $"Tamamlandı — {failed} dosya dönüştürülemedi";
                ShowBatchResult("PDF → Word", total, failed);
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

                var pages = await Task.Run(() => LoadPdfPageMetadata(pdfPath, canLoadPreview: true, previewToken), previewToken);

                foreach (var page in pages)
                    PdfPages.Add(page);

                if (selectedPageIndex is >= 0 && selectedPageIndex < PdfPages.Count)
                    SelectedPage = PdfPages[selectedPageIndex.Value];
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
                        : "Önizleme yüklenemedi."
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

        private static BitmapSource? RenderPdfPagePreview(string pdfPath, int pageIndex, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                PdfiumNativeLoader.EnsureLoaded();

                using var pdfDoc = PdfiumDoc.Load(pdfPath);
                if (pageIndex < 0 || pageIndex >= pdfDoc.PageCount)
                    return null;

                cancellationToken.ThrowIfCancellationRequested();

                var pageSize = pdfDoc.PageSizes[pageIndex] is System.Drawing.SizeF sz ? sz : new System.Drawing.SizeF(595, 842);

                double scale = (double)PdfPreviewDensityDpi / 72.0;
                int width = Math.Max(1, (int)Math.Round(pageSize.Width * scale));
                int height = Math.Max(1, (int)Math.Round(pageSize.Height * scale));

                if (width > PdfPreviewMaxWidth)
                {
                    double aspect = (double)height / width;
                    width = PdfPreviewMaxWidth;
                    height = Math.Max(1, (int)Math.Round(width * aspect));
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var image = pdfDoc.Render(
                    pageIndex,
                    width,
                    height,
                    PdfPreviewDensityDpi,
                    PdfPreviewDensityDpi,
                    PdfRenderFlags.Annotations);

                using var ms = new MemoryStream();
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                cancellationToken.ThrowIfCancellationRequested();

                var bitmap = new BitmapImage();
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

        private static bool IsGhostscriptAvailable()
        {
            return true;
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
                    PdfService.SaveInPlace(doc, EditPdfPath);
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
                    PdfService.SaveInPlace(doc, EditPdfPath);
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
                    PdfService.SaveInPlace(doc, EditPdfPath);
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
                    PdfService.SaveInPlace(doc, EditPdfPath);
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

                    // Aynı dosyanın üzerine kopyalamak "dosya kullanımda" hatası verir; değişiklikler zaten diskte.
                    if (!string.Equals(Path.GetFullPath(EditPdfPath), Path.GetFullPath(saveDlg.FileName), StringComparison.OrdinalIgnoreCase))
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

            // Microsoft Office kuruluysa o, değilse LibreOffice kullanılır.
            if (OfficeToPdfConverter.AppFor(doc.Extension) != null)
            {
                string outputPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.pdf");
                await _officeToPdf.ConvertAsync(doc.FilePath, outputPath, cancellationToken);
                return outputPath;
            }

            return null;
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
