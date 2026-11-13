using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Formats;

namespace PdfStudio.App.Views.Editors;

public partial class SpreadsheetEditorView : UserControl
{
    private SpreadsheetWorkspaceViewModel? _vm;
    private SpreadsheetWorkbookData? _pendingEditSnapshot;
    private int _pendingEditRow = -1;
    private int _pendingEditCol = -1;
    private string? _pendingEditOriginalValue;

    public SpreadsheetEditorView()
    {
        InitializeComponent();
        SheetGrid.LoadingRow += OnLoadingRow;
        SheetGrid.BeginningEdit += OnBeginningEdit;
        SheetGrid.CellEditEnded += OnCellEditEnded;
        SheetGrid.Sorting += OnGridSorting;
        SheetGrid.CurrentCellChanged += OnCurrentCellChanged;
        SheetGrid.KeyDown += OnGridKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.RequestRebuildColumns -= RebuildColumns;
            _vm.RequestScrollToCell -= ScrollToCell;
            _vm.RequestBeginCellEdit -= BeginCellEdit;
        }

        base.OnDataContextChanged(e);

        _vm = DataContext as SpreadsheetWorkspaceViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.RequestRebuildColumns += RebuildColumns;
            _vm.RequestScrollToCell += ScrollToCell;
            _vm.RequestBeginCellEdit += BeginCellEdit;
            RebuildColumns();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpreadsheetWorkspaceViewModel.ActiveSheet))
        {
            RebuildColumns();
        }
    }

    private void RebuildColumns()
    {
        SheetGrid.Columns.Clear();
        if (_vm?.ActiveSheet is null) return;

        int colCount = Math.Max(1, _vm.ActiveSheet.ColumnCount);
        for (int i = 0; i < colCount; i++)
        {
            int colIdx = i;
            var col = new DataGridTextColumn
            {
                Header = SpreadsheetService.GetColumnLabel(colIdx),
                Binding = new Binding($"[{colIdx}]"),
                Width = new DataGridLength(120),
                MinWidth = 60,
                IsReadOnly = false
            };
            SheetGrid.Columns.Add(col);
        }
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.Index + 1).ToString();
    }

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_vm?.ActiveSheet is null) return;
        int rowIdx = e.Row.Index;
        int colIdx = SheetGrid.Columns.IndexOf(e.Column);
        _pendingEditRow = rowIdx;
        _pendingEditCol = colIdx;
        _pendingEditOriginalValue = rowIdx >= 0 && rowIdx < _vm.ActiveSheet.Rows.Count && colIdx >= 0
            ? _vm.ActiveSheet.Rows[rowIdx][colIdx]
            : null;
        _pendingEditSnapshot = _vm.BuildWorkbookData();
    }

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        var snapshot = _pendingEditSnapshot;
        int rowIdx = _pendingEditRow;
        int colIdx = _pendingEditCol;
        string? origVal = _pendingEditOriginalValue;
        _pendingEditSnapshot = null;
        _pendingEditRow = -1;
        _pendingEditCol = -1;
        _pendingEditOriginalValue = null;

        if (_vm?.ActiveSheet is null || snapshot is null || e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        if (rowIdx >= 0 && rowIdx < _vm.ActiveSheet.Rows.Count && colIdx >= 0)
        {
            string newVal = _vm.ActiveSheet.Rows[rowIdx][colIdx];
            if (!string.Equals(origVal, newVal, StringComparison.Ordinal))
            {
                _vm.CommitPendingCellEditSnapshot(snapshot);
            }
        }
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        int colIdx = SheetGrid.Columns.IndexOf(e.Column);
        if (colIdx >= 0)
        {
            _vm?.SortColumnByHeader(colIdx);
        }
    }

    private void OnCurrentCellChanged(object? sender, EventArgs e)
    {
        if (_vm?.ActiveSheet is null) return;
        int rowIdx = SheetGrid.SelectedIndex;
        int colIdx = SheetGrid.CurrentColumn is not null
            ? SheetGrid.Columns.IndexOf(SheetGrid.CurrentColumn)
            : 0;
        if (rowIdx >= 0 && colIdx >= 0)
        {
            _vm.NotifyCellSelected(rowIdx, colIdx);
        }
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginCellEdit();
            e.Handled = true;
        }
    }

    private void BeginCellEdit()
    {
        SheetGrid.Focus();
        SheetGrid.BeginEdit();
    }

    private void ScrollToCell(int rowIndex, int colIndex)
    {
        if (_vm?.ActiveSheet is null) return;
        if (rowIndex < 0 || rowIndex >= _vm.ActiveSheet.Rows.Count) return;

        var rowItem = _vm.ActiveSheet.Rows[rowIndex];
        DataGridColumn? col = colIndex >= 0 && colIndex < SheetGrid.Columns.Count
            ? SheetGrid.Columns[colIndex]
            : null;

        SheetGrid.SelectedIndex = rowIndex;
        if (col is not null)
        {
            SheetGrid.CurrentColumn = col;
        }
        SheetGrid.ScrollIntoView(rowItem, col);
    }
}
