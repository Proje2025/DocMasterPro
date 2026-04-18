using DocConverter.Models;
using UglyToad.PdfPig;

namespace DocConverter.Services;

public class PdfTextSearchService
{
    public Task<IReadOnlyList<PdfSearchResult>> SearchAsync(
        string pdfPath,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<PdfSearchResult>>(Array.Empty<PdfSearchResult>());

        return Task.Run<IReadOnlyList<PdfSearchResult>>(() =>
        {
            var results = new List<PdfSearchResult>();
            using var document = PdfDocument.Open(pdfPath);
            int resultIndex = 0;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var words = page.GetWords().ToList();
                foreach (var word in words)
                {
                    if (word.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0)
                        continue;

                    var bounds = word.BoundingBox;
                    results.Add(new PdfSearchResult
                    {
                        PageIndex = page.Number - 1,
                        ResultIndex = resultIndex++,
                        Text = word.Text,
                        X = bounds.Left,
                        Y = page.Height - bounds.Top,
                        Width = bounds.Width,
                        Height = bounds.Height
                    });
                }
            }

            return results;
        }, cancellationToken);
    }
}
