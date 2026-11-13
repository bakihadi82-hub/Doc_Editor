using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PdfStudio.Core.Formats;

public sealed class SpreadsheetSheetData
{
    public string Name { get; set; } = "Sheet1";
    public List<List<string>> Rows { get; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public SpreadsheetSheetData Clone()
    {
        var copy = new SpreadsheetSheetData { Name = Name };
        foreach (var row in Rows)
        {
            copy.Rows.Add([.. row]);
        }
        return copy;
    }
}

public sealed class SpreadsheetWorkbookData
{
    public List<SpreadsheetSheetData> Sheets { get; } = [];

    public SpreadsheetWorkbookData Clone()
    {
        var copy = new SpreadsheetWorkbookData();
        foreach (var sheet in Sheets)
        {
            copy.Sheets.Add(sheet.Clone());
        }
        return copy;
    }
}

public static class SpreadsheetService
{
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
        var workbook = new SpreadsheetWorkbookData();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = SpreadsheetDocument.Open(fs, isEditable: false);

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
                        expectedRowIndex++;
                    }
                    expectedRowIndex = rowIdx + 1;

                    var rowCells = new List<string>();
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
                        }
                        rowCells[colIdx] = ReadCellValue(cell, sharedStrings);
                    }

                    if (rowCells.Count > maxCols) maxCols = rowCells.Count;
                    sheetDataModel.Rows.Add(rowCells);
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

    private static void SaveXlsx(string? sourceXlsxPath, string tempPath, SpreadsheetWorkbookData workbook, CancellationToken ct)
    {
        bool useSourceTemplate = !string.IsNullOrWhiteSpace(sourceXlsxPath) &&
                                 File.Exists(sourceXlsxPath) &&
                                 string.Equals(Path.GetExtension(sourceXlsxPath), ".xlsx", StringComparison.OrdinalIgnoreCase);

        if (useSourceTemplate)
        {
            File.Copy(sourceXlsxPath!, tempPath, overwrite: true);
            using var doc = SpreadsheetDocument.Open(tempPath, isEditable: true);
            var wbPart = doc.WorkbookPart ?? doc.AddWorkbookPart();
            wbPart.Workbook ??= new Workbook();
            var sheetsEl = wbPart.Workbook.Sheets ?? wbPart.Workbook.AppendChild(new Sheets());

            var existingSheets = sheetsEl.Elements<Sheet>().ToList();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int sIdx = 0; sIdx < workbook.Sheets.Count; sIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var model = workbook.Sheets[sIdx];
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

                WriteSheetData(wsPart, model, ct);
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
                    wbPart.DeletePart(part);
                }
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
                WriteSheetData(wsPart, model, ct);
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

    private static void WriteSheetData(WorksheetPart wsPart, SpreadsheetSheetData model, CancellationToken ct)
    {
        wsPart.Worksheet ??= new Worksheet(new SheetData());
        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>();
        if (sheetData is null)
        {
            sheetData = new SheetData();
            wsPart.Worksheet.AppendChild(sheetData);
        }
        else
        {
            sheetData.RemoveAllChildren<Row>();
        }

        for (int r = 0; r < model.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var rowValues = model.Rows[r];
            var row = new Row { RowIndex = (uint)(r + 1) };
            for (int c = 0; c < rowValues.Count; c++)
            {
                string val = rowValues[c] ?? string.Empty;
                if (val.Length == 0) continue;

                var cell = new Cell { CellReference = GetCellReference(r, c) };
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
                row.AppendChild(cell);
            }
            sheetData.AppendChild(row);
        }
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
        foreach (var row in sheet.Rows)
        {
            while (row.Count < minColumns)
            {
                row.Add(string.Empty);
            }
        }
    }

    private static string ReadCellValue(Cell cell, string[] sharedStrings)
    {
        if (cell.CellValue is null && cell.InlineString is null)
        {
            return cell.InnerText ?? string.Empty;
        }

        string raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                idx >= 0 && idx < sharedStrings.Length)
            {
                return sharedStrings[idx];
            }
            return raw;
        }
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? raw;
        }
        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;
        }
        return raw;
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
