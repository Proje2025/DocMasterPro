using System;
using System.IO;
using PdfSharp.Fonts;

namespace DocConverter.Services
{
    /// <summary>
    /// PDFsharp için font resolver - Windows sistem fontlarını kullanır.
    /// </summary>
    public class WindowsFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // Arial font ailesi için
            if (familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase))
            {
                if (isBold && isItalic)
                    return new FontResolverInfo("Arial#BoldItalic");
                if (isBold)
                    return new FontResolverInfo("Arial#Bold");
                if (isItalic)
                    return new FontResolverInfo("Arial#Italic");
                return new FontResolverInfo("Arial#Regular");
            }

            // Consolas için
            if (familyName.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
            {
                return new FontResolverInfo("Consolas#Regular");
            }

            // Times New Roman
            if (familyName.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase))
            {
                if (isBold)
                    return new FontResolverInfo("Times New Roman#Bold");
                return new FontResolverInfo("Times New Roman#Regular");
            }

            // Varsayılan olarak Arial kullan
            return new FontResolverInfo("Arial#Regular");
        }

        public byte[]? GetFont(string faceName)
        {
            string fontPath = GetFontPath(faceName);
            if (fontPath != null && File.Exists(fontPath))
            {
                return File.ReadAllBytes(fontPath);
            }
            return null;
        }

        private string? GetFontPath(string faceName)
        {
            string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

            return faceName switch
            {
                "Arial#Regular" => Path.Combine(fontsFolder, "arial.ttf"),
                "Arial#Bold" => Path.Combine(fontsFolder, "arialbd.ttf"),
                "Arial#Italic" => Path.Combine(fontsFolder, "ariali.ttf"),
                "Arial#BoldItalic" => Path.Combine(fontsFolder, "arialbi.ttf"),
                "Consolas#Regular" => Path.Combine(fontsFolder, "consola.ttf"),
                "Consolas#Bold" => Path.Combine(fontsFolder, "consolab.ttf"),
                "Times New Roman#Regular" => Path.Combine(fontsFolder, "times.ttf"),
                "Times New Roman#Bold" => Path.Combine(fontsFolder, "timesbd.ttf"),
                _ => Path.Combine(fontsFolder, "arial.ttf")
            };
        }
    }
}
