using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Formats;

namespace PdfStudio.App.ViewModels;

public sealed record BreadcrumbSegment(string Label, string FullPath, bool IsLast);

public abstract partial class DocumentTabViewModel : ObservableObject, IAsyncDisposable
{
    public DocumentFormatKind FormatKind { get; }

    [ObservableProperty]
    private string _originalFilePath;

    [ObservableProperty]
    private string _workingFilePath;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private double _tabPillWidth = 210;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _formatBadgeText = "DOC";

    [ObservableProperty]
    private string _formatDisplayName = "Document";

    [ObservableProperty]
    private string _formatColorHex = "#3B82F6";

    [ObservableProperty]
    private IBrush _formatBadgeBrush = Brushes.CornflowerBlue;

    [ObservableProperty]
    private IBrush _formatBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(36, 59, 130, 246));

    [ObservableProperty]
    private string _microMetadataBadge = string.Empty;

    [ObservableProperty]
    private string _contextStatusText = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = "—";

    [ObservableProperty]
    private string _lastModifiedDisplay = "—";

    public ObservableCollection<BreadcrumbSegment> BreadcrumbSegments { get; } = [];

    public abstract bool CanUndo { get; }
    public abstract bool CanRedo { get; }

    public bool HasMicroMetadata => !string.IsNullOrWhiteSpace(MicroMetadataBadge);

    protected DocumentTabViewModel(string filePath, DocumentFormatKind formatKind)
    {
        FormatKind = formatKind;
        _originalFilePath = filePath;
        _workingFilePath = filePath;
        _title = string.IsNullOrWhiteSpace(filePath) ? "Untitled" : Path.GetFileName(filePath);
        RefreshFileMetadata();
    }

    partial void OnOriginalFilePathChanged(string value)
    {
        Title = string.IsNullOrWhiteSpace(value) ? "Untitled" : Path.GetFileName(value);
        RefreshFileMetadata();
    }

    partial void OnMicroMetadataBadgeChanged(string value)
    {
        OnPropertyChanged(nameof(HasMicroMetadata));
    }

    public void RefreshFileMetadata()
    {
        var desc = DocumentFormatHelper.Describe(OriginalFilePath);
        FormatBadgeText = desc.BadgeText;
        FormatDisplayName = desc.DisplayName;
        FormatColorHex = desc.ColorHex;

        if (Color.TryParse(desc.ColorHex, out var color))
        {
            FormatBadgeBrush = new SolidColorBrush(color);
            FormatBadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
        }

        if (!string.IsNullOrWhiteSpace(OriginalFilePath) && File.Exists(OriginalFilePath))
        {
            try
            {
                var info = new FileInfo(OriginalFilePath);
                FileSizeDisplay = DocumentFormatHelper.FormatFileSize(info.Length);
                LastModifiedDisplay = info.LastWriteTime.ToString("MMM d, yyyy • HH:mm");
                IsReadOnly = info.IsReadOnly;
            }
            catch
            {
                FileSizeDisplay = "—";
                LastModifiedDisplay = "—";
                IsReadOnly = false;
            }
        }
        else
        {
            FileSizeDisplay = "Unsaved buffer";
            LastModifiedDisplay = "Not saved to disk";
            IsReadOnly = false;
        }

        RebuildBreadcrumbs();
    }

    private void RebuildBreadcrumbs()
    {
        BreadcrumbSegments.Clear();
        if (string.IsNullOrWhiteSpace(OriginalFilePath)) return;

        try
        {
            string full = Path.GetFullPath(OriginalFilePath);
            string? root = Path.GetPathRoot(full);
            string remainder = string.IsNullOrEmpty(root) ? full : full[root.Length..];
            var parts = remainder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            string running = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
            if (!string.IsNullOrEmpty(root))
            {
                string driveLabel = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                BreadcrumbSegments.Add(new BreadcrumbSegment(driveLabel, root, parts.Length == 0));
            }

            // Show up to last 4 directory parts + filename to keep context bar crisp
            int startIdx = Math.Max(0, parts.Length - 4);
            for (int i = 0; i < parts.Length; i++)
            {
                running = string.IsNullOrEmpty(running)
                    ? parts[i]
                    : $"{running}{Path.DirectorySeparatorChar}{parts[i]}";

                if (i < startIdx) continue;
                bool isLast = i == parts.Length - 1;
                BreadcrumbSegments.Add(new BreadcrumbSegment(parts[i], running, isLast));
            }
        }
        catch
        {
            BreadcrumbSegments.Add(new BreadcrumbSegment(Title, OriginalFilePath, true));
        }
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
