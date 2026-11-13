using PdfStudio.Core.Documents;

namespace PdfStudio.Core.Forms;

public enum PdfFormFieldKind
{
    Text,
    CheckBox,
    Choice
}

public sealed class PdfFormField
{
    public string Name { get; init; } = string.Empty;
    public PdfFormFieldKind Kind { get; init; }
    public int PageIndex { get; set; }

    /// <summary>
    /// Normalized page rectangle in top-left PDF points (Left &lt;= Right, Top &lt;= Bottom).
    /// </summary>
    public PdfRect BoundsPt { get; set; } = new(0, 0, 0, 0);

    public string Value { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
    public bool IsReadOnly { get; init; }

    public PdfFormField Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        PageIndex = PageIndex,
        BoundsPt = BoundsPt,
        Value = Value,
        IsChecked = IsChecked,
        IsReadOnly = IsReadOnly
    };
}

