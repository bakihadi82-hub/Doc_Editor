using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using PdfStudio.App.ViewModels;

namespace PdfStudio.App.Views.Editors;

public partial class RichDocEditorView : UserControl
{
    public RichDocEditorView()
    {
        InitializeComponent();
    }

    private void OnBlockControlGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not RichDocWorkspaceViewModel vm) return;
        if (sender is Control { DataContext: RichDocBlockViewModel block })
        {
            vm.SelectBlockCommand.Execute(block);
            if (sender is TextBox tb)
            {
                UpdateBlockSelection(block, tb);
            }
        }
    }

    private void OnBlockTextSelectionUpdated(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: RichDocBlockViewModel block } tb)
        {
            UpdateBlockSelection(block, tb);
        }
    }

    private static void UpdateBlockSelection(RichDocBlockViewModel block, TextBox tb)
    {
        int start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        int end = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        block.SelectionStart = Math.Max(0, start);
        block.SelectionLength = Math.Max(0, end - start);
    }

    private void OnTableCellGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not RichDocWorkspaceViewModel vm) return;
        if (sender is Control ctrl)
        {
            var block = ctrl.GetLogicalAncestors()
                .OfType<Control>()
                .Select(c => c.DataContext)
                .OfType<RichDocBlockViewModel>()
                .FirstOrDefault();
            if (block is not null)
            {
                vm.SelectBlockCommand.Execute(block);
            }
        }
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not RichDocWorkspaceViewModel vm) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.PreviousSearchMatch();
        }
        else
        {
            vm.NextSearchMatch();
        }
        e.Handled = true;
    }

    private void OnReplaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not RichDocWorkspaceViewModel vm) return;
        vm.ReplaceCurrentMatch(FindBox.Text ?? vm.FindQuery);
    }

    private void OnReplaceAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not RichDocWorkspaceViewModel vm) return;
        vm.ReplaceAllMatches(FindBox.Text ?? vm.FindQuery);
    }
}
