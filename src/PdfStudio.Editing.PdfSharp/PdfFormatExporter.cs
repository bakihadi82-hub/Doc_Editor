using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfStudio.Core.Formats;
using SkiaSharp;

namespace PdfStudio.Editing.PdfSharp;

/// <summary>
/// Exports non-PDF studio formats (Spreadsheets, Rich Documents, Markdown/Text, Images)
/// into clean multi-page PDF documents atomically.
/// </summary>
public static class PdfFormatExporter
{
    public static Task ExportSpreadsheetAsync(SpreadsheetWorkbookData workbook, string targetPdfPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            EncodingBootstrap.EnsureRegistered();
            SavePdfAtomic(targetPdfPath, doc =>
            {
                var headerFont = new XFont("Arial", 10, XFontStyle.Bold);
                var cellFont = new XFont("Arial", 9.5, XFontStyle.Regular);
                var titleFont = new XFont("Arial", 13, XFontStyle.Bold);

                foreach (var sheet in workbook.Sheets)
                {
                    ct.ThrowIfCancellationRequested();
                    int colCount = Math.Max(1, sheet.ColumnCount);
                    bool landscape = colCount > 5;
                    double pageW = landscape ? 792 : 612;
                    double pageH = landscape ? 612 : 792;
                    const double margin = 36;
                    double usableW = pageW - margin * 2;
                    double colW = Math.Max(48, usableW / Math.Min(colCount, 12));
                    int maxColsPerPage = Math.Max(1, (int)(usableW / colW));
                    const double rowH = 20;

                    var page = doc.AddPage();
                    page.Width = pageW;
                    page.Height = pageH;
                    var gfx = XGraphics.FromPdfPage(page);
                    double y = margin;

                    gfx.DrawString(sheet.Name, titleFont, XBrushes.Black, new XPoint(margin, y + 12));
                    y += 26;

                    for (int r = 0; r < sheet.Rows.Count; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (y + rowH > pageH - margin)
                        {
                            gfx.Dispose();
                            page = doc.AddPage();
                            page.Width = pageW;
                            page.Height = pageH;
                            gfx = XGraphics.FromPdfPage(page);
                            y = margin;
                        }

                        var row = sheet.Rows[r];
                        bool isHeader = r == 0;
                        if (isHeader)
                        {
                            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 242, 246)), margin, y, Math.Min(colCount, maxColsPerPage) * colW, rowH);
                        }

                        for (int c = 0; c < Math.Min(row.Count, maxColsPerPage); c++)
                        {
                            double x = margin + c * colW;
                            gfx.DrawRectangle(new XPen(XColor.FromArgb(210, 214, 220), 0.5), x, y, colW, rowH);
                            string val = row[c] ?? string.Empty;
                            if (val.Length > 28) val = val[..25] + "...";
                            gfx.DrawString(
                                val,
                                isHeader ? headerFont : cellFont,
                                XBrushes.Black,
                                new XRect(x + 4, y + 2, colW - 8, rowH - 4),
                                XStringFormats.CenterLeft);
                        }

                        y += rowH;
                    }

                    gfx.Dispose();
                }

                if (doc.PageCount == 0)
                {
                    doc.AddPage();
                }
            });
        }, ct);
    }

    public static Task ExportRichDocumentAsync(RichDocModel model, string targetPdfPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            EncodingBootstrap.EnsureRegistered();
            SavePdfAtomic(targetPdfPath, doc =>
            {
                const double pageW = 612;
                const double pageH = 792;
                const double margin = 54;
                double usableW = pageW - margin * 2;

                var page = doc.AddPage();
                page.Width = pageW;
                page.Height = pageH;
                var gfx = XGraphics.FromPdfPage(page);
                double y = margin;

                void EnsureSpace(double needed)
                {
                    if (y + needed > pageH - margin)
                    {
                        gfx.Dispose();
                        page = doc.AddPage();
                        page.Width = pageW;
                        page.Height = pageH;
                        gfx = XGraphics.FromPdfPage(page);
                        y = margin;
                    }
                }

                int numCounter = 1;
                foreach (var block in model.Blocks)
                {
                    ct.ThrowIfCancellationRequested();
                    if (block.Kind != RichDocBlockKind.NumberedItem) numCounter = 1;

                    if (block.Kind == RichDocBlockKind.Table && block.TableRows.Count > 0)
                    {
                        int cols = Math.Max(1, block.TableRows.Max(r => r.Count));
                        double colW = usableW / cols;
                        var tblFont = new XFont("Arial", 10, XFontStyle.Regular);
                        foreach (var row in block.TableRows)
                        {
                            EnsureSpace(24);
                            for (int c = 0; c < cols; c++)
                            {
                                double x = margin + c * colW;
                                gfx.DrawRectangle(new XPen(XColor.FromArgb(180, 185, 195), 0.75), x, y, colW, 22);
                                string cell = c < row.Count ? row[c] : string.Empty;
                                gfx.DrawString(cell, tblFont, XBrushes.Black, new XRect(x + 4, y + 2, colW - 8, 18), XStringFormats.CenterLeft);
                            }
                            y += 22;
                        }
                        y += 8;
                        continue;
                    }

                    double fontSize = block.Kind switch
                    {
                        RichDocBlockKind.Heading1 => 20,
                        RichDocBlockKind.Heading2 => 16,
                        RichDocBlockKind.Heading3 => 13.5,
                        _ => 11.5
                    };
                    var style = (block.IsBold || block.Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2 or RichDocBlockKind.Heading3, block.IsItalic) switch
                    {
                        (true, true) => XFontStyle.BoldItalic,
                        (true, false) => XFontStyle.Bold,
                        (false, true) => XFontStyle.Italic,
                        _ => XFontStyle.Regular
                    };
                    var font = new XFont("Arial", fontSize, style);

                    string prefix = block.Kind switch
                    {
                        RichDocBlockKind.BulletItem => "•  ",
                        RichDocBlockKind.NumberedItem => $"{numCounter++}.  ",
                        _ => string.Empty
                    };

                    double indent = block.Kind is RichDocBlockKind.BulletItem or RichDocBlockKind.NumberedItem ? 18 : 0;
                    foreach (string line in WrapText(gfx, prefix + block.Text, font, usableW - indent))
                    {
                        EnsureSpace(fontSize * 1.45);
                        gfx.DrawString(line, font, XBrushes.Black, new XRect(margin + indent, y, usableW - indent, fontSize * 1.4), XStringFormats.TopLeft);
                        y += fontSize * 1.45;
                    }
                    y += block.Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2 ? 8 : 5;
                }

                gfx.Dispose();
            });
        }, ct);
    }

    public static Task ExportTextAsync(string text, string targetPdfPath, bool monospace = false, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            EncodingBootstrap.EnsureRegistered();
            SavePdfAtomic(targetPdfPath, doc =>
            {
                const double pageW = 612;
                const double pageH = 792;
                const double margin = 48;
                double usableW = pageW - margin * 2;
                var font = new XFont(monospace ? "Courier New" : "Arial", monospace ? 9.5 : 11, XFontStyle.Regular);
                double lineH = monospace ? 14 : 16;

                var page = doc.AddPage();
                page.Width = pageW;
                page.Height = pageH;
                var gfx = XGraphics.FromPdfPage(page);
                double y = margin;

                string[] rawLines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                foreach (string rawLine in rawLines)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (string wrapped in WrapText(gfx, rawLine, font, usableW))
                    {
                        if (y + lineH > pageH - margin)
                        {
                            gfx.Dispose();
                            page = doc.AddPage();
                            page.Width = pageW;
                            page.Height = pageH;
                            gfx = XGraphics.FromPdfPage(page);
                            y = margin;
                        }
                        if (!string.IsNullOrEmpty(wrapped))
                        {
                            gfx.DrawString(wrapped, font, XBrushes.Black, new XPoint(margin, y + lineH * 0.8));
                        }
                        y += lineH;
                    }
                }

                gfx.Dispose();
            });
        }, ct);
    }

    public static Task ExportImageAsync(SKBitmap bitmap, string targetPdfPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EncodingBootstrap.EnsureRegistered();
            byte[] pngBytes = ImageDocumentService.EncodeBytes(bitmap, ".png");

            SavePdfAtomic(targetPdfPath, doc =>
            {
                double imgW = bitmap.Width * 72.0 / 96.0;
                double imgH = bitmap.Height * 72.0 / 96.0;
                bool landscape = imgW > imgH;
                double pageW = landscape ? 792 : 612;
                double pageH = landscape ? 612 : 792;
                const double margin = 24;

                var page = doc.AddPage();
                page.Width = pageW;
                page.Height = pageH;

                double availW = pageW - margin * 2;
                double availH = pageH - margin * 2;
                double scale = Math.Min(availW / Math.Max(1, imgW), availH / Math.Max(1, imgH));
                double drawW = imgW * scale;
                double drawH = imgH * scale;
                double left = (pageW - drawW) * 0.5;
                double top = (pageH - drawH) * 0.5;

                using var gfx = XGraphics.FromPdfPage(page);
                using var xImg = XImage.FromStream(() => new MemoryStream(pngBytes, writable: false));
                gfx.DrawImage(xImg, left, top, drawW, drawH);
            });
        }, ct);
    }

    private static IEnumerable<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        string[] words = text.Split(' ');
        var line = new System.Text.StringBuilder();
        foreach (string word in words)
        {
            string candidate = line.Length == 0 ? word : $"{line} {word}";
            if (gfx.MeasureString(candidate, font).Width <= maxWidth || line.Length == 0)
            {
                line.Clear();
                line.Append(candidate);
            }
            else
            {
                yield return line.ToString();
                line.Clear();
                line.Append(word);
            }
        }
        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static void SavePdfAtomic(string targetPdfPath, Action<PdfDocument> build)
    {
        string dir = Path.GetDirectoryName(targetPdfPath) ?? ".";
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetPdfPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var doc = new PdfDocument())
            {
                build(doc);
                doc.Save(tempPath);
            }

            var info = new FileInfo(tempPath);
            if (!info.Exists || info.Length == 0)
            {
                throw new IOException("Atomic PDF export verification failed.");
            }

            File.Move(tempPath, targetPdfPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
