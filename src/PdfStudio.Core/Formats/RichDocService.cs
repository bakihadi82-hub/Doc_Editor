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

public sealed class RichDocBlock
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RichDocBlockKind Kind { get; set; } = RichDocBlockKind.Paragraph;
    public string Text { get; set; } = string.Empty;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public List<List<string>> TableRows { get; set; } = [];

    public RichDocBlock Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Text = Text,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsUnderline = IsUnderline,
        TableRows = TableRows.Select(r => new List<string>(r)).ToList()
    };
}

public sealed class RichDocModel
{
    public List<RichDocBlock> Blocks { get; } = [];
    public byte[]? OriginalPackageBytes { get; set; }

    public RichDocModel Clone()
    {
        var copy = new RichDocModel { OriginalPackageBytes = OriginalPackageBytes };
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

            foreach (var child in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();
                if (child is Paragraph p)
                {
                    model.Blocks.Add(ParseParagraph(p, numberingFormats));
                }
                else if (child is Table tbl)
                {
                    model.Blocks.Add(ParseTable(tbl));
                }
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

                    var sectPr = body.Elements<SectionProperties>().LastOrDefault()?.CloneNode(true);
                    body.RemoveAllChildren();

                    EnsureBasicStylesAndNumbering(mainPart);
                    AppendBlocksToBody(body, model.Blocks);

                    if (sectPr is not null)
                    {
                        body.AppendChild(sectPr);
                    }

                    mainPart.Document.Save();
                }
                else
                {
                    using var doc = WordprocessingDocument.Create(tempPath, WordprocessingDocumentType.Document);
                    var mainPart = doc.AddMainDocumentPart();
                    mainPart.Document = new Document(new Body());
                    EnsureBasicStylesAndNumbering(mainPart);
                    AppendBlocksToBody(mainPart.Document.Body!, model.Blocks);
                    mainPart.Document.Save();
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length == 0)
                {
                    throw new IOException("Atomic DOCX save verification failed.");
                }

                model.OriginalPackageBytes = File.ReadAllBytes(tempPath);
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

    private static RichDocBlock ParseParagraph(Paragraph p, Dictionary<int, bool> numberingFormats)
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
        }

        var runs = p.Elements<Run>().ToList();
        bool bold = runs.Count > 0 && runs.All(r => r.RunProperties?.Bold is { } b && (b.Val is null || b.Val.Value));
        bool italic = runs.Count > 0 && runs.All(r => r.RunProperties?.Italic is { } it && (it.Val is null || it.Val.Value));
        bool underline = runs.Count > 0 && runs.All(r => r.RunProperties?.Underline is not null &&
                                                       r.RunProperties.Underline.Val?.Value != UnderlineValues.None);

        var sb = new System.Text.StringBuilder();
        foreach (var child in p.Descendants())
        {
            if (child is Text t) sb.Append(t.Text);
            else if (child is Break) sb.Append('\n');
        }
        string text = sb.ToString();

        return new RichDocBlock
        {
            Kind = kind,
            Text = text,
            IsBold = bold || kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2,
            IsItalic = italic,
            IsUnderline = underline
        };
    }

    private static RichDocBlock ParseTable(Table tbl)
    {
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

    private static void AppendBlocksToBody(Body body, IReadOnlyList<RichDocBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.Kind == RichDocBlockKind.Table)
            {
                body.AppendChild(BuildWordTable(block));
                continue;
            }

            var p = new Paragraph();
            var pPr = new ParagraphProperties();

            switch (block.Kind)
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

            if (pPr.HasChildren)
            {
                p.AppendChild(pPr);
            }

            var run = new Run();
            var rPr = new RunProperties();
            if (block.IsBold) rPr.AppendChild(new Bold());
            if (block.IsItalic) rPr.AppendChild(new Italic());
            if (block.IsUnderline) rPr.AppendChild(new Underline { Val = UnderlineValues.Single });
            if (rPr.HasChildren) run.AppendChild(rPr);

            string[] lines = (block.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) run.AppendChild(new Break());
                run.AppendChild(new Text(lines[li]) { Space = SpaceProcessingModeValues.Preserve });
            }
            p.AppendChild(run);
            body.AppendChild(p);
        }
    }

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
                var tc = new TableCell(
                    new Paragraph(
                        new Run(
                            new Text(cellVal ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve })));
                tr.AppendChild(tc);
            }
            tbl.AppendChild(tr);
        }

        return tbl;
    }
}
