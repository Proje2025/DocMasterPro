using System.Windows;
using DocConverter.Services;
using DocConverter.Views;
using PdfSharp.Fonts;

namespace DocConverter
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // PDFsharp için font resolver ayarla
            GlobalFontSettings.FontResolver = new WindowsFontResolver();

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;

            string? startupPath = e.Args.FirstOrDefault(arg => !string.IsNullOrWhiteSpace(arg));
            if (!string.IsNullOrWhiteSpace(startupPath))
                window.Loaded += async (_, _) => await window.OpenPdfInStudioAsync(startupPath);

            window.Show();
        }
    }
}
