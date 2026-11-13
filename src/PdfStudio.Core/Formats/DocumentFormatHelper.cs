namespace PdfStudio.Core.Formats;

public enum DocumentFormatKind
{
    Pdf,
    Spreadsheet,
    RichDocument,
    MarkdownText,
    Image
}

public sealed record FormatDescriptor(
    DocumentFormatKind Kind,
    string BadgeText,
    string ColorHex,
    string DisplayName,
    string Extension);

public static class DocumentFormatHelper
{
    public static readonly string[] SupportedExtensions =
    [
        ".pdf",
        ".xlsx", ".csv", ".tsv",
        ".docx",
        ".md", ".txt", ".json", ".xml", ".log",
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    ];

    public static bool IsSupportedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return SupportedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static DocumentFormatKind GetFormatKind(string path)
        => Describe(path).Kind;

    public static FormatDescriptor Describe(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => new FormatDescriptor(DocumentFormatKind.Pdf, "PDF", "#EF4444", "PDF Document", ext),
            ".xlsx" => new FormatDescriptor(DocumentFormatKind.Spreadsheet, "XLSX", "#10B981", "Excel Workbook", ext),
            ".csv" => new FormatDescriptor(DocumentFormatKind.Spreadsheet, "CSV", "#10B981", "CSV Spreadsheet", ext),
            ".tsv" => new FormatDescriptor(DocumentFormatKind.Spreadsheet, "TSV", "#10B981", "TSV Spreadsheet", ext),
            ".docx" => new FormatDescriptor(DocumentFormatKind.RichDocument, "DOCX", "#3B82F6", "Word Document", ext),
            ".md" => new FormatDescriptor(DocumentFormatKind.MarkdownText, "MD", "#A855F7", "Markdown Document", ext),
            ".json" => new FormatDescriptor(DocumentFormatKind.MarkdownText, "JSON", "#A855F7", "JSON File", ext),
            ".xml" => new FormatDescriptor(DocumentFormatKind.MarkdownText, "XML", "#A855F7", "XML File", ext),
            ".txt" => new FormatDescriptor(DocumentFormatKind.MarkdownText, "TXT", "#F59E0B", "Plain Text", ext),
            ".log" => new FormatDescriptor(DocumentFormatKind.MarkdownText, "LOG", "#F59E0B", "Log File", ext),
            ".png" => new FormatDescriptor(DocumentFormatKind.Image, "PNG", "#14B8A6", "PNG Image", ext),
            ".jpg" or ".jpeg" => new FormatDescriptor(DocumentFormatKind.Image, "JPG", "#14B8A6", "JPEG Image", ext),
            ".webp" => new FormatDescriptor(DocumentFormatKind.Image, "WEBP", "#14B8A6", "WebP Image", ext),
            ".bmp" => new FormatDescriptor(DocumentFormatKind.Image, "BMP", "#14B8A6", "Bitmap Image", ext),
            _ => new FormatDescriptor(DocumentFormatKind.MarkdownText, "TXT", "#F59E0B", "Text File", ext)
        };
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }
}
