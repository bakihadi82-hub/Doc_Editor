using PdfStudio.Core.Annotations;

namespace PdfStudio.Core.Documents;

/// <summary>
/// Single character glyph box on a PDF page in top-left page points (Left <= Right, Top <= Bottom).
/// </summary>
public readonly record struct PdfTextChar(
    int Index,
    char Character,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double FontSizePt,
    string FontName = "Arial",
    bool IsBold = false,
    bool IsItalic = false)
{
    public bool IsWhitespace => char.IsWhiteSpace(Character) || Character == '\0';
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
}

/// <summary>
/// Logical line of PDF characters sharing a horizontal baseline.
/// </summary>
public sealed record PdfTextLine(
    int LineIndex,
    int StartCharIndex,
    int EndCharIndex,
    PdfRect BoundsPt,
    double FontSizePt,
    string Text);

/// <summary>
/// Editable text block on a PDF page (single line or tightly grouped paragraph lines).
/// </summary>
public sealed record PdfTextBlock(
    int PageIndex,
    int StartCharIndex,
    int EndCharIndex,
    PdfRect BoundsPt,
    double FontSizePt,
    string Text,
    string FontName = "Arial",
    bool IsBold = false,
    bool IsItalic = false);

/// <summary>
/// Cached per-page text layout extracted from PDFium for instant I-Beam hit-testing,
/// multi-page character/word/line selection, and inline text block detection.
/// </summary>
public sealed class PdfPageTextLayout
{
    public int PageIndex { get; }
    public double PageWidthPt { get; }
    public double PageHeightPt { get; }
    public IReadOnlyList<PdfTextChar> Chars { get; }
    public IReadOnlyList<PdfTextLine> Lines { get; }
    public IReadOnlyList<PdfTextBlock> Blocks { get; }

    public PdfPageTextLayout(
        int pageIndex,
        double pageWidthPt,
        double pageHeightPt,
        IReadOnlyList<PdfTextChar> chars)
    {
        PageIndex = pageIndex;
        PageWidthPt = pageWidthPt;
        PageHeightPt = pageHeightPt;
        Chars = chars;
        Lines = BuildLines(chars);
        Blocks = BuildBlocks(pageIndex, chars, Lines);
    }

    public int HitTestChar(double xPt, double yPt, double tolerancePt = 6.0)
    {
        int bestIdx = -1;
        double bestDistSq = double.MaxValue;

        for (int i = 0; i < Chars.Count; i++)
        {
            var c = Chars[i];
            if (c.Character == '\r' || c.Character == '\n' || c.Character == '\0')
                continue;
            if (c.Right <= c.Left && c.Bottom <= c.Top)
                continue;

            if (xPt >= c.Left - tolerancePt && xPt <= c.Right + tolerancePt &&
                yPt >= c.Top - tolerancePt && yPt <= c.Bottom + tolerancePt)
            {
                double cx = (c.Left + c.Right) * 0.5;
                double cy = (c.Top + c.Bottom) * 0.5;
                double dx = xPt - cx;
                double dy = yPt - cy;
                double distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    public int FindNearestCharOnOrNearPoint(double xPt, double yPt)
    {
        int exact = HitTestChar(xPt, yPt, 8.0);
        if (exact >= 0) return exact;

        int bestIdx = -1;
        double bestScore = double.MaxValue;

        for (int i = 0; i < Chars.Count; i++)
        {
            var c = Chars[i];
            if (c.Character == '\r' || c.Character == '\n' || c.Character == '\0')
                continue;
            if (c.Right <= c.Left && c.Bottom <= c.Top)
                continue;

            double cy = (c.Top + c.Bottom) * 0.5;
            double cx = (c.Left + c.Right) * 0.5;
            double score = Math.Abs(yPt - cy) * 4.0 + Math.Abs(xPt - cx);
            if (score < bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    public PdfTextBlock? HitTestBlock(double xPt, double yPt, double tolerancePt = 4.0)
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            var b = Blocks[i];
            if (xPt >= b.BoundsPt.Left - tolerancePt && xPt <= b.BoundsPt.Right + tolerancePt &&
                yPt >= b.BoundsPt.Top - tolerancePt && yPt <= b.BoundsPt.Bottom + tolerancePt)
            {
                return b;
            }
        }
        return null;
    }

    public (int Start, int End) GetWordRangeAt(int charIndex)
    {
        if (Chars.Count == 0 || charIndex < 0 || charIndex >= Chars.Count)
            return (-1, -1);

        if (Chars[charIndex].IsWhitespace)
            return (charIndex, charIndex);

        int start = charIndex;
        while (start > 0 && !Chars[start - 1].IsWhitespace && SameLine(Chars[start - 1], Chars[start]))
        {
            start--;
        }

        int end = charIndex;
        while (end < Chars.Count - 1 && !Chars[end + 1].IsWhitespace && SameLine(Chars[end], Chars[end + 1]))
        {
            end++;
        }

        return (start, end);
    }

    public (int Start, int End) GetLineRangeAt(int charIndex)
    {
        foreach (var line in Lines)
        {
            if (charIndex >= line.StartCharIndex && charIndex <= line.EndCharIndex)
            {
                return (line.StartCharIndex, line.EndCharIndex);
            }
        }
        return (charIndex, charIndex);
    }

    public string ExtractRangeText(int startIndex, int endIndex)
    {
        if (Chars.Count == 0) return string.Empty;
        int lo = Math.Clamp(Math.Min(startIndex, endIndex), 0, Chars.Count - 1);
        int hi = Math.Clamp(Math.Max(startIndex, endIndex), 0, Chars.Count - 1);

        var sb = new System.Text.StringBuilder(hi - lo + 1);
        PdfTextChar? prev = null;
        for (int i = lo; i <= hi; i++)
        {
            var c = Chars[i];
            if (c.Character == '\0') continue;
            if (c.Character == '\r') continue;

            if (prev is { } p && c.Character != '\n' && p.Character != '\n')
            {
                if (!SameLine(p, c))
                {
                    sb.AppendLine();
                }
                else if (c.Left - p.Right > Math.Max(2.0, p.FontSizePt * 0.25) && !char.IsWhiteSpace(p.Character) && !char.IsWhiteSpace(c.Character))
                {
                    sb.Append(' ');
                }
            }

            if (c.Character == '\n')
            {
                sb.AppendLine();
            }
            else
            {
                sb.Append(c.Character);
            }
            prev = c;
        }
        return sb.ToString();
    }

    public IReadOnlyList<PdfRect> GetMergedLineBoxes(int startIndex, int endIndex)
    {
        var result = new List<PdfRect>();
        if (Chars.Count == 0) return result;

        int lo = Math.Clamp(Math.Min(startIndex, endIndex), 0, Chars.Count - 1);
        int hi = Math.Clamp(Math.Max(startIndex, endIndex), 0, Chars.Count - 1);

        PdfRect? current = null;
        double currentBaseline = 0;

        for (int i = lo; i <= hi; i++)
        {
            var c = Chars[i];
            if (c.Character == '\r' || c.Character == '\n' || c.Character == '\0')
                continue;
            if (c.Right <= c.Left && c.Bottom <= c.Top)
                continue;

            if (current is null)
            {
                current = new PdfRect(c.Left, c.Top, c.Right, c.Bottom);
                currentBaseline = c.Bottom;
            }
            else if (Math.Abs(c.Bottom - currentBaseline) <= Math.Max(4.0, c.FontSizePt * 0.45) &&
                     c.Left >= current.Left - 8.0)
            {
                current = new PdfRect(
                    Math.Min(current.Left, c.Left),
                    Math.Min(current.Top, c.Top),
                    Math.Max(current.Right, c.Right),
                    Math.Max(current.Bottom, c.Bottom));
            }
            else
            {
                result.Add(current);
                current = new PdfRect(c.Left, c.Top, c.Right, c.Bottom);
                currentBaseline = c.Bottom;
            }
        }

        if (current is not null)
        {
            result.Add(current);
        }

        return result;
    }

    private static bool SameLine(PdfTextChar a, PdfTextChar b)
        => Math.Abs((a.Top + a.Bottom) * 0.5 - (b.Top + b.Bottom) * 0.5) <= Math.Max(4.0, Math.Max(a.FontSizePt, b.FontSizePt) * 0.5);

    private static List<PdfTextLine> BuildLines(IReadOnlyList<PdfTextChar> chars)
    {
        var lines = new List<PdfTextLine>();
        if (chars.Count == 0) return lines;

        int lineStart = -1;
        double minL = 0, minT = 0, maxR = 0, maxB = 0;
        double fontSum = 0;
        int visibleCount = 0;
        var sb = new System.Text.StringBuilder();
        PdfTextChar? prevVisible = null;

        void Flush(int endIdx)
        {
            if (lineStart < 0 || visibleCount == 0)
            {
                lineStart = -1;
                sb.Clear();
                visibleCount = 0;
                fontSum = 0;
                prevVisible = null;
                return;
            }
            string text = sb.ToString().TrimEnd();
            if (text.Length > 0)
            {
                lines.Add(new PdfTextLine(
                    lines.Count,
                    lineStart,
                    endIdx,
                    new PdfRect(minL, minT, maxR, maxB),
                    fontSum / visibleCount,
                    text));
            }
            lineStart = -1;
            sb.Clear();
            visibleCount = 0;
            fontSum = 0;
            prevVisible = null;
        }

        for (int i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c.Character == '\n')
            {
                Flush(i - 1);
                continue;
            }
            if (c.Character == '\r' || c.Character == '\0')
            {
                continue;
            }

            if (c.Right <= c.Left && c.Bottom <= c.Top)
            {
                if (c.Character == ' ' && lineStart >= 0 && sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
                continue;
            }

            if (prevVisible is { } prev &&
                (!SameLine(prev, c) || c.Left - prev.Right > Math.Max(36.0, prev.FontSizePt * 3.0)))
            {
                Flush(i - 1);
            }

            if (lineStart < 0)
            {
                lineStart = i;
                minL = c.Left;
                minT = c.Top;
                maxR = c.Right;
                maxB = c.Bottom;
            }
            else
            {
                if (prevVisible is { } p && c.Left - p.Right > Math.Max(1.8, p.FontSizePt * 0.2) && sb.Length > 0 && sb[^1] != ' ' && c.Character != ' ')
                {
                    sb.Append(' ');
                }
                minL = Math.Min(minL, c.Left);
                minT = Math.Min(minT, c.Top);
                maxR = Math.Max(maxR, c.Right);
                maxB = Math.Max(maxB, c.Bottom);
            }

            sb.Append(c.Character);
            fontSum += c.FontSizePt > 0 ? c.FontSizePt : 12.0;
            visibleCount++;
            prevVisible = c;
        }

        Flush(chars.Count - 1);
        return lines;
    }

    private static List<PdfTextBlock> BuildBlocks(int pageIndex, IReadOnlyList<PdfTextChar> chars, IReadOnlyList<PdfTextLine> lines)
    {
        var blocks = new List<PdfTextBlock>(lines.Count);
        foreach (var line in lines)
        {
            string fontName = "Arial";
            bool isBold = false;
            bool isItalic = false;
            for (int i = line.StartCharIndex; i <= line.EndCharIndex && i < chars.Count; i++)
            {
                if (!chars[i].IsWhitespace)
                {
                    fontName = chars[i].FontName;
                    isBold = chars[i].IsBold;
                    isItalic = chars[i].IsItalic;
                    break;
                }
            }
            blocks.Add(new PdfTextBlock(
                pageIndex,
                line.StartCharIndex,
                line.EndCharIndex,
                line.BoundsPt,
                line.FontSizePt > 1.0 ? line.FontSizePt : 12.0,
                line.Text,
                fontName,
                isBold,
                isItalic));
        }
        return blocks;
    }
}
