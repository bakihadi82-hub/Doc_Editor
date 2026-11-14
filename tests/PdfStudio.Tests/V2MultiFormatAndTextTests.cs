using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfStudio.App.Controls;
using PdfStudio.App.Views;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Formats;
using PdfStudio.Editing.PdfSharp;
using PdfStudio.Rendering.Pdfium;
using SkiaSharp;
using Xunit;

namespace PdfStudio.Tests;

public sealed class V2MultiFormatAndTextTests
{
    private static string CreateTextPdf(string path, params string[] linesPerPage)
    {
        EncodingBootstrap.EnsureRegistered();
        using var doc = new PdfDocument();
        foreach (string line in linesPerPage)
        {
            var page = doc.AddPage();
            page.Width = 612;
            page.Height = 792;
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 14, XFontStyle.Regular);
            gfx.DrawString(line, font, XBrushes.Black, new XPoint(72, 120));
        }

        doc.Save(path);
        return path;
    }

    private static string CreateSampleXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();

        var wsPart1 = wbPart.AddNewPart<WorksheetPart>();
        wsPart1.Worksheet = new Worksheet(new SheetData(
            new Row(
                new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Product")) },
                new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Revenue")) }),
            new Row(
                new Cell { CellReference = "A2", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Alpha")) },
                new Cell { CellReference = "B2", CellValue = new CellValue("120"), DataType = CellValues.Number }),
            new Row(
                new Cell { CellReference = "A3", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Beta")) },
                new Cell { CellReference = "B3", CellValue = new CellValue("240"), DataType = CellValues.Number })));

        var wsPart2 = wbPart.AddNewPart<WorksheetPart>();
        wsPart2.Worksheet = new Worksheet(new SheetData(
            new Row(
                new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Notes")) })));

        var sheets = wbPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart1), SheetId = 1, Name = "Sales" });
        sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart2), SheetId = 2, Name = "Summary" });
        wbPart.Workbook.Save();
        return path;
    }

    private static string CreateSampleDocx(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(
            new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Project Overview"))),
                new Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Initial body paragraph text.")))));
        mainPart.Document.Save();
        return path;
    }

    private static string CreateSamplePng(string path, int width = 80, int height = 40)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(0x1E, 0x40, 0xAF));
            using var paint = new SKPaint { Color = new SKColor(0xF5, 0x9E, 0x0B), Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(10, 10, width - 10, height - 10), paint);
        }

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    [Fact]
    public async Task PdfiumTextInspection_ExtractsCharsLinesBlocks_AndHitTestsAccurately()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioV2Text_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = CreateTextPdf(Path.Combine(tempDir, "TextSample.pdf"), "Quarterly Budget Report", "Confidential Summary Page");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            await engine.OpenAsync(pdfPath, ct: ct);

            var layout = await engine.GetPageTextLayoutAsync(0, 0, ct);
            Assert.Equal(612, layout.PageWidthPt, 1);
            Assert.Equal(792, layout.PageHeightPt, 1);
            Assert.NotEmpty(layout.Chars);
            Assert.NotEmpty(layout.Lines);
            Assert.NotEmpty(layout.Blocks);

            var block = layout.Blocks[0];
            Assert.Contains("Quarterly Budget Report", block.Text);

            var firstNonSpace = layout.Chars.First(c => !c.IsWhitespace);
            double hitX = (firstNonSpace.Left + firstNonSpace.Right) * 0.5;
            double hitY = (firstNonSpace.Top + firstNonSpace.Bottom) * 0.5;

            int charIdx = layout.HitTestChar(hitX, hitY, 4.0);
            Assert.True(charIdx >= 0);
            Assert.Equal(firstNonSpace.Character, layout.Chars[charIdx].Character);

            int pdfiumIdx = await engine.GetCharIndexAtPosAsync(0, hitX, hitY, 6.0, 6.0, ct);
            Assert.True(pdfiumIdx >= 0);

            var (wordStart, wordEnd) = layout.GetWordRangeAt(charIdx);
            Assert.Equal("Quarterly", layout.ExtractRangeText(wordStart, wordEnd));

            var (lineStart, lineEnd) = layout.GetLineRangeAt(charIdx);
            Assert.Equal("Quarterly Budget Report", layout.ExtractRangeText(lineStart, lineEnd).Trim());

            var hitBlock = layout.HitTestBlock(hitX, hitY, 4.0);
            Assert.NotNull(hitBlock);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PdfInPlaceTextEdit_AndSelectionMarkup_CommitCleanlyOnAtomicSave()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioV2Edit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string srcPdf = CreateTextPdf(Path.Combine(tempDir, "Editable.pdf"), "Original Headline Text");
        string outPdf = Path.Combine(tempDir, "Edited.pdf");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);
            await vm.LoadFileAsync(srcPdf);
            Assert.NotNull(vm.ActivePdfTab);

            var layout = await vm.GetOrLoadPageTextLayoutAsync(0);
            Assert.NotNull(layout);
            var block = Assert.Single(layout!.Blocks);

            // 1. Add inline Highlight + Strikeout + Underline selection annotations
            var rects = layout.GetMergedLineBoxes(block.StartCharIndex, block.EndCharIndex);
            Assert.NotEmpty(rects);
            vm.ActivePdfTab!.EditPlan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Highlight,
                BoundsPt = block.BoundsPt,
                HighlightRectsPt = [.. rects],
                ColorHex = "#FACC15"
            });
            vm.ActivePdfTab.EditPlan.Annotations.Add(new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.Strikeout,
                BoundsPt = block.BoundsPt,
                HighlightRectsPt = [.. rects],
                ColorHex = "#EF4444"
            });

            // 2. Edit PDF text block inline & commit into Plan
            vm.CommitPdfTextEditDirect(new PdfTextEdit
            {
                PageIndex = 0,
                OriginalBoundsPt = block.BoundsPt,
                OriginalText = block.Text,
                NewText = "Updated Final Title",
                FontSizePt = block.FontSizePt,
                FontFamily = block.FontName
            });

            await vm.SaveToPathAsync(outPdf);
            Assert.True(File.Exists(outPdf));

            await using var verifyEngine = new PdfiumRenderEngine();
            await verifyEngine.OpenAsync(outPdf, ct: ct);
            string extracted = await verifyEngine.ExtractPageTextAsync(0, ct);
            Assert.Contains("Updated Final Title", extracted);
            Assert.DoesNotContain("Original Headline Text", extracted);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SpreadsheetWorkspace_XlsxAndCsv_RoundTripEditSortSearchAndExportPdf()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioV2Sheet_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string xlsxPath = CreateSampleXlsx(Path.Combine(tempDir, "Sales.xlsx"));
        string csvPath = Path.Combine(tempDir, "Data.csv");
        string exportedPdf = Path.Combine(tempDir, "Sales.pdf");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);
            await vm.LoadFileAsync(xlsxPath);

            var sheetTab = Assert.IsType<SpreadsheetWorkspaceViewModel>(vm.ActiveTab);
            Assert.Equal(2, sheetTab.Sheets.Count);
            Assert.Equal("Sales", sheetTab.ActiveSheet?.Name);
            Assert.Equal("Beta", sheetTab.ActiveSheet!.Rows[2][0]);

            // Inline cell edit + undo/redo
            sheetTab.CaptureUndoSnapshotBeforeCellEdit();
            sheetTab.ActiveSheet.Rows[1][0] = "Zeta";
            Assert.True(sheetTab.IsDirty);
            Assert.True(sheetTab.CanUndo);

            vm.UndoCommand.Execute(null);
            Assert.Equal("Alpha", sheetTab.ActiveSheet.Rows[1][0]);

            vm.RedoCommand.Execute(null);
            Assert.Equal("Zeta", sheetTab.ActiveSheet.Rows[1][0]);

            // Insert row + search + sort
            sheetTab.SelectedRowIndex = 1;
            sheetTab.InsertRowBelowCommand.Execute(null);
            sheetTab.ActiveSheet.Rows[2][0] = "Gamma";
            sheetTab.RunSearch("Gamma");
            Assert.Equal(1, sheetTab.SearchMatchCount);

            sheetTab.SelectedColumnIndex = 0;
            sheetTab.SortDescendingCommand.Execute(null);

            // Round-trip atomic save back to .xlsx
            await vm.SaveToPathAsync(xlsxPath);
            var reloaded = await SpreadsheetService.LoadAsync(xlsxPath, ct);
            Assert.Equal(2, reloaded.Sheets.Count);
            Assert.Contains(reloaded.Sheets[0].Rows, r => r.Count > 0 && r[0] == "Zeta");

            // Export to PDF & verify via PDFium
            await vm.ExportActiveTabToPdfPathAsync(exportedPdf);
            Assert.True(File.Exists(exportedPdf));
            await using var verifyPdf = new PdfiumRenderEngine();
            await verifyPdf.OpenAsync(exportedPdf, ct: ct);
            string pdfText = await verifyPdf.ExtractPageTextAsync(0, ct);
            Assert.Contains("Zeta", pdfText);

            // CSV round-trip
            await File.WriteAllTextAsync(csvPath, "ColA,ColB\n10,\"hello, world\"\n", ct);
            var csvTab = new SpreadsheetWorkspaceViewModel(csvPath);
            await csvTab.LoadAsync(ct);
            Assert.Equal("hello, world", csvTab.ActiveSheet!.Rows[1][1]);
            csvTab.ActiveSheet.Rows[1][1] = "updated";
            await csvTab.SaveAsync(csvPath, ct);
            var csvReloaded = await SpreadsheetService.LoadAsync(csvPath, ct);
            Assert.Equal("updated", csvReloaded.Sheets[0].Rows[1][1]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RichDocWorkspace_Docx_RoundTripBlocksFormattingTableAndExportPdf()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioV2Docx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docxPath = CreateSampleDocx(Path.Combine(tempDir, "Spec.docx"));
        string exportedPdf = Path.Combine(tempDir, "Spec.pdf");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);
            await vm.LoadFileAsync(docxPath);

            var docTab = Assert.IsType<RichDocWorkspaceViewModel>(vm.ActiveTab);
            Assert.Equal(2, docTab.Blocks.Count);
            Assert.Equal(RichDocBlockKind.Heading1, docTab.Blocks[0].Kind);

            docTab.SelectBlockCommand.Execute(docTab.Blocks[1]);
            docTab.ToggleBoldCommand.Execute(null);
            docTab.ToggleBulletListCommand.Execute(null);
            docTab.Blocks[1].Text = "Bullet item saved cleanly";
            docTab.InsertTableCommand.Execute(null);
            Assert.Equal(3, docTab.Blocks.Count);

            await vm.SaveToPathAsync(docxPath);
            var reloaded = await RichDocService.LoadAsync(docxPath, ct);
            Assert.Equal(3, reloaded.Blocks.Count);
            Assert.Equal(RichDocBlockKind.BulletItem, reloaded.Blocks[1].Kind);
            Assert.True(reloaded.Blocks[1].IsBold);
            Assert.Equal(RichDocBlockKind.Table, reloaded.Blocks[2].Kind);

            await vm.ExportActiveTabToPdfPathAsync(exportedPdf);
            Assert.True(File.Exists(exportedPdf));
            await using var verifyPdf = new PdfiumRenderEngine();
            await verifyPdf.OpenAsync(exportedPdf, ct: ct);
            string pdfText = await verifyPdf.ExtractPageTextAsync(0, ct);
            Assert.Contains("Project Overview", pdfText);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MarkdownAndImageWorkspaces_EditTransformSaveAndExportPdf()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioV2MdImg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string mdPath = Path.Combine(tempDir, "Readme.md");
        string imgPath = CreateSamplePng(Path.Combine(tempDir, "Banner.png"), 80, 40);
        string imgPdf = Path.Combine(tempDir, "Banner.pdf");

        try
        {
            await File.WriteAllTextAsync(mdPath, "# Studio v2\n\n- Fast tabs\n- Native editors\n", Encoding.UTF8, ct);

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);
            await vm.LoadFileAsync(mdPath);

            var mdTab = Assert.IsType<MarkdownTextWorkspaceViewModel>(vm.ActiveTab);
            Assert.True(mdTab.IsMarkdownFile);
            mdTab.SetSplitPreviewModeCommand.Execute(null);
            Assert.True(mdTab.IsSplitMode);
            var parsedMd = MarkdownTextService.ParseMarkdown(mdTab.TextContent);
            Assert.NotEmpty(parsedMd);

            mdTab.RunSearch("Fast tabs");
            mdTab.ReplaceText = "Browser tabs";
            mdTab.ReplaceCurrentMatch("Fast tabs");
            Assert.True(mdTab.IsDirty);
            await vm.SaveToPathAsync(mdPath);
            Assert.Contains("Browser tabs", await File.ReadAllTextAsync(mdPath, ct));

            // Image load + Rotate 90 CW + Flip + Crop + Resize + Undo + Save + Export PDF
            await vm.LoadFileAsync(imgPath);
            var imgTab = Assert.IsType<ImageWorkspaceViewModel>(vm.ActiveTab);
            Assert.Equal(80, imgTab.ImageWidth);
            Assert.Equal(40, imgTab.ImageHeight);

            imgTab.Rotate90CwCommand.Execute(null);
            Assert.Equal(40, imgTab.ImageWidth);
            Assert.Equal(80, imgTab.ImageHeight);

            imgTab.FlipHorizontalCommand.Execute(null);
            imgTab.ResizeWidth = 60;
            imgTab.ResizeHeight = 120;
            imgTab.ApplyResizeCommand.Execute(null);
            Assert.Equal(60, imgTab.ImageWidth);
            Assert.Equal(120, imgTab.ImageHeight);

            vm.UndoCommand.Execute(null);
            Assert.Equal(40, imgTab.ImageWidth);

            await vm.SaveToPathAsync(imgPath);
            using var reloadedBmp = SKBitmap.Decode(imgPath);
            Assert.NotNull(reloadedBmp);
            Assert.Equal(40, reloadedBmp!.Width);
            Assert.Equal(80, reloadedBmp.Height);

            await vm.ExportActiveTabToPdfPathAsync(imgPdf);
            Assert.True(File.Exists(imgPdf));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task BrowserTabErgonomics_AndPolymorphicHeadlessSwitching_MountsOnlyActiveEditor()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioV2Tabs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = CreateTextPdf(Path.Combine(tempDir, "Contract.pdf"), "Agreement");
        string xlsxPath = CreateSampleXlsx(Path.Combine(tempDir, "Budget.xlsx"));
        string docxPath = CreateSampleDocx(Path.Combine(tempDir, "Brief.docx"));
        string mdPath = Path.Combine(tempDir, "Notes.md");
        string pngPath = CreateSamplePng(Path.Combine(tempDir, "Diagram.png"));

        try
        {
            await File.WriteAllTextAsync(mdPath, "# Notes\nHello World\n");

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);
            var window = new MainWindow { DataContext = vm };
            window.Show();

            await vm.LoadFileAsync(pdfPath);
            await vm.LoadFileAsync(xlsxPath);
            await vm.LoadFileAsync(docxPath);
            await vm.LoadFileAsync(mdPath);
            await vm.LoadFileAsync(pngPath);

            Assert.Equal(5, vm.OpenTabs.Count);
            Assert.True(vm.IsImageWorkspaceActive);
            Assert.False(vm.IsPdfWorkspaceActive);

            // Switch via Ctrl+1..5 style helper and verify polymorphic workspace flags & breadcrumbs
            await vm.SelectTabByNumberCommand.ExecuteAsync("1");
            Assert.True(vm.IsPdfWorkspaceActive);
            Assert.Equal("PDF", vm.ActiveTab!.FormatBadgeText);
            Assert.Equal("1p", vm.ActiveTab.MicroMetadataBadge);
            Assert.NotEmpty(vm.ActiveTab.BreadcrumbSegments);

            await vm.SelectTabByNumberCommand.ExecuteAsync("2");
            Assert.True(vm.IsSpreadsheetWorkspaceActive);
            Assert.Equal("XLSX", vm.ActiveTab!.FormatBadgeText);

            await vm.SelectTabByNumberCommand.ExecuteAsync("3");
            Assert.True(vm.IsRichDocWorkspaceActive);
            Assert.Equal("DOCX", vm.ActiveTab!.FormatBadgeText);

            await vm.SelectTabByNumberCommand.ExecuteAsync("4");
            Assert.True(vm.IsMarkdownWorkspaceActive);
            Assert.Equal("MD", vm.ActiveTab!.FormatBadgeText);

            // Pinning moves pinned tab to left & All Open Tabs filter narrows cleanly
            vm.TogglePinTabCommand.Execute(vm.OpenTabs[3]); // Notes.md
            Assert.True(vm.OpenTabs[0].IsPinned);
            Assert.Equal("Notes.md", vm.OpenTabs[0].Title);

            vm.TabSearchQuery = "Budget";
            Assert.Single(vm.FilteredOpenTabs);
            vm.TabSearchQuery = string.Empty;

            // Duplicate, Close, and Reopen Closed Tab (Ctrl+Shift+T)
            await vm.DuplicateTabCommand.ExecuteAsync(vm.OpenTabs[1]);
            Assert.Equal(6, vm.OpenTabs.Count);

            var sheetToClose = vm.OpenTabs.First(t => t.Title == "Budget.xlsx");
            await vm.CloseTabCommand.ExecuteAsync(sheetToClose);
            Assert.Equal(5, vm.OpenTabs.Count);

            await vm.ReopenClosedTabCommand.ExecuteAsync(null);
            Assert.Contains(vm.OpenTabs, t => t.Title == "Budget.xlsx");

            // Reopened tab is active -> check host Content & realized DataTemplate view in NonPdfWorkspaceHost
            await vm.SelectTabByNumberCommand.ExecuteAsync("2"); // Contract.pdf
            Assert.True(vm.IsPdfWorkspaceActive);
            var pdfCanvas = window.FindControl<PdfCanvasControl>("PdfCanvas");
            Assert.NotNull(pdfCanvas);
            Assert.True(pdfCanvas!.IsVisible);

            var budgetTab = vm.OpenTabs.First(t => t.Title == "Budget.xlsx");
            await vm.CloseTabCommand.ExecuteAsync(budgetTab);
            await vm.ReopenClosedTabCommand.ExecuteAsync(null);
            Assert.IsType<SpreadsheetWorkspaceViewModel>(vm.ActiveTab);

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var host = window.FindControl<ContentControl>("NonPdfWorkspaceHost");
            Assert.NotNull(host);
            Assert.True(host!.IsVisible);
            Assert.IsType<SpreadsheetWorkspaceViewModel>(host.Content);
            Assert.False(pdfCanvas.IsVisible);

            window.Close();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RichDoc_NumberingSchemaOrder_ExplicitFalseFlags_And_MultilineRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "PdfStudioDocxSchema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string docxPath = Path.Combine(tempDir, "schema.docx");

        try
        {
            // Seed a docx with explicit <w:b w:val="0"/> and <w:i w:val="0"/> so parser must NOT mark them bold/italic
            using (var doc = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var rPr = new DocumentFormat.OpenXml.Wordprocessing.RunProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.Bold { Val = false },
                    new DocumentFormat.OpenXml.Wordprocessing.Italic { Val = false });
                main.Document = new Document(new Body(
                    new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(rPr, new DocumentFormat.OpenXml.Wordprocessing.Text("Plain Text")))));
                main.Document.Save();
            }

            var loaded = await RichDocService.LoadAsync(docxPath, ct);
            Assert.Single(loaded.Blocks);
            Assert.False(loaded.Blocks[0].IsBold);
            Assert.False(loaded.Blocks[0].IsItalic);

            loaded.Blocks[0].Text = "Line One\nLine Two";
            loaded.Blocks.Add(new RichDocBlock { Kind = RichDocBlockKind.BulletItem, Text = "Bullet A" });
            loaded.Blocks.Add(new RichDocBlock { Kind = RichDocBlockKind.NumberedItem, Text = "Number 1" });
            await RichDocService.SaveAtomicAsync(docxPath, loaded, ct);

            // Verify OpenXML schema order: all AbstractNum must precede all NumberingInstance
            using (var check = WordprocessingDocument.Open(docxPath, false))
            {
                var children = check.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!.ChildElements.ToList();
                int lastAbstract = -1;
                int firstInstance = int.MaxValue;
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i] is AbstractNum) lastAbstract = i;
                    if (children[i] is NumberingInstance && firstInstance == int.MaxValue) firstInstance = i;
                }
                Assert.True(lastAbstract >= 0 && firstInstance > lastAbstract,
                    "All <w:abstractNum> elements must appear before <w:num> elements.");
            }

            var reloaded = await RichDocService.LoadAsync(docxPath, ct);
            Assert.Equal("Line One\nLine Two", reloaded.Blocks[0].Text);
            Assert.Equal(RichDocBlockKind.BulletItem, reloaded.Blocks[1].Kind);
            Assert.Equal(RichDocBlockKind.NumberedItem, reloaded.Blocks[2].Kind);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PdfSharp_MultilineTextEdit_And_SequentialTjStripping()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "PdfStudioPdfEdit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string srcPath = Path.Combine(tempDir, "multi.pdf");
        string dstPath = Path.Combine(tempDir, "multi_edited.pdf");

        try
        {
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14, XFontStyle.Regular);
                gfx.DrawString("AlphaTarget", font, XBrushes.Black, new XPoint(60, 100));
                gfx.DrawString("KeepSecond", font, XBrushes.Black, new XPoint(250, 100));
                doc.Save(srcPath);
            }

            await using var engine = new PdfiumRenderEngine();
            await engine.OpenAsync(srcPath, ct: ct);
            var layout = await engine.GetPageTextLayoutAsync(0, 0, ct);
            var block = layout.Blocks.First(b => b.Text.Contains("AlphaTarget"));

            var plan = new PdfStudio.Core.Editing.StructuralEditPlan();
            plan.ResetFromPageCount(1);
            plan.HasChanges = true;
            plan.TextEdits.Add(new PdfTextEdit
            {
                PageIndex = 0,
                OriginalBoundsPt = block.BoundsPt,
                OriginalText = block.Text,
                NewText = "FirstLine\nSecondLine",
                FontFamily = "Arial",
                FontSizePt = 14
            });

            var editor = new PdfSharpEditorService();
            await editor.ApplyPlanAndSaveAsync(srcPath, dstPath, plan, ct: ct);

            await using var checkEngine = new PdfiumRenderEngine();
            await checkEngine.OpenAsync(dstPath, ct: ct);
            var updatedLayout = await checkEngine.GetPageTextLayoutAsync(0, 0, ct);
            string fullText = string.Concat(updatedLayout.Chars.Select(c => c.Character));
            Assert.Contains("FirstLine", fullText);
            Assert.Contains("SecondLine", fullText);
            Assert.Contains("KeepSecond", fullText);
            Assert.DoesNotContain("AlphaTarget", fullText);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SearchClear_And_UntitledSaveAs_And_MarkdownUndoBaseline_WorkCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "PdfStudioVmFixes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string mdPath = Path.Combine(tempDir, "notes.md");
        string saveAsPath = Path.Combine(tempDir, "saved_new.md");

        try
        {
            await File.WriteAllTextAsync(mdPath, "alpha beta alpha", ct);
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);

            // 1. Load markdown, search, and verify ClearSearch via CloseSearchBar clears workspace search state
            await vm.LoadFileAsync(mdPath);
            var mdVm = Assert.IsType<MarkdownTextWorkspaceViewModel>(vm.ActiveTab);
            vm.OpenSearchBarCommand.Execute(null);
            vm.SearchQuery = "alpha";
            await vm.RunSearchAsync("alpha");
            Assert.Equal(2, vm.TotalSearchMatches);
            Assert.Equal("alpha", mdVm.FindQuery);

            vm.CloseSearchBarCommand.Execute(null);
            Assert.Equal(0, vm.TotalSearchMatches);
            Assert.Equal("0 of 0", mdVm.SearchStatusLabel);
            Assert.Empty(mdVm.SearchMatches);

            // 2. Editing text and reverting back to baseline clears IsDirty
            mdVm.UpdateTextFromEditor("alpha beta gamma");
            Assert.True(mdVm.IsDirty);
            mdVm.UpdateTextFromEditor("alpha beta alpha");
            Assert.False(mdVm.IsDirty);

            // 3. New untitled tab triggers RequestSaveFilePath on SaveDocument instead of writing Untitled-N silently
            await vm.NewMarkdownDocumentCommand.ExecuteAsync(null);
            Assert.False(File.Exists(vm.ActiveTab!.OriginalFilePath));
            bool promptedSaveAs = false;
            vm.RequestSaveFilePath += _ =>
            {
                promptedSaveAs = true;
                return Task.FromResult<string?>(saveAsPath);
            };
            await vm.SaveDocumentCommand.ExecuteAsync(null);
            Assert.True(promptedSaveAs);
            Assert.True(File.Exists(saveAsPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Spreadsheet_HeaderSort_PendingEditCommit_And_ClearSearch_WorkCorrectly()
    {
        var wb = new SpreadsheetWorkbookData();
        var sheet = new SpreadsheetSheetData { Name = "Data" };
        sheet.Rows.Add(["Item", "Qty"]);
        sheet.Rows.Add(["Pear", "10"]);
        sheet.Rows.Add(["Apple", "2"]);
        wb.Sheets.Add(sheet);

        var vm = new SpreadsheetWorkspaceViewModel("test.xlsx");
        vm.LoadFromWorkbook(wb);

        // SortColumnByHeader sorts ascending then toggles descending while preserving header row
        vm.SortColumnByHeader(1);
        Assert.Equal("Apple", vm.ActiveSheet!.Rows[1][0]);
        vm.SortColumnByHeader(1);
        Assert.Equal("Pear", vm.ActiveSheet!.Rows[1][0]);

        // Search and ClearSearch
        vm.RunSearch("Apple");
        Assert.Equal("1 of 1", vm.SearchStatusLabel);
        vm.ClearSearch();
        Assert.Equal("0 of 0", vm.SearchStatusLabel);
        Assert.Empty(vm.SearchMatches);

        // Pending snapshot commit pushes undo cleanly
        var snap = vm.BuildWorkbookData();
        vm.CommitPendingCellEditSnapshot(snap);
        Assert.True(vm.CanUndo);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task RichDoc_MixedRunsHyperlinksAndEmbeddedElements_PreservedOnInPlaceSave()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioDocxPreserve_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docxPath = Path.Combine(tempDir, "rich_mixed.docx");

        try
        {
            using (var doc = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var linkRel = main.AddHyperlinkRelationship(new Uri("https://example.com/docs"), isExternal: true);

                var mixedParagraph = new Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("Hello ") { Space = SpaceProcessingModeValues.Preserve }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold()),
                        new DocumentFormat.OpenXml.Wordprocessing.Text("bold")),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text(" and ") { Space = SpaceProcessingModeValues.Preserve }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Italic()),
                        new DocumentFormat.OpenXml.Wordprocessing.Text("italic")),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text(" visit ") { Space = SpaceProcessingModeValues.Preserve }),
                    new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.RunProperties(
                                new DocumentFormat.OpenXml.Wordprocessing.Underline { Val = DocumentFormat.OpenXml.Wordprocessing.UnderlineValues.Single }),
                            new DocumentFormat.OpenXml.Wordprocessing.Text("portal")))
                    {
                        Id = linkRel.Id,
                        History = true
                    },
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text(".")));

                var drawingParagraph = new Paragraph(
                    new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Drawing()),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("Figure 1 Caption")));

                var table = new DocumentFormat.OpenXml.Wordprocessing.Table(
                    new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }),
                    new TableRow(
                        new TableCell(
                            new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "E2E8F0" }),
                            new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold()),
                                new DocumentFormat.OpenXml.Wordprocessing.Text("Key")))),
                        new TableCell(
                            new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F8FAFC" }),
                            new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("OldValue"))))));

                main.Document = new Document(new Body(mixedParagraph, drawingParagraph, table));
                main.Document.Save();
            }

            var vm = new RichDocWorkspaceViewModel(docxPath);
            await vm.LoadAsync(ct);

            Assert.True(vm.HasUnsupportedElementsWarning);
            Assert.NotEmpty(vm.Blocks);
            Assert.Equal(3, vm.Blocks.Count);

            var p0 = vm.Blocks[0];
            Assert.True(p0.HasMixedFormatting);
            Assert.Contains(p0.Runs, r => r.IsBold && r.Text == "bold");
            Assert.Contains(p0.Runs, r => r.IsItalic && r.Text == "italic");
            Assert.Contains(p0.Runs, r => r.IsUnderline && r.HyperlinkUri == "https://example.com/docs" && r.Text == "portal");

            // Edit trailing character of paragraph 0 via diff so bold/italic/hyperlink runs survive
            p0.Text = p0.Text[..^1] + "!";
            // Also sub-select "Hello" (0..5) and toggle underline via VM selection API
            p0.SelectionStart = 0;
            p0.SelectionLength = 5;
            vm.SelectBlockCommand.Execute(p0);
            vm.ToggleUnderlineCommand.Execute(null);

            // Modify only cell (0,1) in table, leave cell (0,0) and drawing paragraph untouched
            vm.Blocks[2].TableRows[0].Cells[1].Text = "NewValue";

            await vm.SaveAsync(docxPath, ct);

            using (var check = WordprocessingDocument.Open(docxPath, false))
            {
                var body = check.MainDocumentPart!.Document!.Body!;
                var paragraphs = body.Elements<Paragraph>().ToList();
                Assert.Equal(2, paragraphs.Count);

                // Unmodified drawing paragraph keeps <w:drawing> and center justification
                var savedDrawingPara = paragraphs[1];
                Assert.Single(savedDrawingPara.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>());
                Assert.Equal(JustificationValues.Center, savedDrawingPara.ParagraphProperties?.Justification?.Val?.Value);

                // Mixed paragraph keeps hyperlink, bold, italic, and new sub-range underline
                var savedMixed = paragraphs[0];
                var savedHyperlinks = savedMixed.Elements<DocumentFormat.OpenXml.Wordprocessing.Hyperlink>().ToList();
                Assert.Single(savedHyperlinks);
                string linkId = savedHyperlinks[0].Id!.Value!;
                var rel = check.MainDocumentPart.HyperlinkRelationships.First(r => r.Id == linkId);
                Assert.Equal("https://example.com/docs", rel.Uri.AbsoluteUri);

                var reloaded = await RichDocService.LoadAsync(docxPath, ct);
                Assert.Contains(reloaded.Blocks[0].Runs, r => r.IsUnderline && r.Text == "Hello");
                Assert.Contains(reloaded.Blocks[0].Runs, r => r.IsBold && r.Text == "bold");
                Assert.Contains(reloaded.Blocks[0].Runs, r => r.IsItalic && r.Text == "italic");
                Assert.EndsWith("!", reloaded.Blocks[0].Text);

                // Table keeps tblPr, untouched cell (0,0) shading & bold, and edited cell (0,1) shading
                var savedTable = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().Single();
                Assert.Equal("5000", savedTable.GetFirstChild<TableProperties>()?.TableWidth?.Width?.Value);
                var savedRow = savedTable.Elements<TableRow>().Single();
                var savedCells = savedRow.Elements<TableCell>().ToList();
                Assert.Equal("E2E8F0", savedCells[0].TableCellProperties?.Shading?.Fill?.Value);
                Assert.NotNull(savedCells[0].Descendants<DocumentFormat.OpenXml.Wordprocessing.Bold>().FirstOrDefault());
                Assert.Equal("F8FAFC", savedCells[1].TableCellProperties?.Shading?.Fill?.Value);
                Assert.Equal("NewValue", reloaded.Blocks[2].TableRows[0][1]);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Spreadsheet_FormulasStylesDatesColumnWidths_AndCalculationChain_PreservedAndUpdatedOnSave()
    {
        var ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioXlsxPreserve_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string xlsxPath = Path.Combine(tempDir, "financial.xlsx");

        try
        {
            using (var doc = SpreadsheetDocument.Create(xlsxPath, SpreadsheetDocumentType.Workbook))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();

                var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
                stylesPart.Stylesheet = new Stylesheet(
                    new NumberingFormats(
                        new DocumentFormat.OpenXml.Spreadsheet.NumberingFormat { NumberFormatId = 164, FormatCode = "$#,##0.00" }) { Count = 1 },
                    new DocumentFormat.OpenXml.Spreadsheet.Fonts(new DocumentFormat.OpenXml.Spreadsheet.Font()) { Count = 1 },
                    new Fills(new Fill(new PatternFill { PatternType = PatternValues.None })) { Count = 1 },
                    new Borders(new DocumentFormat.OpenXml.Spreadsheet.Border()) { Count = 1 },
                    new CellFormats(
                        new CellFormat { NumberFormatId = 0 },
                        new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
                        new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }) { Count = 3 });
                stylesPart.Stylesheet.Save();

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                wsPart.Worksheet = new Worksheet(
                    new DocumentFormat.OpenXml.Spreadsheet.Columns(
                        new DocumentFormat.OpenXml.Spreadsheet.Column { Min = 1, Max = 1, Width = 24.0, CustomWidth = true },
                        new DocumentFormat.OpenXml.Spreadsheet.Column { Min = 2, Max = 2, Width = 18.5, CustomWidth = true }),
                    new SheetData(
                        new Row(
                            new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Date")) },
                            new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Amount")) },
                            new Cell { CellReference = "C1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Double")) },
                            new Cell { CellReference = "D1", DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("Sum")) })
                        { RowIndex = 1 },
                        new Row(
                            new Cell { CellReference = "A2", StyleIndex = 2, CellValue = new CellValue("45292") },
                            new Cell { CellReference = "B2", StyleIndex = 1, DataType = CellValues.Number, CellValue = new CellValue("100") },
                            new Cell { CellReference = "C2", StyleIndex = 1, CellFormula = new CellFormula("B2*2"), CellValue = new CellValue("200") },
                            new Cell { CellReference = "D2", StyleIndex = 1, CellFormula = new CellFormula("SUM(B2:B3)"), CellValue = new CellValue("350") })
                        { RowIndex = 2 },
                        new Row(
                            new Cell { CellReference = "A3", StyleIndex = 2, CellValue = new CellValue("45293") },
                            new Cell { CellReference = "B3", StyleIndex = 1, DataType = CellValues.Number, CellValue = new CellValue("250") })
                        { RowIndex = 3 }));
                wsPart.Worksheet.Save();

                var calcPart = wbPart.AddNewPart<CalculationChainPart>();
                calcPart.CalculationChain = new CalculationChain(
                    new CalculationCell { CellReference = "C2", SheetId = 1 },
                    new CalculationCell { CellReference = "D2", SheetId = 1 });
                calcPart.CalculationChain.Save();

                var sheets = wbPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Ledger" });
                wbPart.Workbook.Save();
            }

            // 1. Load & verify serial dates formatted cleanly and column widths preserved
            var vm = new SpreadsheetWorkspaceViewModel(xlsxPath);
            await vm.LoadAsync(ct);

            Assert.Equal("2024-01-01", vm.ActiveSheet!.Rows[1][0]);
            Assert.Equal("2024-01-02", vm.ActiveSheet.Rows[2][0]);
            Assert.Equal(2, vm.ActiveSheet.ColumnWidths.Count);
            Assert.Equal(24.0, vm.ActiveSheet.ColumnWidths[0].Width, 2);

            // 2. Edit non-formula cell B3, save, verify formulas, styles, serial dates, cols & calcChain preserved
            vm.ActiveSheet.Rows[2][1] = "300";
            await vm.SaveAsync(xlsxPath, ct);

            using (var check1 = SpreadsheetDocument.Open(xlsxPath, false))
            {
                var wbPart = check1.WorkbookPart!;
                Assert.NotNull(wbPart.CalculationChainPart);

                var ws = wbPart.WorksheetParts.First().Worksheet!;
                var cols = ws.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.Columns>()!
                    .Elements<DocumentFormat.OpenXml.Spreadsheet.Column>().ToList();
                Assert.Equal(24.0, cols[0].Width!.Value, 2);
                Assert.Equal(18.5, cols[1].Width!.Value, 2);

                var cells = ws.Descendants<Cell>().ToDictionary(c => c.CellReference!.Value!);
                Assert.Equal("45292", cells["A2"].CellValue!.Text);
                Assert.Equal(2u, cells["A2"].StyleIndex!.Value);
                Assert.Equal("300", cells["B3"].CellValue!.Text);
                Assert.Equal(1u, cells["B3"].StyleIndex!.Value);
                Assert.Equal("B2*2", cells["C2"].CellFormula?.Text);
                Assert.Equal(1u, cells["C2"].StyleIndex!.Value);
                Assert.Equal("SUM(B2:B3)", cells["D2"].CellFormula?.Text);
            }

            // 3. Edit/overwrite formula cell C2 -> CalculationChainPart must be removed while D2 keeps formula
            vm.ActiveSheet!.Rows[1][2] = "999";
            await vm.SaveAsync(xlsxPath, ct);

            using (var check2 = SpreadsheetDocument.Open(xlsxPath, false))
            {
                var wbPart = check2.WorkbookPart!;
                Assert.Null(wbPart.CalculationChainPart);
                Assert.True(wbPart.Workbook!.CalculationProperties?.FullCalculationOnLoad?.Value == true);

                var cells = wbPart.WorksheetParts.First().Worksheet!.Descendants<Cell>()
                    .ToDictionary(c => c.CellReference!.Value!);
                Assert.Null(cells["C2"].CellFormula);
                Assert.Equal("999", cells["C2"].CellValue!.Text);
                Assert.Equal(1u, cells["C2"].StyleIndex!.Value);
                Assert.Equal("SUM(B2:B3)", cells["D2"].CellFormula?.Text);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RichDoc_FindAndReplace_ReplacesSingleAndAll_WithUndoRedo()
    {
        var model = new RichDocModel();
        model.Blocks.Add(new RichDocBlock
        {
            Kind = RichDocBlockKind.Paragraph,
            Text = "alpha one alpha two"
        });
        model.Blocks.Add(new RichDocBlock
        {
            Kind = RichDocBlockKind.Paragraph,
            Text = "unchanged paragraph"
        });
        var table = new RichDocBlock { Kind = RichDocBlockKind.Table };
        table.TableRows.Add(["ALPHA cell", "other"]);
        model.Blocks.Add(table);

        var vm = new RichDocWorkspaceViewModel("sample.docx");
        vm.LoadFromModel(model);

        vm.FindQuery = "alpha";
        Assert.Equal(2, vm.SearchMatches.Count);
        Assert.Equal("1 of 2", vm.SearchStatusLabel);

        vm.ReplaceText = "omega";
        vm.ReplaceCurrentMatch();

        Assert.Equal("omega one alpha two", vm.Blocks[0].Text);
        Assert.Equal("ALPHA cell", vm.Blocks[2].TableRows[0].Cells[0].Text);
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanUndo);
        Assert.Equal("1 of 2", vm.SearchStatusLabel);

        int replaced = vm.ReplaceAllMatches();
        Assert.Equal(2, replaced);
        Assert.Equal("omega one omega two", vm.Blocks[0].Text);
        Assert.Equal("omega cell", vm.Blocks[2].TableRows[0].Cells[0].Text);
        Assert.Equal("0 of 0", vm.SearchStatusLabel);

        vm.Undo();
        Assert.Equal("omega one alpha two", vm.Blocks[0].Text);
        Assert.Equal("ALPHA cell", vm.Blocks[2].TableRows[0].Cells[0].Text);

        vm.Undo();
        Assert.Equal("alpha one alpha two", vm.Blocks[0].Text);
        Assert.False(vm.IsDirty);

        vm.Redo();
        vm.Redo();
        Assert.Equal("omega one omega two", vm.Blocks[0].Text);
        Assert.Equal("omega cell", vm.Blocks[2].TableRows[0].Cells[0].Text);
    }

    [Fact]
    public void Spreadsheet_FindAndReplace_ReplacesSingleAndAll_AcrossSheets_WithUndoRedo()
    {
        var wb = new SpreadsheetWorkbookData();
        wb.Sheets.Add(new SpreadsheetSheetData
        {
            Name = "Q1",
            Rows =
            {
                new List<string> { "Tax", "100" },
                new List<string> { "Subtotal Tax", "Tax" }
            }
        });
        wb.Sheets.Add(new SpreadsheetSheetData
        {
            Name = "Q2",
            Rows =
            {
                new List<string> { "tax Credit", "50" }
            }
        });

        var vm = new SpreadsheetWorkspaceViewModel("book.xlsx");
        vm.LoadFromWorkbook(wb);

        vm.FindQuery = "tax";
        Assert.Equal(4, vm.SearchMatches.Count);
        Assert.Equal("1 of 4", vm.SearchStatusLabel);

        vm.NextSearchMatch();
        vm.ReplaceText = "Duty";
        vm.ReplaceCurrentMatch();

        Assert.Equal("Subtotal Duty", vm.Sheets[0].Rows[1][0]);
        Assert.Equal(3, vm.SearchMatches.Count);
        Assert.Equal("1 of 3", vm.SearchStatusLabel);
        Assert.True(vm.IsDirty);

        int replaced = vm.ReplaceAllMatches();
        Assert.Equal(3, replaced);
        Assert.Equal("Duty", vm.Sheets[0].Rows[0][0]);
        Assert.Equal("Duty", vm.Sheets[0].Rows[1][1]);
        Assert.Equal("Duty Credit", vm.Sheets[1].Rows[0][0]);
        Assert.Equal("0 of 0", vm.SearchStatusLabel);

        vm.Undo();
        Assert.Equal("Tax", vm.Sheets[0].Rows[0][0]);
        Assert.Equal("Subtotal Duty", vm.Sheets[0].Rows[1][0]);

        vm.Undo();
        Assert.Equal("Subtotal Tax", vm.Sheets[0].Rows[1][0]);
        Assert.False(vm.IsDirty);

        vm.Redo();
        vm.Redo();
        Assert.Equal("Duty Credit", vm.Sheets[1].Rows[0][0]);
    }
}
