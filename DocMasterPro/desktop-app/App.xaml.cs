using System.Windows;
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
            // PDFsharp için font resolver ayarla
            GlobalFontSettings.FontResolver = new WindowsFontResolver();
            WindowsFileAssociationService.RegisterPdfOpenWithForCurrentUser();

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
    }
}
