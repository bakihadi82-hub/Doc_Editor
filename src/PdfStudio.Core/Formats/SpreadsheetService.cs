using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PdfStudio.Core.Formats;

/// <summary>
/// Per-cell OpenXML preservation metadata so edits round-trip styles, formulas, and date formats safely.
/// </summary>
public sealed class SpreadsheetCellMetadata
{
    public uint? StyleIndex { get; set; }
    public CellValues? OriginalDataType { get; set; }
    public string? OriginalRawValue { get; set; }
    public string? LoadedDisplayValue { get; set; }
    public string? FormulaText { get; set; }
    public CellFormulaValues? FormulaType { get; set; }
    public string? SharedFormulaReference { get; set; }
    public uint? SharedFormulaIndex { get; set; }
    public bool IsDateFormatted { get; set; }
    public string? NumberFormatCode { get; set; }
    public uint? NumberFormatId { get; set; }

    public SpreadsheetCellMetadata Clone() => new()
    {
        StyleIndex = StyleIndex,
        OriginalDataType = OriginalDataType,
        OriginalRawValue = OriginalRawValue,
        LoadedDisplayValue = LoadedDisplayValue,
        FormulaText = FormulaText,
        FormulaType = FormulaType,
        SharedFormulaReference = SharedFormulaReference,
        SharedFormulaIndex = SharedFormulaIndex,
        IsDateFormatted = IsDateFormatted,
        NumberFormatCode = NumberFormatCode,
        NumberFormatId = NumberFormatId
    };
}

/// <summary>
/// Column width definition preserved from &lt;x:cols&gt; in XLSX worksheets.
/// </summary>
public sealed class SpreadsheetColumnWidth
{
    public uint Min { get; set; }
    public uint Max { get; set; }
    public double Width { get; set; }
    public bool CustomWidth { get; set; }
    public bool BestFit { get; set; }
    public bool Hidden { get; set; }
    public uint? StyleIndex { get; set; }

    public SpreadsheetColumnWidth Clone() => new()
    {
        Min = Min,
        Max = Max,
        Width = Width,
        CustomWidth = CustomWidth,
        BestFit = BestFit,
        Hidden = Hidden,
        StyleIndex = StyleIndex
    };
}

public sealed class SpreadsheetSheetData
{
    public string Name { get; set; } = "Sheet1";
    public List<List<string>> Rows { get; } = [];
    public List<List<SpreadsheetCellMetadata?>> CellMetadata { get; } = [];
    public List<SpreadsheetColumnWidth> ColumnWidths { get; } = [];
    public bool FormulaMutated { get; set; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public SpreadsheetCellMetadata? GetCellMetadata(int rowIndex, int colIndex)
    {
        if (rowIndex < 0 || rowIndex >= CellMetadata.Count) return null;
        var metaRow = CellMetadata[rowIndex];
        return colIndex >= 0 && colIndex < metaRow.Count ? metaRow[colIndex] : null;
    }

    public void SetCellMetadata(int rowIndex, int colIndex, SpreadsheetCellMetadata? metadata)
    {
        if (rowIndex < 0 || colIndex < 0) return;
        while (CellMetadata.Count <= rowIndex)
        {
            CellMetadata.Add([]);
        }
        var metaRow = CellMetadata[rowIndex];
        while (metaRow.Count <= colIndex)
        {
            metaRow.Add(null);
        }
        metaRow[colIndex] = metadata;
    }

    public double? GetColumnWidthPixels(int zeroBasedColIndex)
    {
        uint oneBased = (uint)(zeroBasedColIndex + 1);
        var match = ColumnWidths.FirstOrDefault(w => oneBased >= w.Min && oneBased <= w.Max && w.Width > 0);
        if (match is null) return null;
        return Math.Clamp(match.Width * 7.5 + 12.0, 48.0, 480.0);
    }

    public SpreadsheetSheetData Clone()
    {
        var copy = new SpreadsheetSheetData
        {
            Name = Name,
            FormulaMutated = FormulaMutated
        };
        foreach (var row in Rows)
        {
            copy.Rows.Add([.. row]);
        }
        foreach (var metaRow in CellMetadata)
        {
            copy.CellMetadata.Add(metaRow.Select(m => m?.Clone()).ToList());
        }
        foreach (var cw in ColumnWidths)
        {
            copy.ColumnWidths.Add(cw.Clone());
        }
        return copy;
    }
}

public sealed class SpreadsheetWorkbookData
{
    public List<SpreadsheetSheetData> Sheets { get; } = [];
    public byte[]? OriginalPackageBytes { get; set; }

    public SpreadsheetWorkbookData Clone()
    {
        var copy = new SpreadsheetWorkbookData
        {
            OriginalPackageBytes = OriginalPackageBytes
        };
        foreach (var sheet in Sheets)
        {
            copy.Sheets.Add(sheet.Clone());
        }
        return copy;
    }
}

public static class SpreadsheetService
{
    private sealed record ResolvedCellStyle(uint? NumFmtId, string? FormatCode, bool IsDateFormat);

    public static Task<SpreadsheetWorkbookData> LoadAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".csv" => LoadDelimited(filePath, ',', Path.GetFileNameWithoutExtension(filePath), ct),
                ".tsv" => LoadDelimited(filePath, '\t', Path.GetFileNameWithoutExtension(filePath), ct),
                _ => LoadXlsx(filePath, ct)
            };
        }, ct);
    }

    public static Task SaveAtomicAsync(
        string? sourceXlsxPath,
        string targetFilePath,
        SpreadsheetWorkbookData workbook,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(targetFilePath) ?? ".";
            Directory.CreateDirectory(dir);
            string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                string ext = Path.GetExtension(targetFilePath).ToLowerInvariant();
                if (ext == ".csv")
                {
                    SaveDelimited(tempPath, ',', workbook.Sheets.FirstOrDefault() ?? new SpreadsheetSheetData(), ct);
                }
                else if (ext == ".tsv")
                {
                    SaveDelimited(tempPath, '\t', workbook.Sheets.FirstOrDefault() ?? new SpreadsheetSheetData(), ct);
                }
                else
                {
                    SaveXlsx(sourceXlsxPath, tempPath, workbook, ct);
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists)
                {
                    throw new IOException("Atomic spreadsheet save verification failed.");
                }

                if (ext == ".xlsx")
                {
                    workbook.OriginalPackageBytes = File.ReadAllBytes(tempPath);
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

    private static SpreadsheetWorkbookData LoadDelimited(string filePath, char delimiter, string sheetName, CancellationToken ct)
    {
        var workbook = new SpreadsheetWorkbookData();
        var sheet = new SpreadsheetSheetData { Name = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName };

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);

        int maxCols = 0;
        var currentRow = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        int chInt;
        while ((chInt = reader.Read()) != -1)
        {
            ct.ThrowIfCancellationRequested();
            char ch = (char)chInt;

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else
            {
                if (ch == '"' && field.Length == 0)
                {
                    inQuotes = true;
                }
                else if (ch == delimiter)
                {
                    currentRow.Add(field.ToString());
                    field.Clear();
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                    }
                    currentRow.Add(field.ToString());
                    field.Clear();
                    if (currentRow.Count > maxCols) maxCols = currentRow.Count;
                    sheet.Rows.Add(currentRow);
                    currentRow = [];
                }
                else
                {
                    field.Append(ch);
                }
            }
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            if (currentRow.Count > maxCols) maxCols = currentRow.Count;
            sheet.Rows.Add(currentRow);
        }

        NormalizeSheetColumns(sheet, Math.Max(1, maxCols));
        workbook.Sheets.Add(sheet);
        return workbook;
    }

    private static void SaveDelimited(string tempPath, char delimiter, SpreadsheetSheetData sheet, CancellationToken ct)
    {
        using var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 65536);
        foreach (var row in sheet.Rows)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) writer.Write(delimiter);
                string cell = row[i] ?? string.Empty;
                bool needsQuotes = cell.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0;
                if (needsQuotes)
                {
                    writer.Write('"');
                    writer.Write(cell.Replace("\"", "\"\""));
                    writer.Write('"');
                }
                else
                {
                    writer.Write(cell);
                }
            }
            writer.WriteLine();
        }
    }

    private static SpreadsheetWorkbookData LoadXlsx(string filePath, CancellationToken ct)
    {
        byte[] packageBytes = File.ReadAllBytes(filePath);
        var workbook = new SpreadsheetWorkbookData { OriginalPackageBytes = packageBytes };
        using var ms = new MemoryStream(packageBytes, writable: false);
        using var doc = SpreadsheetDocument.Open(ms, isEditable: false);

        var wbPart = doc.WorkbookPart;
        if (wbPart?.Workbook?.Sheets is null)
        {
            workbook.Sheets.Add(new SpreadsheetSheetData { Name = "Sheet1" });
            return workbook;
        }

        var sst = wbPart.SharedStringTablePart?.SharedStringTable;
        var sharedStrings = sst is null
            ? Array.Empty<string>()
            : sst.Elements<SharedStringItem>().Select(item => item.InnerText).ToArray();

        var styleLookup = BuildStyleLookup(wbPart.WorkbookStylesPart?.Stylesheet);

        foreach (var sheetEl in wbPart.Workbook.Sheets.Elements<Sheet>())
        {
            ct.ThrowIfCancellationRequested();
            string name = sheetEl.Name?.Value ?? "Sheet";
            string? relId = sheetEl.Id?.Value;
            if (string.IsNullOrEmpty(relId) || wbPart.GetPartById(relId) is not WorksheetPart wsPart)
            {
                continue;
            }

            var sheetDataModel = new SpreadsheetSheetData { Name = name };
            var colsEl = wsPart.Worksheet?.GetFirstChild<Columns>();
            if (colsEl is not null)
            {
                foreach (var col in colsEl.Elements<Column>())
                {
                    if (col.Min?.Value is uint min && col.Max?.Value is uint max && col.Width?.Value is double width)
                    {
                        sheetDataModel.ColumnWidths.Add(new SpreadsheetColumnWidth
                        {
                            Min = min,
                            Max = max,
                            Width = width,
                            CustomWidth = col.CustomWidth?.Value ?? false,
                            BestFit = col.BestFit?.Value ?? false,
                            Hidden = col.Hidden?.Value ?? false,
                            StyleIndex = col.Style?.Value
                        });
                    }
                }
            }

            var sheetData = wsPart.Worksheet?.GetFirstChild<SheetData>();
            int maxCols = 0;

            if (sheetData is not null)
            {
                uint expectedRowIndex = 1;
                foreach (var row in sheetData.Elements<Row>())
                {
                    ct.ThrowIfCancellationRequested();
                    uint rowIdx = row.RowIndex?.Value ?? expectedRowIndex;
                    while (expectedRowIndex < rowIdx)
                    {
                        sheetDataModel.Rows.Add([]);
                        sheetDataModel.CellMetadata.Add([]);
                        expectedRowIndex++;
                    }
                    expectedRowIndex = rowIdx + 1;

                    var rowCells = new List<string>();
                    var rowMeta = new List<SpreadsheetCellMetadata?>();
                    int sequentialCol = 0;
                    foreach (var cell in row.Elements<Cell>())
                    {
                        int colIdx = cell.CellReference?.Value is { Length: > 0 } cellRef
                            ? ParseColumnIndex(cellRef)
                            : sequentialCol;
                        sequentialCol = colIdx + 1;

                        while (rowCells.Count <= colIdx)
                        {
                            rowCells.Add(string.Empty);
                            rowMeta.Add(null);
                        }

                        var (display, meta) = ReadCellWithMetadata(cell, sharedStrings, styleLookup);
                        rowCells[colIdx] = display;
                        rowMeta[colIdx] = meta;
                    }

                    if (rowCells.Count > maxCols) maxCols = rowCells.Count;
                    sheetDataModel.Rows.Add(rowCells);
                    sheetDataModel.CellMetadata.Add(rowMeta);
                }
            }

            NormalizeSheetColumns(sheetDataModel, Math.Max(1, maxCols));
            workbook.Sheets.Add(sheetDataModel);
        }

        if (workbook.Sheets.Count == 0)
        {
            workbook.Sheets.Add(new SpreadsheetSheetData { Name = "Sheet1" });
        }

        return workbook;
    }

    private static List<ResolvedCellStyle> BuildStyleLookup(Stylesheet? stylesheet)
    {
        var list = new List<ResolvedCellStyle>();
        if (stylesheet?.CellFormats is null) return list;

        var customFormats = new Dictionary<uint, string>();
        if (stylesheet.NumberingFormats is not null)
        {
            foreach (var nf in stylesheet.NumberingFormats.Elements<NumberingFormat>())
            {
                if (nf.NumberFormatId?.Value is uint nfId && nf.FormatCode?.Value is { } code)
                {
                    customFormats[nfId] = code;
                }
            }
        }

        foreach (var cf in stylesheet.CellFormats.Elements<CellFormat>())
        {
            uint? numFmtId = cf.NumberFormatId?.Value;
            bool applyNumFmt = cf.ApplyNumberFormat?.Value ?? (numFmtId is not null);
            string? formatCode = numFmtId is uint id && customFormats.TryGetValue(id, out var found) ? found : null;
            bool isDate = applyNumFmt && numFmtId is not null && IsExcelDateFormat(numFmtId.Value, formatCode);
            list.Add(new ResolvedCellStyle(numFmtId, formatCode, isDate));
        }

        return list;
    }

    private static bool IsExcelDateFormat(uint numFmtId, string? formatCode)
    {
        if ((numFmtId >= 14 && numFmtId <= 22) ||
            (numFmtId >= 27 && numFmtId <= 36) ||
            (numFmtId >= 45 && numFmtId <= 47) ||
            (numFmtId >= 50 && numFmtId <= 58))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(formatCode)) return false;

        string section = ExtractFirstFormatSection(formatCode);
        string cleaned = StripFormatLiteralsAndBrackets(section).ToLowerInvariant();
        if (cleaned.Length == 0) return false;

        bool hasDateToken = cleaned.Contains('y') || cleaned.Contains('d');
        bool hasTimePair = (cleaned.Contains('h') && (cleaned.Contains('m') || cleaned.Contains('s'))) ||
                           (cleaned.Contains('m') && cleaned.Contains('s')) ||
                           cleaned.Contains("am/pm") ||
                           cleaned.Contains("a/p");
        return hasDateToken || hasTimePair;
    }

    private static string ExtractFirstFormatSection(string formatCode)
    {
        bool inQuotes = false;
        for (int i = 0; i < formatCode.Length; i++)
        {
            char c = formatCode[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == '\\') i++;
            else if (c == ';' && !inQuotes) return formatCode[..i];
        }
        return formatCode;
    }

    private static string StripFormatLiteralsAndBrackets(string section)
    {
        var sb = new StringBuilder(section.Length);
        bool inQuotes = false;
        bool inBrackets = false;

        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            if (inQuotes)
            {
                if (c == '"') inQuotes = false;
                continue;
            }
            if (inBrackets)
            {
                if (c == ']') inBrackets = false;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == '[')
            {
                inBrackets = true;
            }
            else if (c == '\\')
            {
                i++;
            }
            else if (c == '_' || c == '*')
            {
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string FormatExcelSerialDate(double serial, uint? numFmtId, string? formatCode)
    {
        if (double.IsNaN(serial) || double.IsInfinity(serial) || serial < -657434 || serial >= 2958466)
        {
            return serial.ToString(CultureInfo.InvariantCulture);
        }

        DateTime dt;
        try
        {
            dt = DateTime.FromOADate(serial);
        }
        catch (ArgumentException)
        {
            return serial.ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(formatCode) && numFmtId is uint id)
        {
            string builtInPattern = id switch
            {
                14 => "yyyy-MM-dd",
                15 => "d-MMM-yy",
                16 => "d-MMM",
                17 => "MMM-yy",
                18 => "h:mm tt",
                19 => "h:mm:ss tt",
                20 => "HH:mm",
                21 => "HH:mm:ss",
                22 => "yyyy-MM-dd HH:mm",
                45 => "mm:ss",
                46 => "HH:mm:ss",
                47 => "mm:ss.0",
                _ => "yyyy-MM-dd"
            };
            return dt.ToString(builtInPattern, CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(formatCode))
        {
            string dotNetPattern = TranslateExcelDateFormatToDotNet(formatCode);
            if (!string.IsNullOrEmpty(dotNetPattern))
            {
                try
                {
                    return dt.ToString(dotNetPattern, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    // Fallback cleanly below
                }
            }
        }

        return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string TranslateExcelDateFormatToDotNet(string excelFormat)
    {
        string section = ExtractFirstFormatSection(excelFormat);
        bool hasAmPm = section.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) ||
                       section.Contains("A/P", StringComparison.OrdinalIgnoreCase);

        // First tokenize while keeping quoted literals and skipping [locale/color] directives
        var tokens = new List<(bool IsLiteral, string Text)>();
        int i = 0;
        while (i < section.Length)
        {
            char c = section[i];
            if (c == '[')
            {
                int close = section.IndexOf(']', i + 1);
                i = close >= 0 ? close + 1 : section.Length;
                continue;
            }
            if (c == '"')
            {
                int start = i + 1;
                int close = section.IndexOf('"', start);
                string lit = close >= 0 ? section[start..close] : section[start..];
                tokens.Add((true, lit));
                i = close >= 0 ? close + 1 : section.Length;
                continue;
            }
            if (c == '\\' && i + 1 < section.Length)
            {
                tokens.Add((true, section[i + 1].ToString()));
                i += 2;
                continue;
            }
            if ((c == '_' || c == '*') && i + 1 < section.Length)
            {
                i += 2;
                continue;
            }

            if (i + 5 <= section.Length && section.AsSpan(i, 5).Equals("AM/PM", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add((false, "tt"));
                i += 5;
                continue;
            }
            if (i + 3 <= section.Length && section.AsSpan(i, 3).Equals("A/P", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add((false, "tt"));
                i += 3;
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (lower is 'y' or 'm' or 'd' or 'h' or 's')
            {
                int start = i;
                while (i < section.Length && char.ToLowerInvariant(section[i]) == lower)
                {
                    i++;
                }
                tokens.Add((false, section[start..i].ToLowerInvariant()));
                continue;
            }

            tokens.Add((true, c.ToString()));
            i++;
        }

        var result = new StringBuilder();
        for (int t = 0; t < tokens.Count; t++)
        {
            var (isLit, text) = tokens[t];
            if (isLit)
            {
                if (text.Length == 1 && ":/-., ".Contains(text[0]))
                {
                    result.Append(text);
                }
                else
                {
                    result.Append('\'').Append(text.Replace("'", "''")).Append('\'');
                }
                continue;
            }

            char kind = text[0];
            if (kind == 'y')
            {
                result.Append(text.Length >= 4 ? "yyyy" : "yy");
            }
            else if (kind == 'd')
            {
                result.Append(text.Length switch
                {
                    >= 4 => "dddd",
                    3 => "ddd",
                    2 => "dd",
                    _ => "d"
                });
            }
            else if (kind == 'h')
            {
                result.Append(hasAmPm ? (text.Length >= 2 ? "hh" : "h") : (text.Length >= 2 ? "HH" : "H"));
            }
            else if (kind == 's')
            {
                result.Append(text.Length >= 2 ? "ss" : "s");
            }
            else if (kind == 'm')
            {
                bool isMinute = IsPrecededByHourToken(tokens, t) || IsFollowedBySecondToken(tokens, t);
                if (isMinute)
                {
                    result.Append(text.Length >= 2 ? "mm" : "m");
                }
                else
                {
                    result.Append(text.Length switch
                    {
                        >= 4 => "MMMM",
                        3 => "MMM",
                        2 => "MM",
                        _ => "M"
                    });
                }
            }
            else
            {
                result.Append(text);
            }
        }

        return result.ToString();
    }

    private static bool IsPrecededByHourToken(List<(bool IsLiteral, string Text)> tokens, int idx)
    {
        for (int i = idx - 1; i >= 0; i--)
        {
            if (tokens[i].IsLiteral) continue;
            return tokens[i].Text.StartsWith('h');
        }
        return false;
    }

    private static bool IsFollowedBySecondToken(List<(bool IsLiteral, string Text)> tokens, int idx)
    {
        for (int i = idx + 1; i < tokens.Count; i++)
        {
            if (tokens[i].IsLiteral) continue;
            return tokens[i].Text.StartsWith('s');
        }
        return false;
    }

    private static (string DisplayValue, SpreadsheetCellMetadata Metadata) ReadCellWithMetadata(
        Cell cell,
        string[] sharedStrings,
        List<ResolvedCellStyle> styleLookup)
    {
        uint? styleIdx = cell.StyleIndex?.Value;
        ResolvedCellStyle? resolvedStyle = styleIdx is uint sIdx && sIdx < styleLookup.Count
            ? styleLookup[(int)sIdx]
            : null;

        string? raw = cell.CellValue?.InnerText;
        string display;
        bool isDate = resolvedStyle?.IsDateFormat == true;

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            isDate = false;
            if (raw is not null &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ssIdx) &&
                ssIdx >= 0 && ssIdx < sharedStrings.Length)
            {
                display = sharedStrings[ssIdx];
            }
            else
            {
                display = raw ?? string.Empty;
            }
        }
        else if (cell.DataType?.Value == CellValues.InlineString)
        {
            isDate = false;
            raw = cell.InlineString?.InnerText;
            display = raw ?? string.Empty;
        }
        else if (cell.DataType?.Value == CellValues.Boolean)
        {
            isDate = false;
            display = raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw ?? string.Empty;
        }
        else if (cell.DataType?.Value == CellValues.String || cell.DataType?.Value == CellValues.Error)
        {
            isDate = false;
            display = raw ?? string.Empty;
        }
        else if (isDate &&
                 raw is not null &&
                 double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double oaDate))
        {
            display = FormatExcelSerialDate(oaDate, resolvedStyle?.NumFmtId, resolvedStyle?.FormatCode);
        }
        else if (cell.CellFormula is { Text: { Length: > 0 } formulaStr } && string.IsNullOrEmpty(raw))
        {
            display = "=" + formulaStr;
        }
        else if (raw is null && cell.InlineString is null)
        {
            display = cell.InnerText ?? string.Empty;
        }
        else
        {
            display = raw ?? string.Empty;
        }

        var meta = new SpreadsheetCellMetadata
        {
            StyleIndex = styleIdx,
            OriginalDataType = cell.DataType?.Value,
            OriginalRawValue = raw,
            LoadedDisplayValue = display,
            FormulaText = cell.CellFormula?.Text,
            FormulaType = cell.CellFormula?.FormulaType?.Value,
            SharedFormulaReference = cell.CellFormula?.Reference?.Value,
            SharedFormulaIndex = cell.CellFormula?.SharedIndex?.Value,
            IsDateFormatted = isDate,
            NumberFormatCode = resolvedStyle?.FormatCode,
            NumberFormatId = resolvedStyle?.NumFmtId
        };

        return (display, meta);
    }

    private static void SaveXlsx(string? sourceXlsxPath, string tempPath, SpreadsheetWorkbookData workbook, CancellationToken ct)
    {
        bool useSourceFile = !string.IsNullOrWhiteSpace(sourceXlsxPath) &&
                             File.Exists(sourceXlsxPath) &&
                             string.Equals(Path.GetExtension(sourceXlsxPath), ".xlsx", StringComparison.OrdinalIgnoreCase);

        if (useSourceFile)
        {
            File.Copy(sourceXlsxPath!, tempPath, overwrite: true);
        }
        else if (workbook.OriginalPackageBytes is { Length: > 0 } pkgBytes)
        {
            File.WriteAllBytes(tempPath, pkgBytes);
        }

        if (useSourceFile || workbook.OriginalPackageBytes is { Length: > 0 })
        {
            using var doc = SpreadsheetDocument.Open(tempPath, isEditable: true);
            var wbPart = doc.WorkbookPart ?? doc.AddWorkbookPart();
            wbPart.Workbook ??= new Workbook();
            var sheetsEl = wbPart.Workbook.Sheets ?? wbPart.Workbook.AppendChild(new Sheets());

            var existingSheets = sheetsEl.Elements<Sheet>().ToList();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyFormulaChanged = false;

            for (int sIdx = 0; sIdx < workbook.Sheets.Count; sIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var model = workbook.Sheets[sIdx];
                if (model.FormulaMutated) anyFormulaChanged = true;

                string safeName = EnsureUniqueSheetName(model.Name, usedNames);

                Sheet? sheetEl = sIdx < existingSheets.Count ? existingSheets[sIdx] : null;
                WorksheetPart wsPart;
                if (sheetEl?.Id?.Value is { Length: > 0 } relId &&
                    wbPart.TryGetPartById(relId, out var existingPart) &&
                    existingPart is WorksheetPart existingWsPart)
                {
                    wsPart = existingWsPart;
                    sheetEl.Name = safeName;
                }
                else
                {
                    wsPart = wbPart.AddNewPart<WorksheetPart>();
                    wsPart.Worksheet = new Worksheet(new SheetData());
                    uint nextId = sheetsEl.Elements<Sheet>().Select(s => s.SheetId?.Value ?? 0).DefaultIfEmpty(0u).Max() + 1;
                    sheetEl = new Sheet
                    {
                        Id = wbPart.GetIdOfPart(wsPart),
                        SheetId = nextId,
                        Name = safeName
                    };
                    sheetsEl.AppendChild(sheetEl);
                }

                WriteSheetData(wsPart, model, out bool sheetFormulaChanged, ct);
                if (sheetFormulaChanged) anyFormulaChanged = true;
                wsPart.Worksheet!.Save();
            }

            // Remove extra sheets deleted by user
            for (int sIdx = existingSheets.Count - 1; sIdx >= workbook.Sheets.Count; sIdx--)
            {
                var extra = existingSheets[sIdx];
                string? relId = extra.Id?.Value;
                extra.Remove();
                if (!string.IsNullOrEmpty(relId) && wbPart.TryGetPartById(relId, out var part))
                {
                    if (part is WorksheetPart removedWs &&
                        removedWs.Worksheet?.Descendants<CellFormula>().Any() == true)
                    {
                        anyFormulaChanged = true;
                    }
                    wbPart.DeletePart(part);
                }
            }

            if (anyFormulaChanged)
            {
                if (wbPart.CalculationChainPart is { } calcChainPart)
                {
                    wbPart.DeletePart(calcChainPart);
                }
                wbPart.Workbook.CalculationProperties ??= new CalculationProperties();
                wbPart.Workbook.CalculationProperties.FullCalculationOnLoad = true;
            }

            wbPart.Workbook.Save();
            return;
        }

        using (var doc = SpreadsheetDocument.Create(tempPath, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var sheets = wbPart.Workbook.AppendChild(new Sheets());
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            uint sheetId = 1;
            foreach (var model in workbook.Sheets)
            {
                ct.ThrowIfCancellationRequested();
                string safeName = EnsureUniqueSheetName(model.Name, usedNames);
                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                wsPart.Worksheet = new Worksheet(new SheetData());
                WriteSheetData(wsPart, model, out _, ct);
                wsPart.Worksheet!.Save();

                sheets.AppendChild(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = sheetId++,
                    Name = safeName
                });
            }

            wbPart.Workbook.Save();
        }
    }

    private static void WriteSheetData(
        WorksheetPart wsPart,
        SpreadsheetSheetData model,
        out bool formulasChangedOrRemoved,
        CancellationToken ct)
    {
        formulasChangedOrRemoved = model.FormulaMutated;
        wsPart.Worksheet ??= new Worksheet(new SheetData());

        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>();
        if (sheetData is null)
        {
            sheetData = new SheetData();
            wsPart.Worksheet.AppendChild(sheetData);
        }

        ApplyColumnWidths(wsPart.Worksheet, sheetData, model.ColumnWidths);

        var existingRows = new Dictionary<uint, Row>();
        foreach (var existingRow in sheetData.Elements<Row>().ToList())
        {
            uint rowIdx = existingRow.RowIndex?.Value ?? 0;
            if (rowIdx > 0 && rowIdx <= model.Rows.Count && !existingRows.ContainsKey(rowIdx))
            {
                existingRows[rowIdx] = existingRow;
            }
            else
            {
                if (existingRow.Descendants<CellFormula>().Any())
                {
                    formulasChangedOrRemoved = true;
                }
                existingRow.Remove();
            }
        }

        for (int r = 0; r < model.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            uint rowNumber = (uint)(r + 1);
            var rowValues = model.Rows[r];

            existingRows.TryGetValue(rowNumber, out var existingRow);
            var row = existingRow ?? new Row { RowIndex = rowNumber };

            var existingCellsByCol = new Dictionary<int, Cell>();
            int seqCol = 0;
            foreach (var ec in row.Elements<Cell>().ToList())
            {
                int ecCol = ec.CellReference?.Value is { Length: > 0 } cRef
                    ? ParseColumnIndex(cRef)
                    : seqCol;
                seqCol = ecCol + 1;
                if (ecCol >= rowValues.Count)
                {
                    if (ec.CellFormula is not null) formulasChangedOrRemoved = true;
                    ec.Remove();
                }
                else
                {
                    existingCellsByCol[ecCol] = ec;
                    ec.Remove();
                }
            }

            for (int c = 0; c < rowValues.Count; c++)
            {
                string val = rowValues[c] ?? string.Empty;
                var meta = model.GetCellMetadata(r, c);
                existingCellsByCol.TryGetValue(c, out var existingCell);

                Cell? updatedCell = UpdateOrCreateCell(
                    GetCellReference(r, c),
                    val,
                    meta,
                    existingCell,
                    ref formulasChangedOrRemoved);

                if (updatedCell is not null)
                {
                    row.AppendChild(updatedCell);
                }
            }

            if (row.Parent is null)
            {
                sheetData.AppendChild(row);
            }
        }
    }

    private static void ApplyColumnWidths(Worksheet worksheet, SheetData sheetData, List<SpreadsheetColumnWidth> columnWidths)
    {
        if (columnWidths.Count == 0) return;

        var existingCols = worksheet.GetFirstChild<Columns>();
        existingCols?.Remove();

        var cols = new Columns();
        foreach (var cw in columnWidths)
        {
            var col = new Column
            {
                Min = cw.Min,
                Max = cw.Max,
                Width = cw.Width,
                CustomWidth = cw.CustomWidth
            };
            if (cw.BestFit) col.BestFit = true;
            if (cw.Hidden) col.Hidden = true;
            if (cw.StyleIndex is uint sIdx) col.Style = sIdx;
            cols.AppendChild(col);
        }

        worksheet.InsertBefore(cols, sheetData);
    }

    private static Cell? UpdateOrCreateCell(
        string cellRef,
        string val,
        SpreadsheetCellMetadata? meta,
        Cell? existingCell,
        ref bool formulasChangedOrRemoved)
    {
        bool unedited = meta is not null && string.Equals(val, meta.LoadedDisplayValue, StringComparison.Ordinal);

        if (unedited)
        {
            if (existingCell is not null)
            {
                existingCell.CellReference = cellRef;
                return existingCell;
            }

            if (val.Length == 0 && meta!.StyleIndex is null && meta.FormulaText is null)
            {
                return null;
            }

            var cell = new Cell { CellReference = cellRef };
            if (meta!.StyleIndex is uint sIdx) cell.StyleIndex = sIdx;

            if (meta.FormulaText is not null)
            {
                var cf = new CellFormula(meta.FormulaText);
                if (meta.FormulaType is { } fType) cf.FormulaType = fType;
                if (meta.SharedFormulaReference is not null) cf.Reference = meta.SharedFormulaReference;
                if (meta.SharedFormulaIndex is uint sfIdx) cf.SharedIndex = sfIdx;
                cell.CellFormula = cf;
                if (meta.OriginalDataType is { } odt) cell.DataType = odt;
                if (meta.OriginalRawValue is not null) cell.CellValue = new CellValue(meta.OriginalRawValue);
            }
            else if (meta.OriginalDataType == CellValues.InlineString)
            {
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(val) { Space = SpaceProcessingModeValues.Preserve });
            }
            else if (meta.OriginalRawValue is not null)
            {
                if (meta.OriginalDataType is { } odt) cell.DataType = odt;
                cell.CellValue = new CellValue(meta.OriginalRawValue);
            }
            else
            {
                WriteTypedCellValue(cell, val, meta);
            }

            return cell;
        }

        // User edited this cell.
        if (meta?.FormulaText is not null || existingCell?.CellFormula is not null)
        {
            formulasChangedOrRemoved = true;
        }

        var edited = existingCell ?? new Cell();
        edited.CellReference = cellRef;
        if (meta?.StyleIndex is uint preservedStyle && edited.StyleIndex is null)
        {
            edited.StyleIndex = preservedStyle;
        }

        edited.CellFormula = null;
        edited.CellValue = null;
        edited.InlineString = null;

        if (val.Length == 0)
        {
            edited.DataType = null;
            return edited.StyleIndex is not null ? edited : null;
        }

        if (val.StartsWith('=') && val.Length > 1)
        {
            edited.DataType = null;
            edited.CellFormula = new CellFormula(val[1..]);
            formulasChangedOrRemoved = true;
            return edited;
        }

        WriteTypedCellValue(edited, val, meta);
        return edited;
    }

    private static void WriteTypedCellValue(Cell cell, string val, SpreadsheetCellMetadata? meta)
    {
        if (meta?.IsDateFormatted == true && TryParseExcelDateInput(val, out DateTime parsedDt))
        {
            cell.DataType = null;
            cell.CellValue = new CellValue(parsedDt.ToOADate().ToString(CultureInfo.InvariantCulture));
            return;
        }

        bool hasLeadingZeroText = val.Length > 1 && val[0] == '0' && val[1] != '.';
        if (!hasLeadingZeroText &&
            double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
            !double.IsNaN(num) && !double.IsInfinity(num))
        {
            cell.DataType = CellValues.Number;
            cell.CellValue = new CellValue(num.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text(val)
            {
                Space = SpaceProcessingModeValues.Preserve
            });
        }
    }

    private static bool TryParseExcelDateInput(string val, out DateTime dt)
    {
        if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
        {
            return true;
        }
        return DateTime.TryParse(val, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dt);
    }

    private static string EnsureUniqueSheetName(string desired, HashSet<string> used)
    {
        string clean = string.IsNullOrWhiteSpace(desired) ? "Sheet" : desired.Trim();
        if (clean.Length > 31) clean = clean[..31];
        string candidate = clean;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            string s = $"_{suffix++}";
            int maxBase = Math.Max(1, 31 - s.Length);
            candidate = (clean.Length > maxBase ? clean[..maxBase] : clean) + s;
        }
        return candidate;
    }

    private static void NormalizeSheetColumns(SpreadsheetSheetData sheet, int minColumns)
    {
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            while (row.Count < minColumns)
            {
                row.Add(string.Empty);
            }

            if (r < sheet.CellMetadata.Count)
            {
                var metaRow = sheet.CellMetadata[r];
                while (metaRow.Count < minColumns)
                {
                    metaRow.Add(null);
                }
            }
        }
    }

    public static string GetColumnLabel(int zeroBasedIndex)
    {
        var sb = new StringBuilder();
        int n = zeroBasedIndex + 1;
        while (n > 0)
        {
            int rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return sb.ToString();
    }

    public static string GetCellReference(int zeroBasedRow, int zeroBasedCol)
        => $"{GetColumnLabel(zeroBasedCol)}{zeroBasedRow + 1}";

    private static int ParseColumnIndex(string cellReference)
    {
        int col = 0;
        for (int i = 0; i < cellReference.Length; i++)
        {
            char c = char.ToUpperInvariant(cellReference[i]);
            if (c < 'A' || c > 'Z') break;
            col = col * 26 + (c - 'A' + 1);
        }
        return Math.Max(0, col - 1);
    }
}
