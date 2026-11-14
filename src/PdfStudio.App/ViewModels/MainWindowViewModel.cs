using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Styling;
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

    public const int DefaultVisibleThumbnailCount = ThumbnailManagerViewModel.DefaultVisibleThumbnailCount;
    public const int ThumbnailOverscanCount = ThumbnailManagerViewModel.ThumbnailOverscanCount;

    public ThumbnailManagerViewModel Thumbnails { get; }

    public int MaxCachedThumbnails
    {
        get => Thumbnails.MaxCachedThumbnails;
        set => Thumbnails.MaxCachedThumbnails = value;
    }

    public int CachedThumbnailCount => Thumbnails.CachedThumbnailCount;
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
    private bool _isLeftPanelOpen = true;

    [ObservableProperty]
    private bool _isRightPanelOpen = true;

    [ObservableProperty]
    private bool _isPagesTabActive = true;

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
    private bool _flattenFormsOnSave;

    [ObservableProperty]
    private double _leftSidebarWidth = 220.0;

    [ObservableProperty]
    private double _windowWidth = 1360.0;

    [ObservableProperty]
    private double _windowHeight = 860.0;

    [ObservableProperty]
    private bool _isWindowMaximized;

    [ObservableProperty]
    private bool _isPageJumpEditing;

    [ObservableProperty]
    private string _pageJumpInput = "1";

    public int ContextPageIndex { get; set; }
    public PdfPoint ContextPagePointPt { get; set; }

    public LruBitmapCache BitmapCache { get; } = new(512L * 1024 * 1024);
    public IPdfRenderEngine RenderEngine => _renderEngine;

    public ObservableCollection<PdfPageInfo> Pages { get; } = [];
    public ObservableCollection<PageThumbnailViewModel> PageThumbnails => Thumbnails.PageThumbnails;
    public ObservableCollection<PdfOutlineNode> OutlineNodes { get; } = [];
    public ObservableCollection<DocumentTabViewModel> OpenTabs { get; } = [];
    public ObservableCollection<PdfFormField> FormFields { get; } = [];
    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; } = [];
    public ObservableCollection<ThemeOptionViewModel> LightThemeOptions { get; } = [];
    public ObservableCollection<ThemeOptionViewModel> DarkThemeOptions { get; } = [];
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = [];

    public IBrush SelectedThemeAccentBrush => new SolidColorBrush(Color.Parse(SelectedTheme.AccentHex));
    public bool HasRecentFiles => RecentFiles.Count > 0;

    public IReadOnlyList<PdfTextMatch> SearchMatches => _searchMatches;
    public PdfTextMatch? ActiveSearchMatch =>
        ActiveSearchMatchIndex >= 0 && ActiveSearchMatchIndex < _searchMatches.Count
            ? _searchMatches[ActiveSearchMatchIndex]
            : null;

    public bool IsOutlineTabActive => !IsPagesTabActive;

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
    public event Action? RequestUpdateThumbnailViewport;

    public MainWindowViewModel(
        IPdfRenderEngine renderEngine,
        IPdfEditorService? editorService = null,
        string? settingsFilePath = null)
    {
        _renderEngine = renderEngine;
        _editorService = editorService ?? new PdfSharpEditorService();
        _settingsFilePath = settingsFilePath;

        Thumbnails = new ThumbnailManagerViewModel(
            () => _renderEngine.IsOpen,
            request => _renderEngine.RenderPageAsync(request));
        Thumbnails.RequestUpdateThumbnailViewport += () => RequestUpdateThumbnailViewport?.Invoke();

        AnnotationToolbar = new AnnotationToolbarViewModel(
            onToolChanged: value =>
            {
                if (value != PdfToolKind.Select)
                {
                    ClearPdfTextSelection();
                }
                if (value != PdfToolKind.EditText)
                {
                    CancelInlinePdfTextEdit();
                }
            },
            onAnnotationTextMutated: () =>
            {
                HasUnsavedChanges = true;
                if (ActivePdfTab is not null)
                {
                    ActivePdfTab.EditPlan.HasChanges = true;
                }
            },
            onInvalidateCanvas: () => RequestInvalidateCanvas?.Invoke());
        AnnotationToolbar.PropertyChanged += OnAnnotationToolbarPropertyChanged;

        Modals = new ModalDialogServiceViewModel();
        Modals.PropertyChanged += OnModalsPropertyChanged;

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

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageCounterText));
        UpdateActivePageMetadata();
        Thumbnails.UpdateSelection(value);
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
    private void ToggleLeftPanel() => IsLeftPanelOpen = !IsLeftPanelOpen;

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    [RelayCommand]
    private void SelectPagesTab() => IsPagesTabActive = true;

    [RelayCommand]
    private void SelectOutlineTab() => IsPagesTabActive = false;

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
        try
        {
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
        catch (Exception ex)
        {
            StatusMessage = $"Could not save file: {ex.Message}";
            ShowErrorDialog($"Could not save file:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveDocumentAsAsync()
    {
        if (!HasOpenDocument || ActiveTab is null || RequestSaveFilePath is null) return;
        string? targetPath = await RequestSaveFilePath.Invoke(ActiveTab.Title);
        if (string.IsNullOrWhiteSpace(targetPath)) return;

        try
        {
            if (ActivePdfTab is not null)
            {
                await SaveToPathAsync(targetPath);
            }
            else
            {
                await SaveActiveNonPdfTabAsync(targetPath);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save file: {ex.Message}";
            ShowErrorDialog($"Could not save file:\n{ex.Message}");
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
            Modals.BeginCloseConfirmation(tab);
            return;
        }

        await CloseTabForceAsync(tab);
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
            Thumbnails.Cancel();
            ClearSearchState();
            BitmapCache.Clear();
            await _renderEngine.CloseAsync();
            Thumbnails.DisposeAll();
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

            Modals.ClearOpenPasswordState();

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
            Modals.BeginOpenPasswordPrompt(fullPath, password);
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

        Thumbnails.Rebuild(builtPages, CurrentPage);
        RequestInvalidateCanvas?.Invoke();
    }

    public void UpdateThumbnailViewport(int firstVisibleIndex, int lastVisibleIndex)
        => Thumbnails.UpdateViewport(firstVisibleIndex, lastVisibleIndex);

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
}
