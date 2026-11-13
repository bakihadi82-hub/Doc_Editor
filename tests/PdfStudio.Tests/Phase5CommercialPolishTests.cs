using System.IO;
using Avalonia.Headless.XUnit;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfStudio.App.Services;
using PdfStudio.App.Themes;
using PdfStudio.App.ViewModels;
using PdfStudio.App.Views;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Editing.PdfSharp;
using PdfStudio.Rendering.Pdfium;
using Xunit;

namespace PdfStudio.Tests;

public sealed class Phase5CommercialPolishTests
{
    private static string CreateMultiPageTextPdf(string path, params string[] pageTexts)
    {
        EncodingBootstrap.EnsureRegistered();
        using var doc = new PdfDocument();
        foreach (string text in pageTexts)
        {
            var page = doc.AddPage();
            page.Width = 612;
            page.Height = 792;
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 14, XFontStyle.Regular);
            gfx.DrawString(text, font, XBrushes.Black, new XPoint(72, 120));
        }

        doc.Save(path);
        return path;
    }

    [Fact]
    public void SettingsPath_AndCrashLogPath_LiveUnderLocalAppData()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expectedSettings = Path.Combine(localAppData, "PdfStudio", "settings.json");
        string expectedCrashLog = Path.Combine(localAppData, "PdfStudio", "logs", "crash.log");

        Assert.Equal(expectedSettings, ThemeManager.GetDefaultSettingsPath());
        Assert.Equal(expectedCrashLog, CrashLogger.GetDefaultLogPath());
    }

    [Fact]
    public async Task SettingsPersistence_WindowLayoutAndRecentFiles_RoundTripAndRemove()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioP5Settings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        string samplePdf = CreateMultiPageTextPdf(Path.Combine(tempDir, "Sample.pdf"), "Hello Page 1", "Hello Page 2");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath);

            vm.WindowWidth = 1480;
            vm.WindowHeight = 920;
            vm.IsWindowMaximized = true;
            vm.LeftSidebarWidth = 248;

            await vm.LoadFileAsync(samplePdf);
            vm.SaveSettingsSnapshot();

            Assert.True(vm.HasRecentFiles);
            Assert.Single(vm.RecentFiles);
            Assert.Equal("Sample.pdf", vm.RecentFiles[0].FileName);
            Assert.Equal("2 pages", vm.RecentFiles[0].PageCountLabel);

            await using var engine2 = new PdfiumRenderEngine();
            var vmReloaded = new MainWindowViewModel(engine2, settingsFilePath: settingsPath);

            Assert.Equal(1480, vmReloaded.WindowWidth);
            Assert.Equal(920, vmReloaded.WindowHeight);
            Assert.True(vmReloaded.IsWindowMaximized);
            Assert.Equal(248, vmReloaded.LeftSidebarWidth);
            Assert.Single(vmReloaded.RecentFiles);
            Assert.Equal(samplePdf, vmReloaded.RecentFiles[0].FilePath);

            vmReloaded.RemoveRecentFileCommand.Execute(vmReloaded.RecentFiles[0]);
            Assert.False(vmReloaded.HasRecentFiles);

            var persisted = ThemeManager.LoadAppSettings(settingsPath);
            Assert.Empty(persisted.RecentFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task BlankPageInsert_CopyPageText_AndPageJump_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioP5Doc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        string samplePdf = CreateMultiPageTextPdf(Path.Combine(tempDir, "Contract.pdf"), "UniqueAlphaClause", "SecondPageBeta");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath);
            string? copiedText = null;
            vm.RequestCopyToClipboard += text =>
            {
                copiedText = text;
                return Task.CompletedTask;
            };

            int scrolledToPage = -1;
            vm.RequestScrollToPage += pageIdx => scrolledToPage = pageIdx;

            await vm.LoadFileAsync(samplePdf);
            Assert.Equal(2, vm.TotalPages);

            vm.ContextPageIndex = 0;
            await vm.CopyPageTextCommand.ExecuteAsync(null);
            Assert.NotNull(copiedText);
            Assert.Contains("UniqueAlphaClause", copiedText!, StringComparison.Ordinal);
            Assert.True(vm.IsToastVisible);
            Assert.Equal("Copied", vm.ToastMessage);

            // Jump to page 2
            vm.BeginPageJumpCommand.Execute(null);
            Assert.True(vm.IsPageJumpEditing);
            vm.PageJumpInput = "2";
            vm.CommitPageJumpCommand.Execute(null);
            Assert.False(vm.IsPageJumpEditing);
            Assert.Equal(1, scrolledToPage);

            // Insert blank page after page 1 and save atomically
            vm.InsertBlankPageAfterCommand.Execute(vm.PageThumbnails[0]);
            Assert.Equal(3, vm.TotalPages);
            Assert.True(vm.Pages[1].IsBlankPage);

            await vm.SaveDocumentCommand.ExecuteAsync(null);
            Assert.False(vm.HasUnsavedChanges);
            Assert.Equal("Saved", vm.ToastMessage);

            // Re-open with PDFium to verify valid 3-page output PDF
            await using var verifyEngine = new PdfiumRenderEngine();
            var reopenedPages = await verifyEngine.OpenAsync(samplePdf);
            Assert.Equal(3, reopenedPages.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ToolProperties_AndDirtyTabCloseConfirmation_WorkProperly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioP5Props_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        string samplePdf = CreateMultiPageTextPdf(Path.Combine(tempDir, "Notes.pdf"), "Annotation Page");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath)
            {
                PromptOnDirtyTabClose = true
            };

            await vm.LoadFileAsync(samplePdf);

            vm.SetDrawToolCommand.Execute(null);
            Assert.True(vm.IsAnnotationToolActive);
            Assert.True(vm.IsStrokeToolActive);

            vm.SetToolColorCommand.Execute("#EF4444");
            vm.SetStrokeWidthCommand.Execute("4");
            Assert.Equal("#EF4444", vm.ActiveToolColorHex);
            Assert.True(vm.IsToolColorRed);
            Assert.True(vm.IsStroke4Pt);

            vm.SetTextToolCommand.Execute(null);
            Assert.True(vm.IsTextOrNoteToolActive);
            vm.SetFontSizeCommand.Execute("16");
            Assert.True(vm.IsFontSize16);

            var ann = new PdfAnnotation
            {
                PageIndex = 0,
                Kind = AnnotationKind.TextBox,
                BoundsPt = new PdfRect(50, 50, 170, 80),
                ColorHex = vm.ActiveToolColorHex,
                FontSizePt = vm.ActiveFontSizePt,
                TextContent = "Review"
            };
            vm.AddAnnotation(ann);
            Assert.True(vm.HasUnsavedChanges);

            // Closing dirty tab prompts confirmation modal when PromptOnDirtyTabClose is enabled
            await vm.CloseActiveTabCommand.ExecuteAsync(null);
            Assert.True(vm.IsUnsavedChangesModalOpen);
            Assert.Single(vm.OpenTabs);

            await vm.ConfirmDiscardAndCloseTabCommand.ExecuteAsync(null);
            Assert.False(vm.IsUnsavedChangesModalOpen);
            Assert.Empty(vm.OpenTabs);
            Assert.False(vm.HasOpenDocument);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CrashLogger_AndSingleInstancePipe_WorkReliably()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioP5Infra_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "logs", "crash.log");

        try
        {
            var ex = new InvalidOperationException("Simulated unhandled crash");
            CrashLogger.LogException(ex, "UnitTest", logPath);

            Assert.True(File.Exists(logPath));
            string content = await File.ReadAllTextAsync(logPath);
            Assert.Contains("UnitTest", content, StringComparison.Ordinal);
            Assert.Contains("Simulated unhandled crash", content, StringComparison.Ordinal);

            string mutexName = $@"Local\PdfStudio_TestMutex_{Guid.NewGuid():N}";
            string pipeName = $"PdfStudio_TestPipe_{Guid.NewGuid():N}";
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool acquired = SingleInstanceCoordinator.TryAcquirePrimaryInstance(
                msg => tcs.TrySetResult(msg),
                out var coordinator,
                mutexName,
                pipeName);

            Assert.True(acquired);
            Assert.NotNull(coordinator);

            try
            {
                string secondPdfPath = Path.Combine(tempDir, "Second.pdf");
                bool secondAcquired = SingleInstanceCoordinator.TryAcquirePrimaryInstance(
                    _ => { },
                    out var secondary,
                    mutexName,
                    pipeName);

                Assert.False(secondAcquired);
                Assert.Null(secondary);

                bool sent = await SingleInstanceCoordinator.SendArgumentsToPrimaryInstanceAsync([secondPdfPath], pipeName, 3000);
                Assert.True(sent);

                string received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(Path.GetFullPath(secondPdfPath), received);
            }
            finally
            {
                coordinator.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task MainWindow_WiresPolishBindings_AndEnablesDirtyClosePrompt()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioP5Win_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath);
            var window = new MainWindow { DataContext = vm };
            window.Show();

            Assert.True(window.IsVisible);
            Assert.True(vm.PromptOnDirtyTabClose);
            Assert.False(vm.IsUnsavedChangesModalOpen);
            Assert.False(vm.IsErrorModalOpen);

            window.Close();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}

