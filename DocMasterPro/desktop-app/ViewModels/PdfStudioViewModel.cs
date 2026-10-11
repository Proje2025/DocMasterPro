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

public enum PdfZoomMode
{
    Custom,
    FitWidth,
    FitPage
}

public partial class PdfStudioViewModel : ObservableObject, IDisposable
{
    public const double MinZoomPercent = 10;
    public const double MaxZoomPercent = 800;

    public const string FitWidthLabel = "Genişliğe Sığdır";
    public const string FitPageLabel = "Sayfaya Sığdır";
    public const string ActualSizeLabel = "Gerçek Boyut";

    /// <summary>Yakınlaştır / uzaklaştır düğmelerinin durakları.</summary>
    private static readonly double[] ZoomStops = { 10, 25, 33, 50, 67, 75, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 600, 800 };

    private readonly PdfSessionService _sessionService;
    private readonly PdfViewerService _viewerService;
    private readonly PdfTextSearchService _searchService;
    private readonly PdfAnnotationService _annotationService;
    private readonly SemaphoreSlim _renderGate = new(2, 2);
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private PdfViewerService.PdfViewerSession? _viewerSession;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _renderCts;
    private bool _isOpeningInitialPage;
    private bool _applyingFitZoom;
    private bool _updatingZoomText;
    private int _renderGeneration;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _widestPagePoints;
    private double _tallestPagePoints;

    private const int MaxRenderNeighborRadius = 2;
    private const int MaxCacheRadius = 7;
    private const long RenderCacheBudgetBytes = 320L * 1024 * 1024;
    private const int ZoomRenderDelayMs = 90;
    private const double ThumbnailWidth = 132;

    // Sayfa alanının kenar boşlukları ve dikey kaydırma çubuğu (PdfStudioView.xaml ile eş).
    private const double ViewportHorizontalChrome = 2 * 24 + 20;
    private const double ViewportVerticalChrome = 2 * 14;

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
    private PdfZoomMode zoomMode = PdfZoomMode.FitWidth;

    [ObservableProperty]
    private string zoomText = "100%";

    /// <summary>Ekranın Windows ölçek oranı (%150 → 1.5). Görünüm tarafından bildirilir.</summary>
    [ObservableProperty]
    private double renderScale = 1;

    [ObservableProperty]
    private bool isThumbnailPaneVisible = true;

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

    public IReadOnlyList<string> ZoomPresets { get; } = new[]
    {
        FitWidthLabel, FitPageLabel, ActualSizeLabel,
        "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%"
    };

    public string CurrentFileName => Session?.FileName ?? "PDF açılmadı";

    public string PageLabel => Session == null ? "0 / 0" : $"{CurrentPageIndex + 1} / {Session.PageCount}";

    public string PageCountLabel => Session == null ? "/ 0" : $"/ {Session.PageCount}";

    public string DirtyLabel => Session?.IsDirty == true ? "Kaydedilmemiş değişiklikler" : "";

    public bool HasSession => Session != null;

    public bool HasUnsavedChanges => Session?.IsDirty == true;

    public PdfPageViewItem? CurrentPageItem =>
        CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count ? Pages[CurrentPageIndex] : null;

    /// <summary>Geçerli sayfanın fiziksel boyutu (ör. "210 × 297 mm").</summary>
    public string PageSizeLabel
    {
        get
        {
            var page = CurrentPageItem;
            if (page == null)
                return "";

            double widthMm = page.SourceWidth / 72d * 25.4;
            double heightMm = page.SourceHeight / 72d * 25.4;
            return string.Create(CultureInfo.InvariantCulture, $"{widthMm:0} × {heightMm:0} mm");
        }
    }

    partial void OnSessionChanged(PdfDocumentSession? value)
    {
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(PageCountLabel));
        OnPropertyChanged(nameof(DirtyLabel));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CurrentPageItem));
        OnPropertyChanged(nameof(PageSizeLabel));
        NotifyCommandStates();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        foreach (var page in Pages)
            page.IsActive = page.PageIndex == value;

        if (Pages.Count > 0)
            JumpToPageText = (value + 1).ToString(CultureInfo.InvariantCulture);

        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(CurrentPageItem));
        OnPropertyChanged(nameof(PageSizeLabel));
        NotifyCommandStates();
    }

    partial void OnZoomPercentChanged(double value)
    {
        if (!_applyingFitZoom)
            ZoomMode = PdfZoomMode.Custom;

        UpdateZoomText();
        UpdatePageMetrics();
        NotifyCommandStates();

        // Eski görüntüler yeni boyuta gerilerek ekranda kalır; netleri arka planda hazırlanır.
        QueuePageWindowRender(CurrentPageIndex, ZoomRenderDelayMs);
    }

    partial void OnZoomModeChanged(PdfZoomMode value)
    {
        UpdateZoomText();
        if (value != PdfZoomMode.Custom)
            ApplyFitZoom();
    }

    partial void OnZoomTextChanged(string value)
    {
        if (_updatingZoomText)
            return;

        ApplyZoomText(value);
    }

    partial void OnRenderScaleChanged(double value)
    {
        UpdatePageMetrics();
        QueuePageWindowRender(CurrentPageIndex, ZoomRenderDelayMs);
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
            AnnotationFontSize = value.FontSize;

            // Vurgu ve notun kendi sabit renkleri vardır; seçildiklerinde yeni metin/kutu için
            // kullanılan rengi ezmemeliler (aksi halde sonraki metin sarı ve okunmaz olur).
            if (value.Type is not (PdfAnnotationType.Highlight or PdfAnnotationType.Note))
                AnnotationColor = value.Color;
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
        StatusMessage = "PDF açılıyor...";

        try
        {
            string sourcePath = Path.GetFullPath(path);
            var previousSession = Session;
            var viewerSession = _viewerService.OpenSession(sourcePath);
            PdfDocumentSession newSession;
            try
            {
                newSession = await _sessionService.OpenPdfSessionAsync(sourcePath, viewerSession.PageCount);
            }
            catch
            {
                viewerSession.Dispose();
                throw;
            }

            var sessionToken = ResetSessionWork();
            DisposeViewerSession();
            _viewerSession = viewerSession;
            Session = newSession;
            Pages.Clear();
            Annotations.Clear();
            SearchResults.Clear();
            SelectedAnnotation = null;
            SelectedSearchResult = null;
            BuildPageList(viewerSession);
            CurrentPageIndex = 0;
            JumpToPageText = Pages.Count > 0 ? "1" : "";
            OnPropertyChanged(nameof(PageLabel));
            OnPropertyChanged(nameof(CurrentPageItem));
            OnPropertyChanged(nameof(PageSizeLabel));
            _sessionService.CleanupSession(previousSession);

            if (ZoomMode != PdfZoomMode.Custom)
                ApplyFitZoom();

            StatusMessage = $"{newSession.PageCount} sayfa bulundu. İlk sayfa hazırlanıyor...";
            _isOpeningInitialPage = true;
            try
            {
                await EnsureInitialPageRenderedAsync(sessionToken);
            }
            finally
            {
                _isOpeningInitialPage = false;
            }

            StartWorkingCopyPreparation(newSession, sessionToken);
            QueuePageWindowRender(0);
            StatusMessage = $"{newSession.FileName} açıldı — {newSession.PageCount} sayfa";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "PDF açılamadı";
            FileLogger.LogError("PdfStudioOpen", ex);
            if (_viewerSession == null && Session != null)
                TryReopenViewerSession(Session.IsWorkingCopyReady ? Session.WorkingPath : Session.OriginalPath);

            MessageBox.Show(
                $"PDF açılamadı:\n\n{ex.Message}\n\nDosya bozuk, parola korumalı ya da başka bir programda kilitli olabilir.",
                "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
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
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(Session.OriginalPath) + "_studio.pdf",
            InitialDirectory = Path.GetDirectoryName(Session.OriginalPath)
        };

        if (dialog.ShowDialog() != true)
            return;

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
            bool printed = _viewerService.Print(printPath);
            StatusMessage = printed ? "Belge yazıcıya gönderildi" : "Yazdırma iptal edildi";
        }
        catch (Exception ex)
        {
            StatusMessage = "Yazdırma başarısız";
            FileLogger.LogError("PdfStudioPrint", ex);
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

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void FirstPage()
    {
        GoToPage(0, requestScroll: true);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void LastPage()
    {
        GoToPage(Pages.Count - 1, requestScroll: true);
    }

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn()
    {
        ZoomPercent = NextZoomStop(ZoomPercent, up: true);
    }

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut()
    {
        ZoomPercent = NextZoomStop(ZoomPercent, up: false);
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void FitWidth()
    {
        ZoomMode = PdfZoomMode.FitWidth;
        ApplyFitZoom();
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void FitPage()
    {
        ZoomMode = PdfZoomMode.FitPage;
        ApplyFitZoom();
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void ActualSize()
    {
        ZoomPercent = 100;
    }

    [RelayCommand]
    private void ToggleThumbnailPane()
    {
        IsThumbnailPaneVisible = !IsThumbnailPaneVisible;
    }

    [RelayCommand(CanExecute = nameof(HasOpenSession))]
    private void GoToPage()
    {
        if (!int.TryParse(JumpToPageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNumber))
        {
            StatusMessage = "Geçerli bir sayfa numarası girin";
            JumpToPageText = (CurrentPageIndex + 1).ToString(CultureInfo.InvariantCulture);
            return;
        }

        GoToPage(pageNumber - 1, requestScroll: true);
    }

    /// <summary>Küçük resim panelinden bir sayfaya gider.</summary>
    public void NavigateToPage(int pageIndex)
    {
        GoToPage(pageIndex, requestScroll: true);
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
        NotifyCommandStates();

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            StatusMessage = "Arama metni girin";
            return;
        }

        IsBusy = true;
        StatusMessage = "PDF metin katmanı aranıyor...";

        try
        {
            var results = await _searchService.SearchAsync(Session.WorkingPath, SearchQuery.Trim());
            foreach (var result in results)
            {
                UpdateViewBounds(result);
                SearchResults.Add(result);
                if (result.PageIndex >= 0 && result.PageIndex < Pages.Count)
                    Pages[result.PageIndex].SearchResults.Add(result);
            }

            if (SearchResults.Count == 0)
            {
                StatusMessage = "Sonuç yok. Belge taranmış görüntü olabilir (metin katmanı yok).";
                return;
            }

            SelectedSearchResult = SearchResults[0];
            StatusMessage = $"{SearchResults.Count} sonuç bulundu";
        }
        catch (Exception ex)
        {
            StatusMessage = "Arama başarısız";
            FileLogger.LogError("PdfStudioSearch", ex);
            MessageBox.Show(ex.Message, "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSearchResults))]
    private void NextSearchResult()
    {
        int next = SelectedSearchResult == null ? 0 : (SearchResults.IndexOf(SelectedSearchResult) + 1) % SearchResults.Count;
        SelectedSearchResult = SearchResults[next];
        StatusMessage = $"Sonuç {next + 1} / {SearchResults.Count}";
    }

    [RelayCommand(CanExecute = nameof(HasSearchResults))]
    private void PreviousSearchResult()
    {
        int previous = SelectedSearchResult == null
            ? 0
            : (SearchResults.IndexOf(SelectedSearchResult) - 1 + SearchResults.Count) % SearchResults.Count;
        SelectedSearchResult = SearchResults[previous];
        StatusMessage = $"Sonuç {previous + 1} / {SearchResults.Count}";
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
        if (SelectedAnnotation.Type is not (PdfAnnotationType.Highlight or PdfAnnotationType.Note))
            SelectedAnnotation.Color = AnnotationColor;
        SelectedAnnotation.FontSize = Math.Clamp(AnnotationFontSize, 4, 200);
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
            FontSize = Math.Clamp(AnnotationFontSize, 4, 200),
            Opacity = SelectedTool == PdfStudioTool.Highlight ? 0.45 : 0.92,
            StrokeWidth = 2
        };

        SetDefaultSize(annotation);
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

    /// <summary>Görünüm, sayfa alanının boyutu değiştikçe bildirir (sığdırma modları için).</summary>
    public void SetViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return;

        if (Math.Abs(width - _viewportWidth) < 0.5 && Math.Abs(height - _viewportHeight) < 0.5)
            return;

        _viewportWidth = width;
        _viewportHeight = height;

        if (ZoomMode != PdfZoomMode.Custom)
            ApplyFitZoom();
    }

    /// <summary>
    /// Yakınlaştırmayı çarpanla değiştirir (Ctrl + fare tekerleği).
    /// </summary>
    public void ZoomBy(double factor)
    {
        if (Session == null || factor <= 0)
            return;

        ZoomPercent = Math.Clamp(Math.Round(ZoomPercent * factor), MinZoomPercent, MaxZoomPercent);
    }

    public async Task EnsurePageWindowRenderedAsync(int centerPageIndex)
    {
        if (Session == null || Pages.Count == 0)
            return;

        var token = ResetRenderWork();
        int generation = _renderGeneration;

        await RenderPageWindowAsync(centerPageIndex, generation, token, delayMs: 0);
    }

    public void QueuePageWindowRender(int centerPageIndex, int delayMs = 0)
    {
        if (_isOpeningInitialPage || Session == null || Pages.Count == 0)
            return;

        var token = ResetRenderWork();
        int generation = _renderGeneration;
        _ = RenderPageWindowAsync(centerPageIndex, generation, token, delayMs);
    }

    /// <summary>Küçük resim panelinde görünür hale gelen sayfanın küçük resmini arka planda hazırlar.</summary>
    public void RequestThumbnail(PdfPageViewItem page)
    {
        if (page == null || page.Thumbnail != null || page.IsThumbnailRequested || _viewerSession == null)
            return;

        page.IsThumbnailRequested = true;
        _ = RenderThumbnailAsync(page, _viewerSession, _sessionCts?.Token ?? CancellationToken.None);
    }

    private async Task RenderThumbnailAsync(
        PdfPageViewItem page,
        PdfViewerService.PdfViewerSession viewerSession,
        CancellationToken token)
    {
        bool entered = false;
        try
        {
            await _thumbnailGate.WaitAsync(token);
            entered = true;

            if (!ReferenceEquals(viewerSession, _viewerSession))
                return;

            int pixelWidth = (int)Math.Round(ThumbnailWidth * Math.Clamp(RenderScale, 1, 3));
            var thumbnail = await viewerSession.RenderThumbnailAsync(page.PageIndex, pixelWidth, token);

            if (!token.IsCancellationRequested && ReferenceEquals(viewerSession, _viewerSession))
                page.Thumbnail = thumbnail;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.LogError("PdfStudioThumbnail", ex);
        }
        finally
        {
            // Hazırlanamadıysa (iptal, oturum değişimi) sayfa yeniden görünür olduğunda tekrar denenir.
            if (page.Thumbnail == null)
                page.IsThumbnailRequested = false;

            if (entered)
                _thumbnailGate.Release();
        }
    }

    private async Task EnsureInitialPageRenderedAsync(CancellationToken sessionToken)
    {
        if (Pages.Count == 0)
            return;

        var token = _renderCts?.Token ?? sessionToken;
        int generation = _renderGeneration;
        await EnsurePageRenderedAsync(Pages[0], generation, token);
    }

    private async Task RenderPageWindowAsync(int centerPageIndex, int generation, CancellationToken token, int delayMs)
    {
        try
        {
            // Art arda gelen yakınlaştırma adımlarında (Ctrl + tekerlek) yalnızca son değer çizilir.
            if (delayMs > 0)
                await Task.Delay(delayMs, token);

            if (Pages.Count == 0)
                return;

            int target = Math.Clamp(centerPageIndex, 0, Pages.Count - 1);

            foreach (int index in GetRenderOrder(target))
            {
                if (index >= Pages.Count)
                    break;

                await EnsurePageRenderedAsync(Pages[index], generation, token);
            }

            if (generation == _renderGeneration)
                TrimRenderedPages(target);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private IEnumerable<int> GetRenderOrder(int centerPageIndex)
    {
        yield return centerPageIndex;

        int radius = Math.Min(MaxRenderNeighborRadius, CurrentCacheRadius);
        for (int offset = 1; offset <= radius; offset++)
        {
            int previous = centerPageIndex - offset;
            int next = centerPageIndex + offset;

            // Okuma yönü aşağı doğru olduğundan önce sonraki sayfa hazırlanır.
            if (next < Pages.Count)
                yield return next;

            if (previous >= 0)
                yield return previous;
        }
    }

    private async Task<bool> SaveToAsync(string outputPath)
    {
        if (Session == null)
            return false;

        IsBusy = true;
        StatusMessage = "PDF güvenli şekilde kaydediliyor...";

        try
        {
            string outputPdf = await BuildOutputPdfAsync();
            bool savingToOriginal = IsSamePath(outputPath, Session.OriginalPath);
            if (savingToOriginal)
            {
                ResetRenderWork();
                DisposeViewerSession();
            }

            await _sessionService.AtomicSaveAsync(outputPdf, outputPath);

            if (savingToOriginal)
                TryReopenViewerSession(Session.WorkingPath);

            Session.OutputPath = outputPath;
            Session.IsDirty = false;
            OnPropertyChanged(nameof(DirtyLabel));
            OnPropertyChanged(nameof(HasUnsavedChanges));
            StatusMessage = $"Kaydedildi: {outputPath}";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Kaydetme başarısız";
            FileLogger.LogError("PdfStudioSave", ex);
            if (_viewerSession == null && Session != null)
                TryReopenViewerSession(Session.IsWorkingCopyReady ? Session.WorkingPath : Session.OriginalPath);

            string reason = ex is IOException or UnauthorizedAccessException
                ? $"{ex.Message}\n\nDosya başka bir programda açık olabilir ya da klasöre yazma izniniz olmayabilir."
                : ex.Message;
            MessageBox.Show($"PDF kaydedilemedi:\n\n{reason}", "DocMaster Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
            QueuePageWindowRender(CurrentPageIndex);
        }
    }

    private async Task<string> BuildOutputPdfAsync()
    {
        if (Session == null)
            throw new InvalidOperationException("Açık PDF oturumu yok.");

        string workingPath = await EnsureWorkingCopyReadyAsync();

        if (Annotations.Count == 0)
            return workingPath;

        string output = Path.Combine(Session.SessionFolder, $"studio-output-{Guid.NewGuid():N}.pdf");
        await _annotationService.ApplyAnnotationsAsync(workingPath, output, Annotations);
        return output;
    }

    private async Task<string> EnsureWorkingCopyReadyAsync(CancellationToken cancellationToken = default)
    {
        if (Session == null)
            throw new InvalidOperationException("Açık PDF oturumu yok.");

        return await _sessionService.EnsureWorkingCopyAsync(Session, cancellationToken);
    }

    private void BuildPageList(PdfViewerService.PdfViewerSession viewerSession)
    {
        Pages.Clear();

        // Her sayfanın kendi boyutu baştan bilinir; karışık boyutlu belgelerde yerleşim zıplamaz.
        var sizes = viewerSession.GetPageSizes();
        _widestPagePoints = 0;
        _tallestPagePoints = 0;

        for (int index = 0; index < sizes.Count; index++)
        {
            var page = new PdfPageViewItem(index, sizes[index].Width, sizes[index].Height);
            ApplyPageMetric(page);
            Pages.Add(page);

            _widestPagePoints = Math.Max(_widestPagePoints, sizes[index].Width);
            _tallestPagePoints = Math.Max(_tallestPagePoints, sizes[index].Height);
        }

        if (Pages.Count > 0)
            Pages[0].IsActive = true;
    }

    private async Task EnsurePageRenderedAsync(PdfPageViewItem page, int generation, CancellationToken token)
    {
        // Aynı sayfa eski bir yakınlaştırmayla çiziliyor olabilir; o iş bittiğinde sonucu atılır.
        // Bu yüzden "çiziliyor" değil, "bu ölçekte çiziliyor" kontrolü yapılır.
        string key = CurrentRenderKey;
        if ((page.Image != null && page.RenderedKey == key) || page.PendingKey == key || generation != _renderGeneration)
            return;

        bool started = false;
        await _renderGate.WaitAsync(token);
        try
        {
            key = CurrentRenderKey;
            if ((page.Image != null && page.RenderedKey == key) || page.PendingKey == key || generation != _renderGeneration)
                return;

            started = true;
            page.PendingKey = key;
            page.IsRendering = true;
            if (page.Image == null)
                page.RenderStatus = "Sayfa hazırlanıyor...";

            var viewerSession = _viewerSession ?? throw new InvalidOperationException("PDF render oturumu yok.");

            // Başlamış bir çizim, sıradaki istekler yüzünden iptal edilmez: yalnızca belge değişince
            // iptal olur. Aksi halde yeni istek "bu ölçek zaten çiziliyor" diye atlar, eski çizim de
            // iptal edildiği için sonuç hiç atanmaz ve sayfa bulanık kalırdı.
            CancellationToken sessionToken = _sessionCts?.Token ?? CancellationToken.None;
            var rendered = await viewerSession.RenderPageAsync(page.PageIndex, ZoomPercent, RenderScale, sessionToken);

            // Sonuç, çizim sürerken yakınlaştırma değişmediyse geçerlidir.
            if (key != CurrentRenderKey || !ReferenceEquals(viewerSession, _viewerSession))
                return;

            page.UpdateSourceSize(rendered.SourceWidth, rendered.SourceHeight);
            page.ViewWidth = rendered.ViewWidth;
            page.ViewHeight = rendered.ViewHeight;
            page.Image = rendered.Image;
            page.RenderedKey = key;
            // Bitmap piksel üst sınırına takıldıysa ekranda gerilir; o durumda yumuşatarak ölçeklenir.
            page.RenderedExact = Math.Abs(rendered.Width - rendered.ViewWidth * RenderScale) < 1.5;
            page.IsImageExact = page.RenderedExact;
            page.RenderStatus = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Kaydetme sırasında render oturumu kapatılıp yeniden açılır.
        }
        catch (Exception ex)
        {
            page.RenderStatus = "Sayfa görüntülenemedi";
            StatusMessage = "PDF sayfası görüntülenemedi";
            FileLogger.LogError("PdfStudioRender", ex);
        }
        finally
        {
            if (started && page.PendingKey == key)
            {
                page.PendingKey = "";
                page.IsRendering = false;
            }

            _renderGate.Release();
        }
    }

    private void UpdatePageMetrics()
    {
        string key = CurrentRenderKey;

        foreach (var page in Pages)
        {
            ApplyPageMetric(page);

            // Yakınlaştırma eski değerine dönerse eldeki görüntü yine birebirdir.
            page.IsImageExact = page.Image != null && page.RenderedKey == key && page.RenderedExact;

            foreach (var annotation in page.Annotations)
                UpdateViewBounds(annotation);

            foreach (var result in page.SearchResults)
                UpdateViewBounds(result);
        }
    }

    private void ApplyPageMetric(PdfPageViewItem page)
    {
        var (_, _, viewWidth, viewHeight) =
            PdfViewerService.MeasurePage(page.SourceWidth, page.SourceHeight, ZoomPercent, RenderScale);
        page.ViewWidth = viewWidth;
        page.ViewHeight = viewHeight;
    }

    private void TrimRenderedPages(int centerPageIndex)
    {
        int cacheRadius = CurrentCacheRadius;

        foreach (var page in Pages)
        {
            if (Math.Abs(page.PageIndex - centerPageIndex) <= cacheRadius)
                continue;

            if (page.Image == null || page.IsRendering)
                continue;

            page.Image = null;
            page.RenderedKey = "";
            page.IsImageExact = false;
            page.RenderStatus = "";
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

    private CancellationToken ResetSessionWork()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();
        ResetRenderWork();
        return _sessionCts.Token;
    }

    private void StartWorkingCopyPreparation(PdfDocumentSession session, CancellationToken token)
    {
        _ = PrepareWorkingCopyAsync(session, token);
    }

    private async Task PrepareWorkingCopyAsync(PdfDocumentSession session, CancellationToken token)
    {
        try
        {
            await _sessionService.EnsureWorkingCopyAsync(session, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLogger.LogError("PdfStudioWorkingCopy", ex);
        }
    }

    private void DisposeViewerSession()
    {
        _viewerSession?.Dispose();
        _viewerSession = null;
    }

    private void ReopenViewerSession(string pdfPath)
    {
        DisposeViewerSession();
        _viewerSession = _viewerService.OpenSession(pdfPath);
    }

    private void TryReopenViewerSession(string pdfPath)
    {
        try
        {
            ReopenViewerSession(pdfPath);
        }
        catch (Exception ex)
        {
            FileLogger.LogError("PdfStudioReopenViewer", ex);
        }
    }

    private static bool IsSamePath(string first, string second)
    {
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
    }

    private void GoToPage(int pageIndex, bool requestScroll)
    {
        if (Session == null || Pages.Count == 0)
            return;

        int target = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        CurrentPageIndex = target;
        JumpToPageText = (target + 1).ToString(CultureInfo.InvariantCulture);
        QueuePageWindowRender(target);

        if (requestScroll)
            ScrollToPageRequested?.Invoke(this, target);
    }

    private void ApplyFitZoom()
    {
        if (Pages.Count == 0 || _viewportWidth <= 0 || _viewportHeight <= 0 || ZoomMode == PdfZoomMode.Custom)
            return;

        // Belgedeki en geniş / en uzun sayfa esas alınır; böylece karışık boyutlu belgelerde
        // kaydırırken yakınlaştırma değişip görüntü zıplamaz.
        double pageWidth = _widestPagePoints * PdfViewerService.ScreenDpi / PdfViewerService.PdfPointDpi;
        double pageHeight = _tallestPagePoints * PdfViewerService.ScreenDpi / PdfViewerService.PdfPointDpi;
        if (pageWidth <= 0 || pageHeight <= 0)
            return;

        double fitWidth = (_viewportWidth - ViewportHorizontalChrome) / pageWidth * 100d;
        double zoom = ZoomMode == PdfZoomMode.FitPage
            ? Math.Min(fitWidth, (_viewportHeight - ViewportVerticalChrome) / pageHeight * 100d)
            : fitWidth;

        zoom = Math.Clamp(Math.Floor(zoom * 10d) / 10d, MinZoomPercent, MaxZoomPercent);
        if (Math.Abs(zoom - ZoomPercent) < 0.05)
            return;

        _applyingFitZoom = true;
        try
        {
            ZoomPercent = zoom;
        }
        finally
        {
            _applyingFitZoom = false;
        }
    }

    private void ApplyZoomText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        string value = text.Trim();

        if (string.Equals(value, FitWidthLabel, StringComparison.CurrentCultureIgnoreCase))
        {
            ZoomMode = PdfZoomMode.FitWidth;
            ApplyFitZoom();
        }
        else if (string.Equals(value, FitPageLabel, StringComparison.CurrentCultureIgnoreCase))
        {
            ZoomMode = PdfZoomMode.FitPage;
            ApplyFitZoom();
        }
        else if (string.Equals(value, ActualSizeLabel, StringComparison.CurrentCultureIgnoreCase))
        {
            ZoomPercent = 100;
            ZoomMode = PdfZoomMode.Custom;
        }
        else
        {
            string number = value.TrimEnd('%', ' ').Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                UpdateZoomText();
                return;
            }

            double zoom = Math.Clamp(parsed, MinZoomPercent, MaxZoomPercent);
            if (Math.Abs(zoom - ZoomPercent) < 0.05)
                ZoomMode = PdfZoomMode.Custom;
            else
                ZoomPercent = zoom;
        }

        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        _updatingZoomText = true;
        try
        {
            ZoomText = FormatZoom(ZoomPercent);
        }
        finally
        {
            _updatingZoomText = false;
        }
    }

    private static string FormatZoom(double zoom)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(zoom):0}%");
    }

    private static double NextZoomStop(double current, bool up)
    {
        if (up)
        {
            foreach (double stop in ZoomStops)
            {
                if (stop > current + 0.5)
                    return stop;
            }

            return MaxZoomPercent;
        }

        for (int i = ZoomStops.Length - 1; i >= 0; i--)
        {
            if (ZoomStops[i] < current - 0.5)
                return ZoomStops[i];
        }

        return MinZoomPercent;
    }

    private void UpdateViewBounds(PdfAnnotationItem annotation)
    {
        double scale = CurrentScale;
        annotation.ViewX = annotation.X * scale;
        annotation.ViewY = annotation.Y * scale;
        annotation.ViewWidth = annotation.Width * scale;
        annotation.ViewHeight = annotation.Height * scale;
        annotation.ViewFontSize = Math.Max(1, annotation.FontSize * scale);
    }

    private void UpdateViewBounds(PdfSearchResult result)
    {
        double scale = CurrentScale;
        result.ViewX = result.X * scale;
        result.ViewY = result.Y * scale;
        result.ViewWidth = Math.Max(4, result.Width * scale);
        result.ViewHeight = Math.Max(4, result.Height * scale);
    }

    /// <summary>
    /// Yeni açıklamaların varsayılan boyutu PDF noktası cinsindendir; böylece hangi yakınlaştırmada
    /// eklenirse eklensin kağıt üzerinde aynı büyüklükte olur.
    /// </summary>
    private static void SetDefaultSize(PdfAnnotationItem annotation)
    {
        switch (annotation.Type)
        {
            case PdfAnnotationType.Text:
                annotation.Width = 165;
                annotation.Height = 36;
                break;
            case PdfAnnotationType.Highlight:
                annotation.Width = 142;
                annotation.Height = 21;
                break;
            case PdfAnnotationType.Note:
                annotation.Width = 135;
                annotation.Height = 82;
                annotation.Color = "#FFE08A";
                break;
            case PdfAnnotationType.Ink:
                annotation.Width = 105;
                annotation.Height = 52;
                break;
            case PdfAnnotationType.Rectangle:
                annotation.Width = 135;
                annotation.Height = 68;
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
        OnPropertyChanged(nameof(HasUnsavedChanges));
        NotifyCommandStates();
    }

    /// <summary>
    /// Kaydedilmemiş değişiklik varsa kullanıcıya sorar. Devam edilebilirse true döner.
    /// </summary>
    public bool ConfirmDiscardChanges(string action)
    {
        if (Session?.IsDirty != true)
            return true;

        var result = MessageBox.Show(
            $"'{Session.FileName}' belgesinde kaydedilmemiş değişiklikler var. {action} bu değişiklikler kaybolacak.\n\nDevam edilsin mi?",
            "DocMaster Pro",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }

    private bool ConfirmReplaceOpenSession() => ConfirmDiscardChanges("Yeni PDF açılırsa");

    /// <summary>PDF noktasını ekran birimine (DIP) çeviren çarpan.</summary>
    private double CurrentScale =>
        Math.Max(0.05, ZoomPercent / 100d) * PdfViewerService.ScreenDpi / PdfViewerService.PdfPointDpi;

    private string CurrentRenderKey =>
        string.Create(CultureInfo.InvariantCulture, $"{ZoomPercent:0.###}|{RenderScale:0.###}");

    /// <summary>
    /// Bellek bütçesine göre geçerli sayfanın iki yanında kaç sayfanın görüntüsünün tutulacağı.
    /// Yüksek yakınlaştırmada sayfa başına bitmap büyüdüğü için yarıçap küçülür.
    /// </summary>
    private int CurrentCacheRadius
    {
        get
        {
            var page = CurrentPageItem;
            if (page == null)
                return MaxCacheRadius;

            var (pixelWidth, pixelHeight, _, _) =
                PdfViewerService.MeasurePage(page.SourceWidth, page.SourceHeight, ZoomPercent, RenderScale);
            long bytesPerPage = Math.Max(1L, (long)pixelWidth * pixelHeight * 4);
            long pages = RenderCacheBudgetBytes / bytesPerPage;
            return (int)Math.Clamp((pages - 1) / 2, 1, MaxCacheRadius);
        }
    }

    private bool HasOpenSession() => Session != null && !IsBusy;

    private bool CanSave() => HasOpenSession();

    private bool CanGoPrevious() => HasOpenSession() && CurrentPageIndex > 0;

    private bool CanGoNext() => HasOpenSession() && Session != null && CurrentPageIndex < Session.PageCount - 1;

    private bool CanZoomIn() => HasOpenSession() && ZoomPercent < MaxZoomPercent - 0.5;

    private bool CanZoomOut() => HasOpenSession() && ZoomPercent > MinZoomPercent + 0.5;

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
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        FitWidthCommand.NotifyCanExecuteChanged();
        FitPageCommand.NotifyCanExecuteChanged();
        ActualSizeCommand.NotifyCanExecuteChanged();
        GoToPageCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        NextSearchResultCommand.NotifyCanExecuteChanged();
        PreviousSearchResultCommand.NotifyCanExecuteChanged();
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        ApplyAnnotationPropertiesCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _renderCts?.Cancel();
        DisposeViewerSession();
        _sessionCts?.Dispose();
        _renderCts?.Dispose();
        _sessionCts = null;
        _renderCts = null;
        // Semaforlar bilerek serbest bırakılmaz: arka planda bitmekte olan çizimler hâlâ Release çağırabilir.
        _sessionService.CleanupSession(Session);
        GC.SuppressFinalize(this);
    }
}
