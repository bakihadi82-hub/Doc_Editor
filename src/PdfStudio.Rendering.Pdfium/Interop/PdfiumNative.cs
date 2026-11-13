using System.Runtime.InteropServices;

namespace PdfStudio.Rendering.Pdfium.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct FsSizeF
{
    public float Width;
    public float Height;
}

/// <summary>
/// Direct .NET 10 source-generated [LibraryImport] P/Invoke bindings for native pdfium.dll.
/// Native binary is supplied automatically via bblanchon.PDFium.Win32.
/// </summary>
internal static partial class PdfiumNative
{
    private const string LibraryName = "pdfium";

    public const int BitmapFormatBgra = 4; // FPDFBitmap_BGRA (B, G, R, A byte order)
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int RenderAnnot = FPDF_ANNOT;
    public const int RenderLcdText = FPDF_LCD_TEXT;
    public const uint FindMatchCase = 0x00000001;

    [LibraryImport(LibraryName)]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(LibraryName)]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport(LibraryName)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadMemDocument(nint dataBuf, int size, string? password);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(LibraryName)]
    public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, out FsSizeF size);

    [LibraryImport(LibraryName)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(LibraryName)]
    public static partial int FPDFPage_GetRotation(nint page);

    [LibraryImport(LibraryName)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(LibraryName)]
    public static partial void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(LibraryName)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(LibraryName)]
    public static partial void FPDF_RenderPageBitmap(
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [LibraryImport(LibraryName)]
    public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(LibraryName)]
    public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport(LibraryName)]
    public static partial uint FPDFBookmark_GetTitle(nint bookmark, nint buffer, uint buflen);

    [LibraryImport(LibraryName)]
    public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(LibraryName)]
    public static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);

    [LibraryImport(LibraryName)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(LibraryName)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_CountChars(nint textPage);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, nint result);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_GetCharBox(
        nint textPage,
        int index,
        out double left,
        out double right,
        out double bottom,
        out double top);

    [LibraryImport(LibraryName)]
    public static partial uint FPDFText_GetUnicode(nint textPage, int index);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_GetCharIndexAtPos(
        nint textPage,
        double x,
        double y,
        double xTolerance,
        double yTolerance);

    [LibraryImport(LibraryName)]
    public static partial double FPDFText_GetFontSize(nint textPage, int index);

    [LibraryImport(LibraryName)]
    public static partial uint FPDFText_GetFontInfo(
        nint textPage,
        int index,
        nint buffer,
        uint buflen,
        out int flags);

    [LibraryImport(LibraryName)]
    public static partial nint FPDFText_FindStart(nint textPage, nint findWhatUtf16, uint flags, int startIndex);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_FindNext(nint handle);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_GetSchResultIndex(nint handle);

    [LibraryImport(LibraryName)]
    public static partial int FPDFText_GetSchCount(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void FPDFText_FindClose(nint handle);
}

