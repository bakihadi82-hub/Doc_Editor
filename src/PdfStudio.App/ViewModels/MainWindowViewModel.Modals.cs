using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.App.Themes;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;

namespace PdfStudio.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ModalDialogServiceViewModel Modals { get; }

    public bool IsThemePickerOpen
    {
        get => Modals.IsThemePickerOpen;
        set => Modals.IsThemePickerOpen = value;
    }

    public bool IsCommandPaletteOpen
    {
        get => Modals.IsCommandPaletteOpen;
        set => Modals.IsCommandPaletteOpen = value;
    }

    public string CommandSearchQuery
    {
        get => Modals.CommandSearchQuery;
        set => Modals.CommandSearchQuery = value;
    }

    public bool IsSignatureModalOpen
    {
        get => Modals.IsSignatureModalOpen;
        set => Modals.IsSignatureModalOpen = value;
    }

    public bool IsPasswordModalOpen
    {
        get => Modals.IsPasswordModalOpen;
        set => Modals.IsPasswordModalOpen = value;
    }

    public bool ProtectWithPassword
    {
        get => Modals.ProtectWithPassword;
        set => Modals.ProtectWithPassword = value;
    }

    public string DocumentPassword
    {
        get => Modals.DocumentPassword;
        set => Modals.DocumentPassword = value;
    }

    public bool IsOpenPasswordPromptOpen
    {
        get => Modals.IsOpenPasswordPromptOpen;
        set => Modals.IsOpenPasswordPromptOpen = value;
    }

    public string OpenPasswordInput
    {
        get => Modals.OpenPasswordInput;
        set => Modals.OpenPasswordInput = value;
    }

    public string OpenPasswordError
    {
        get => Modals.OpenPasswordError;
        set => Modals.OpenPasswordError = value;
    }

    public bool IsToastVisible
    {
        get => Modals.IsToastVisible;
        set => Modals.IsToastVisible = value;
    }

    public string ToastMessage
    {
        get => Modals.ToastMessage;
        set => Modals.ToastMessage = value;
    }

    public bool IsUnsavedChangesModalOpen
    {
        get => Modals.IsUnsavedChangesModalOpen;
        set => Modals.IsUnsavedChangesModalOpen = value;
    }

    public DocumentTabViewModel? PendingCloseTab
    {
        get => Modals.PendingCloseTab;
        set => Modals.PendingCloseTab = value;
    }

    public bool IsErrorModalOpen
    {
        get => Modals.IsErrorModalOpen;
        set => Modals.IsErrorModalOpen = value;
    }

    public string ErrorModalMessage
    {
        get => Modals.ErrorModalMessage;
        set => Modals.ErrorModalMessage = value;
    }

    public bool PromptOnDirtyTabClose
    {
        get => Modals.PromptOnDirtyTabClose;
        set => Modals.PromptOnDirtyTabClose = value;
    }

    public ObservableCollection<CommandPaletteItem> FilteredCommands => Modals.FilteredCommands;
    public string PendingCloseTabTitle => PendingCloseTab?.Title ?? DocumentTitle;

    private void OnModalsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)) return;
        OnPropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(ModalDialogServiceViewModel.PendingCloseTab))
        {
            OnPropertyChanged(nameof(PendingCloseTabTitle));
        }
    }

    public void ShowToast(string message) => Modals.ShowToast(message);

    public void ShowErrorDialog(string message) => Modals.ShowErrorDialog(message);

    [RelayCommand]
    private void CloseErrorModal() => Modals.CloseErrorModal();

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
        if (string.IsNullOrWhiteSpace(Modals.PendingLockedFilePath))
        {
            IsOpenPasswordPromptOpen = false;
            return;
        }

        string path = Modals.PendingLockedFilePath;
        string pwd = OpenPasswordInput;
        OpenPasswordError = string.Empty;
        await LoadFileAsync(path, pwd);
    }

    [RelayCommand]
    private void CancelOpenPassword() => Modals.ClearOpenPasswordState();

    [RelayCommand]
    private void ToggleCommandPalette() => Modals.ToggleCommandPalette();

    [RelayCommand]
    private void CloseCommandPalette() => Modals.CloseCommandPalette();

    [RelayCommand]
    private void ExecutePaletteCommand(CommandPaletteItem? item)
    {
        if (item is null) return;
        IsCommandPaletteOpen = false;
        item.Execute();
    }

    [RelayCommand]
    private async Task ConfirmSaveAndCloseTabAsync()
    {
        var tab = Modals.ConsumePendingCloseTab();
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
        var tab = Modals.ConsumePendingCloseTab();
        if (tab is null) return;

        await CloseTabForceAsync(tab);
    }

    [RelayCommand]
    private void CancelCloseTab() => Modals.CancelCloseTab();

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
        if (Modals.TryDismissTopModal())
        {
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

    private void BuildCommandPalette()
    {
        Modals.RegisterCommands([
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
            new CommandPaletteItem("dark-invert", "Toggle PDF Dark Mode", "View", "", TogglePdfDarkInvert),
            new CommandPaletteItem("left-panel", "Toggle Pages Sidebar", "View", "[", ToggleLeftPanel),
            new CommandPaletteItem("right-panel", "Toggle Inspector", "View", "]", ToggleRightPanel),
            new CommandPaletteItem("zoom-in", "Zoom In", "View", "Ctrl+=", ZoomIn),
            new CommandPaletteItem("zoom-out", "Zoom Out", "View", "Ctrl+-", ZoomOut),
            new CommandPaletteItem("zoom-100", "Actual Size", "View", "Ctrl+0", ResetZoom),
            new CommandPaletteItem("fit-width", "Fit Width", "View", "Alt+1", FitToWidth),
            new CommandPaletteItem("fit-page", "Fit Page", "View", "Alt+2", FitToPage),
        ]);
    }
}
