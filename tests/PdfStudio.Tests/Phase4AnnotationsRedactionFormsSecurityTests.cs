using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Forms;
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
}

