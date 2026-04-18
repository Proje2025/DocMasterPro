using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models;

public partial class PdfDocumentSession : ObservableObject
{
    public PdfDocumentSession(
        string originalPath,
        string workingPath,
        string sessionFolder,
        int pageCount,
        DateTime openedAt)
    {
        OriginalPath = originalPath;
        WorkingPath = workingPath;
        SessionFolder = sessionFolder;
        PageCount = pageCount;
        OpenedAt = openedAt;
        OutputPath = originalPath;
    }

    public string OriginalPath { get; }

    public string WorkingPath { get; }

    public string SessionFolder { get; }

    [ObservableProperty]
    private string outputPath;

    [ObservableProperty]
    private int pageCount;

    [ObservableProperty]
    private bool isDirty;

    public DateTime OpenedAt { get; }

    public string FileName => Path.GetFileName(OriginalPath);
}
