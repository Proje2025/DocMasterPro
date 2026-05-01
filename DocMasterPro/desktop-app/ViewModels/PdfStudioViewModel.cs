using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConverter.Models;
using DocConverter.Services;
using Microsoft.Win32;

namespace DocConverter.ViewModels;

public partial class PdfStudioViewModel : ObservableObject, IDisposable
{
    private readonly PdfSessionService _sessionService;
    private readonly PdfViewerService _viewerService;
    private readonly PdfTextSearchService _searchService;
    private readonly PdfAnnotationService _annotationService;
    private readonly SemaphoreSlim _renderGate = new(2, 2);
    private CancellationTokenSource? _renderCts;
    private int _renderGeneration;

    private const double ScreenDpi = 96d;
    private const double PdfPointDpi = 72d;
    private const int RenderNeighborRadius = 2;
    private const int CacheRadius = 7;

    public PdfStudioViewModel()
        : this(new PdfSessionService(), new PdfViewerService(), new PdfTextSearchService(), new PdfAnnotationService())
    {
    }

    public PdfStudioViewModel(
        PdfSessionService sessionService,
        PdfViewerService viewerService,
        PdfTextSearchService searchService,
        PdfAnnotationService annotationService)
    {
        _sessionService = sessionService;
        _viewerService = viewerService;
        _searchService = searchService;
        _annotationService = annotationService;
    }

    public event EventHandler<int>? ScrollToPageRequested;

    [ObservableProperty]
    private PdfDocumentSession? session;

    [ObservableProperty]
    private int currentPageIndex;

    [ObservableProperty]
    private double zoomPercent = 100;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "PDF Studio hazır";

    [ObservableProperty]
    private string searchQuery = "";

    [ObservableProperty]
    private string jumpToPageText = "";

    [ObservableProperty]
    private PdfSearchResult? selectedSearchResult;

    [ObservableProperty]
    private PdfAnnotationItem? selectedAnnotation;

    [ObservableProperty]
    private PdfStudioTool selectedTool = PdfStudioTool.Select;

    [ObservableProperty]
    private string annotationText = "Yeni metin";

    [ObservableProperty]
    private string annotationColor = "#2563EB";

    [ObservableProperty]
    private double annotationFontSize = 16;

    public ObservableCollection<PdfPageViewItem> Pages { get; } = new();

    public ObservableCollection<PdfAnnotationItem> Annotations { get; } = new();

    public ObservableCollection<PdfSearchResult> SearchResults { get; } = new();

    public string CurrentFileName => Session?.FileName ?? "PDF açılmadı";

    public string PageLabel => Session == null ? "0 / 0" : $"{CurrentPageIndex + 1} / {Session.PageCount}";

    public string DirtyLabel => Session?.IsDirty == true ? "Kaydedilmemiş değişiklikler" : "Temiz";

    public bool HasSession => Session != null;

    public PdfPageViewItem? CurrentPageItem =>
        CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count ? Pages[CurrentPageIndex] : null;

    partial void OnSessionChanged(PdfDocumentSession? value)
    {
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(DirtyLabel));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(CurrentPageItem));
        NotifyCommandStates();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        foreach (var page in Pages)
            page.IsActive = page.PageIndex == value;

        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(CurrentPageItem));
        NotifyCommandStates();
    }

    partial void OnZoomPercentChanged(double value)
    {
        UpdatePageMetrics();
        ClearRenderedPages("Yakınlaştırma değişti");
        NotifyCommandStates();
        _ = EnsurePageWindowRenderedAsync(CurrentPageIndex);
    }

    partial void OnSelectedSearchResultChanged(PdfSearchResult? value)
    {
        foreach (var result in SearchResults)
            result.IsActive = result == value;

        if (value != null)
            GoToPage(value.PageIndex, requestScroll: true);
    }

    partial void OnSelectedAnnotationChanged(PdfAnnotationItem? value)
    {
        foreach (var item in Annotations)
            item.IsSelected = item == value;

        if (value != null)
        {
            AnnotationText = value.Text;
            AnnotationColor = value.Color;
            AnnotationFontSize = value.FontSize;
        }

        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        ApplyAnnotationPropertiesCommand.NotifyCanExecuteChanged();
    }

    partial void OnAnnotationTextChanged(string value)
    {
        ApplyAnnotationPropertiesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenPdf()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF Dosyası|*.pdf",
            Title = "PDF aç"
        };

        if (dialog.ShowDialog() != true)
            return;

        await OpenPdfPathAsync(dialog.FileName, promptForUnsavedChanges: true);
    }

    public async Task<bool> OpenPdfPathAsync(string path, bool promptForUnsavedChanges = false)
    {
        if (promptForUnsavedChanges && !ConfirmReplaceOpenSession())
            return false;

        IsBusy = true;
        StatusMessage = "PDF güvenli çalışma oturumu olarak açılıyor...";

        try
        {
            string sourcePath = Path.GetFullPath(path);
            var previousSession = Session;
            var newSession = await _sessionService.OpenPdfSessionAsync(sourcePath);

            ResetRenderWork();
            Session = newSession;
            Pages.Clear();
            Annotations.Clear();
            SearchResults.Clear();
            SelectedAnnotation = null;
            SelectedSearchResult = null;
            BuildPageList(newSession.WorkingPath);
            CurrentPageIndex = 0;
            _sessionService.CleanupSession(previousSession);

            StatusMessage = $"{newSession.PageCount} sayfa bulundu. Görünür sayfalar arka planda hazırlanıyor.";
            await EnsurePageWindowRenderedAsync(0);
            StatusMessage = "PDF açıldı. Kaynak dosya çalışma kopyası üzerinden korunuyor.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "PDF açılamadı";
            MessageBox.Show(ex.Message, "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (Session == null)
            return;

        await SaveToAsync(Session.OutputPath);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAs()
    {
        if (Session == null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "PDF Dosyası|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(Session.OriginalPath) + "_studio.pdf",
            InitialDirectory = Path.GetDirectoryName(Session.OriginalPath)
        };

        if (dialog.ShowDialog() != true)
            return;

        Session.OutputPath = dialog.FileName;
        await SaveToAsync(dialog.FileName);
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private async Task Print()
    {
        if (Session == null)
            return;

        IsBusy = true;
        StatusMessage = "Yazdırma dosyası hazırlanıyor...";

        try
        {
            string printPath = await BuildOutputPdfAsync();
            _viewerService.Print(printPath);
            StatusMessage = "Yazdırma tamamlandı";
        }
        catch (Exception ex)
        {
            StatusMessage = "Yazdırma başarısız";
            MessageBox.Show(ex.Message, "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage()
    {
        GoToPage(CurrentPageIndex - 1, requestScroll: true);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        GoToPage(CurrentPageIndex + 1, requestScroll: true);
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void ZoomIn()
    {
        ZoomPercent = Math.Min(300, ZoomPercent + 10);
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void ZoomOut()
    {
        ZoomPercent = Math.Max(40, ZoomPercent - 10);
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void FitWidth()
    {
        ZoomPercent = 100;
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void GoToPage()
    {
        if (!int.TryParse(JumpToPageText, out int pageNumber))
        {
            StatusMessage = "Geçerli bir sayfa numarası girin";
            return;
        }

        GoToPage(pageNumber - 1, requestScroll: true);
    }

    [RelayCommand]
    private void SelectTool(string? toolName)
    {
        if (Enum.TryParse(toolName, ignoreCase: true, out PdfStudioTool tool))
            SelectedTool = tool;
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private async Task Search()
    {
        if (Session == null)
            return;

        SearchResults.Clear();
        foreach (var page in Pages)
            page.SearchResults.Clear();
        SelectedSearchResult = null;

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            StatusMessage = "Arama metni girin";
            return;
        }

        IsBusy = true;
        StatusMessage = "PDF metin katmanı aranıyor...";

        try
        {
            var results = await _searchService.SearchAsync(Session.WorkingPath, SearchQuery);
            foreach (var result in results)
            {
                UpdateViewBounds(result);
                SearchResults.Add(result);
                if (result.PageIndex >= 0 && result.PageIndex < Pages.Count)
                    Pages[result.PageIndex].SearchResults.Add(result);
            }

            if (SearchResults.Count == 0)
            {
                StatusMessage = "Sonuç yok. PDF taranmış görüntü olabilir veya metin katmanı bulunmayabilir.";
                return;
            }

            SelectedSearchResult = SearchResults[0];
            StatusMessage = $"{SearchResults.Count} sonuç bulundu";
        }
        catch (Exception ex)
        {
            StatusMessage = "Arama başarısız";
            MessageBox.Show(ex.Message, "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSearchResults))]
    private void NextSearchResult()
    {
        int next = SelectedSearchResult == null ? 0 : (SelectedSearchResult.ResultIndex + 1) % SearchResults.Count;
        SelectedSearchResult = SearchResults[next];
    }

    [RelayCommand(CanExecute = nameof(HasSearchResults))]
    private void PreviousSearchResult()
    {
        int previous = SelectedSearchResult == null
            ? 0
            : (SelectedSearchResult.ResultIndex - 1 + SearchResults.Count) % SearchResults.Count;
        SelectedSearchResult = SearchResults[previous];
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedAnnotation))]
    private void DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation == null)
            return;

        RemoveAnnotationFromPage(SelectedAnnotation);
        Annotations.Remove(SelectedAnnotation);
        SelectedAnnotation = null;
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanApplyAnnotationProperties))]
    private void ApplyAnnotationProperties()
    {
        if (SelectedAnnotation == null)
            return;

        SelectedAnnotation.Text = AnnotationText;
        SelectedAnnotation.Color = AnnotationColor;
        SelectedAnnotation.FontSize = AnnotationFontSize;
        UpdateViewBounds(SelectedAnnotation);
        MarkDirty();
    }

    public void AddAnnotationAt(int pageIndex, double viewX, double viewY)
    {
        if (Session == null || SelectedTool == PdfStudioTool.Select)
            return;

        if (pageIndex < 0 || pageIndex >= Pages.Count)
            return;

        if (SelectedTool == PdfStudioTool.Eraser)
        {
            EraseAt(pageIndex, viewX, viewY);
            return;
        }

        double scale = CurrentScale;
        var annotation = new PdfAnnotationItem
        {
            PageIndex = pageIndex,
            Type = SelectedTool switch
            {
                PdfStudioTool.Text => PdfAnnotationType.Text,
                PdfStudioTool.Highlight => PdfAnnotationType.Highlight,
                PdfStudioTool.Note => PdfAnnotationType.Note,
                PdfStudioTool.Ink => PdfAnnotationType.Ink,
                PdfStudioTool.Rectangle => PdfAnnotationType.Rectangle,
                _ => PdfAnnotationType.Text
            },
            X = viewX / scale,
            Y = viewY / scale,
            Text = AnnotationText,
            Color = SelectedTool == PdfStudioTool.Highlight ? "#F8D84A" : AnnotationColor,
            FontSize = AnnotationFontSize,
            Opacity = SelectedTool == PdfStudioTool.Highlight ? 0.45 : 0.92,
            StrokeWidth = 2
        };

        SetDefaultSize(annotation, scale);
        UpdateViewBounds(annotation);

        Annotations.Add(annotation);
        Pages[pageIndex].Annotations.Add(annotation);
        SelectedAnnotation = annotation;
        MarkDirty();
    }

    public void SelectAnnotation(PdfAnnotationItem? annotation)
    {
        SelectedAnnotation = annotation;
    }

    public void SetCurrentPageFromView(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count || pageIndex == CurrentPageIndex)
            return;

        CurrentPageIndex = pageIndex;
    }

    public async Task EnsurePageWindowRenderedAsync(int centerPageIndex)
    {
        if (Session == null || Pages.Count == 0)
            return;

        int start = Math.Max(0, centerPageIndex - RenderNeighborRadius);
        int end = Math.Min(Pages.Count - 1, centerPageIndex + RenderNeighborRadius);
        var token = _renderCts?.Token ?? CancellationToken.None;
        int generation = _renderGeneration;

        for (int index = start; index <= end; index++)
            await EnsurePageRenderedAsync(Pages[index], generation, token);

        TrimRenderedPages(centerPageIndex);
    }

    private async Task SaveToAsync(string outputPath)
    {
        if (Session == null)
            return;

        IsBusy = true;
        StatusMessage = "PDF güvenli şekilde kaydediliyor...";

        try
        {
            string outputPdf = await BuildOutputPdfAsync();
            await _sessionService.AtomicSaveAsync(outputPdf, outputPath);
            Session.OutputPath = outputPath;
            Session.IsDirty = false;
            OnPropertyChanged(nameof(DirtyLabel));
            StatusMessage = $"Kaydedildi: {outputPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Kaydetme başarısız";
            MessageBox.Show(ex.Message, "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private async Task<string> BuildOutputPdfAsync()
    {
        if (Session == null)
            throw new InvalidOperationException("Açık PDF oturumu yok.");

        if (Annotations.Count == 0)
            return Session.WorkingPath;

        string output = Path.Combine(Session.SessionFolder, $"studio-output-{Guid.NewGuid():N}.pdf");
        await _annotationService.ApplyAnnotationsAsync(Session.WorkingPath, output, Annotations);
        return output;
    }

    private void BuildPageList(string pdfPath)
    {
        var sizes = _viewerService.GetPageSizes(pdfPath);
        Pages.Clear();

        for (int index = 0; index < sizes.Count; index++)
        {
            var size = sizes[index];
            var page = new PdfPageViewItem(index, size.Width, size.Height);
            ApplyPageMetric(page);
            Pages.Add(page);
        }

        if (Pages.Count > 0)
            Pages[0].IsActive = true;
    }

    private async Task EnsurePageRenderedAsync(PdfPageViewItem page, int generation, CancellationToken token)
    {
        if (page.Image != null || page.IsRendering || generation != _renderGeneration)
            return;

        await _renderGate.WaitAsync(token);
        try
        {
            if (page.Image != null || page.IsRendering || generation != _renderGeneration)
                return;

            page.IsRendering = true;
            page.RenderStatus = "Sayfa hazırlanıyor...";

            var rendered = await _viewerService.RenderPageAsync(Session!.WorkingPath, page.PageIndex, ZoomPercent, token);
            if (token.IsCancellationRequested || generation != _renderGeneration)
                return;

            page.Image = rendered.Image;
            page.ViewWidth = rendered.Width;
            page.ViewHeight = rendered.Height;
            page.RenderStatus = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            page.RenderStatus = "Sayfa render edilemedi";
            StatusMessage = "PDF sayfası render edilemedi";
            FileLogger.LogError("PdfStudioRender", ex);
        }
        finally
        {
            page.IsRendering = false;
            _renderGate.Release();
        }
    }

    private void UpdatePageMetrics()
    {
        foreach (var page in Pages)
        {
            ApplyPageMetric(page);

            foreach (var annotation in page.Annotations)
                UpdateViewBounds(annotation);

            foreach (var result in page.SearchResults)
                UpdateViewBounds(result);
        }
    }

    private void ApplyPageMetric(PdfPageViewItem page)
    {
        double scale = CurrentScale;
        page.ViewWidth = Math.Max(1, Math.Round(page.SourceWidth * scale));
        page.ViewHeight = Math.Max(1, Math.Round(page.SourceHeight * scale));
    }

    private void ClearRenderedPages(string status)
    {
        ResetRenderWork();
        foreach (var page in Pages)
        {
            page.Image = null;
            page.IsRendering = false;
            page.RenderStatus = status;
        }
    }

    private void TrimRenderedPages(int centerPageIndex)
    {
        foreach (var page in Pages)
        {
            if (Math.Abs(page.PageIndex - centerPageIndex) <= CacheRadius)
                continue;

            if (page.Image == null || page.IsRendering)
                continue;

            page.Image = null;
            page.RenderStatus = "Sayfa beklemede";
        }
    }

    private CancellationToken ResetRenderWork()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        _renderGeneration++;
        return _renderCts.Token;
    }

    private void GoToPage(int pageIndex, bool requestScroll)
    {
        if (Session == null || Pages.Count == 0)
            return;

        int target = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        CurrentPageIndex = target;
        JumpToPageText = (target + 1).ToString(CultureInfo.InvariantCulture);
        _ = EnsurePageWindowRenderedAsync(target);

        if (requestScroll)
            ScrollToPageRequested?.Invoke(this, target);
    }

    private void UpdateViewBounds(PdfAnnotationItem annotation)
    {
        double scale = CurrentScale;
        annotation.ViewX = annotation.X * scale;
        annotation.ViewY = annotation.Y * scale;
        annotation.ViewWidth = annotation.Width * scale;
        annotation.ViewHeight = annotation.Height * scale;
    }

    private void UpdateViewBounds(PdfSearchResult result)
    {
        double scale = CurrentScale;
        result.ViewX = result.X * scale;
        result.ViewY = result.Y * scale;
        result.ViewWidth = Math.Max(12, result.Width * scale);
        result.ViewHeight = Math.Max(8, result.Height * scale);
    }

    private void SetDefaultSize(PdfAnnotationItem annotation, double scale)
    {
        switch (annotation.Type)
        {
            case PdfAnnotationType.Text:
                annotation.Width = 220 / scale;
                annotation.Height = 48 / scale;
                break;
            case PdfAnnotationType.Highlight:
                annotation.Width = 190 / scale;
                annotation.Height = 28 / scale;
                break;
            case PdfAnnotationType.Note:
                annotation.Width = 180 / scale;
                annotation.Height = 110 / scale;
                annotation.Color = "#FFE08A";
                break;
            case PdfAnnotationType.Ink:
                annotation.Width = 140 / scale;
                annotation.Height = 70 / scale;
                break;
            case PdfAnnotationType.Rectangle:
                annotation.Width = 180 / scale;
                annotation.Height = 90 / scale;
                break;
        }
    }

    private void EraseAt(int pageIndex, double viewX, double viewY)
    {
        var hit = Pages[pageIndex].Annotations.LastOrDefault(a =>
            viewX >= a.ViewX &&
            viewX <= a.ViewX + a.ViewWidth &&
            viewY >= a.ViewY &&
            viewY <= a.ViewY + a.ViewHeight);

        if (hit == null)
            return;

        RemoveAnnotationFromPage(hit);
        Annotations.Remove(hit);
        SelectedAnnotation = null;
        MarkDirty();
    }

    private void RemoveAnnotationFromPage(PdfAnnotationItem annotation)
    {
        if (annotation.PageIndex >= 0 && annotation.PageIndex < Pages.Count)
            Pages[annotation.PageIndex].Annotations.Remove(annotation);
    }

    private void MarkDirty()
    {
        if (Session == null)
            return;

        Session.IsDirty = true;
        OnPropertyChanged(nameof(DirtyLabel));
        NotifyCommandStates();
    }

    private bool ConfirmReplaceOpenSession()
    {
        if (Session?.IsDirty != true)
            return true;

        var result = MessageBox.Show(
            "Açık PDF'de kaydedilmemiş değişiklikler var. Yeni PDF açılırsa bu değişiklikler atılacak. Devam edilsin mi?",
            "DocMaster Pro",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }

    private double CurrentScale => Math.Max(0.25, ZoomPercent / 100d) * ScreenDpi / PdfPointDpi;

    private bool HasOpenSession() => Session != null && !IsBusy;

    private bool CanSave() => HasOpenSession();

    private bool CanGoPrevious() => HasOpenSession() && CurrentPageIndex > 0;

    private bool CanGoNext() => HasOpenSession() && Session != null && CurrentPageIndex < Session.PageCount - 1;

    private bool HasSearchResults() => SearchResults.Count > 0;

    private bool CanDeleteSelectedAnnotation() => SelectedAnnotation != null && !IsBusy;

    private bool CanApplyAnnotationProperties() => SelectedAnnotation != null && !IsBusy;

    private void NotifyCommandStates()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        FitWidthCommand.NotifyCanExecuteChanged();
        GoToPageCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        NextSearchResultCommand.NotifyCanExecuteChanged();
        PreviousSearchResultCommand.NotifyCanExecuteChanged();
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        ApplyAnnotationPropertiesCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderGate.Dispose();
        _sessionService.CleanupSession(Session);
        GC.SuppressFinalize(this);
    }
}
