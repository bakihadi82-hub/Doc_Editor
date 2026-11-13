using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Editing;

namespace PdfStudio.App.ViewModels;

public sealed partial class DocumentTabViewModel : ObservableObject
{
    public string OriginalFilePath { get; set; }
    public string WorkingFilePath { get; set; }
    public string? Password { get; set; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isActive;

    public StructuralEditPlan EditPlan { get; set; } = new();
    public Stack<(string WorkingPath, StructuralEditPlan Plan)> UndoStack { get; } = new();
    public Stack<(string WorkingPath, StructuralEditPlan Plan)> RedoStack { get; } = new();
    public int CurrentPage { get; set; } = 1;
    public int ZoomPercent { get; set; } = 100;

    public DocumentTabViewModel(string filePath)
    {
        OriginalFilePath = filePath;
        WorkingFilePath = filePath;
        _title = Path.GetFileName(filePath);
    }
}
