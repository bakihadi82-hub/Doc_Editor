namespace PdfStudio.Core.Documents;

/// <summary>
/// Immutable page geometry and rotation metadata in PDF points (1/72 inch).
/// </summary>
public sealed record PdfPageInfo(
    int PageIndex,
    float WidthPoints,
    float HeightPoints,
    int RotationDegrees = 0,
    int SourcePageIndex = -1,
    bool IsBlankPage = false)
{
    public int DisplayNumber => PageIndex + 1;
    public int EffectiveSourcePageIndex => SourcePageIndex >= 0 ? SourcePageIndex : PageIndex;
}
