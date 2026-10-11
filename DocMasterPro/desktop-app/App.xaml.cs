using System.Windows;
using System.Windows.Threading;
using DocConverter.Helpers;
using DocConverter.Services;
using DocConverter.Views;
using PdfSharp.Fonts;
using Velopack;

namespace DocConverter
{
    public partial class App : Application
    {
        private static string[] startupArgs = Array.Empty<string>();

        [STAThread]
        private static void Main(string[] args)
        {
            VelopackApp.Build().Run();
            startupArgs = args;

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            RegisterGlobalExceptionHandlers();

            // PDFsharp için font resolver ayarla
            GlobalFontSettings.FontResolver = new WindowsFontResolver();
            WindowsFileAssociationService.RegisterPdfOpenWithForCurrentUser();

            // PDFium'u baştan yükle: tek dosya (single-file) yayında PdfiumViewer kendi başına bulamaz ve
            // ilk PDF işlemi yazdırma olursa "pdfium.dll yüklenemedi" hatası verirdi.
            try
            {
                PdfiumNativeLoader.EnsureLoaded();
            }
            catch (Exception ex)
            {
                FileLogger.LogError("PDFium yüklenemedi", ex);
            }

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;

            string? startupPath = (startupArgs.Length > 0 ? startupArgs : e.Args)
                .FirstOrDefault(arg => !string.IsNullOrWhiteSpace(arg) && !arg.StartsWith('-') && !arg.StartsWith('/'));
            if (!string.IsNullOrWhiteSpace(startupPath) && PathValidator.TryResolveExistingPdfPath(startupPath, out string? resolvedPdf) && !string.IsNullOrWhiteSpace(resolvedPdf))
            {
                string targetPdf = resolvedPdf;
                window.Loaded += async (_, _) => await window.OpenPdfInStudioAsync(targetPdf);
            }

            window.Show();
        }

        /// <summary>
        /// Yakalanmamış hatalar uygulamayı sessizce kapatmasın: kaydedilir, kullanıcıya gösterilir
        /// ve mümkünse uygulama açık kalır (taranmış ama kaydedilmemiş sayfalar kaybolmasın).
        /// </summary>
        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                FileLogger.LogError("Gözlenmeyen görev hatası", args.Exception);
                args.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    FileLogger.LogError("Kritik hata (uygulama kapanıyor)", ex);
            };
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            FileLogger.LogError("Beklenmeyen hata", e.Exception);
            e.Handled = true;

            MessageBox.Show(
                "Beklenmeyen bir hata oluştu, işlem tamamlanamadı:\n\n" +
                e.Exception.Message +
                "\n\nUygulama açık kalacak. Ayrıntılar günlük dosyasına kaydedildi:\n" +
                FileLogger.LogFilePath,
                "DocMaster Pro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
