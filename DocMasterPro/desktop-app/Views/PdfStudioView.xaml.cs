using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.ViewModels;

namespace DocConverter.Views;

public partial class PdfStudioView : UserControl
{
    private PdfStudioViewModel? subscribedViewModel;
    private int viewportRestoreGeneration;

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

    private void PdfStudioPageItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfPageViewItem page })
            return;

        if (DataContext is not PdfStudioViewModel vm)
            return;

        UpdateCurrentPageFromViewport();
        vm.QueuePageWindowRender(page.PageIndex);
    }

    private void PdfStudioPageItem_Unloaded(object sender, RoutedEventArgs e)
    {
        UpdateCurrentPageFromViewport();
    }

    private void DocumentPagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not PdfStudioViewModel vm)
            return;

        int? pageIndex = UpdateCurrentPageFromViewport();
        if (pageIndex.HasValue)
            vm.QueuePageWindowRender(pageIndex.Value);
    }

    private int? UpdateCurrentPageFromViewport()
    {
        if (DataContext is not PdfStudioViewModel vm)
            return null;

        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return null;

        if (!TryGetPrimaryVisiblePage(vm, scrollViewer, out PdfPageViewItem page, out _))
            return null;

        vm.SetCurrentPageFromView(page.PageIndex);
        return page.PageIndex;
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
        {
            Dispatcher.BeginInvoke(new Action(ScrollToFirstPage), DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName == nameof(PdfStudioViewModel.ZoomPercent))
            QueueViewportRestoreAfterZoom();
    }

    private void PdfStudioViewModel_ScrollToPageRequested(object? sender, int pageIndex)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ScrollToPage(pageIndex, centerVertically: true);
        }), DispatcherPriority.Loaded);
    }

    private void QueueViewportRestoreAfterZoom()
    {
        int generation = ++viewportRestoreGeneration;
        ViewportAnchor? anchor = CaptureViewportAnchor();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != viewportRestoreGeneration)
                return;

            if (anchor.HasValue)
                RestoreViewportAnchor(anchor.Value);
            else
                CenterHorizontalViewport();
        }), DispatcherPriority.Loaded);
    }

    private ViewportAnchor? CaptureViewportAnchor()
    {
        if (DataContext is not PdfStudioViewModel vm)
            return null;

        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return null;

        if (!TryGetPrimaryVisiblePage(vm, scrollViewer, out PdfPageViewItem page, out FrameworkElement container))
            return null;

        if (!TryGetElementPoint(container, scrollViewer, out Point point))
            return null;

        double relativeY = (scrollViewer.ViewportHeight / 2d - point.Y) / Math.Max(1d, container.ActualHeight);
        return new ViewportAnchor(page.PageIndex, Math.Clamp(relativeY, 0d, 1d));
    }

    private void RestoreViewportAnchor(ViewportAnchor anchor)
    {
        if (DataContext is not PdfStudioViewModel vm || anchor.PageIndex < 0 || anchor.PageIndex >= vm.Pages.Count)
        {
            CenterHorizontalViewport();
            return;
        }

        PdfPageViewItem page = vm.Pages[anchor.PageIndex];
        DocumentPagesList.ScrollIntoView(page);
        DocumentPagesList.UpdateLayout();

        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return;

        if (TryGetPageContainer(page, out FrameworkElement container) &&
            TryGetElementPoint(container, scrollViewer, out Point point))
        {
            double targetOffset = scrollViewer.VerticalOffset
                + point.Y
                + container.ActualHeight * anchor.RelativeY
                - scrollViewer.ViewportHeight / 2d;

            scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(targetOffset, scrollViewer.ScrollableHeight));
        }

        CenterHorizontalViewport(scrollViewer);
        UpdateCurrentPageFromViewport();
    }

    private void ScrollToFirstPage()
    {
        if (DataContext is not PdfStudioViewModel vm || vm.Pages.Count == 0)
            return;

        DocumentPagesList.ScrollIntoView(vm.Pages[0]);
        DocumentPagesList.UpdateLayout();
        CenterHorizontalViewport();
        UpdateCurrentPageFromViewport();
    }

    private void ScrollToPage(int pageIndex, bool centerVertically)
    {
        if (DataContext is not PdfStudioViewModel vm)
            return;

        if (pageIndex < 0 || pageIndex >= vm.Pages.Count)
            return;

        PdfPageViewItem page = vm.Pages[pageIndex];
        DocumentPagesList.ScrollIntoView(page);
        DocumentPagesList.UpdateLayout();

        if (!centerVertically || TryCenterPageInViewport(page))
            return;

        Dispatcher.BeginInvoke(new Action(() => TryCenterPageInViewport(page)), DispatcherPriority.Loaded);
    }

    private bool TryCenterPageInViewport(PdfPageViewItem page)
    {
        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return false;

        if (!TryGetPageContainer(page, out FrameworkElement container) ||
            !TryGetElementPoint(container, scrollViewer, out Point point))
        {
            return false;
        }

        bool fitsInViewport = container.ActualHeight <= scrollViewer.ViewportHeight;
        double pageAnchor = fitsInViewport ? container.ActualHeight / 2d : 0d;
        double viewportAnchor = fitsInViewport ? scrollViewer.ViewportHeight / 2d : 0d;
        double targetOffset = scrollViewer.VerticalOffset + point.Y + pageAnchor - viewportAnchor;

        scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(targetOffset, scrollViewer.ScrollableHeight));
        CenterHorizontalViewport(scrollViewer);
        UpdateCurrentPageFromViewport();
        return true;
    }

    private bool TryGetPrimaryVisiblePage(
        PdfStudioViewModel vm,
        ScrollViewer scrollViewer,
        out PdfPageViewItem page,
        out FrameworkElement container)
    {
        page = null!;
        container = null!;

        double viewportCenter = scrollViewer.ViewportHeight / 2d;
        double bestScore = double.NegativeInfinity;
        bool found = false;

        foreach (var candidate in vm.Pages)
        {
            if (!TryGetPageContainer(candidate, out FrameworkElement candidateContainer))
                continue;

            if (!TryGetElementPoint(candidateContainer, scrollViewer, out Point point))
                continue;

            double itemHeight = Math.Max(1d, candidateContainer.ActualHeight);
            double visibleTop = Math.Max(0d, point.Y);
            double visibleBottom = Math.Min(scrollViewer.ViewportHeight, point.Y + itemHeight);
            double visibleHeight = visibleBottom - visibleTop;

            if (visibleHeight <= 0)
                continue;

            bool containsCenter = point.Y <= viewportCenter && point.Y + itemHeight >= viewportCenter;
            double itemCenter = point.Y + itemHeight / 2d;
            double centerDistance = Math.Abs(itemCenter - viewportCenter);
            double score = (containsCenter ? 1_000_000d : 0d) + visibleHeight - centerDistance / 1000d;

            if (score <= bestScore)
                continue;

            bestScore = score;
            found = true;
            page = candidate;
            container = candidateContainer;
        }

        return found;
    }

    private bool TryGetPageContainer(PdfPageViewItem page, out FrameworkElement container)
    {
        if (DocumentPagesList.ItemContainerGenerator.ContainerFromItem(page) is FrameworkElement result &&
            result.ActualHeight > 0)
        {
            container = result;
            return true;
        }

        container = null!;
        return false;
    }

    private static bool TryGetElementPoint(FrameworkElement element, Visual ancestor, out Point point)
    {
        try
        {
            point = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
            return true;
        }
        catch (InvalidOperationException)
        {
            point = default;
            return false;
        }
    }

    private ScrollViewer? GetDocumentScrollViewer()
    {
        return FindVisualChild<ScrollViewer>(DocumentPagesList);
    }

    private void CenterHorizontalViewport()
    {
        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer != null)
            CenterHorizontalViewport(scrollViewer);
    }

    private static void CenterHorizontalViewport(ScrollViewer scrollViewer)
    {
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.ScrollableWidth / 2d);
    }

    private static double ClampScrollOffset(double offset, double maxOffset)
    {
        return Math.Clamp(offset, 0d, Math.Max(0d, maxOffset));
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

    private readonly record struct ViewportAnchor(int PageIndex, double RelativeY);
}
