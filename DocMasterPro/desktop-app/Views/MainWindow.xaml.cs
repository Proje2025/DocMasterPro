using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;

namespace DocConverter.Views
{
    public partial class MainWindow : Window
    {
        private Point _startPoint;
        private bool _isDragging = false;
        private string _currentListTag = "";

        public MainWindow()
        {
            InitializeComponent();
            Title = "DocMaster Pro - PDF Studio";
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                await vm.CheckForUpdatesOnStartupAsync();
        }

        public async Task OpenPdfInStudioAsync(string path, string? successStatusMessage = null)
        {
            MainTabs.SelectedIndex = 0;

            if (DataContext is not MainViewModel vm)
                return;

            if (!PathValidator.TryResolveExistingPdfPath(path, out string pdfPath))
            {
                MessageBox.Show(
                    $"PDF Studio yalnızca mevcut PDF dosyalarını açabilir:\n{path}",
                    "DocMaster Pro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            bool opened = await vm.PdfStudio.OpenPdfPathAsync(pdfPath, promptForUnsavedChanges: true);
            if (opened && !string.IsNullOrWhiteSpace(successStatusMessage))
                vm.PdfStudio.StatusMessage = successStatusMessage;
        }

        // ==================== Ortak DragEnter ====================
        private void MergeTab_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        // ==================== Dosya Bırakma (Harici Dosyalar) ====================
        private void MergeTab_Drop(object sender, DragEventArgs e)
        {
            HandleFileDrop(e, "Merge");
        }

        private void ImageTab_Drop(object sender, DragEventArgs e)
        {
            HandleFileDrop(e, "Image");
        }

        private void OfficeTab_Drop(object sender, DragEventArgs e)
        {
            HandleFileDrop(e, "Office");
        }

        private void PdfToWordTab_Drop(object sender, DragEventArgs e)
        {
            HandleFileDrop(e, "PdfToWord");
        }

        private void HandleFileDrop(DragEventArgs e, string listType)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            if (DataContext is not MainViewModel vm) return;

            foreach (var f in files)
            {
                if (!PathValidator.IsPathSafe(f)) continue;

                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (!PathValidator.IsSupportedExtension(ext)) continue;

                var fileInfo = new FileInfo(f);
                int? pageCount = null;

                if (ext == ".pdf")
                {
                    var conv = new ConverterService();
                    pageCount = conv.GetPdfPageCount(f);
                }

                var item = new DocumentItem
                {
                    FileName = Path.GetFileName(f),
                    FilePath = f,
                    Extension = ext,
                    FileSize = fileInfo.Length,
                    FileSizeFormatted = PathValidator.FormatFileSize(fileInfo.Length),
                    PageCount = pageCount
                };

                switch (listType)
                {
                    case "Merge":
                        vm.MergeDocuments.Add(item);
                        break;
                    case "Image":
                        if (PathValidator.ImageExtensions.Contains(ext))
                            vm.ImageDocuments.Add(item);
                        break;
                    case "Office":
                        if (PathValidator.OfficeExtensions.Contains(ext))
                            vm.OfficeDocuments.Add(item);
                        break;
                    case "PdfToWord":
                        if (ext == ".pdf")
                            vm.PdfToWordDocuments.Add(item);
                        break;
                }
            }
            e.Handled = true;
        }

        // ==================== Drag-Drop Sıralama ====================
        private void ListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);

            // ListView'in Tag'ini al (hangi liste olduğunu belirlemek için)
            if (sender is ListView lv)
            {
                _currentListTag = lv.Tag?.ToString() ?? "";
            }
        }

        private void ListView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not ListView lv) return;
            if (lv.SelectedItem is not DocumentItem item) return;

            var currentPosition = e.GetPosition(null);
            var diff = _startPoint - currentPosition;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
                DragDrop.DoDragDrop(lv, item, DragDropEffects.Move);
            }
        }

        private void ListView_Drop(object sender, DragEventArgs e)
        {
            HandleListViewDrop(e, "Merge");
        }

        private void ImageListView_Drop(object sender, DragEventArgs e)
        {
            HandleListViewDrop(e, "Image");
        }

        private void OfficeListView_Drop(object sender, DragEventArgs e)
        {
            HandleListViewDrop(e, "Office");
        }

        private void PdfToWordListView_Drop(object sender, DragEventArgs e)
        {
            HandleListViewDrop(e, "PdfToWord");
        }

        private void HandleListViewDrop(DragEventArgs e, string listType)
        {
            if (!_isDragging) return;
            if (e.Data.GetData(typeof(DocumentItem)) is not DocumentItem draggedItem) return;
            if (DataContext is not MainViewModel vm) return;

            // Hedef ListView'i bul
            var listView = e.OriginalSource as DependencyObject;
            while (listView != null && listView is not ListView)
            {
                listView = System.Windows.Media.VisualTreeHelper.GetParent(listView);
            }

            if (listView is not ListView lv) return;

            // Mouse pozisyonunda hangi item'in üzerinde olunduğunu bul
            var point = e.GetPosition(lv);
            var hitTest = System.Windows.Media.VisualTreeHelper.HitTest(lv, point);

            if (hitTest?.VisualHit == null) return;

            DependencyObject obj = hitTest.VisualHit;
            while (obj != null && obj is not ListViewItem)
            {
                obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
            }

            if (obj is ListViewItem targetItem && targetItem.DataContext is DocumentItem targetItemData)
            {
                // Doğru koleksiyonda mı kontrol et
                ObservableCollection<DocumentItem> collection = listType switch
                {
                    "Merge" => vm.MergeDocuments,
                    "Image" => vm.ImageDocuments,
                    "Office" => vm.OfficeDocuments,
                    "PdfToWord" => vm.PdfToWordDocuments,
                    _ => null!
                };

                if (collection == null) return;

                int oldIndex = collection.IndexOf(draggedItem);
                int newIndex = collection.IndexOf(targetItemData);

                if (oldIndex != -1 && newIndex != -1 && oldIndex != newIndex)
                {
                    collection.Move(oldIndex, newIndex);
                }
            }

            _isDragging = false;
            e.Handled = true;
        }

        private async void PdfPageItem_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: PdfPageInfo page }) return;
            if (DataContext is not MainViewModel vm) return;

            await vm.EnsurePagePreviewAsync(page);
        }
    }
}
