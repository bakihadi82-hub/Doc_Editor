using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.Core.Formats;
using PdfStudio.Editing.PdfSharp;

namespace PdfStudio.App.ViewModels;

public sealed partial class RichDocTableCellViewModel : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    private string _text;

    public RichDocTableCellViewModel(string text, Action onChanged)
    {
        _text = text;
        _onChanged = onChanged;
    }

    partial void OnTextChanged(string value) => _onChanged();
}

public sealed class RichDocTableRowViewModel
{
    public ObservableCollection<RichDocTableCellViewModel> Cells { get; } = [];
}

public sealed partial class RichDocBlockViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public Guid Id { get; }

    [ObservableProperty]
    private RichDocBlockKind _kind;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isBold;

    [ObservableProperty]
    private bool _isItalic;

    [ObservableProperty]
    private bool _isUnderline;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _listNumber = 1;

    public ObservableCollection<RichDocTableRowViewModel> TableRows { get; } = [];

    public bool IsTextBlock => Kind != RichDocBlockKind.Table;
    public bool IsTableBlock => Kind == RichDocBlockKind.Table;
    public bool IsBullet => Kind == RichDocBlockKind.BulletItem;
    public bool IsNumbered => Kind == RichDocBlockKind.NumberedItem;
    public string ListPrefix => Kind switch
    {
        RichDocBlockKind.BulletItem => "•",
        RichDocBlockKind.NumberedItem => $"{ListNumber}.",
        _ => string.Empty
    };

    public double BlockFontSize => Kind switch
    {
        RichDocBlockKind.Heading1 => 24,
        RichDocBlockKind.Heading2 => 19,
        RichDocBlockKind.Heading3 => 15.5,
        _ => 13.5
    };

    public FontWeight BlockFontWeight =>
        (IsBold || Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2 or RichDocBlockKind.Heading3)
            ? FontWeight.Bold
            : FontWeight.Normal;

    public FontStyle BlockFontStyle => IsItalic ? FontStyle.Italic : FontStyle.Normal;
    public TextDecorationCollection? BlockTextDecorations => IsUnderline ? TextDecorations.Underline : null;
    public Thickness BlockMargin => Kind switch
    {
        RichDocBlockKind.Heading1 => new Thickness(0, 12, 0, 4),
        RichDocBlockKind.Heading2 => new Thickness(0, 10, 0, 3),
        RichDocBlockKind.Heading3 => new Thickness(0, 8, 0, 2),
        RichDocBlockKind.BulletItem or RichDocBlockKind.NumberedItem => new Thickness(20, 2, 0, 2),
        _ => new Thickness(0, 2, 0, 4)
    };

    public RichDocBlockViewModel(RichDocBlock block, Action onChanged)
    {
        Id = block.Id;
        _kind = block.Kind;
        _text = block.Text;
        _isBold = block.IsBold;
        _isItalic = block.IsItalic;
        _isUnderline = block.IsUnderline;
        _onChanged = onChanged;

        foreach (var row in block.TableRows)
        {
            var rVm = new RichDocTableRowViewModel();
            foreach (var cell in row)
            {
                rVm.Cells.Add(new RichDocTableCellViewModel(cell, onChanged));
            }
            TableRows.Add(rVm);
        }
    }

    partial void OnKindChanged(RichDocBlockKind value)
    {
        OnPropertyChanged(nameof(IsTextBlock));
        OnPropertyChanged(nameof(IsTableBlock));
        OnPropertyChanged(nameof(IsBullet));
        OnPropertyChanged(nameof(IsNumbered));
        OnPropertyChanged(nameof(ListPrefix));
        OnPropertyChanged(nameof(BlockFontSize));
        OnPropertyChanged(nameof(BlockFontWeight));
        OnPropertyChanged(nameof(BlockMargin));
        _onChanged();
    }

    partial void OnTextChanged(string value) => _onChanged();
    partial void OnIsBoldChanged(bool value) { OnPropertyChanged(nameof(BlockFontWeight)); _onChanged(); }
    partial void OnIsItalicChanged(bool value) { OnPropertyChanged(nameof(BlockFontStyle)); _onChanged(); }
    partial void OnIsUnderlineChanged(bool value) { OnPropertyChanged(nameof(BlockTextDecorations)); _onChanged(); }
    partial void OnListNumberChanged(int value) => OnPropertyChanged(nameof(ListPrefix));

    public RichDocBlock ToModel() => new()
    {
        Id = Id,
        Kind = Kind,
        Text = Text,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsUnderline = IsUnderline,
        TableRows = TableRows.Select(r => r.Cells.Select(c => c.Text).ToList()).ToList()
    };
}

public sealed partial class RichDocWorkspaceViewModel : DocumentTabViewModel
{
    private byte[]? _originalPackageBytes;
    private readonly Stack<RichDocModel> _undoStack = new();
    private readonly Stack<RichDocModel> _redoStack = new();
    private bool _suppressMutations;
    private readonly List<RichDocBlockViewModel> _searchMatches = [];
    private int _activeMatchIndex = -1;

    public ObservableCollection<RichDocBlockViewModel> Blocks { get; } = [];

    [ObservableProperty]
    private RichDocBlockViewModel? _selectedBlock;

    [ObservableProperty]
    private string _searchStatusLabel = "0 of 0";

    public bool IsHeadingActive => SelectedBlock?.Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2 or RichDocBlockKind.Heading3;
    public bool IsBoldActive => SelectedBlock?.IsBold == true;
    public bool IsItalicActive => SelectedBlock?.IsItalic == true;
    public bool IsUnderlineActive => SelectedBlock?.IsUnderline == true;
    public bool IsBulletActive => SelectedBlock?.Kind == RichDocBlockKind.BulletItem;
    public bool IsNumberedActive => SelectedBlock?.Kind == RichDocBlockKind.NumberedItem;
    public bool IsTableSelected => SelectedBlock?.Kind == RichDocBlockKind.Table;
    public string HeadingLabel => SelectedBlock?.Kind switch
    {
        RichDocBlockKind.Heading1 => "H1",
        RichDocBlockKind.Heading2 => "H2",
        RichDocBlockKind.Heading3 => "H3",
        _ => "Body"
    };

    public override bool CanUndo => _undoStack.Count > 0;
    public override bool CanRedo => _redoStack.Count > 0;

    public RichDocWorkspaceViewModel(string filePath)
        : base(filePath, DocumentFormatKind.RichDocument)
    {
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var model = await RichDocService.LoadAsync(OriginalFilePath, ct);
        ApplyModel(model);
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        UpdateMetadataBadges();
    }

    public void LoadFromModel(RichDocModel model)
    {
        ApplyModel(model);
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        UpdateMetadataBadges();
    }

    private void ApplyModel(RichDocModel model)
    {
        _suppressMutations = true;
        try
        {
            _originalPackageBytes = model.OriginalPackageBytes;
            Blocks.Clear();
            foreach (var b in model.Blocks)
            {
                Blocks.Add(new RichDocBlockViewModel(b, OnBlockMutated));
            }
            if (Blocks.Count == 0)
            {
                Blocks.Add(new RichDocBlockViewModel(new RichDocBlock(), OnBlockMutated));
            }
            RenumberLists();
            SelectBlock(Blocks[0]);
        }
        finally
        {
            _suppressMutations = false;
        }
    }

    public RichDocModel BuildModel()
    {
        var m = new RichDocModel { OriginalPackageBytes = _originalPackageBytes };
        foreach (var b in Blocks)
        {
            m.Blocks.Add(b.ToModel());
        }
        return m;
    }

    private void PushUndo()
    {
        _undoStack.Push(BuildModel());
        _redoStack.Clear();
        NotifyUndoRedo();
    }

    private void OnBlockMutated()
    {
        if (_suppressMutations) return;
        IsDirty = true;
        UpdateMetadataBadges();
    }

    partial void OnSelectedBlockChanged(RichDocBlockViewModel? value)
    {
        foreach (var b in Blocks)
        {
            b.IsSelected = ReferenceEquals(b, value);
        }
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void SelectBlock(RichDocBlockViewModel? block)
    {
        SelectedBlock = block;
    }

    [RelayCommand]
    private void CycleHeading()
    {
        if (SelectedBlock is null || SelectedBlock.Kind == RichDocBlockKind.Table) return;
        PushUndo();
        SelectedBlock.Kind = SelectedBlock.Kind switch
        {
            RichDocBlockKind.Paragraph => RichDocBlockKind.Heading1,
            RichDocBlockKind.Heading1 => RichDocBlockKind.Heading2,
            RichDocBlockKind.Heading2 => RichDocBlockKind.Heading3,
            _ => RichDocBlockKind.Paragraph
        };
        if (SelectedBlock.Kind is RichDocBlockKind.Heading1 or RichDocBlockKind.Heading2 or RichDocBlockKind.Heading3)
        {
            SelectedBlock.IsBold = true;
        }
        RenumberLists();
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void ToggleBold()
    {
        if (SelectedBlock is null || SelectedBlock.Kind == RichDocBlockKind.Table) return;
        PushUndo();
        SelectedBlock.IsBold = !SelectedBlock.IsBold;
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void ToggleItalic()
    {
        if (SelectedBlock is null || SelectedBlock.Kind == RichDocBlockKind.Table) return;
        PushUndo();
        SelectedBlock.IsItalic = !SelectedBlock.IsItalic;
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void ToggleUnderline()
    {
        if (SelectedBlock is null || SelectedBlock.Kind == RichDocBlockKind.Table) return;
        PushUndo();
        SelectedBlock.IsUnderline = !SelectedBlock.IsUnderline;
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void ToggleBulletList()
    {
        if (SelectedBlock is null || SelectedBlock.Kind == RichDocBlockKind.Table) return;
        PushUndo();
        SelectedBlock.Kind = SelectedBlock.Kind == RichDocBlockKind.BulletItem
            ? RichDocBlockKind.Paragraph
            : RichDocBlockKind.BulletItem;
        RenumberLists();
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void ToggleNumberedList()
    {
        if (SelectedBlock is null || SelectedBlock.Kind == RichDocBlockKind.Table) return;
        PushUndo();
        SelectedBlock.Kind = SelectedBlock.Kind == RichDocBlockKind.NumberedItem
            ? RichDocBlockKind.Paragraph
            : RichDocBlockKind.NumberedItem;
        RenumberLists();
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void AddParagraphAfter()
    {
        PushUndo();
        int idx = SelectedBlock is not null ? Blocks.IndexOf(SelectedBlock) + 1 : Blocks.Count;
        idx = Math.Clamp(idx, 0, Blocks.Count);
        var kind = SelectedBlock?.Kind is RichDocBlockKind.BulletItem or RichDocBlockKind.NumberedItem
            ? SelectedBlock.Kind
            : RichDocBlockKind.Paragraph;
        var nb = new RichDocBlockViewModel(new RichDocBlock { Kind = kind }, OnBlockMutated);
        Blocks.Insert(idx, nb);
        RenumberLists();
        SelectedBlock = nb;
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void InsertTable()
    {
        PushUndo();
        int idx = SelectedBlock is not null ? Blocks.IndexOf(SelectedBlock) + 1 : Blocks.Count;
        idx = Math.Clamp(idx, 0, Blocks.Count);
        var tbl = new RichDocBlock
        {
            Kind = RichDocBlockKind.Table,
            TableRows =
            [
                ["Column 1", "Column 2", "Column 3"],
                ["", "", ""]
            ]
        };
        var vm = new RichDocBlockViewModel(tbl, OnBlockMutated);
        Blocks.Insert(idx, vm);
        SelectedBlock = vm;
        IsDirty = true;
        UpdateMetadataBadges();
        NotifyFormattingFlags();
    }

    [RelayCommand]
    private void AddTableRow()
    {
        if (SelectedBlock?.Kind != RichDocBlockKind.Table) return;
        PushUndo();
        int cols = Math.Max(1, SelectedBlock.TableRows.FirstOrDefault()?.Cells.Count ?? 2);
        var row = new RichDocTableRowViewModel();
        for (int i = 0; i < cols; i++) row.Cells.Add(new RichDocTableCellViewModel(string.Empty, OnBlockMutated));
        SelectedBlock.TableRows.Add(row);
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void AddTableColumn()
    {
        if (SelectedBlock?.Kind != RichDocBlockKind.Table) return;
        PushUndo();
        foreach (var r in SelectedBlock.TableRows)
        {
            r.Cells.Add(new RichDocTableCellViewModel(string.Empty, OnBlockMutated));
        }
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void DeleteSelectedBlock()
    {
        if (SelectedBlock is null || Blocks.Count <= 1) return;
        PushUndo();
        int idx = Blocks.IndexOf(SelectedBlock);
        Blocks.RemoveAt(idx);
        RenumberLists();
        SelectedBlock = Blocks[Math.Clamp(idx, 0, Blocks.Count - 1)];
        IsDirty = true;
        UpdateMetadataBadges();
    }

    private void RenumberLists()
    {
        int n = 1;
        foreach (var b in Blocks)
        {
            if (b.Kind == RichDocBlockKind.NumberedItem)
            {
                b.ListNumber = n++;
            }
            else
            {
                n = 1;
            }
        }
    }

    public void RunSearch(string query)
    {
        _searchMatches.Clear();
        _activeMatchIndex = -1;
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var b in Blocks)
            {
                bool hit = b.Kind == RichDocBlockKind.Table
                    ? b.TableRows.Any(r => r.Cells.Any(c => c.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    : b.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (hit) _searchMatches.Add(b);
            }
        }

        if (_searchMatches.Count > 0)
        {
            _activeMatchIndex = 0;
            SelectedBlock = _searchMatches[0];
        }
        SearchStatusLabel = _searchMatches.Count > 0 ? $"1 of {_searchMatches.Count}" : "0 of 0";
    }

    public void NextSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        _activeMatchIndex = (_activeMatchIndex + 1) % _searchMatches.Count;
        SelectedBlock = _searchMatches[_activeMatchIndex];
        SearchStatusLabel = $"{_activeMatchIndex + 1} of {_searchMatches.Count}";
    }

    public void PreviousSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        _activeMatchIndex = (_activeMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        SelectedBlock = _searchMatches[_activeMatchIndex];
        SearchStatusLabel = $"{_activeMatchIndex + 1} of {_searchMatches.Count}";
    }

    public void ClearSearch()
    {
        _searchMatches.Clear();
        _activeMatchIndex = -1;
        SearchStatusLabel = "0 of 0";
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(BuildModel());
        ApplyModel(_undoStack.Pop());
        IsDirty = _undoStack.Count > 0;
        NotifyUndoRedo();
        UpdateMetadataBadges();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(BuildModel());
        ApplyModel(_redoStack.Pop());
        IsDirty = true;
        NotifyUndoRedo();
        UpdateMetadataBadges();
    }

    public async Task SaveAsync(string targetPath, CancellationToken ct = default)
    {
        var model = BuildModel();
        await RichDocService.SaveAtomicAsync(targetPath, model, ct);
        _originalPackageBytes = model.OriginalPackageBytes;
        OriginalFilePath = targetPath;
        WorkingFilePath = targetPath;
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        NotifyUndoRedo();
        RefreshFileMetadata();
        UpdateMetadataBadges();
    }

    public async Task ExportToPdfAsync(string targetPdfPath, CancellationToken ct = default)
    {
        await PdfFormatExporter.ExportRichDocumentAsync(BuildModel(), targetPdfPath, ct);
    }

    public void UpdateMetadataBadges()
    {
        int words = BuildModel().WordCount;
        MicroMetadataBadge = $"{words}w";
        ContextStatusText = $"{Blocks.Count} blocks • {words} words";
    }

    private void NotifyFormattingFlags()
    {
        OnPropertyChanged(nameof(IsHeadingActive));
        OnPropertyChanged(nameof(IsBoldActive));
        OnPropertyChanged(nameof(IsItalicActive));
        OnPropertyChanged(nameof(IsUnderlineActive));
        OnPropertyChanged(nameof(IsBulletActive));
        OnPropertyChanged(nameof(IsNumberedActive));
        OnPropertyChanged(nameof(IsTableSelected));
        OnPropertyChanged(nameof(HeadingLabel));
    }

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
}
