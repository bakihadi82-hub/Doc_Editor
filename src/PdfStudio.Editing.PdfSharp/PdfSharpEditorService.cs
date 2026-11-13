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
                double pageHeightPt = page.Height.Point;

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

                    var boundsPt = ParseTopLeftRect(dict.Elements.GetArray("/Rect"), pageHeightPt);
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
                        }

                        var pageAnnotations = plan.Annotations.Where(a => a.PageIndex == displayIdx).ToList();
                        var pageRedactions = burnRedactions
                            ? pageAnnotations.Where(a => a.Kind == AnnotationKind.Redaction).ToList()
                            : [];
                        var pageFields = plan.FormFields.Where(f => f.PageIndex == displayIdx).ToList();

                        // 1. Burn true redactions by stripping covered text bytes from content stream first
                        if (pageRedactions.Count > 0)
                        {
                            StripRedactedContentFromPage(added, pageRedactions);
                        }

                        // 2. Render visual annotations, redaction blackouts, and form values
                        bool hasNonRedactionAnnots = pageAnnotations.Any(a => a.Kind != AnnotationKind.Redaction);
                        if (hasNonRedactionAnnots || pageRedactions.Count > 0 || (flattenForms && pageFields.Count > 0))
                        {
                            using var gfx = XGraphics.FromPdfPage(added, XGraphicsPdfPageOptions.Append);
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

                    // 4. Password protection
                    if (!string.IsNullOrEmpty(saveOptions?.UserPassword) || !string.IsNullOrEmpty(saveOptions?.OwnerPassword))
                    {
                        string? userPwd = saveOptions?.UserPassword;
                        string ownerPwd = !string.IsNullOrEmpty(saveOptions?.OwnerPassword)
                            ? saveOptions!.OwnerPassword!
                            : userPwd!;

                        output.SecuritySettings.DocumentSecurityLevel = PdfDocumentSecurityLevel.Encrypted128Bit;
                        if (!string.IsNullOrEmpty(userPwd))
                        {
                            output.SecuritySettings.UserPassword = userPwd;
                        }
                        output.SecuritySettings.OwnerPassword = ownerPwd;
                        output.SecuritySettings.PermitPrint = true;
                        output.SecuritySettings.PermitFullQualityPrint = true;
                        output.SecuritySettings.PermitModifyDocument = true;
                        output.SecuritySettings.PermitAnnotations = true;
                        output.SecuritySettings.PermitFormsFill = true;
                        output.SecuritySettings.PermitExtractContent = true;
                        output.SecuritySettings.PermitAssembleDocument = true;
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

    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputFilePath, CancellationToken ct = default)
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
                        using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
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

    public Task ExtractPagesAsync(string sourceFilePath, IReadOnlyList<int> zeroBasedPageIndices, string outputFilePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string tempPath = Path.Combine(
                Path.GetDirectoryName(outputFilePath)!,
                $".{Path.GetFileName(outputFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var input = PdfReader.Open(sourceFilePath, PdfDocumentOpenMode.Import))
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

    public Task SplitEveryNPagesAsync(string sourceFilePath, int pagesPerFile, string outputDirectory, string filePrefix, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerFile);
        return Task.Run(() =>
        {
            Directory.CreateDirectory(outputDirectory);
            using var input = PdfReader.Open(sourceFilePath, PdfDocumentOpenMode.Import);
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

    private static PdfRect ParseTopLeftRect(PdfArray? rectArray, double pageHeightPt)
    {
        if (rectArray is null || rectArray.Elements.Count < 4)
        {
            return new PdfRect(0, 0, 0, 0);
        }

        double x1 = GetReal(rectArray.Elements[0]);
        double y1 = GetReal(rectArray.Elements[1]);
        double x2 = GetReal(rectArray.Elements[2]);
        double y2 = GetReal(rectArray.Elements[3]);

        double left = Math.Min(x1, x2);
        double right = Math.Max(x1, x2);
        double topFromBottom = Math.Max(y1, y2);
        double bottomFromBottom = Math.Min(y1, y2);

        double top = pageHeightPt - topFromBottom;
        double bottom = pageHeightPt - bottomFromBottom;
        return new PdfRect(left, top, right, bottom);
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
                gfx.DrawRectangle(new XSolidBrush(color), rect.Left, rect.Top, w, h);
                break;
            }
            case AnnotationKind.Underline:
            {
                var color = ParseColor(ann.ColorHex, XColor.FromArgb(255, 234, 179, 8));
                double y = rect.Bottom;
                gfx.DrawLine(new XPen(color, Math.Max(1.0, ann.StrokeWidthPt)), rect.Left, y, rect.Right, y);
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
                var font = new XFont("Arial", fontSize, XFontStyle.Regular);
                gfx.DrawString(text, font, new XSolidBrush(color), new XPoint(rect.Left + 2, rect.Top + fontSize + 2));
                break;
            }
            case AnnotationKind.StickyNote:
            {
                var fill = XColor.FromArgb(245, 254, 240, 138);
                var border = XColor.FromArgb(255, 202, 138, 4);
                gfx.DrawRoundedRectangle(new XPen(border, 1), new XSolidBrush(fill), rect.Left, rect.Top, Math.Max(w, 110), Math.Max(h, 48), 4, 4);
                string noteText = string.IsNullOrWhiteSpace(ann.TextContent) ? "Note" : ann.TextContent;
                var font = new XFont("Arial", 9.5, XFontStyle.Regular);
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
            var font = new XFont("Arial", fontSize, XFontStyle.Regular);
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
        double pageHeightPt = page.Height.Point;
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
                widget.Elements["/Rect"] = new PdfRectangle(
                    new XPoint(r.Left, pageHeightPt - r.Bottom),
                    new XPoint(r.Right, pageHeightPt - r.Top));
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

    /// <summary>
    /// Strips underlying PDF text operators/glyph strings that intersect any redaction box
    /// so redacted text is permanently erased from the PDF content stream.
    /// </summary>
    private static void StripRedactedContentFromPage(PdfPage page, IReadOnlyList<PdfAnnotation> redactions)
    {
        if (redactions.Count == 0) return;

        double pageHeightPt = page.Height.Point;
        var normRedactions = redactions
            .Select(r => NormalizeRect(r.BoundsPt))
            .Select(r => new PdfRect(r.Left - 2.0, r.Top - 2.0, r.Right + 2.0, r.Bottom + 2.0))
            .ToList();

        CSequence sequence;
        try
        {
            sequence = ContentReader.ReadContent(page);
        }
        catch
        {
            return;
        }

        var filtered = new CSequence();
        var ctmStack = new Stack<Matrix2D>();
        var ctm = Matrix2D.Identity;
        var tlm = Matrix2D.Identity;
        var tm = Matrix2D.Identity;
        double fontSize = 12.0;
        double leading = 14.0;
        bool inText = false;

        foreach (var obj in sequence)
        {
            if (obj is not COperator op)
            {
                filtered.Add(obj);
                continue;
            }

            string name = op.OpCode.Name;
            switch (name)
            {
                case "q":
                    ctmStack.Push(ctm);
                    filtered.Add(op);
                    break;
                case "Q":
                    if (ctmStack.Count > 0) ctm = ctmStack.Pop();
                    filtered.Add(op);
                    break;
                case "cm":
                    if (TryReadMatrix(op.Operands, out var cmMat))
                    {
                        ctm = Matrix2D.Multiply(cmMat, ctm);
                    }
                    filtered.Add(op);
                    break;
                case "BT":
                    inText = true;
                    tlm = Matrix2D.Identity;
                    tm = Matrix2D.Identity;
                    filtered.Add(op);
                    break;
                case "ET":
                    inText = false;
                    filtered.Add(op);
                    break;
                case "Tf":
                    if (op.Operands.Count >= 2 && TryReadNumber(op.Operands[1], out double tfSize))
                    {
                        fontSize = Math.Abs(tfSize);
                    }
                    filtered.Add(op);
                    break;
                case "TL":
                    if (op.Operands.Count >= 1 && TryReadNumber(op.Operands[0], out double tlVal))
                    {
                        leading = tlVal;
                    }
                    filtered.Add(op);
                    break;
                case "Tm":
                    if (TryReadMatrix(op.Operands, out var newTm))
                    {
                        tlm = newTm;
                        tm = newTm;
                    }
                    filtered.Add(op);
                    break;
                case "Td":
                case "TD":
                    if (op.Operands.Count >= 2 &&
                        TryReadNumber(op.Operands[0], out double tx) &&
                        TryReadNumber(op.Operands[1], out double ty))
                    {
                        if (name == "TD") leading = -ty;
                        var tr = new Matrix2D(1, 0, 0, 1, tx, ty);
                        tlm = Matrix2D.Multiply(tr, tlm);
                        tm = tlm;
                    }
                    filtered.Add(op);
                    break;
                case "T*":
                {
                    var tr = new Matrix2D(1, 0, 0, 1, 0, -leading);
                    tlm = Matrix2D.Multiply(tr, tlm);
                    tm = tlm;
                    filtered.Add(op);
                    break;
                }
                case "'":
                case "\"":
                {
                    var tr = new Matrix2D(1, 0, 0, 1, 0, -leading);
                    tlm = Matrix2D.Multiply(tr, tlm);
                    tm = tlm;
                    int strIdx = name == "\"" ? 2 : 0;
                    if (op.Operands.Count > strIdx && op.Operands[strIdx] is CString cStr)
                    {
                        var combined = inText ? Matrix2D.Multiply(tm, ctm) : ctm;
                        string cleaned = FilterStringForRedactions(cStr, combined, fontSize, pageHeightPt, normRedactions, out bool allRemoved, out _);
                        if (!allRemoved)
                        {
                            cStr.Value = cleaned;
                            filtered.Add(op);
                        }
                    }
                    else
                    {
                        filtered.Add(op);
                    }
                    break;
                }
                case "Tj":
                {
                    if (op.Operands.Count >= 1 && op.Operands[0] is CString cStr)
                    {
                        var combined = inText ? Matrix2D.Multiply(tm, ctm) : ctm;
                        string cleaned = FilterStringForRedactions(cStr, combined, fontSize, pageHeightPt, normRedactions, out bool allRemoved, out _);
                        if (!allRemoved)
                        {
                            cStr.Value = cleaned;
                            filtered.Add(op);
                        }
                    }
                    else
                    {
                        filtered.Add(op);
                    }
                    break;
                }
                case "TJ":
                {
                    if (op.Operands.Count >= 1 && op.Operands[0] is CArray arr)
                    {
                        var combined = inText ? Matrix2D.Multiply(tm, ctm) : ctm;
                        double xOffset = 0;
                        bool anyKept = false;
                        foreach (var elem in arr)
                        {
                            if (elem is CString cs)
                            {
                                var shifted = Matrix2D.Multiply(new Matrix2D(1, 0, 0, 1, xOffset, 0), combined);
                                string cleaned = FilterStringForRedactions(cs, shifted, fontSize, pageHeightPt, normRedactions, out bool allRemoved, out int origGlyphCount);
                                cs.Value = allRemoved ? string.Empty : cleaned;
                                if (!allRemoved) anyKept = true;
                                xOffset += origGlyphCount * (fontSize > 0 ? fontSize : 12.0) * 0.55;
                            }
                            else if (elem is CReal cr)
                            {
                                xOffset -= (cr.Value / 1000.0) * (fontSize > 0 ? fontSize : 12.0);
                            }
                            else if (elem is CInteger ci)
                            {
                                xOffset -= (ci.Value / 1000.0) * (fontSize > 0 ? fontSize : 12.0);
                            }
                        }
                        if (anyKept)
                        {
                            filtered.Add(op);
                        }
                    }
                    else
                    {
                        filtered.Add(op);
                    }
                    break;
                }
                default:
                    filtered.Add(op);
                    break;
            }
        }

        page.Contents.ReplaceContent(filtered);
    }

    private static string FilterStringForRedactions(
        CString cStr,
        Matrix2D combined,
        double fontSize,
        double pageHeightPt,
        IReadOnlyList<PdfRect> normRedactions,
        out bool allRemoved,
        out int origGlyphCount)
    {
        string text = cStr.Value ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            allRemoved = false;
            origGlyphCount = 0;
            return text;
        }

        bool isTwoByte = cStr.CStringType == CStringType.HexString && text.Length >= 2 && text.Length % 2 == 0;
        if (!isTwoByte && text.Length >= 2 && text.Length % 2 == 0)
        {
            int evenZeros = 0;
            int pairs = text.Length / 2;
            for (int p = 0; p < pairs; p++)
            {
                if (text[p * 2] == '\0') evenZeros++;
            }
            if (evenZeros * 2 >= pairs)
            {
                isTwoByte = true;
            }
        }

        int stride = isTwoByte ? 2 : 1;
        int glyphCount = text.Length / stride;
        origGlyphCount = glyphCount;

        double fs = fontSize > 0 ? fontSize : 12.0;
        double glyphAdvance = fs * 0.55;
        var kept = new List<char>(text.Length);
        int keptGlyphs = 0;

        for (int g = 0; g < glyphCount; g++)
        {
            double x0 = g * glyphAdvance;
            double x1 = x0 + glyphAdvance;
            var (px0, py0) = combined.Transform(x0, -fs * 0.2);
            var (px1, py1) = combined.Transform(x1, fs * 0.85);

            double left = Math.Min(px0, px1);
            double right = Math.Max(px0, px1);
            double top = Math.Min(pageHeightPt - py0, pageHeightPt - py1);
            double bottom = Math.Max(pageHeightPt - py0, pageHeightPt - py1);
            var charBox = new PdfRect(left, top, right, bottom);

            if (!IntersectsAny(charBox, normRedactions))
            {
                keptGlyphs++;
                for (int k = 0; k < stride; k++)
                {
                    kept.Add(text[g * stride + k]);
                }
            }
        }

        if (keptGlyphs == 0)
        {
            allRemoved = true;
            return string.Empty;
        }

        allRemoved = false;
        return keptGlyphs == glyphCount ? text : new string([.. kept]);
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
