namespace PdfStudio.Core.Documents;

/// <summary>
/// Hierarchical PDF bookmark / outline node.
/// </summary>
public sealed record PdfOutlineNode(
    string Title,
    int? TargetPageIndex,
    IReadOnlyList<PdfOutlineNode> Children);

