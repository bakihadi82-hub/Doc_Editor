using PdfSharpCore.Pdf;
using PdfStudio.Core.Rendering;
using PdfStudio.Rendering.Pdfium;
using SkiaSharp;
using Xunit;

namespace PdfStudio.Tests;

public sealed class Phase1EngineTests
{
    [Fact]
    public void LruBitmapCache_EvictsAndDisposesOldestBeyondBudget()
    {
        // Each 100x100 BGRA bitmap = 40,000 bytes. Budget = 70,000 bytes => holds 1 at a time.
        using var cache = new LruBitmapCache(maxBytes: 70_000);
        var bmp1 = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp2 = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul);

        cache.Put(new BitmapCacheKey(0, 100, 100, 0), bmp1);
        Assert.Equal(1, cache.Count);

        cache.Put(new BitmapCacheKey(1, 100, 100, 0), bmp2);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(new BitmapCacheKey(0, 100, 100, 0), out _));
        Assert.True(cache.TryGet(new BitmapCacheKey(1, 100, 100, 0), out var hit));
        Assert.Same(bmp2, hit);
    }

    [Fact]
    public async Task PdfiumRenderEngine_LoadsAndRendersSyntheticPdfDirectlyToSKBitmap()
    {
        using var doc = new PdfDocument();
        var p1 = doc.AddPage();
        p1.Width = 612;
        p1.Height = 792;
        var p2 = doc.AddPage();
        p2.Width = 595;
        p2.Height = 842;

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        byte[] bytes = ms.ToArray();

        await using var engine = new PdfiumRenderEngine();
        var pages = await engine.OpenBytesAsync(bytes, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, pages.Count);
        Assert.Equal(612f, pages[0].WidthPoints, 1);
        Assert.Equal(792f, pages[0].HeightPoints, 1);
        Assert.Equal(595f, pages[1].WidthPoints, 1);

        using var rendered = await engine.RenderPageAsync(new RenderRequest(
            PageIndex: 0,
            TargetWidthPx: 306,
            TargetHeightPx: 396,
            RotationQuarterTurns: 0,
            Priority: RenderPriority.VisibleViewport));

        Assert.Equal(306, rendered.Width);
        Assert.Equal(396, rendered.Height);
        Assert.Equal(SKColorType.Bgra8888, rendered.ColorType);
    }

    [Fact]
    public async Task PdfiumRenderEngine_OpenAsync_StreamsIntoNativeMemory_AndDisposalCancelsQueuedTasks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"PdfStudioOpenDispose_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string pdfPath = Path.Combine(tempDir, "large_multi.pdf");

        try
        {
            using (var doc = new PdfDocument())
            {
                // Make page count >= 12 and include >300KB of stream content to exercise chunked FileStream -> NativeMemory reads.
                string paddingComment = "% " + new string('A', 40_000) + "\n";
                for (int i = 0; i < 12; i++)
                {
                    var page = doc.AddPage();
                    page.Width = 612;
                    page.Height = 792;
                    var content = new PdfDictionary(doc);
                    content.CreateStream(System.Text.Encoding.ASCII.GetBytes(paddingComment + "q Q\n"));
                    doc.Internals.AddObject(content);
                    page.Contents.Elements.Add(content.Reference!);
                }
                doc.Save(pdfPath);
            }

            Assert.True(new FileInfo(pdfPath).Length > 256 * 1024);

            var engine = new PdfiumRenderEngine();
            var ct = TestContext.Current.CancellationToken;
            var pages = await engine.OpenAsync(pdfPath, ct: ct);
            Assert.Equal(12, pages.Count);

            using (var first = await engine.RenderPageAsync(new RenderRequest(0, 200, 260, 0, RenderPriority.VisibleViewport, ct)))
            {
                Assert.Equal(200, first.Width);
                Assert.Equal(260, first.Height);
            }

            // Queue multiple lower-priority thumbnail renders, then immediately dispose engine while they wait.
            var queuedTasks = new List<Task<SKBitmap>>();
            for (int i = 0; i < 24; i++)
            {
                queuedTasks.Add(engine.RenderPageAsync(new RenderRequest(i % 12, 560, 720, 0, RenderPriority.Thumbnail, ct)));
            }

            await engine.DisposeAsync();

            bool sawCanceled = false;
            foreach (var task in queuedTasks)
            {
                try
                {
                    using var bmp = await task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    sawCanceled = true;
                }
            }

            Assert.True(sawCanceled, "Expected at least one queued lower-priority render task to be canceled on disposal instead of hanging.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}

