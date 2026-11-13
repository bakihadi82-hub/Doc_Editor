using System.Text;
using Markdig;
using Markdig.Syntax;

namespace PdfStudio.Core.Formats;

public static class MarkdownTextService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static async Task<string> LoadAsync(string filePath, CancellationToken ct = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public static async Task SaveAtomicAsync(string targetFilePath, string content, CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(targetFilePath) ?? ".";
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetFilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
                .ConfigureAwait(false);

            var info = new FileInfo(tempPath);
            if (!info.Exists)
            {
                throw new IOException("Atomic text save verification failed.");
            }

            File.Move(tempPath, targetFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    public static MarkdownDocument ParseMarkdown(string markdown)
        => Markdown.Parse(markdown ?? string.Empty, Pipeline);
}
