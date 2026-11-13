using PdfStudio.Core.Forms;

namespace PdfStudio.Core.Editing;

public sealed record PdfMetadataInfo(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer);

public sealed record PdfSaveOptions(
    string? UserPassword = null,
    string? OwnerPassword = null,
    bool UseAes256 = true,
    bool FlattenForms = false,
    bool BurnRedactions = false,
    string? SourcePassword = null);

/// <summary>
/// Structural and document editing service backed by PdfSharpCore.
/// </summary>
public interface IPdfEditorService
{
    Task<PdfMetadataInfo> ReadMetadataAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<PdfFormField>> ReadFormFieldsAsync(string filePath, string? password = null, CancellationToken ct = default);
    Task ApplyPlanAndSaveAsync(
        string sourceFilePath,
        string targetFilePath,
        StructuralEditPlan plan,
        PdfMetadataInfo? updatedMetadata = null,
        CancellationToken ct = default,
        PdfSaveOptions? saveOptions = null);
    Task MergeAsync(IReadOnlyList<string> inputPaths, string outputFilePath, CancellationToken ct = default);
    Task ExtractPagesAsync(string sourceFilePath, IReadOnlyList<int> zeroBasedPageIndices, string outputFilePath, CancellationToken ct = default);
    Task SplitEveryNPagesAsync(string sourceFilePath, int pagesPerFile, string outputDirectory, string filePrefix, CancellationToken ct = default);
}
