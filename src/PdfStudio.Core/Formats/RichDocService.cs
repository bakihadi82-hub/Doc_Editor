using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PdfStudio.Core.Formats;

public enum RichDocBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    BulletItem,
    NumberedItem,
    Table
}

/// <summary>
/// Inline styled run within a <see cref="RichDocBlock"/>, supporting mixed formatting and hyperlinks.
/// </summary>
public sealed class RichDocRun
{
    public string Text { get; set; } = string.Empty;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public string? HyperlinkUri { get; set; }
    public string? OriginalRunPropertiesXml { get; set; }

    public RichDocRun Clone() => new()
    {
        Text = Text,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsUnderline = IsUnderline,
        HyperlinkUri = HyperlinkUri,
        OriginalRunPropertiesXml = OriginalRunPropertiesXml
    };

    public bool ContentEquals(RichDocRun other) =>
        string.Equals(Text, other.Text, StringComparison.Ordinal) &&
        IsBold == other.IsBold &&
        IsItalic == other.IsItalic &&
        IsUnderline == other.IsUnderline &&
        string.Equals(HyperlinkUri, other.HyperlinkUri, StringComparison.Ordinal);
}

public sealed class RichDocBlock
{
    private RichDocBlockKind _kind = RichDocBlockKind.Paragraph;
    private bool _defaultBold;
    private bool _defaultItalic;
    private bool _defaultUnderline;

    public Guid Id { get; init; } = Guid.NewGuid();

    public RichDocBlockKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            if (Runs.Count == 0 && value is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2)
            {
                _defaultBold = true;
            }
        }
    }

    public List<RichDocRun> Runs { get; set; } = [];
    public int? SourceBodyElementIndex { get; set; }
    public bool HasEmbeddedNonTextElements { get; set; }
    internal RichDocBlock? BaselineSnapshot { get; set; }

    public string Text
    {
        get => Runs.Count == 0 ? string.Empty : string.Concat(Runs.Select(r => r.Text));
        set => SetBlockText(value ?? string.Empty);
    }

    public bool IsBold
    {
        get => Runs.Count > 0
            ? Runs.Where(r => r.Text.Length > 0).DefaultIfEmpty(Runs[0]).All(r => r.IsBold)
            : (_defaultBold || Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2);
        set
        {
            _defaultBold = value;
            foreach (var r in Runs)
            {
                r.IsBold = value;
            }
        }
    }

    public bool IsItalic
    {
        get => Runs.Count > 0
            ? Runs.Where(r => r.Text.Length > 0).DefaultIfEmpty(Runs[0]).All(r => r.IsItalic)
            : _defaultItalic;
        set
        {
            _defaultItalic = value;
            foreach (var r in Runs)
            {
                r.IsItalic = value;
            }
        }
    }

    public bool IsUnderline
    {
        get => Runs.Count > 0
            ? Runs.Where(r => r.Text.Length > 0).DefaultIfEmpty(Runs[0]).All(r => r.IsUnderline)
            : _defaultUnderline;
        set
        {
            _defaultUnderline = value;
            foreach (var r in Runs)
            {
                r.IsUnderline = value;
            }
        }
    }

    public List<List<string>> TableRows { get; set; } = [];

    /// <summary>
    /// Updates plain text while preserving inline run boundaries and formatting via minimal prefix/suffix diffing.
    /// </summary>
    private void SetBlockText(string next)
    {
        string oldText = Text;
        if (string.Equals(oldText, next, StringComparison.Ordinal))
        {
            return;
        }

        if (Runs.Count == 0)
        {
            if (next.Length > 0)
            {
                Runs.Add(new RichDocRun
                {
                    Text = next,
                    IsBold = _defaultBold || Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2,
                    IsItalic = _defaultItalic,
                    IsUnderline = _defaultUnderline
                });
            }
            return;
        }

        if (Runs.Count == 1)
        {
            Runs[0].Text = next;
            return;
        }

        if (next.Length == 0)
        {
            var anchor = Runs[0];
            anchor.Text = string.Empty;
            Runs.Clear();
            Runs.Add(anchor);
            return;
        }

        int minLen = Math.Min(oldText.Length, next.Length);
        int prefix = 0;
        while (prefix < minLen && oldText[prefix] == next[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < minLen - prefix &&
               oldText[oldText.Length - 1 - suffix] == next[next.Length - 1 - suffix])
        {
            suffix++;
        }

        int editStart = prefix;
        int editEndOld = oldText.Length - suffix;
        string replacement = next.Substring(prefix, next.Length - prefix - suffix);

        var updatedRuns = new List<RichDocRun>(Runs.Count);
        int pos = 0;
        bool inserted = false;

        foreach (var run in Runs)
        {
            int rStart = pos;
            int rLen = run.Text.Length;
            int rEnd = rStart + rLen;
            pos = rEnd;

            if (editStart == editEndOld)
            {
                // Pure insertion at editStart
                if (!inserted && editStart >= rStart && editStart <= rEnd)
                {
                    bool attachHere = editStart < rEnd || rLen == 0 || ReferenceEquals(run, Runs[^1]);
                    if (attachHere)
                    {
                        int localIdx = Math.Clamp(editStart - rStart, 0, rLen);
                        var clone = run.Clone();
                        clone.Text = run.Text.Insert(localIdx, replacement);
                        updatedRuns.Add(clone);
                        inserted = true;
                        continue;
                    }
                }

                updatedRuns.Add(run.Clone());
                continue;
            }

            if (rEnd <= editStart || rStart >= editEndOld)
            {
                updatedRuns.Add(run.Clone());
                continue;
            }

            string leftKeep = editStart > rStart ? run.Text[..(editStart - rStart)] : string.Empty;
            string rightKeep = editEndOld < rEnd ? run.Text[(editEndOld - rStart)..] : string.Empty;
            string combined = !inserted ? leftKeep + replacement + rightKeep : leftKeep + rightKeep;
            if (!inserted) inserted = true;

            if (combined.Length > 0)
            {
                var clone = run.Clone();
                clone.Text = combined;
                updatedRuns.Add(clone);
            }
        }

        if (!inserted && replacement.Length > 0)
        {
            var last = updatedRuns.Count > 0 ? updatedRuns[^1] : Runs[^1].Clone();
            last.Text += replacement;
            if (updatedRuns.Count == 0) updatedRuns.Add(last);
        }

        if (updatedRuns.Count == 0)
        {
            var fallback = Runs[0].Clone();
            fallback.Text = next;
            updatedRuns.Add(fallback);
        }

        Runs = updatedRuns;
    }

    /// <summary>
    /// Splits inline runs cleanly to apply or toggle formatting over a character sub-range.
    /// </summary>
    public void ApplyInlineFormatting(
        int startIndex,
        int length,
        bool? toggleBold = null,
        bool? toggleItalic = null,
        bool? toggleUnderline = null)
    {
        if (length <= 0 || Kind == RichDocBlockKind.Table) return;

        string fullText = Text;
        int clampedStart = Math.Clamp(startIndex, 0, fullText.Length);
        int clampedEnd = Math.Clamp(clampedStart + length, clampedStart, fullText.Length);
        if (clampedEnd <= clampedStart || Runs.Count == 0) return;

        var split = new List<RichDocRun>(Runs.Count + 2);
        var selectedRuns = new List<RichDocRun>();
        int pos = 0;

        foreach (var run in Runs)
        {
            int rStart = pos;
            int rLen = run.Text.Length;
            int rEnd = rStart + rLen;
            pos = rEnd;

            if (rLen == 0) continue;

            if (rEnd <= clampedStart || rStart >= clampedEnd)
            {
                split.Add(run.Clone());
                continue;
            }

            int cutA = Math.Max(rStart, clampedStart) - rStart;
            int cutB = Math.Min(rEnd, clampedEnd) - rStart;

            if (cutA > 0)
            {
                var left = run.Clone();
                left.Text = run.Text[..cutA];
                split.Add(left);
            }

            var mid = run.Clone();
            mid.Text = run.Text[cutA..cutB];
            split.Add(mid);
            selectedRuns.Add(mid);

            if (cutB < rLen)
            {
                var right = run.Clone();
                right.Text = run.Text[cutB..];
                split.Add(right);
            }
        }

        if (selectedRuns.Count == 0) return;

        if (toggleBold == true)
        {
            bool target = !selectedRuns.All(r => r.IsBold);
            foreach (var r in selectedRuns) r.IsBold = target;
        }
        if (toggleItalic == true)
        {
            bool target = !selectedRuns.All(r => r.IsItalic);
            foreach (var r in selectedRuns) r.IsItalic = target;
        }
        if (toggleUnderline == true)
        {
            bool target = !selectedRuns.All(r => r.IsUnderline);
            foreach (var r in selectedRuns) r.IsUnderline = target;
        }

        Runs = MergeAdjacentCompatibleRuns(split);
    }

    private static List<RichDocRun> MergeAdjacentCompatibleRuns(List<RichDocRun> runs)
    {
        if (runs.Count <= 1) return runs;
        var merged = new List<RichDocRun>(runs.Count);
        foreach (var r in runs)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                if (prev.IsBold == r.IsBold &&
                    prev.IsItalic == r.IsItalic &&
                    prev.IsUnderline == r.IsUnderline &&
                    string.Equals(prev.HyperlinkUri, r.HyperlinkUri, StringComparison.Ordinal) &&
                    string.Equals(prev.OriginalRunPropertiesXml, r.OriginalRunPropertiesXml, StringComparison.Ordinal))
                {
                    prev.Text += r.Text;
                    continue;
                }
            }
            merged.Add(r);
        }
        return merged;
    }

    public bool ContentEquals(RichDocBlock? other)
    {
        if (other is null || Kind != other.Kind) return false;
        if (Kind == RichDocBlockKind.Table)
        {
            if (TableRows.Count != other.TableRows.Count) return false;
            for (int r = 0; r < TableRows.Count; r++)
            {
                if (TableRows[r].Count != other.TableRows[r].Count) return false;
                for (int c = 0; c < TableRows[r].Count; c++)
                {
                    if (!string.Equals(TableRows[r][c], other.TableRows[r][c], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        if (Runs.Count != other.Runs.Count) return false;
        for (int i = 0; i < Runs.Count; i++)
        {
            if (!Runs[i].ContentEquals(other.Runs[i])) return false;
        }
        return IsBold == other.IsBold && IsItalic == other.IsItalic && IsUnderline == other.IsUnderline;
    }

    public void SyncSavedState(RichDocBlock saved)
    {
        SourceBodyElementIndex = saved.SourceBodyElementIndex;
        BaselineSnapshot = Clone(includeBaseline: false);
    }

    public RichDocBlock Clone(bool includeBaseline = true)
    {
        var copy = new RichDocBlock
        {
            Id = Id,
            _kind = _kind,
            _defaultBold = _defaultBold,
            _defaultItalic = _defaultItalic,
            _defaultUnderline = _defaultUnderline,
            SourceBodyElementIndex = SourceBodyElementIndex,
            HasEmbeddedNonTextElements = HasEmbeddedNonTextElements,
            Runs = Runs.Select(r => r.Clone()).ToList(),
            TableRows = TableRows.Select(r => new List<string>(r)).ToList()
        };
        if (includeBaseline && BaselineSnapshot is not null)
        {
            copy.BaselineSnapshot = BaselineSnapshot.Clone(includeBaseline: false);
        }
        return copy;
    }
}

public sealed class RichDocModel
{
    public List<RichDocBlock> Blocks { get; } = [];
    public byte[]? OriginalPackageBytes { get; set; }
    public bool HasUnsupportedElements { get; set; }
    public string? UnsupportedElementsWarning { get; set; }

    public RichDocModel Clone()
    {
        var copy = new RichDocModel
        {
            OriginalPackageBytes = OriginalPackageBytes,
            HasUnsupportedElements = HasUnsupportedElements,
            UnsupportedElementsWarning = UnsupportedElementsWarning
        };
        foreach (var b in Blocks)
        {
            copy.Blocks.Add(b.Clone());
        }
        return copy;
    }

    public int WordCount => Blocks.Sum(b => b.Kind == RichDocBlockKind.Table
        ? b.TableRows.SelectMany(r => r).Sum(CountWords)
        : CountWords(b.Text));

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }
}

public static class RichDocService
{
    public static Task<RichDocModel> LoadAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(filePath);
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(ms, isEditable: false);

            var model = new RichDocModel { OriginalPackageBytes = bytes };
            var mainPart = doc.MainDocumentPart;
            var body = mainPart?.Document?.Body;
            if (body is null)
            {
                model.Blocks.Add(new RichDocBlock { Kind = RichDocBlockKind.Paragraph });
                return model;
            }

            var numberingFormats = ReadNumberingFormats(mainPart?.NumberingDefinitionsPart);
            var hyperlinkMap = BuildHyperlinkUriMap(mainPart);
            var detectedUnsupported = new HashSet<string>(StringComparer.Ordinal);

            int bodyIdx = 0;
            foreach (var child in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();
                if (child is Paragraph p)
                {
                    var block = ParseParagraph(p, bodyIdx, numberingFormats, hyperlinkMap, detectedUnsupported);
                    block.BaselineSnapshot = block.Clone(includeBaseline: false);
                    model.Blocks.Add(block);
                }
                else if (child is Table tbl)
                {
                    var block = ParseTable(tbl, bodyIdx, detectedUnsupported);
                    block.BaselineSnapshot = block.Clone(includeBaseline: false);
                    model.Blocks.Add(block);
                }
                else if (child is not SectionProperties and not BookmarkStart and not BookmarkEnd)
                {
                    detectedUnsupported.Add(child.LocalName);
                }
                bodyIdx++;
            }

            if (detectedUnsupported.Count > 0)
            {
                model.HasUnsupportedElements = true;
                model.UnsupportedElementsWarning =
                    $"Document contains complex Word elements ({string.Join(", ", detectedUnsupported.OrderBy(s => s))}). " +
                    "Unmodified sections are preserved in place; edited blocks will keep surrounding elements intact.";
            }

            if (model.Blocks.Count == 0)
            {
                model.Blocks.Add(new RichDocBlock { Kind = RichDocBlockKind.Paragraph });
            }

            return model;
        }, ct);
    }

    public static Task SaveAtomicAsync(string targetFilePath, RichDocModel model, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(targetFilePath) ?? ".";
            Directory.CreateDirectory(dir);
            string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                if (model.OriginalPackageBytes is { Length: > 0 } origBytes)
                {
                    File.WriteAllBytes(tempPath, origBytes);
                    using var doc = WordprocessingDocument.Open(tempPath, isEditable: true);
                    var mainPart = doc.MainDocumentPart ?? doc.AddMainDocumentPart();
                    mainPart.Document ??= new Document(new Body());
                    var body = mainPart.Document.Body ?? mainPart.Document.AppendChild(new Body());

                    EnsureBasicStylesAndNumbering(mainPart);
                    DiffAndUpdateBodyInPlace(mainPart, body, model.Blocks, ct);
                    mainPart.Document.Save();
                }
                else
                {
                    using var doc = WordprocessingDocument.Create(tempPath, WordprocessingDocumentType.Document);
                    var mainPart = doc.AddMainDocumentPart();
                    mainPart.Document = new Document(new Body());
                    EnsureBasicStylesAndNumbering(mainPart);
                    AppendBlocksToBody(mainPart, mainPart.Document.Body!, model.Blocks);
                    mainPart.Document.Save();
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length == 0)
                {
                    throw new IOException("Atomic DOCX save verification failed.");
                }

                model.OriginalPackageBytes = File.ReadAllBytes(tempPath);
                foreach (var block in model.Blocks)
                {
                    block.BaselineSnapshot = block.Clone(includeBaseline: false);
                }

                File.Move(tempPath, targetFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                }
            }
        }, ct);
    }

    private static void DiffAndUpdateBodyInPlace(
        MainDocumentPart mainPart,
        Body body,
        IReadOnlyList<RichDocBlock> blocks,
        CancellationToken ct)
    {
        var originalChildren = body.ChildElements.ToList();
        var retainedIndices = new HashSet<int>();
        foreach (var block in blocks)
        {
            if (block.SourceBodyElementIndex is int srcIdx &&
                srcIdx >= 0 &&
                srcIdx < originalChildren.Count)
            {
                retainedIndices.Add(srcIdx);
            }
        }

        // Remove only tracked paragraphs/tables that were deleted by the user.
        for (int i = 0; i < originalChildren.Count; i++)
        {
            if (!retainedIndices.Contains(i) && originalChildren[i] is Paragraph or Table)
            {
                originalChildren[i].Remove();
            }
        }

        var sectPr = body.Elements<SectionProperties>().LastOrDefault();
        OpenXmlElement? lastPlaced = null;
        int nextBodyIdx = 0;

        for (int bIdx = 0; bIdx < blocks.Count; bIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var block = blocks[bIdx];
            OpenXmlElement? placed = null;

            if (block.SourceBodyElementIndex is int srcIdx &&
                srcIdx >= 0 &&
                srcIdx < originalChildren.Count)
            {
                var origEl = originalChildren[srcIdx];
                bool isUnmodified = block.BaselineSnapshot is not null && block.ContentEquals(block.BaselineSnapshot);

                if (isUnmodified &&
                    ((origEl is Paragraph && block.Kind != RichDocBlockKind.Table) ||
                     (origEl is Table && block.Kind == RichDocBlockKind.Table)))
                {
                    placed = origEl;
                }
                else if (origEl is Paragraph existingP && block.Kind != RichDocBlockKind.Table)
                {
                    UpdateParagraphInPlace(mainPart, existingP, block);
                    placed = existingP;
                }
                else if (origEl is Table existingTbl && block.Kind == RichDocBlockKind.Table)
                {
                    UpdateTableInPlace(existingTbl, block);
                    placed = existingTbl;
                }
                else
                {
                    placed = block.Kind == RichDocBlockKind.Table
                        ? BuildWordTable(block)
                        : BuildWordParagraph(mainPart, block);
                    origEl.InsertAfterSelf(placed);
                    origEl.Remove();
                }

                // If user reordered blocks cleanly, keep DOM order synchronized.
                if (lastPlaced is not null && placed.PreviousSibling() != lastPlaced && placed.Parent == body)
                {
                    // Move only if out of order relative to tracked neighbors.
                    if (!IsElementAfter(lastPlaced, placed))
                    {
                        placed.Remove();
                        lastPlaced.InsertAfterSelf(placed);
                    }
                }
            }
            else
            {
                placed = block.Kind == RichDocBlockKind.Table
                    ? BuildWordTable(block)
                    : BuildWordParagraph(mainPart, block);

                if (lastPlaced is not null && lastPlaced.Parent == body)
                {
                    lastPlaced.InsertAfterSelf(placed);
                }
                else if (sectPr is not null && sectPr.Parent == body)
                {
                    body.InsertBefore(placed, sectPr);
                }
                else
                {
                    body.AppendChild(placed);
                }
            }

            if (placed is not null)
            {
                lastPlaced = placed;
            }
        }

        // Refresh SourceBodyElementIndex for subsequent saves within the same session.
        var finalChildren = body.ChildElements.ToList();
        nextBodyIdx = 0;
        var blockSet = new HashSet<RichDocBlock>(blocks);
        for (int i = 0; i < blocks.Count; i++)
        {
            while (nextBodyIdx < finalChildren.Count && finalChildren[nextBodyIdx] is not Paragraph and not Table)
            {
                nextBodyIdx++;
            }
            if (nextBodyIdx < finalChildren.Count && blockSet.Contains(blocks[i]))
            {
                blocks[i].SourceBodyElementIndex = nextBodyIdx++;
            }
        }
    }

    private static bool IsElementAfter(OpenXmlElement earlier, OpenXmlElement candidate)
    {
        for (var cur = earlier.NextSibling(); cur is not null; cur = cur.NextSibling())
        {
            if (ReferenceEquals(cur, candidate)) return true;
        }
        return false;
    }

    private static Dictionary<string, string> BuildHyperlinkUriMap(MainDocumentPart? mainPart)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mainPart is null) return map;
        foreach (var rel in mainPart.HyperlinkRelationships)
        {
            map[rel.Id] = rel.Uri.IsAbsoluteUri ? rel.Uri.AbsoluteUri : rel.Uri.OriginalString;
        }
        return map;
    }

    private static Dictionary<int, bool> ReadNumberingFormats(NumberingDefinitionsPart? numberingPart)
    {
        var numIdToIsOrdered = new Dictionary<int, bool>();
        if (numberingPart?.Numbering is null) return numIdToIsOrdered;

        var abstractIsOrdered = new Dictionary<int, bool>();
        foreach (var abs in numberingPart.Numbering.Elements<AbstractNum>())
        {
            if (abs.AbstractNumberId?.Value is not int absId) continue;
            var lvl0 = abs.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == 0)
                       ?? abs.Elements<Level>().FirstOrDefault();
            var fmt = lvl0?.NumberingFormat?.Val?.Value;
            abstractIsOrdered[absId] = fmt is not null && fmt != NumberFormatValues.Bullet;
        }

        foreach (var inst in numberingPart.Numbering.Elements<NumberingInstance>())
        {
            if (inst.NumberID?.Value is int numId &&
                inst.AbstractNumId?.Val?.Value is int absId &&
                abstractIsOrdered.TryGetValue(absId, out bool ordered))
            {
                numIdToIsOrdered[numId] = ordered;
            }
        }
        return numIdToIsOrdered;
    }

    private static RichDocBlock ParseParagraph(
        Paragraph p,
        int bodyIndex,
        Dictionary<int, bool> numberingFormats,
        Dictionary<string, string> hyperlinkMap,
        HashSet<string> detectedUnsupported)
    {
        var pPr = p.ParagraphProperties;
        string? styleId = pPr?.ParagraphStyleId?.Val?.Value;

        var kind = styleId?.ToLowerInvariant() switch
        {
            "heading1" or "heading 1" or "title" => RichDocBlockKind.Heading1,
            "heading2" or "heading 2" or "subtitle" => RichDocBlockKind.Heading2,
            "heading3" or "heading 3" => RichDocBlockKind.Heading3,
            _ => RichDocBlockKind.Paragraph
        };

        if (kind == RichDocBlockKind.Paragraph && pPr?.NumberingProperties?.NumberingId?.Val?.Value is int numId)
        {
            bool isOrdered = numberingFormats.TryGetValue(numId, out bool ord) && ord;
            kind = isOrdered ? RichDocBlockKind.NumberedItem : RichDocBlockKind.BulletItem;
            if ((pPr.NumberingProperties.NumberingLevelReference?.Val?.Value ?? 0) > 0)
            {
                detectedUnsupported.Add("nested lists");
            }
        }

        var parsedRuns = new List<RichDocRun>();
        bool hasEmbeddedNonText = false;

        void CollectUnsupportedFromElement(OpenXmlElement el)
        {
            foreach (var d in el.Descendants())
            {
                switch (d)
                {
                    case Drawing:
                    case Picture:
                    case EmbeddedObject:
                        hasEmbeddedNonText = true;
                        detectedUnsupported.Add("embedded images/drawings");
                        break;
                    case CommentRangeStart:
                    case CommentReference:
                        hasEmbeddedNonText = true;
                        detectedUnsupported.Add("comments");
                        break;
                    case InsertedRun:
                    case DeletedRun:
                        hasEmbeddedNonText = true;
                        detectedUnsupported.Add("track changes");
                        break;
                    case FieldChar:
                        hasEmbeddedNonText = true;
                        detectedUnsupported.Add("complex fields");
                        break;
                }
            }
        }

        foreach (var child in p.ChildElements)
        {
            if (child is ParagraphProperties or BookmarkStart or BookmarkEnd or CommentRangeStart or CommentRangeEnd)
            {
                if (child is CommentRangeStart)
                {
                    hasEmbeddedNonText = true;
                    detectedUnsupported.Add("comments");
                }
                continue;
            }

            if (child is Run r)
            {
                CollectUnsupportedFromElement(r);
                if (TryParseWordRun(r, hyperlinkUri: null, out var parsed))
                {
                    parsedRuns.Add(parsed);
                }
            }
            else if (child is Hyperlink link)
            {
                string? uri = null;
                if (link.Id?.Value is { Length: > 0 } rId && hyperlinkMap.TryGetValue(rId, out var mappedUri))
                {
                    uri = mappedUri;
                }
                else if (link.Anchor?.Value is { Length: > 0 } anchor)
                {
                    uri = "#" + anchor;
                }

                foreach (var linkChild in link.Elements<Run>())
                {
                    CollectUnsupportedFromElement(linkChild);
                    if (TryParseWordRun(linkChild, uri, out var parsed))
                    {
                        parsedRuns.Add(parsed);
                    }
                }
            }
            else if (child is InsertedRun or DeletedRun)
            {
                hasEmbeddedNonText = true;
                detectedUnsupported.Add("track changes");
                if (child is InsertedRun)
                {
                    foreach (var insRun in child.Elements<Run>())
                    {
                        if (TryParseWordRun(insRun, hyperlinkUri: null, out var parsed))
                        {
                            parsedRuns.Add(parsed);
                        }
                    }
                }
            }
            else
            {
                hasEmbeddedNonText = true;
                CollectUnsupportedFromElement(child);
                detectedUnsupported.Add(child.LocalName);
            }
        }

        return new RichDocBlock
        {
            Kind = kind,
            SourceBodyElementIndex = bodyIndex,
            HasEmbeddedNonTextElements = hasEmbeddedNonText,
            Runs = parsedRuns
        };
    }

    private static bool TryParseWordRun(Run r, string? hyperlinkUri, out RichDocRun parsed)
    {
        var sb = new StringBuilder();
        bool hasTextNode = false;
        foreach (var rc in r.ChildElements)
        {
            if (rc is Text t)
            {
                sb.Append(t.Text);
                hasTextNode = true;
            }
            else if (rc is Break)
            {
                sb.Append('\n');
                hasTextNode = true;
            }
            else if (rc is TabChar)
            {
                sb.Append('\t');
                hasTextNode = true;
            }
        }

        if (!hasTextNode && r.Descendants<Drawing>().Any())
        {
            parsed = new RichDocRun();
            return false;
        }

        if (!hasTextNode && r.Descendants<Picture>().Any())
        {
            parsed = new RichDocRun();
            return false;
        }

        var rPr = r.RunProperties;
        bool isBold = rPr?.Bold is { } b && (b.Val is null || b.Val.Value);
        bool isItalic = rPr?.Italic is { } it && (it.Val is null || it.Val.Value);
        bool isUnderline = rPr?.Underline is not null && rPr.Underline.Val?.Value != UnderlineValues.None;

        parsed = new RichDocRun
        {
            Text = sb.ToString(),
            IsBold = isBold,
            IsItalic = isItalic,
            IsUnderline = isUnderline,
            HyperlinkUri = hyperlinkUri,
            OriginalRunPropertiesXml = rPr?.OuterXml
        };
        return hasTextNode || parsed.Text.Length > 0;
    }

    private static RichDocBlock ParseTable(Table tbl, int bodyIndex, HashSet<string> detectedUnsupported)
    {
        bool hasNonText = tbl.Descendants<Drawing>().Any() || tbl.Descendants<Picture>().Any();
        if (hasNonText)
        {
            detectedUnsupported.Add("embedded images/drawings");
        }

        var rows = new List<List<string>>();
        foreach (var tr in tbl.Elements<TableRow>())
        {
            var row = new List<string>();
            foreach (var tc in tr.Elements<TableCell>())
            {
                string cellText = string.Join("\n", tc.Elements<Paragraph>().Select(p =>
                    string.Concat(p.Descendants<Text>().Select(t => t.Text))));
                row.Add(cellText);
            }
            if (row.Count > 0) rows.Add(row);
        }

        if (rows.Count == 0)
        {
            rows.Add(["", ""]);
            rows.Add(["", ""]);
        }

        int maxCols = rows.Max(r => r.Count);
        foreach (var r in rows)
        {
            while (r.Count < maxCols) r.Add(string.Empty);
        }

        return new RichDocBlock
        {
            Kind = RichDocBlockKind.Table,
            SourceBodyElementIndex = bodyIndex,
            HasEmbeddedNonTextElements = hasNonText,
            TableRows = rows
        };
    }

    private static void EnsureBasicStylesAndNumbering(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.StyleDefinitionsPart ?? mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles ??= new Styles();

        EnsureHeadingStyle(stylesPart.Styles, "Heading1", "Heading 1", "32", true);
        EnsureHeadingStyle(stylesPart.Styles, "Heading2", "Heading 2", "26", true);
        EnsureHeadingStyle(stylesPart.Styles, "Heading3", "Heading 3", "22", true);
        stylesPart.Styles.Save();

        var numPart = mainPart.NumberingDefinitionsPart ?? mainPart.AddNewPart<NumberingDefinitionsPart>();
        numPart.Numbering ??= new Numbering();

        void EnsureNumberingDef(int id, NumberFormatValues fmt, string levelText)
        {
            if (!numPart.Numbering.Elements<AbstractNum>().Any(a => a.AbstractNumberId?.Value == id))
            {
                var abs = new AbstractNum(
                    new Level(
                        new NumberingFormat { Val = fmt },
                        new LevelText { Val = levelText })
                    { LevelIndex = 0 })
                { AbstractNumberId = id };

                var firstNum = numPart.Numbering.Elements<NumberingInstance>().FirstOrDefault();
                if (firstNum is not null)
                    numPart.Numbering.InsertBefore(abs, firstNum);
                else
                    numPart.Numbering.AppendChild(abs);
            }

            if (!numPart.Numbering.Elements<NumberingInstance>().Any(n => n.NumberID?.Value == id))
            {
                numPart.Numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = id }) { NumberID = id });
            }
        }

        EnsureNumberingDef(101, NumberFormatValues.Bullet, "•");
        EnsureNumberingDef(102, NumberFormatValues.Decimal, "%1.");

        numPart.Numbering.Save();
    }

    private static void EnsureHeadingStyle(Styles styles, string styleId, string name, string halfPointSize, bool bold)
    {
        if (styles.Elements<Style>().Any(s => string.Equals(s.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var rPr = new StyleRunProperties(new FontSize { Val = halfPointSize });
        if (bold) rPr.AppendChild(new Bold());

        styles.AppendChild(new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            rPr)
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        });
    }

    private static void AppendBlocksToBody(MainDocumentPart mainPart, Body body, IReadOnlyList<RichDocBlock> blocks)
    {
        foreach (var block in blocks)
        {
            OpenXmlElement child = block.Kind == RichDocBlockKind.Table
                ? BuildWordTable(block)
                : BuildWordParagraph(mainPart, block);
            body.AppendChild(child);
        }
    }

    private static Paragraph BuildWordParagraph(MainDocumentPart mainPart, RichDocBlock block)
    {
        var p = new Paragraph();
        var pPr = new ParagraphProperties();
        ApplyParagraphKindToProperties(pPr, block.Kind);
        if (pPr.HasChildren)
        {
            p.AppendChild(pPr);
        }

        foreach (var node in BuildInlineNodes(mainPart, block))
        {
            p.AppendChild(node);
        }

        return p;
    }

    private static void UpdateParagraphInPlace(MainDocumentPart mainPart, Paragraph p, RichDocBlock block)
    {
        // Only overwrite ParagraphProperties if block Kind changed from loaded baseline.
        if (block.BaselineSnapshot is null || block.Kind != block.BaselineSnapshot.Kind)
        {
            var pPr = p.ParagraphProperties ?? p.PrependChild(new ParagraphProperties());
            ApplyParagraphKindToProperties(pPr, block.Kind);
        }

        // Preserve non-text elements (drawings, bookmarks, comments, fields) while replacing text/hyperlink runs.
        OpenXmlElement? insertionAnchor = null;
        var toRemove = new List<OpenXmlElement>();

        foreach (var child in p.ChildElements)
        {
            if (child is Run r && IsPureTextRun(r))
            {
                insertionAnchor ??= child.PreviousSibling();
                toRemove.Add(child);
            }
            else if (child is Hyperlink)
            {
                insertionAnchor ??= child.PreviousSibling();
                toRemove.Add(child);
            }
            else if (child is Run mixedRun && !IsPureTextRun(mixedRun))
            {
                // Strip only text nodes from mixed run so embedded drawing/comment stays intact.
                foreach (var textEl in mixedRun.ChildElements.Where(x => x is Text or Break or TabChar).ToList())
                {
                    textEl.Remove();
                }
            }
        }

        foreach (var node in toRemove)
        {
            node.Remove();
        }

        var inlineNodes = BuildInlineNodes(mainPart, block);
        if (insertionAnchor is not null && insertionAnchor.Parent == p)
        {
            var cursor = insertionAnchor;
            foreach (var node in inlineNodes)
            {
                cursor.InsertAfterSelf(node);
                cursor = node;
            }
        }
        else if (p.ParagraphProperties is { } pPr && pPr.Parent == p)
        {
            var cursor = (OpenXmlElement)pPr;
            foreach (var node in inlineNodes)
            {
                cursor.InsertAfterSelf(node);
                cursor = node;
            }
        }
        else
        {
            foreach (var node in inlineNodes)
            {
                p.AppendChild(node);
            }
        }
    }

    private static bool IsPureTextRun(Run r)
    {
        return !r.Descendants<Drawing>().Any() &&
               !r.Descendants<Picture>().Any() &&
               !r.Descendants<EmbeddedObject>().Any() &&
               !r.Descendants<CommentReference>().Any() &&
               !r.Descendants<FieldChar>().Any();
    }

    private static void ApplyParagraphKindToProperties(ParagraphProperties pPr, RichDocBlockKind kind)
    {
        pPr.RemoveAllChildren<ParagraphStyleId>();
        pPr.RemoveAllChildren<NumberingProperties>();

        switch (kind)
        {
            case RichDocBlockKind.Heading1:
                pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Heading1" };
                break;
            case RichDocBlockKind.Heading2:
                pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Heading2" };
                break;
            case RichDocBlockKind.Heading3:
                pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Heading3" };
                break;
            case RichDocBlockKind.BulletItem:
                pPr.NumberingProperties = new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 101 });
                break;
            case RichDocBlockKind.NumberedItem:
                pPr.NumberingProperties = new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 102 });
                break;
        }
    }

    private static List<OpenXmlElement> BuildInlineNodes(MainDocumentPart mainPart, RichDocBlock block)
    {
        var runs = block.Runs.Count > 0
            ? block.Runs
            :
            [
                new RichDocRun
                {
                    Text = block.Text,
                    IsBold = block.IsBold,
                    IsItalic = block.IsItalic,
                    IsUnderline = block.IsUnderline
                }
            ];

        var nodes = new List<OpenXmlElement>(runs.Count);
        Hyperlink? currentHyperlink = null;
        string? currentHyperlinkUri = null;

        foreach (var runModel in runs)
        {
            var wordRun = BuildWordRun(runModel);
            if (!string.IsNullOrWhiteSpace(runModel.HyperlinkUri))
            {
                if (currentHyperlink is not null &&
                    string.Equals(currentHyperlinkUri, runModel.HyperlinkUri, StringComparison.Ordinal))
                {
                    currentHyperlink.AppendChild(wordRun);
                }
                else
                {
                    currentHyperlink = WrapInHyperlink(mainPart, runModel.HyperlinkUri, wordRun);
                    currentHyperlinkUri = runModel.HyperlinkUri;
                    nodes.Add(currentHyperlink);
                }
            }
            else
            {
                currentHyperlink = null;
                currentHyperlinkUri = null;
                nodes.Add(wordRun);
            }
        }

        return nodes;
    }

    private static Hyperlink WrapInHyperlink(MainDocumentPart mainPart, string uriString, Run wordRun)
    {
        if (uriString.StartsWith('#') && uriString.Length > 1)
        {
            return new Hyperlink(wordRun) { Anchor = uriString[1..], History = true };
        }

        if (Uri.TryCreate(uriString, UriKind.RelativeOrAbsolute, out var targetUri))
        {
            var existing = mainPart.HyperlinkRelationships.FirstOrDefault(r =>
                string.Equals(r.Uri.IsAbsoluteUri ? r.Uri.AbsoluteUri : r.Uri.OriginalString,
                    targetUri.IsAbsoluteUri ? targetUri.AbsoluteUri : targetUri.OriginalString,
                    StringComparison.Ordinal));
            var rel = existing ?? mainPart.AddHyperlinkRelationship(targetUri, isExternal: targetUri.IsAbsoluteUri);
            return new Hyperlink(wordRun) { Id = rel.Id, History = true };
        }

        return new Hyperlink(wordRun) { Anchor = uriString, History = true };
    }

    private static Run BuildWordRun(RichDocRun runModel)
    {
        var run = new Run();
        RunProperties rPr;
        bool hadOrigBold = false;
        bool hadOrigItalic = false;
        bool hadOrigUnderline = false;

        if (!string.IsNullOrWhiteSpace(runModel.OriginalRunPropertiesXml))
        {
            rPr = new RunProperties(runModel.OriginalRunPropertiesXml);
            hadOrigBold = rPr.Bold is { } ob && (ob.Val is null || ob.Val.Value);
            hadOrigItalic = rPr.Italic is { } oi && (oi.Val is null || oi.Val.Value);
            hadOrigUnderline = rPr.Underline is not null && rPr.Underline.Val?.Value != UnderlineValues.None;
        }
        else
        {
            rPr = new RunProperties();
        }

        if (string.IsNullOrWhiteSpace(runModel.OriginalRunPropertiesXml) || hadOrigBold != runModel.IsBold)
        {
            rPr.RemoveAllChildren<Bold>();
            if (runModel.IsBold) rPr.AppendChild(new Bold());
        }

        if (string.IsNullOrWhiteSpace(runModel.OriginalRunPropertiesXml) || hadOrigItalic != runModel.IsItalic)
        {
            rPr.RemoveAllChildren<Italic>();
            if (runModel.IsItalic) rPr.AppendChild(new Italic());
        }

        if (string.IsNullOrWhiteSpace(runModel.OriginalRunPropertiesXml) || hadOrigUnderline != runModel.IsUnderline)
        {
            rPr.RemoveAllChildren<Underline>();
            if (runModel.IsUnderline) rPr.AppendChild(new Underline { Val = UnderlineValues.Single });
        }

        if (rPr.HasChildren)
        {
            run.AppendChild(rPr);
        }

        string normalized = (runModel.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            if (li > 0) run.AppendChild(new Break());
            string[] tabParts = lines[li].Split('\t');
            for (int ti = 0; ti < tabParts.Length; ti++)
            {
                if (ti > 0) run.AppendChild(new TabChar());
                if (tabParts[ti].Length > 0 || (lines.Length == 1 && tabParts.Length == 1))
                {
                    run.AppendChild(new Text(tabParts[ti]) { Space = SpaceProcessingModeValues.Preserve });
                }
            }
        }

        return run;
    }

    private static void UpdateTableInPlace(Table tbl, RichDocBlock block)
    {
        var baselineRows = block.BaselineSnapshot?.TableRows;
        var existingRows = tbl.Elements<TableRow>().ToList();

        for (int r = 0; r < block.TableRows.Count; r++)
        {
            var modelRow = block.TableRows[r];
            TableRow tr;
            if (r < existingRows.Count)
            {
                tr = existingRows[r];
            }
            else
            {
                tr = new TableRow();
                tbl.AppendChild(tr);
            }

            var existingCells = tr.Elements<TableCell>().ToList();
            for (int c = 0; c < modelRow.Count; c++)
            {
                string cellText = modelRow[c] ?? string.Empty;
                bool cellUnmodified = baselineRows is not null &&
                                      r < baselineRows.Count &&
                                      c < baselineRows[r].Count &&
                                      string.Equals(cellText, baselineRows[r][c], StringComparison.Ordinal);

                if (c < existingCells.Count)
                {
                    if (cellUnmodified) continue;
                    UpdateTableCellInPlace(existingCells[c], cellText);
                }
                else
                {
                    tr.AppendChild(BuildWordTableCell(cellText));
                }
            }

            for (int c = existingCells.Count - 1; c >= modelRow.Count; c--)
            {
                existingCells[c].Remove();
            }
        }

        for (int r = existingRows.Count - 1; r >= block.TableRows.Count; r--)
        {
            existingRows[r].Remove();
        }
    }

    private static void UpdateTableCellInPlace(TableCell tc, string cellText)
    {
        var firstParagraph = tc.Elements<Paragraph>().FirstOrDefault();
        var preservedPPr = firstParagraph?.ParagraphProperties?.CloneNode(true);
        var preservedRPr = firstParagraph?.Elements<Run>().FirstOrDefault()?.RunProperties?.CloneNode(true);

        foreach (var oldP in tc.Elements<Paragraph>().ToList())
        {
            oldP.Remove();
        }

        string[] lines = cellText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var p = new Paragraph();
            if (i == 0 && preservedPPr is not null) p.AppendChild(preservedPPr);
            var run = new Run();
            if (preservedRPr is not null) run.AppendChild(preservedRPr.CloneNode(true));
            run.AppendChild(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            p.AppendChild(run);
            tc.AppendChild(p);
        }
    }

    private static TableCell BuildWordTableCell(string cellText) =>
        new(
            new Paragraph(
                new Run(
                    new Text(cellText) { Space = SpaceProcessingModeValues.Preserve })));

    private static Table BuildWordTable(RichDocBlock block)
    {
        var tbl = new Table(
            new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        foreach (var rowData in block.TableRows)
        {
            var tr = new TableRow();
            foreach (var cellVal in rowData)
            {
                tr.AppendChild(BuildWordTableCell(cellVal ?? string.Empty));
            }
            tbl.AppendChild(tr);
        }

        return tbl;
    }
}
