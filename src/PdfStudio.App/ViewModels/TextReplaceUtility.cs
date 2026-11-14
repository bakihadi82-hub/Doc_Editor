using System.Text;

namespace PdfStudio.App.ViewModels;

/// <summary>
/// Shared case-insensitive plain-text search and replacement helpers for workspace editors.
/// </summary>
internal static class TextReplaceUtility
{
    public static bool TryReplaceFirst(
        string? input,
        string query,
        string? replacement,
        out string updated)
    {
        string text = input ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            updated = text;
            return false;
        }

        int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            updated = text;
            return false;
        }

        string rep = replacement ?? string.Empty;
        updated = string.Concat(text.AsSpan(0, idx), rep, text.AsSpan(idx + query.Length));
        return true;
    }

    public static string ReplaceAll(
        string? input,
        string query,
        string? replacement,
        out int replacementCount)
    {
        string text = input ?? string.Empty;
        replacementCount = 0;
        if (string.IsNullOrEmpty(query))
        {
            return text;
        }

        int pos = 0;
        int hit = text.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
        if (hit < 0)
        {
            return text;
        }

        string rep = replacement ?? string.Empty;
        var sb = new StringBuilder(text.Length);
        while (hit >= 0)
        {
            sb.Append(text, pos, hit - pos);
            sb.Append(rep);
            replacementCount++;
            pos = hit + query.Length;
            hit = text.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
