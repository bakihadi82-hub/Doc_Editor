using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Formats;

namespace PdfStudio.App.ViewModels;

public sealed record PdfTextSelectionSpan(
    int StartPageIndex,
    int StartCharIndex,
    int EndPageIndex,
    int EndCharIndex)
{
    public bool IsNormalizedOrder =>
        StartPageIndex < EndPageIndex || (StartPageIndex == EndPageIndex && StartCharIndex <= EndCharIndex);

    public PdfTextSelectionSpan Normalized() => IsNormalizedOrder
        ? this
        : new PdfTextSelectionSpan(EndPageIndex, EndCharIndex, StartPageIndex, StartCharIndex);
}

public sealed class PdfWorkspaceViewModel : DocumentTabViewModel
{
    public StructuralEditPlan EditPlan { get; set; } = new();
    public Stack<(string WorkingPath, StructuralEditPlan Plan)> UndoStack { get; } = new();
    public Stack<(string WorkingPath, StructuralEditPlan Plan)> RedoStack { get; } = new();
    public string? Password { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int ZoomPercent { get; set; } = 100;

    public Dictionary<int, PdfPageTextLayout> TextLayoutCache { get; } = new();
    public PdfTextSelectionSpan? ActiveSelection { get; set; }

    public override bool CanUndo => UndoStack.Count > 0;
    public override bool CanRedo => RedoStack.Count > 0;

    public PdfWorkspaceViewModel(string filePath)
        : base(filePath, DocumentFormatKind.Pdf)
    {
        UpdateMetadataBadges();
    }

    public void UpdateMetadataBadges()
    {
        int count = Math.Max(1, EditPlan.Entries.Count);
        MicroMetadataBadge = $"{count}p";
        ContextStatusText = $"Page {Math.Clamp(CurrentPage, 1, count)} of {count} • {ZoomPercent}%";
    }

    public void ClearTextLayoutCache()
    {
        TextLayoutCache.Clear();
        ActiveSelection = null;
    }
}
