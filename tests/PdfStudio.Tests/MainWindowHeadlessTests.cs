using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using PdfSharpCore.Pdf;
using PdfStudio.App.Controls;
using PdfStudio.App.Themes;
using PdfStudio.App.ViewModels;
using PdfStudio.App.Views;
using PdfStudio.Rendering.Pdfium;
using SkiaSharp;
using Xunit;

namespace PdfStudio.Tests;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public async Task MainWindow_LoadsDesignTokens_AndSwitchesAllThemesWithPersistence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioThemeTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath);
            var window = new MainWindow { DataContext = vm };
            window.Show();

            Assert.True(window.IsVisible);
            Assert.Equal("Obsidian", vm.SelectedTheme.Name);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

            // Verify default Obsidian 4-layer elevation colors
            AssertThemeBrushes(window, ThemeManager.Obsidian);

            // Verify all 6 curated VS Code-style presets
            ThemePreset[] presets =
            [
                ThemeManager.StudioLight,
                ThemeManager.WarmPaper,
                ThemeManager.MidnightNord,
                ThemeManager.TokyoNight,
                ThemeManager.MonokaiDimmed,
                ThemeManager.Obsidian
            ];

            foreach (var preset in presets)
            {
                vm.SelectThemeCommand.Execute(preset);
                Assert.Equal(preset.Id, vm.SelectedTheme.Id);
                Assert.Equal(preset.IsDark ? ThemeVariant.Dark : ThemeVariant.Light, Application.Current.RequestedThemeVariant);
                AssertThemeBrushes(window, preset);
            }

            // Persist Warm Paper and verify restoration in a fresh ViewModel instance
            vm.SelectThemeCommand.Execute(ThemeManager.TokyoNight);
            Assert.True(File.Exists(settingsPath));

            await using var engine2 = new PdfiumRenderEngine();
            var restoredVm = new MainWindowViewModel(engine2, settingsFilePath: settingsPath);
            Assert.Equal(ThemeManager.TokyoNight.Id, restoredVm.SelectedTheme.Id);
            AssertThemeBrushes(window, ThemeManager.TokyoNight);

            // Toggle sidebars & command palette & zoom
            restoredVm.ToggleLeftPanelCommand.Execute(null);
            Assert.False(restoredVm.IsLeftPanelOpen);
            restoredVm.ToggleRightPanelCommand.Execute(null);
            Assert.False(restoredVm.IsRightPanelOpen);

            restoredVm.ToggleCommandPaletteCommand.Execute(null);
            Assert.True(restoredVm.IsCommandPaletteOpen);

            restoredVm.ZoomInCommand.Execute(null);
            Assert.Equal("125%", restoredVm.ZoomText);

            window.Close();
        }
        finally
        {
            // Restore default Obsidian so static App resources remain clean for subsequent runs
            ThemeManager.ApplyTheme(ThemeManager.Obsidian);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task PdfCanvas_CtrlMouseWheelZoom_MarksHandledImmediately_AndLocksCursorPointInSingleFrame()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioZoomTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var pdfPath = Path.Combine(tempDir, "multipage.pdf");
            using (var doc = new PdfDocument())
            {
                for (int i = 0; i < 3; i++)
                {
                    var p = doc.AddPage();
                    p.Width = 612;
                    p.Height = 792;
                }
                doc.Save(pdfPath);
            }

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath);
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 1280,
                Height = 820
            };
            window.Show();

            await vm.LoadFileAsync(pdfPath);
            window.UpdateLayout();

            var canvas = window.FindControl<PdfCanvasControl>("PdfCanvas");
            Assert.NotNull(canvas);

            // Ensure viewport has laid out bounds and start slightly scrolled onto page 1
            canvas.UpdateLayout();
            double initialZoom = canvas.CurrentZoom;
            Assert.Equal(100.0, initialZoom, 1);

            var anchor = new Point(canvas.Bounds.Width * 0.55, 260);
            double preDocX = canvas.Offset.X + anchor.X;
            double preDocY = canvas.Offset.Y + anchor.Y;
            double pageWidth100 = 612.0 * (96.0 / 72.0);
            double prePageLeft = (Math.Max(canvas.Bounds.Width, pageWidth100 + 48.0) - pageWidth100) * 0.5;
            double expectedRelX = (preDocX - prePageLeft) / pageWidth100;
            double expectedRelY = (preDocY - 24.0) / (792.0 * (96.0 / 72.0));

            // Fire Ctrl + Mouse Wheel via Avalonia's PointerWheelChangedEvent
            var pointer = new Avalonia.Input.Pointer(0, PointerType.Mouse, isPrimary: true);
            var rootPos = canvas.TranslatePoint(anchor, window) ?? anchor;
            var wheelArgs = new PointerWheelEventArgs(
                canvas,
                pointer,
                window,
                rootPos,
                timestamp: 100,
                new PointerPointProperties(RawInputModifiers.Control, PointerUpdateKind.Other),
                KeyModifiers.Control,
                new Vector(0, 1))
            {
                RoutedEvent = InputElement.PointerWheelChangedEvent
            };

            canvas.RaiseEvent(wheelArgs);

            Assert.True(wheelArgs.Handled);
            Assert.True(canvas.CurrentZoom > initialZoom);

            // Verify cursor point stays anchored synchronously in the same frame (no timer lag)
            double scaleFactor = canvas.CurrentZoom / 100.0;
            double pageWidthAfter = Math.Round(612.0 * (96.0 / 72.0) * scaleFactor);
            double pageHeightAfter = Math.Round(792.0 * (96.0 / 72.0) * scaleFactor);
            double postPageLeft = (Math.Max(canvas.Bounds.Width, pageWidthAfter + 48.0) - pageWidthAfter) * 0.5;

            double postDocX = canvas.Offset.X + anchor.X;
            double postDocY = canvas.Offset.Y + anchor.Y;
            double actualRelX = (postDocX - postPageLeft) / pageWidthAfter;
            double actualRelY = (postDocY - 24.0) / pageHeightAfter;

            Assert.InRange(Math.Abs(actualRelX - expectedRelX), 0.0, 0.01);
            Assert.InRange(Math.Abs(actualRelY - expectedRelY), 0.0, 0.01);

            window.Close();
        }
        finally
        {
            ThemeManager.ApplyTheme(ThemeManager.Obsidian);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void AssertThemeBrushes(Window window, ThemePreset preset)
    {
        var variant = preset.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        Assert.True(window.TryFindResource("CanvasBackgroundBrush", variant, out var canvasRes));
        Assert.Equal(Color.Parse(preset.DeskHex), Assert.IsType<SolidColorBrush>(canvasRes).Color);

        Assert.True(window.TryFindResource("PanelBackgroundBrush", variant, out var panelRes));
        Assert.Equal(Color.Parse(preset.SidebarHex), Assert.IsType<SolidColorBrush>(panelRes).Color);

        Assert.True(window.TryFindResource("FloatingToolbarBrush", variant, out var floatRes));
        Assert.Equal(Color.Parse(preset.ToolbarHex), Assert.IsType<SolidColorBrush>(floatRes).Color);

        Assert.True(window.TryFindResource("HairlineBorderBrush", variant, out var borderRes));
        Assert.Equal(Color.Parse(preset.HairlineBorderHex), Assert.IsType<SolidColorBrush>(borderRes).Color);

        Assert.True(window.TryFindResource("AccentBrush", variant, out var accentRes));
        Assert.Equal(Color.Parse(preset.AccentHex), Assert.IsType<SolidColorBrush>(accentRes).Color);
    }

    [AvaloniaFact]
    public async Task PdfDarkModeInversion_TogglesPerTab_AndTransformsCanvasColors()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioDarkInvertTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            static void WriteBlankPdf(string path)
            {
                using var doc = new PdfDocument();
                var p = doc.AddPage();
                p.Width = 612;
                p.Height = 792;
                doc.Save(path);
            }

            string pdfA = Path.Combine(tempDir, "docA.pdf");
            string pdfB = Path.Combine(tempDir, "docB.pdf");
            WriteBlankPdf(pdfA);
            WriteBlankPdf(pdfB);

            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine, settingsFilePath: settingsPath);
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 1280,
                Height = 820
            };
            window.Show();

            await vm.LoadFileAsync(pdfA);
            var tabA = Assert.IsType<PdfWorkspaceViewModel>(vm.ActiveTab);
            await vm.LoadFileAsync(pdfB);
            var tabB = Assert.IsType<PdfWorkspaceViewModel>(vm.ActiveTab);
            window.UpdateLayout();

            var invertButton = window.FindControl<Button>("PdfDarkInvertButton");
            Assert.NotNull(invertButton);
            Assert.False(vm.IsPdfDarkInvertEnabled);
            Assert.DoesNotContain("active", invertButton.Classes);

            invertButton.Command!.Execute(null);
            Assert.True(vm.IsPdfDarkInvertEnabled);
            Assert.True(tabB.IsDarkInvertEnabled);
            Assert.False(tabA.IsDarkInvertEnabled);
            Assert.Contains("active", invertButton.Classes);

            await vm.SwitchTabCommand.ExecuteAsync(tabA);
            Assert.False(vm.IsPdfDarkInvertEnabled);
            Assert.DoesNotContain("active", invertButton.Classes);

            await vm.SwitchTabCommand.ExecuteAsync(tabB);
            Assert.True(vm.IsPdfDarkInvertEnabled);
            Assert.Contains("active", invertButton.Classes);

            var invertedWhite = PdfCanvasControl.TransformColorForDarkInvert(SKColors.White);
            Assert.InRange(invertedWhite.Red, (byte)15, (byte)45);
            Assert.InRange(invertedWhite.Green, (byte)15, (byte)45);
            Assert.InRange(invertedWhite.Blue, (byte)20, (byte)55);

            var invertedBlack = PdfCanvasControl.TransformColorForDarkInvert(SKColors.Black);
            Assert.InRange(invertedBlack.Red, (byte)220, (byte)255);
            Assert.InRange(invertedBlack.Green, (byte)215, (byte)255);
            Assert.InRange(invertedBlack.Blue, (byte)210, (byte)255);

            window.Close();
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
