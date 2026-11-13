using System.IO;
using PdfSharpCore.Pdf;
using PdfStudio.App.ViewModels;
using PdfStudio.Editing.PdfSharp;
using PdfStudio.Rendering.Pdfium;
using Xunit;

namespace PdfStudio.Tests;

public class Phase3StructuralEditingTests
{
    private static string CreateMultiPagePdf(string directory, string fileName, params (double Width, double Height)[] pageSizes)
    {
        var path = Path.Combine(directory, fileName);
        using var document = new PdfDocument();
        foreach (var (width, height) in pageSizes)
        {
            var page = document.AddPage();
            page.Width = width;
            page.Height = height;
        }

        document.Save(path);
        return path;
    }

    [Fact]
    public async Task StructuralOperations_RotateReorderDelete_WithUndoRedo_AndSave()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioPhase3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = CreateMultiPagePdf(tempDir, "source.pdf", (612, 792), (595, 842), (500, 700));
            var savePath = Path.Combine(tempDir, "saved.pdf");

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, new PdfSharpEditorService());

            await vm.LoadFileAsync(pdfPath);

            Assert.Equal(3, vm.TotalPages);
            Assert.False(vm.HasUnsavedChanges);
            Assert.False(vm.CanUndo);
            Assert.False(vm.CanRedo);

            // 1. Rotate page 1 (index 0) 90 deg CW
            vm.CurrentPage = 1;
            vm.RotateSelectedRightCommand.Execute(null);

            Assert.True(vm.HasUnsavedChanges);
            Assert.True(vm.CanUndo);
            Assert.Equal(792, vm.Pages[0].WidthPoints, 1);
            Assert.Equal(612, vm.Pages[0].HeightPoints, 1);
            Assert.Equal(90, vm.Pages[0].RotationDegrees);

            // 2. Move page 0 to index 2 -> order becomes [595x842, 500x700, 792x612]
            vm.MovePage(0, 2);
            Assert.Equal(595, vm.Pages[0].WidthPoints, 1);
            Assert.Equal(500, vm.Pages[1].WidthPoints, 1);
            Assert.Equal(792, vm.Pages[2].WidthPoints, 1);

            // 3. Delete page 3 (index 2, which is 792x612)
            vm.CurrentPage = 3;
            vm.DeleteSelectedPageCommand.Execute(null);
            Assert.Equal(2, vm.TotalPages);
            Assert.Equal(595, vm.Pages[0].WidthPoints, 1);
            Assert.Equal(500, vm.Pages[1].WidthPoints, 1);

            // 4. Undo all 3 edits
            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.TotalPages);
            Assert.Equal(792, vm.Pages[2].WidthPoints, 1);

            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Equal(792, vm.Pages[0].WidthPoints, 1);
            Assert.Equal(595, vm.Pages[1].WidthPoints, 1);
            Assert.Equal(500, vm.Pages[2].WidthPoints, 1);

            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Equal(612, vm.Pages[0].WidthPoints, 1);
            Assert.Equal(792, vm.Pages[0].HeightPoints, 1);
            Assert.Equal(0, vm.Pages[0].RotationDegrees);
            Assert.False(vm.HasUnsavedChanges);
            Assert.False(vm.CanUndo);
            Assert.True(vm.CanRedo);

            // 5. Redo rotation + reorder (leave 3 pages, rotated page moved to end)
            await vm.RedoCommand.ExecuteAsync(null);
            await vm.RedoCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.TotalPages);
            Assert.Equal(595, vm.Pages[0].WidthPoints, 1);
            Assert.Equal(500, vm.Pages[1].WidthPoints, 1);
            Assert.Equal(792, vm.Pages[2].WidthPoints, 1);
            Assert.Equal(612, vm.Pages[2].HeightPoints, 1);
            Assert.True(vm.HasUnsavedChanges);

            // 6. Save to disk and verify persisted PDF in a fresh engine
            await vm.SaveToPathAsync(savePath);
            Assert.False(vm.HasUnsavedChanges);
            Assert.Equal(savePath, vm.DocumentPath);

            await using var verifyEngine = new PdfiumRenderEngine();
            var savedPages = await verifyEngine.OpenAsync(savePath, ct: TestContext.Current.CancellationToken);
            Assert.Equal(3, savedPages.Count);
            Assert.Equal(595, savedPages[0].WidthPoints, 1);
            Assert.Equal(842, savedPages[0].HeightPoints, 1);
            Assert.Equal(500, savedPages[1].WidthPoints, 1);
            Assert.Equal(700, savedPages[1].HeightPoints, 1);
            Assert.Equal(792, savedPages[2].WidthPoints, 1);
            Assert.Equal(612, savedPages[2].HeightPoints, 1);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractAndMerge_ProduceExpectedPageCounts_AndSupportUndo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioMerge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var mainPdf = CreateMultiPagePdf(tempDir, "main.pdf", (612, 792), (595, 842));
            var extraPdf = CreateMultiPagePdf(tempDir, "extra.pdf", (400, 600), (450, 650));
            var extractedPdf = Path.Combine(tempDir, "extracted.pdf");

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, new PdfSharpEditorService());

            await vm.LoadFileAsync(mainPdf);
            Assert.Equal(2, vm.TotalPages);

            // Extract page index 1 (595x842)
            await vm.ExtractPagesToPathAsync([1], extractedPdf);
            Assert.True(File.Exists(extractedPdf));

            await using (var verifyExtractEngine = new PdfiumRenderEngine())
            {
                var extractedPages = await verifyExtractEngine.OpenAsync(extractedPdf, ct: TestContext.Current.CancellationToken);
                Assert.Single(extractedPages);
                Assert.Equal(595, extractedPages[0].WidthPoints, 1);
                Assert.Equal(842, extractedPages[0].HeightPoints, 1);
            }

            // Merge extraPdf (2 pages) into mainPdf -> 4 pages
            await vm.MergeFilesIntoCurrentDocumentAsync([extraPdf]);
            Assert.Equal(4, vm.TotalPages);
            Assert.True(vm.HasUnsavedChanges);
            Assert.True(vm.CanUndo);
            Assert.Equal(400, vm.Pages[2].WidthPoints, 1);
            Assert.Equal(450, vm.Pages[3].WidthPoints, 1);

            // Undo merge restores 2 pages and clears dirty state
            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.TotalPages);
            Assert.False(vm.HasUnsavedChanges);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Tabs_SwitchAndClose_ManageActiveStateAndDirtyFlag()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioTabs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var doc1 = CreateMultiPagePdf(tempDir, "first.pdf", (612, 792));
            var doc2 = CreateMultiPagePdf(tempDir, "second.pdf", (500, 700), (520, 720));

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, new PdfSharpEditorService());

            await vm.LoadFileAsync(doc1);
            await vm.LoadFileAsync(doc2);

            Assert.Equal(2, vm.OpenTabs.Count);
            Assert.Equal("second.pdf", vm.ActiveTab?.Title);
            Assert.Equal(2, vm.TotalPages);

            await vm.PreviousTabCommand.ExecuteAsync(null);
            Assert.Equal("first.pdf", vm.ActiveTab?.Title);
            Assert.Equal(1, vm.TotalPages);

            vm.RotateSelectedRightCommand.Execute(null);
            Assert.True(vm.ActiveTab?.IsDirty);
            Assert.True(vm.HasUnsavedChanges);

            await vm.NextTabCommand.ExecuteAsync(null);
            Assert.Equal("second.pdf", vm.ActiveTab?.Title);
            Assert.False(vm.HasUnsavedChanges);

            await vm.CloseActiveTabCommand.ExecuteAsync(null);
            Assert.Single(vm.OpenTabs);
            Assert.Equal("first.pdf", vm.ActiveTab?.Title);
            Assert.True(vm.HasUnsavedChanges);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
