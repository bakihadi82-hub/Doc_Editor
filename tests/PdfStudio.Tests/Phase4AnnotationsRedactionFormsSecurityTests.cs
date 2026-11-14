using System.IO;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;
using PdfSharpCore.Pdf.IO;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Forms;
using PdfStudio.Core.Rendering;
using PdfStudio.Editing.PdfSharp;
using PdfStudio.Rendering.Pdfium;
using SkiaSharp;
using Xunit;

namespace PdfStudio.Tests;

public class Phase4AnnotationsRedactionFormsSecurityTests
{
    private static byte[] CreateTransparentPngBytes()
    {
        using var bmp = new SKBitmap(120, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint
        {
            Color = SKColors.Navy,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true
        };
        canvas.DrawLine(10, 30, 110, 10, paint);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string CreateTextPdf(string path, params (string Text, double X, double Y)[] lines)
    {
        EncodingBootstrap.EnsureRegistered();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = 612;
        page.Height = 792;
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 14, XFontStyle.Regular);
            foreach (var (text, x, y) in lines)
            {
                gfx.DrawString(text, font, XBrushes.Black, new XPoint(x, y));
            }
        }
        doc.Save(path);
        return path;
    }

    private static string CreateFormPdf(string path)
    {
        EncodingBootstrap.EnsureRegistered();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = 612;
        page.Height = 792;
        double h = page.Height.Point;

        var acroForm = new PdfDictionary(doc);
        acroForm.Elements["/Fields"] = new PdfArray(doc);
        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
        doc.Internals.AddObject(acroForm);
        doc.Internals.Catalog.Elements["/AcroForm"] = acroForm.Reference!;

        var annots = new PdfArray(doc);
        page.Elements["/Annots"] = annots;

        var textWidget = new PdfDictionary(doc);
        textWidget.Elements["/Type"] = new PdfName("/Annot");
        textWidget.Elements["/Subtype"] = new PdfName("/Widget");
        textWidget.Elements["/FT"] = new PdfName("/Tx");
        textWidget.Elements["/T"] = new PdfString("FullName");
        textWidget.Elements["/V"] = new PdfString("");
        textWidget.Elements["/Rect"] = new PdfRectangle(new XPoint(72, h - 140), new XPoint(272, h - 115));
        doc.Internals.AddObject(textWidget);
        annots.Elements.Add(textWidget.Reference!);
        acroForm.Elements.GetArray("/Fields")!.Elements.Add(textWidget.Reference!);

        var checkWidget = new PdfDictionary(doc);
        checkWidget.Elements["/Type"] = new PdfName("/Annot");
        checkWidget.Elements["/Subtype"] = new PdfName("/Widget");
        checkWidget.Elements["/FT"] = new PdfName("/Btn");
        checkWidget.Elements["/T"] = new PdfString("Agreed");
        checkWidget.Elements["/V"] = new PdfName("/Off");
        checkWidget.Elements["/AS"] = new PdfName("/Off");
        checkWidget.Elements["/Rect"] = new PdfRectangle(new XPoint(72, h - 180), new XPoint(92, h - 160));
        doc.Internals.AddObject(checkWidget);
        annots.Elements.Add(checkWidget.Reference!);
        acroForm.Elements.GetArray("/Fields")!.Elements.Add(checkWidget.Reference!);

        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task ToolDock_Shortcuts_SwitchActiveTools_AndCreateAnnotations()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPhase4Tools_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = CreateTextPdf(Path.Combine(tempDir, "notes.pdf"), ("Sample Document", 72, 100));
            var savePath = Path.Combine(tempDir, "annotated.pdf");

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, new PdfSharpEditorService());
            await vm.LoadFileAsync(pdfPath);

            vm.SetHighlightToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Highlight, vm.ActiveTool);
            vm.SetUnderlineToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Underline, vm.ActiveTool);
            vm.SetDrawToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Draw, vm.ActiveTool);
            vm.SetTextToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Text, vm.ActiveTool);
            vm.SetNoteToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Note, vm.ActiveTool);
            vm.SetRedactToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Redact, vm.ActiveTool);
            vm.SetSelectToolCommand.Execute(null);
            Assert.Equal(PdfToolKind.Select, vm.ActiveTool);

            vm.AddAnnotation(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Highlight,
                BoundsPt = new PdfRect(70, 82, 220, 108)
            });
            vm.AddAnnotation(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.StickyNote,
                BoundsPt = new PdfRect(300, 100, 420, 156),
                TextContent = "Review this header"
            });

            Assert.Equal(2, vm.Annotations.Count);
            Assert.True(vm.CanUndo);
            Assert.True(vm.HasUnsavedChanges);

            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Single(vm.Annotations);
            await vm.RedoCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Annotations.Count);

            await vm.SaveToPathAsync(savePath);
            Assert.False(vm.HasUnsavedChanges);
            Assert.True(File.Exists(savePath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TrueRedaction_ApplyAndSave_PermanentlyStripsUnderlyingText()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPhase4Redact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = CreateTextPdf(
                Path.Combine(tempDir, "secrets.pdf"),
                ("PUBLIC-HEADER-100", 72, 100),
                ("CONFIDENTIAL-SECRET-9921", 72, 180));

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, new PdfSharpEditorService());
            await vm.LoadFileAsync(pdfPath);

            var secretMatches = await engine.FindTextAsync(0, "CONFIDENTIAL-SECRET-9921", matchCase: true, TestContext.Current.CancellationToken);
            Assert.NotEmpty(secretMatches);

            // Convert PDFium bottom-left char bounds to top-left page points
            double pageHeightPt = vm.Pages[0].HeightPoints;
            double minLeft = secretMatches[0].Bounds.Min(b => b.Left);
            double maxRight = secretMatches[0].Bounds.Max(b => b.Right);
            double minTop = secretMatches[0].Bounds.Min(b => pageHeightPt - b.Top);
            double maxBottom = secretMatches[0].Bounds.Max(b => pageHeightPt - b.Bottom);

            vm.AddAnnotation(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(minLeft - 4, minTop - 4, maxRight + 4, maxBottom + 4)
            });

            Assert.True(vm.HasPendingRedactions);

            await vm.ApplyRedactionsAndSaveCommand.ExecuteAsync(null);
            Assert.False(vm.HasPendingRedactions);
            Assert.False(vm.HasUnsavedChanges);

            // Reopen in a fresh engine and verify the redacted text is permanently gone
            await using var verifyEngine = new PdfiumRenderEngine();
            await verifyEngine.OpenAsync(pdfPath, ct: TestContext.Current.CancellationToken);
            string extracted = await verifyEngine.ExtractPageTextAsync(0, TestContext.Current.CancellationToken);
            var afterMatches = await verifyEngine.FindTextAsync(0, "CONFIDENTIAL", matchCase: false, TestContext.Current.CancellationToken);

            Assert.DoesNotContain("CONFIDENTIAL", extracted);
            Assert.DoesNotContain("9921", extracted);
            Assert.Empty(afterMatches);
            Assert.Contains("PUBLIC-HEADER-100", extracted);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FormsAndSignatures_FillFlatten_AndStampSignaturePng()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPhase4Forms_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var formPdf = CreateFormPdf(Path.Combine(tempDir, "form.pdf"));
            var flattenedPath = Path.Combine(tempDir, "form_flattened.pdf");

            await using var engine = new PdfiumRenderEngine();
            var editor = new PdfSharpEditorService();
            var vm = new MainWindowViewModel(engine, editor);

            await vm.LoadFileAsync(formPdf);
            Assert.Equal(2, vm.FormFields.Count);

            var nameField = Assert.Single(vm.FormFields, f => f.Name == "FullName");
            var checkField = Assert.Single(vm.FormFields, f => f.Name == "Agreed");

            vm.ToggleOrUpdateFormField(nameField, textValue: "Ada Lovelace");
            vm.ToggleOrUpdateFormField(checkField, isChecked: true);
            vm.ApplySignatureBytes(CreateTransparentPngBytes());
            vm.FlattenFormsOnSave = true;

            await vm.SaveToPathAsync(flattenedPath);

            // Flattened document should no longer expose editable AcroForm widgets
            var fieldsAfterFlatten = await editor.ReadFormFieldsAsync(flattenedPath, ct: TestContext.Current.CancellationToken);
            Assert.Empty(fieldsAfterFlatten);

            await using var verifyEngine = new PdfiumRenderEngine();
            await verifyEngine.OpenAsync(flattenedPath, ct: TestContext.Current.CancellationToken);
            string extracted = await verifyEngine.ExtractPageTextAsync(0, TestContext.Current.CancellationToken);
            Assert.Contains("Ada Lovelace", extracted);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Security_ProtectWithPassword_EncryptsPdfAndRequiresPasswordToOpen()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPhase4Security_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = CreateTextPdf(Path.Combine(tempDir, "plain.pdf"), ("Top Secret Balance Sheet", 72, 120));
            var protectedPath = Path.Combine(tempDir, "protected.pdf");

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, new PdfSharpEditorService());
            await vm.LoadFileAsync(pdfPath);

            vm.ProtectWithPassword = true;
            vm.DocumentPassword = "StudioSecret256!";
            await vm.SaveToPathAsync(protectedPath);

            Assert.True(File.Exists(protectedPath));

            await using var lockedEngine = new PdfiumRenderEngine();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lockedEngine.OpenAsync(protectedPath, password: null, TestContext.Current.CancellationToken));

            await using var unlockedEngine = new PdfiumRenderEngine();
            var pages = await unlockedEngine.OpenAsync(protectedPath, "StudioSecret256!", TestContext.Current.CancellationToken);
            Assert.Single(pages);

            string text = await unlockedEngine.ExtractPageTextAsync(0, TestContext.Current.CancellationToken);
            Assert.Contains("Top Secret Balance Sheet", text);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TrueRedaction_FormXObject_WithMatrixAndSharedReuse_StripsOnlyTargetInvocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioFormXObj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EncodingBootstrap.EnsureRegistered();
            string srcPath = Path.Combine(tempDir, "form_xobj.pdf");
            string dstPath = Path.Combine(tempDir, "form_xobj_redacted.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = 612;
                page.Height = 792;

                // Build a Form XObject whose content includes a keep line and a secret line,
                // then draw it twice at different positions and explicitly give /Fm0 a /Matrix transform.
                var xform = new XForm(doc, XUnit.FromPoint(300), XUnit.FromPoint(90));
                using (var fgfx = XGraphics.FromForm(xform))
                {
                    var font = new XFont("Arial", 14, XFontStyle.Regular);
                    fgfx.DrawString("FORM-KEEP-111", font, XBrushes.Black, new XPoint(10, 20));
                    fgfx.DrawString("FORM-SECRET-999", font, XBrushes.Black, new XPoint(10, 55));
                }

                using (var pgfx = XGraphics.FromPdfPage(page))
                {
                    pgfx.DrawImage(xform, 72, 100);
                    pgfx.DrawImage(xform, 72, 320);
                }

                // Shift /Fm0's internal /Matrix by +20pt horizontally to ensure Form /Matrix is honored.
                var xobjs = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
                if (xobjs is not null)
                {
                    foreach (string key in xobjs.Elements.Keys)
                    {
                        var d = xobjs.Elements.GetDictionary(key);
                        if (d is not null && string.Equals(d.Elements.GetName("/Subtype"), "/Form", StringComparison.Ordinal))
                        {
                            var mat = new PdfArray(doc);
                            mat.Elements.Add(new PdfReal(1));
                            mat.Elements.Add(new PdfReal(0));
                            mat.Elements.Add(new PdfReal(0));
                            mat.Elements.Add(new PdfReal(1));
                            mat.Elements.Add(new PdfReal(20));
                            mat.Elements.Add(new PdfReal(0));
                            d.Elements["/Matrix"] = mat;
                        }
                    }
                }

                doc.Save(srcPath);
            }

            await using var engine = new PdfiumRenderEngine();
            await engine.OpenAsync(srcPath, ct: ct);
            var matches = await engine.FindTextAsync(0, "FORM-SECRET-999", matchCase: true, ct);
            Assert.Equal(2, matches.Count);

            // Redact ONLY the upper invocation of FORM-SECRET-999
            double pageHeight = 792.0;
            var upperMatch = matches.OrderBy(m => pageHeight - m.Bounds.Max(b => b.Top)).First();
            double left = upperMatch.Bounds.Min(b => b.Left);
            double right = upperMatch.Bounds.Max(b => b.Right);
            double top = upperMatch.Bounds.Min(b => pageHeight - b.Top);
            double bottom = upperMatch.Bounds.Max(b => pageHeight - b.Bottom);

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(left - 2, top - 2, right + 2, bottom + 2)
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(
                srcPath,
                dstPath,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(BurnRedactions: true));

            await using var verifyEngine = new PdfiumRenderEngine();
            await verifyEngine.OpenAsync(dstPath, ct: ct);

            var remainingSecrets = await verifyEngine.FindTextAsync(0, "FORM-SECRET-999", matchCase: true, ct);
            var remainingKeeps = await verifyEngine.FindTextAsync(0, "FORM-KEEP-111", matchCase: true, ct);

            Assert.Single(remainingSecrets); // Lower invocation intact, upper invocation stripped!
            Assert.Equal(2, remainingKeeps.Count); // Keep text in both invocations intact!
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TrueRedaction_ImageDoAndVectorPaths_StripsOverlappingAndPreservesUnredacted()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioVecImg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EncodingBootstrap.EnsureRegistered();
            string srcPath = Path.Combine(tempDir, "vec_img.pdf");
            string dstPath = Path.Combine(tempDir, "vec_img_redacted.pdf");

            static byte[] MakeSolidPng(SKColor color)
            {
                using var bmp = new SKBitmap(20, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(color);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }

            byte[] redImgBytes = MakeSolidPng(SKColors.Red);
            byte[] blueImgBytes = MakeSolidPng(SKColors.Blue);

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = 612;
                page.Height = 792;
                using var gfx = XGraphics.FromPdfPage(page);

                // Outer green border rectangle (stroke only) surrounding interior redactions -> MUST be preserved
                gfx.DrawRectangle(new XPen(XColors.LimeGreen, 3), 20, 20, 572, 752);

                // 1) Overlapping raster image at (72, 100, 120, 60) -> MUST be stripped
                using (var xRedImg = XImage.FromStream(() => new MemoryStream(redImgBytes, writable: false)))
                {
                    gfx.DrawImage(xRedImg, 72, 100, 120, 60);
                }

                // 2) Safe raster image at (72, 360, 120, 60) -> MUST be preserved
                using (var xBlueImg = XImage.FromStream(() => new MemoryStream(blueImgBytes, writable: false)))
                {
                    gfx.DrawImage(xBlueImg, 72, 360, 120, 60);
                }

                // 3) Overlapping filled red rectangle at (72, 220, 140, 40) + overlapping stroked line -> MUST be stripped
                gfx.DrawRectangle(XBrushes.Red, 72, 220, 140, 40);
                gfx.DrawLine(new XPen(XColors.Purple, 2), 72, 220, 212, 260);

                // 4) Safe filled orange rectangle outside redaction at (350, 450, 100, 40) -> MUST be preserved
                gfx.DrawRectangle(XBrushes.DarkOrange, 350, 450, 100, 40);

                doc.Save(srcPath);
            }

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(68, 96, 196, 164)
            });
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(90, 225, 190, 255)
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(
                srcPath,
                dstPath,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(BurnRedactions: true));

            // Inspect content stream directly to verify red Image Do and overlapping vector path operators are gone,
            // while unredacted Image Do and outer border stroke remain.
            using (var checkDoc = PdfReader.Open(dstPath, PdfDocumentOpenMode.Import))
            {
                var checkPage = checkDoc.Pages[0];
                var seq = ContentReader.ReadContent(checkPage);
                var doNames = seq.OfType<COperator>()
                    .Where(o => o.OpCode.Name == "Do")
                    .Select(o => (o.Operands[0] as CName)?.Name)
                    .Where(n => n is not null)
                    .ToList();

                var xobjs = checkPage.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
                int remainingImageDos = doNames.Count(n =>
                    xobjs?.Elements.GetDictionary(n!)?.Elements.GetName("/Subtype") == "/Image");
                Assert.Equal(1, remainingImageDos);
            }

            // Visually render page via PDFium and verify non-redacted regions remain intact
            await using var renderEngine = new PdfiumRenderEngine();
            await renderEngine.OpenAsync(dstPath, ct: ct);
            using var bmp = await renderEngine.RenderPageAsync(new RenderRequest(0, 612, 792, 0, RenderPriority.VisibleViewport, ct));

            // Outer green border at left edge (x=20, y=300) should still be green!
            var borderPx = bmp.GetPixel(20, 300);
            Assert.True(borderPx.Green > borderPx.Red + 40 && borderPx.Green > borderPx.Blue + 40,
                $"Expected outer green border pixel at (20,300), got {borderPx}");

            // Unredacted blue image at (130, 390) should still be blue!
            var blueImgPx = bmp.GetPixel(130, 390);
            Assert.True(blueImgPx.Blue > blueImgPx.Red + 80,
                $"Expected preserved blue image pixel at (130,390), got {blueImgPx}");

            // Unredacted orange rect at (400, 470) should still be orange!
            var orangePx = bmp.GetPixel(400, 470);
            Assert.True(orangePx.Red > orangePx.Blue + 80 && orangePx.Green > 80,
                $"Expected preserved orange vector rect at (400,470), got {orangePx}");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TrueRedaction_FontWidthsAndPartialString_PreservesTrailingPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioWidths_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EncodingBootstrap.EnsureRegistered();
            string srcPath = Path.Combine(tempDir, "widths.pdf");
            string dstPath = Path.Combine(tempDir, "widths_redacted.pdf");

            // Narrow 'i' glyphs shift true X of "SECRET" well left of a naive 0.55*fontSize estimate,
            // and trailing "KEEP" must keep its original X coordinate after "SECRET" is stripped.
            CreateTextPdf(srcPath, ("iiiiiiiiiiSECRET KEEP", 72, 120));

            await using var srcEngine = new PdfiumRenderEngine();
            await srcEngine.OpenAsync(srcPath, ct: ct);
            var secretMatches = await srcEngine.FindTextAsync(0, "SECRET", matchCase: true, ct);
            var keepBeforeMatches = await srcEngine.FindTextAsync(0, "KEEP", matchCase: true, ct);
            Assert.Single(secretMatches);
            Assert.Single(keepBeforeMatches);

            double pageH = 792.0;
            double sLeft = secretMatches[0].Bounds.Min(b => b.Left);
            double sRight = secretMatches[0].Bounds.Max(b => b.Right);
            double sTop = secretMatches[0].Bounds.Min(b => pageH - b.Top);
            double sBot = secretMatches[0].Bounds.Max(b => pageH - b.Bottom);
            double keepBeforeLeft = keepBeforeMatches[0].Bounds.Min(b => b.Left);

            // Use a 0pt-margin redaction right on SECRET (Plan adds +/-2pt, well clear of KEEP after space).
            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(sLeft, sTop, sRight, sBot)
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(
                srcPath,
                dstPath,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(BurnRedactions: true));

            await using var dstEngine = new PdfiumRenderEngine();
            await dstEngine.OpenAsync(dstPath, ct: ct);
            string extracted = await dstEngine.ExtractPageTextAsync(0, ct);
            var keepAfterMatches = await dstEngine.FindTextAsync(0, "KEEP", matchCase: true, ct);

            Assert.DoesNotContain("SECRET", extracted);
            Assert.Contains("iiii", extracted);
            Assert.Single(keepAfterMatches);
            double keepAfterLeft = keepAfterMatches[0].Bounds.Min(b => b.Left);
            Assert.InRange(Math.Abs(keepAfterLeft - keepBeforeLeft), 0.0, 2.0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TrueRedaction_CorruptContentStream_PropagatesErrorAndNotifiesViewModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioCorrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EncodingBootstrap.EnsureRegistered();
            string corruptPath = Path.Combine(tempDir, "corrupt_stream.pdf");
            string targetPath = Path.Combine(tempDir, "should_not_exist.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = 612;
                page.Height = 792;

                var badContent = new PdfDictionary(doc);
                badContent.Elements["/Filter"] = new PdfName("/FlateDecode");
                badContent.CreateStream([0xFF, 0xFE, 0xFA, 0x00, 0x01, 0x02]);
                doc.Internals.AddObject(badContent);
                page.Contents.Elements.Add(badContent.Reference!);
                doc.Save(corruptPath);
            }

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(50, 50, 200, 100)
            });

            var editor = new PdfSharpEditorService();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                editor.ApplyPlanAndSaveAsync(
                    corruptPath,
                    targetPath,
                    plan,
                    ct: ct,
                    saveOptions: new PdfSaveOptions(BurnRedactions: true)));

            Assert.False(File.Exists(targetPath));

            // ViewModel command should surface the error dialog instead of silently succeeding
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, editor);
            await vm.LoadFileAsync(corruptPath);
            vm.AddAnnotation(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = new PdfRect(50, 50, 200, 100)
            });

            await vm.ApplyRedactionsAndSaveCommand.ExecuteAsync(null);
            Assert.True(vm.IsErrorModalOpen);
            Assert.Contains("parse PDF content stream", vm.ErrorModalMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task RotatedPage_TextEditsAnnotationsAndRedactions_AlignInDisplayCoordinates(int rotationDegrees)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioRot_{rotationDegrees}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EncodingBootstrap.EnsureRegistered();
            string srcPath = Path.Combine(tempDir, "rotated_src.pdf");
            string dstPath = Path.Combine(tempDir, "rotated_out.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = 612;
                page.Height = 792;
                using (var gfx = XGraphics.FromPdfPage(page))
                {
                    var font = new XFont("Arial", 14, XFontStyle.Regular);
                    gfx.DrawString("KEEP-TEXT-ONE", font, XBrushes.Black, new XPoint(100, 120));
                    gfx.DrawString("REDACT-SECRET", font, XBrushes.Black, new XPoint(100, 220));
                    gfx.DrawString("REPLACE-TARGET", font, XBrushes.Black, new XPoint(100, 320));
                }
                page.Rotate = rotationDegrees;
                doc.Save(srcPath);
            }

            await using var engine = new PdfiumRenderEngine();
            var pages = await engine.OpenAsync(srcPath, ct: ct);
            const double unrotW = 612.0;
            const double unrotH = 792.0;

            var redactMatches = await engine.FindTextAsync(0, "REDACT-SECRET", matchCase: true, ct);
            var targetMatches = await engine.FindTextAsync(0, "REPLACE-TARGET", matchCase: true, ct);
            Assert.Single(redactMatches);
            Assert.Single(targetMatches);

            // Convert PDFium's unrotated bottom-left char bounds into Display Top-Left coordinates for page rotation.
            static PdfRect ToDisplayTopLeft(PdfTextMatch match, double w0, double h0, int rot)
            {
                double minX = match.Bounds.Min(b => Math.Min(b.Left, b.Right));
                double maxX = match.Bounds.Max(b => Math.Max(b.Left, b.Right));
                double minY = match.Bounds.Min(b => Math.Min(b.Top, b.Bottom));
                double maxY = match.Bounds.Max(b => Math.Max(b.Top, b.Bottom));
                return rot switch
                {
                    90 => new PdfRect(minY, minX, maxY, maxX),
                    180 => new PdfRect(w0 - maxX, minY, w0 - minX, maxY),
                    270 => new PdfRect(h0 - maxY, w0 - maxX, h0 - minY, w0 - minX),
                    _ => new PdfRect(minX, h0 - maxY, maxX, h0 - minY)
                };
            }

            var redactRect = ToDisplayTopLeft(redactMatches[0], unrotW, unrotH, rotationDegrees);
            var targetRect = ToDisplayTopLeft(targetMatches[0], unrotW, unrotH, rotationDegrees);

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Redaction,
                BoundsPt = redactRect
            });
            plan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.TextBox,
                BoundsPt = new PdfRect(140, 80, 320, 110),
                TextContent = "ROTATED-ANNOT",
                FontSizePt = 12
            });
            plan.TextEdits.Add(new PdfTextEdit
            {
                PageIndex = 0,
                OriginalBoundsPt = targetRect,
                OriginalText = "REPLACE-TARGET",
                NewText = "UPDATED-LINE",
                FontFamily = "Arial",
                FontSizePt = 14
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(
                srcPath,
                dstPath,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(BurnRedactions: true));

            using (var savedDoc = PdfReader.Open(dstPath, PdfDocumentOpenMode.Import))
            {
                Assert.Equal(rotationDegrees, savedDoc.Pages[0].Rotate);
            }

            await using var verifyEngine = new PdfiumRenderEngine();
            var verifyPages = await verifyEngine.OpenAsync(dstPath, ct: ct);
            Assert.Equal(pages[0].WidthPoints, verifyPages[0].WidthPoints, 1);
            Assert.Equal(pages[0].HeightPoints, verifyPages[0].HeightPoints, 1);

            string extracted = await verifyEngine.ExtractPageTextAsync(0, ct);
            Assert.DoesNotContain("REDACT-SECRET", extracted);
            Assert.DoesNotContain("REPLACE-TARGET", extracted);
            Assert.Contains("KEEP-TEXT-ONE", extracted);
            Assert.Contains("UPDATED-LINE", extracted);
            Assert.Contains("ROTATED-ANNOT", extracted);

            // Check display coordinates of replaced text match targetRect closely
            var updatedMatch = Assert.Single(await verifyEngine.FindTextAsync(0, "UPDATED-LINE", matchCase: true, ct));
            var updatedDisplay = ToDisplayTopLeft(updatedMatch, unrotW, unrotH, rotationDegrees);
            Assert.InRange(Math.Abs(updatedDisplay.Left - targetRect.Left), 0.0, 6.0);
            Assert.InRange(Math.Abs(updatedDisplay.Top - targetRect.Top), 0.0, 8.0);

            var annotMatch = Assert.Single(await verifyEngine.FindTextAsync(0, "ROTATED-ANNOT", matchCase: true, ct));
            var annotDisplay = ToDisplayTopLeft(annotMatch, unrotW, unrotH, rotationDegrees);
            Assert.InRange(Math.Abs(annotDisplay.Left - 142.0), 0.0, 8.0);
            Assert.InRange(Math.Abs(annotDisplay.Top - 82.0), 0.0, 12.0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TextEdit_WordWrapsInsideOriginalBoundsWidth_AndFallsBackWhenFontUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioWrapFont_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string srcPath = CreateTextPdf(Path.Combine(tempDir, "wrap_src.pdf"), ("OldText", 72, 100));
            string dstPath = Path.Combine(tempDir, "wrap_dst.pdf");

            await using var engine = new PdfiumRenderEngine();
            await engine.OpenAsync(srcPath, ct: ct);
            var layout = await engine.GetPageTextLayoutAsync(0, 0, ct);
            var block = layout.Blocks.First(b => b.Text.Contains("OldText"));

            // Define a fixed 110pt-wide edit box; long multi-word replacement must wrap rather than overflow +200pt.
            var editBox = new PdfRect(block.BoundsPt.Left, block.BoundsPt.Top, block.BoundsPt.Left + 110.0, block.BoundsPt.Bottom);
            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.TextEdits.Add(new PdfTextEdit
            {
                PageIndex = 0,
                OriginalBoundsPt = editBox,
                OriginalText = block.Text,
                NewText = "Alpha Beta Gamma Delta Epsilon Zeta",
                FontFamily = "CompletelyMissingFontFamily_XYZ_999",
                FontSizePt = 12
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(srcPath, dstPath, plan, ct: ct);

            await using var verifyEngine = new PdfiumRenderEngine();
            await verifyEngine.OpenAsync(dstPath, ct: ct);
            var updatedLayout = await verifyEngine.GetPageTextLayoutAsync(0, 0, ct);
            string fullText = string.Concat(updatedLayout.Chars.Select(c => c.Character));

            Assert.Contains("Alpha", fullText);
            Assert.Contains("Zeta", fullText);

            var nonSpaceChars = updatedLayout.Chars.Where(c => !char.IsWhiteSpace(c.Character)).ToList();
            double maxCharRight = nonSpaceChars.Max(c => c.Right);
            double minBaselineBottom = nonSpaceChars.Min(c => c.Bottom);
            double maxBaselineBottom = nonSpaceChars.Max(c => c.Bottom);

            // No glyphs overshoot OriginalBoundsPt.Right significantly (old +200pt would hit ~310pt)
            Assert.True(maxCharRight <= editBox.Right + 10.0,
                $"Expected wrapped lines within {editBox.Right + 10:0.#}, actual maxRight={maxCharRight:0.#}");
            // Text actually wrapped onto multiple lines vertically
            Assert.True(maxBaselineBottom - minBaselineBottom > 12.0,
                $"Expected multi-line vertical span, got {maxBaselineBottom - minBaselineBottom:0.#}");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Security_OwnerPasswordPermissionsAndSecurityLevel_AreHonored()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPerms_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string srcPath = CreateTextPdf(Path.Combine(tempDir, "perms_src.pdf"), ("Permission Protected Doc", 72, 100));
            string restricted128Path = Path.Combine(tempDir, "restricted_128.pdf");
            string restricted40Path = Path.Combine(tempDir, "restricted_40.pdf");

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(
                srcPath,
                restricted128Path,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(
                    UserPassword: null,
                    OwnerPassword: "OwnerOnlyPass!",
                    UseAes256: true,
                    PermitPrint: false,
                    PermitModifyDocument: false,
                    PermitExtractContent: false,
                    PermitAnnotations: false,
                    PermitFormsFill: false,
                    PermitFullQualityPrint: false,
                    PermitAssembleDocument: false,
                    PermitAccessibilityExtractContent: false));

            // Viewable without user password, but restrictions enforced in SecuritySettings
            await using var viewEngine = new PdfiumRenderEngine();
            var pages = await viewEngine.OpenAsync(restricted128Path, password: null, ct: ct);
            Assert.Single(pages);

            using (var reader = PdfReader.Open(restricted128Path, PdfDocumentOpenMode.ReadOnly))
            {
                Assert.False(reader.SecuritySettings.PermitPrint);
                Assert.False(reader.SecuritySettings.PermitFullQualityPrint);
                Assert.False(reader.SecuritySettings.PermitModifyDocument);
                Assert.False(reader.SecuritySettings.PermitExtractContent);
                Assert.False(reader.SecuritySettings.PermitAnnotations);
                Assert.False(reader.SecuritySettings.PermitFormsFill);
                Assert.False(reader.SecuritySettings.PermitAssembleDocument);
                Assert.False(reader.SecuritySettings.PermitAccessibilityExtractContent);
                Assert.Equal(2, reader.SecurityHandler.Elements.GetInteger("/V"));
                Assert.Equal(3, reader.SecurityHandler.Elements.GetInteger("/R"));
            }

            // Modify mode without owner password is rejected, while owner password succeeds
            Assert.Throws<PdfReaderException>(() => PdfReader.Open(restricted128Path, PdfDocumentOpenMode.Modify));
            using (var ownerOpened = PdfReader.Open(restricted128Path, "OwnerOnlyPass!", PdfDocumentOpenMode.Modify))
            {
                Assert.Equal(1, ownerOpened.PageCount);
            }

            // UseAes256: false selects 40-bit encryption rather than hardcoding 128-bit
            await editor.ApplyPlanAndSaveAsync(
                srcPath,
                restricted40Path,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(
                    UserPassword: null,
                    OwnerPassword: "OwnerOnlyPass!",
                    UseAes256: false,
                    PermitPrint: false,
                    PermitModifyDocument: true,
                    PermitExtractContent: false));

            using (var reader40 = PdfReader.Open(restricted40Path, PdfDocumentOpenMode.ReadOnly))
            {
                Assert.False(reader40.SecuritySettings.PermitPrint);
                Assert.True(reader40.SecuritySettings.PermitModifyDocument);
                Assert.False(reader40.SecuritySettings.PermitExtractContent);
                Assert.Equal(1, reader40.SecurityHandler.Elements.GetInteger("/V"));
                Assert.Equal(2, reader40.SecurityHandler.Elements.GetInteger("/R"));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MergeExtractSplit_WithPassword_OpensEncryptedSources()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPwdOps_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            const string pwd = "MergePassword#99";
            string plain1 = Path.Combine(tempDir, "plain1.pdf");
            string plain2 = Path.Combine(tempDir, "plain2.pdf");
            string enc1 = Path.Combine(tempDir, "enc1.pdf");
            string enc2 = Path.Combine(tempDir, "enc2.pdf");

            EncodingBootstrap.EnsureRegistered();
            static void MakeTwoPagePdf(string path, string p1Text, string p2Text)
            {
                using var d = new PdfDocument();
                var p1 = d.AddPage();
                using (var g1 = XGraphics.FromPdfPage(p1))
                    g1.DrawString(p1Text, new XFont("Arial", 14), XBrushes.Black, 72, 100);
                var p2 = d.AddPage();
                using (var g2 = XGraphics.FromPdfPage(p2))
                    g2.DrawString(p2Text, new XFont("Arial", 14), XBrushes.Black, 72, 100);
                d.Save(path);
            }

            MakeTwoPagePdf(plain1, "Doc1-Page1", "Doc1-Page2");
            MakeTwoPagePdf(plain2, "Doc2-Page1", "Doc2-Page2");

            var editor = new PdfSharpEditorService();
            var twoPagePlan = new StructuralEditPlan();
            twoPagePlan.ResetFromPageCount(2);

            await editor.ApplyPlanAndSaveAsync(plain1, enc1, twoPagePlan, ct: ct, saveOptions: new PdfSaveOptions(UserPassword: pwd, OwnerPassword: pwd));
            await editor.ApplyPlanAndSaveAsync(plain2, enc2, twoPagePlan, ct: ct, saveOptions: new PdfSaveOptions(UserPassword: pwd, OwnerPassword: pwd));

            string extractedPath = Path.Combine(tempDir, "extracted.pdf");
            await Assert.ThrowsAsync<PdfReaderException>(() =>
                editor.ExtractPagesAsync(enc1, [1], extractedPath, password: null, ct: ct));

            await editor.ExtractPagesAsync(enc1, [1], extractedPath, password: pwd, ct: ct);

            string mergedPath = Path.Combine(tempDir, "merged.pdf");
            await editor.MergeAsync([enc1, enc2], mergedPath, password: pwd, ct: ct);

            string splitDir = Path.Combine(tempDir, "splits");
            await editor.SplitEveryNPagesAsync(enc1, 1, splitDir, "part", password: pwd, ct: ct);

            await using var verifyEngine = new PdfiumRenderEngine();
            var extractedPages = await verifyEngine.OpenAsync(extractedPath, ct: ct);
            Assert.Single(extractedPages);
            Assert.Contains("Doc1-Page2", await verifyEngine.ExtractPageTextAsync(0, ct));

            await using var mergeEngine = new PdfiumRenderEngine();
            var mergedPages = await mergeEngine.OpenAsync(mergedPath, ct: ct);
            Assert.Equal(4, mergedPages.Count);
            Assert.Contains("Doc1-Page1", await mergeEngine.ExtractPageTextAsync(0, ct));
            Assert.Contains("Doc2-Page2", await mergeEngine.ExtractPageTextAsync(3, ct));

            string[] splitFiles = Directory.GetFiles(splitDir, "*.pdf").OrderBy(f => f).ToArray();
            Assert.Equal(2, splitFiles.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task CropBoxAndNonZeroMediaBox_WithRotation_AlignsRedactionsTextEditsAndFormFields(int rotation)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioCrop_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "cropped.pdf");
            string outputPath = Path.Combine(tempDir, "redacted.pdf");
            EncodingBootstrap.EnsureRegistered();

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new XPoint(100, 200), new XPoint(712, 992));
                page.CropBox = new PdfRectangle(new XPoint(150, 300), new XPoint(550, 800));
                page.Rotate = rotation;

                var fontDict = new PdfDictionary(doc);
                fontDict.Elements["/Type"] = new PdfName("/Font");
                fontDict.Elements["/Subtype"] = new PdfName("/Type1");
                fontDict.Elements["/BaseFont"] = new PdfName("/Helvetica");
                doc.Internals.AddObject(fontDict);

                var fonts = new PdfDictionary(doc);
                fonts.Elements["/F1"] = fontDict.Reference!;
                var res = new PdfDictionary(doc);
                res.Elements["/Font"] = fonts;
                page.Elements["/Resources"] = res;

                var cdict = new PdfDictionary(doc);
                cdict.CreateStream(Encoding.ASCII.GetBytes("BT /F1 18 Tf 220 620 Td (SECRET-CROP) Tj ET")) ;
                doc.Internals.AddObject(cdict);
                page.Contents.Elements.Add(cdict.Reference!);

                var widget = new PdfDictionary(doc);
                widget.Elements["/Type"] = new PdfName("/Annot");
                widget.Elements["/Subtype"] = new PdfName("/Widget");
                widget.Elements["/FT"] = new PdfName("/Tx");
                widget.Elements["/T"] = new PdfString("ExistingBox");
                widget.Elements["/Rect"] = new PdfRectangle(new XPoint(220, 500), new XPoint(340, 525));
                widget.Elements["/V"] = new PdfString("Initial");
                doc.Internals.AddObject(widget);
                page.Elements["/Annots"] = new PdfArray(doc, widget.Reference!);

                doc.Save(inputPath);
            }

            var editor = new PdfSharpEditorService();
            var readFields = await editor.ReadFormFieldsAsync(inputPath, ct: ct);
            var existingField = Assert.Single(readFields);

            PdfRect secretBox;
            double pageW, pageH;
            await using (var preEngine = new PdfiumRenderEngine())
            {
                var pages = await preEngine.OpenAsync(inputPath, ct: ct);
                pageW = pages[0].WidthPoints;
                pageH = pages[0].HeightPoints;
                Assert.Equal(rotation == 90 ? 500 : 400, pageW, 1);
                Assert.Equal(rotation == 90 ? 400 : 500, pageH, 1);
            }

            // "SECRET-CROP" is drawn at PDF baseline (220, 620), 18pt Helvetica (width ~116pt, ascent ~13pt, descent ~4pt)
            // inside CropBox [150, 300, 550, 800]:
            //   rot=0  -> display (220-150 .. 336-150, 800-634 .. 800-615) = (70..186, 166..185)
            //   rot=90 -> display (615-300 .. 634-300, 220-150 .. 336-150) = (315..334, 70..186)
            secretBox = rotation == 90
                ? new PdfRect(312, 68, 338, 195)
                : new PdfRect(68, 162, 195, 188);

            // Also verify ReadFormFieldsAsync returned display coordinates inside visible CropBox dimensions
            Assert.InRange(existingField.BoundsPt.Left, 0, pageW);
            Assert.InRange(existingField.BoundsPt.Top, 0, pageH);
            Assert.True(existingField.BoundsPt.Right > existingField.BoundsPt.Left);
            Assert.True(existingField.BoundsPt.Bottom > existingField.BoundsPt.Top);

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.Annotations.Add(new PdfAnnotation
            {
                Kind = AnnotationKind.Redaction,
                PageIndex = 0,
                BoundsPt = secretBox
            });
            plan.TextEdits.Add(new PdfTextEdit
            {
                PageIndex = 0,
                OriginalBoundsPt = new PdfRect(60, 80, 260, 110),
                NewText = "CROP-OK",
                FontSizePt = 14,
                FontFamily = "Arial"
            });
            plan.FormFields.Add(new PdfFormField
            {
                Name = "NewCroppedField",
                Kind = PdfFormFieldKind.Text,
                PageIndex = 0,
                BoundsPt = new PdfRect(60, 140, 180, 165),
                Value = "Added"
            });

            await editor.ApplyPlanAndSaveAsync(inputPath, outputPath, plan, ct: ct, saveOptions: new PdfSaveOptions(BurnRedactions: true));

            var fieldsAfter = await editor.ReadFormFieldsAsync(outputPath, ct: ct);
            var addedField = Assert.Single(fieldsAfter, f => f.Name == "NewCroppedField");
            Assert.InRange(addedField.BoundsPt.Left, 58, 62);
            Assert.InRange(addedField.BoundsPt.Top, 138, 142);

            await using var postEngine = new PdfiumRenderEngine();
            var outPages = await postEngine.OpenAsync(outputPath, ct: ct);
            string extracted = await postEngine.ExtractPageTextAsync(0, ct);
            Assert.DoesNotContain("SECRET", extracted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CROP-OK", extracted, StringComparison.OrdinalIgnoreCase);

            using var bmp = await postEngine.RenderPageAsync(new RenderRequest(0, (int)pageW, (int)pageH, 0, RenderPriority.VisibleViewport, ct));
            int cx = Math.Clamp((int)((secretBox.Left + secretBox.Right) * 0.5), 1, bmp.Width - 2);
            int cy = Math.Clamp((int)((secretBox.Top + secretBox.Bottom) * 0.5), 1, bmp.Height - 2);
            var px = bmp.GetPixel(cx, cy);
            Assert.True(px.Red < 30 && px.Green < 30 && px.Blue < 30, $"Expected black redaction pixel at ({cx},{cy}), got {px}");

            // Confirm rendered pixels inside replacement text box (60..140, 80..102) actually contain dark glyph strokes
            bool hasDarkTextPixel = false;
            for (int y = 80; y <= 105 && !hasDarkTextPixel; y++)
            {
                for (int x = 60; x <= 140; x++)
                {
                    var tp = bmp.GetPixel(x, y);
                    if (tp.Red < 120 && tp.Green < 120 && tp.Blue < 120)
                    {
                        hasDarkTextPixel = true;
                        break;
                    }
                }
            }
            Assert.True(hasDarkTextPixel, "Expected dark glyphs rendered inside cropped/rotated CROP-OK text edit bounds.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SimpleFont_OneByteHexString_And_Standard14Widths_PreservesAdjacentGlyphs()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioHexStd14_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "hex.pdf");
            string outputPath = Path.Combine(tempDir, "hex_out.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(612);
                page.Height = XUnit.FromPoint(792);

                var fontDict = new PdfDictionary(doc);
                fontDict.Elements["/Type"] = new PdfName("/Font");
                fontDict.Elements["/Subtype"] = new PdfName("/Type1");
                fontDict.Elements["/BaseFont"] = new PdfName("/Helvetica");
                doc.Internals.AddObject(fontDict);

                var fonts = new PdfDictionary(doc);
                fonts.Elements["/F1"] = fontDict.Reference!;
                var res = new PdfDictionary(doc);
                res.Elements["/Font"] = fonts;
                page.Elements["/Resources"] = res;

                var cdict = new PdfDictionary(doc);
                cdict.CreateStream(Encoding.ASCII.GetBytes("BT /F1 18 Tf 72 700 Td <41424344> Tj 0 -40 Td (iiii SECRET WWWW) Tj ET"));
                doc.Internals.AddObject(cdict);
                page.Contents.Elements.Add(cdict.Reference!);
                doc.Save(inputPath);
            }

            static PdfRect ToTopLeft(IReadOnlyList<PdfRect> rawBounds, double pageH = 792.0)
            {
                double minX = rawBounds.Min(b => Math.Min(b.Left, b.Right));
                double maxX = rawBounds.Max(b => Math.Max(b.Left, b.Right));
                double minY = rawBounds.Min(b => Math.Min(b.Top, b.Bottom));
                double maxY = rawBounds.Max(b => Math.Max(b.Top, b.Bottom));
                return new PdfRect(minX, pageH - maxY, maxX, pageH - minY);
            }

            PdfRect bBox, secretBox;
            double originalWLeft;
            await using (var preEngine = new PdfiumRenderEngine())
            {
                await preEngine.OpenAsync(inputPath, ct: ct);
                var bMatch = Assert.Single(await preEngine.FindTextAsync(0, "ABCD", matchCase: true, ct: ct));
                Assert.Equal(4, bMatch.Bounds.Count);
                var bRaw = bMatch.Bounds[1];
                var bDisp = ToTopLeft([bRaw]);
                bBox = new PdfRect(bDisp.Left + 0.5, bDisp.Top + 1.0, bDisp.Right - 0.5, bDisp.Bottom - 1.0);

                var secMatch = Assert.Single(await preEngine.FindTextAsync(0, "SECRET", matchCase: true, ct: ct));
                var secDisp = ToTopLeft(secMatch.Bounds);
                secretBox = new PdfRect(secDisp.Left + 0.5, secDisp.Top + 1.0, secDisp.Right - 0.5, secDisp.Bottom - 1.0);

                var wMatch = Assert.Single(await preEngine.FindTextAsync(0, "WWWW", matchCase: true, ct: ct));
                originalWLeft = wMatch.Bounds.Min(c => c.Left);
            }

            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.Annotations.Add(new PdfAnnotation { Kind = AnnotationKind.Redaction, PageIndex = 0, BoundsPt = bBox });
            plan.Annotations.Add(new PdfAnnotation { Kind = AnnotationKind.Redaction, PageIndex = 0, BoundsPt = secretBox });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(inputPath, outputPath, plan, ct: ct, saveOptions: new PdfSaveOptions(BurnRedactions: true));

            await using var postEngine = new PdfiumRenderEngine();
            await postEngine.OpenAsync(outputPath, ct: ct);
            string extracted = await postEngine.ExtractPageTextAsync(0, ct);
            Assert.Contains("ACD", extracted.Replace(" ", ""));
            Assert.DoesNotContain("B", extracted);
            Assert.DoesNotContain("SECRET", extracted);
            Assert.Contains("iiii", extracted);
            Assert.Contains("WWWW", extracted);

            var newWMatch = Assert.Single(await postEngine.FindTextAsync(0, "WWWW", matchCase: true, ct: ct));
            double newWLeft = newWMatch.Bounds.Min(c => c.Left);
            Assert.InRange(newWLeft, originalWLeft - 2.0, originalWLeft + 2.0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task HollowEvenOddVectorFrame_And_CombinedFillStroke_PreservesBorderWhileStrippingInterior()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioVec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "hollow.pdf");
            string outputPath = Path.Combine(tempDir, "hollow_redacted.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(612);
                page.Height = XUnit.FromPoint(792);

                var cdict = new PdfDictionary(doc);
                cdict.CreateStream(Encoding.ASCII.GetBytes(
                    "q 0 1 0 rg 0 0 612 792 re 20 20 572 752 re f* Q\n" +
                    "q 1 0 0 rg 0 0 1 RG 4 w 200 400 80 40 re B Q\n"));
                doc.Internals.AddObject(cdict);
                page.Contents.Elements.Add(cdict.Reference!);
                doc.Save(inputPath);
            }

            // Redact interior red rectangle (PDF y=400..440 -> display top=792-440=352, bottom=792-400=392)
            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.Annotations.Add(new PdfAnnotation
            {
                Kind = AnnotationKind.Redaction,
                PageIndex = 0,
                BoundsPt = new PdfRect(195, 347, 285, 397)
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(inputPath, outputPath, plan, ct: ct, saveOptions: new PdfSaveOptions(BurnRedactions: true));

            await using var engine = new PdfiumRenderEngine();
            await engine.OpenAsync(outputPath, ct: ct);
            using var bmp = await engine.RenderPageAsync(new RenderRequest(0, 612, 792, 0, RenderPriority.VisibleViewport, ct));

            var borderPx = bmp.GetPixel(300, 10);
            Assert.True(borderPx.Green > 200 && borderPx.Red < 30 && borderPx.Blue < 30,
                $"Expected hollow green frame border at (300,10) to survive, got {borderPx}");

            var redactedPx = bmp.GetPixel(240, 372);
            Assert.True(redactedPx.Red < 20 && redactedPx.Green < 20 && redactedPx.Blue < 20,
                $"Expected redacted interior pixel to be black, got {redactedPx}");

            using var outDoc = PdfReader.Open(outputPath, PdfDocumentOpenMode.ReadOnly);
            var seq = ContentReader.ReadContent(outDoc.Pages[0]);
            Assert.DoesNotContain(seq.OfType<COperator>(), op => op.OpCode.Name == "B");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InlineImages_StripsRedactedInlineImage_WhilePreservingUnredactedBinaryPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioInline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "inline.pdf");
            string outputPath = Path.Combine(tempDir, "inline_redacted.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(400);
                page.Height = XUnit.FromPoint(400);

                // Two 1x1 RGB inline images (red [255,0,0] and blue [0,0,255])
                byte[] streamBytes =
                [
                    .. Encoding.ASCII.GetBytes("q 40 0 0 40 40 260 cm BI /W 1 /H 1 /BPC 8 /CS /RGB ID "),
                    0xFF, 0x00, 0x00,
                    .. Encoding.ASCII.GetBytes(" EI Q\nq 40 0 0 40 200 260 cm BI /W 1 /H 1 /BPC 8 /CS /RGB ID "),
                    0x00, 0x00, 0xFF,
                    .. Encoding.ASCII.GetBytes(" EI Q\n")
                ];
                var cdict = new PdfDictionary(doc);
                cdict.CreateStream(streamBytes);
                doc.Internals.AddObject(cdict);
                page.Contents.Elements.Add(cdict.Reference!);
                doc.Save(inputPath);
            }

            // Redact Image 1 only: display (40..80, 100..140)
            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.Annotations.Add(new PdfAnnotation
            {
                Kind = AnnotationKind.Redaction,
                PageIndex = 0,
                BoundsPt = new PdfRect(35, 95, 85, 145)
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(inputPath, outputPath, plan, ct: ct, saveOptions: new PdfSaveOptions(BurnRedactions: true));

            using (var outDoc = PdfReader.Open(outputPath, PdfDocumentOpenMode.ReadOnly))
            {
                var seq = ContentReader.ReadContent(outDoc.Pages[0]);
                Assert.Single(seq.OfType<COperator>(), op => op.OpCode.Name == "BI");
            }

            await using var engine = new PdfiumRenderEngine();
            await engine.OpenAsync(outputPath, ct: ct);
            using var bmp = await engine.RenderPageAsync(new RenderRequest(0, 400, 400, 0, RenderPriority.VisibleViewport, ct));

            var redactedPx = bmp.GetPixel(60, 120);
            Assert.True(redactedPx.Red < 20 && redactedPx.Green < 20 && redactedPx.Blue < 20,
                $"Expected redacted inline image 1 to be black, got {redactedPx}");

            var preservedPx = bmp.GetPixel(220, 120);
            Assert.True(preservedPx.Blue > 200 && preservedPx.Red < 20 && preservedPx.Green < 20,
                $"Expected unredacted blue inline image 2 at (220,120) to survive, got {preservedPx}");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task NestedFormXObject_InheritsGState_WithPlanRotation_RedactsEncryptedPdf()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioNestedForm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "nested.pdf");
            string outputPath = Path.Combine(tempDir, "nested_out.pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(612);
                page.Height = XUnit.FromPoint(792);

                var fontDict = new PdfDictionary(doc);
                fontDict.Elements["/Type"] = new PdfName("/Font");
                fontDict.Elements["/Subtype"] = new PdfName("/Type1");
                fontDict.Elements["/BaseFont"] = new PdfName("/Helvetica");
                doc.Internals.AddObject(fontDict);

                // Inner Form relies on inherited /F1 18 Tf from Outer Form / page
                var innerForm = new PdfDictionary(doc);
                innerForm.Elements["/Type"] = new PdfName("/XObject");
                innerForm.Elements["/Subtype"] = new PdfName("/Form");
                innerForm.Elements["/BBox"] = new PdfRectangle(new XPoint(0, 0), new XPoint(300, 60));
                innerForm.CreateStream(Encoding.ASCII.GetBytes("BT 0 10 Td (NESTED-SECRET) Tj ET"));
                doc.Internals.AddObject(innerForm);

                var outerRes = new PdfDictionary(doc);
                var outerXObjs = new PdfDictionary(doc);
                outerXObjs.Elements["/Inner"] = innerForm.Reference!;
                outerRes.Elements["/XObject"] = outerXObjs;

                var outerForm = new PdfDictionary(doc);
                outerForm.Elements["/Type"] = new PdfName("/XObject");
                outerForm.Elements["/Subtype"] = new PdfName("/Form");
                outerForm.Elements["/BBox"] = new PdfRectangle(new XPoint(0, 0), new XPoint(400, 200));
                outerForm.Elements["/Resources"] = outerRes;
                outerForm.CreateStream(Encoding.ASCII.GetBytes("q 1 0 0 1 20 30 cm /Inner Do Q"));
                doc.Internals.AddObject(outerForm);

                var pageFonts = new PdfDictionary(doc);
                pageFonts.Elements["/F1"] = fontDict.Reference!;
                var pageXObjs = new PdfDictionary(doc);
                pageXObjs.Elements["/Outer"] = outerForm.Reference!;
                var pageRes = new PdfDictionary(doc);
                pageRes.Elements["/Font"] = pageFonts;
                pageRes.Elements["/XObject"] = pageXObjs;
                page.Elements["/Resources"] = pageRes;

                var cdict = new PdfDictionary(doc);
                cdict.CreateStream(Encoding.ASCII.GetBytes(
                    "BT /F1 16 Tf 72 100 Td (KEEP-ME) Tj ET\n" +
                    "q /F1 18 Tf 1 0 0 1 80 500 cm /Outer Do Q\n"));
                doc.Internals.AddObject(cdict);
                page.Contents.Elements.Add(cdict.Reference!);
                doc.Save(inputPath);
            }

            // User PDFium location before rotation:
            // Inner origin = (80+20, 500+30+10) = (100, 540) in PDF space, width ~140, height ~18.
            // After RotatePage(0, 1) -> Rotate = 90 (display size 792 x 612):
            // ToDisplayPoint(px, py, 0..612 x 0..792, 90) = (py, px) => x in [535..575], y in [95..250]!
            var plan = new StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.RotateAt(0, 1);
            plan.Annotations.Add(new PdfAnnotation
            {
                Kind = AnnotationKind.Redaction,
                PageIndex = 0,
                BoundsPt = new PdfRect(530, 95, 580, 260)
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(
                inputPath,
                outputPath,
                plan,
                ct: ct,
                saveOptions: new PdfSaveOptions(
                    BurnRedactions: true,
                    UserPassword: "u1",
                    OwnerPassword: "o1",
                    UseAes256: true,
                    PermitExtractContent: false));

            await using var engine = new PdfiumRenderEngine();
            var pages = await engine.OpenAsync(outputPath, password: "o1", ct: ct);
            Assert.Equal(792, pages[0].WidthPoints, 1);
            Assert.Equal(612, pages[0].HeightPoints, 1);
            string extracted = await engine.ExtractPageTextAsync(0, ct);
            Assert.DoesNotContain("NESTED", extracted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SECRET", extracted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("KEEP-ME", extracted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}

