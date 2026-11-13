using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.Core.Formats;
using PdfStudio.Editing.PdfSharp;

namespace PdfStudio.App.ViewModels;

public enum MarkdownViewMode
{
    Editor,
    SplitPreview,
    CleanReader
}

public sealed partial class MarkdownTextWorkspaceViewModel : DocumentTabViewModel
{
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private string _committedBaseline = string.Empty;
    private bool _suppressHistory;
    private readonly List<(int Offset, int Length)> _matches = [];
    private int _activeMatchIndex = -1;

    [ObservableProperty]
    private string _textContent = string.Empty;

    [ObservableProperty]
    private bool _isMarkdownFile;

    [ObservableProperty]
    private MarkdownViewMode _viewMode = MarkdownViewMode.Editor;

    [ObservableProperty]
    private bool _wordWrap = true;

    [ObservableProperty]
    private int _caretLine = 1;

    [ObservableProperty]
    private int _caretColumn = 1;

    [ObservableProperty]
    private int _lineCount = 1;

    [ObservableProperty]
    private string _findQuery = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    [ObservableProperty]
    private string _searchStatusLabel = "0 of 0";

    public bool IsEditorMode => ViewMode == MarkdownViewMode.Editor;
    public bool IsSplitMode => ViewMode == MarkdownViewMode.SplitPreview;
    public bool IsReaderMode => ViewMode == MarkdownViewMode.CleanReader;
    public bool ShowEditorPane => !IsMarkdownFile || ViewMode is MarkdownViewMode.Editor or MarkdownViewMode.SplitPreview;
    public bool ShowPreviewPane => IsMarkdownFile && ViewMode is MarkdownViewMode.SplitPreview or MarkdownViewMode.CleanReader;
    public string SyntaxExtension => Path.GetExtension(OriginalFilePath).ToLowerInvariant();
    public IReadOnlyList<(int Offset, int Length)> SearchMatches => _matches;
    public int ActiveMatchIndex => _activeMatchIndex;

    public override bool CanUndo => RequestCanUndo?.Invoke() ?? _undoStack.Count > 0;
    public override bool CanRedo => RequestCanRedo?.Invoke() ?? _redoStack.Count > 0;

    public Func<bool>? RequestCanUndo { get; set; }
    public Func<bool>? RequestCanRedo { get; set; }
    public Action? RequestEditorUndo { get; set; }
    public Action? RequestEditorRedo { get; set; }
    public event Action<(int Offset, int Length)>? RequestSelectRange;
    public event Action? RequestRefreshSearchHighlights;
    public event Action? RequestMarkdownPreviewRefresh;

    public MarkdownTextWorkspaceViewModel(string filePath)
        : base(filePath, DocumentFormatKind.MarkdownText)
    {
        _isMarkdownFile = string.Equals(Path.GetExtension(filePath), ".md", StringComparison.OrdinalIgnoreCase);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        string text = await MarkdownTextService.LoadAsync(OriginalFilePath, ct);
        SetInitialText(text);
    }

    public void SetInitialText(string text)
    {
        _suppressHistory = true;
        try
        {
            _committedBaseline = text ?? string.Empty;
            TextContent = _committedBaseline;
            _undoStack.Clear();
            _redoStack.Clear();
            IsDirty = false;
            UpdateLineStats();
        }
        finally
        {
            _suppressHistory = false;
        }
    }

    partial void OnTextContentChanged(string value)
    {
        if (!_suppressHistory)
        {
            IsDirty = !string.Equals(value, _committedBaseline, StringComparison.Ordinal);
            UpdateLineStats();
            if (IsMarkdownFile && ShowPreviewPane)
            {
                RequestMarkdownPreviewRefresh?.Invoke();
            }
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void UpdateTextFromEditor(string text)
    {
        if (string.Equals(TextContent, text, StringComparison.Ordinal)) return;
        if (!_suppressHistory && RequestEditorUndo is null)
        {
            _undoStack.Push(TextContent);
            _redoStack.Clear();
        }
        TextContent = text;
    }

    partial void OnViewModeChanged(MarkdownViewMode value)
    {
        OnPropertyChanged(nameof(IsEditorMode));
        OnPropertyChanged(nameof(IsSplitMode));
        OnPropertyChanged(nameof(IsReaderMode));
        OnPropertyChanged(nameof(ShowEditorPane));
        OnPropertyChanged(nameof(ShowPreviewPane));
        if (ShowPreviewPane)
        {
            RequestMarkdownPreviewRefresh?.Invoke();
        }
    }

    partial void OnCaretLineChanged(int value) => UpdateContextStatus();
    partial void OnCaretColumnChanged(int value) => UpdateContextStatus();

    [RelayCommand]
    private void SetEditorMode() => ViewMode = MarkdownViewMode.Editor;

    [RelayCommand]
    private void SetSplitPreviewMode() => ViewMode = MarkdownViewMode.SplitPreview;

    [RelayCommand]
    private void SetCleanReaderMode() => ViewMode = MarkdownViewMode.CleanReader;

    [RelayCommand]
    private void ToggleWordWrap() => WordWrap = !WordWrap;

    public void RunSearch(string query)
    {
        FindQuery = query ?? string.Empty;
        _matches.Clear();
        _activeMatchIndex = -1;

        if (!string.IsNullOrEmpty(query) && !string.IsNullOrEmpty(TextContent))
        {
            int idx = 0;
            while ((idx = TextContent.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                _matches.Add((idx, query.Length));
                idx += Math.Max(1, query.Length);
            }
        }

        if (_matches.Count > 0)
        {
            _activeMatchIndex = 0;
            RequestSelectRange?.Invoke(_matches[0]);
            SearchStatusLabel = $"1 of {_matches.Count}";
        }
        else
        {
            SearchStatusLabel = "0 of 0";
        }
        RequestRefreshSearchHighlights?.Invoke();
    }

    public void ClearSearch()
    {
        _matches.Clear();
        _activeMatchIndex = -1;
        SearchStatusLabel = "0 of 0";
        RequestRefreshSearchHighlights?.Invoke();
    }

    public void NextSearchMatch()
    {
        if (_matches.Count == 0) return;
        _activeMatchIndex = (_activeMatchIndex + 1) % _matches.Count;
        RequestSelectRange?.Invoke(_matches[_activeMatchIndex]);
        SearchStatusLabel = $"{_activeMatchIndex + 1} of {_matches.Count}";
        RequestRefreshSearchHighlights?.Invoke();
    }

    public void PreviousSearchMatch()
    {
        if (_matches.Count == 0) return;
        _activeMatchIndex = (_activeMatchIndex - 1 + _matches.Count) % _matches.Count;
        RequestSelectRange?.Invoke(_matches[_activeMatchIndex]);
        SearchStatusLabel = $"{_activeMatchIndex + 1} of {_matches.Count}";
        RequestRefreshSearchHighlights?.Invoke();
    }

    public void ReplaceCurrentMatch(string query)
    {
        if (string.IsNullOrEmpty(query) || _matches.Count == 0) return;
        if (_activeMatchIndex < 0 || _activeMatchIndex >= _matches.Count) _activeMatchIndex = 0;
        var (offset, length) = _matches[_activeMatchIndex];
        if (offset < 0 || offset + length > TextContent.Length) return;

        string replacement = ReplaceText ?? string.Empty;
        string updated = TextContent[..offset] + replacement + TextContent[(offset + length)..];
        UpdateTextFromEditor(updated);
        RunSearch(query);
    }

    public int ReplaceAllMatches(string query)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(TextContent)) return 0;
        string replacement = ReplaceText ?? string.Empty;
        int count = 0;
        var sb = new System.Text.StringBuilder();
        int idx = 0;
        int hit;
        while ((hit = TextContent.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            sb.Append(TextContent, idx, hit - idx);
            sb.Append(replacement);
            idx = hit + query.Length;
            count++;
        }
        if (count == 0) return 0;
        sb.Append(TextContent, idx, TextContent.Length - idx);
        UpdateTextFromEditor(sb.ToString());
        RunSearch(query);
        return count;
    }

    public void Undo()
    {
        if (RequestEditorUndo is not null && (RequestCanUndo?.Invoke() ?? false))
        {
            RequestEditorUndo.Invoke();
            return;
        }

        if (_undoStack.Count == 0) return;
        _suppressHistory = true;
        try
        {
            _redoStack.Push(TextContent);
            TextContent = _undoStack.Pop();
            IsDirty = !string.Equals(TextContent, _committedBaseline, StringComparison.Ordinal);
            UpdateLineStats();
        }
        finally
        {
            _suppressHistory = false;
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void Redo()
    {
        if (RequestEditorRedo is not null && (RequestCanRedo?.Invoke() ?? false))
        {
            RequestEditorRedo.Invoke();
            return;
        }

        if (_redoStack.Count == 0) return;
        _suppressHistory = true;
        try
        {
            _undoStack.Push(TextContent);
            TextContent = _redoStack.Pop();
            IsDirty = !string.Equals(TextContent, _committedBaseline, StringComparison.Ordinal);
            UpdateLineStats();
        }
        finally
        {
            _suppressHistory = false;
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public async Task SaveAsync(string targetPath, CancellationToken ct = default)
    {
        await MarkdownTextService.SaveAtomicAsync(targetPath, TextContent, ct);
        _committedBaseline = TextContent;
        OriginalFilePath = targetPath;
        WorkingFilePath = targetPath;
        IsMarkdownFile = string.Equals(Path.GetExtension(targetPath), ".md", StringComparison.OrdinalIgnoreCase);
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        RefreshFileMetadata();
        UpdateLineStats();
    }

    public async Task ExportToPdfAsync(string targetPdfPath, CancellationToken ct = default)
    {
        bool mono = SyntaxExtension is ".json" or ".xml" or ".log";
        await PdfFormatExporter.ExportTextAsync(TextContent, targetPdfPath, mono, ct);
    }

    private void UpdateLineStats()
    {
        int lines = 1;
        foreach (char ch in TextContent)
        {
            if (ch == '\n') lines++;
        }
        LineCount = lines;
        MicroMetadataBadge = $"{lines}L";
        UpdateContextStatus();
    }

    private void UpdateContextStatus()
    {
        ContextStatusText = $"UTF-8 • {FormatDisplayName} • {LineCount} lines • Ln {CaretLine}, Col {CaretColumn}";
    }
}
