using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfStudio.App.ViewModels;

/// <summary>
/// Manages modal dialog state, transient toasts, password prompts,
/// dirty-tab close confirmations, theme picker popup, and command palette filtering.
/// </summary>
public sealed partial class ModalDialogServiceViewModel : ObservableObject
{
    private CancellationTokenSource? _toastCts;
    private readonly List<CommandPaletteItem> _allCommands = [];

    [ObservableProperty]
    private bool _isThemePickerOpen;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandSearchQuery = string.Empty;

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

    public string? PendingLockedFilePath { get; set; }
    public bool PromptOnDirtyTabClose { get; set; }
    public ObservableCollection<CommandPaletteItem> FilteredCommands { get; } = [];

    partial void OnCommandSearchQueryChanged(string value) => RefreshFilteredCommands();

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

    public void CloseErrorModal() => IsErrorModalOpen = false;

    public void BeginCloseConfirmation(DocumentTabViewModel tab)
    {
        PendingCloseTab = tab;
        IsUnsavedChangesModalOpen = true;
    }

    public DocumentTabViewModel? ConsumePendingCloseTab()
    {
        var tab = PendingCloseTab;
        IsUnsavedChangesModalOpen = false;
        PendingCloseTab = null;
        return tab;
    }

    public void CancelCloseTab()
    {
        IsUnsavedChangesModalOpen = false;
        PendingCloseTab = null;
    }

    public void BeginOpenPasswordPrompt(string fullPath, string? attemptedPassword)
    {
        PendingLockedFilePath = fullPath;
        OpenPasswordError = string.IsNullOrEmpty(attemptedPassword) ? string.Empty : "Incorrect password. Try again.";
        IsOpenPasswordPromptOpen = true;
    }

    public void ClearOpenPasswordState()
    {
        PendingLockedFilePath = null;
        OpenPasswordInput = string.Empty;
        OpenPasswordError = string.Empty;
        IsOpenPasswordPromptOpen = false;
    }

    public void ToggleCommandPalette()
    {
        IsCommandPaletteOpen = !IsCommandPaletteOpen;
        if (IsCommandPaletteOpen)
        {
            CommandSearchQuery = string.Empty;
        }
    }

    public void CloseCommandPalette() => IsCommandPaletteOpen = false;

    public void RegisterCommands(IEnumerable<CommandPaletteItem> commands)
    {
        _allCommands.AddRange(commands);
        RefreshFilteredCommands();
    }

    public void RefreshFilteredCommands()
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

    public bool TryDismissTopModal()
    {
        if (IsErrorModalOpen)
        {
            IsErrorModalOpen = false;
            return true;
        }
        if (IsUnsavedChangesModalOpen)
        {
            CancelCloseTab();
            return true;
        }
        if (IsThemePickerOpen)
        {
            IsThemePickerOpen = false;
            return true;
        }
        if (IsOpenPasswordPromptOpen)
        {
            ClearOpenPasswordState();
            return true;
        }
        if (IsPasswordModalOpen)
        {
            IsPasswordModalOpen = false;
            return true;
        }
        if (IsSignatureModalOpen)
        {
            IsSignatureModalOpen = false;
            return true;
        }
        if (IsCommandPaletteOpen)
        {
            CloseCommandPalette();
            return true;
        }
        return false;
    }
}
