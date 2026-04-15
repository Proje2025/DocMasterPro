using System.Windows;
using DocConverter.Services;
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
        }
    }
}
