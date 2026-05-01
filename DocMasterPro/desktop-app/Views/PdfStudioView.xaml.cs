using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.ViewModels;

namespace DocConverter.Views;

public partial class PdfStudioView : UserControl
{
    private PdfStudioViewModel? subscribedViewModel;

    public PdfStudioView()
    {
        InitializeComponent();
        DataContextChanged += PdfStudioView_DataContextChanged;
        Unloaded += (_, _) => SubscribeToViewModel(null);
    }

    private void PdfStudio_DragEnter(object sender, DragEventArgs e)
    {
        SetPdfDropEffect(e);
    }

    private void PdfStudio_DragOver(object sender, DragEventArgs e)
    {
        SetPdfDropEffect(e);
    }

    private async void PdfStudio_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not PdfStudioViewModel vm)
            return;

        string[] files = GetDroppedFiles(e);
        string? pdfPath = files
            .Select(file => PathValidator.TryResolveExistingPdfPath(file, out string fullPath) ? fullPath : null)
            .FirstOrDefault(path => path != null);

        if (pdfPath == null)
        {
            MessageBox.Show(
                "PDF Studio yalnızca PDF dosyalarını sürükle-bırak ile açabilir.",
                "DocMaster Pro",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool opened = await vm.OpenPdfPathAsync(pdfPath, promptForUnsavedChanges: true);
        if (opened && files.Length > 1)
            vm.StatusMessage = "Birden fazla dosya bırakıldı; ilk geçerli PDF açıldı.";
    }

    private async void PdfStudioPageItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfPageViewItem page })
            return;

        if (DataContext is not PdfStudioViewModel vm)
            return;

        vm.SetCurrentPageFromView(page.PageIndex);
        await vm.EnsurePageWindowRenderedAsync(page.PageIndex);
    }

    private void PdfStudioPageItem_Unloaded(object sender, RoutedEventArgs e)
    {
        UpdateCurrentPageFromViewport();
    }

    private async void DocumentPagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not PdfStudioViewModel vm)
            return;

        int? pageIndex = UpdateCurrentPageFromViewport();
        if (pageIndex.HasValue)
            await vm.EnsurePageWindowRenderedAsync(pageIndex.Value);
    }

    private int? UpdateCurrentPageFromViewport()
    {
        if (DataContext is not PdfStudioViewModel vm)
            return null;

        ScrollViewer? scrollViewer = FindVisualChild<ScrollViewer>(DocumentPagesList);
        if (scrollViewer == null)
            return null;

        double viewportTop = 0;
        double bestDistance = double.MaxValue;
        int? bestPageIndex = null;

        foreach (var page in vm.Pages)
        {
            if (DocumentPagesList.ItemContainerGenerator.ContainerFromItem(page) is not FrameworkElement container)
                continue;

            Point point = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0));
            double distance = Math.Abs(point.Y - viewportTop);
            if (point.Y <= scrollViewer.ViewportHeight && point.Y + container.ActualHeight >= 0 && distance < bestDistance)
            {
                bestDistance = distance;
                bestPageIndex = page.PageIndex;
            }
        }

        if (bestPageIndex.HasValue)
            vm.SetCurrentPageFromView(bestPageIndex.Value);

        return bestPageIndex;
    }

    private void PageSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PdfStudioViewModel vm)
            return;

        if (sender is not FrameworkElement { DataContext: PdfPageViewItem page } surface)
            return;

        Point point = e.GetPosition(surface);
        vm.AddAnnotationAt(page.PageIndex, point.X, point.Y);
    }

    private void Annotation_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PdfStudioViewModel vm && sender is FrameworkElement { DataContext: PdfAnnotationItem annotation })
        {
            vm.SelectAnnotation(annotation);
            e.Handled = true;
        }
    }

    private static void SetPdfDropEffect(DragEventArgs e)
    {
        e.Effects = GetDroppedFiles(e).Any(IsPdfFileName)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[] GetDroppedFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return Array.Empty<string>();

        return e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
    }

    private static bool IsPdfFileName(string file)
    {
        try
        {
            return string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void PdfStudioView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SubscribeToViewModel(e.NewValue as PdfStudioViewModel);
    }

    private void SubscribeToViewModel(PdfStudioViewModel? viewModel)
    {
        if (ReferenceEquals(subscribedViewModel, viewModel))
            return;

        if (subscribedViewModel != null)
        {
            subscribedViewModel.PropertyChanged -= PdfStudioViewModel_PropertyChanged;
            subscribedViewModel.ScrollToPageRequested -= PdfStudioViewModel_ScrollToPageRequested;
        }

        subscribedViewModel = viewModel;
        if (subscribedViewModel != null)
        {
            subscribedViewModel.PropertyChanged += PdfStudioViewModel_PropertyChanged;
            subscribedViewModel.ScrollToPageRequested += PdfStudioViewModel_ScrollToPageRequested;
        }
    }

    private void PdfStudioViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfStudioViewModel.Session))
            Dispatcher.BeginInvoke(new Action(() => DocumentPagesList.ScrollIntoView(DocumentPagesList.Items.Cast<object>().FirstOrDefault())));
    }

    private void PdfStudioViewModel_ScrollToPageRequested(object? sender, int pageIndex)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is not PdfStudioViewModel vm)
                return;

            if (pageIndex < 0 || pageIndex >= vm.Pages.Count)
                return;

            DocumentPagesList.ScrollIntoView(vm.Pages[pageIndex]);
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;

            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
