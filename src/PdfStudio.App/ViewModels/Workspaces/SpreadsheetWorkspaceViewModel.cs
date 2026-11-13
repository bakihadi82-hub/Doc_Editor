using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.Core.Formats;
using PdfStudio.Editing.PdfSharp;

namespace PdfStudio.App.ViewModels;

public sealed class SpreadsheetRowViewModel : ObservableObject
{
    private readonly List<string> _cells;
    private readonly Action? _onCellEdited;

    public SpreadsheetRowViewModel(IEnumerable<string> values, int minColumns, Action? onCellEdited = null)
    {
        _cells = [.. values];
        while (_cells.Count < minColumns)
        {
            _cells.Add(string.Empty);
        }
        _onCellEdited = onCellEdited;
    }

    public int CellCount => _cells.Count;

    public string this[int index]
    {
        get => index >= 0 && index < _cells.Count ? _cells[index] : string.Empty;
        set
        {
            if (index < 0) return;
            while (_cells.Count <= index) _cells.Add(string.Empty);
            string next = value ?? string.Empty;
            if (!string.Equals(_cells[index], next, StringComparison.Ordinal))
            {
                _cells[index] = next;
                OnPropertyChanged("Item[]");
                _onCellEdited?.Invoke();
            }
        }
    }

    public void InsertCellAt(int index, string value = "")
    {
        int clamped = Math.Clamp(index, 0, _cells.Count);
        _cells.Insert(clamped, value);
        OnPropertyChanged("Item[]");
    }

    public void RemoveCellAt(int index)
    {
        if (index >= 0 && index < _cells.Count)
        {
            _cells.RemoveAt(index);
            OnPropertyChanged("Item[]");
        }
    }

    public void EnsureColumns(int count)
    {
        bool changed = false;
        while (_cells.Count < count)
        {
            _cells.Add(string.Empty);
            changed = true;
        }
        if (changed) OnPropertyChanged("Item[]");
    }

    public List<string> ToList() => [.. _cells];
}

public sealed partial class SpreadsheetSheetViewModel : ObservableObject
{
    private readonly Action _onEdited;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private int _columnCount;

    public ObservableCollection<SpreadsheetRowViewModel> Rows { get; } = [];

    public SpreadsheetSheetViewModel(SpreadsheetSheetData data, Action onEdited)
    {
        _name = data.Name;
        _onEdited = onEdited;
        _columnCount = Math.Max(1, data.ColumnCount);

        foreach (var row in data.Rows)
        {
            Rows.Add(new SpreadsheetRowViewModel(row, _columnCount, onEdited));
        }

        if (Rows.Count == 0)
        {
            Rows.Add(new SpreadsheetRowViewModel([], _columnCount, onEdited));
        }
    }

    public SpreadsheetSheetData ToData()
    {
        var data = new SpreadsheetSheetData { Name = Name };
        foreach (var row in Rows)
        {
            data.Rows.Add(row.ToList());
        }
        return data;
    }
}

public sealed record SpreadsheetCellMatch(int SheetIndex, int RowIndex, int ColumnIndex);

public sealed partial class SpreadsheetWorkspaceViewModel : DocumentTabViewModel
{
    private readonly Stack<SpreadsheetWorkbookData> _undoStack = new();
    private readonly Stack<SpreadsheetWorkbookData> _redoStack = new();
    private bool _suppressCellEditUndo;
    private readonly List<SpreadsheetCellMatch> _searchMatches = [];
    private int _activeSearchMatchIndex = -1;
    private int _lastSortedColumn = -1;
    private bool _lastSortAscending;

    public ObservableCollection<SpreadsheetSheetViewModel> Sheets { get; } = [];

    [ObservableProperty]
    private SpreadsheetSheetViewModel? _activeSheet;

    [ObservableProperty]
    private int _selectedRowIndex = -1;

    [ObservableProperty]
    private int _selectedColumnIndex = 0;

    [ObservableProperty]
    private SpreadsheetRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _searchStatusLabel = "0 of 0";

    public override bool CanUndo => _undoStack.Count > 0;
    public override bool CanRedo => _redoStack.Count > 0;
    public IReadOnlyList<SpreadsheetCellMatch> SearchMatches => _searchMatches;
    public int ActiveSearchMatchIndex => _activeSearchMatchIndex;
    public int SearchMatchCount => _searchMatches.Count;
    public int ActiveSearchMatchNumber => _activeSearchMatchIndex >= 0 ? _activeSearchMatchIndex + 1 : 0;

    public event Action? RequestRebuildColumns;
    public event Action<int, int>? RequestScrollToCell;
    public event Action? RequestBeginCellEdit;

    public SpreadsheetWorkspaceViewModel(string filePath)
        : base(filePath, DocumentFormatKind.Spreadsheet)
    {
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var data = await SpreadsheetService.LoadAsync(OriginalFilePath, ct);
        ApplyWorkbookData(data);
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        UpdateMetadataBadges();
    }

    public void LoadFromWorkbook(SpreadsheetWorkbookData data)
    {
        ApplyWorkbookData(data);
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        UpdateMetadataBadges();
    }

    private void ApplyWorkbookData(SpreadsheetWorkbookData data, int? preferredSheetIndex = null)
    {
        _suppressCellEditUndo = true;
        try
        {
            Sheets.Clear();
            foreach (var sheet in data.Sheets)
            {
                Sheets.Add(new SpreadsheetSheetViewModel(sheet, OnCellEdited));
            }
            if (Sheets.Count == 0)
            {
                Sheets.Add(new SpreadsheetSheetViewModel(new SpreadsheetSheetData { Name = "Sheet1" }, OnCellEdited));
            }
            int idx = Math.Clamp(preferredSheetIndex ?? 0, 0, Sheets.Count - 1);
            SelectSheet(Sheets[idx]);
        }
        finally
        {
            _suppressCellEditUndo = false;
        }
    }

    public SpreadsheetWorkbookData BuildWorkbookData()
    {
        var wb = new SpreadsheetWorkbookData();
        foreach (var s in Sheets)
        {
            wb.Sheets.Add(s.ToData());
        }
        return wb;
    }

    public void CaptureUndoSnapshotBeforeCellEdit()
    {
        if (_suppressCellEditUndo) return;
        _undoStack.Push(BuildWorkbookData());
        _redoStack.Clear();
        NotifyUndoRedo();
    }

    public void CommitPendingCellEditSnapshot(SpreadsheetWorkbookData? snapshot)
    {
        if (_suppressCellEditUndo || snapshot is null) return;
        _undoStack.Push(snapshot);
        _redoStack.Clear();
        IsDirty = true;
        NotifyUndoRedo();
        UpdateMetadataBadges();
    }

    private void PushUndoSnapshot()
    {
        _undoStack.Push(BuildWorkbookData());
        _redoStack.Clear();
        NotifyUndoRedo();
    }

    private void OnCellEdited()
    {
        if (_suppressCellEditUndo) return;
        IsDirty = true;
        UpdateMetadataBadges();
    }

    public void NotifyCellSelected(int rowIdx, int colIdx)
    {
        SelectedRowIndex = rowIdx;
        SelectedColumnIndex = colIdx;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void SelectSheet(SpreadsheetSheetViewModel? sheet)
    {
        if (sheet is null) return;
        foreach (var s in Sheets) s.IsActive = ReferenceEquals(s, sheet);
        ActiveSheet = sheet;
        SelectedRowIndex = sheet.Rows.Count > 0 ? 0 : -1;
        SelectedColumnIndex = 0;
        UpdateMetadataBadges();
        RequestRebuildColumns?.Invoke();
    }

    [RelayCommand]
    private void AddSheet()
    {
        PushUndoSnapshot();
        var data = new SpreadsheetSheetData { Name = $"Sheet{Sheets.Count + 1}" };
        data.Rows.Add(["", "", "", ""]);
        var vm = new SpreadsheetSheetViewModel(data, OnCellEdited);
        Sheets.Add(vm);
        SelectSheet(vm);
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void InsertRowBelow()
    {
        if (ActiveSheet is null) return;
        PushUndoSnapshot();
        int insertAt = SelectedRowIndex >= 0 && SelectedRowIndex < ActiveSheet.Rows.Count
            ? SelectedRowIndex + 1
            : ActiveSheet.Rows.Count;
        _suppressCellEditUndo = true;
        ActiveSheet.Rows.Insert(insertAt, new SpreadsheetRowViewModel([], ActiveSheet.ColumnCount, OnCellEdited));
        _suppressCellEditUndo = false;
        SelectedRowIndex = insertAt;
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void InsertRowAbove()
    {
        if (ActiveSheet is null) return;
        PushUndoSnapshot();
        int insertAt = SelectedRowIndex >= 0 && SelectedRowIndex < ActiveSheet.Rows.Count
            ? SelectedRowIndex
            : 0;
        _suppressCellEditUndo = true;
        ActiveSheet.Rows.Insert(insertAt, new SpreadsheetRowViewModel([], ActiveSheet.ColumnCount, OnCellEdited));
        _suppressCellEditUndo = false;
        SelectedRowIndex = insertAt;
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void DeleteSelectedRow()
    {
        if (ActiveSheet is null || ActiveSheet.Rows.Count <= 1) return;
        int idx = SelectedRowIndex >= 0 && SelectedRowIndex < ActiveSheet.Rows.Count
            ? SelectedRowIndex
            : ActiveSheet.Rows.Count - 1;
        PushUndoSnapshot();
        ActiveSheet.Rows.RemoveAt(idx);
        SelectedRowIndex = Math.Clamp(idx, 0, ActiveSheet.Rows.Count - 1);
        IsDirty = true;
        UpdateMetadataBadges();
    }

    [RelayCommand]
    private void InsertColumnRight()
    {
        if (ActiveSheet is null) return;
        PushUndoSnapshot();
        int colIdx = Math.Clamp(SelectedColumnIndex + 1, 0, ActiveSheet.ColumnCount);
        foreach (var row in ActiveSheet.Rows)
        {
            row.InsertCellAt(colIdx, string.Empty);
        }
        ActiveSheet.ColumnCount++;
        SelectedColumnIndex = colIdx;
        IsDirty = true;
        UpdateMetadataBadges();
        RequestRebuildColumns?.Invoke();
    }

    [RelayCommand]
    private void DeleteSelectedColumn()
    {
        if (ActiveSheet is null || ActiveSheet.ColumnCount <= 1) return;
        int colIdx = Math.Clamp(SelectedColumnIndex, 0, ActiveSheet.ColumnCount - 1);
        PushUndoSnapshot();
        foreach (var row in ActiveSheet.Rows)
        {
            row.RemoveCellAt(colIdx);
        }
        ActiveSheet.ColumnCount--;
        SelectedColumnIndex = Math.Clamp(colIdx, 0, ActiveSheet.ColumnCount - 1);
        IsDirty = true;
        UpdateMetadataBadges();
        RequestRebuildColumns?.Invoke();
    }

    [RelayCommand]
    private void SortAscending() => SortActiveColumn(ascending: true);

    [RelayCommand]
    private void SortDescending() => SortActiveColumn(ascending: false);

    public void SortColumnByHeader(int columnIndex)
    {
        bool ascending = _lastSortedColumn == columnIndex ? !_lastSortAscending : true;
        _lastSortedColumn = columnIndex;
        _lastSortAscending = ascending;
        SelectedColumnIndex = columnIndex;
        SortActiveColumn(ascending, columnIndex);
    }

    public void SortActiveColumn(bool ascending, int? columnIndex = null)
    {
        if (ActiveSheet is null || ActiveSheet.Rows.Count <= 1) return;
        int col = Math.Clamp(columnIndex ?? SelectedColumnIndex, 0, Math.Max(0, ActiveSheet.ColumnCount - 1));
        PushUndoSnapshot();

        // Keep row 0 as header if it contains non-numeric text and subsequent rows exist, or sort rows 1..N when row 0 looks like header
        bool preserveHeader = ActiveSheet.Rows.Count > 1 &&
                              !double.TryParse(ActiveSheet.Rows[0][col], NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        int startRow = preserveHeader ? 1 : 0;
        var sortable = ActiveSheet.Rows.Skip(startRow).ToList();
        sortable.Sort((a, b) =>
        {
            int cmp = CompareCellValues(a[col], b[col]);
            return ascending ? cmp : -cmp;
        });

        _suppressCellEditUndo = true;
        try
        {
            for (int i = 0; i < sortable.Count; i++)
            {
                ActiveSheet.Rows[startRow + i] = sortable[i];
            }
        }
        finally
        {
            _suppressCellEditUndo = false;
        }

        IsDirty = true;
        UpdateMetadataBadges();
    }

    private static int CompareCellValues(string a, string b)
    {
        bool aNum = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da);
        bool bNum = double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db);
        if (aNum && bNum) return da.CompareTo(db);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void BeginCellEdit() => RequestBeginCellEdit?.Invoke();

    public void RunSearch(string query)
    {
        _searchMatches.Clear();
        _activeSearchMatchIndex = -1;

        if (!string.IsNullOrWhiteSpace(query))
        {
            for (int s = 0; s < Sheets.Count; s++)
            {
                var sheet = Sheets[s];
                for (int r = 0; r < sheet.Rows.Count; r++)
                {
                    var row = sheet.Rows[r];
                    for (int c = 0; c < sheet.ColumnCount; c++)
                    {
                        if (row[c].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            _searchMatches.Add(new SpreadsheetCellMatch(s, r, c));
                        }
                    }
                }
            }
        }

        if (_searchMatches.Count > 0)
        {
            _activeSearchMatchIndex = 0;
            JumpToActiveMatch();
        }
        SearchStatusLabel = _searchMatches.Count > 0 ? $"1 of {_searchMatches.Count}" : "0 of 0";
    }

    public void NextSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        _activeSearchMatchIndex = (_activeSearchMatchIndex + 1) % _searchMatches.Count;
        JumpToActiveMatch();
    }

    public void PreviousSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        _activeSearchMatchIndex = (_activeSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        JumpToActiveMatch();
    }

    public void ClearSearch()
    {
        _searchMatches.Clear();
        _activeSearchMatchIndex = -1;
        SearchStatusLabel = "0 of 0";
    }

    private void JumpToActiveMatch()
    {
        if (_activeSearchMatchIndex < 0 || _activeSearchMatchIndex >= _searchMatches.Count) return;
        var match = _searchMatches[_activeSearchMatchIndex];
        if (match.SheetIndex >= 0 && match.SheetIndex < Sheets.Count && !ReferenceEquals(ActiveSheet, Sheets[match.SheetIndex]))
        {
            SelectSheet(Sheets[match.SheetIndex]);
        }
        SelectedRowIndex = match.RowIndex;
        SelectedColumnIndex = match.ColumnIndex;
        if (ActiveSheet is not null && match.RowIndex >= 0 && match.RowIndex < ActiveSheet.Rows.Count)
        {
            SelectedRow = ActiveSheet.Rows[match.RowIndex];
        }
        RequestScrollToCell?.Invoke(match.RowIndex, match.ColumnIndex);
        SearchStatusLabel = $"{_activeSearchMatchIndex + 1} of {_searchMatches.Count}";
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        int sheetIdx = ActiveSheet is not null ? Math.Max(0, Sheets.IndexOf(ActiveSheet)) : 0;
        _redoStack.Push(BuildWorkbookData());
        var prev = _undoStack.Pop();
        ApplyWorkbookData(prev, sheetIdx);
        IsDirty = _undoStack.Count > 0;
        NotifyUndoRedo();
        UpdateMetadataBadges();
        RequestRebuildColumns?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        int sheetIdx = ActiveSheet is not null ? Math.Max(0, Sheets.IndexOf(ActiveSheet)) : 0;
        _undoStack.Push(BuildWorkbookData());
        var next = _redoStack.Pop();
        ApplyWorkbookData(next, sheetIdx);
        IsDirty = true;
        NotifyUndoRedo();
        UpdateMetadataBadges();
        RequestRebuildColumns?.Invoke();
    }

    public async Task SaveAsync(string targetPath, CancellationToken ct = default)
    {
        var wb = BuildWorkbookData();
        await SpreadsheetService.SaveAtomicAsync(OriginalFilePath, targetPath, wb, ct);
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
        await PdfFormatExporter.ExportSpreadsheetAsync(BuildWorkbookData(), targetPdfPath, ct);
    }

    public void UpdateMetadataBadges()
    {
        int rows = ActiveSheet?.Rows.Count ?? 0;
        int cols = ActiveSheet?.ColumnCount ?? 0;
        string sheetName = ActiveSheet?.Name ?? "Sheet1";

        MicroMetadataBadge = string.Equals(FormatBadgeText, "XLSX", StringComparison.Ordinal)
            ? $"{Sheets.Count} {(Sheets.Count == 1 ? "sheet" : "sheets")}"
            : $"{rows} rows";

        string cellCoord = SelectedRowIndex >= 0
            ? $" • {SpreadsheetService.GetCellReference(SelectedRowIndex, Math.Max(0, SelectedColumnIndex))}"
            : string.Empty;

        ContextStatusText = $"{sheetName} • {rows} rows × {cols} cols{cellCoord}";
    }

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
}
