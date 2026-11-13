using PdfStudio.Core.Documents;
using SkiaSharp;

namespace PdfStudio.Core.Rendering;

/// <summary>
/// Single-session PDF rendering, outline, and text query abstraction backed by native PDFium.
/// </summary>
public interface IPdfRenderEngine : IAsyncDisposable
{
    bool IsOpen { get; }
    int PageCount { get; }

    Task<IReadOnlyList<PdfPageInfo>> OpenAsync(string filePath, string? password = null, CancellationToken ct = default);
    Task<IReadOnlyList<PdfPageInfo>> OpenBytesAsync(ReadOnlyMemory<byte> pdfBytes, string? password = null, CancellationToken ct = default);
    Task CloseAsync();

    Task<SKBitmap> RenderPageAsync(RenderRequest request);
    Task<IReadOnlyList<PdfOutlineNode>> GetOutlineAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PdfTextMatch>> FindTextAsync(int pageIndex, string query, bool matchCase = false, CancellationToken ct = default);
    Task<string> ExtractPageTextAsync(int pageIndex, CancellationToken ct = default);
    Task<PdfPageTextLayout> GetPageTextLayoutAsync(int pageIndex, int rotationQuarterTurns = 0, CancellationToken ct = default);
    Task<int> GetCharIndexAtPosAsync(int pageIndex, double xPdfPt, double yPdfPt, double xTolerance = 6.0, double yTolerance = 6.0, CancellationToken ct = default);
}

