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
        DateTime openedAt,
        bool isWorkingCopyReady = false)
    {
        OriginalPath = originalPath;
        WorkingPath = workingPath;
        SessionFolder = sessionFolder;
        PageCount = pageCount;
        OpenedAt = openedAt;
        OutputPath = originalPath;
        IsWorkingCopyReady = isWorkingCopyReady;
    }

    public string OriginalPath { get; }

    [ObservableProperty]
    private string workingPath;

    public string SessionFolder { get; }

    [ObservableProperty]
    private string outputPath;

    [ObservableProperty]
    private int pageCount;

    [ObservableProperty]
    private bool isDirty;

    [ObservableProperty]
    private bool isWorkingCopyReady;

    public DateTime OpenedAt { get; }

    public string FileName => Path.GetFileName(OriginalPath);

    internal SemaphoreSlim WorkingCopyGate { get; } = new(1, 1);
}
