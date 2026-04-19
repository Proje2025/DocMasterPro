using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DocConverter.Helpers;
using DocConverter.ViewModels;

namespace DocConverter.Views;

public partial class PdfStudioView : UserControl
{
    private const double ScrollEdgeTolerance = 2d;
    private ScrollTarget scrollTargetAfterRender = ScrollTarget.None;
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

    private void DocumentScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        if (DataContext is not PdfStudioViewModel vm)
            return;

        SubscribeToViewModel(vm);

        if (e.Delta < 0 && IsAtBottom(scrollViewer) && vm.TryGoToNextPageFromScroll())
        {
            e.Handled = true;
            scrollTargetAfterRender = ScrollTarget.Top;
        }
        else if (e.Delta > 0 && IsAtTop(scrollViewer) && vm.TryGoToPreviousPageFromScroll())
        {
            e.Handled = true;
            scrollTargetAfterRender = ScrollTarget.Bottom;
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
            subscribedViewModel.PropertyChanged -= PdfStudioViewModel_PropertyChanged;

        subscribedViewModel = viewModel;
        if (subscribedViewModel != null)
            subscribedViewModel.PropertyChanged += PdfStudioViewModel_PropertyChanged;
    }

    private void PdfStudioViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PdfStudioViewModel.CurrentPageImage))
            return;

        if (scrollTargetAfterRender == ScrollTarget.None)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (scrollTargetAfterRender == ScrollTarget.Bottom)
                DocumentScrollViewer.ScrollToVerticalOffset(DocumentScrollViewer.ScrollableHeight);
            else
                DocumentScrollViewer.ScrollToTop();

            scrollTargetAfterRender = ScrollTarget.None;
        }));
    }

    private static bool IsAtTop(ScrollViewer scrollViewer) =>
        scrollViewer.VerticalOffset <= ScrollEdgeTolerance;

    private static bool IsAtBottom(ScrollViewer scrollViewer) =>
        scrollViewer.ScrollableHeight <= ScrollEdgeTolerance ||
        scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - ScrollEdgeTolerance;

    private enum ScrollTarget
    {
        None,
        Top,
        Bottom
    }
}
