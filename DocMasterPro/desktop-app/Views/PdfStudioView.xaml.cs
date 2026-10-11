using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DocConverter.Helpers;
using DocConverter.Models;
using DocConverter.ViewModels;

namespace DocConverter.Views;

public partial class PdfStudioView : UserControl
{
    private const double WheelZoomStep = 1.1;

    private PdfStudioViewModel? subscribedViewModel;
    private int viewportRestoreGeneration;

    // Sürükleyerek kaydırma (el aracı)
    private bool isPanning;
    private bool panMoved;
    private Point panStart;
    private double panStartHorizontal;
    private double panStartVertical;

    public PdfStudioView()
    {
        InitializeComponent();
        DataContextChanged += PdfStudioView_DataContextChanged;
        Loaded += (_, _) =>
        {
            SubscribeToViewModel(DataContext as PdfStudioViewModel);
            PushViewMetrics();
        };
        Unloaded += (_, _) => SubscribeToViewModel(null);
        PreviewKeyDown += PdfStudioView_PreviewKeyDown;
    }

    private PdfStudioViewModel? ViewModel => DataContext as PdfStudioViewModel;

    // ==================== Sürükle-bırak ====================

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

        if (ViewModel is not { } vm)
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

    // ==================== Ekran ölçeği ve görünür alan ====================

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (ViewModel is { } vm)
            vm.RenderScale = newDpi.DpiScaleX;
    }

    private void DocumentPagesList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PushViewMetrics();
    }

    /// <summary>
    /// Görünüm modeline ekranın ölçek oranını ve belge alanının boyutunu bildirir; sayfalar buna göre
    /// fiziksel piksel çözünürlüğünde çizilir ve sığdırma modları buna göre hesaplanır.
    /// </summary>
    private void PushViewMetrics()
    {
        if (ViewModel is not { } vm)
            return;

        vm.RenderScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        if (DocumentPagesList.ActualWidth > 0 && DocumentPagesList.ActualHeight > 0)
            vm.SetViewportSize(DocumentPagesList.ActualWidth, DocumentPagesList.ActualHeight);
    }

    // ==================== Klavye ====================

    private void PdfStudioView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        ModifierKeys modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool typing = Keyboard.FocusedElement is TextBoxBase or ComboBox;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O:
                    e.Handled = Run(vm.OpenPdfCommand);
                    return;
                case Key.S:
                    e.Handled = Run(shift ? vm.SaveAsCommand : vm.SaveCommand);
                    return;
                case Key.P:
                    e.Handled = Run(vm.PrintCommand);
                    return;
                case Key.F:
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                    return;
                case Key.OemPlus or Key.Add:
                    e.Handled = Run(vm.ZoomInCommand);
                    return;
                case Key.OemMinus or Key.Subtract:
                    e.Handled = Run(vm.ZoomOutCommand);
                    return;
                case Key.D0 or Key.NumPad0:
                    e.Handled = Run(vm.FitPageCommand);
                    return;
                case Key.D1 or Key.NumPad1:
                    e.Handled = Run(vm.ActualSizeCommand);
                    return;
                case Key.D2 or Key.NumPad2:
                    e.Handled = Run(vm.FitWidthCommand);
                    return;
                case Key.Home when !typing:
                    e.Handled = Run(vm.FirstPageCommand);
                    return;
                case Key.End when !typing:
                    e.Handled = Run(vm.LastPageCommand);
                    return;
            }

            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = Run(shift ? vm.PreviousSearchResultCommand : vm.NextSearchResultCommand);
            return;
        }

        if (typing)
            return;

        switch (e.Key)
        {
            case Key.Left:
                e.Handled = Run(vm.PreviousPageCommand);
                break;
            case Key.Right:
                e.Handled = Run(vm.NextPageCommand);
                break;
            case Key.Delete:
                e.Handled = Run(vm.DeleteSelectedAnnotationCommand);
                break;
            case Key.Escape:
                vm.SelectedTool = PdfStudioTool.Select;
                vm.SelectAnnotation(null);
                e.Handled = true;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space:
                e.Handled = ScrollByKey(e.Key, shift);
                break;
        }
    }

    private static bool Run(ICommand command)
    {
        if (!command.CanExecute(null))
            return false;

        command.Execute(null);
        return true;
    }

    private bool ScrollByKey(Key key, bool shift)
    {
        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return false;

        double page = Math.Max(40, scrollViewer.ViewportHeight - 40);
        double offset = scrollViewer.VerticalOffset;

        double target = key switch
        {
            Key.Up => offset - 60,
            Key.Down => offset + 60,
            Key.PageUp => offset - page,
            Key.PageDown => offset + page,
            Key.Space => shift ? offset - page : offset + page,
            Key.Home => 0,
            Key.End => scrollViewer.ScrollableHeight,
            _ => offset
        };

        scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(target, scrollViewer.ScrollableHeight));
        return true;
    }

    private void PageNumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } vm)
            return;

        Run(vm.GoToPageCommand);
        DocumentPagesList.Focus();
        e.Handled = true;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } vm)
            return;

        Run(vm.SearchCommand);
        e.Handled = true;
    }

    private void TextBox_SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.Dispatcher.BeginInvoke(new Action(box.SelectAll), DispatcherPriority.Input);
    }

    // ==================== Yakınlaştırma kutusu ====================

    private void ZoomBox_DropDownClosed(object? sender, EventArgs e)
    {
        CommitZoomText();
        DocumentPagesList.Focus();
    }

    private void ZoomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitZoomText();
        DocumentPagesList.Focus();
        e.Handled = true;
    }

    private void ZoomBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ZoomBox.IsKeyboardFocusWithin)
            CommitZoomText();
    }

    private void CommitZoomText()
    {
        var binding = ZoomBox.GetBindingExpression(ComboBox.TextProperty);
        binding?.UpdateSource();
        // Geçersiz ya da sınır dışı bir değer yazıldıysa kutu gerçek değere döner.
        binding?.UpdateTarget();
    }

    // ==================== Fare: yakınlaştırma ve sürükleyerek kaydırma ====================

    private void DocumentPagesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || ViewModel is not { } vm)
            return;

        vm.ZoomBy(e.Delta > 0 ? WheelZoomStep : 1 / WheelZoomStep);
        e.Handled = true;
    }

    private void DocumentPagesList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Kısayollar belge alanına tıklandıktan sonra çalışsın.
        if (!IsWithinScrollBar(e.OriginalSource as DependencyObject))
            DocumentPagesList.Focus();

        if (e.ChangedButton == MouseButton.Middle)
        {
            BeginPan(e);
            e.Handled = true;
        }
    }

    private void DocumentPagesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!isPanning)
            return;

        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return;

        Point current = e.GetPosition(DocumentPagesList);
        Vector delta = current - panStart;
        if (!panMoved && Math.Abs(delta.X) < 3 && Math.Abs(delta.Y) < 3)
            return;

        panMoved = true;
        scrollViewer.ScrollToHorizontalOffset(ClampScrollOffset(panStartHorizontal - delta.X, scrollViewer.ScrollableWidth));
        scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(panStartVertical - delta.Y, scrollViewer.ScrollableHeight));
    }

    private void DocumentPagesList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (isPanning)
            EndPan();
    }

    private void DocumentPagesList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        isPanning = false;
        DocumentPagesList.Cursor = null;
    }

    private void BeginPan(MouseButtonEventArgs e)
    {
        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return;

        isPanning = true;
        panMoved = false;
        panStart = e.GetPosition(DocumentPagesList);
        panStartHorizontal = scrollViewer.HorizontalOffset;
        panStartVertical = scrollViewer.VerticalOffset;
        DocumentPagesList.Cursor = Cursors.ScrollAll;
        DocumentPagesList.CaptureMouse();
    }

    private void EndPan()
    {
        isPanning = false;
        DocumentPagesList.Cursor = null;
        if (DocumentPagesList.IsMouseCaptured)
            DocumentPagesList.ReleaseMouseCapture();
    }

    private static bool IsWithinScrollBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ScrollBar)
                return true;

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void PageSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        if (sender is not FrameworkElement { DataContext: PdfPageViewItem page } surface)
            return;

        if (vm.SelectedTool == PdfStudioTool.Select)
        {
            // Seç aracı: boş alana tıklamak seçimi kaldırır, sürüklemek sayfayı kaydırır.
            vm.SelectAnnotation(null);
            BeginPan(e);
            return;
        }

        Point point = e.GetPosition(surface);
        vm.AddAnnotationAt(page.PageIndex, point.X, point.Y);
    }

    private void Annotation_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not FrameworkElement { DataContext: PdfAnnotationItem annotation } element)
            return;

        if (vm.SelectedTool == PdfStudioTool.Eraser)
        {
            vm.SelectAnnotation(annotation);
            Run(vm.DeleteSelectedAnnotationCommand);
        }
        else
        {
            vm.SelectAnnotation(annotation);
        }

        e.Handled = true;
    }

    // ==================== Küçük resimler ====================

    private void ThumbnailItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PdfPageViewItem page } && ViewModel is { } vm)
            vm.RequestThumbnail(page);
    }

    private void ThumbnailItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PdfPageViewItem page } && ViewModel is { } vm)
            vm.NavigateToPage(page.PageIndex);
    }

    // ==================== Sayfa listesi ====================

    private void PdfStudioPageItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfPageViewItem page })
            return;

        if (ViewModel is not { } vm)
            return;

        int? current = UpdateCurrentPageFromViewport();
        vm.QueuePageWindowRender(current ?? page.PageIndex);
    }

    private void PdfStudioPageItem_Unloaded(object sender, RoutedEventArgs e)
    {
        UpdateCurrentPageFromViewport();
    }

    private void DocumentPagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        // Yalnızca gerçek kaydırmada tetikle; içerik boyutu değişimleri (yakınlaştırma) ayrıca ele alınır.
        if (e.VerticalChange == 0 && e.HorizontalChange == 0 && e.ViewportHeightChange == 0)
            return;

        int? pageIndex = UpdateCurrentPageFromViewport();
        if (pageIndex.HasValue)
            vm.QueuePageWindowRender(pageIndex.Value);
    }

    private int? UpdateCurrentPageFromViewport()
    {
        if (ViewModel is not { } vm)
            return null;

        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer == null)
            return null;

        if (!TryGetPrimaryVisiblePage(vm, scrollViewer, out PdfPageViewItem page, out _))
            return null;

        vm.SetCurrentPageFromView(page.PageIndex);
        return page.PageIndex;
    }

    private void PdfStudioView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SubscribeToViewModel(e.NewValue as PdfStudioViewModel);
        PushViewMetrics();
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
        switch (e.PropertyName)
        {
            case nameof(PdfStudioViewModel.Session):
                Dispatcher.BeginInvoke(new Action(ScrollToFirstPage), DispatcherPriority.Loaded);
                break;

            case nameof(PdfStudioViewModel.ZoomPercent):
                QueueViewportRestoreAfterZoom();
                break;

            case nameof(PdfStudioViewModel.CurrentPageIndex):
                Dispatcher.BeginInvoke(new Action(BringActiveThumbnailIntoView), DispatcherPriority.Background);
                break;

            case nameof(PdfStudioViewModel.IsThumbnailPaneVisible):
                Dispatcher.BeginInvoke(new Action(BringActiveThumbnailIntoView), DispatcherPriority.Loaded);
                break;
        }
    }

    private void BringActiveThumbnailIntoView()
    {
        if (ViewModel?.CurrentPageItem is { } page && ThumbnailList.IsVisible)
            ThumbnailList.ScrollIntoView(page);
    }

    private void PdfStudioViewModel_ScrollToPageRequested(object? sender, int pageIndex)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ScrollToPage(pageIndex);
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

    /// <summary>
    /// Yakınlaştırmadan önce görünür alanın ortasındaki sayfa noktasını kaydeder; yakınlaştırmadan sonra
    /// aynı nokta yine ortada kalır.
    /// </summary>
    private ViewportAnchor? CaptureViewportAnchor()
    {
        if (ViewModel is not { } vm)
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
        if (ViewModel is not { } vm || anchor.PageIndex < 0 || anchor.PageIndex >= vm.Pages.Count)
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
        if (ViewModel is not { } vm || vm.Pages.Count == 0)
            return;

        PushViewMetrics();
        DocumentPagesList.ScrollIntoView(vm.Pages[0]);
        DocumentPagesList.UpdateLayout();

        ScrollViewer? scrollViewer = GetDocumentScrollViewer();
        scrollViewer?.ScrollToVerticalOffset(0);
        CenterHorizontalViewport();
        UpdateCurrentPageFromViewport();
    }

    private void ScrollToPage(int pageIndex)
    {
        if (ViewModel is not { } vm)
            return;

        if (pageIndex < 0 || pageIndex >= vm.Pages.Count)
            return;

        PdfPageViewItem page = vm.Pages[pageIndex];
        DocumentPagesList.ScrollIntoView(page);
        DocumentPagesList.UpdateLayout();

        if (TryAlignPageInViewport(page))
            return;

        Dispatcher.BeginInvoke(new Action(() => TryAlignPageInViewport(page)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Sayfa görünür alana sığıyorsa ortalar, sığmıyorsa üst kenarını görünür alanın üstüne hizalar.
    /// </summary>
    private bool TryAlignPageInViewport(PdfPageViewItem page)
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
        double viewportAnchor = fitsInViewport ? scrollViewer.ViewportHeight / 2d : 8d;
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

        // Yalnızca oluşturulmuş (ekranda ya da önbellekte olan) kapsayıcılara bakılır;
        // yüzlerce sayfalık belgede bütün sayfaları dolaşmak her kaydırmada gereksizdir.
        var generator = DocumentPagesList.ItemContainerGenerator;
        Panel? host = FindVisualChild<VirtualizingStackPanel>(DocumentPagesList);
        if (host == null)
            return false;

        foreach (object child in host.Children)
        {
            if (child is not FrameworkElement candidateContainer || candidateContainer.ActualHeight <= 0)
                continue;

            if (generator.ItemFromContainer(candidateContainer) is not PdfPageViewItem candidate)
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
