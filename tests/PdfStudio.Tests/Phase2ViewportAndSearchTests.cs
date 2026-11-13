using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using PdfStudio.App.ViewModels;
using PdfStudio.Rendering.Pdfium;
using Xunit;

namespace PdfStudio.Tests;

public sealed class Phase2ViewportAndSearchTests
{
    static Phase2ViewportAndSearchTests()
    {
        if (GlobalFontSettings.FontResolver is null)
        {
            GlobalFontSettings.FontResolver = new WindowsTestFontResolver();
        }
    }

    private static string CreateSearchFixturePdf()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pdfstudio_search_{Guid.NewGuid():N}.pdf");
        using var doc = new PdfDocument();

        var page1 = doc.AddPage();
        using (var gfx1 = XGraphics.FromPdfPage(page1))
        {
            var font = new XFont("Arial", 14, XFontStyle.Regular);
            gfx1.DrawString("Native PDFium Search Target Alpha", font, XBrushes.Black, new XPoint(72, 100));
        }

        var page2 = doc.AddPage();
        using (var gfx2 = XGraphics.FromPdfPage(page2))
        {
            var font = new XFont("Arial", 14, XFontStyle.Regular);
            gfx2.DrawString("Second Page Search Target Beta", font, XBrushes.Black, new XPoint(72, 140));
        }

        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task PdfiumRenderEngine_ExtractsTextAndFindsBoundingBoxes()
    {
        string path = CreateSearchFixturePdf();
        try
        {
            await using var engine = new PdfiumRenderEngine();
            var ct = TestContext.Current.CancellationToken;
            var pages = await engine.OpenAsync(path, ct: ct);
            Assert.Equal(2, pages.Count);

            string p1Text = await engine.ExtractPageTextAsync(0, ct);
            Assert.Contains("Target", p1Text);

            var matches = await engine.FindTextAsync(0, "Target", matchCase: false, ct);
            Assert.Single(matches);
            Assert.Equal(0, matches[0].PageIndex);
            Assert.Equal("Target".Length, matches[0].Bounds.Count);
            Assert.All(matches[0].Bounds, box =>
            {
                Assert.True(box.Right > box.Left);
                Assert.True(box.Top > box.Bottom);
            });
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MainWindowViewModel_FullTextSearch_NavigatesMatches()
    {
        string path = CreateSearchFixturePdf();
        try
        {
            await using var engine = new PdfiumRenderEngine();
            var vm = new MainWindowViewModel(engine);

            await vm.LoadFileAsync(path);
            Assert.True(vm.HasOpenDocument);
            Assert.Equal(2, vm.TotalPages);

            await vm.RunSearchAsync("Target");
            Assert.Equal(2, vm.TotalSearchMatches);
            Assert.Equal("1 of 2", vm.SearchCounterText);
            Assert.Equal(1, vm.CurrentPage);

            vm.NextSearchMatchCommand.Execute(null);
            Assert.Equal("2 of 2", vm.SearchCounterText);
            Assert.Equal(2, vm.CurrentPage);

            vm.PreviousSearchMatchCommand.Execute(null);
            Assert.Equal("1 of 2", vm.SearchCounterText);
            Assert.Equal(1, vm.CurrentPage);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class WindowsTestFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        public byte[] GetFont(string faceName)
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string arial = Path.Combine(fontsDir, "arial.ttf");
            return File.ReadAllBytes(arial);
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
            => new FontResolverInfo("Arial");
    }
}

