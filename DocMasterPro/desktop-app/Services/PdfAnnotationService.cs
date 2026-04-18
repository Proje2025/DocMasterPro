using System.Drawing;
using System.IO;
using DocConverter.Models;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DocConverter.Services;

public class PdfAnnotationService
{
    public Task ApplyAnnotationsAsync(
        string inputPdf,
        string outputPdf,
        IEnumerable<PdfAnnotationItem> annotations,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPdf) || !File.Exists(inputPdf))
            throw new FileNotFoundException("PDF dosyası bulunamadı.", inputPdf);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = PdfReader.Open(inputPdf, PdfDocumentOpenMode.Modify);
            foreach (var group in annotations.GroupBy(a => a.PageIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Key < 0 || group.Key >= document.PageCount)
                    continue;

                PdfPage page = document.Pages[group.Key];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                foreach (var annotation in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DrawAnnotation(gfx, annotation);
                }
            }

            document.Save(outputPdf);

            if (!File.Exists(outputPdf) || new FileInfo(outputPdf).Length == 0)
                throw new IOException("Annotation uygulanmış PDF çıktısı oluşturulamadı.");
        }, cancellationToken);
    }

    private static void DrawAnnotation(XGraphics gfx, PdfAnnotationItem annotation)
    {
        XRect rect = new(annotation.X, annotation.Y, annotation.Width, annotation.Height);
        XColor color = ToXColor(annotation.Color, annotation.Opacity);

        switch (annotation.Type)
        {
            case PdfAnnotationType.Text:
                DrawText(gfx, annotation, rect);
                break;

            case PdfAnnotationType.Highlight:
                gfx.DrawRectangle(new XSolidBrush(color), rect);
                break;

            case PdfAnnotationType.Note:
                DrawNote(gfx, annotation, rect, color);
                break;

            case PdfAnnotationType.Ink:
                gfx.DrawLine(new XPen(ToXColor(annotation.Color, 1), annotation.StrokeWidth),
                    annotation.X,
                    annotation.Y,
                    annotation.X + annotation.Width,
                    annotation.Y + annotation.Height);
                break;

            case PdfAnnotationType.Rectangle:
                gfx.DrawRectangle(new XPen(ToXColor(annotation.Color, 1), annotation.StrokeWidth), rect);
                break;
        }
    }

    private static void DrawText(XGraphics gfx, PdfAnnotationItem annotation, XRect rect)
    {
        string text = string.IsNullOrWhiteSpace(annotation.Text) ? "Yeni metin" : annotation.Text;
        var font = new XFont("Arial", annotation.FontSize);
        var brush = new XSolidBrush(ToXColor(annotation.Color, 1));
        var formatter = new XTextFormatter(gfx);
        formatter.DrawString(text, font, brush, rect, XStringFormats.TopLeft);
    }

    private static void DrawNote(XGraphics gfx, PdfAnnotationItem annotation, XRect rect, XColor color)
    {
        gfx.DrawRoundedRectangle(new XSolidBrush(color), rect, new XSize(6, 6));
        gfx.DrawRoundedRectangle(new XPen(ToXColor("#C69A00", 1), 1), rect, new XSize(6, 6));

        string text = string.IsNullOrWhiteSpace(annotation.Text) ? "Not" : annotation.Text;
        var formatter = new XTextFormatter(gfx);
        formatter.DrawString(text, new XFont("Arial", Math.Max(9, annotation.FontSize - 2)),
            XBrushes.Black, new XRect(rect.X + 8, rect.Y + 6, Math.Max(1, rect.Width - 16), Math.Max(1, rect.Height - 12)),
            XStringFormats.TopLeft);
    }

    private static XColor ToXColor(string hex, double opacity)
    {
        Color parsed;
        try
        {
            parsed = ColorTranslator.FromHtml(hex);
        }
        catch
        {
            parsed = Color.Gold;
        }

        int alpha = Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        return XColor.FromArgb(alpha, parsed.R, parsed.G, parsed.B);
    }
}
