using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Formats;
using PdfStudio.Editing.PdfSharp;

namespace PdfStudio.App.ViewModels;

public sealed record ClosedTabRecord(string FilePath, DocumentFormatKind FormatKind, bool WasPinned);

public sealed partial class MainWindowViewModel
{
    private readonly Stack<ClosedTabRecord> _closedTabHistory = new();
    private int _untitledCounter = 1;
    private DocumentTabViewModel? _subscribedActiveTab;

    // Inline PDF text edit state
    private int _editingPdfPageIndex = -1;
    private PdfTextBlock? _editingPdfBlock;
    private PdfTextEdit? _editingExistingPdfTextEdit;

    // PDF text selection pill state
    private List<(int PageIndex, PdfRect BoundsPt)> _selectedPdfLineBoxesPt = [];

    [ObservableProperty]
    private bool _isAllTabsDropdownOpen;

    [ObservableProperty]
    private string _tabSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isNewTabMenuOpen;

    [ObservableProperty]
    private bool _isPdfSelectionPillVisible;

    [ObservableProperty]
    private double _pdfSelectionPillX;

    [ObservableProperty]
    private double _pdfSelectionPillY;

    [ObservableProperty]
    private string _selectedPdfText = string.Empty;

    [ObservableProperty]
    private bool _isInlinePdfTextEditOpen;

    [ObservableProperty]
    private double _inlinePdfTextEditX;

    [ObservableProperty]
    private double _inlinePdfTextEditY;

    [ObservableProperty]
    private double _inlinePdfTextEditWidth = 180;

    [ObservableProperty]
    private double _inlinePdfTextEditHeight = 28;

    [ObservableProperty]
    private double _inlinePdfTextEditFontSizePx = 14;

    [ObservableProperty]
    private string _inlinePdfTextEditValue = string.Empty;

    public ObservableCollection<DocumentTabViewModel> FilteredOpenTabs { get; } = [];

    public PdfWorkspaceViewModel? ActivePdfTab => ActiveTab as PdfWorkspaceViewModel;
    public DocumentTabViewModel? ActiveNonPdfTab => ActiveTab is PdfWorkspaceViewModel ? null : ActiveTab;
    public SpreadsheetWorkspaceViewModel? ActiveSpreadsheetTab => ActiveTab as SpreadsheetWorkspaceViewModel;
    public RichDocWorkspaceViewModel? ActiveRichDocTab => ActiveTab as RichDocWorkspaceViewModel;
    public MarkdownTextWorkspaceViewModel? ActiveMarkdownTab => ActiveTab as MarkdownTextWorkspaceViewModel;
    public ImageWorkspaceViewModel? ActiveImageTab => ActiveTab as ImageWorkspaceViewModel;

    public bool IsPdfWorkspaceActive => ActiveTab is PdfWorkspaceViewModel;
    public bool IsSpreadsheetWorkspaceActive => ActiveTab is SpreadsheetWorkspaceViewModel;
    public bool IsRichDocWorkspaceActive => ActiveTab is RichDocWorkspaceViewModel;
    public bool IsMarkdownWorkspaceActive => ActiveTab is MarkdownTextWorkspaceViewModel;
    public bool IsImageWorkspaceActive => ActiveTab is ImageWorkspaceViewModel;
    public bool IsPdfOrImageWorkspaceActive => ActiveTab is PdfWorkspaceViewModel or ImageWorkspaceViewModel;
    public bool CanExportPdf => HasOpenDocument && ActiveTab is not null;
    public string ContextBarStatusText => ActiveTab?.ContextStatusText ?? "Ready";

    public event Action? RequestFocusInlinePdfTextEdit;
    public event Action? RequestFocusTabSearchBox;
    public event Action? RequestShowPdfSelectionUpdated;

    partial void OnTabSearchQueryChanged(string value) => RefreshFilteredOpenTabs();

    private void SetActiveTabAndNotify(DocumentTabViewModel? next)
    {
        if (_subscribedActiveTab is not null)
        {
            _subscribedActiveTab.PropertyChanged -= OnSubscribedActiveTabPropertyChanged;
            _subscribedActiveTab = null;
        }

        ActiveTab = next;

        if (next is not null)
        {
            next.PropertyChanged += OnSubscribedActiveTabPropertyChanged;
            _subscribedActiveTab = next;
        }

        NotifyWorkspacePropertiesChanged();
    }

    private void OnSubscribedActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DocumentTabViewModel changedTab || !ReferenceEquals(changedTab, ActiveTab)) return;

        switch (e.PropertyName)
        {
            case nameof(DocumentTabViewModel.IsDirty):
                HasUnsavedChanges = changedTab.IsDirty;
                break;
            case nameof(DocumentTabViewModel.CanUndo):
            case nameof(DocumentTabViewModel.CanRedo):
                NotifyUndoRedoChanged();
                break;
            case nameof(DocumentTabViewModel.ContextStatusText):
                OnPropertyChanged(nameof(ContextBarStatusText));
                StatusMessage = changedTab.ContextStatusText;
                break;
            case nameof(DocumentTabViewModel.Title):
                DocumentTitle = changedTab.Title;
                break;
            case nameof(DocumentTabViewModel.OriginalFilePath):
                DocumentPath = changedTab.OriginalFilePath;
                break;
        }

        if (ActiveTab is ImageWorkspaceViewModel img && e.PropertyName == nameof(ImageWorkspaceViewModel.ZoomPercent))
        {
            _suppressZoomSync = true;
            try
            {
                ZoomPercent = img.ZoomPercent;
            }
            finally
            {
                _suppressZoomSync = false;
            }
        }
    }

    private void NotifyWorkspacePropertiesChanged()
    {
        OnPropertyChanged(nameof(ActivePdfTab));
        OnPropertyChanged(nameof(ActiveNonPdfTab));
        OnPropertyChanged(nameof(ActiveSpreadsheetTab));
        OnPropertyChanged(nameof(ActiveRichDocTab));
        OnPropertyChanged(nameof(ActiveMarkdownTab));
        OnPropertyChanged(nameof(ActiveImageTab));
        OnPropertyChanged(nameof(IsPdfWorkspaceActive));
        OnPropertyChanged(nameof(IsSpreadsheetWorkspaceActive));
        OnPropertyChanged(nameof(IsRichDocWorkspaceActive));
        OnPropertyChanged(nameof(IsMarkdownWorkspaceActive));
        OnPropertyChanged(nameof(IsImageWorkspaceActive));
        OnPropertyChanged(nameof(IsPdfOrImageWorkspaceActive));
        OnPropertyChanged(nameof(CanExportPdf));
        OnPropertyChanged(nameof(ContextBarStatusText));
        NotifyUndoRedoChanged();
        NotifyAnnotationStateChanged();
    }

    // --- Browser Tab Ergonomics ---

    public void RefreshFilteredOpenTabs()
    {
        FilteredOpenTabs.Clear();
        string q = TabSearchQuery.Trim();
        foreach (var tab in OpenTabs)
        {
            if (string.IsNullOrEmpty(q) ||
                tab.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                tab.OriginalFilePath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                tab.FormatBadgeText.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredOpenTabs.Add(tab);
            }
        }
    }

    private void RecordClosedTab(DocumentTabViewModel tab)
    {
        if (!string.IsNullOrWhiteSpace(tab.OriginalFilePath) && File.Exists(tab.OriginalFilePath))
        {
            _closedTabHistory.Push(new ClosedTabRecord(tab.OriginalFilePath, tab.FormatKind, tab.IsPinned));
        }
    }

    [RelayCommand]
    private void ToggleAllTabsDropdown()
    {
        RefreshFilteredOpenTabs();
        IsAllTabsDropdownOpen = !IsAllTabsDropdownOpen;
        if (IsAllTabsDropdownOpen)
        {
            TabSearchQuery = string.Empty;
            RequestFocusTabSearchBox?.Invoke();
        }
    }

    [RelayCommand]
    private async Task SelectTabFromDropdownAsync(DocumentTabViewModel? tab)
    {
        IsAllTabsDropdownOpen = false;
        if (tab is not null)
        {
            await ActivateTabAsync(tab);
        }
    }

    [RelayCommand]
    private void ToggleNewTabMenu() => IsNewTabMenuOpen = !IsNewTabMenuOpen;

    [RelayCommand]
    private async Task NewMarkdownDocumentAsync()
    {
        IsNewTabMenuOpen = false;
        string name = $"Untitled-{_untitledCounter++}.md";
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
        var tab = new MarkdownTextWorkspaceViewModel(path);
        tab.SetInitialText($"# {Path.GetFileNameWithoutExtension(name)}\n\nStart writing Markdown here...\n");
        tab.IsLoaded = true;
        OpenTabs.Add(tab);
        RefreshFilteredOpenTabs();
        await ActivateTabAsync(tab);
    }

    [RelayCommand]
    private async Task NewSpreadsheetDocumentAsync()
    {
        IsNewTabMenuOpen = false;
        string name = $"Sheet-{_untitledCounter++}.xlsx";
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
        var wb = new SpreadsheetWorkbookData();
        var sheet = new SpreadsheetSheetData { Name = "Sheet1" };
        for (int r = 0; r < 25; r++)
        {
            var row = new List<string>(10);
            for (int c = 0; c < 10; c++) row.Add(string.Empty);
            sheet.Rows.Add(row);
        }
        wb.Sheets.Add(sheet);
        var tab = new SpreadsheetWorkspaceViewModel(path);
        tab.LoadFromWorkbook(wb);
        tab.IsLoaded = true;
        OpenTabs.Add(tab);
        RefreshFilteredOpenTabs();
        await ActivateTabAsync(tab);
    }

    [RelayCommand]
    private async Task NewRichDocumentAsync()
    {
        IsNewTabMenuOpen = false;
        string name = $"Document-{_untitledCounter++}.docx";
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
        var model = new RichDocModel();
        model.Blocks.Add(new RichDocBlock { Kind = RichDocBlockKind.Heading1, Text = "Untitled Document", IsBold = true });
        model.Blocks.Add(new RichDocBlock { Kind = RichDocBlockKind.Paragraph, Text = string.Empty });
        var tab = new RichDocWorkspaceViewModel(path);
        tab.LoadFromModel(model);
        tab.IsLoaded = true;
        OpenTabs.Add(tab);
        RefreshFilteredOpenTabs();
        await ActivateTabAsync(tab);
    }

    [RelayCommand]
    private async Task ReopenClosedTabAsync()
    {
        while (_closedTabHistory.Count > 0)
        {
            var closed = _closedTabHistory.Pop();
            if (!File.Exists(closed.FilePath)) continue;
            await LoadFileAsync(closed.FilePath);
            if (ActiveTab is not null && closed.WasPinned)
            {
                TogglePinTab(ActiveTab);
            }
            return;
        }
        ShowToast("No recently closed tabs");
    }

    [RelayCommand]
    private void TogglePinTab(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;

        tab.IsPinned = !tab.IsPinned;
        int currentIdx = OpenTabs.IndexOf(tab);
        if (currentIdx >= 0)
        {
            int targetIdx = tab.IsPinned
                ? OpenTabs.TakeWhile(t => t.IsPinned && !ReferenceEquals(t, tab)).Count()
                : OpenTabs.Count(t => t.IsPinned);
            targetIdx = Math.Clamp(targetIdx, 0, OpenTabs.Count - 1);
            if (targetIdx != currentIdx)
            {
                OpenTabs.Move(currentIdx, targetIdx);
            }
        }
        RefreshFilteredOpenTabs();
    }

    [RelayCommand]
    private async Task DuplicateTabAsync(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;

        string dir = Path.GetDirectoryName(tab.OriginalFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string stem = Path.GetFileNameWithoutExtension(tab.OriginalFilePath);
        string ext = Path.GetExtension(tab.OriginalFilePath);
        string copyPath = Path.Combine(dir, $"{stem} (Copy {_untitledCounter++}){ext}");

        DocumentTabViewModel? dup = null;
        switch (tab)
        {
            case PdfWorkspaceViewModel pdf:
            {
                var p = new PdfWorkspaceViewModel(copyPath) { Password = pdf.Password, IsLoaded = true };
                if (File.Exists(pdf.WorkingFilePath))
                {
                    string tempCopy = CreateTempPdfPath("dup");
                    File.Copy(pdf.WorkingFilePath, tempCopy, overwrite: true);
                    p.WorkingFilePath = tempCopy;
                }
                p.EditPlan.RestoreFrom(pdf.EditPlan.Clone());
                p.EditPlan.HasChanges = true;
                p.IsDirty = true;
                p.UpdateMetadataBadges();
                dup = p;
                break;
            }
            case SpreadsheetWorkspaceViewModel sheet:
            {
                var s = new SpreadsheetWorkspaceViewModel(copyPath) { IsLoaded = true };
                s.LoadFromWorkbook(sheet.BuildWorkbookData());
                s.IsDirty = true;
                dup = s;
                break;
            }
            case RichDocWorkspaceViewModel doc:
            {
                var d = new RichDocWorkspaceViewModel(copyPath) { IsLoaded = true };
                d.LoadFromModel(doc.BuildModel());
                d.IsDirty = true;
                dup = d;
                break;
            }
            case MarkdownTextWorkspaceViewModel md:
            {
                var m = new MarkdownTextWorkspaceViewModel(copyPath) { IsLoaded = true };
                m.SetInitialText(md.TextContent);
                m.IsDirty = true;
                dup = m;
                break;
            }
            case ImageWorkspaceViewModel img when img.CurrentBitmap is not null:
            {
                var i = new ImageWorkspaceViewModel(copyPath) { IsLoaded = true };
                i.LoadBitmap(img.CurrentBitmap);
                i.IsDirty = true;
                dup = i;
                break;
            }
        }

        if (dup is null) return;
        int insertAt = Math.Clamp(OpenTabs.IndexOf(tab) + 1, 0, OpenTabs.Count);
        OpenTabs.Insert(insertAt, dup);
        RefreshFilteredOpenTabs();
        await ActivateTabAsync(dup);
    }

    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= OpenTabs.Count || toIndex < 0 || toIndex >= OpenTabs.Count || fromIndex == toIndex)
        {
            return;
        }
        OpenTabs.Move(fromIndex, toIndex);
        RefreshFilteredOpenTabs();
    }

    [RelayCommand]
    private async Task CopyTabFullPathAsync(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null || RequestCopyToClipboard is null) return;
        await RequestCopyToClipboard.Invoke(tab.OriginalFilePath);
        ShowToast("Copied full path");
    }

    [RelayCommand]
    private void RevealInExplorer(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        RevealPathInExplorer(tab.OriginalFilePath);
    }

    [RelayCommand]
    private void BreadcrumbNavigate(BreadcrumbSegment? segment)
    {
        if (segment is null || string.IsNullOrWhiteSpace(segment.FullPath)) return;
        RevealPathInExplorer(segment.FullPath);
    }

    private void RevealPathInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Explorer failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        var toClose = OpenTabs.Where(t => !ReferenceEquals(t, tab) && !t.IsPinned).ToList();
        foreach (var t in toClose)
        {
            await CloseTabForceAsync(t);
        }
        await ActivateTabAsync(tab);
    }

    [RelayCommand]
    private async Task CloseTabsToRightAsync(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        int idx = OpenTabs.IndexOf(tab);
        if (idx < 0 || idx >= OpenTabs.Count - 1) return;
        var toClose = OpenTabs.Skip(idx + 1).Where(t => !t.IsPinned).ToList();
        foreach (var t in toClose)
        {
            await CloseTabForceAsync(t);
        }
    }

    [RelayCommand]
    private async Task SelectTabByNumberAsync(object? numberArg)
    {
        if (OpenTabs.Count == 0) return;
        int num = numberArg switch
        {
            int i => i,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => 0
        };
        if (num <= 0) return;
        int idx = num == 9 ? OpenTabs.Count - 1 : Math.Clamp(num - 1, 0, OpenTabs.Count - 1);
        await ActivateTabAsync(OpenTabs[idx]);
    }

    // --- Non-PDF Loading, Activation, Save, Export PDF, Undo/Redo ---

    private async Task LoadNonPdfFileAsync(string fullPath, DocumentFormatKind kind)
    {
        try
        {
            StatusMessage = $"Opening {Path.GetFileName(fullPath)}…";
            ClearSearchState();
            ClearPdfTextSelection();
            CancelInlinePdfTextEdit();

            DocumentTabViewModel tab;
            switch (kind)
            {
                case DocumentFormatKind.Spreadsheet:
                    var s = new SpreadsheetWorkspaceViewModel(fullPath);
                    await s.LoadAsync();
                    s.IsLoaded = true;
                    tab = s;
                    break;
                case DocumentFormatKind.RichDocument:
                    var d = new RichDocWorkspaceViewModel(fullPath);
                    await d.LoadAsync();
                    d.IsLoaded = true;
                    tab = d;
                    break;
                case DocumentFormatKind.MarkdownText:
                    var m = new MarkdownTextWorkspaceViewModel(fullPath);
                    await m.LoadAsync();
                    m.IsLoaded = true;
                    tab = m;
                    break;
                case DocumentFormatKind.Image:
                    var i = new ImageWorkspaceViewModel(fullPath);
                    await i.LoadAsync();
                    i.IsLoaded = true;
                    tab = i;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported format: {kind}");
            }

            OpenTabs.Add(tab);
            RefreshFilteredOpenTabs();
            await ActivateNonPdfTabAsync(tab);
            RecordRecentFile(fullPath, 1);

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                await RunSearchAsync(SearchQuery);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open file: {ex.Message}";
            ShowErrorDialog($"Could not open file:\n{ex.Message}");
        }
    }

    private async Task ActivateNonPdfTabAsync(DocumentTabViewModel tab)
    {
        try
        {
            ClearSearchState();
            ClearPdfTextSelection();
            CancelInlinePdfTextEdit();
            CancelThumbnails();
            DisposeThumbnails();
            Pages.Clear();
            OutlineNodes.Clear();
            FormFields.Clear();

            if (!tab.IsLoaded && File.Exists(tab.OriginalFilePath))
            {
                switch (tab)
                {
                    case SpreadsheetWorkspaceViewModel s:
                        await s.LoadAsync();
                        s.IsLoaded = true;
                        break;
                    case RichDocWorkspaceViewModel d:
                        await d.LoadAsync();
                        d.IsLoaded = true;
                        break;
                    case MarkdownTextWorkspaceViewModel m:
                        await m.LoadAsync();
                        m.IsLoaded = true;
                        break;
                    case ImageWorkspaceViewModel i:
                        await i.LoadAsync();
                        i.IsLoaded = true;
                        break;
                }
            }

            foreach (var t in OpenTabs) t.IsActive = ReferenceEquals(t, tab);
            SetActiveTabAndNotify(tab);
            SelectedAnnotation = null;

            DocumentPath = tab.OriginalFilePath;
            DocumentTitle = tab.Title;
            HasOpenDocument = true;
            HasUnsavedChanges = tab.IsDirty;
            StatusMessage = tab.ContextStatusText;

            if (tab is ImageWorkspaceViewModel img)
            {
                SetZoomPercentFromViewport(img.ZoomPercent);
            }

            RequestTabCrossfade?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not switch tab: {ex.Message}";
            ShowErrorDialog($"Could not switch tab:\n{ex.Message}");
        }
    }

    private async Task SaveActiveNonPdfTabAsync(string targetPath)
    {
        if (ActiveTab is null) return;
        try
        {
            StatusMessage = $"Saving {Path.GetFileName(targetPath)}…";
            switch (ActiveTab)
            {
                case SpreadsheetWorkspaceViewModel s:
                    await s.SaveAsync(targetPath);
                    break;
                case RichDocWorkspaceViewModel d:
                    await d.SaveAsync(targetPath);
                    break;
                case MarkdownTextWorkspaceViewModel m:
                    await m.SaveAsync(targetPath);
                    break;
                case ImageWorkspaceViewModel i:
                    await i.SaveAsync(targetPath);
                    break;
                default:
                    return;
            }

            DocumentPath = targetPath;
            DocumentTitle = ActiveTab.Title;
            HasUnsavedChanges = ActiveTab.IsDirty;
            OnPropertyChanged(nameof(ContextBarStatusText));
            NotifyUndoRedoChanged();
            StatusMessage = $"Saved {ActiveTab.Title}";
            ShowToast("Saved");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            ShowErrorDialog($"Could not save file:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportToPdfAsync()
    {
        if (!HasOpenDocument || ActiveTab is null || RequestSaveFilePath is null) return;
        string suggestedPdf = $"{Path.GetFileNameWithoutExtension(ActiveTab.Title)}.pdf";
        string? outputPdf = await RequestSaveFilePath.Invoke(suggestedPdf);
        if (string.IsNullOrWhiteSpace(outputPdf)) return;

        await ExportActiveTabToPdfPathAsync(outputPdf);
    }

    public async Task ExportActiveTabToPdfPathAsync(string targetPdfPath)
    {
        if (ActiveTab is null) return;
        try
        {
            StatusMessage = $"Exporting {Path.GetFileName(targetPdfPath)}…";
            switch (ActiveTab)
            {
                case PdfWorkspaceViewModel:
                    await SaveToPathAsync(targetPdfPath);
                    return;
                case SpreadsheetWorkspaceViewModel s:
                    await s.ExportToPdfAsync(targetPdfPath);
                    break;
                case RichDocWorkspaceViewModel d:
                    await d.ExportToPdfAsync(targetPdfPath);
                    break;
                case MarkdownTextWorkspaceViewModel m:
                    await m.ExportToPdfAsync(targetPdfPath);
                    break;
                case ImageWorkspaceViewModel i:
                    await i.ExportToPdfAsync(targetPdfPath);
                    break;
            }
            StatusMessage = $"Exported {Path.GetFileName(targetPdfPath)}";
            ShowToast("Exported PDF");
        }
        catch (Exception ex)
        {
            ShowErrorDialog($"PDF export failed:\n{ex.Message}");
        }
    }

    private void UndoActiveNonPdfTab()
    {
        switch (ActiveTab)
        {
            case SpreadsheetWorkspaceViewModel s: s.Undo(); break;
            case RichDocWorkspaceViewModel d: d.Undo(); break;
            case MarkdownTextWorkspaceViewModel m: m.Undo(); break;
            case ImageWorkspaceViewModel i: i.Undo(); break;
        }
        HasUnsavedChanges = ActiveTab?.IsDirty ?? false;
        NotifyUndoRedoChanged();
    }

    private void RedoActiveNonPdfTab()
    {
        switch (ActiveTab)
        {
            case SpreadsheetWorkspaceViewModel s: s.Redo(); break;
            case RichDocWorkspaceViewModel d: d.Redo(); break;
            case MarkdownTextWorkspaceViewModel m: m.Redo(); break;
            case ImageWorkspaceViewModel i: i.Redo(); break;
        }
        HasUnsavedChanges = ActiveTab?.IsDirty ?? false;
        NotifyUndoRedoChanged();
    }

    private void UpdateNonPdfSearchStatus()
    {
        if (ActiveSpreadsheetTab is { } s)
        {
            TotalSearchMatches = s.SearchMatches.Count;
            ActiveSearchMatchIndex = s.ActiveSearchMatchIndex;
        }
        else if (ActiveRichDocTab is { } d)
        {
            // Parse from SearchStatusLabel ("i of n")
            ParseSearchLabel(d.SearchStatusLabel);
        }
        else if (ActiveMarkdownTab is { } m)
        {
            TotalSearchMatches = m.SearchMatches.Count;
            ActiveSearchMatchIndex = m.ActiveMatchIndex;
        }
    }

    private void ParseSearchLabel(string label)
    {
        var parts = label.Split(" of ", StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int idx) && int.TryParse(parts[1], out int total) && total > 0)
        {
            TotalSearchMatches = total;
            ActiveSearchMatchIndex = Math.Max(0, idx - 1);
        }
        else
        {
            TotalSearchMatches = 0;
            ActiveSearchMatchIndex = -1;
        }
    }

    // --- PDF Text Selection, Action Pill & In-Place Text Editing ---

    public async Task<PdfPageTextLayout?> GetOrLoadPageTextLayoutAsync(int displayPageIndex)
    {
        var pdfTab = ActivePdfTab;
        if (pdfTab is null || displayPageIndex < 0 || displayPageIndex >= Pages.Count) return null;

        if (pdfTab.TextLayoutCache.TryGetValue(displayPageIndex, out var cached))
        {
            return cached;
        }

        var page = Pages[displayPageIndex];
        if (page.IsBlankPage || !_renderEngine.IsOpen)
        {
            var empty = new PdfPageTextLayout(displayPageIndex, page.WidthPoints, page.HeightPoints, []);
            pdfTab.TextLayoutCache[displayPageIndex] = empty;
            return empty;
        }

        int quarterTurns = ((page.RotationDegrees / 90) % 4 + 4) % 4;
        var layout = await _renderEngine.GetPageTextLayoutAsync(page.EffectiveSourcePageIndex, quarterTurns);
        var mapped = new PdfPageTextLayout(displayPageIndex, layout.PageWidthPt, layout.PageHeightPt, layout.Chars);
        pdfTab.TextLayoutCache[displayPageIndex] = mapped;
        return mapped;
    }

    public void UpdatePdfTextSelection(
        PdfTextSelectionSpan? span,
        string selectedText,
        List<(int PageIndex, PdfRect BoundsPt)> lineBoxesPt,
        Rect anchorBoundsDip,
        double viewportWidthDip,
        double viewportHeightDip)
    {
        if (ActivePdfTab is null) return;
        ActivePdfTab.ActiveSelection = span;
        SelectedPdfText = selectedText;
        _selectedPdfLineBoxesPt = lineBoxesPt;

        if (span is null || lineBoxesPt.Count == 0 || string.IsNullOrEmpty(selectedText))
        {
            IsPdfSelectionPillVisible = false;
            RequestShowPdfSelectionUpdated?.Invoke();
            return;
        }

        if (anchorBoundsDip.Bottom < 0 || anchorBoundsDip.Top > viewportHeightDip)
        {
            IsPdfSelectionPillVisible = false;
            RequestShowPdfSelectionUpdated?.Invoke();
            return;
        }

        const double pillW = 360;
        const double pillH = 36;
        double x = Math.Clamp(anchorBoundsDip.Center.X - pillW * 0.5, 12, Math.Max(12, viewportWidthDip - pillW - 12));
        double y = anchorBoundsDip.Top - pillH - 8;
        if (y < 12)
        {
            y = Math.Min(Math.Max(12, viewportHeightDip - pillH - 68), anchorBoundsDip.Bottom + 8);
        }

        PdfSelectionPillX = x;
        PdfSelectionPillY = y;
        IsPdfSelectionPillVisible = true;
        RequestShowPdfSelectionUpdated?.Invoke();
    }

    public void ClearPdfTextSelection()
    {
        if (ActivePdfTab is not null)
        {
            ActivePdfTab.ActiveSelection = null;
        }
        SelectedPdfText = string.Empty;
        _selectedPdfLineBoxesPt.Clear();
        IsPdfSelectionPillVisible = false;
        RequestShowPdfSelectionUpdated?.Invoke();
    }

    [RelayCommand]
    private async Task CopySelectedPdfTextAsync()
    {
        if (string.IsNullOrEmpty(SelectedPdfText) || RequestCopyToClipboard is null) return;
        await RequestCopyToClipboard.Invoke(SelectedPdfText);
        ShowToast("Copied selected text");
    }

    [RelayCommand]
    private void ApplySelectionHighlight() => ApplySelectionMarkup(AnnotationKind.Highlight, "#FACC15");

    [RelayCommand]
    private void ApplySelectionUnderline() => ApplySelectionMarkup(AnnotationKind.Underline, "#3B82F6");

    [RelayCommand]
    private void ApplySelectionStrikeout() => ApplySelectionMarkup(AnnotationKind.Strikeout, "#EF4444");

    [RelayCommand]
    private void ApplySelectionRedact() => ApplySelectionMarkup(AnnotationKind.Redaction, "#000000");

    private void ApplySelectionMarkup(AnnotationKind kind, string colorHex)
    {
        if (ActivePdfTab is null || _selectedPdfLineBoxesPt.Count == 0) return;

        PushUndoSnapshot(ActivePdfTab);
        foreach (var pageGroup in _selectedPdfLineBoxesPt.GroupBy(b => b.PageIndex))
        {
            var rects = pageGroup.Select(g => g.BoundsPt).ToList();
            double left = rects.Min(r => r.Left);
            double top = rects.Min(r => r.Top);
            double right = rects.Max(r => r.Right);
            double bottom = rects.Max(r => r.Bottom);

            if (kind == AnnotationKind.Redaction)
            {
                foreach (var r in rects)
                {
                    ActivePdfTab.EditPlan.Annotations.Add(new PdfAnnotation
                    {
                        PageIndex = pageGroup.Key,
                        Kind = AnnotationKind.Redaction,
                        BoundsPt = r,
                        ColorHex = colorHex
                    });
                }
            }
            else
            {
                ActivePdfTab.EditPlan.Annotations.Add(new PdfAnnotation
                {
                    PageIndex = pageGroup.Key,
                    Kind = kind,
                    BoundsPt = new PdfRect(left, top, right, bottom),
                    HighlightRectsPt = rects,
                    ColorHex = colorHex,
                    StrokeWidthPt = 2.0
                });
            }
        }

        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        ClearPdfTextSelection();
        NotifyAnnotationStateChanged();
        RequestInvalidateCanvas?.Invoke();
    }

    public bool TryGetEditingPdfBlockInfo(out int pageIndex, out PdfRect boundsPt, out double fontSizePt)
    {
        if (IsInlinePdfTextEditOpen && _editingPdfBlock is not null && _editingPdfPageIndex >= 0)
        {
            pageIndex = _editingPdfPageIndex;
            boundsPt = _editingExistingPdfTextEdit?.OriginalBoundsPt ?? _editingPdfBlock.BoundsPt;
            fontSizePt = _editingExistingPdfTextEdit?.FontSizePt ?? _editingPdfBlock.FontSizePt;
            return true;
        }
        pageIndex = -1;
        boundsPt = new PdfRect(0, 0, 0, 0);
        fontSizePt = 12;
        return false;
    }

    public void UpdateInlinePdfTextEditLayout(Rect boundsDip, double zoomFactor)
    {
        if (!IsInlinePdfTextEditOpen) return;
        InlinePdfTextEditX = Math.Max(8, boundsDip.Left - 4);
        InlinePdfTextEditY = Math.Max(8, boundsDip.Top - 4);
        InlinePdfTextEditWidth = Math.Clamp(boundsDip.Width + 48, 120, 560);
        InlinePdfTextEditHeight = Math.Clamp(boundsDip.Height + 14, 30, 180);
        double fontSizePt = _editingExistingPdfTextEdit?.FontSizePt ?? _editingPdfBlock?.FontSizePt ?? 12.0;
        InlinePdfTextEditFontSizePx = Math.Clamp(fontSizePt * (96.0 / 72.0) * zoomFactor, 10, 48);
    }

    public void BeginInlinePdfTextEdit(
        int pageIndex,
        PdfTextBlock block,
        Rect boundsDip,
        double zoomFactor,
        PdfTextEdit? existingEdit)
    {
        _editingPdfPageIndex = pageIndex;
        _editingPdfBlock = block;
        _editingExistingPdfTextEdit = existingEdit;

        InlinePdfTextEditX = Math.Max(8, boundsDip.Left - 4);
        InlinePdfTextEditY = Math.Max(8, boundsDip.Top - 4);
        InlinePdfTextEditWidth = Math.Clamp(boundsDip.Width + 48, 120, 560);
        InlinePdfTextEditHeight = Math.Clamp(boundsDip.Height + 14, 30, 180);
        double fontSizePt = existingEdit?.FontSizePt ?? block.FontSizePt;
        InlinePdfTextEditFontSizePx = Math.Clamp(fontSizePt * (96.0 / 72.0) * zoomFactor, 10, 48);
        InlinePdfTextEditValue = existingEdit?.NewText ?? block.Text;
        IsInlinePdfTextEditOpen = true;
        RequestFocusInlinePdfTextEdit?.Invoke();
    }

    [RelayCommand]
    private void CommitInlinePdfTextEdit()
    {
        if (!IsInlinePdfTextEditOpen || _editingPdfBlock is null || _editingPdfPageIndex < 0)
        {
            CancelInlinePdfTextEdit();
            return;
        }

        var edit = new PdfTextEdit
        {
            Id = _editingExistingPdfTextEdit?.Id ?? Guid.NewGuid(),
            PageIndex = _editingPdfPageIndex,
            OriginalBoundsPt = _editingExistingPdfTextEdit?.OriginalBoundsPt ?? _editingPdfBlock.BoundsPt,
            OriginalText = _editingExistingPdfTextEdit?.OriginalText ?? _editingPdfBlock.Text,
            NewText = InlinePdfTextEditValue ?? string.Empty,
            FontFamily = _editingExistingPdfTextEdit?.FontFamily ?? _editingPdfBlock.FontName,
            FontSizePt = _editingExistingPdfTextEdit?.FontSizePt ?? _editingPdfBlock.FontSizePt
        };

        CommitPdfTextEditDirect(edit);
        CancelInlinePdfTextEdit();
    }

    [RelayCommand]
    private void DeleteInlinePdfTextBlock()
    {
        InlinePdfTextEditValue = string.Empty;
        CommitInlinePdfTextEdit();
    }

    [RelayCommand]
    private void CancelInlinePdfTextEdit()
    {
        IsInlinePdfTextEditOpen = false;
        _editingPdfPageIndex = -1;
        _editingPdfBlock = null;
        _editingExistingPdfTextEdit = null;
        InlinePdfTextEditValue = string.Empty;
    }

    public void CommitPdfTextEditDirect(PdfTextEdit edit)
    {
        if (ActivePdfTab is null) return;
        PushUndoSnapshot(ActivePdfTab);
        ActivePdfTab.EditPlan.TextEdits.RemoveAll(e => e.Id == edit.Id);
        ActivePdfTab.EditPlan.TextEdits.Add(edit);
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        RequestInvalidateCanvas?.Invoke();
    }
}
