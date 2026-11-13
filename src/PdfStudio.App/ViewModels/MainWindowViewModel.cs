using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.App.Themes;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Formats;
using PdfStudio.Core.Forms;
using PdfStudio.Core.Rendering;
using PdfStudio.Editing.PdfSharp;
using SkiaSharp;

namespace PdfStudio.App.ViewModels;

public sealed record CommandPaletteItem(string Id, string Title, string Category, string Shortcut, Action Execute);

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IPdfRenderEngine _renderEngine;
    private readonly IPdfEditorService _editorService;
    private readonly string? _settingsFilePath;
    private readonly List<PdfTextMatch> _searchMatches = [];
    private readonly List<string> _tempWorkingFiles = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _thumbnailCts;
    private bool _suppressPageScrollEvent;
    private bool _suppressZoomSync;
    private bool _suppressThemeSync;
    private ThemePreset _lastDarkTheme = ThemeManager.Obsidian;
    private ThemePreset _lastLightTheme = ThemeManager.StudioLight;

    private static readonly int[] ZoomStops = [25, 50, 75, 100, 125, 150, 200, 300, 400];

    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private ThemePreset _selectedTheme = ThemeManager.Obsidian;

    [ObservableProperty]
    private bool _isThemePickerOpen;

    [ObservableProperty]
    private bool _isLeftPanelOpen = true;

    [ObservableProperty]
    private bool _isRightPanelOpen = true;

    [ObservableProperty]
    private bool _isPagesTabActive = true;

    [ObservableProperty]
    private PdfToolKind _activeTool = PdfToolKind.Select;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchBarOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _activeSearchMatchIndex = -1;

    [ObservableProperty]
    private int _totalSearchMatches;

    [ObservableProperty]
    private string _documentTitle = "No file open";

    [ObservableProperty]
    private string? _documentPath;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _zoomPercent = 100;

    [ObservableProperty]
    private double _cursorXPt;

    [ObservableProperty]
    private double _cursorYPt;

    [ObservableProperty]
    private string _activePageSizeLabel = "612 × 792";

    [ObservableProperty]
    private bool _hasOpenDocument;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DocumentTabViewModel? _activeTab;

    [ObservableProperty]
    private PdfAnnotation? _selectedAnnotation;

    [ObservableProperty]
    private string _selectedAnnotationText = string.Empty;

    [ObservableProperty]
    private bool _flattenFormsOnSave;

    [ObservableProperty]
    private bool _isSignatureModalOpen;

    [ObservableProperty]
    private bool _isPasswordModalOpen;

    [ObservableProperty]
    private bool _protectWithPassword;

    [ObservableProperty]
    private string _documentPassword = string.Empty;

    [ObservableProperty]
    private bool _isOpenPasswordPromptOpen;

    [ObservableProperty]
    private string _openPasswordInput = string.Empty;

    [ObservableProperty]
    private string _openPasswordError = string.Empty;

    private string? _pendingLockedFilePath;
    private CancellationTokenSource? _toastCts;

    public bool PromptOnDirtyTabClose { get; set; }

    [ObservableProperty]
    private double _leftSidebarWidth = 220.0;

    [ObservableProperty]
    private double _windowWidth = 1360.0;

    [ObservableProperty]
    private double _windowHeight = 860.0;

    [ObservableProperty]
    private bool _isWindowMaximized;

    [ObservableProperty]
    private string _activeToolColorHex = "#FACC15";

    [ObservableProperty]
    private double _activeStrokeWidthPt = 2.0;

    [ObservableProperty]
    private double _activeFontSizePt = 12.0;

    [ObservableProperty]
    private bool _isPageJumpEditing;

    [ObservableProperty]
    private string _pageJumpInput = "1";

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private bool _isUnsavedChangesModalOpen;

    [ObservableProperty]
    private DocumentTabViewModel? _pendingCloseTab;

    [ObservableProperty]
    private bool _isErrorModalOpen;

    [ObservableProperty]
    private string _errorModalMessage = string.Empty;

    public int ContextPageIndex { get; set; }
    public PdfPoint ContextPagePointPt { get; set; }

    public LruBitmapCache BitmapCache { get; } = new(512L * 1024 * 1024);
    public IPdfRenderEngine RenderEngine => _renderEngine;

    public ObservableCollection<PdfPageInfo> Pages { get; } = [];
    public ObservableCollection<PageThumbnailViewModel> PageThumbnails { get; } = [];
    public ObservableCollection<PdfOutlineNode> OutlineNodes { get; } = [];
    public ObservableCollection<DocumentTabViewModel> OpenTabs { get; } = [];
    public ObservableCollection<CommandPaletteItem> FilteredCommands { get; } = [];
    public ObservableCollection<PdfFormField> FormFields { get; } = [];
    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; } = [];
    public ObservableCollection<ThemeOptionViewModel> LightThemeOptions { get; } = [];
    public ObservableCollection<ThemeOptionViewModel> DarkThemeOptions { get; } = [];
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = [];

    public IBrush SelectedThemeAccentBrush => new SolidColorBrush(Color.Parse(SelectedTheme.AccentHex));
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public string PendingCloseTabTitle => PendingCloseTab?.Title ?? DocumentTitle;

    public IReadOnlyList<PdfTextMatch> SearchMatches => _searchMatches;
    public IReadOnlyList<PdfAnnotation> Annotations => ActivePdfTab?.EditPlan.Annotations ?? (IReadOnlyList<PdfAnnotation>)[];
    public PdfTextMatch? ActiveSearchMatch =>
        ActiveSearchMatchIndex >= 0 && ActiveSearchMatchIndex < _searchMatches.Count
            ? _searchMatches[ActiveSearchMatchIndex]
            : null;

    private readonly List<CommandPaletteItem> _allCommands = [];

    public bool IsOutlineTabActive => !IsPagesTabActive;
    public bool IsSelectTool => ActiveTool == PdfToolKind.Select;
    public bool IsHandTool => ActiveTool == PdfToolKind.Hand;
    public bool IsHighlightTool => ActiveTool == PdfToolKind.Highlight;
    public bool IsUnderlineTool => ActiveTool == PdfToolKind.Underline;
    public bool IsDrawTool => ActiveTool == PdfToolKind.Draw;
    public bool IsTextTool => ActiveTool == PdfToolKind.Text;
    public bool IsNoteTool => ActiveTool == PdfToolKind.Note;
    public bool IsRedactTool => ActiveTool == PdfToolKind.Redact;
    public bool IsEditTextTool => ActiveTool == PdfToolKind.EditText;
    public bool IsAnnotationToolActive => ActiveTool is PdfToolKind.Highlight or PdfToolKind.Underline or PdfToolKind.Draw or PdfToolKind.Text or PdfToolKind.Note;
    public bool IsStrokeToolActive => ActiveTool is PdfToolKind.Highlight or PdfToolKind.Underline or PdfToolKind.Draw;
    public bool IsTextOrNoteToolActive => ActiveTool is PdfToolKind.Text or PdfToolKind.Note;
    public bool IsToolColorYellow => string.Equals(ActiveToolColorHex, "#FACC15", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorBlue => string.Equals(ActiveToolColorHex, "#3B82F6", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorRed => string.Equals(ActiveToolColorHex, "#EF4444", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorGreen => string.Equals(ActiveToolColorHex, "#10B981", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorPurple => string.Equals(ActiveToolColorHex, "#A855F7", StringComparison.OrdinalIgnoreCase);
    public bool IsStroke1Pt => Math.Abs(ActiveStrokeWidthPt - 1.0) < 0.1;
    public bool IsStroke2Pt => Math.Abs(ActiveStrokeWidthPt - 2.0) < 0.1;
    public bool IsStroke4Pt => Math.Abs(ActiveStrokeWidthPt - 4.0) < 0.1;
    public bool IsFontSize10 => Math.Abs(ActiveFontSizePt - 10.0) < 0.1;
    public bool IsFontSize12 => Math.Abs(ActiveFontSizePt - 12.0) < 0.1;
    public bool IsFontSize16 => Math.Abs(ActiveFontSizePt - 16.0) < 0.1;
    public bool IsFontSize20 => Math.Abs(ActiveFontSizePt - 20.0) < 0.1;
    public bool HasSelectedAnnotation => SelectedAnnotation is not null;
    public bool HasSelectedEditableText => SelectedAnnotation is { Kind: AnnotationKind.TextBox or AnnotationKind.StickyNote };
    public int PendingRedactionCount => ActivePdfTab?.EditPlan.Annotations.Count(a => a.Kind == AnnotationKind.Redaction) ?? 0;
    public bool HasPendingRedactions => PendingRedactionCount > 0;
    public bool HasFormFields => FormFields.Count > 0;

    public string PageCounterText => $"Page {CurrentPage} of {TotalPages}";
    public string ZoomText => $"{ZoomPercent}%";
    public string CoordinateText => $"{CursorXPt,5:0.0} × {CursorYPt,5:0.0}";
    public string SearchCounterText => TotalSearchMatches > 0
        ? $"{ActiveSearchMatchIndex + 1} of {TotalSearchMatches}"
        : "0 of 0";

    public bool CanUndo => ActiveTab?.CanUndo ?? false;
    public bool CanRedo => ActiveTab?.CanRedo ?? false;

    public event Func<Task<string?>>? RequestOpenFilePath;
    public event Func<Task<IReadOnlyList<string>?>>? RequestOpenMultipleFilePaths;
    public event Func<string, Task<string?>>? RequestSaveFilePath;
    public event Func<Task<byte[]?>>? RequestImportPngBytes;
    public event Action? RequestClearSignaturePad;
    public event Action<int>? RequestScrollToPage;
    public event Action<PdfTextMatch>? RequestScrollToMatch;
    public event Action<double>? RequestScrollViewportByFraction;
    public event Action? RequestFitToWidth;
    public event Action? RequestFitToPage;
    public event Action? RequestInvalidateCanvas;
    public event Action<int>? RequestTargetZoom;
    public event Action? RequestFocusSearchBox;
    public event Action? RequestFocusPageJumpBox;
    public event Func<string, Task>? RequestCopyToClipboard;
    public event Action? RequestTabCrossfade;

    public MainWindowViewModel(
        IPdfRenderEngine renderEngine,
        IPdfEditorService? editorService = null,
        string? settingsFilePath = null)
    {
        _renderEngine = renderEngine;
        _editorService = editorService ?? new PdfSharpEditorService();
        _settingsFilePath = settingsFilePath;

        foreach (var preset in ThemeManager.AllPresets)
        {
            var option = new ThemeOptionViewModel(preset);
            ThemeOptions.Add(option);
            if (preset.IsDark)
            {
                DarkThemeOptions.Add(option);
            }
            else
            {
                LightThemeOptions.Add(option);
            }
        }

        BuildCommandPalette();
        RefreshFilteredCommands();

        var loadedSettings = ThemeManager.LoadAppSettings(_settingsFilePath);
        _windowWidth = loadedSettings.WindowWidth;
        _windowHeight = loadedSettings.WindowHeight;
        _isWindowMaximized = loadedSettings.IsMaximized;
        _leftSidebarWidth = loadedSettings.LeftSidebarWidth;
        _isLeftPanelOpen = loadedSettings.IsLeftPanelOpen;
        _isRightPanelOpen = loadedSettings.IsRightPanelOpen;

        foreach (var recent in loadedSettings.RecentFiles)
        {
            RecentFiles.Add(recent);
        }
        OnPropertyChanged(nameof(HasRecentFiles));

        var initialTheme = ThemeManager.GetByIdOrName(loadedSettings.ThemeId);
        ApplyThemeInternal(initialTheme, persist: false);
    }

    partial void OnIsPagesTabActiveChanged(bool value) => OnPropertyChanged(nameof(IsOutlineTabActive));

    partial void OnActiveToolChanged(PdfToolKind value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsHandTool));
        OnPropertyChanged(nameof(IsHighlightTool));
        OnPropertyChanged(nameof(IsUnderlineTool));
        OnPropertyChanged(nameof(IsDrawTool));
        OnPropertyChanged(nameof(IsTextTool));
        OnPropertyChanged(nameof(IsNoteTool));
        OnPropertyChanged(nameof(IsRedactTool));
        OnPropertyChanged(nameof(IsEditTextTool));
        OnPropertyChanged(nameof(IsAnnotationToolActive));
        OnPropertyChanged(nameof(IsStrokeToolActive));
        OnPropertyChanged(nameof(IsTextOrNoteToolActive));
        if (value != PdfToolKind.Select)
        {
            ClearPdfTextSelection();
        }
        if (value != PdfToolKind.EditText)
        {
            CancelInlinePdfTextEdit();
        }
        RequestInvalidateCanvas?.Invoke();
    }

    partial void OnActiveToolColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(IsToolColorYellow));
        OnPropertyChanged(nameof(IsToolColorBlue));
        OnPropertyChanged(nameof(IsToolColorRed));
        OnPropertyChanged(nameof(IsToolColorGreen));
        OnPropertyChanged(nameof(IsToolColorPurple));
    }

    partial void OnActiveStrokeWidthPtChanged(double value)
    {
        OnPropertyChanged(nameof(IsStroke1Pt));
        OnPropertyChanged(nameof(IsStroke2Pt));
        OnPropertyChanged(nameof(IsStroke4Pt));
    }

    partial void OnActiveFontSizePtChanged(double value)
    {
        OnPropertyChanged(nameof(IsFontSize10));
        OnPropertyChanged(nameof(IsFontSize12));
        OnPropertyChanged(nameof(IsFontSize16));
        OnPropertyChanged(nameof(IsFontSize20));
    }

    partial void OnPendingCloseTabChanged(DocumentTabViewModel? value)
        => OnPropertyChanged(nameof(PendingCloseTabTitle));

    partial void OnSelectedAnnotationChanged(PdfAnnotation? value)
    {
        SelectedAnnotationText = value?.TextContent ?? string.Empty;
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedEditableText));
        RequestInvalidateCanvas?.Invoke();
    }

    partial void OnSelectedAnnotationTextChanged(string value)
    {
        if (SelectedAnnotation is not null &&
            SelectedAnnotation.Kind is AnnotationKind.TextBox or AnnotationKind.StickyNote &&
            !string.Equals(SelectedAnnotation.TextContent, value, StringComparison.Ordinal))
        {
            SelectedAnnotation.TextContent = value;
            HasUnsavedChanges = true;
            if (ActivePdfTab is not null)
            {
                ActivePdfTab.EditPlan.HasChanges = true;
            }
            RequestInvalidateCanvas?.Invoke();
        }
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageCounterText));
        UpdateActivePageMetadata();
        UpdateThumbnailSelection();
        if (ActivePdfTab is not null)
        {
            ActivePdfTab.CurrentPage = value;
            ActivePdfTab.UpdateMetadataBadges();
        }
        if (!_suppressPageScrollEvent && HasOpenDocument && IsPdfWorkspaceActive)
        {
            RequestScrollToPage?.Invoke(Math.Clamp(value - 1, 0, Math.Max(0, Pages.Count - 1)));
        }
    }

    partial void OnTotalPagesChanged(int value) => OnPropertyChanged(nameof(PageCounterText));

    partial void OnZoomPercentChanged(int value)
    {
        OnPropertyChanged(nameof(ZoomText));
        if (ActivePdfTab is not null)
        {
            ActivePdfTab.ZoomPercent = value;
            ActivePdfTab.UpdateMetadataBadges();
        }
        if (!_suppressZoomSync)
        {
            if (ActiveImageTab is not null)
            {
                ActiveImageTab.ZoomPercent = value;
            }
            else
            {
                RequestTargetZoom?.Invoke(value);
            }
        }
    }

    partial void OnCursorXPtChanged(double value) => OnPropertyChanged(nameof(CoordinateText));
    partial void OnCursorYPtChanged(double value) => OnPropertyChanged(nameof(CoordinateText));
    partial void OnCommandSearchQueryChanged(string value) => RefreshFilteredCommands();

    partial void OnActiveSearchMatchIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SearchCounterText));
        OnPropertyChanged(nameof(ActiveSearchMatch));
    }

    partial void OnTotalSearchMatchesChanged(int value) => OnPropertyChanged(nameof(SearchCounterText));

    partial void OnSearchQueryChanged(string value)
    {
        _ = RunSearchAsync(value);
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        if (_suppressThemeSync) return;
        ApplyThemeInternal(value ? _lastDarkTheme : _lastLightTheme, persist: true);
    }

    partial void OnSelectedThemeChanged(ThemePreset value)
    {
        if (_suppressThemeSync) return;
        ApplyThemeInternal(value, persist: true);
    }

    private void ApplyThemeInternal(ThemePreset preset, bool persist)
    {
        _suppressThemeSync = true;
        try
        {
            if (preset.IsDark)
            {
                _lastDarkTheme = preset;
            }
            else
            {
                _lastLightTheme = preset;
            }

            SelectedTheme = preset;
            IsDarkMode = preset.IsDark;

            foreach (var option in ThemeOptions)
            {
                option.IsSelected = string.Equals(option.Id, preset.Id, StringComparison.OrdinalIgnoreCase);
            }

            ThemeManager.ApplyTheme(preset);
            OnPropertyChanged(nameof(SelectedThemeAccentBrush));

            if (persist)
            {
                SaveSettingsSnapshot();
            }

            RequestInvalidateCanvas?.Invoke();
        }
        finally
        {
            _suppressThemeSync = false;
        }
    }

    public void SaveSettingsSnapshot()
    {
        var settings = new UserAppSettings
        {
            ThemeId = SelectedTheme.Id,
            WindowWidth = WindowWidth >= 640 ? WindowWidth : 1360,
            WindowHeight = WindowHeight >= 480 ? WindowHeight : 860,
            IsMaximized = IsWindowMaximized,
            LeftSidebarWidth = LeftSidebarWidth >= 160 ? LeftSidebarWidth : 220,
            IsLeftPanelOpen = IsLeftPanelOpen,
            IsRightPanelOpen = IsRightPanelOpen,
            RecentFiles = RecentFiles.ToList()
        };
        ThemeManager.SaveAppSettings(settings, _settingsFilePath);
    }

    private void RecordRecentFile(string fullPath, int pageCount)
    {
        string fileName = Path.GetFileName(fullPath);
        string parent = Path.GetDirectoryName(fullPath) ?? string.Empty;
        string dateLabel = DateTime.Now.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

        for (int i = RecentFiles.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentFiles[i].FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                RecentFiles.RemoveAt(i);
            }
        }

        RecentFiles.Insert(0, new RecentFileEntry(fullPath, fileName, parent, dateLabel, pageCount));
        while (RecentFiles.Count > 8)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }

        OnPropertyChanged(nameof(HasRecentFiles));
        SaveSettingsSnapshot();
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _toastCts?.Cancel();
        _toastCts?.Dispose();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastMessage = message;
        IsToastVisible = true;

        _ = AutoDismissToastAsync(cts.Token);
    }

    private async Task AutoDismissToastAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(2000, ct);
            if (!ct.IsCancellationRequested)
            {
                IsToastVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer toast
        }
    }

    public void ShowErrorDialog(string message)
    {
        ErrorModalMessage = message;
        IsErrorModalOpen = true;
    }

    [RelayCommand]
    private void CloseErrorModal() => IsErrorModalOpen = false;

    [RelayCommand]
    private async Task OpenRecentFileAsync(RecentFileEntry? entry)
    {
        if (entry is null) return;
        if (!File.Exists(entry.FilePath))
        {
            RemoveRecentFile(entry);
            ShowErrorDialog($"File no longer exists:\n{entry.FilePath}");
            return;
        }
        await LoadFileAsync(entry.FilePath);
    }

    [RelayCommand]
    private void RemoveRecentFile(RecentFileEntry? entry)
    {
        if (entry is null) return;
        RecentFiles.Remove(entry);
        OnPropertyChanged(nameof(HasRecentFiles));
        SaveSettingsSnapshot();
    }

    [RelayCommand]
    private void SetToolColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        ActiveToolColorHex = hex;
        if (SelectedAnnotation is not null)
        {
            BeginAnnotationTransformSnapshot();
            SelectedAnnotation.ColorHex = hex;
            CommitAnnotationTransform();
        }
    }

    [RelayCommand]
    private void SetStrokeWidth(object? widthArg)
    {
        if (!TryParseDoubleArg(widthArg, out double width)) return;
        ActiveStrokeWidthPt = Math.Clamp(width, 0.5, 16.0);
        if (SelectedAnnotation is not null)
        {
            BeginAnnotationTransformSnapshot();
            SelectedAnnotation.StrokeWidthPt = ActiveStrokeWidthPt;
            CommitAnnotationTransform();
        }
    }

    [RelayCommand]
    private void SetFontSize(object? sizeArg)
    {
        if (!TryParseDoubleArg(sizeArg, out double size)) return;
        ActiveFontSizePt = Math.Clamp(size, 6.0, 48.0);
        if (SelectedAnnotation is not null && SelectedAnnotation.Kind is AnnotationKind.TextBox or AnnotationKind.StickyNote)
        {
            BeginAnnotationTransformSnapshot();
            SelectedAnnotation.FontSizePt = ActiveFontSizePt;
            CommitAnnotationTransform();
        }
    }

    private static bool TryParseDoubleArg(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    [RelayCommand]
    private void BeginPageJump()
    {
        if (!HasOpenDocument) return;
        PageJumpInput = CurrentPage.ToString(CultureInfo.InvariantCulture);
        IsPageJumpEditing = true;
        RequestFocusPageJumpBox?.Invoke();
    }

    [RelayCommand]
    private void CommitPageJump()
    {
        IsPageJumpEditing = false;
        if (!HasOpenDocument) return;
        if (int.TryParse(PageJumpInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetPage))
        {
            int clamped = Math.Clamp(targetPage, 1, TotalPages);
            CurrentPage = clamped;
            RequestScrollToPage?.Invoke(clamped - 1);
        }
    }

    [RelayCommand]
    private void CancelPageJump() => IsPageJumpEditing = false;

    partial void OnHasUnsavedChangesChanged(bool value)
    {
        if (ActiveTab is not null)
        {
            ActiveTab.IsDirty = value;
        }
    }

    public void SetCurrentPageFromScroll(int oneBasedPage)
    {
        int clamped = Math.Clamp(oneBasedPage, 1, Math.Max(1, TotalPages));
        if (clamped == CurrentPage) return;

        _suppressPageScrollEvent = true;
        try
        {
            CurrentPage = clamped;
        }
        finally
        {
            _suppressPageScrollEvent = false;
        }
    }

    public void SetZoomPercentFromViewport(int zoomPercent)
    {
        int clamped = Math.Clamp(zoomPercent, 25, 400);
        if (clamped == ZoomPercent) return;

        _suppressZoomSync = true;
        try
        {
            ZoomPercent = clamped;
        }
        finally
        {
            _suppressZoomSync = false;
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        ApplyThemeInternal(IsDarkMode ? _lastLightTheme : _lastDarkTheme, persist: true);
    }

    [RelayCommand]
    private void ToggleThemePicker() => IsThemePickerOpen = !IsThemePickerOpen;

    [RelayCommand]
    private void CloseThemePicker() => IsThemePickerOpen = false;

    [RelayCommand]
    private void SelectTheme(object? parameter)
    {
        var preset = parameter switch
        {
            ThemePreset tp => tp,
            ThemeOptionViewModel opt => opt.Preset,
            string s => ThemeManager.GetByIdOrName(s),
            _ => null
        };

        if (preset is null) return;
        ApplyThemeInternal(preset, persist: true);
        IsThemePickerOpen = false;
    }

    [RelayCommand]
    private void ToggleLeftPanel() => IsLeftPanelOpen = !IsLeftPanelOpen;

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    [RelayCommand]
    private void SelectPagesTab() => IsPagesTabActive = true;

    [RelayCommand]
    private void SelectOutlineTab() => IsPagesTabActive = false;

    [RelayCommand]
    private void SetSelectTool() => ActiveTool = PdfToolKind.Select;

    [RelayCommand]
    private void SetHandTool() => ActiveTool = PdfToolKind.Hand;

    [RelayCommand]
    private void SetHighlightTool() => ActiveTool = PdfToolKind.Highlight;

    [RelayCommand]
    private void SetUnderlineTool() => ActiveTool = PdfToolKind.Underline;

    [RelayCommand]
    private void SetDrawTool() => ActiveTool = PdfToolKind.Draw;

    [RelayCommand]
    private void SetTextTool() => ActiveTool = PdfToolKind.Text;

    [RelayCommand]
    private void SetNoteTool() => ActiveTool = PdfToolKind.Note;

    [RelayCommand]
    private void SetRedactTool() => ActiveTool = PdfToolKind.Redact;

    [RelayCommand]
    private void SetEditTextTool() => ActiveTool = PdfToolKind.EditText;

    [RelayCommand]
    private void ZoomIn()
    {
        int next = ZoomStops.FirstOrDefault(z => z > ZoomPercent);
        ZoomPercent = next > 0 ? next : ZoomStops[^1];
    }

    [RelayCommand]
    private void ZoomOut()
    {
        int prev = ZoomStops.LastOrDefault(z => z < ZoomPercent);
        ZoomPercent = prev > 0 ? prev : ZoomStops[0];
    }

    [RelayCommand]
    private void ResetZoom() => ZoomPercent = 100;

    [RelayCommand]
    private void FitToWidth() => RequestFitToWidth?.Invoke();

    [RelayCommand]
    private void FitToPage() => RequestFitToPage?.Invoke();

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1) CurrentPage--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages) CurrentPage++;
    }

    [RelayCommand]
    private void PageDownViewport()
    {
        if (RequestScrollViewportByFraction is not null)
        {
            RequestScrollViewportByFraction.Invoke(0.85);
        }
        else
        {
            NextPage();
        }
    }

    [RelayCommand]
    private void PageUpViewport()
    {
        if (RequestScrollViewportByFraction is not null)
        {
            RequestScrollViewportByFraction.Invoke(-0.85);
        }
        else
        {
            PreviousPage();
        }
    }

    [RelayCommand]
    private void FirstPage()
    {
        if (HasOpenDocument) CurrentPage = 1;
    }

    [RelayCommand]
    private void LastPage()
    {
        if (HasOpenDocument) CurrentPage = TotalPages;
    }

    [RelayCommand]
    private void HandleEscape()
    {
        if (IsAllTabsDropdownOpen)
        {
            IsAllTabsDropdownOpen = false;
            return;
        }
        if (IsNewTabMenuOpen)
        {
            IsNewTabMenuOpen = false;
            return;
        }
        if (IsInlinePdfTextEditOpen)
        {
            CancelInlinePdfTextEdit();
            return;
        }
        if (IsPdfSelectionPillVisible)
        {
            ClearPdfTextSelection();
            return;
        }
        if (IsPageJumpEditing)
        {
            IsPageJumpEditing = false;
            return;
        }
        if (IsErrorModalOpen)
        {
            IsErrorModalOpen = false;
            return;
        }
        if (IsUnsavedChangesModalOpen)
        {
            CancelCloseTab();
            return;
        }
        if (IsThemePickerOpen)
        {
            IsThemePickerOpen = false;
            return;
        }
        if (IsOpenPasswordPromptOpen)
        {
            CancelOpenPassword();
            return;
        }
        if (IsPasswordModalOpen)
        {
            IsPasswordModalOpen = false;
            return;
        }
        if (IsSignatureModalOpen)
        {
            IsSignatureModalOpen = false;
            return;
        }
        if (IsCommandPaletteOpen)
        {
            CloseCommandPalette();
            return;
        }
        if (IsSearchBarOpen)
        {
            CloseSearchBar();
            return;
        }
        SelectedAnnotation = null;
        ActiveTool = PdfToolKind.Select;
    }

    [RelayCommand]
    private void SelectPage(PdfPageInfo? page)
    {
        if (page is null) return;
        int target = page.PageIndex + 1;
        if (CurrentPage != target)
        {
            CurrentPage = target;
        }
        else
        {
            RequestScrollToPage?.Invoke(page.PageIndex);
        }
    }

    [RelayCommand]
    private void SelectThumbnail(PageThumbnailViewModel? thumb)
    {
        if (thumb is null) return;
        SelectPage(thumb.PageInfo);
    }

    [RelayCommand]
    private void SelectOutlineNode(PdfOutlineNode? node)
    {
        if (node?.TargetPageIndex is int pageIndex && pageIndex >= 0 && pageIndex < Pages.Count)
        {
            int target = pageIndex + 1;
            if (CurrentPage != target)
            {
                CurrentPage = target;
            }
            else
            {
                RequestScrollToPage?.Invoke(pageIndex);
            }
        }
    }

    [RelayCommand]
    private void ToggleSearchBar()
    {
        IsSearchBarOpen = !IsSearchBarOpen;
        if (IsSearchBarOpen)
        {
            RequestFocusSearchBox?.Invoke();
        }
        else
        {
            ClearSearchState();
        }
    }

    [RelayCommand]
    private void OpenSearchBar()
    {
        IsSearchBarOpen = true;
        RequestFocusSearchBox?.Invoke();
    }

    [RelayCommand]
    private void CloseSearchBar()
    {
        IsSearchBarOpen = false;
        ClearSearchState();
    }

    [RelayCommand]
    private void NextSearchMatch()
    {
        if (ActiveSpreadsheetTab is { } sheet)
        {
            sheet.NextSearchMatch();
            UpdateNonPdfSearchStatus();
            return;
        }
        if (ActiveRichDocTab is { } doc)
        {
            doc.NextSearchMatch();
            UpdateNonPdfSearchStatus();
            return;
        }
        if (ActiveMarkdownTab is { } md)
        {
            md.NextSearchMatch();
            UpdateNonPdfSearchStatus();
            return;
        }

        if (_searchMatches.Count == 0) return;
        ActiveSearchMatchIndex = (ActiveSearchMatchIndex + 1) % _searchMatches.Count;
        FocusActiveSearchMatch();
    }

    [RelayCommand]
    private void PreviousSearchMatch()
    {
        if (ActiveSpreadsheetTab is { } sheet)
        {
            sheet.PreviousSearchMatch();
            UpdateNonPdfSearchStatus();
            return;
        }
        if (ActiveRichDocTab is { } doc)
        {
            doc.PreviousSearchMatch();
            UpdateNonPdfSearchStatus();
            return;
        }
        if (ActiveMarkdownTab is { } md)
        {
            md.PreviousSearchMatch();
            UpdateNonPdfSearchStatus();
            return;
        }

        if (_searchMatches.Count == 0) return;
        ActiveSearchMatchIndex = (ActiveSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        FocusActiveSearchMatch();
    }

    public async Task RunSearchAsync(string query)
    {
        if (ActiveSpreadsheetTab is { } sheet)
        {
            sheet.RunSearch(query);
            UpdateNonPdfSearchStatus();
            return;
        }
        if (ActiveRichDocTab is { } doc)
        {
            doc.RunSearch(query);
            UpdateNonPdfSearchStatus();
            return;
        }
        if (ActiveMarkdownTab is { } md)
        {
            md.RunSearch(query);
            UpdateNonPdfSearchStatus();
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _searchMatches.Clear();
        ActiveSearchMatchIndex = -1;
        TotalSearchMatches = 0;

        if (!HasOpenDocument || !IsPdfWorkspaceActive || string.IsNullOrWhiteSpace(query))
        {
            RequestInvalidateCanvas?.Invoke();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        try
        {
            var found = new List<PdfTextMatch>();
            for (int i = 0; i < Pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = Pages[i];
                if (page.IsBlankPage) continue;
                var pageMatches = await _renderEngine.FindTextAsync(page.EffectiveSourcePageIndex, query, matchCase: false, ct);
                foreach (var match in pageMatches)
                {
                    found.Add(match with { PageIndex = page.PageIndex });
                }
            }

            if (ct.IsCancellationRequested) return;

            _searchMatches.AddRange(found);
            TotalSearchMatches = _searchMatches.Count;
            ActiveSearchMatchIndex = _searchMatches.Count > 0 ? 0 : -1;

            if (ActiveSearchMatch is not null)
            {
                FocusActiveSearchMatch();
            }
            else
            {
                RequestInvalidateCanvas?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
    }

    private void FocusActiveSearchMatch()
    {
        if (ActiveSearchMatch is { } match)
        {
            SetCurrentPageFromScroll(match.PageIndex + 1);
            if (RequestScrollToMatch is not null)
            {
                RequestScrollToMatch.Invoke(match);
            }
            else
            {
                RequestScrollToPage?.Invoke(match.PageIndex);
            }
        }
        RequestInvalidateCanvas?.Invoke();
    }

    private void ClearSearchState()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _searchMatches.Clear();
        ActiveSearchMatchIndex = -1;
        TotalSearchMatches = 0;
        ActiveSpreadsheetTab?.ClearSearch();
        ActiveRichDocTab?.ClearSearch();
        ActiveMarkdownTab?.ClearSearch();
        RequestInvalidateCanvas?.Invoke();
    }

    // --- Phase 4: Annotations, Redaction, Forms, Signatures, Security ---

    public void AddAnnotation(PdfAnnotation annotation)
    {
        if (!HasOpenDocument || ActivePdfTab is null) return;

        PushUndoSnapshot(ActivePdfTab);
        ActivePdfTab.EditPlan.Annotations.Add(annotation);
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        SelectedAnnotation = annotation;
        NotifyAnnotationStateChanged();
        RequestInvalidateCanvas?.Invoke();
    }

    public void BeginAnnotationTransformSnapshot()
    {
        if (HasOpenDocument && ActivePdfTab is not null)
        {
            PushUndoSnapshot(ActivePdfTab);
        }
    }

    public void CommitAnnotationTransform()
    {
        if (ActivePdfTab is null) return;
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        RequestInvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void DeleteSelectedAnnotation()
    {
        if (!HasOpenDocument || ActivePdfTab is null || SelectedAnnotation is null) return;

        var toRemove = SelectedAnnotation;
        PushUndoSnapshot(ActivePdfTab);
        ActivePdfTab.EditPlan.Annotations.RemoveAll(a => a.Id == toRemove.Id);
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        SelectedAnnotation = null;
        NotifyAnnotationStateChanged();
        RequestInvalidateCanvas?.Invoke();
    }

    public void ToggleOrUpdateFormField(PdfFormField field, bool? isChecked = null, string? textValue = null)
    {
        if (!HasOpenDocument || ActivePdfTab is null || field.IsReadOnly) return;

        PushUndoSnapshot(ActivePdfTab);
        if (isChecked.HasValue)
        {
            field.IsChecked = isChecked.Value;
        }
        if (textValue is not null)
        {
            field.Value = textValue;
        }
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(FormFields));
        RequestInvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private async Task ApplyRedactionsAndSaveAsync()
    {
        if (!HasOpenDocument || ActivePdfTab is null) return;
        var options = new PdfSaveOptions(
            UserPassword: ProtectWithPassword && !string.IsNullOrWhiteSpace(DocumentPassword) ? DocumentPassword : ActivePdfTab.Password,
            OwnerPassword: ProtectWithPassword && !string.IsNullOrWhiteSpace(DocumentPassword) ? DocumentPassword : ActivePdfTab.Password,
            UseAes256: true,
            FlattenForms: FlattenFormsOnSave,
            BurnRedactions: true,
            SourcePassword: ActivePdfTab.Password);

        await SaveToPathAsync(ActivePdfTab.OriginalFilePath, options);
        StatusMessage = "Redactions applied";
        ShowToast("Redactions applied");
    }

    [RelayCommand]
    private void OpenSignatureModal()
    {
        if (!HasOpenDocument) return;
        IsSignatureModalOpen = true;
    }

    [RelayCommand]
    private void CloseSignatureModal() => IsSignatureModalOpen = false;

    [RelayCommand]
    private void ClearSignaturePad() => RequestClearSignaturePad?.Invoke();

    [RelayCommand]
    private async Task ImportSignaturePngAsync()
    {
        if (RequestImportPngBytes is null) return;
        byte[]? bytes = await RequestImportPngBytes.Invoke();
        if (bytes is { Length: > 0 })
        {
            ApplySignatureBytes(bytes);
        }
    }

    public void ApplySignatureBytes(byte[] pngBytes)
    {
        if (!HasOpenDocument || ActivePdfTab is null || pngBytes.Length == 0) return;

        int pageIdx = Math.Clamp(CurrentPage - 1, 0, Math.Max(0, Pages.Count - 1));
        var page = Pages[pageIdx];
        double stampW = Math.Min(180.0, page.WidthPoints * 0.4);
        double stampH = 72.0;
        double left = (page.WidthPoints - stampW) * 0.5;
        double top = (page.HeightPoints - stampH) * 0.5;

        var signature = new PdfAnnotation
        {
            PageIndex = pageIdx,
            Kind = AnnotationKind.SignatureStamp,
            BoundsPt = new PdfRect(left, top, left + stampW, top + stampH),
            SignaturePngBytes = pngBytes
        };

        AddAnnotation(signature);
        ActiveTool = PdfToolKind.Select;
        IsSignatureModalOpen = false;
        StatusMessage = "Signature placed";
    }

    [RelayCommand]
    private void OpenPasswordModal()
    {
        if (!HasOpenDocument) return;
        IsPasswordModalOpen = true;
    }

    [RelayCommand]
    private void ClosePasswordModal() => IsPasswordModalOpen = false;

    [RelayCommand]
    private async Task ConfirmPasswordProtectAndSaveAsAsync()
    {
        IsPasswordModalOpen = false;
        await SaveDocumentAsAsync();
    }

    [RelayCommand]
    private async Task SubmitOpenPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingLockedFilePath))
        {
            IsOpenPasswordPromptOpen = false;
            return;
        }

        string path = _pendingLockedFilePath;
        string pwd = OpenPasswordInput;
        OpenPasswordError = string.Empty;
        await LoadFileAsync(path, pwd);
    }

    [RelayCommand]
    private void CancelOpenPassword()
    {
        _pendingLockedFilePath = null;
        OpenPasswordInput = string.Empty;
        OpenPasswordError = string.Empty;
        IsOpenPasswordPromptOpen = false;
    }

    private void NotifyAnnotationStateChanged()
    {
        OnPropertyChanged(nameof(Annotations));
        OnPropertyChanged(nameof(PendingRedactionCount));
        OnPropertyChanged(nameof(HasPendingRedactions));
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedEditableText));
    }

    private void SyncFormFieldsFromPlan()
    {
        FormFields.Clear();
        if (ActivePdfTab is not null)
        {
            foreach (var f in ActivePdfTab.EditPlan.FormFields)
            {
                FormFields.Add(f);
            }
        }
        OnPropertyChanged(nameof(HasFormFields));
    }

    // --- Page Organization, Context Menu Actions & Undo/Redo ---

    private int ResolveTargetDisplayIndex(object? parameter) => parameter switch
    {
        PageThumbnailViewModel thumb => Math.Clamp(thumb.DisplayIndex, 0, Math.Max(0, Pages.Count - 1)),
        int i => Math.Clamp(i, 0, Math.Max(0, Pages.Count - 1)),
        _ => Math.Clamp(CurrentPage - 1, 0, Math.Max(0, Pages.Count - 1))
    };

    [RelayCommand]
    private void RotateSelectedRight(object? parameter) => RotatePageAt(ResolveTargetDisplayIndex(parameter), +1);

    [RelayCommand]
    private void RotateSelectedLeft(object? parameter) => RotatePageAt(ResolveTargetDisplayIndex(parameter), -1);

    public void RotatePageAt(int displayIndex, int quarterTurnsDelta)
    {
        if (!HasOpenDocument || ActivePdfTab is null || displayIndex < 0 || displayIndex >= ActivePdfTab.EditPlan.Entries.Count)
        {
            return;
        }

        PushUndoSnapshot(ActivePdfTab);
        ActivePdfTab.EditPlan.RotateAt(displayIndex, quarterTurnsDelta);
        HasUnsavedChanges = true;
        RebuildPagesFromPlan();
        StatusMessage = $"Rotated page {displayIndex + 1}";
    }

    [RelayCommand]
    private void DeleteSelectedPage(object? parameter)
    {
        if (!HasOpenDocument || ActivePdfTab is null || Pages.Count <= 1)
        {
            return;
        }

        int targetIdx = ResolveTargetDisplayIndex(parameter);
        if (targetIdx < 0 || targetIdx >= ActivePdfTab.EditPlan.Entries.Count) return;

        PushUndoSnapshot(ActivePdfTab);
        if (!ActivePdfTab.EditPlan.DeleteAt(targetIdx))
        {
            ActivePdfTab.UndoStack.Pop();
            NotifyUndoRedoChanged();
            return;
        }

        HasUnsavedChanges = true;
        int nextPage = Math.Clamp(targetIdx + 1, 1, ActivePdfTab.EditPlan.Entries.Count);
        SetCurrentPageFromScroll(nextPage);
        RebuildPagesFromPlan();
        NotifyAnnotationStateChanged();
        SyncFormFieldsFromPlan();
        StatusMessage = $"Deleted page {targetIdx + 1}";
    }

    [RelayCommand]
    private void InsertBlankPageAfter(object? parameter)
    {
        if (!HasOpenDocument || ActivePdfTab is null || Pages.Count == 0) return;

        int targetIdx = ResolveTargetDisplayIndex(parameter);
        var refPage = Pages[targetIdx];

        PushUndoSnapshot(ActivePdfTab);
        int insertedIdx = ActivePdfTab.EditPlan.InsertBlankAfter(targetIdx, refPage.WidthPoints, refPage.HeightPoints);
        HasUnsavedChanges = true;
        SetCurrentPageFromScroll(insertedIdx + 1);
        RebuildPagesFromPlan();
        NotifyAnnotationStateChanged();
        SyncFormFieldsFromPlan();
        RequestScrollToPage?.Invoke(insertedIdx);
        StatusMessage = $"Inserted blank page {insertedIdx + 1}";
        ShowToast("Blank page inserted");
    }

    [RelayCommand]
    private async Task InsertPdfAfterAsync(object? parameter)
    {
        if (!HasOpenDocument || ActivePdfTab is null || RequestOpenMultipleFilePaths is null) return;
        int afterIdx = ResolveTargetDisplayIndex(parameter);
        var files = await RequestOpenMultipleFilePaths.Invoke();
        if (files is null || files.Count == 0) return;

        await InsertFilesAfterPageAsync(afterIdx, files);
    }

    public async Task InsertFilesAfterPageAsync(int afterDisplayIndex, IReadOnlyList<string> externalPdfPaths)
    {
        if (ActivePdfTab is null || externalPdfPaths.Count == 0 || ActivePdfTab.EditPlan.Entries.Count == 0) return;

        int clampedAfter = Math.Clamp(afterDisplayIndex, 0, ActivePdfTab.EditPlan.Entries.Count - 1);
        var opts = new PdfSaveOptions(SourcePassword: ActivePdfTab.Password);

        var headPlan = new StructuralEditPlan();
        for (int i = 0; i <= clampedAfter; i++)
        {
            headPlan.Entries.Add(ActivePdfTab.EditPlan.Entries[i]);
        }

        var tailPlan = new StructuralEditPlan();
        for (int i = clampedAfter + 1; i < ActivePdfTab.EditPlan.Entries.Count; i++)
        {
            tailPlan.Entries.Add(ActivePdfTab.EditPlan.Entries[i]);
        }

        var mergeInputs = new List<string>();
        string headPath = CreateTempPdfPath("head");
        await _editorService.ApplyPlanAndSaveAsync(ActivePdfTab.WorkingFilePath, headPath, headPlan, saveOptions: opts);
        mergeInputs.Add(headPath);
        mergeInputs.AddRange(externalPdfPaths);

        if (tailPlan.Entries.Count > 0)
        {
            string tailPath = CreateTempPdfPath("tail");
            await _editorService.ApplyPlanAndSaveAsync(ActivePdfTab.WorkingFilePath, tailPath, tailPlan, saveOptions: opts);
            mergeInputs.Add(tailPath);
        }

        string mergedWorking = CreateTempPdfPath("inserted");
        await _editorService.MergeAsync(mergeInputs, mergedWorking);

        PushUndoSnapshot(ActivePdfTab);

        BitmapCache.Clear();
        var mergedPages = await _renderEngine.OpenAsync(mergedWorking);
        ActivePdfTab.WorkingFilePath = mergedWorking;
        ActivePdfTab.Password = null;
        ActivePdfTab.EditPlan.ResetFromPages(mergedPages);
        ActivePdfTab.EditPlan.HasChanges = true;
        ActivePdfTab.ClearTextLayoutCache();
        HasUnsavedChanges = true;

        RebuildPagesFromPlan();
        NotifyAnnotationStateChanged();
        SyncFormFieldsFromPlan();
        StatusMessage = $"Inserted {mergedPages.Count} total pages";
        ShowToast("Inserted PDF");
    }

    [RelayCommand]
    private async Task CopyPageTextAsync()
    {
        if (!HasOpenDocument || Pages.Count == 0) return;

        string textToCopy;
        if (SelectedAnnotation is not null && !string.IsNullOrWhiteSpace(SelectedAnnotation.TextContent))
        {
            textToCopy = SelectedAnnotation.TextContent;
        }
        else
        {
            int idx = Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1);
            var page = Pages[idx];
            textToCopy = page.IsBlankPage
                ? string.Empty
                : await _renderEngine.ExtractPageTextAsync(page.EffectiveSourcePageIndex);
        }

        if (string.IsNullOrWhiteSpace(textToCopy))
        {
            ShowToast("No text on page");
            return;
        }

        if (RequestCopyToClipboard is not null)
        {
            await RequestCopyToClipboard.Invoke(textToCopy);
        }
        ShowToast("Copied");
    }

    [RelayCommand]
    private void HighlightAtContext()
    {
        if (!HasOpenDocument || Pages.Count == 0) return;
        int pageIdx = Math.Clamp(ContextPageIndex, 0, Pages.Count - 1);
        var page = Pages[pageIdx];
        double left = Math.Clamp(ContextPagePointPt.XPt - 60, 8, Math.Max(8, page.WidthPoints - 130));
        double top = Math.Clamp(ContextPagePointPt.YPt - 10, 8, Math.Max(8, page.HeightPoints - 24));

        var highlight = new PdfAnnotation
        {
            PageIndex = pageIdx,
            Kind = AnnotationKind.Highlight,
            BoundsPt = new PdfRect(left, top, left + 120, top + 18),
            ColorHex = ActiveToolColorHex,
            StrokeWidthPt = ActiveStrokeWidthPt
        };
        AddAnnotation(highlight);
    }

    [RelayCommand]
    private void AddNoteAtContext()
    {
        if (!HasOpenDocument || Pages.Count == 0) return;
        int pageIdx = Math.Clamp(ContextPageIndex, 0, Pages.Count - 1);
        var page = Pages[pageIdx];
        double left = Math.Clamp(ContextPagePointPt.XPt, 8, Math.Max(8, page.WidthPoints - 138));
        double top = Math.Clamp(ContextPagePointPt.YPt, 8, Math.Max(8, page.HeightPoints - 64));

        var note = new PdfAnnotation
        {
            PageIndex = pageIdx,
            Kind = AnnotationKind.StickyNote,
            BoundsPt = new PdfRect(left, top, left + 130, top + 56),
            ColorHex = "#CA8A04",
            FontSizePt = ActiveFontSizePt,
            TextContent = "Note"
        };
        AddAnnotation(note);
        IsRightPanelOpen = true;
    }

    public void MovePage(int fromIndex, int toIndex)
    {
        if (!HasOpenDocument || ActivePdfTab is null || fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= ActivePdfTab.EditPlan.Entries.Count || toIndex < 0 || toIndex >= ActivePdfTab.EditPlan.Entries.Count)
        {
            return;
        }

        PushUndoSnapshot(ActivePdfTab);
        if (!ActivePdfTab.EditPlan.Move(fromIndex, toIndex))
        {
            ActivePdfTab.UndoStack.Pop();
            NotifyUndoRedoChanged();
            return;
        }

        HasUnsavedChanges = true;
        SetCurrentPageFromScroll(toIndex + 1);
        RebuildPagesFromPlan();
        RequestScrollToPage?.Invoke(toIndex);
        StatusMessage = $"Moved page {fromIndex + 1} to {toIndex + 1}";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (ActiveTab is null) return;
        if (ActivePdfTab is not null)
        {
            if (ActivePdfTab.UndoStack.Count == 0) return;

            var current = (ActivePdfTab.WorkingFilePath, ActivePdfTab.EditPlan.Clone());
            var previous = ActivePdfTab.UndoStack.Pop();
            ActivePdfTab.RedoStack.Push(current);

            await RestoreTabSnapshotAsync(ActivePdfTab, previous);
            HasUnsavedChanges = ActivePdfTab.UndoStack.Count > 0 || ActivePdfTab.EditPlan.HasChanges;
            StatusMessage = HasUnsavedChanges ? "Undone" : "Ready";
            return;
        }

        UndoActiveNonPdfTab();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (ActiveTab is null) return;
        if (ActivePdfTab is not null)
        {
            if (ActivePdfTab.RedoStack.Count == 0) return;

            var current = (ActivePdfTab.WorkingFilePath, ActivePdfTab.EditPlan.Clone());
            var next = ActivePdfTab.RedoStack.Pop();
            ActivePdfTab.UndoStack.Push(current);

            await RestoreTabSnapshotAsync(ActivePdfTab, next);
            HasUnsavedChanges = true;
            StatusMessage = "Redone";
            return;
        }

        RedoActiveNonPdfTab();
    }

    [RelayCommand]
    private async Task ExtractSelectedPageAsync(object? parameter)
    {
        if (!HasOpenDocument || ActivePdfTab is null || RequestSaveFilePath is null) return;
        int targetIdx = ResolveTargetDisplayIndex(parameter);
        string suggestedName = $"{Path.GetFileNameWithoutExtension(ActivePdfTab.Title)}_page_{targetIdx + 1}.pdf";
        string? outputPath = await RequestSaveFilePath.Invoke(suggestedName);
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        await ExtractPagesToPathAsync([targetIdx], outputPath);
    }

    public async Task ExtractPagesToPathAsync(IReadOnlyList<int> displayIndices, string outputPath)
    {
        if (ActivePdfTab is null || displayIndices.Count == 0) return;

        var subsetPlan = new StructuralEditPlan();
        foreach (int idx in displayIndices)
        {
            if (idx >= 0 && idx < ActivePdfTab.EditPlan.Entries.Count)
            {
                subsetPlan.Entries.Add(ActivePdfTab.EditPlan.Entries[idx]);
            }
        }

        if (subsetPlan.Entries.Count == 0) return;

        var opts = new PdfSaveOptions(SourcePassword: ActivePdfTab.Password);
        await _editorService.ApplyPlanAndSaveAsync(ActivePdfTab.WorkingFilePath, outputPath, subsetPlan, saveOptions: opts);
        StatusMessage = $"Extracted {subsetPlan.Entries.Count} page(s)";
    }

    [RelayCommand]
    private async Task MergeDocumentsAsync()
    {
        if (!HasOpenDocument || ActivePdfTab is null || RequestOpenMultipleFilePaths is null) return;
        var files = await RequestOpenMultipleFilePaths.Invoke();
        if (files is null || files.Count == 0) return;

        await MergeFilesIntoCurrentDocumentAsync(files);
    }

    public async Task MergeFilesIntoCurrentDocumentAsync(IReadOnlyList<string> externalPdfPaths)
    {
        if (ActivePdfTab is null || externalPdfPaths.Count == 0) return;

        string materializedCurrent = CreateTempPdfPath("staged");
        string mergedWorking = CreateTempPdfPath("merged");

        var opts = new PdfSaveOptions(SourcePassword: ActivePdfTab.Password);
        await _editorService.ApplyPlanAndSaveAsync(ActivePdfTab.WorkingFilePath, materializedCurrent, ActivePdfTab.EditPlan, saveOptions: opts);
        var inputs = new List<string>(1 + externalPdfPaths.Count) { materializedCurrent };
        inputs.AddRange(externalPdfPaths);
        await _editorService.MergeAsync(inputs, mergedWorking);

        PushUndoSnapshot(ActivePdfTab);

        BitmapCache.Clear();
        var mergedPages = await _renderEngine.OpenAsync(mergedWorking);
        ActivePdfTab.WorkingFilePath = mergedWorking;
        ActivePdfTab.Password = null;
        ActivePdfTab.EditPlan.ResetFromPages(mergedPages);
        ActivePdfTab.EditPlan.HasChanges = true;
        ActivePdfTab.ClearTextLayoutCache();
        HasUnsavedChanges = true;

        RebuildPagesFromPlan();
        NotifyAnnotationStateChanged();
        SyncFormFieldsFromPlan();
        StatusMessage = $"Merged {mergedPages.Count} pages";
    }

    [RelayCommand]
    private async Task SaveDocumentAsync()
    {
        if (!HasOpenDocument || ActiveTab is null) return;
        if (ActivePdfTab is not null)
        {
            await SaveToPathAsync(ActivePdfTab.OriginalFilePath);
            return;
        }

        if (!File.Exists(ActiveTab.OriginalFilePath) && RequestSaveFilePath is not null)
        {
            await SaveDocumentAsAsync();
            return;
        }

        await SaveActiveNonPdfTabAsync(ActiveTab.OriginalFilePath);
    }

    [RelayCommand]
    private async Task SaveDocumentAsAsync()
    {
        if (!HasOpenDocument || ActiveTab is null || RequestSaveFilePath is null) return;
        string? targetPath = await RequestSaveFilePath.Invoke(ActiveTab.Title);
        if (string.IsNullOrWhiteSpace(targetPath)) return;

        if (ActivePdfTab is not null)
        {
            await SaveToPathAsync(targetPath);
        }
        else
        {
            await SaveActiveNonPdfTabAsync(targetPath);
        }
    }

    public async Task SaveToPathAsync(string targetPath, PdfSaveOptions? options = null)
    {
        if (ActiveTab is null) return;
        if (ActivePdfTab is null)
        {
            await SaveActiveNonPdfTabAsync(targetPath);
            return;
        }

        StatusMessage = $"Saving {Path.GetFileName(targetPath)}…";

        string? nextPassword = ProtectWithPassword && !string.IsNullOrWhiteSpace(DocumentPassword)
            ? DocumentPassword
            : options?.UserPassword ?? ActivePdfTab.Password;

        var effectiveOptions = options ?? new PdfSaveOptions(
            UserPassword: nextPassword,
            OwnerPassword: nextPassword,
            UseAes256: true,
            FlattenForms: FlattenFormsOnSave,
            BurnRedactions: false,
            SourcePassword: ActivePdfTab.Password);

        if (effectiveOptions.SourcePassword is null && ActivePdfTab.Password is not null)
        {
            effectiveOptions = effectiveOptions with { SourcePassword = ActivePdfTab.Password };
        }

        await _editorService.ApplyPlanAndSaveAsync(
            ActivePdfTab.WorkingFilePath,
            targetPath,
            ActivePdfTab.EditPlan,
            updatedMetadata: null,
            ct: CancellationToken.None,
            saveOptions: effectiveOptions);

        string? reopenPassword = !string.IsNullOrEmpty(effectiveOptions.UserPassword)
            ? effectiveOptions.UserPassword
            : effectiveOptions.OwnerPassword;

        BitmapCache.Clear();
        var savedPages = await _renderEngine.OpenAsync(targetPath, reopenPassword);
        var outline = await _renderEngine.GetOutlineAsync();
        var reloadedFields = effectiveOptions.FlattenForms
            ? []
            : await _editorService.ReadFormFieldsAsync(targetPath, reopenPassword);

        ActivePdfTab.OriginalFilePath = targetPath;
        ActivePdfTab.WorkingFilePath = targetPath;
        ActivePdfTab.Password = reopenPassword;
        ActivePdfTab.Title = Path.GetFileName(targetPath);
        ActivePdfTab.UndoStack.Clear();
        ActivePdfTab.RedoStack.Clear();
        ActivePdfTab.EditPlan.ResetFromPages(savedPages);
        ActivePdfTab.ClearTextLayoutCache();
        foreach (var field in reloadedFields)
        {
            ActivePdfTab.EditPlan.FormFields.Add(field);
        }
        ActivePdfTab.RefreshFileMetadata();
        ActivePdfTab.UpdateMetadataBadges();
        NotifyUndoRedoChanged();
        SelectedAnnotation = null;

        DocumentPath = targetPath;
        DocumentTitle = ActivePdfTab.Title;
        HasUnsavedChanges = false;
        OnPropertyChanged(nameof(ContextBarStatusText));

        OutlineNodes.Clear();
        foreach (var node in outline)
        {
            OutlineNodes.Add(node);
        }

        SetCurrentPageFromScroll(Math.Clamp(CurrentPage, 1, Math.Max(1, savedPages.Count)));
        RebuildPagesFromPlan();
        NotifyAnnotationStateChanged();
        SyncFormFieldsFromPlan();
        StatusMessage = string.IsNullOrEmpty(reopenPassword)
            ? $"Saved {ActivePdfTab.Title}"
            : $"Saved {ActivePdfTab.Title} (protected)";
        ShowToast("Saved");
    }

    private void PushUndoSnapshot(PdfWorkspaceViewModel tab)
    {
        tab.UndoStack.Push((tab.WorkingFilePath, tab.EditPlan.Clone()));
        tab.RedoStack.Clear();
        NotifyUndoRedoChanged();
    }

    private async Task RestoreTabSnapshotAsync(PdfWorkspaceViewModel tab, (string WorkingPath, StructuralEditPlan Plan) snapshot)
    {
        if (!string.Equals(tab.WorkingFilePath, snapshot.WorkingPath, StringComparison.OrdinalIgnoreCase))
        {
            BitmapCache.Clear();
            await _renderEngine.OpenAsync(snapshot.WorkingPath, tab.Password);
            tab.WorkingFilePath = snapshot.WorkingPath;
        }

        tab.EditPlan.RestoreFrom(snapshot.Plan);
        tab.ClearTextLayoutCache();
        SelectedAnnotation = null;
        NotifyUndoRedoChanged();

        int maxPage = Math.Max(1, tab.EditPlan.Entries.Count);
        SetCurrentPageFromScroll(Math.Clamp(CurrentPage, 1, maxPage));
        RebuildPagesFromPlan();
        NotifyAnnotationStateChanged();
        SyncFormFieldsFromPlan();
    }

    private void NotifyUndoRedoChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private string CreateTempPdfPath(string tag)
    {
        string path = Path.Combine(Path.GetTempPath(), $"pdfstudio_{tag}_{Guid.NewGuid():N}.pdf");
        _tempWorkingFiles.Add(path);
        return path;
    }

    // --- Tab Management & Document Loading ---

    [RelayCommand]
    private void ToggleCommandPalette()
    {
        IsCommandPaletteOpen = !IsCommandPaletteOpen;
        if (IsCommandPaletteOpen)
        {
            CommandSearchQuery = string.Empty;
        }
    }

    [RelayCommand]
    private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    [RelayCommand]
    private void ExecutePaletteCommand(CommandPaletteItem? item)
    {
        if (item is null) return;
        IsCommandPaletteOpen = false;
        item.Execute();
    }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        if (RequestOpenFilePath is null) return;
        string? path = await RequestOpenFilePath.Invoke();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await LoadFileAsync(path);
        }
    }

    [RelayCommand]
    private async Task SwitchTabAsync(DocumentTabViewModel? tab)
    {
        if (tab is null || ReferenceEquals(tab, ActiveTab)) return;
        await ActivateTabAsync(tab);
    }

    [RelayCommand]
    private async Task NextTabAsync()
    {
        if (OpenTabs.Count <= 1 || ActiveTab is null) return;
        int idx = OpenTabs.IndexOf(ActiveTab);
        var next = OpenTabs[(idx + 1) % OpenTabs.Count];
        await ActivateTabAsync(next);
    }

    [RelayCommand]
    private async Task PreviousTabAsync()
    {
        if (OpenTabs.Count <= 1 || ActiveTab is null) return;
        int idx = OpenTabs.IndexOf(ActiveTab);
        var prev = OpenTabs[(idx - 1 + OpenTabs.Count) % OpenTabs.Count];
        await ActivateTabAsync(prev);
    }

    [RelayCommand]
    private async Task CloseActiveTabAsync()
    {
        if (ActiveTab is not null)
        {
            await CloseTabAsync(ActiveTab);
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(DocumentTabViewModel? tab)
    {
        if (tab is null) return;
        int idx = OpenTabs.IndexOf(tab);
        if (idx < 0) return;

        if (PromptOnDirtyTabClose && tab.IsDirty)
        {
            PendingCloseTab = tab;
            IsUnsavedChangesModalOpen = true;
            return;
        }

        await CloseTabForceAsync(tab);
    }

    [RelayCommand]
    private async Task ConfirmSaveAndCloseTabAsync()
    {
        var tab = PendingCloseTab;
        IsUnsavedChangesModalOpen = false;
        PendingCloseTab = null;
        if (tab is null) return;

        if (!ReferenceEquals(ActiveTab, tab))
        {
            await ActivateTabAsync(tab);
        }

        await SaveDocumentAsync();
        if (!tab.IsDirty)
        {
            await CloseTabForceAsync(tab);
        }
    }

    [RelayCommand]
    private async Task ConfirmDiscardAndCloseTabAsync()
    {
        var tab = PendingCloseTab;
        IsUnsavedChangesModalOpen = false;
        PendingCloseTab = null;
        if (tab is null) return;

        await CloseTabForceAsync(tab);
    }

    [RelayCommand]
    private void CancelCloseTab()
    {
        IsUnsavedChangesModalOpen = false;
        PendingCloseTab = null;
    }

    private async Task CloseTabForceAsync(DocumentTabViewModel tab)
    {
        int idx = OpenTabs.IndexOf(tab);
        if (idx < 0) return;

        bool wasActive = ReferenceEquals(tab, ActiveTab);
        RecordClosedTab(tab);
        OpenTabs.RemoveAt(idx);
        await tab.DisposeAsync();
        RefreshFilteredOpenTabs();

        if (!wasActive) return;

        if (OpenTabs.Count > 0)
        {
            int nextIdx = Math.Clamp(idx, 0, OpenTabs.Count - 1);
            await ActivateTabAsync(OpenTabs[nextIdx]);
        }
        else
        {
            SetActiveTabAndNotify(null);
            SelectedAnnotation = null;
            ClearPdfTextSelection();
            CancelInlinePdfTextEdit();
            CancelThumbnails();
            ClearSearchState();
            BitmapCache.Clear();
            await _renderEngine.CloseAsync();
            DisposeThumbnails();
            Pages.Clear();
            OutlineNodes.Clear();
            FormFields.Clear();
            DocumentPath = null;
            DocumentTitle = "No file open";
            TotalPages = 1;
            SetCurrentPageFromScroll(1);
            HasOpenDocument = false;
            HasUnsavedChanges = false;
            NotifyUndoRedoChanged();
            NotifyAnnotationStateChanged();
            OnPropertyChanged(nameof(HasFormFields));
            StatusMessage = "Ready";
            RequestInvalidateCanvas?.Invoke();
        }
    }

    public async Task LoadFileAsync(string filePath, string? password = null)
    {
        string fullPath = Path.GetFullPath(filePath);
        var existing = OpenTabs.FirstOrDefault(t =>
            string.Equals(t.OriginalFilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            await ActivateTabAsync(existing);
            return;
        }

        if (!DocumentFormatHelper.IsSupportedFile(fullPath))
        {
            ShowErrorDialog($"Unsupported file format:\n{Path.GetFileName(fullPath)}");
            return;
        }

        var kind = DocumentFormatHelper.GetFormatKind(fullPath);
        if (kind != DocumentFormatKind.Pdf)
        {
            await LoadNonPdfFileAsync(fullPath, kind);
            return;
        }

        try
        {
            StatusMessage = $"Opening {Path.GetFileName(fullPath)}…";
            ClearSearchState();
            ClearPdfTextSelection();
            CancelInlinePdfTextEdit();
            BitmapCache.Clear();

            var pages = await _renderEngine.OpenAsync(fullPath, password);
            var outline = await _renderEngine.GetOutlineAsync();
            var fields = await _editorService.ReadFormFieldsAsync(fullPath, password);

            _pendingLockedFilePath = null;
            IsOpenPasswordPromptOpen = false;
            OpenPasswordInput = string.Empty;
            OpenPasswordError = string.Empty;

            var newTab = new PdfWorkspaceViewModel(fullPath) { Password = password, IsLoaded = true };
            newTab.EditPlan.ResetFromPages(pages);
            foreach (var f in fields)
            {
                newTab.EditPlan.FormFields.Add(f);
            }
            newTab.UpdateMetadataBadges();
            OpenTabs.Add(newTab);
            RefreshFilteredOpenTabs();

            foreach (var t in OpenTabs) t.IsActive = ReferenceEquals(t, newTab);
            SetActiveTabAndNotify(newTab);
            SelectedAnnotation = null;

            OutlineNodes.Clear();
            foreach (var node in outline)
            {
                OutlineNodes.Add(node);
            }

            DocumentPath = fullPath;
            DocumentTitle = newTab.Title;
            HasOpenDocument = true;
            HasUnsavedChanges = false;
            SetCurrentPageFromScroll(1);
            NotifyUndoRedoChanged();
            RebuildPagesFromPlan();
            NotifyAnnotationStateChanged();
            SyncFormFieldsFromPlan();

            StatusMessage = $"{pages.Count} pages";
            RecordRecentFile(fullPath, pages.Count);
            RequestTabCrossfade?.Invoke();
            RequestScrollToPage?.Invoke(0);
            RequestInvalidateCanvas?.Invoke();

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                await RunSearchAsync(SearchQuery);
            }
        }
        catch (Exception ex) when (ex.Message.Contains("error code 4", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            _pendingLockedFilePath = fullPath;
            OpenPasswordError = string.IsNullOrEmpty(password) ? string.Empty : "Incorrect password. Try again.";
            IsOpenPasswordPromptOpen = true;
            StatusMessage = "Password required";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open file: {ex.Message}";
            ShowErrorDialog($"Could not open file:\n{ex.Message}");
        }
    }

    private async Task ActivateTabAsync(DocumentTabViewModel tab)
    {
        if (tab is not PdfWorkspaceViewModel pdfTab)
        {
            await ActivateNonPdfTabAsync(tab);
            return;
        }

        try
        {
            ClearSearchState();
            ClearPdfTextSelection();
            CancelInlinePdfTextEdit();
            BitmapCache.Clear();

            if (!pdfTab.IsLoaded && File.Exists(pdfTab.OriginalFilePath))
            {
                var pages = await _renderEngine.OpenAsync(pdfTab.OriginalFilePath, pdfTab.Password);
                var fields = await _editorService.ReadFormFieldsAsync(pdfTab.OriginalFilePath, pdfTab.Password);
                pdfTab.EditPlan.ResetFromPages(pages);
                foreach (var f in fields) pdfTab.EditPlan.FormFields.Add(f);
                pdfTab.IsLoaded = true;
            }
            else
            {
                await _renderEngine.OpenAsync(pdfTab.WorkingFilePath, pdfTab.Password);
            }

            var outline = await _renderEngine.GetOutlineAsync();

            foreach (var t in OpenTabs) t.IsActive = ReferenceEquals(t, pdfTab);
            SetActiveTabAndNotify(pdfTab);
            SelectedAnnotation = null;

            OutlineNodes.Clear();
            foreach (var node in outline)
            {
                OutlineNodes.Add(node);
            }

            DocumentPath = pdfTab.OriginalFilePath;
            DocumentTitle = pdfTab.Title;
            HasOpenDocument = true;
            HasUnsavedChanges = pdfTab.IsDirty;
            SetCurrentPageFromScroll(Math.Clamp(pdfTab.CurrentPage, 1, Math.Max(1, pdfTab.EditPlan.Entries.Count)));
            SetZoomPercentFromViewport(pdfTab.ZoomPercent);
            pdfTab.UpdateMetadataBadges();
            NotifyUndoRedoChanged();
            RebuildPagesFromPlan();
            NotifyAnnotationStateChanged();
            SyncFormFieldsFromPlan();

            StatusMessage = $"{Pages.Count} pages";
            RequestTabCrossfade?.Invoke();
            RequestScrollToPage?.Invoke(Math.Clamp(CurrentPage - 1, 0, Math.Max(0, Pages.Count - 1)));
            RequestInvalidateCanvas?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not switch tab: {ex.Message}";
            ShowErrorDialog($"Could not switch tab:\n{ex.Message}");
        }
    }

    private void RebuildPagesFromPlan()
    {
        if (ActivePdfTab is null) return;

        var builtPages = ActivePdfTab.EditPlan.BuildPageInfos();
        Pages.Clear();
        foreach (var p in builtPages)
        {
            Pages.Add(p);
        }

        TotalPages = Math.Max(1, builtPages.Count);
        ActivePdfTab.UpdateMetadataBadges();
        OnPropertyChanged(nameof(ContextBarStatusText));
        UpdateActivePageMetadata();

        CancelThumbnails();
        DisposeThumbnails();
        for (int i = 0; i < builtPages.Count; i++)
        {
            PageThumbnails.Add(new PageThumbnailViewModel(i, builtPages[i]));
        }
        UpdateThumbnailSelection();

        RequestInvalidateCanvas?.Invoke();
        StartLazyThumbnailLoading();
    }

    private void UpdateThumbnailSelection()
    {
        int selectedIdx = CurrentPage - 1;
        for (int i = 0; i < PageThumbnails.Count; i++)
        {
            PageThumbnails[i].IsSelected = i == selectedIdx;
        }
    }

    private void StartLazyThumbnailLoading()
    {
        if (!_renderEngine.IsOpen || PageThumbnails.Count == 0) return;

        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        var items = PageThumbnails.ToList();
        _ = LoadThumbnailsAsync(items, cts.Token);
    }

    private async Task LoadThumbnailsAsync(List<PageThumbnailViewModel> items, CancellationToken ct)
    {
        const int targetWidthPx = 280;
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;

            var page = item.PageInfo;
            if (page.IsBlankPage) continue;
            double aspect = page.WidthPoints > 0 ? page.HeightPoints / page.WidthPoints : 1.3;
            int targetHeightPx = Math.Max(1, (int)Math.Round(targetWidthPx * aspect));
            int rot = ((page.RotationDegrees / 90) % 4 + 4) % 4;

            try
            {
                using var skBmp = await _renderEngine.RenderPageAsync(new RenderRequest(
                    page.EffectiveSourcePageIndex,
                    targetWidthPx,
                    targetHeightPx,
                    rot,
                    RenderPriority.Thumbnail,
                    ct));

                if (ct.IsCancellationRequested) return;

                var avaloniaBmp = ConvertToAvaloniaBitmap(skBmp);
                if (Dispatcher.UIThread.CheckAccess())
                {
                    item.ThumbnailBitmap = avaloniaBmp;
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            item.ThumbnailBitmap = avaloniaBmp;
                        }
                        else
                        {
                            avaloniaBmp.Dispose();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Ignore transient thumbnail errors during tab switch or close
            }
        }
    }

    private static WriteableBitmap ConvertToAvaloniaBitmap(SKBitmap skBitmap)
    {
        var wb = new WriteableBitmap(
            new PixelSize(skBitmap.Width, skBitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var fb = wb.Lock();
        int rowBytes = Math.Min(fb.RowBytes, skBitmap.RowBytes);
        unsafe
        {
            byte* src = (byte*)skBitmap.GetPixels();
            byte* dst = (byte*)fb.Address;
            for (int y = 0; y < skBitmap.Height; y++)
            {
                Buffer.MemoryCopy(
                    src + (long)y * skBitmap.RowBytes,
                    dst + (long)y * fb.RowBytes,
                    fb.RowBytes,
                    rowBytes);
            }
        }

        return wb;
    }

    private void CancelThumbnails()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    private void DisposeThumbnails()
    {
        foreach (var t in PageThumbnails)
        {
            t.ThumbnailBitmap?.Dispose();
            t.ThumbnailBitmap = null;
        }
        PageThumbnails.Clear();
    }

    private void UpdateActivePageMetadata()
    {
        int idx = CurrentPage - 1;
        if (idx >= 0 && idx < Pages.Count)
        {
            var p = Pages[idx];
            ActivePageSizeLabel = $"{p.WidthPoints:0.#} × {p.HeightPoints:0.#}";
        }
        else
        {
            ActivePageSizeLabel = "612 × 792";
        }
    }

    private void BuildCommandPalette()
    {
        _allCommands.AddRange([
            new CommandPaletteItem("new-tab", "New Tab / Open…", "Tabs", "Ctrl+T", ToggleNewTabMenu),
            new CommandPaletteItem("open", "Open File…", "File", "Ctrl+O", () => _ = OpenDocumentAsync()),
            new CommandPaletteItem("save", "Save", "File", "Ctrl+S", () => _ = SaveDocumentAsync()),
            new CommandPaletteItem("save-as", "Save As…", "File", "Ctrl+Shift+S", () => _ = SaveDocumentAsAsync()),
            new CommandPaletteItem("export-pdf", "Export to PDF…", "File", "", () => _ = ExportToPdfAsync()),
            new CommandPaletteItem("protect", "Protect with Password", "File", "", OpenPasswordModal),
            new CommandPaletteItem("close-tab", "Close Tab", "Tabs", "Ctrl+W", () => _ = CloseActiveTabAsync()),
            new CommandPaletteItem("reopen-tab", "Reopen Closed Tab", "Tabs", "Ctrl+Shift+T", () => _ = ReopenClosedTabAsync()),
            new CommandPaletteItem("all-tabs", "Search All Open Tabs", "Tabs", "Ctrl+Shift+A", ToggleAllTabsDropdown),
            new CommandPaletteItem("find", "Find…", "Edit", "Ctrl+F", OpenSearchBar),
            new CommandPaletteItem("undo", "Undo", "Edit", "Ctrl+Z", () => _ = UndoAsync()),
            new CommandPaletteItem("redo", "Redo", "Edit", "Ctrl+Y", () => _ = RedoAsync()),
            new CommandPaletteItem("tool-select", "Select", "PDF Tools", "V", SetSelectTool),
            new CommandPaletteItem("tool-hand", "Hand", "PDF Tools", "H", SetHandTool),
            new CommandPaletteItem("tool-edit-text", "Edit PDF Text", "PDF Tools", "E", SetEditTextTool),
            new CommandPaletteItem("tool-highlight", "Highlight", "PDF Tools", "U", SetHighlightTool),
            new CommandPaletteItem("tool-underline", "Underline", "PDF Tools", "", SetUnderlineTool),
            new CommandPaletteItem("tool-draw", "Draw", "PDF Tools", "P", SetDrawTool),
            new CommandPaletteItem("tool-text", "Add Text Box", "PDF Tools", "T", SetTextTool),
            new CommandPaletteItem("tool-note", "Sticky Note", "PDF Tools", "N", SetNoteTool),
            new CommandPaletteItem("tool-redact", "Redact", "PDF Tools", "R", SetRedactTool),
            new CommandPaletteItem("tool-sign", "Signature…", "PDF Tools", "", OpenSignatureModal),
            new CommandPaletteItem("rot-r", "Rotate Right", "Pages", "Ctrl+Shift+R", () => RotateSelectedRight(null)),
            new CommandPaletteItem("rot-l", "Rotate Left", "Pages", "Ctrl+Shift+L", () => RotateSelectedLeft(null)),
            new CommandPaletteItem("del-page", "Delete Page", "Pages", "Del", () => DeleteSelectedPage(null)),
            new CommandPaletteItem("insert-blank", "Insert Blank Page After", "Pages", "", () => InsertBlankPageAfter(null)),
            new CommandPaletteItem("extract", "Extract…", "Pages", "", () => _ = ExtractSelectedPageAsync(null)),
            new CommandPaletteItem("merge", "Merge…", "Pages", "", () => _ = MergeDocumentsAsync()),
            new CommandPaletteItem("theme", "Theme Picker", "View", "Ctrl+Alt+T", ToggleThemePicker),
            new CommandPaletteItem("left-panel", "Toggle Pages Sidebar", "View", "[", ToggleLeftPanel),
            new CommandPaletteItem("right-panel", "Toggle Inspector", "View", "]", ToggleRightPanel),
            new CommandPaletteItem("zoom-in", "Zoom In", "View", "Ctrl+=", ZoomIn),
            new CommandPaletteItem("zoom-out", "Zoom Out", "View", "Ctrl+-", ZoomOut),
            new CommandPaletteItem("zoom-100", "Actual Size", "View", "Ctrl+0", ResetZoom),
            new CommandPaletteItem("fit-width", "Fit Width", "View", "Alt+1", FitToWidth),
            new CommandPaletteItem("fit-page", "Fit Page", "View", "Alt+2", FitToPage),
        ]);
    }

    private void RefreshFilteredCommands()
    {
        FilteredCommands.Clear();
        string q = CommandSearchQuery.Trim();
        foreach (var cmd in _allCommands)
        {
            if (string.IsNullOrEmpty(q) ||
                cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCommands.Add(cmd);
            }
        }
    }
}
