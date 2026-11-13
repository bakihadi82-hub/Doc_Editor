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
        }
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
}
