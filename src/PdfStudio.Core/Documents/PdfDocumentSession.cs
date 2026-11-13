namespace PdfStudio.Core.Documents;

/// <summary>
/// Tracks the active PDF document state, pages, and unsaved structural dirtiness.
/// </summary>
public sealed class PdfDocumentSession
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public required IReadOnlyList<PdfPageInfo> Pages { get; init; }
    public int PageCount => Pages.Count;
    public bool IsDirty { get; set; }
}

