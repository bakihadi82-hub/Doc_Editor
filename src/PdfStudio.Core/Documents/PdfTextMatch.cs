namespace PdfStudio.Core.Documents;

/// <summary>
/// PDF-space bounding rectangle (points, bottom-left origin) for text selection or search hits.
/// </summary>
public sealed record PdfRect(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Search hit or extracted text span on a specific page.
/// </summary>
public sealed record PdfTextMatch(int PageIndex, int CharIndex, int CharLength, IReadOnlyList<PdfRect> Bounds);

