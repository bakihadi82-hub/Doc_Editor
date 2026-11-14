using System.Globalization;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Security;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Forms;

namespace PdfStudio.Editing.PdfSharp;

/// <summary>
/// Structural and document editing service powered by PdfSharpCore with atomic temp-file replacement.
/// </summary>
public sealed class PdfSharpEditorService : IPdfEditorService
{
    public PdfSharpEditorService()
    {
        EncodingBootstrap.EnsureRegistered();
    }

    public Task<PdfMetadataInfo> ReadMetadataAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.InformationOnly);
            var info = doc.Info;
            return new PdfMetadataInfo(
                info.Title,
                info.Author,
                info.Subject,
                info.Keywords,
                info.Creator,
                info.Producer);
        }, ct);
    }

    public Task<IReadOnlyList<PdfFormField>> ReadFormFieldsAsync(string filePath, string? password = null, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<PdfFormField>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var doc = OpenPdf(filePath, password, PdfDocumentOpenMode.Import);
            var result = new List<PdfFormField>();

            for (int pageIdx = 0; pageIdx < doc.PageCount; pageIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.Pages[pageIdx];
                var pageBox = GetEffectivePageBox(page);
                int pageRotate = ((page.Rotate % 360) + 360) % 360;

                if (page.Elements["/Annots"] is not PdfArray annots)
                {
                    continue;
                }

                foreach (var item in annots.Elements)
                {
                    var dict = ResolveDictionary(item);
                    if (dict is null) continue;

                    string? subtype = dict.Elements.GetName("/Subtype");
                    if (!string.Equals(subtype, "/Widget", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parent = ResolveDictionary(dict.Elements["/Parent"]);
                    string ft = dict.Elements.GetName("/FT");
                    if (string.IsNullOrEmpty(ft) && parent is not null)
                    {
                        ft = parent.Elements.GetName("/FT");
                    }

                    string name = dict.Elements.GetString("/T");
                    if (string.IsNullOrEmpty(name) && parent is not null)
                    {
                        name = parent.Elements.GetString("/T");
                    }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = $"Field_{pageIdx + 1}_{result.Count + 1}";
                    }

                    var kind = ft switch
                    {
                        "/Btn" => PdfFormFieldKind.CheckBox,
                        "/Ch" => PdfFormFieldKind.Choice,
                        _ => PdfFormFieldKind.Text
                    };

                    var boundsPt = ParseTopLeftRect(dict.Elements.GetArray("/Rect"), pageBox, pageRotate);
                    int ff = dict.Elements.GetInteger("/Ff");
                    if (ff == 0 && parent is not null)
                    {
                        ff = parent.Elements.GetInteger("/Ff");
                    }
                    bool isReadOnly = (ff & 1) != 0;

                    string value = dict.Elements.GetString("/V");
                    if (string.IsNullOrEmpty(value) && parent is not null)
                    {
                        value = parent.Elements.GetString("/V");
                    }

                    string apState = dict.Elements.GetName("/AS");
                    bool isChecked = kind == PdfFormFieldKind.CheckBox &&
                        (!string.Equals(apState, "/Off", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(apState) ||
                         string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(value, "/Yes", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(value, "On", StringComparison.OrdinalIgnoreCase));

                    result.Add(new PdfFormField
                    {
                        Name = name,
                        Kind = kind,
                        PageIndex = pageIdx,
                        BoundsPt = boundsPt,
                        Value = value,
                        IsChecked = isChecked,
                        IsReadOnly = isReadOnly
                    });
                }
            }

            return result;
        }, ct);
    }

    public Task ApplyPlanAndSaveAsync(
        string sourceFilePath,
        string targetFilePath,
        StructuralEditPlan plan,
        PdfMetadataInfo? updatedMetadata = null,
        CancellationToken ct = default,
        PdfSaveOptions? saveOptions = null)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string tempPath = Path.Combine(
                Path.GetDirectoryName(targetFilePath)!,
                $".{Path.GetFileName(targetFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var input = OpenPdf(sourceFilePath, saveOptions?.SourcePassword, PdfDocumentOpenMode.Import))
                using (var output = new PdfDocument())
                {
                    output.Info.Title = updatedMetadata?.Title ?? input.Info.Title;
                    output.Info.Author = updatedMetadata?.Author ?? input.Info.Author;
                    output.Info.Subject = updatedMetadata?.Subject ?? input.Info.Subject;
                    output.Info.Keywords = updatedMetadata?.Keywords ?? input.Info.Keywords;
                    output.Info.Creator = updatedMetadata?.Creator ?? input.Info.Creator;

                    bool burnRedactions = saveOptions?.BurnRedactions == true;
                    bool flattenForms = saveOptions?.FlattenForms == true;

                    for (int displayIdx = 0; displayIdx < plan.Entries.Count; displayIdx++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = plan.Entries[displayIdx];
                        PdfPage added;

                        if (entry.SourcePageIndex is int srcIdx)
                        {
                            var imported = input.Pages[srcIdx];
                            added = output.AddPage(imported);
                            if (entry.AddedRotationQuarterTurns != 0)
                            {
                                int current = ((added.Rotate % 360) + 360) % 360;
                                int delta = entry.AddedRotationQuarterTurns * 90;
                                added.Rotate = ((current + delta) % 360 + 360) % 360;
                            }
                        }
                        else
                        {
                            added = output.AddPage();
                            added.Width = entry.BlankWidthPoints;
                            added.Height = entry.BlankHeightPoints;
                            if (entry.AddedRotationQuarterTurns != 0)
                            {
                                int delta = entry.AddedRotationQuarterTurns * 90;
                                added.Rotate = ((delta % 360) + 360) % 360;
                            }
                        }

                        var pageAnnotations = plan.Annotations.Where(a => a.PageIndex == displayIdx).ToList();
                        var pageRedactions = burnRedactions
                            ? pageAnnotations.Where(a => a.Kind == AnnotationKind.Redaction).ToList()
                            : [];
                        var pageTextEdits = plan.TextEdits.Where(e => e.PageIndex == displayIdx).ToList();
                        var pageFields = plan.FormFields.Where(f => f.PageIndex == displayIdx).ToList();

                        // 1. Burn true redactions & strip replaced/deleted inline text blocks from content stream first
                        if (pageRedactions.Count > 0)
                        {
                            var redactionBoxes = pageRedactions
                                .Select(r => NormalizeRect(r.BoundsPt))
                                .Select(r => new PdfRect(r.Left - 2.0, r.Top - 2.0, r.Right + 2.0, r.Bottom + 2.0))
                                .ToList();
                            StripRegionsFromPage(output, added, redactionBoxes, displayIdx + 1, stripNonTextContent: true);
                        }

                        if (pageTextEdits.Count > 0)
                        {
                            var editBoxes = pageTextEdits
                                .Select(e => NormalizeRect(e.OriginalBoundsPt))
                                .Select(r => new PdfRect(r.Left - 0.5, r.Top - 0.5, r.Right + 0.5, r.Bottom + 0.5))
                                .ToList();
                            StripRegionsFromPage(output, added, editBoxes, displayIdx + 1, stripNonTextContent: false);
                        }

                        // 2. Render visual annotations, committed text edits, redaction blackouts, and form values
                        bool hasNonRedactionAnnots = pageAnnotations.Any(a => a.Kind != AnnotationKind.Redaction);
                        bool hasTextEditReplacements = pageTextEdits.Any(e => !string.IsNullOrEmpty(e.NewText));
                        if (hasNonRedactionAnnots || pageRedactions.Count > 0 || hasTextEditReplacements || (flattenForms && pageFields.Count > 0))
                        {
                            using var gfx = XGraphics.FromPdfPage(added, XGraphicsPdfPageOptions.Append);
                            var pageBox = GetEffectivePageBox(added);
                            double mediaH = added.Height.Point;
                            int pageRotate = ((added.Rotate % 360) + 360) % 360;
                            XGraphicsState? rotState = null;
                            switch (pageRotate)
                            {
                                case 90:
                                    rotState = gfx.Save();
                                    gfx.TranslateTransform(pageBox.Left, mediaH - pageBox.Top);
                                    gfx.RotateTransform(-90);
                                    break;
                                case 180:
                                    rotState = gfx.Save();
                                    gfx.TranslateTransform(pageBox.Right, mediaH - pageBox.Top);
                                    gfx.RotateTransform(-180);
                                    break;
                                case 270:
                                    rotState = gfx.Save();
                                    gfx.TranslateTransform(pageBox.Right, mediaH - pageBox.Bottom);
                                    gfx.RotateTransform(90);
                                    break;
                                default:
                                {
                                    double dx0 = pageBox.Left;
                                    double dy0 = mediaH - pageBox.Bottom;
                                    if (Math.Abs(dx0) > 1e-6 || Math.Abs(dy0) > 1e-6)
                                    {
                                        rotState = gfx.Save();
                                        gfx.TranslateTransform(dx0, dy0);
                                    }
                                    break;
                                }
                            }

                            try
                            {
                                foreach (var edit in pageTextEdits)
                                {
                                    if (!string.IsNullOrEmpty(edit.NewText))
                                    {
                                        DrawTextEdit(gfx, edit);
                                    }
                                }

                                foreach (var ann in pageAnnotations)
                                {
                                    if (ann.Kind == AnnotationKind.Redaction) continue;
                                    DrawAnnotation(gfx, ann);
                                }

                                foreach (var redaction in pageRedactions)
                                {
                                    var r = NormalizeRect(redaction.BoundsPt);
                                    gfx.DrawRectangle(XBrushes.Black, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                                }

                                if (flattenForms)
                                {
                                    foreach (var field in pageFields)
                                    {
                                        DrawFlattenedField(gfx, field);
                                    }
                                }
                            }
                            finally
                            {
                                if (rotState is not null)
                                {
                                    gfx.Restore(rotState);
                                }
                            }
                        }

                        // 3. Persist or flatten AcroForm widget annotations
                        if (flattenForms)
                        {
                            RemoveWidgetAnnotations(added);
                        }
                        else if (pageFields.Count > 0)
                        {
                            ApplyFormFieldsToPage(output, added, pageFields);
                        }
                    }

                    if (flattenForms)
                    {
                        output.Internals.Catalog.Elements.Remove("/AcroForm");
                    }

                    // 4. Password protection & permissions
                    if (!string.IsNullOrEmpty(saveOptions?.UserPassword) || !string.IsNullOrEmpty(saveOptions?.OwnerPassword))
                    {
                        string? userPwd = saveOptions?.UserPassword;
                        string ownerPwd = !string.IsNullOrEmpty(saveOptions?.OwnerPassword)
                            ? saveOptions!.OwnerPassword!
                            : userPwd!;

                        // PdfSharpCore 1.3.67 supports None, Encrypted40Bit, and Encrypted128Bit (highest available).
                        output.SecuritySettings.DocumentSecurityLevel = saveOptions?.UseAes256 != false
                            ? PdfDocumentSecurityLevel.Encrypted128Bit
                            : PdfDocumentSecurityLevel.Encrypted40Bit;
                        if (!string.IsNullOrEmpty(userPwd))
                        {
                            output.SecuritySettings.UserPassword = userPwd;
                        }
                        output.SecuritySettings.OwnerPassword = ownerPwd;

                        bool permitPrint = saveOptions?.PermitPrint ?? true;
                        output.SecuritySettings.PermitPrint = permitPrint;
                        output.SecuritySettings.PermitFullQualityPrint = permitPrint && (saveOptions?.PermitFullQualityPrint ?? true);
                        output.SecuritySettings.PermitModifyDocument = saveOptions?.PermitModifyDocument ?? true;
                        output.SecuritySettings.PermitAnnotations = saveOptions?.PermitAnnotations ?? true;
                        output.SecuritySettings.PermitFormsFill = saveOptions?.PermitFormsFill ?? true;
                        output.SecuritySettings.PermitExtractContent = saveOptions?.PermitExtractContent ?? true;
                        output.SecuritySettings.PermitAssembleDocument = saveOptions?.PermitAssembleDocument ?? true;
                        output.SecuritySettings.PermitAccessibilityExtractContent = saveOptions?.PermitAccessibilityExtractContent ?? true;
                    }

                    output.Save(tempPath);
                }

                var tempInfo = new FileInfo(tempPath);
                if (!tempInfo.Exists || tempInfo.Length == 0)
                {
                    throw new IOException("Atomic save verification failed.");
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

    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputFilePath, string? password = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string tempPath = Path.Combine(
                Path.GetDirectoryName(outputFilePath)!,
                $".{Path.GetFileName(outputFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var output = new PdfDocument())
                {
                    foreach (string path in inputPaths)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var input = OpenPdf(path, password, PdfDocumentOpenMode.Import);
                        for (int i = 0; i < input.PageCount; i++)
                        {
                            output.AddPage(input.Pages[i]);
                        }
                    }
                    output.Save(tempPath);
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length == 0)
                {
                    throw new IOException("Atomic merge verification failed.");
                }

                File.Move(tempPath, outputFilePath, overwrite: true);
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

    public Task ExtractPagesAsync(string sourceFilePath, IReadOnlyList<int> zeroBasedPageIndices, string outputFilePath, string? password = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string tempPath = Path.Combine(
                Path.GetDirectoryName(outputFilePath)!,
                $".{Path.GetFileName(outputFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var input = OpenPdf(sourceFilePath, password, PdfDocumentOpenMode.Import))
                using (var output = new PdfDocument())
                {
                    foreach (int idx in zeroBasedPageIndices)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.AddPage(input.Pages[idx]);
                    }
                    output.Save(tempPath);
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length == 0)
                {
                    throw new IOException("Atomic extract verification failed.");
                }

                File.Move(tempPath, outputFilePath, overwrite: true);
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

    public Task SplitEveryNPagesAsync(string sourceFilePath, int pagesPerFile, string outputDirectory, string filePrefix, string? password = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerFile);
        return Task.Run(() =>
        {
            Directory.CreateDirectory(outputDirectory);
            using var input = OpenPdf(sourceFilePath, password, PdfDocumentOpenMode.Import);
            int part = 1;
            for (int start = 0; start < input.PageCount; start += pagesPerFile)
            {
                ct.ThrowIfCancellationRequested();
                using var chunk = new PdfDocument();
                int end = Math.Min(start + pagesPerFile, input.PageCount);
                for (int i = start; i < end; i++)
                {
                    chunk.AddPage(input.Pages[i]);
                }
                chunk.Save(Path.Combine(outputDirectory, $"{filePrefix}_part{part:D3}.pdf"));
                part++;
            }
        }, ct);
    }

    private static PdfDocument OpenPdf(string path, string? password, PdfDocumentOpenMode mode)
    {
        return string.IsNullOrEmpty(password)
            ? PdfReader.Open(path, mode)
            : PdfReader.Open(path, password, mode);
    }

    private static PdfDictionary? ResolveDictionary(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dict => dict,
        _ => null
    };

    private static PdfRect GetEffectivePageBox(PdfPage page)
    {
        bool hasCrop = page.Elements.ContainsKey("/CropBox") && !page.CropBox.IsEmpty;
        var box = hasCrop ? page.CropBox : page.MediaBox;
        double boxW = Math.Abs(box.X2 - box.X1);
        double boxH = Math.Abs(box.Y2 - box.Y1);
        return boxW > 0 && boxH > 0
            ? new PdfRect(Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2), Math.Max(box.X1, box.X2), Math.Max(box.Y1, box.Y2))
            : new PdfRect(0, 0, page.Width.Point, page.Height.Point);
    }

    private static (double X, double Y) ToPdfPoint(double dx, double dy, PdfRect pageBox, int pageRotate)
    {
        return pageRotate switch
        {
            90 => (pageBox.Left + dy, pageBox.Top + dx),
            180 => (pageBox.Right - dx, pageBox.Top + dy),
            270 => (pageBox.Right - dy, pageBox.Bottom - dx),
            _ => (pageBox.Left + dx, pageBox.Bottom - dy)
        };
    }

    private static PdfRect ParseTopLeftRect(PdfArray? rectArray, PdfRect pageBox, int pageRotate)
    {
        if (rectArray is null || rectArray.Elements.Count < 4)
        {
            return new PdfRect(0, 0, 0, 0);
        }

        double x1 = GetReal(rectArray.Elements[0]);
        double y1 = GetReal(rectArray.Elements[1]);
        double x2 = GetReal(rectArray.Elements[2]);
        double y2 = GetReal(rectArray.Elements[3]);

        var d1 = ToDisplayPoint(Math.Min(x1, x2), Math.Min(y1, y2), pageBox, pageRotate);
        var d2 = ToDisplayPoint(Math.Max(x1, x2), Math.Max(y1, y2), pageBox, pageRotate);
        return new PdfRect(
            Math.Min(d1.X, d2.X),
            Math.Min(d1.Y, d2.Y),
            Math.Max(d1.X, d2.X),
            Math.Max(d1.Y, d2.Y));
    }

    private static double GetReal(PdfItem item) => item switch
    {
        PdfReal r => r.Value,
        PdfInteger i => i.Value,
        _ => 0
    };

    private static PdfRect NormalizeRect(PdfRect r) => new(
        Math.Min(r.Left, r.Right),
        Math.Min(r.Top, r.Bottom),
        Math.Max(r.Left, r.Right),
        Math.Max(r.Top, r.Bottom));

    private static void DrawAnnotation(XGraphics gfx, PdfAnnotation ann)
    {
        var rect = NormalizeRect(ann.BoundsPt);
        double w = Math.Max(1.0, rect.Right - rect.Left);
        double h = Math.Max(1.0, rect.Bottom - rect.Top);

        switch (ann.Kind)
        {
            case AnnotationKind.Highlight:
            {
                var color = ParseColor(ann.ColorHex, XColor.FromArgb(102, 250, 204, 21), alphaOverride: 102);
                var brush = new XSolidBrush(color);
                if (ann.HighlightRectsPt.Count > 0)
                {
                    foreach (var hr in ann.HighlightRectsPt)
                    {
                        var r = NormalizeRect(hr);
                        gfx.DrawRectangle(brush, r.Left, r.Top, Math.Max(4.0, r.Right - r.Left), Math.Max(4.0, r.Bottom - r.Top));
                    }
                }
                else
                {
                    gfx.DrawRectangle(brush, rect.Left, rect.Top, w, h);
                }
                break;
            }
            case AnnotationKind.Underline:
            {
                var color = ParseColor(ann.ColorHex, XColor.FromArgb(255, 234, 179, 8));
                var pen = new XPen(color, Math.Max(1.0, ann.StrokeWidthPt));
                if (ann.HighlightRectsPt.Count > 0)
                {
                    foreach (var hr in ann.HighlightRectsPt)
                    {
                        var r = NormalizeRect(hr);
                        gfx.DrawLine(pen, r.Left, r.Bottom, r.Right, r.Bottom);
                    }
                }
                else
                {
                    gfx.DrawLine(pen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
                }
                break;
            }
            case AnnotationKind.Strikeout:
            {
                var color = ParseColor(ann.ColorHex, XColor.FromArgb(255, 239, 68, 68));
                var pen = new XPen(color, Math.Max(1.2, ann.StrokeWidthPt));
                if (ann.HighlightRectsPt.Count > 0)
                {
                    foreach (var hr in ann.HighlightRectsPt)
                    {
                        var r = NormalizeRect(hr);
                        double y = (r.Top + r.Bottom) * 0.5;
                        gfx.DrawLine(pen, r.Left, y, r.Right, y);
                    }
                }
                else
                {
                    double y = (rect.Top + rect.Bottom) * 0.5;
                    gfx.DrawLine(pen, rect.Left, y, rect.Right, y);
                }
                break;
            }
            case AnnotationKind.FreehandDraw:
            {
                if (ann.InkPointsPt.Count >= 2)
                {
                    var color = ParseColor(ann.ColorHex, XColor.FromArgb(255, 59, 130, 246));
                    var pen = new XPen(color, Math.Max(1.0, ann.StrokeWidthPt))
                    {
                        LineCap = XLineCap.Round,
                        LineJoin = XLineJoin.Round
                    };
                    var points = ann.InkPointsPt.Select(p => new XPoint(p.XPt, p.YPt)).ToArray();
                    gfx.DrawLines(pen, points);
                }
                break;
            }
            case AnnotationKind.TextBox:
            {
                string text = string.IsNullOrWhiteSpace(ann.TextContent) ? "Text" : ann.TextContent;
                var color = ParseColor(ann.ColorHex, XColor.FromArgb(255, 220, 38, 38));
                double fontSize = ann.FontSizePt > 0 ? ann.FontSizePt : 12.0;
                var font = ResolveSafeFont("Arial", fontSize, XFontStyle.Regular, gfx);
                gfx.DrawString(text, font, new XSolidBrush(color), new XPoint(rect.Left + 2, rect.Top + fontSize + 2));
                break;
            }
            case AnnotationKind.StickyNote:
            {
                var fill = XColor.FromArgb(245, 254, 240, 138);
                var border = XColor.FromArgb(255, 202, 138, 4);
                gfx.DrawRoundedRectangle(new XPen(border, 1), new XSolidBrush(fill), rect.Left, rect.Top, Math.Max(w, 110), Math.Max(h, 48), 4, 4);
                string noteText = string.IsNullOrWhiteSpace(ann.TextContent) ? "Note" : ann.TextContent;
                var font = ResolveSafeFont("Arial", 9.5, XFontStyle.Regular, gfx);
                gfx.DrawString(noteText, font, XBrushes.Black, new XRect(rect.Left + 6, rect.Top + 6, Math.Max(w, 110) - 12, Math.Max(h, 48) - 12), XStringFormats.TopLeft);
                break;
            }
            case AnnotationKind.SignatureStamp:
            {
                if (ann.SignaturePngBytes is { Length: > 0 } pngBytes)
                {
                    using var xImg = XImage.FromStream(() => new MemoryStream(pngBytes, writable: false));
                    gfx.DrawImage(xImg, rect.Left, rect.Top, w, h);
                }
                break;
            }
        }
    }

    private static void DrawFlattenedField(XGraphics gfx, PdfFormField field)
    {
        var rect = NormalizeRect(field.BoundsPt);
        double w = Math.Max(8.0, rect.Right - rect.Left);
        double h = Math.Max(8.0, rect.Bottom - rect.Top);

        if (field.Kind == PdfFormFieldKind.CheckBox)
        {
            gfx.DrawRectangle(new XPen(XColor.FromArgb(255, 75, 85, 99), 1), XBrushes.White, rect.Left, rect.Top, w, h);
            if (field.IsChecked)
            {
                var pen = new XPen(XColor.FromArgb(255, 17, 24, 39), 1.8) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                gfx.DrawLines(pen, [
                    new XPoint(rect.Left + w * 0.22, rect.Top + h * 0.52),
                    new XPoint(rect.Left + w * 0.44, rect.Top + h * 0.76),
                    new XPoint(rect.Left + w * 0.80, rect.Top + h * 0.24)
                ]);
            }
        }
        else if (!string.IsNullOrEmpty(field.Value))
        {
            double fontSize = Math.Clamp(h * 0.65, 8.0, 14.0);
            var font = ResolveSafeFont("Arial", fontSize, XFontStyle.Regular, gfx);
            gfx.DrawString(field.Value, font, XBrushes.Black, new XRect(rect.Left + 3, rect.Top + 1, Math.Max(4, w - 6), h), XStringFormats.CenterLeft);
        }
    }

    private static void RemoveWidgetAnnotations(PdfPage page)
    {
        if (page.Elements["/Annots"] is not PdfArray annots) return;
        for (int i = annots.Elements.Count - 1; i >= 0; i--)
        {
            var dict = ResolveDictionary(annots.Elements[i]);
            if (dict is not null && string.Equals(dict.Elements.GetName("/Subtype"), "/Widget", StringComparison.Ordinal))
            {
                annots.Elements.RemoveAt(i);
            }
        }
        if (annots.Elements.Count == 0)
        {
            page.Elements.Remove("/Annots");
        }
    }

    private static void ApplyFormFieldsToPage(PdfDocument output, PdfPage page, IReadOnlyList<PdfFormField> fields)
    {
        var pageBox = GetEffectivePageBox(page);
        int pageRotate = ((page.Rotate % 360) + 360) % 360;
        var acroForm = output.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        if (acroForm is null)
        {
            acroForm = new PdfDictionary(output);
            acroForm.Elements["/Fields"] = new PdfArray(output);
            acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
            output.Internals.AddObject(acroForm);
            output.Internals.Catalog.Elements["/AcroForm"] = acroForm.Reference!;
        }
        else
        {
            acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
        }

        var acroFieldsArray = acroForm.Elements.GetArray("/Fields");
        if (acroFieldsArray is null)
        {
            acroFieldsArray = new PdfArray(output);
            acroForm.Elements["/Fields"] = acroFieldsArray;
        }

        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(output);
            page.Elements["/Annots"] = annots;
        }

        var widgetsByName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        foreach (var item in annots.Elements)
        {
            var dict = ResolveDictionary(item);
            if (dict is null || !string.Equals(dict.Elements.GetName("/Subtype"), "/Widget", StringComparison.Ordinal))
            {
                continue;
            }
            string name = dict.Elements.GetString("/T");
            if (!string.IsNullOrEmpty(name))
            {
                widgetsByName[name] = dict;
            }
        }

        foreach (var field in fields)
        {
            if (widgetsByName.TryGetValue(field.Name, out var existingWidget))
            {
                if (field.Kind == PdfFormFieldKind.CheckBox)
                {
                    var state = new PdfName(field.IsChecked ? "/Yes" : "/Off");
                    existingWidget.Elements["/V"] = state;
                    existingWidget.Elements["/AS"] = state;
                }
                else
                {
                    existingWidget.Elements["/V"] = new PdfString(field.Value);
                }
                existingWidget.Elements.Remove("/AP");
            }
            else
            {
                var r = NormalizeRect(field.BoundsPt);
                var widget = new PdfDictionary(output);
                widget.Elements["/Type"] = new PdfName("/Annot");
                widget.Elements["/Subtype"] = new PdfName("/Widget");
                widget.Elements["/FT"] = new PdfName(field.Kind switch
                {
                    PdfFormFieldKind.CheckBox => "/Btn",
                    PdfFormFieldKind.Choice => "/Ch",
                    _ => "/Tx"
                });
                widget.Elements["/T"] = new PdfString(field.Name);
                var p1 = ToPdfPoint(r.Left, r.Top, pageBox, pageRotate);
                var p2 = ToPdfPoint(r.Right, r.Bottom, pageBox, pageRotate);
                widget.Elements["/Rect"] = new PdfRectangle(
                    new XPoint(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y)),
                    new XPoint(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y)));
                if (field.Kind == PdfFormFieldKind.CheckBox)
                {
                    var state = new PdfName(field.IsChecked ? "/Yes" : "/Off");
                    widget.Elements["/V"] = state;
                    widget.Elements["/AS"] = state;
                }
                else
                {
                    widget.Elements["/V"] = new PdfString(field.Value);
                }
                if (page.Reference is not null)
                {
                    widget.Elements["/P"] = page.Reference;
                }

                output.Internals.AddObject(widget);
                annots.Elements.Add(widget.Reference!);
                acroFieldsArray.Elements.Add(widget.Reference!);
            }
        }
    }

    private readonly record struct Matrix2D(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix2D Identity = new(1, 0, 0, 1, 0, 0);

        public static Matrix2D Multiply(Matrix2D m1, Matrix2D m2) => new(
            m1.A * m2.A + m1.B * m2.C,
            m1.A * m2.B + m1.B * m2.D,
            m1.C * m2.A + m1.D * m2.C,
            m1.C * m2.B + m1.D * m2.D,
            m1.E * m2.A + m1.F * m2.C + m2.E,
            m1.E * m2.B + m1.F * m2.D + m2.F);

        public (double X, double Y) Transform(double x, double y) =>
            (A * x + C * y + E, B * x + D * y + F);
    }

    private static void DrawTextEdit(XGraphics gfx, PdfTextEdit edit)
    {
        var rect = NormalizeRect(edit.OriginalBoundsPt);
        double fontSize = edit.FontSizePt > 1.0 ? edit.FontSizePt : 12.0;
        var style = (edit.IsBold, edit.IsItalic) switch
        {
            (true, true) => XFontStyle.BoldItalic,
            (true, false) => XFontStyle.Bold,
            (false, true) => XFontStyle.Italic,
            _ => XFontStyle.Regular
        };
        var font = ResolveSafeFont(edit.FontFamily, fontSize, style, gfx);
        var color = ParseColor(edit.ColorHex, XColor.FromArgb(255, 17, 24, 39));
        var brush = new XSolidBrush(color);

        double boxWidth = rect.Right - rect.Left;
        double maxWidth = boxWidth > 1.0 ? boxWidth : fontSize * 4.0;
        double lineHeight = fontSize * 1.25;
        var wrappedLines = WrapText(gfx, edit.NewText ?? string.Empty, font, maxWidth);

        for (int i = 0; i < wrappedLines.Count; i++)
        {
            double y = rect.Top + i * lineHeight;
            gfx.DrawString(
                wrappedLines[i],
                font,
                brush,
                new XRect(rect.Left, y, maxWidth, lineHeight * 1.2),
                XStringFormats.TopLeft);
        }
    }

    private static List<string> WrapText(XGraphics gfx, string rawText, XFont font, double maxWidth)
    {
        string normalized = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] paragraphs = normalized.Split('\n');
        var result = new List<string>(paragraphs.Length);

        foreach (string paragraph in paragraphs)
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                result.Add(string.Empty);
                continue;
            }

            if (gfx.MeasureString(paragraph, font).Width <= maxWidth + 0.01)
            {
                result.Add(paragraph);
                continue;
            }

            string[] words = paragraph.Split(' ');
            var line = new System.Text.StringBuilder();
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length == 0 || gfx.MeasureString(candidate, font).Width <= maxWidth + 0.01)
                {
                    line.Clear();
                    line.Append(candidate);
                }
                else
                {
                    result.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
            }

            if (line.Length > 0)
            {
                result.Add(line.ToString());
            }
        }

        return result;
    }

    private static XFont ResolveSafeFont(string? familyName, double fontSize, XFontStyle style, XGraphics gfx)
    {
        string requested = CleanFontFamilyName(familyName);
        var candidates = new List<(string Family, XFontStyle Style)>();
        var seen = new HashSet<(string, XFontStyle)>();

        void AddCandidate(string? fam, XFontStyle st)
        {
            if (string.IsNullOrWhiteSpace(fam)) return;
            var key = (fam.Trim(), st);
            if (seen.Add(key)) candidates.Add(key);
        }

        void AddFamilyWithStyleFallback(string? fam)
        {
            if (string.IsNullOrWhiteSpace(fam)) return;
            AddCandidate(fam, style);
            if (style != XFontStyle.Regular)
            {
                AddCandidate(fam, XFontStyle.Regular);
            }
        }

        AddFamilyWithStyleFallback(requested);

        string alias = requested.ToLowerInvariant() switch
        {
            "arialmt" or "helvetica" or "helveticaneue" or "helvetica neue" or "nimbus sans" or "liberation sans" or "dejavu sans" => "Arial",
            "times" or "times roman" or "times-roman" or "timesnewroman" or "liberation serif" or "dejavu serif" => "Times New Roman",
            "courier" or "couriernew" or "liberation mono" or "dejavu sans mono" => "Courier New",
            "segoeui" => "Segoe UI",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(alias))
        {
            AddFamilyWithStyleFallback(alias);
        }

        AddFamilyWithStyleFallback(PdfSharpCore.Fonts.GlobalFontSettings.FontResolver?.DefaultFontName);
        AddFamilyWithStyleFallback("Arial");
        AddFamilyWithStyleFallback("Helvetica");
        AddFamilyWithStyleFallback("Segoe UI");
        AddFamilyWithStyleFallback("Liberation Sans");
        AddFamilyWithStyleFallback("DejaVu Sans");
        AddFamilyWithStyleFallback("Times New Roman");
        AddFamilyWithStyleFallback("Courier New");

        Exception? lastEx = null;
        foreach (var (fam, st) in candidates)
        {
            try
            {
                var font = new XFont(fam, fontSize, st);
                _ = gfx.MeasureString("A", font);
                return font;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw new InvalidOperationException(
            $"Unable to resolve a usable font for '{familyName ?? "Arial"}'.",
            lastEx);
    }

    private static string CleanFontFamilyName(string? rawFamily)
    {
        string s = (rawFamily ?? string.Empty).Trim();
        if (s.Length > 7 && s[6] == '+')
        {
            bool hasTag = true;
            for (int i = 0; i < 6; i++)
            {
                if (s[i] < 'A' || s[i] > 'Z')
                {
                    hasTag = false;
                    break;
                }
            }
            if (hasTag) s = s[7..].Trim();
        }

        foreach (string suffix in (string[])[
            ",BoldItalic", ",Bold", ",Italic",
            "-BoldItalicMT", "-BoldMT", "-ItalicMT", "PS-BoldItalicMT", "PS-BoldMT", "PS-ItalicMT",
            "PSMT", "-BoldItalic", "-BoldOblique", "-Bold", "-Italic", "-Oblique", "-Regular", "MT"])
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && s.Length > suffix.Length)
            {
                s = s[..^suffix.Length].Trim();
                break;
            }
        }

        if (s.EndsWith("PS", StringComparison.Ordinal) &&
            s.Contains("Times", StringComparison.OrdinalIgnoreCase) &&
            s.Length > 2)
        {
            s = s[..^2].Trim();
        }

        return string.IsNullOrWhiteSpace(s) ? "Arial" : s;
    }

    /// <summary>
    /// Strips underlying PDF text operators/glyph strings (and when <paramref name="stripNonTextContent"/> is true,
    /// overlapping Form XObjects, images, and vector paths) that intersect any target box.
    /// Propagates parse errors instead of leaving redacted content intact.
    /// </summary>
    private static void StripRegionsFromPage(
        PdfDocument ownerDoc,
        PdfPage page,
        IReadOnlyList<PdfRect> normRedactions,
        int oneBasedPageNumber,
        bool stripNonTextContent)
    {
        if (normRedactions.Count == 0) return;

        var pageBox = GetEffectivePageBox(page);
        int pageRotate = ((page.Rotate % 360) + 360) % 360;

        CSequence sequence;
        try
        {
            sequence = ContentReader.ReadContent(page);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse PDF content stream on page {oneBasedPageNumber} for true redaction: {ex.Message}",
                ex);
        }

        var pageResourcesDict = ResolvePageResourcesDictionary(page);
        var rootScope = new ResourceScope(pageResourcesDict, null);
        var activeForms = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var initialState = new StripGState(Matrix2D.Identity, null, 12.0, 14.0, 0.0, 0.0, 100.0, 1.0);
        int remappedCounter = 0;
        byte[]? rawPageContent = ContainsInlineImageOperator(sequence)
            ? ReadCombinedPageContentBytes(page)
            : null;

        var result = FilterContentSequence(
            ownerDoc,
            sequence,
            rawPageContent,
            initialState,
            rootScope,
            pageBox,
            pageRotate,
            normRedactions,
            oneBasedPageNumber,
            stripNonTextContent,
            activeForms,
            ref remappedCounter);

        if (!result.Modified) return;

        if (result.RemappedXObjects.Count > 0)
        {
            EnsureDirectXObjectsRegistered(ownerDoc, page, result.RemappedXObjects);
        }

        if (result.KeptInlineImages.Count > 0)
        {
            byte[] serialized = SerializeSequenceWithInlineImages(result.Sequence, result.KeptInlineImages);
            var pdfContent = new PdfContent(ownerDoc);
            pdfContent.CreateStream(serialized);
            ownerDoc.Internals.AddObject(pdfContent);
            page.Contents.Elements.Clear();
            page.Contents.Elements.Add(pdfContent.Reference!);
        }
        else
        {
            page.Contents.ReplaceContent(result.Sequence);
        }
    }

    private static PdfDictionary? ResolvePageResourcesDictionary(PdfPage page)
    {
        var direct = page.Elements.GetDictionary("/Resources");
        if (direct is not null) return direct;

        var parent = page.Elements.GetDictionary("/Parent");
        while (parent is not null)
        {
            var inherited = parent.Elements.GetDictionary("/Resources");
            if (inherited is not null) return inherited;
            parent = parent.Elements.GetDictionary("/Parent");
        }

        return null;
    }

    private static void EnsureDirectXObjectsRegistered(
        PdfDocument ownerDoc,
        PdfPage page,
        IReadOnlyDictionary<string, PdfDictionary> remappedXObjects)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            var inherited = ResolvePageResourcesDictionary(page);
            resources = inherited is not null ? inherited.Clone() : new PdfDictionary(ownerDoc);
            page.Elements["/Resources"] = resources;
        }

        var xObjects = resources.Elements.GetDictionary("/XObject");
        if (xObjects is null)
        {
            xObjects = new PdfDictionary(ownerDoc);
            resources.Elements["/XObject"] = xObjects;
        }

        foreach (var (name, dict) in remappedXObjects)
        {
            xObjects.Elements[name] = dict.Reference ?? (PdfItem)dict;
        }
    }

    private sealed class StripGState
    {
        public Matrix2D Ctm;
        public string? FontName;
        public double FontSize;
        public double Leading;
        public double CharSpacing;
        public double WordSpacing;
        public double HorizontalScale;
        public double LineWidth;

        public StripGState(
            Matrix2D ctm,
            string? fontName,
            double fontSize,
            double leading,
            double charSpacing,
            double wordSpacing,
            double horizontalScale,
            double lineWidth)
        {
            Ctm = ctm;
            FontName = fontName;
            FontSize = fontSize;
            Leading = leading;
            CharSpacing = charSpacing;
            WordSpacing = wordSpacing;
            HorizontalScale = horizontalScale;
            LineWidth = lineWidth;
        }

        public StripGState Clone() => new(Ctm, FontName, FontSize, Leading, CharSpacing, WordSpacing, HorizontalScale, LineWidth);
    }

    private sealed class ResourceScope
    {
        private readonly PdfDictionary? _resources;
        private readonly ResourceScope? _parent;
        private Dictionary<string, PdfFontMetrics>? _fonts;

        public ResourceScope(PdfDictionary? resources, ResourceScope? parent)
        {
            _resources = resources;
            _parent = parent;
        }

        public PdfDictionary? ResolveXObject(string rawName, out PdfDictionary? owningXObjectDict)
        {
            string normName = NormalizePdfName(rawName);
            var xObjDict = _resources?.Elements.GetDictionary("/XObject");
            if (xObjDict is not null)
            {
                var found = xObjDict.Elements.GetDictionary(normName);
                if (found is not null)
                {
                    owningXObjectDict = xObjDict;
                    return found;
                }
            }

            if (_parent is not null)
            {
                return _parent.ResolveXObject(normName, out owningXObjectDict);
            }

            owningXObjectDict = null;
            return null;
        }

        public bool ContainsXObjectName(string normName)
        {
            var xObjDict = _resources?.Elements.GetDictionary("/XObject");
            if (xObjDict is not null && xObjDict.Elements.ContainsKey(normName))
            {
                return true;
            }
            return _parent?.ContainsXObjectName(normName) == true;
        }

        public PdfFontMetrics? ResolveFont(string? fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return null;
            string norm = NormalizePdfName(fontName);
            EnsureFontsLoaded();
            if (_fonts!.TryGetValue(norm, out var metrics))
            {
                return metrics;
            }
            return _parent?.ResolveFont(norm);
        }

        private void EnsureFontsLoaded()
        {
            if (_fonts is not null) return;
            _fonts = new Dictionary<string, PdfFontMetrics>(StringComparer.Ordinal);
            var fontDict = _resources?.Elements.GetDictionary("/Font");
            if (fontDict is null) return;

            foreach (string key in fontDict.Elements.Keys)
            {
                var f = fontDict.Elements.GetDictionary(key);
                if (f is null) continue;
                _fonts[NormalizePdfName(key)] = PdfFontMetrics.FromPdfDictionary(f);
            }
        }

        private static string NormalizePdfName(string name) =>
            name.StartsWith('/') ? name : "/" + name;
    }

    private sealed class PdfFontMetrics
    {
        public bool IsTwoByte { get; init; }
        public double WidthScale { get; init; } = 0.001;
        public double DefaultWidth { get; init; } = 550.0;
        public Dictionary<int, double>? Widths { get; init; }

        public static readonly PdfFontMetrics Default = new()
        {
            IsTwoByte = false,
            WidthScale = 0.001,
            DefaultWidth = 550.0,
            Widths = null
        };

        public double GetAdvanceTextSpace(int code, double absFontSize, double charSpacing, double wordSpacing, double horizontalScaleRatio, double fontSizeSign)
        {
            double widthUnits = Widths is not null && Widths.TryGetValue(code, out double w)
                ? w
                : DefaultWidth;
            double tw = !IsTwoByte && code == 32 ? wordSpacing : 0.0;
            double signedFontSize = fontSizeSign * absFontSize;
            return ((widthUnits * WidthScale * signedFontSize) + charSpacing + tw) * horizontalScaleRatio;
        }

        public static PdfFontMetrics FromPdfDictionary(PdfDictionary fontDict)
        {
            string subtype = fontDict.Elements.GetName("/Subtype");
            if (string.Equals(subtype, "/Type0", StringComparison.Ordinal))
            {
                double dw = 1000.0;
                Dictionary<int, double>? cidWidths = null;

                var descArr = fontDict.Elements.GetArray("/DescendantFonts");
                var cidFont = descArr is { Elements.Count: > 0 } ? ResolveDictionary(descArr.Elements[0]) : null;
                if (cidFont is not null)
                {
                    if (TryGetDictNumber(cidFont, "/DW", out double parsedDw))
                    {
                        dw = parsedDw;
                    }

                    var wArr = cidFont.Elements.GetArray("/W");
                    if (wArr is not null)
                    {
                        cidWidths = ParseCidWidthArray(wArr);
                    }
                }

                return new PdfFontMetrics
                {
                    IsTwoByte = true,
                    WidthScale = 0.001,
                    DefaultWidth = dw,
                    Widths = cidWidths
                };
            }

            double scale = 0.001;
            if (string.Equals(subtype, "/Type3", StringComparison.Ordinal))
            {
                var fm = fontDict.Elements.GetArray("/FontMatrix");
                if (fm is { Elements.Count: >= 1 } && TryGetArrayNumber(fm.Elements[0], out double a) && a > 0)
                {
                    scale = a;
                }
            }

            string baseFont = fontDict.Elements.GetName("/BaseFont");
            double fallbackWidth = baseFont.Contains("Courier", StringComparison.OrdinalIgnoreCase) ? 600.0 : 550.0;

            var descriptor = fontDict.Elements.GetDictionary("/FontDescriptor");
            if (descriptor is not null && TryGetDictNumber(descriptor, "/MissingWidth", out double missingW) && missingW > 0)
            {
                fallbackWidth = missingW;
            }

            Dictionary<int, double>? simpleWidths = null;
            var widthsArr = fontDict.Elements.GetArray("/Widths");
            if (widthsArr is not null && widthsArr.Elements.Count > 0)
            {
                int firstChar = fontDict.Elements.GetInteger("/FirstChar");
                simpleWidths = new Dictionary<int, double>(widthsArr.Elements.Count);
                for (int i = 0; i < widthsArr.Elements.Count; i++)
                {
                    if (TryGetArrayNumber(widthsArr.Elements[i], out double wi))
                    {
                        simpleWidths[firstChar + i] = wi;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(baseFont) &&
                     !baseFont.Contains("Courier", StringComparison.OrdinalIgnoreCase))
            {
                simpleWidths = ResolveStandard14Widths(baseFont);
            }

            return new PdfFontMetrics
            {
                IsTwoByte = false,
                WidthScale = scale,
                DefaultWidth = fallbackWidth,
                Widths = simpleWidths
            };
        }

        private static readonly Lazy<Dictionary<int, double>> HelveticaWidths = new(() => BuildAsciiWidths(
        [
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            278, 278, 584, 584, 584, 556, 1015,
            667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
            278, 278, 278, 469, 556, 333,
            556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,
            334, 260, 334, 584
        ]));

        private static readonly Lazy<Dictionary<int, double>> HelveticaBoldWidths = new(() => BuildAsciiWidths(
        [
            278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            333, 333, 584, 584, 584, 611, 975,
            722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
            333, 278, 333, 584, 556, 333,
            556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500,
            389, 280, 389, 584
        ]));

        private static readonly Lazy<Dictionary<int, double>> TimesRomanWidths = new(() => BuildAsciiWidths(
        [
            250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278,
            500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
            278, 278, 564, 564, 564, 444, 921,
            722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, 556, 722, 667, 556, 611, 722, 722, 944, 722, 722, 611,
            333, 278, 333, 469, 500, 333,
            444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500, 500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444,
            480, 200, 480, 541
        ]));

        private static readonly Lazy<Dictionary<int, double>> TimesBoldWidths = new(() => BuildAsciiWidths(
        [
            250, 333, 555, 500, 500, 1000, 833, 278, 333, 333, 500, 570, 250, 333, 250, 278,
            500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
            333, 333, 570, 570, 570, 500, 930,
            722, 667, 722, 722, 667, 611, 778, 778, 389, 500, 778, 667, 944, 722, 778, 611, 778, 722, 556, 667, 722, 722, 1000, 722, 722, 667,
            333, 278, 333, 581, 500, 333,
            500, 556, 444, 556, 444, 333, 500, 556, 278, 333, 556, 278, 833, 556, 500, 556, 556, 444, 389, 333, 556, 500, 722, 500, 500, 444,
            394, 220, 394, 520
        ]));

        private static Dictionary<int, double> BuildAsciiWidths(ReadOnlySpan<short> widths)
        {
            var dict = new Dictionary<int, double>(widths.Length);
            for (int i = 0; i < widths.Length; i++)
            {
                dict[32 + i] = widths[i];
            }
            return dict;
        }

        private static Dictionary<int, double>? ResolveStandard14Widths(string baseFont)
        {
            string clean = baseFont.TrimStart('/');
            if (clean.Length > 7 && clean[6] == '+')
            {
                clean = clean[7..];
            }
            bool isBold = clean.Contains("Bold", StringComparison.OrdinalIgnoreCase);

            if (clean.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("Arial", StringComparison.OrdinalIgnoreCase))
            {
                return isBold ? HelveticaBoldWidths.Value : HelveticaWidths.Value;
            }
            if (clean.Contains("Times", StringComparison.OrdinalIgnoreCase))
            {
                return isBold ? TimesBoldWidths.Value : TimesRomanWidths.Value;
            }
            return null;
        }

        private static Dictionary<int, double> ParseCidWidthArray(PdfArray wArr)
        {
            var map = new Dictionary<int, double>();
            int i = 0;
            while (i < wArr.Elements.Count)
            {
                if (!TryGetArrayInt(wArr.Elements[i], out int cStart))
                {
                    i++;
                    continue;
                }

                if (i + 1 >= wArr.Elements.Count) break;
                var next = ResolveItem(wArr.Elements[i + 1]);
                if (next is PdfArray subArr)
                {
                    for (int j = 0; j < subArr.Elements.Count; j++)
                    {
                        if (TryGetArrayNumber(subArr.Elements[j], out double w))
                        {
                            map[cStart + j] = w;
                        }
                    }
                    i += 2;
                }
                else if (i + 2 < wArr.Elements.Count &&
                    TryGetArrayInt(wArr.Elements[i + 1], out int cEnd) &&
                    TryGetArrayNumber(wArr.Elements[i + 2], out double rangeW))
                {
                    int limit = Math.Min(cEnd, cStart + 65535);
                    for (int c = cStart; c <= limit; c++)
                    {
                        map[c] = rangeW;
                    }
                    i += 3;
                }
                else
                {
                    i++;
                }
            }
            return map;
        }

        private static PdfItem? ResolveItem(PdfItem? item) =>
            item is PdfReference r ? r.Value : item;

        private static bool TryGetDictNumber(PdfDictionary dict, string key, out double val)
        {
            var item = ResolveItem(dict.Elements[key]);
            return TryGetArrayNumber(item, out val);
        }

        private static bool TryGetArrayNumber(PdfItem? item, out double val)
        {
            item = ResolveItem(item);
            switch (item)
            {
                case PdfInteger pi:
                    val = pi.Value;
                    return true;
                case PdfReal pr:
                    val = pr.Value;
                    return true;
                default:
                    val = 0;
                    return false;
            }
        }

        private static bool TryGetArrayInt(PdfItem? item, out int val)
        {
            item = ResolveItem(item);
            if (item is PdfInteger pi)
            {
                val = pi.Value;
                return true;
            }
            val = 0;
            return false;
        }
    }

    private sealed class SequenceFilterResult
    {
        public CSequence Sequence { get; init; } = new();
        public bool Modified { get; init; }
        public Dictionary<string, PdfDictionary> RemappedXObjects { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<COperator, byte[]> KeptInlineImages { get; init; } = new(ReferenceEqualityComparer.Instance);
    }

    private readonly record struct PathSegment(double X1, double Y1, double X2, double Y2);

    private static SequenceFilterResult FilterContentSequence(
        PdfDocument ownerDoc,
        CSequence sequence,
        byte[]? rawStreamBytes,
        StripGState initialState,
        ResourceScope scope,
        PdfRect pageBox,
        int pageRotate,
        IReadOnlyList<PdfRect> normRedactions,
        int oneBasedPageNumber,
        bool stripNonTextContent,
        HashSet<PdfDictionary> activeForms,
        ref int remappedCounter)
    {
        var filtered = new CSequence();
        var stateStack = new Stack<StripGState>();
        var gs = initialState.Clone();
        var tlm = Matrix2D.Identity;
        var tm = Matrix2D.Identity;
        bool inText = false;
        bool modified = false;
        var remappedXObjects = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        List<byte[]>? rawInlineSlices = rawStreamBytes is not null ? ExtractInlineImageSpans(rawStreamBytes) : null;
        int inlineImageCursor = 0;
        var keptInlineImages = new Dictionary<COperator, byte[]>(ReferenceEqualityComparer.Instance);

        // Path accumulation state for vector path redaction
        var pathOps = new List<COperator>();
        var pathPoints = new List<(double X, double Y)>();
        var pathSegments = new List<PathSegment>();
        var subpaths = new List<List<(double X, double Y)>>();
        List<(double X, double Y)>? currentSubpath = null;
        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        bool hasCurPoint = false;
        bool pathHasClip = false;
        bool skippingInlineImage = false;

        void ResetPath()
        {
            pathOps.Clear();
            pathPoints.Clear();
            pathSegments.Clear();
            subpaths.Clear();
            currentSubpath = null;
            hasCurPoint = false;
            pathHasClip = false;
        }

        void FlushPathVerbatim()
        {
            foreach (var pOp in pathOps) filtered.Add(pOp);
            ResetPath();
        }

        foreach (var obj in sequence)
        {
            if (obj is not COperator op)
            {
                if (!skippingInlineImage)
                {
                    filtered.Add(obj);
                }
                continue;
            }

            string name = op.OpCode.Name;

            if (skippingInlineImage)
            {
                if (name == "EI")
                {
                    skippingInlineImage = false;
                }
                continue;
            }

            switch (name)
            {
                case "q":
                    FlushPathVerbatim();
                    stateStack.Push(gs.Clone());
                    filtered.Add(op);
                    break;

                case "Q":
                    FlushPathVerbatim();
                    if (stateStack.Count > 0) gs = stateStack.Pop();
                    filtered.Add(op);
                    break;

                case "cm":
                    FlushPathVerbatim();
                    if (TryReadMatrix(op.Operands, out var cmMat))
                    {
                        gs.Ctm = Matrix2D.Multiply(cmMat, gs.Ctm);
                    }
                    filtered.Add(op);
                    break;

                case "w":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && TryReadNumber(op.Operands[0], out double lw))
                    {
                        gs.LineWidth = Math.Abs(lw);
                    }
                    filtered.Add(op);
                    break;

                case "Tc":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && TryReadNumber(op.Operands[0], out double tcVal))
                    {
                        gs.CharSpacing = tcVal;
                    }
                    filtered.Add(op);
                    break;

                case "Tw":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && TryReadNumber(op.Operands[0], out double twVal))
                    {
                        gs.WordSpacing = twVal;
                    }
                    filtered.Add(op);
                    break;

                case "Tz":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && TryReadNumber(op.Operands[0], out double tzVal))
                    {
                        gs.HorizontalScale = tzVal;
                    }
                    filtered.Add(op);
                    break;

                case "BT":
                    FlushPathVerbatim();
                    inText = true;
                    tlm = Matrix2D.Identity;
                    tm = Matrix2D.Identity;
                    filtered.Add(op);
                    break;

                case "ET":
                    FlushPathVerbatim();
                    inText = false;
                    filtered.Add(op);
                    break;

                case "Tf":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && op.Operands[0] is CName fontCName)
                    {
                        gs.FontName = fontCName.Name;
                    }
                    if (op.Operands.Count >= 2 && TryReadNumber(op.Operands[1], out double tfSize))
                    {
                        gs.FontSize = tfSize;
                    }
                    filtered.Add(op);
                    break;

                case "TL":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && TryReadNumber(op.Operands[0], out double tlVal))
                    {
                        gs.Leading = tlVal;
                    }
                    filtered.Add(op);
                    break;

                case "Tm":
                    FlushPathVerbatim();
                    if (TryReadMatrix(op.Operands, out var newTm))
                    {
                        tlm = newTm;
                        tm = newTm;
                    }
                    filtered.Add(op);
                    break;

                case "Td":
                case "TD":
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 2 &&
                        TryReadNumber(op.Operands[0], out double tx) &&
                        TryReadNumber(op.Operands[1], out double ty))
                    {
                        if (name == "TD") gs.Leading = -ty;
                        var tr = new Matrix2D(1, 0, 0, 1, tx, ty);
                        tlm = Matrix2D.Multiply(tr, tlm);
                        tm = tlm;
                    }
                    filtered.Add(op);
                    break;

                case "T*":
                {
                    FlushPathVerbatim();
                    var tr = new Matrix2D(1, 0, 0, 1, 0, -gs.Leading);
                    tlm = Matrix2D.Multiply(tr, tlm);
                    tm = tlm;
                    filtered.Add(op);
                    break;
                }

                case "'":
                case "\"":
                {
                    FlushPathVerbatim();
                    if (name == "\"" &&
                        op.Operands.Count >= 2 &&
                        TryReadNumber(op.Operands[0], out double aw) &&
                        TryReadNumber(op.Operands[1], out double ac))
                    {
                        gs.WordSpacing = aw;
                        gs.CharSpacing = ac;
                    }

                    var tr = new Matrix2D(1, 0, 0, 1, 0, -gs.Leading);
                    tlm = Matrix2D.Multiply(tr, tlm);
                    tm = tlm;

                    int strIdx = name == "\"" ? 2 : 0;
                    if (op.Operands.Count > strIdx && op.Operands[strIdx] is CString cStr)
                    {
                        var combined = inText ? Matrix2D.Multiply(tm, gs.Ctm) : gs.Ctm;
                        var resolvedMetrics = scope.ResolveFont(gs.FontName);
                        var metrics = resolvedMetrics ?? PdfFontMetrics.Default;
                        var strRes = FilterCString(cStr, combined, gs, metrics, resolvedMetrics is not null, pageBox, pageRotate, normRedactions);

                        if (!strRes.AnyRemoved)
                        {
                            filtered.Add(op);
                        }
                        else
                        {
                            modified = true;
                            if (name == "\"" && op.Operands.Count >= 2)
                            {
                                var opTw = OpCodes.OperatorFromName("Tw");
                                opTw.Operands.Add(op.Operands[0]);
                                filtered.Add(opTw);

                                var opTc = OpCodes.OperatorFromName("Tc");
                                opTc.Operands.Add(op.Operands[1]);
                                filtered.Add(opTc);
                            }

                            filtered.Add(OpCodes.OperatorFromName("T*"));
                            if (strRes.TjArray is not null && strRes.TjArray.Count > 0)
                            {
                                var tjOp = OpCodes.OperatorFromName("TJ");
                                tjOp.Operands.Add((CObject)strRes.TjArray);
                                filtered.Add(tjOp);
                            }
                        }

                        tm = Matrix2D.Multiply(new Matrix2D(1, 0, 0, 1, strRes.TotalAdvance, 0), tm);
                    }
                    else
                    {
                        filtered.Add(op);
                    }
                    break;
                }

                case "Tj":
                {
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && op.Operands[0] is CString cStr)
                    {
                        var combined = inText ? Matrix2D.Multiply(tm, gs.Ctm) : gs.Ctm;
                        var resolvedMetrics = scope.ResolveFont(gs.FontName);
                        var metrics = resolvedMetrics ?? PdfFontMetrics.Default;
                        var strRes = FilterCString(cStr, combined, gs, metrics, resolvedMetrics is not null, pageBox, pageRotate, normRedactions);

                        if (!strRes.AnyRemoved)
                        {
                            filtered.Add(op);
                        }
                        else
                        {
                            modified = true;
                            if (strRes.TjArray is not null && strRes.TjArray.Count > 0)
                            {
                                var tjOp = OpCodes.OperatorFromName("TJ");
                                tjOp.Operands.Add((CObject)strRes.TjArray);
                                filtered.Add(tjOp);
                            }
                        }

                        tm = Matrix2D.Multiply(new Matrix2D(1, 0, 0, 1, strRes.TotalAdvance, 0), tm);
                    }
                    else
                    {
                        filtered.Add(op);
                    }
                    break;
                }

                case "TJ":
                {
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && op.Operands[0] is CArray arr)
                    {
                        var combined = inText ? Matrix2D.Multiply(tm, gs.Ctm) : gs.Ctm;
                        var resolvedMetrics = scope.ResolveFont(gs.FontName);
                        var metrics = resolvedMetrics ?? PdfFontMetrics.Default;
                        bool fontKnown = resolvedMetrics is not null;
                        double absFs = Math.Abs(gs.FontSize) > 0 ? Math.Abs(gs.FontSize) : 12.0;
                        double fsSign = gs.FontSize < 0 ? -1.0 : 1.0;
                        double hScale = gs.HorizontalScale / 100.0;
                        double xOffset = 0.0;
                        bool anyRemoved = false;
                        bool anyTextKept = false;
                        var newArr = new CArray();

                        foreach (var elem in arr)
                        {
                            if (elem is CString cs)
                            {
                                var shifted = Matrix2D.Multiply(new Matrix2D(1, 0, 0, 1, xOffset, 0), combined);
                                var strRes = FilterCString(cs, shifted, gs, metrics, fontKnown, pageBox, pageRotate, normRedactions);
                                if (strRes.AnyRemoved)
                                {
                                    anyRemoved = true;
                                    if (strRes.AnyKept) anyTextKept = true;
                                    if (strRes.TjArray is not null)
                                    {
                                        foreach (var item in strRes.TjArray)
                                        {
                                            newArr.Add(item);
                                        }
                                    }
                                }
                                else
                                {
                                    anyTextKept = true;
                                    newArr.Add(cs);
                                }
                                xOffset += strRes.TotalAdvance;
                            }
                            else if (TryReadNumber(elem, out double adj))
                            {
                                xOffset -= fsSign * (adj / 1000.0) * absFs * hScale;
                                newArr.Add(elem);
                            }
                            else
                            {
                                newArr.Add(elem);
                            }
                        }

                        tm = Matrix2D.Multiply(new Matrix2D(1, 0, 0, 1, xOffset, 0), tm);
                        if (!anyRemoved)
                        {
                            filtered.Add(op);
                        }
                        else
                        {
                            modified = true;
                            if (anyTextKept)
                            {
                                var rebuilt = OpCodes.OperatorFromName("TJ");
                                rebuilt.Operands.Add((CObject)newArr);
                                filtered.Add(rebuilt);
                            }
                            else if (Math.Abs(xOffset) > 1e-6)
                            {
                                double totalTj = -(xOffset * 1000.0) / (fsSign * absFs * (Math.Abs(hScale) > 1e-9 ? hScale : 1.0));
                                var advanceArr = new CArray();
                                advanceArr.Add(new CReal { Value = Math.Round(totalTj, 3) });
                                var advanceOp = OpCodes.OperatorFromName("TJ");
                                advanceOp.Operands.Add((CObject)advanceArr);
                                filtered.Add(advanceOp);
                            }
                        }
                    }
                    else
                    {
                        filtered.Add(op);
                    }
                    break;
                }

                case "Do":
                {
                    FlushPathVerbatim();
                    if (op.Operands.Count >= 1 && op.Operands[0] is CName xObjCName)
                    {
                        var xObjDict = scope.ResolveXObject(xObjCName.Name, out _);
                        if (xObjDict is null)
                        {
                            filtered.Add(op);
                            break;
                        }

                        string subtype = xObjDict.Elements.GetName("/Subtype");
                        if (string.Equals(subtype, "/Image", StringComparison.Ordinal))
                        {
                            if (stripNonTextContent)
                            {
                                var imgBox = TransformUnitSquareToDisplayBox(gs.Ctm, pageBox, pageRotate);
                                if (IntersectsAny(imgBox, normRedactions))
                                {
                                    modified = true;
                                    break;
                                }
                            }
                            filtered.Add(op);
                            break;
                        }

                        if (string.Equals(subtype, "/Form", StringComparison.Ordinal))
                        {
                            if (!activeForms.Add(xObjDict))
                            {
                                filtered.Add(op);
                                break;
                            }

                            try
                            {
                                var formMatrix = ReadPdfArrayMatrix(xObjDict.Elements.GetArray("/Matrix"));
                                var formCtm = Matrix2D.Multiply(formMatrix, gs.Ctm);

                                bool shouldInspect = true;
                                var bboxArr = xObjDict.Elements.GetArray("/BBox");
                                if (TryReadPdfRectArray(bboxArr, out var bboxRect) &&
                                    bboxRect.Right > bboxRect.Left &&
                                    bboxRect.Bottom > bboxRect.Top)
                                {
                                    var bboxDisplay = TransformPdfRectToDisplayBox(bboxRect, formCtm, pageBox, pageRotate);
                                    if (!IntersectsAny(bboxDisplay, normRedactions))
                                    {
                                        shouldInspect = false;
                                    }
                                }

                                if (!shouldInspect || xObjDict.Stream is null)
                                {
                                    filtered.Add(op);
                                    break;
                                }

                                CSequence formSeq;
                                byte[] rawBytes;
                                try
                                {
                                    rawBytes = xObjDict.Stream.UnfilteredValue;
                                    formSeq = rawBytes.Length == 0 ? new CSequence() : ContentReader.ReadContent(rawBytes);
                                }
                                catch (Exception ex)
                                {
                                    throw new InvalidOperationException(
                                        $"Failed to parse PDF Form XObject '{xObjCName.Name}' on page {oneBasedPageNumber} for true redaction: {ex.Message}",
                                        ex);
                                }

                                var formScope = new ResourceScope(xObjDict.Elements.GetDictionary("/Resources"), scope);
                                var formInitial = gs.Clone();
                                formInitial.Ctm = formCtm;
                                byte[]? formRawForBi = ContainsInlineImageOperator(formSeq) ? rawBytes : null;
                                var formResult = FilterContentSequence(
                                    ownerDoc,
                                    formSeq,
                                    formRawForBi,
                                    formInitial,
                                    formScope,
                                    pageBox,
                                    pageRotate,
                                    normRedactions,
                                    oneBasedPageNumber,
                                    stripNonTextContent,
                                    activeForms,
                                    ref remappedCounter);

                                if (!formResult.Modified)
                                {
                                    filtered.Add(op);
                                    break;
                                }

                                modified = true;
                                var clonedForm = CloneFormWithFilteredContent(
                                    ownerDoc,
                                    xObjDict,
                                    formResult.Sequence,
                                    formResult.KeptInlineImages,
                                    formResult.RemappedXObjects);

                                string baseName = xObjCName.Name.StartsWith('/') ? xObjCName.Name : "/" + xObjCName.Name;
                                string uniqueName;
                                do
                                {
                                    remappedCounter++;
                                    uniqueName = $"{baseName}_redacted_{remappedCounter}";
                                }
                                while (scope.ContainsXObjectName(uniqueName) || remappedXObjects.ContainsKey(uniqueName));

                                remappedXObjects[uniqueName] = clonedForm;
                                var newDoOp = OpCodes.OperatorFromName("Do");
                                newDoOp.Operands.Add(new CName(uniqueName));
                                filtered.Add(newDoOp);
                            }
                            finally
                            {
                                activeForms.Remove(xObjDict);
                            }
                            break;
                        }
                    }

                    filtered.Add(op);
                    break;
                }

                case "BI":
                {
                    FlushPathVerbatim();
                    int biIdx = inlineImageCursor++;
                    if (stripNonTextContent)
                    {
                        var imgBox = TransformUnitSquareToDisplayBox(gs.Ctm, pageBox, pageRotate);
                        if (IntersectsAny(imgBox, normRedactions))
                        {
                            skippingInlineImage = true;
                            modified = true;
                            break;
                        }
                    }
                    if (rawInlineSlices is not null && biIdx < rawInlineSlices.Count)
                    {
                        keptInlineImages[op] = rawInlineSlices[biIdx];
                    }
                    filtered.Add(op);
                    break;
                }

                // Path construction & clipping modifiers
                case "m":
                    pathOps.Add(op);
                    if (op.Operands.Count >= 2 &&
                        TryReadNumber(op.Operands[0], out double mx) &&
                        TryReadNumber(op.Operands[1], out double my))
                    {
                        if (currentSubpath is { Count: > 0 })
                        {
                            subpaths.Add(currentSubpath);
                        }
                        currentSubpath = [(mx, my)];
                        curX = mx; curY = my;
                        startX = mx; startY = my;
                        hasCurPoint = true;
                        pathPoints.Add((mx, my));
                    }
                    break;

                case "l":
                    pathOps.Add(op);
                    if (op.Operands.Count >= 2 &&
                        TryReadNumber(op.Operands[0], out double lx) &&
                        TryReadNumber(op.Operands[1], out double ly))
                    {
                        if (hasCurPoint) pathSegments.Add(new PathSegment(curX, curY, lx, ly));
                        currentSubpath ??= hasCurPoint ? [(curX, curY)] : [];
                        currentSubpath.Add((lx, ly));
                        curX = lx; curY = ly;
                        hasCurPoint = true;
                        pathPoints.Add((lx, ly));
                    }
                    break;

                case "c":
                    pathOps.Add(op);
                    if (op.Operands.Count >= 6 &&
                        TryReadNumber(op.Operands[0], out double cx1) &&
                        TryReadNumber(op.Operands[1], out double cy1) &&
                        TryReadNumber(op.Operands[2], out double cx2) &&
                        TryReadNumber(op.Operands[3], out double cy2) &&
                        TryReadNumber(op.Operands[4], out double cx3) &&
                        TryReadNumber(op.Operands[5], out double cy3))
                    {
                        if (hasCurPoint)
                        {
                            currentSubpath ??= [(curX, curY)];
                            FlattenCubicBezier(curX, curY, cx1, cy1, cx2, cy2, cx3, cy3, pathPoints, currentSubpath, pathSegments);
                        }
                        curX = cx3; curY = cy3;
                        hasCurPoint = true;
                    }
                    break;

                case "v":
                    pathOps.Add(op);
                    if (op.Operands.Count >= 4 &&
                        TryReadNumber(op.Operands[0], out double vx2) &&
                        TryReadNumber(op.Operands[1], out double vy2) &&
                        TryReadNumber(op.Operands[2], out double vx3) &&
                        TryReadNumber(op.Operands[3], out double vy3))
                    {
                        if (hasCurPoint)
                        {
                            currentSubpath ??= [(curX, curY)];
                            FlattenCubicBezier(curX, curY, curX, curY, vx2, vy2, vx3, vy3, pathPoints, currentSubpath, pathSegments);
                        }
                        curX = vx3; curY = vy3;
                        hasCurPoint = true;
                    }
                    break;

                case "y":
                    pathOps.Add(op);
                    if (op.Operands.Count >= 4 &&
                        TryReadNumber(op.Operands[0], out double yx1) &&
                        TryReadNumber(op.Operands[1], out double yy1) &&
                        TryReadNumber(op.Operands[2], out double yx3) &&
                        TryReadNumber(op.Operands[3], out double yy3))
                    {
                        if (hasCurPoint)
                        {
                            currentSubpath ??= [(curX, curY)];
                            FlattenCubicBezier(curX, curY, yx1, yy1, yx3, yy3, yx3, yy3, pathPoints, currentSubpath, pathSegments);
                        }
                        curX = yx3; curY = yy3;
                        hasCurPoint = true;
                    }
                    break;

                case "h":
                    pathOps.Add(op);
                    if (hasCurPoint)
                    {
                        pathSegments.Add(new PathSegment(curX, curY, startX, startY));
                        curX = startX; curY = startY;
                    }
                    if (currentSubpath is { Count: > 0 })
                    {
                        subpaths.Add(currentSubpath);
                        currentSubpath = null;
                    }
                    break;

                case "re":
                    pathOps.Add(op);
                    if (op.Operands.Count >= 4 &&
                        TryReadNumber(op.Operands[0], out double rx) &&
                        TryReadNumber(op.Operands[1], out double ry) &&
                        TryReadNumber(op.Operands[2], out double rw) &&
                        TryReadNumber(op.Operands[3], out double rh))
                    {
                        if (currentSubpath is { Count: > 0 })
                        {
                            subpaths.Add(currentSubpath);
                            currentSubpath = null;
                        }
                        double rx2 = rx + rw;
                        double ry2 = ry + rh;
                        subpaths.Add([(rx, ry), (rx2, ry), (rx2, ry2), (rx, ry2)]);
                        pathPoints.Add((rx, ry));
                        pathPoints.Add((rx2, ry));
                        pathPoints.Add((rx2, ry2));
                        pathPoints.Add((rx, ry2));
                        pathSegments.Add(new PathSegment(rx, ry, rx2, ry));
                        pathSegments.Add(new PathSegment(rx2, ry, rx2, ry2));
                        pathSegments.Add(new PathSegment(rx2, ry2, rx, ry2));
                        pathSegments.Add(new PathSegment(rx, ry2, rx, ry));
                        curX = rx; curY = ry;
                        startX = rx; startY = ry;
                        hasCurPoint = true;
                    }
                    break;

                case "W":
                case "W*":
                    pathOps.Add(op);
                    pathHasClip = true;
                    break;

                case "n":
                    FlushPathVerbatim();
                    filtered.Add(op);
                    break;

                case "S":
                case "s":
                {
                    if (name == "s" && hasCurPoint)
                    {
                        pathSegments.Add(new PathSegment(curX, curY, startX, startY));
                    }

                    bool stripStroke = stripNonTextContent &&
                        StrokePathIntersectsRedactions(pathSegments, pathPoints, gs.Ctm, gs.LineWidth, pageBox, pageRotate, normRedactions);

                    if (stripStroke)
                    {
                        modified = true;
                        if (pathHasClip)
                        {
                            foreach (var pOp in pathOps) filtered.Add(pOp);
                            filtered.Add(OpCodes.OperatorFromName("n"));
                        }
                        ResetPath();
                    }
                    else
                    {
                        FlushPathVerbatim();
                        filtered.Add(op);
                    }
                    break;
                }

                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                {
                    if ((name == "b" || name == "b*") && hasCurPoint)
                    {
                        pathSegments.Add(new PathSegment(curX, curY, startX, startY));
                    }
                    if (currentSubpath is { Count: > 0 })
                    {
                        subpaths.Add(currentSubpath);
                        currentSubpath = null;
                    }

                    bool useEvenOdd = name.EndsWith('*');
                    bool checkStroke = name is "B" or "B*" or "b" or "b*";
                    bool stripFill = stripNonTextContent &&
                        ((subpaths.Count > 0 && FilledPathIntersectsRedactions(subpaths, gs.Ctm, useEvenOdd, pageBox, pageRotate, normRedactions)) ||
                         (checkStroke && StrokePathIntersectsRedactions(pathSegments, pathPoints, gs.Ctm, gs.LineWidth, pageBox, pageRotate, normRedactions)));

                    if (stripFill)
                    {
                        modified = true;
                        if (pathHasClip)
                        {
                            foreach (var pOp in pathOps) filtered.Add(pOp);
                            filtered.Add(OpCodes.OperatorFromName("n"));
                        }
                        ResetPath();
                    }
                    else
                    {
                        FlushPathVerbatim();
                        filtered.Add(op);
                    }
                    break;
                }

                default:
                    FlushPathVerbatim();
                    filtered.Add(op);
                    break;
            }
        }

        FlushPathVerbatim();
        return new SequenceFilterResult
        {
            Sequence = filtered,
            Modified = modified,
            RemappedXObjects = remappedXObjects,
            KeptInlineImages = keptInlineImages
        };
    }

    private sealed class CStringFilterResult
    {
        public bool AnyRemoved { get; init; }
        public bool AnyKept { get; init; }
        public double TotalAdvance { get; init; }
        public CArray? TjArray { get; init; }
    }

    private static CStringFilterResult FilterCString(
        CString cStr,
        Matrix2D combined,
        StripGState gs,
        PdfFontMetrics metrics,
        bool fontKnown,
        PdfRect pageBox,
        int pageRotate,
        IReadOnlyList<PdfRect> normRedactions)
    {
        string text = cStr.Value ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return new CStringFilterResult
            {
                AnyRemoved = false,
                AnyKept = false,
                TotalAdvance = 0.0,
                TjArray = null
            };
        }

        bool isTwoByte = metrics.IsTwoByte && text.Length >= 2 && text.Length % 2 == 0;
        if (!fontKnown && !isTwoByte && text.Length >= 2 && text.Length % 2 == 0)
        {
            if (cStr.CStringType == CStringType.HexString)
            {
                isTwoByte = true;
            }
            else
            {
                int evenZeros = 0;
                int pairs = text.Length / 2;
                for (int p = 0; p < pairs; p++)
                {
                    if (text[p * 2] == '\0') evenZeros++;
                }
                if (evenZeros * 2 >= pairs) isTwoByte = true;
            }
        }

        int stride = isTwoByte ? 2 : 1;
        int glyphCount = text.Length / stride;
        if (glyphCount == 0)
        {
            return new CStringFilterResult
            {
                AnyRemoved = false,
                AnyKept = false,
                TotalAdvance = 0.0,
                TjArray = null
            };
        }

        double absFs = Math.Abs(gs.FontSize) > 0 ? Math.Abs(gs.FontSize) : 12.0;
        double fsSign = gs.FontSize < 0 ? -1.0 : 1.0;
        double hScaleRatio = gs.HorizontalScale / 100.0;

        double xOffset = 0.0;
        var keptRun = new List<char>(text.Length);
        var tjArray = new CArray();
        double pendingSkipAdv = 0.0;
        bool anyRemoved = false;
        bool anyKept = false;

        double tjDenom = fsSign * absFs * (Math.Abs(hScaleRatio) > 1e-9 ? hScaleRatio : 1.0);

        void FlushSkip()
        {
            if (Math.Abs(pendingSkipAdv) > 1e-6)
            {
                double tjNum = -(pendingSkipAdv * 1000.0) / tjDenom;
                tjArray.Add(new CReal { Value = Math.Round(tjNum, 3) });
                pendingSkipAdv = 0.0;
            }
        }

        void FlushKeptRun()
        {
            if (keptRun.Count > 0)
            {
                tjArray.Add(new CString
                {
                    Value = new string([.. keptRun]),
                    CStringType = CStringType.String
                });
                keptRun.Clear();
            }
        }

        for (int g = 0; g < glyphCount; g++)
        {
            int code = stride == 2
                ? (((byte)text[g * 2]) << 8) | (byte)text[g * 2 + 1]
                : (byte)text[g];

            double adv = metrics.GetAdvanceTextSpace(code, absFs, gs.CharSpacing, gs.WordSpacing, hScaleRatio, fsSign);
            double x0 = xOffset;
            double x1 = xOffset + adv;
            double y0 = -absFs * 0.2 * fsSign;
            double y1 = absFs * 0.85 * fsSign;

            var charBox = TransformTextRectToDisplayBox(x0, y0, x1, y1, combined, pageBox, pageRotate);
            bool hit = GlyphIntersectsAny(charBox, normRedactions);

            if (hit)
            {
                anyRemoved = true;
                FlushKeptRun();
                pendingSkipAdv += adv;
            }
            else
            {
                anyKept = true;
                FlushSkip();
                for (int k = 0; k < stride; k++)
                {
                    keptRun.Add(text[g * stride + k]);
                }
            }

            xOffset += adv;
        }

        if (!anyRemoved)
        {
            return new CStringFilterResult
            {
                AnyRemoved = false,
                AnyKept = true,
                TotalAdvance = xOffset,
                TjArray = null
            };
        }

        FlushKeptRun();
        FlushSkip();

        return new CStringFilterResult
        {
            AnyRemoved = true,
            AnyKept = anyKept,
            TotalAdvance = xOffset,
            TjArray = tjArray
        };
    }

    private static PdfDictionary CloneFormWithFilteredContent(
        PdfDocument ownerDoc,
        PdfDictionary originalForm,
        CSequence filteredSequence,
        IReadOnlyDictionary<COperator, byte[]> keptInlineImages,
        IReadOnlyDictionary<string, PdfDictionary> childRemappedXObjects)
    {
        var cloned = originalForm.Clone();
        byte[] newContent = keptInlineImages.Count > 0
            ? SerializeSequenceWithInlineImages(filteredSequence, keptInlineImages)
            : filteredSequence.ToContent();
        if (cloned.Stream is not null)
        {
            cloned.Stream.Value = newContent;
        }
        else
        {
            cloned.CreateStream(newContent);
        }

        cloned.Elements.Remove("/Filter");
        cloned.Elements.Remove("/DecodeParms");
        cloned.Elements.SetInteger("/Length", newContent.Length);

        if (childRemappedXObjects.Count > 0)
        {
            var origRes = originalForm.Elements.GetDictionary("/Resources");
            var clonedRes = origRes is not null ? origRes.Clone() : new PdfDictionary(ownerDoc);
            var origXObjs = origRes?.Elements.GetDictionary("/XObject");
            var clonedXObjs = origXObjs is not null ? origXObjs.Clone() : new PdfDictionary(ownerDoc);

            foreach (var (name, dict) in childRemappedXObjects)
            {
                clonedXObjs.Elements[name] = dict.Reference ?? (PdfItem)dict;
            }

            clonedRes.Elements["/XObject"] = clonedXObjs;
            cloned.Elements["/Resources"] = clonedRes;
        }

        ownerDoc.Internals.AddObject(cloned);
        return cloned;
    }

    private static bool ContainsInlineImageOperator(CSequence sequence)
    {
        foreach (var obj in sequence)
        {
            if (obj is COperator op && string.Equals(op.OpCode.Name, "BI", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] ReadCombinedPageContentBytes(PdfPage page)
    {
        using var ms = new MemoryStream();
        foreach (var item in page.Contents.Elements)
        {
            var dict = ResolveDictionary(item);
            if (dict?.Stream is not null)
            {
                byte[] part = dict.Stream.UnfilteredValue;
                if (part.Length > 0)
                {
                    ms.Write(part);
                    ms.WriteByte((byte)'\n');
                }
            }
        }
        return ms.ToArray();
    }

    private static List<byte[]> ExtractInlineImageSpans(byte[] raw)
    {
        static bool IsWs(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        static string? NextToken(byte[] data, ref int pos, out int tokStart)
        {
            tokStart = pos;
            while (pos < data.Length)
            {
                byte b = data[pos];
                if (IsWs(b))
                {
                    pos++;
                    continue;
                }
                if (b == (byte)'%')
                {
                    while (pos < data.Length && data[pos] != (byte)'\n' && data[pos] != (byte)'\r') pos++;
                    continue;
                }
                if (b == (byte)'(')
                {
                    pos++;
                    int depth = 1;
                    while (pos < data.Length && depth > 0)
                    {
                        byte c = data[pos++];
                        if (c == (byte)'\\' && pos < data.Length)
                        {
                            pos++;
                        }
                        else if (c == (byte)'(')
                        {
                            depth++;
                        }
                        else if (c == (byte)')')
                        {
                            depth--;
                        }
                    }
                    continue;
                }
                if (b == (byte)'<')
                {
                    if (pos + 1 < data.Length && data[pos + 1] == (byte)'<')
                    {
                        pos += 2;
                    }
                    else
                    {
                        pos++;
                        while (pos < data.Length && data[pos] != (byte)'>') pos++;
                        if (pos < data.Length) pos++;
                    }
                    continue;
                }
                if (b == (byte)'>')
                {
                    pos += (pos + 1 < data.Length && data[pos + 1] == (byte)'>') ? 2 : 1;
                    continue;
                }
                if (b == (byte)'/')
                {
                    pos++;
                    while (pos < data.Length && !IsWs(data[pos]) && !IsDelim(data[pos])) pos++;
                    continue;
                }
                if (IsDelim(b))
                {
                    pos++;
                    continue;
                }

                tokStart = pos;
                while (pos < data.Length && !IsWs(data[pos]) && !IsDelim(data[pos])) pos++;
                return System.Text.Encoding.ASCII.GetString(data, tokStart, pos - tokStart);
            }
            return null;
        }

        var slices = new List<byte[]>();
        int p = 0;
        while (p < raw.Length)
        {
            string? tok = NextToken(raw, ref p, out int biStart);
            if (tok is null) break;
            if (!string.Equals(tok, "BI", StringComparison.Ordinal)) continue;

            bool foundId = false;
            while (p < raw.Length)
            {
                string? innerTok = NextToken(raw, ref p, out _);
                if (innerTok is null) break;
                if (string.Equals(innerTok, "ID", StringComparison.Ordinal))
                {
                    foundId = true;
                    break;
                }
            }

            if (!foundId) break;

            int afterId = p;
            int scan = afterId + 1;
            bool foundEi = false;
            while (scan + 1 < raw.Length)
            {
                if (IsWs(raw[scan - 1]) &&
                    raw[scan] == (byte)'E' &&
                    raw[scan + 1] == (byte)'I' &&
                    (scan + 2 >= raw.Length || IsWs(raw[scan + 2]) || raw[scan + 2] == (byte)'%'))
                {
                    int eiEnd = scan + 2;
                    slices.Add(raw[biStart..eiEnd]);
                    p = eiEnd;
                    foundEi = true;
                    break;
                }
                scan++;
            }

            if (!foundEi) break;
        }

        return slices;
    }

    private static byte[] SerializeSequenceWithInlineImages(
        CSequence sequence,
        IReadOnlyDictionary<COperator, byte[]> keptInlineImages)
    {
        using var ms = new MemoryStream();
        var chunk = new CSequence();
        bool skippingEiAfterBi = false;

        void FlushChunk()
        {
            if (chunk.Count == 0) return;
            byte[] bytes = chunk.ToContent();
            if (bytes.Length > 0)
            {
                ms.Write(bytes);
                if (bytes[^1] != (byte)'\n') ms.WriteByte((byte)'\n');
            }
            chunk.Clear();
        }

        foreach (var obj in sequence)
        {
            if (obj is COperator op)
            {
                string name = op.OpCode.Name;
                if (skippingEiAfterBi)
                {
                    if (name == "EI")
                    {
                        skippingEiAfterBi = false;
                    }
                    continue;
                }

                if (name == "BI" && keptInlineImages.TryGetValue(op, out byte[]? rawSlice))
                {
                    FlushChunk();
                    ms.Write(rawSlice);
                    ms.WriteByte((byte)'\n');
                    skippingEiAfterBi = true;
                    continue;
                }
            }

            if (!skippingEiAfterBi)
            {
                chunk.Add(obj);
            }
        }

        FlushChunk();
        return ms.ToArray();
    }

    private static Matrix2D ReadPdfArrayMatrix(PdfArray? arr)
    {
        if (arr is null || arr.Elements.Count < 6) return Matrix2D.Identity;
        if (TryGetPdfNum(arr.Elements[0], out double a) &&
            TryGetPdfNum(arr.Elements[1], out double b) &&
            TryGetPdfNum(arr.Elements[2], out double c) &&
            TryGetPdfNum(arr.Elements[3], out double d) &&
            TryGetPdfNum(arr.Elements[4], out double e) &&
            TryGetPdfNum(arr.Elements[5], out double f))
        {
            return new Matrix2D(a, b, c, d, e, f);
        }
        return Matrix2D.Identity;
    }

    private static bool TryReadPdfRectArray(PdfArray? arr, out PdfRect rect)
    {
        if (arr is not null && arr.Elements.Count >= 4 &&
            TryGetPdfNum(arr.Elements[0], out double x1) &&
            TryGetPdfNum(arr.Elements[1], out double y1) &&
            TryGetPdfNum(arr.Elements[2], out double x2) &&
            TryGetPdfNum(arr.Elements[3], out double y2))
        {
            rect = new PdfRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            return true;
        }
        rect = new PdfRect(0, 0, 0, 0);
        return false;
    }

    private static bool TryGetPdfNum(PdfItem? item, out double val)
    {
        if (item is PdfReference r) item = r.Value;
        switch (item)
        {
            case PdfInteger pi:
                val = pi.Value;
                return true;
            case PdfReal pr:
                val = pr.Value;
                return true;
            default:
                val = 0;
                return false;
        }
    }

    private static (double X, double Y) ToDisplayPoint(double px, double py, PdfRect pageBox, int pageRotate)
    {
        double w = pageBox.Right - pageBox.Left;
        double h = pageBox.Bottom - pageBox.Top;
        double rx = px - pageBox.Left;
        double ry = py - pageBox.Top;

        return pageRotate switch
        {
            90 => (ry, rx),
            180 => (w - rx, ry),
            270 => (h - ry, w - rx),
            _ => (rx, h - ry)
        };
    }

    private static PdfRect TransformCornersToDisplayBox(
        ReadOnlySpan<(double X, double Y)> userCorners,
        Matrix2D matrix,
        PdfRect pageBox,
        int pageRotate)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        foreach (var (ux, uy) in userCorners)
        {
            var (px, py) = matrix.Transform(ux, uy);
            var (dx, dy) = ToDisplayPoint(px, py, pageBox, pageRotate);
            if (dx < minX) minX = dx;
            if (dy < minY) minY = dy;
            if (dx > maxX) maxX = dx;
            if (dy > maxY) maxY = dy;
        }

        return new PdfRect(minX, minY, maxX, maxY);
    }

    private static PdfRect TransformTextRectToDisplayBox(
        double x0, double y0, double x1, double y1,
        Matrix2D combined,
        PdfRect pageBox,
        int pageRotate)
    {
        Span<(double X, double Y)> corners = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];
        return TransformCornersToDisplayBox(corners, combined, pageBox, pageRotate);
    }

    private static PdfRect TransformUnitSquareToDisplayBox(Matrix2D ctm, PdfRect pageBox, int pageRotate)
    {
        Span<(double X, double Y)> corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
        return TransformCornersToDisplayBox(corners, ctm, pageBox, pageRotate);
    }

    private static PdfRect TransformPdfRectToDisplayBox(PdfRect rect, Matrix2D matrix, PdfRect pageBox, int pageRotate)
    {
        Span<(double X, double Y)> corners =
        [
            (rect.Left, rect.Top),
            (rect.Right, rect.Top),
            (rect.Right, rect.Bottom),
            (rect.Left, rect.Bottom)
        ];
        return TransformCornersToDisplayBox(corners, matrix, pageBox, pageRotate);
    }

    private static void FlattenCubicBezier(
        double x0, double y0,
        double x1, double y1,
        double x2, double y2,
        double x3, double y3,
        List<(double X, double Y)> points,
        List<(double X, double Y)> subpath,
        List<PathSegment> segments)
    {
        const int steps = 8;
        double prevX = x0, prevY = y0;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double mt = 1.0 - t;
            double nx = (mt * mt * mt * x0) + (3 * mt * mt * t * x1) + (3 * mt * t * t * x2) + (t * t * t * x3);
            double ny = (mt * mt * mt * y0) + (3 * mt * mt * t * y1) + (3 * mt * t * t * y2) + (t * t * t * y3);
            segments.Add(new PathSegment(prevX, prevY, nx, ny));
            points.Add((nx, ny));
            subpath.Add((nx, ny));
            prevX = nx;
            prevY = ny;
        }
    }

    private static bool StrokePathIntersectsRedactions(
        List<PathSegment> segments,
        List<(double X, double Y)> points,
        Matrix2D ctm,
        double lineWidth,
        PdfRect pageBox,
        int pageRotate,
        IReadOnlyList<PdfRect> redactions)
    {
        double scale = Math.Max(
            Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B),
            Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D));
        double expand = Math.Max(0.5, lineWidth * scale * 0.5);

        if (segments.Count == 0)
        {
            if (points.Count == 0) return false;
            foreach (var (px, py) in points)
            {
                var (tx, ty) = ctm.Transform(px, py);
                var (dx, dy) = ToDisplayPoint(tx, ty, pageBox, pageRotate);
                var dotBox = new PdfRect(dx - expand, dy - expand, dx + expand, dy + expand);
                if (IntersectsAny(dotBox, redactions)) return true;
            }
            return false;
        }

        foreach (var seg in segments)
        {
            var (tx1, ty1) = ctm.Transform(seg.X1, seg.Y1);
            var (tx2, ty2) = ctm.Transform(seg.X2, seg.Y2);
            var (dx1, dy1) = ToDisplayPoint(tx1, ty1, pageBox, pageRotate);
            var (dx2, dy2) = ToDisplayPoint(tx2, ty2, pageBox, pageRotate);

            foreach (var r in redactions)
            {
                var expanded = new PdfRect(r.Left - expand, r.Top - expand, r.Right + expand, r.Bottom + expand);
                if (SegmentIntersectsRect(dx1, dy1, dx2, dy2, expanded))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool FilledPathIntersectsRedactions(
        List<List<(double X, double Y)>> subpaths,
        Matrix2D ctm,
        bool useEvenOdd,
        PdfRect pageBox,
        int pageRotate,
        IReadOnlyList<PdfRect> redactions)
    {
        if (subpaths.Count == 0) return false;

        var displaySubpaths = new List<List<(double X, double Y)>>(subpaths.Count);
        foreach (var sp in subpaths)
        {
            if (sp.Count == 0) continue;
            var transformed = new List<(double X, double Y)>(sp.Count);
            foreach (var (px, py) in sp)
            {
                var (tx, ty) = ctm.Transform(px, py);
                transformed.Add(ToDisplayPoint(tx, ty, pageBox, pageRotate));
            }
            displaySubpaths.Add(transformed);
        }

        if (displaySubpaths.Count == 0) return false;

        foreach (var r in redactions)
        {
            foreach (var poly in displaySubpaths)
            {
                foreach (var (vx, vy) in poly)
                {
                    if (vx >= r.Left && vx <= r.Right && vy >= r.Top && vy <= r.Bottom)
                    {
                        return true;
                    }
                }

                if (poly.Count >= 2)
                {
                    int edgeCount = poly.Count >= 3 ? poly.Count : poly.Count - 1;
                    for (int i = 0; i < edgeCount; i++)
                    {
                        var p1 = poly[i];
                        var p2 = poly[(i + 1) % poly.Count];
                        if (SegmentIntersectsRect(p1.X, p1.Y, p2.X, p2.Y, r))
                        {
                            return true;
                        }
                    }
                }
            }

            double cx = (r.Left + r.Right) * 0.5;
            double cy = (r.Top + r.Bottom) * 0.5;
            if (IsPointFilled(cx, cy, displaySubpaths, useEvenOdd))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPointFilled(
        double px,
        double py,
        List<List<(double X, double Y)>> polys,
        bool useEvenOdd)
    {
        bool insideEvenOdd = false;
        int winding = 0;

        foreach (var poly in polys)
        {
            if (poly.Count < 3) continue;
            for (int i = 0; i < poly.Count; i++)
            {
                var (x1, y1) = poly[i];
                var (x2, y2) = poly[(i + 1) % poly.Count];
                if ((y1 > py) == (y2 > py)) continue;

                double xIntersect = x1 + ((py - y1) * (x2 - x1)) / (y2 - y1);
                if (px < xIntersect)
                {
                    if (useEvenOdd)
                    {
                        insideEvenOdd = !insideEvenOdd;
                    }
                    else
                    {
                        winding += y2 > y1 ? 1 : -1;
                    }
                }
            }
        }

        return useEvenOdd ? insideEvenOdd : winding != 0;
    }

    private static bool SegmentIntersectsRect(double x1, double y1, double x2, double y2, PdfRect r)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double t0 = 0.0, t1 = 1.0;

        static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0.0;
            double t = q / p;
            if (p < 0.0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }

        if (!Clip(-dx, x1 - r.Left, ref t0, ref t1)) return false;
        if (!Clip(dx, r.Right - x1, ref t0, ref t1)) return false;
        if (!Clip(-dy, y1 - r.Top, ref t0, ref t1)) return false;
        if (!Clip(dy, r.Bottom - y1, ref t0, ref t1)) return false;
        return t0 <= t1;
    }

    private static bool GlyphIntersectsAny(PdfRect charBox, IReadOnlyList<PdfRect> redactions)
    {
        double cw = charBox.Right - charBox.Left;
        double ch = charBox.Bottom - charBox.Top;
        double cx = (charBox.Left + charBox.Right) * 0.5;
        double cy = (charBox.Top + charBox.Bottom) * 0.5;

        foreach (var r in redactions)
        {
            if (charBox.Right < r.Left || charBox.Left > r.Right ||
                charBox.Bottom < r.Top || charBox.Top > r.Bottom)
            {
                continue;
            }

            if (cx >= r.Left && cx <= r.Right && cy >= r.Top && cy <= r.Bottom)
            {
                return true;
            }

            double ox = Math.Min(charBox.Right, r.Right) - Math.Max(charBox.Left, r.Left);
            double oy = Math.Min(charBox.Bottom, r.Bottom) - Math.Max(charBox.Top, r.Top);
            bool xHit = cw <= 1e-3 || ox > cw * 0.25;
            bool yHit = ch <= 1e-3 || oy > ch * 0.25;
            if (xHit && yHit)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IntersectsAny(PdfRect box, IReadOnlyList<PdfRect> redactions)
    {
        foreach (var r in redactions)
        {
            if (box.Right >= r.Left && box.Left <= r.Right &&
                box.Bottom >= r.Top && box.Top <= r.Bottom)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryReadMatrix(CSequence operands, out Matrix2D matrix)
    {
        if (operands.Count >= 6 &&
            TryReadNumber(operands[0], out double a) &&
            TryReadNumber(operands[1], out double b) &&
            TryReadNumber(operands[2], out double c) &&
            TryReadNumber(operands[3], out double d) &&
            TryReadNumber(operands[4], out double e) &&
            TryReadNumber(operands[5], out double f))
        {
            matrix = new Matrix2D(a, b, c, d, e, f);
            return true;
        }
        matrix = Matrix2D.Identity;
        return false;
    }

    private static bool TryReadNumber(CObject obj, out double value)
    {
        switch (obj)
        {
            case CReal r:
                value = r.Value;
                return true;
            case CInteger i:
                value = i.Value;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static XColor ParseColor(string hex, XColor fallback, byte? alphaOverride = null)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[0..2], NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, null, out byte b))
        {
            return XColor.FromArgb(alphaOverride ?? 255, r, g, b);
        }
        return fallback;
    }
}
