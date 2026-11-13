using Microsoft.Win32.SafeHandles;

namespace PdfStudio.Rendering.Pdfium.Interop;

internal sealed class SafePdfDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdfDocumentHandle() : base(ownsHandle: true) { }

    public SafePdfDocumentHandle(nint handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDF_CloseDocument(handle);
        return true;
    }
}

internal sealed class SafePdfPageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdfPageHandle(nint handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDF_ClosePage(handle);
        return true;
    }
}

internal sealed class SafePdfBitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdfBitmapHandle(nint handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFBitmap_Destroy(handle);
        return true;
    }
}

internal sealed class SafePdfTextPageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdfTextPageHandle(nint handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFText_ClosePage(handle);
        return true;
    }
}

