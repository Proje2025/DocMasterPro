using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DocConverter.Models;
using DocConverter.Services;
using DocConverter.ViewModels;
using DocConverter.Views;
using PdfSharp.Fonts;

// "Office dönüştürme motoru" arayüzünün yerleşim ve erişilebilirlik denetimi. Yalnızca sorunları yazdırır.
// Pencereler ekran dışında açılır; App.OnStartup çalışmaz (dosya ilişkilendirmesi ve güncelleme denetimi yapılmaz).
// Çalıştırma: dotnet run --project DocMasterPro/tools/UiCheck   (ya da scripts/kalite.ps1)
public static class Program
{
    private const long MB = 1024L * 1024L;
    private static readonly Dictionary<string, List<string>> Problems = new();

    [STAThread]
    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "DocMasterPro.UiCheck");
        Directory.CreateDirectory(outDir);
        FileLogger.RedirectTo(Path.Combine(outDir, "logs"));
        GlobalFontSettings.FontResolver = new WindowsFontResolver();

        var app = new DocConverter.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var errors = new List<string>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };

        string longMessage = string.Join(" ", Enumerable.Repeat("LibreOffice hazırlanırken beklenmeyen bir hata oluştu ve işlem tamamlanamadı.", 5));

        Dialog("d1_onay", new ScriptedInstaller(), null, wantDefault: true);
        Dialog("d2_yetersiz_alan", new ScriptedInstaller { AvailableBytes = 1200 * MB }, null, wantDefault: false);
        Dialog("d3_indiriliyor", new ScriptedInstaller { HoldAt = new EngineSetupProgress(EngineSetupStage.Downloading, 124 * MB, 373 * MB) }, vm => vm.StartCommand.Execute(null), wantDefault: false);
        Dialog("d4_dogrulaniyor", new ScriptedInstaller { HoldAt = new EngineSetupProgress(EngineSetupStage.Verifying, 0, 0) }, vm => vm.StartCommand.Execute(null), wantDefault: false);
        Dialog("d5_hazirlaniyor", new ScriptedInstaller { HoldAt = new EngineSetupProgress(EngineSetupStage.Preparing, 0, 0) }, vm => vm.StartCommand.Execute(null), wantDefault: false);
        Dialog("d6_hata_indirme", new ScriptedInstaller { Error = EngineSetupError.DownloadFailed }, vm => vm.StartCommand.Execute(null), wantDefault: true);
        Dialog("d7_hata_imza", new ScriptedInstaller { Error = EngineSetupError.SignatureInvalid }, vm => vm.StartCommand.Execute(null), wantDefault: true);
        Dialog("d8_hata_disk", new ScriptedInstaller { Error = EngineSetupError.DiskFull }, vm => vm.StartCommand.Execute(null), wantDefault: true);
        Dialog("d9_hata_uzun", new ScriptedInstaller { Error = EngineSetupError.Unknown, Message = longMessage }, vm => vm.StartCommand.Execute(null), wantDefault: true);
        Dialog("d10_hazir", new ScriptedInstaller(), vm => vm.State = EngineSetupState.Completed, wantDefault: false, wantCancel: false);

        MainWindow window = null!;
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            window = new MainWindow { Left = -30000, Top = 0, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal };
            window.Show();
        });
        Pump(4000);

        if (window.DataContext is MainViewModel main)
        {
            var tabs = (TabControl)window.FindName("MainTabs");
            var root = (FrameworkElement)window.Content;
            var sizes = new (int W, int H)[] { (900, 600), (1280, 800), (1920, 1080) };
            var scenes = new (string Name, int Tab, OfficeEngineStatus Status)[]
            {
                ("office", 0, new OfficeEngineStatus(OfficeEngineKind.MicrosoftOffice)),
                ("libre_kurulu", 0, new OfficeEngineStatus(OfficeEngineKind.LibreOfficeInstalled, "26.2")),
                ("libre_indirildi", 0, new OfficeEngineStatus(OfficeEngineKind.LibreOfficeDownloaded, "26.2.6")),
                ("yok", 0, new OfficeEngineStatus(OfficeEngineKind.None)),
                ("word_gerekli", 1, new OfficeEngineStatus(OfficeEngineKind.LibreOfficeDownloaded, "26.2.6")),
                ("word_var", 1, new OfficeEngineStatus(OfficeEngineKind.MicrosoftOffice)),
            };

            foreach (var size in sizes)
            {
                window.Dispatcher.InvokeAsync(() => { window.Width = size.W; window.Height = size.H; });
                Pump(400);
                foreach (var scene in scenes)
                {
                    window.Dispatcher.InvokeAsync(() => { main.SelectedAppSection = 1; tabs.SelectedIndex = scene.Tab; main.OfficeEngine = scene.Status; });
                    Pump(500);
                    var host = tabs.Template?.FindName("PART_SelectedContentHost", tabs) as FrameworkElement ?? tabs;
                    Inspect($"{size.W}x{size.H}/{scene.Name}", root, host);
                }
            }
            main.PdfStudio.Dispose();
        }
        else
        {
            Add("ana pencere", "MainViewModel bulunamadı", "kurulum");
        }

        foreach (var pair in Problems.OrderBy(p => p.Key))
        {
            var where = pair.Value.Distinct().ToList();
            Console.WriteLine($"SORUN {pair.Key}  [{string.Join(", ", where.Take(4))}{(where.Count > 4 ? $", +{where.Count - 4}" : "")}]");
        }
        Console.WriteLine($"sorun: {Problems.Count}, yakalanmamış hata: {errors.Count}");
        foreach (string error in errors.Take(3))
            Console.WriteLine(error.Substring(0, Math.Min(500, error.Length)));
        return Problems.Count == 0 && errors.Count == 0 ? 0 : 1;
    }

    private static void Dialog(string name, ScriptedInstaller installer, Action<OfficeEngineSetupViewModel>? act, bool wantDefault, bool wantCancel = true)
    {
        OfficeEngineSetupDialog dialog = null!;
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            var vm = new OfficeEngineSetupViewModel(installer);
            dialog = new OfficeEngineSetupDialog(vm) { Left = -30000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            dialog.Show();
            act?.Invoke(vm);
        });
        Pump(900);

        var content = (FrameworkElement)dialog.Content;
        Inspect(name, content, content);

        // The app's own minimum window is 900x600, so the dialog must fit inside that.
        if (dialog.ActualHeight > 600 || dialog.ActualWidth > 900)
            Add($"900x600'e sığmıyor ({dialog.ActualWidth:0}x{dialog.ActualHeight:0})", "pencere", name);

        var buttons = Descendants(content).OfType<Button>().Where(b => b.IsVisible).ToList();
        if (wantCancel && !buttons.Any(b => b.IsCancel))
            Add("Esc ile kapatılamıyor (IsCancel düğmesi yok)", "pencere", name);
        if (wantDefault && !buttons.Any(b => b.IsDefault && b.IsEnabled))
            Add("Enter için varsayılan düğme yok", "pencere", name);

        installer.Release();
        Dispatcher.CurrentDispatcher.InvokeAsync(() => dialog.Close());
        Pump(200);
    }

    private static void Inspect(string scene, FrameworkElement root, FrameworkElement scope)
    {
        var rootRect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        foreach (DependencyObject node in Descendants(scope))
        {
            if (node is not FrameworkElement fe || !fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                continue;

            Rect bounds;
            try { bounds = fe.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight)); }
            catch (InvalidOperationException) { continue; }

            Geometry? clip = LayoutInformation.GetLayoutClip(fe);
            if (clip != null && (clip.Bounds.Width < fe.ActualWidth - 1 || clip.Bounds.Height < fe.ActualHeight - 1))
                Add($"kırpılıyor ({fe.ActualWidth:0}x{fe.ActualHeight:0} → {clip.Bounds.Width:0}x{clip.Bounds.Height:0})", Describe(fe), scene);

            if (fe is TextBlock or ButtonBase or ProgressBar)
            {
                if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > rootRect.Right + 1 || bounds.Bottom > rootRect.Bottom + 1)
                    Add("pencerenin dışına taşıyor", Describe(fe), scene);
            }

            if (fe is TextBlock text && !string.IsNullOrWhiteSpace(PlainText(text)) && text.IsEnabled)
            {
                Color background = BackgroundOf(text);
                bool icon = text.FontFamily.Source.Contains("MDL2") || text.FontFamily.Source.Contains("Icons");
                bool large = text.FontSize >= 24 || (text.FontSize >= 18.66 && text.FontWeight.ToOpenTypeWeight() >= 700);
                double needed = icon || large ? 3.0 : 4.5;

                CheckContrast(text.Foreground, text, background, needed, Describe(text), scene);
                foreach (Run run in text.Inlines.OfType<Run>())
                {
                    if (run.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue && !string.IsNullOrWhiteSpace(run.Text))
                        CheckContrast(run.Foreground, text, background, needed, "Run \"" + Short(run.Text) + "\"", scene);
                }
            }

            if (fe is ButtonBase button)
            {
                if (button.ActualWidth < 24 || button.ActualHeight < 24)
                    Add($"tıklama alanı 24 pikselden küçük ({button.ActualWidth:0}x{button.ActualHeight:0})", Describe(button), scene);
                if (string.IsNullOrWhiteSpace(AccessibleName(button)))
                    Add("erişilebilir adı yok", Describe(button), scene);
                if (button.IsEnabled && (!button.Focusable || !KeyboardNavigation.GetIsTabStop(button)))
                    Add("klavyeyle odaklanamıyor", Describe(button), scene);
            }

            if (fe is ProgressBar bar && string.IsNullOrWhiteSpace(AccessibleName(bar)))
                Add("erişilebilir adı yok", Describe(bar), scene);
        }
    }

    private static void CheckContrast(Brush? brush, TextBlock owner, Color background, double needed, string what, string scene)
    {
        if (brush is not SolidColorBrush solid)
            return;

        double alpha = solid.Color.A / 255d * solid.Opacity;
        for (DependencyObject? p = owner; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is UIElement element) alpha *= element.Opacity;

        Color foreground = Blend(solid.Color, background, alpha);
        double ratio = Contrast(foreground, background);
        if (ratio < needed)
            Add($"kontrast {ratio:0.0}:1 (en az {needed:0.0}) {Hex(foreground)} / {Hex(background)}", what, scene);
    }

    private static Color BackgroundOf(DependencyObject start)
    {
        var layers = new List<(Color Color, double Alpha)>();
        for (DependencyObject? p = VisualTreeHelper.GetParent(start); p != null; p = VisualTreeHelper.GetParent(p))
        {
            Brush? brush = p switch
            {
                Border b => b.Background,
                Panel panel => panel.Background,
                Control c => c.Background,
                _ => null
            };

            Color? color = brush switch
            {
                SolidColorBrush s => s.Color,
                GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
                _ => null
            };

            if (color is not { } value || value.A == 0)
                continue;

            double alpha = value.A / 255d * brush!.Opacity;
            layers.Add((value, alpha));
            if (alpha >= 0.999)
                break;
        }

        Color result = Colors.White;
        for (int i = layers.Count - 1; i >= 0; i--)
            result = Blend(layers[i].Color, result, layers[i].Alpha);
        return result;
    }

    private static Color Blend(Color top, Color bottom, double alpha) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string AccessibleName(UIElement element)
    {
        string name = UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "";
        return new string(name.Where(ch => ch < '' || ch > '').ToArray()).Trim();
    }

    private static string PlainText(TextBlock text) =>
        string.IsNullOrEmpty(text.Text) ? string.Concat(text.Inlines.OfType<Run>().Select(r => r.Text)) : text.Text;

    private static string Short(string value)
    {
        value = value.Trim().Replace("\r", " ").Replace("\n", " ");
        return value.Length > 38 ? value.Substring(0, 38) + "…" : value;
    }

    private static string Describe(FrameworkElement fe)
    {
        string label = fe switch
        {
            TextBlock t => Short(PlainText(t)),
            ContentControl c when c.Content is string s => Short(s),
            _ => ""
        };
        if (label.Any(ch => ch >= '' && ch <= ''))
            label = "simge";
        string name = string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name;
        return $"{fe.GetType().Name}{name}{(label.Length > 0 ? " \"" + label + "\"" : "")}";
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child))
                yield return nested;
        }
    }

    private static void Add(string problem, string what, string scene)
    {
        string key = $"{what}: {problem}";
        if (!Problems.TryGetValue(key, out var scenes))
            Problems[key] = scenes = new List<string>();
        scenes.Add(scene);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Installer that stops at a chosen stage or fails with a chosen error.</summary>
    private sealed class ScriptedInstaller : IOfficeEngineInstaller
    {
        private readonly TaskCompletionSource _hold = new();

        public long AvailableBytes { get; set; } = 180L * 1024 * MB;
        public EngineSetupProgress? HoldAt { get; set; }
        public EngineSetupError? Error { get; set; }
        public string Message { get; set; } = "betik hatası";

        public EngineSetupRequirements GetRequirements() =>
            new("26.2.6", 373 * MB, 2048 * MB, AvailableBytes, "C:", "The Document Foundation");

        public async Task InstallAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            if (Error is { } error)
            {
                await Task.Delay(50, cancellationToken);
                throw new EngineSetupException(error, Message);
            }

            if (HoldAt is { } hold)
            {
                progress.Report(hold);
                await _hold.Task.WaitAsync(cancellationToken);
            }
        }

        public void Release() => _hold.TrySetResult();
    }
}
