using PdfStudio.Core.Documents;

namespace PdfStudio.Core.Annotations;

public enum PdfToolKind
{
    Select,
    Hand,
    EditText,
    Highlight,
    Underline,
    Strikeout,
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
    Strikeout,
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
    public List<PdfRect> HighlightRectsPt { get; init; } = [];

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
        SignaturePngBytes = SignaturePngBytes is null ? null : [.. SignaturePngBytes],
        HighlightRectsPt = [.. HighlightRectsPt]
    };
}

/// <summary>
/// Represents an inline edit or deletion of an existing PDF text block,
/// previewed live on canvas and committed cleanly to the PDF content stream on Save.
/// </summary>
public sealed class PdfTextEdit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int PageIndex { get; set; }
    public PdfRect OriginalBoundsPt { get; set; } = new(0, 0, 0, 0);
    public string OriginalText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
    public double FontSizePt { get; set; } = 12.0;
    public string FontFamily { get; set; } = "Arial";
    public string ColorHex { get; set; } = "#111827";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }

    public PdfTextEdit Clone() => new()
    {
        Id = Id,
        PageIndex = PageIndex,
        OriginalBoundsPt = OriginalBoundsPt,
        OriginalText = OriginalText,
        NewText = NewText,
        FontSizePt = FontSizePt,
        FontFamily = FontFamily,
        ColorHex = ColorHex,
        IsBold = IsBold,
        IsItalic = IsItalic
    };
}

