namespace PdfStudio.Core.Rendering;

public enum RenderPriority
{
    VisibleViewport = 0,
    AdjacentPreload = 1,
    Thumbnail = 2
}

/// <summary>
/// Describes a single PDF page rasterization request at an exact pixel dimension.
/// </summary>
public sealed record RenderRequest(
    int PageIndex,
    int TargetWidthPx,
    int TargetHeightPx,
    int RotationQuarterTurns,
    RenderPriority Priority,
    CancellationToken CancellationToken = default);

