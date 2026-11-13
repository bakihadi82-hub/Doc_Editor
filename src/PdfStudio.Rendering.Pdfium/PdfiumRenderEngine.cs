using System.Runtime.InteropServices;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Rendering;
using PdfStudio.Rendering.Pdfium.Interop;
using SkiaSharp;

namespace PdfStudio.Rendering.Pdfium;

/// <summary>
/// High-performance PDFium render engine keeping a persistent FPDF_DOCUMENT handle in native memory
/// and rasterizing directly into SkiaSharp SKBitmap buffers via FPDFBitmap_CreateEx (zero-copy).
/// </summary>
public sealed class PdfiumRenderEngine : IPdfRenderEngine
{
    private static int _libraryRefCount;
    private static readonly Lock LibraryGate = new();

    private readonly PdfiumWorker _worker = new();
    private SafePdfDocumentHandle? _documentHandle;
    private nint _pinnedBuffer;
    private int _pinnedLength;
    private int _pageCount;
    private bool _disposed;

    public bool IsOpen => _documentHandle is { IsInvalid: false, IsClosed: false };
    public int PageCount => _pageCount;

    public PdfiumRenderEngine()
    {
        lock (LibraryGate)
        {
            if (_libraryRefCount == 0)
            {
                PdfiumNative.FPDF_InitLibrary();
            }
            _libraryRefCount++;
        }
    }

    public async Task<IReadOnlyList<PdfPageInfo>> OpenAsync(string filePath, string? password = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        return await OpenBytesAsync(bytes, password, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PdfPageInfo>> OpenBytesAsync(ReadOnlyMemory<byte> pdfBytes, string? password = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _worker.EnqueueAsync<IReadOnlyList<PdfPageInfo>>(() =>
        {
            CloseCore();

            if (pdfBytes.IsEmpty)
            {
                throw new InvalidDataException("PDF buffer is empty.");
            }

            unsafe
            {
                _pinnedLength = pdfBytes.Length;
                _pinnedBuffer = (nint)NativeMemory.Alloc((nuint)_pinnedLength);
                pdfBytes.Span.CopyTo(new Span<byte>((void*)_pinnedBuffer, _pinnedLength));
            }

            nint rawDoc = PdfiumNative.FPDF_LoadMemDocument(_pinnedBuffer, _pinnedLength, password);
            if (rawDoc == 0)
            {
                uint err = PdfiumNative.FPDF_GetLastError();
                FreePinnedBuffer();
                throw new InvalidOperationException($"PDFium failed to load document (error code {err}).");
            }

            _documentHandle = new SafePdfDocumentHandle(rawDoc);
            _pageCount = PdfiumNative.FPDF_GetPageCount(rawDoc);

            var pages = new List<PdfPageInfo>(_pageCount);
            for (int i = 0; i < _pageCount; i++)
            {
                PdfiumNative.FPDF_GetPageSizeByIndexF(rawDoc, i, out var size);
                pages.Add(new PdfPageInfo(i, size.Width, size.Height));
            }

            return pages;
        }, RenderPriority.VisibleViewport, ct);
    }

    public Task CloseAsync()
    {
        if (_disposed) return Task.CompletedTask;
        return _worker.EnqueueAsync(() =>
        {
            CloseCore();
            return true;
        }, RenderPriority.VisibleViewport);
    }

    public Task<SKBitmap> RenderPageAsync(RenderRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TargetWidthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TargetHeightPx);

        return _worker.EnqueueAsync(() =>
        {
            if (_documentHandle is null || _documentHandle.IsInvalid)
            {
                throw new InvalidOperationException("No PDF document is open.");
            }

            nint doc = _documentHandle.DangerousGetHandle();
            nint rawPage = PdfiumNative.FPDF_LoadPage(doc, request.PageIndex);
            if (rawPage == 0)
            {
                throw new InvalidOperationException($"Failed to load PDF page {request.PageIndex}.");
            }

            using var pageHandle = new SafePdfPageHandle(rawPage);

            var info = new SKImageInfo(
                request.TargetWidthPx,
                request.TargetHeightPx,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            var bitmap = new SKBitmap(info);
            try
            {
                nint pixels = bitmap.GetPixels();
                nint rawBitmap = PdfiumNative.FPDFBitmap_CreateEx(
                    request.TargetWidthPx,
                    request.TargetHeightPx,
                    PdfiumNative.BitmapFormatBgra,
                    pixels,
                    bitmap.RowBytes);

                if (rawBitmap == 0)
                {
                    throw new OutOfMemoryException("FPDFBitmap_CreateEx failed.");
                }

                using var pdfBitmap = new SafePdfBitmapHandle(rawBitmap);

                // Fill page paper background crisp opaque white before PDFium paints page primitives
                PdfiumNative.FPDFBitmap_FillRect(
                    rawBitmap,
                    0,
                    0,
                    request.TargetWidthPx,
                    request.TargetHeightPx,
                    0xFFFFFFFF);

                int rotate = ((request.RotationQuarterTurns % 4) + 4) % 4;
                PdfiumNative.FPDF_RenderPageBitmap(
                    rawBitmap,
                    rawPage,
                    0,
                    0,
                    request.TargetWidthPx,
                    request.TargetHeightPx,
                    rotate,
                    PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT);

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }, request.Priority, request.CancellationToken);
    }

    public Task<IReadOnlyList<PdfOutlineNode>> GetOutlineAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _worker.EnqueueAsync<IReadOnlyList<PdfOutlineNode>>(() =>
        {
            if (_documentHandle is null || _documentHandle.IsInvalid)
            {
                return [];
            }

            nint doc = _documentHandle.DangerousGetHandle();
            return ReadBookmarkSiblings(doc, 0);
        }, RenderPriority.Thumbnail, ct);
    }

    public Task<string> ExtractPageTextAsync(int pageIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _worker.EnqueueAsync(() =>
        {
            if (_documentHandle is null || _documentHandle.IsInvalid)
            {
                return string.Empty;
            }

            nint rawPage = PdfiumNative.FPDF_LoadPage(_documentHandle.DangerousGetHandle(), pageIndex);
            if (rawPage == 0) return string.Empty;
            using var page = new SafePdfPageHandle(rawPage);

            nint rawText = PdfiumNative.FPDFText_LoadPage(rawPage);
            if (rawText == 0) return string.Empty;
            using var textPage = new SafePdfTextPageHandle(rawText);

            int count = PdfiumNative.FPDFText_CountChars(rawText);
            if (count <= 0) return string.Empty;

            unsafe
            {
                char[] buffer = new char[count + 1];
                fixed (char* ptr = buffer)
                {
                    int written = PdfiumNative.FPDFText_GetText(rawText, 0, count, (nint)ptr);
                    return written > 1 ? new string(buffer, 0, written - 1) : string.Empty;
                }
            }
        }, RenderPriority.Thumbnail, ct);
    }

    public Task<IReadOnlyList<PdfTextMatch>> FindTextAsync(int pageIndex, string query, bool matchCase = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(query))
        {
            return Task.FromResult<IReadOnlyList<PdfTextMatch>>([]);
        }

        return _worker.EnqueueAsync<IReadOnlyList<PdfTextMatch>>(() =>
        {
            if (_documentHandle is null || _documentHandle.IsInvalid)
            {
                return [];
            }

            nint rawPage = PdfiumNative.FPDF_LoadPage(_documentHandle.DangerousGetHandle(), pageIndex);
            if (rawPage == 0) return [];
            using var page = new SafePdfPageHandle(rawPage);

            nint rawText = PdfiumNative.FPDFText_LoadPage(rawPage);
            if (rawText == 0) return [];
            using var textPage = new SafePdfTextPageHandle(rawText);

            var matches = new List<PdfTextMatch>();
            unsafe
            {
                fixed (char* queryPtr = query)
                {
                    uint flags = matchCase ? PdfiumNative.FindMatchCase : 0;
                    nint search = PdfiumNative.FPDFText_FindStart(rawText, (nint)queryPtr, flags, 0);
                    if (search == 0) return matches;

                    try
                    {
                        while (PdfiumNative.FPDFText_FindNext(search) != 0)
                        {
                            int startIdx = PdfiumNative.FPDFText_GetSchResultIndex(search);
                            int length = PdfiumNative.FPDFText_GetSchCount(search);
                            var boxes = new List<PdfRect>(length);
                            for (int i = 0; i < length; i++)
                            {
                                if (PdfiumNative.FPDFText_GetCharBox(rawText, startIdx + i, out double l, out double r, out double b, out double t) != 0)
                                {
                                    boxes.Add(new PdfRect(l, t, r, b));
                                }
                            }
                            matches.Add(new PdfTextMatch(pageIndex, startIdx, length, boxes));
                        }
                    }
                    finally
                    {
                        PdfiumNative.FPDFText_FindClose(search);
                    }
                }
            }

            return matches;
        }, RenderPriority.VisibleViewport, ct);
    }

    public Task<PdfPageTextLayout> GetPageTextLayoutAsync(int pageIndex, int rotationQuarterTurns = 0, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _worker.EnqueueAsync(() =>
        {
            if (_documentHandle is null || _documentHandle.IsInvalid)
            {
                return new PdfPageTextLayout(pageIndex, 612, 792, []);
            }

            nint doc = _documentHandle.DangerousGetHandle();
            PdfiumNative.FPDF_GetPageSizeByIndexF(doc, pageIndex, out var size);
            double w0 = size.Width > 0 ? size.Width : 612.0;
            double h0 = size.Height > 0 ? size.Height : 792.0;
            int rot = ((rotationQuarterTurns % 4) + 4) % 4;
            double effW = rot % 2 != 0 ? h0 : w0;
            double effH = rot % 2 != 0 ? w0 : h0;

            nint rawPage = PdfiumNative.FPDF_LoadPage(doc, pageIndex);
            if (rawPage == 0) return new PdfPageTextLayout(pageIndex, effW, effH, []);
            using var page = new SafePdfPageHandle(rawPage);

            nint rawText = PdfiumNative.FPDFText_LoadPage(rawPage);
            if (rawText == 0) return new PdfPageTextLayout(pageIndex, effW, effH, []);
            using var textPage = new SafePdfTextPageHandle(rawText);

            int count = PdfiumNative.FPDFText_CountChars(rawText);
            if (count <= 0) return new PdfPageTextLayout(pageIndex, effW, effH, []);

            var chars = new List<PdfTextChar>(count);
            byte[] fontBuf = new byte[128];

            for (int i = 0; i < count; i++)
            {
                uint uni = PdfiumNative.FPDFText_GetUnicode(rawText, i);
                char ch = uni <= char.MaxValue ? (char)uni : '\0';

                double fontSize = PdfiumNative.FPDFText_GetFontSize(rawText, i);
                if (fontSize <= 0.1 || double.IsNaN(fontSize)) fontSize = 12.0;

                string fontName = "Arial";
                bool isBold = false;
                bool isItalic = false;
                unsafe
                {
                    fixed (byte* fPtr = fontBuf)
                    {
                        uint len = PdfiumNative.FPDFText_GetFontInfo(rawText, i, (nint)fPtr, (uint)fontBuf.Length, out int flags);
                        if (len > 1)
                        {
                            int strLen = Math.Min((int)len - 1, fontBuf.Length - 1);
                            fontName = System.Text.Encoding.ASCII.GetString(fontBuf, 0, strLen);
                        }
                        isBold = (flags & (1 << 18)) != 0 || fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase);
                        isItalic = (flags & (1 << 6)) != 0 || fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (PdfiumNative.FPDFText_GetCharBox(rawText, i, out double l, out double r, out double b, out double t) != 0)
                {
                    double x0 = Math.Min(l, r);
                    double x1 = Math.Max(l, r);
                    double y0 = h0 - Math.Max(b, t);
                    double y1 = h0 - Math.Min(b, t);

                    (double dl, double dt, double dr, double db) = rot switch
                    {
                        1 => (h0 - y1, x0, h0 - y0, x1),
                        2 => (w0 - x1, h0 - y1, w0 - x0, h0 - y0),
                        3 => (y0, w0 - x1, y1, w0 - x0),
                        _ => (x0, y0, x1, y1)
                    };

                    chars.Add(new PdfTextChar(
                        i,
                        ch,
                        Math.Min(dl, dr),
                        Math.Min(dt, db),
                        Math.Max(dl, dr),
                        Math.Max(dt, db),
                        fontSize,
                        fontName,
                        isBold,
                        isItalic));
                }
                else
                {
                    chars.Add(new PdfTextChar(i, ch, 0, 0, 0, 0, fontSize, fontName, isBold, isItalic));
                }
            }

            return new PdfPageTextLayout(pageIndex, effW, effH, chars);
        }, RenderPriority.VisibleViewport, ct);
    }

    public Task<int> GetCharIndexAtPosAsync(
        int pageIndex,
        double xPdfPt,
        double yPdfPt,
        double xTolerance = 6.0,
        double yTolerance = 6.0,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _worker.EnqueueAsync(() =>
        {
            if (_documentHandle is null || _documentHandle.IsInvalid)
            {
                return -1;
            }

            nint doc = _documentHandle.DangerousGetHandle();
            PdfiumNative.FPDF_GetPageSizeByIndexF(doc, pageIndex, out var size);
            double h0 = size.Height > 0 ? size.Height : 792.0;

            nint rawPage = PdfiumNative.FPDF_LoadPage(doc, pageIndex);
            if (rawPage == 0) return -1;
            using var page = new SafePdfPageHandle(rawPage);

            nint rawText = PdfiumNative.FPDFText_LoadPage(rawPage);
            if (rawText == 0) return -1;
            using var textPage = new SafePdfTextPageHandle(rawText);

            return PdfiumNative.FPDFText_GetCharIndexAtPos(rawText, xPdfPt, h0 - yPdfPt, xTolerance, yTolerance);
        }, RenderPriority.VisibleViewport, ct);
    }

    private static List<PdfOutlineNode> ReadBookmarkSiblings(nint doc, nint parentBookmark)
    {
        var nodes = new List<PdfOutlineNode>();
        nint current = PdfiumNative.FPDFBookmark_GetFirstChild(doc, parentBookmark);
        while (current != 0)
        {
            string title = ReadBookmarkTitle(current);
            nint dest = PdfiumNative.FPDFBookmark_GetDest(doc, current);
            int? targetPage = dest != 0 ? PdfiumNative.FPDFDest_GetDestPageIndex(doc, dest) : null;
            if (targetPage < 0) targetPage = null;

            var children = ReadBookmarkSiblings(doc, current);
            nodes.Add(new PdfOutlineNode(title, targetPage, children));
            current = PdfiumNative.FPDFBookmark_GetNextSibling(doc, current);
        }
        return nodes;
    }

    private static string ReadBookmarkTitle(nint bookmark)
    {
        uint bytesNeeded = PdfiumNative.FPDFBookmark_GetTitle(bookmark, 0, 0);
        if (bytesNeeded <= 2) return string.Empty;

        unsafe
        {
            byte[] buf = new byte[bytesNeeded];
            fixed (byte* ptr = buf)
            {
                PdfiumNative.FPDFBookmark_GetTitle(bookmark, (nint)ptr, bytesNeeded);
            }
            return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }
    }

    private void CloseCore()
    {
        _documentHandle?.Dispose();
        _documentHandle = null;
        _pageCount = 0;
        FreePinnedBuffer();
    }

    private unsafe void FreePinnedBuffer()
    {
        if (_pinnedBuffer != 0)
        {
            NativeMemory.Free((void*)_pinnedBuffer);
            _pinnedBuffer = 0;
            _pinnedLength = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await CloseAsync().ConfigureAwait(false);
        _disposed = true;
        _worker.Dispose();

        lock (LibraryGate)
        {
            _libraryRefCount--;
            if (_libraryRefCount == 0)
            {
                PdfiumNative.FPDF_DestroyLibrary();
            }
        }
    }
}

