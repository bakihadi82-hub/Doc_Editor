using PdfStudio.Core.Documents;

namespace PdfStudio.Core.Annotations;

public enum PdfToolKind
{
    Select,
    Hand,
    Highlight,
    Underline,
    Draw,
    Text,
    Note,
    Redact,
    Signature
}

public enum AnnotationKind
{
    Highlight,
    Underline,
    FreehandDraw,
    TextBox,
    StickyNote,
    Redaction,
    SignatureStamp
}

public readonly record struct PdfPoint(double XPt, double YPt);

public sealed class PdfAnnotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int PageIndex { get; set; }
    public AnnotationKind Kind { get; init; }

    /// <summary>
    /// Normalized page rectangle in top-left PDF points (Left &lt;= Right, Top &lt;= Bottom).
    /// </summary>
    public PdfRect BoundsPt { get; set; } = new(0, 0, 0, 0);

    public string ColorHex { get; set; } = "#FACC15";
    public double StrokeWidthPt { get; set; } = 2.0;
    public double FontSizePt { get; set; } = 12.0;
    public List<PdfPoint> InkPointsPt { get; init; } = [];
    public string TextContent { get; set; } = string.Empty;
    public byte[]? SignaturePngBytes { get; set; }

    public PdfAnnotation Clone() => new()
    {
        Id = Id,
        PageIndex = PageIndex,
        Kind = Kind,
        BoundsPt = BoundsPt,
        ColorHex = ColorHex,
        StrokeWidthPt = StrokeWidthPt,
        FontSizePt = FontSizePt,
        InkPointsPt = [.. InkPointsPt],
        TextContent = TextContent,
        SignaturePngBytes = SignaturePngBytes is null ? null : [.. SignaturePngBytes]
    };
}

