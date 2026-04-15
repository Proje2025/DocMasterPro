using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        // ==================== Ortak Özellikler ====================
        [ObservableProperty]
        private int progress;

        [ObservableProperty]
        private bool isBusy;

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

        // ==================== Constructor ====================
        public MainViewModel()
        {
            MergeDocuments.CollectionChanged += (_, _) => MergeCommand.NotifyCanExecuteChanged();
            ImageDocuments.CollectionChanged += (_, _) => ConvertImagesToPdfCommand.NotifyCanExecuteChanged();
            OfficeDocuments.CollectionChanged += (_, _) => ConvertOfficeToPdfCommand.NotifyCanExecuteChanged();
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

                MessageBox.Show("Birleştirme tamamlandı!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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

            // Tüm koleksiyonlardan dene kaldır
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

            // Buton durumunu güncelle
            SplitCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanSplit))]
        public async Task Split()
        {
            if (string.IsNullOrWhiteSpace(SplitPdfPath)) return;

            var ranges = PathValidator.ValidatePageRanges(PageRangeText, 9999);
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
                await _pdf.SplitPdfAsync(SplitPdfPath, SplitOutputFolder, ranges);
                Progress = 100;
                MessageBox.Show("PDF bölme tamamlandı!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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
                
                // Sadece görüntü dosyalarını kabul et
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
                    Progress = (current * 100) / total;

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

                // Hiç PDF oluşturulamadıysa hata ver
                if (pdfPaths.Count == 0)
                {
                    MessageBox.Show("Hiçbir görüntü PDF'e dönüştürülemedi.\nDetaylar için log dosyasını kontrol edin.", 
                        "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Bazı dosyalar başarısız olduysa uyar
                if (failedItems.Count > 0)
                {
                    var result = MessageBox.Show(
                        $"Bazı dosyalar dönüştürülemedi: {string.Join(", ", failedItems)}\n\nDevam edilsin mi?", 
                        "Uyarı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                        return;
                }

                var reporter = new Progress<int>(v => Progress = v);
                await _pdf.MergePdfsAsync(pdfPaths, saveDlg.FileName, reporter);

                MessageBox.Show("PDF oluşturuldu!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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

                // Buton durumunu güncelle
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
                await _conv.ConvertPdfToImagesAsync(ExportPdfPath, ExportOutputFolder, SelectedImageFormat.ToLower());
                Progress = 100;
                MessageBox.Show("Dönüştürme tamamlandı!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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

                MessageBox.Show("Dönüştürme tamamlandı!", "DocMaster Pro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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
                // Ghostscript kontrolü
                if (!IsGhostscriptAvailable())
                {
                    // Ghostscript yoksa sadece metin bilgisi göster
                    LoadPdfPagesBasic(pdfPath);
                    MessageBox.Show(
                        "PDF sayfa önizlemeleri için Ghostscript önerilir.\n" +
                        "Sayfa bilgileri gösteriliyor.\n\n" +
                        "Önizleme için: https://ghostscript.com/releases/gsdnld.html",
                        "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Magick.NET ile thumbnail üret
                LoadPdfPagesWithThumbnails(pdfPath);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("LoadPdfPages", ex);
                // Hata durumunda basic mod ile dene
                try
                {
                    LoadPdfPagesBasic(pdfPath);
                }
                catch { }
                MessageBox.Show($"PDF açılırken hata oluştu: {ex.Message}\n\nSayfa bilgileri gösteriliyor.",
                    "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadPdfPagesBasic(string pdfPath)
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);

            if (doc.PageCount == 0)
            {
                MessageBox.Show("PDF dosyasında sayfa bulunamadı.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

            if (doc.PageCount == 0)
            {
                MessageBox.Show("PDF dosyasında sayfa bulunamadı.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Magick.NET ile PDF'i oku
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
                // Önce görüntüyü PNG formatında bir MemoryStream'e yaz
                using var ms = new MemoryStream();
                image.Write(ms, MagickFormat.Png);
                ms.Position = 0;

                // MemoryStream'den yeni bir MagickImage oluştur
                using var thumb = new MagickImage(ms);

                // Yüksek kaliteli boyutlandırma için Resize kullan (Sample yerine)
                thumb.FilterType = FilterType.Lanczos;
                thumb.Resize(maxWidth, 0); // 0 = otomatik yükseklik (aspect korur)
                thumb.Format = MagickFormat.Png;

                // PNG byte dizisine dönüştür
                var bytes = thumb.ToByteArray();

                // BitmapSource oluştur
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
                    pdfPage.Rotate = (pdfPage.Rotate + 90) % 360;
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

            try
            {
                // PdfPages listesindeki öğeleri yeniden düzenle
                var pagesList = PdfPages.ToList();
                (pagesList[index - 1], pagesList[index]) = (pagesList[index], pagesList[index - 1]);

                // Yeni PdfDocument oluştur ve sayfaları sırayla ekle
                using var newDoc = new PdfDocument();
                using var sourceDoc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Import);

                foreach (var pageInfo in pagesList)
                {
                    newDoc.AddPage(sourceDoc.Pages[pageInfo.PageIndex]);
                }

                // Mevcut dosyayı değiştir
                newDoc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);

                // Seçimi koru (yeni pozisyonda)
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

            try
            {
                // PdfPages listesindeki öğeleri yeniden düzenle
                var pagesList = PdfPages.ToList();
                (pagesList[index + 1], pagesList[index]) = (pagesList[index], pagesList[index + 1]);

                // Yeni PdfDocument oluştur ve sayfaları sırayla ekle
                using var newDoc = new PdfDocument();
                using var sourceDoc = PdfReader.Open(EditPdfPath, PdfDocumentOpenMode.Import);

                foreach (var pageInfo in pagesList)
                {
                    newDoc.AddPage(sourceDoc.Pages[pageInfo.PageIndex]);
                }

                // Mevcut dosyayı değiştir
                newDoc.Save(EditPdfPath);
                LoadPdfPages(EditPdfPath);

                // Seçimi koru (yeni pozisyonda)
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
                    var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    var font = new XFont("Arial", 48);
                    var brush = new XSolidBrush(XColor.FromArgb(80, 128, 128, 128));

                    // Ortaya çapraz filigran
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
                    MessageBox.Show("PDF kaydedildi!", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    FileLogger.LogError("SaveEditedPdf", ex);
                    MessageBox.Show($"Kayıt başarısız: {ex.Message}", "DocMaster Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ==================== Yardımcı Metodlar ====================
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
            else if (extension is ".txt" or ".rtf")
                await _officeConv.ConvertTxtToPdfAsync(inputPath, outputPath);
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
