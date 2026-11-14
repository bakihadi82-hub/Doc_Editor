using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Forms;

namespace PdfStudio.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public AnnotationToolbarViewModel AnnotationToolbar { get; }

    public PdfToolKind ActiveTool
    {
        get => AnnotationToolbar.ActiveTool;
        set => AnnotationToolbar.ActiveTool = value;
    }

    public string ActiveToolColorHex
    {
        get => AnnotationToolbar.ActiveToolColorHex;
        set => AnnotationToolbar.ActiveToolColorHex = value;
    }

    public double ActiveStrokeWidthPt
    {
        get => AnnotationToolbar.ActiveStrokeWidthPt;
        set => AnnotationToolbar.ActiveStrokeWidthPt = value;
    }

    public double ActiveFontSizePt
    {
        get => AnnotationToolbar.ActiveFontSizePt;
        set => AnnotationToolbar.ActiveFontSizePt = value;
    }

    public PdfAnnotation? SelectedAnnotation
    {
        get => AnnotationToolbar.SelectedAnnotation;
        set => AnnotationToolbar.SelectedAnnotation = value;
    }

    public string SelectedAnnotationText
    {
        get => AnnotationToolbar.SelectedAnnotationText;
        set => AnnotationToolbar.SelectedAnnotationText = value;
    }

    public bool IsSelectTool => AnnotationToolbar.IsSelectTool;
    public bool IsHandTool => AnnotationToolbar.IsHandTool;
    public bool IsHighlightTool => AnnotationToolbar.IsHighlightTool;
    public bool IsUnderlineTool => AnnotationToolbar.IsUnderlineTool;
    public bool IsDrawTool => AnnotationToolbar.IsDrawTool;
    public bool IsTextTool => AnnotationToolbar.IsTextTool;
    public bool IsNoteTool => AnnotationToolbar.IsNoteTool;
    public bool IsRedactTool => AnnotationToolbar.IsRedactTool;
    public bool IsEditTextTool => AnnotationToolbar.IsEditTextTool;
    public bool IsAnnotationToolActive => AnnotationToolbar.IsAnnotationToolActive;
    public bool IsStrokeToolActive => AnnotationToolbar.IsStrokeToolActive;
    public bool IsTextOrNoteToolActive => AnnotationToolbar.IsTextOrNoteToolActive;
    public bool IsToolColorYellow => AnnotationToolbar.IsToolColorYellow;
    public bool IsToolColorBlue => AnnotationToolbar.IsToolColorBlue;
    public bool IsToolColorRed => AnnotationToolbar.IsToolColorRed;
    public bool IsToolColorGreen => AnnotationToolbar.IsToolColorGreen;
    public bool IsToolColorPurple => AnnotationToolbar.IsToolColorPurple;
    public bool IsStroke1Pt => AnnotationToolbar.IsStroke1Pt;
    public bool IsStroke2Pt => AnnotationToolbar.IsStroke2Pt;
    public bool IsStroke4Pt => AnnotationToolbar.IsStroke4Pt;
    public bool IsFontSize10 => AnnotationToolbar.IsFontSize10;
    public bool IsFontSize12 => AnnotationToolbar.IsFontSize12;
    public bool IsFontSize16 => AnnotationToolbar.IsFontSize16;
    public bool IsFontSize20 => AnnotationToolbar.IsFontSize20;
    public bool HasSelectedAnnotation => AnnotationToolbar.HasSelectedAnnotation;
    public bool HasSelectedEditableText => AnnotationToolbar.HasSelectedEditableText;

    public IReadOnlyList<PdfAnnotation> Annotations => ActivePdfTab?.EditPlan.Annotations ?? (IReadOnlyList<PdfAnnotation>)[];
    public int PendingRedactionCount => ActivePdfTab?.EditPlan.Annotations.Count(a => a.Kind == AnnotationKind.Redaction) ?? 0;
    public bool HasPendingRedactions => PendingRedactionCount > 0;
    public bool HasFormFields => FormFields.Count > 0;

    public bool IsPdfDarkInvertEnabled
    {
        get => ActivePdfTab?.IsDarkInvertEnabled ?? false;
        set
        {
            if (ActivePdfTab is null || ActivePdfTab.IsDarkInvertEnabled == value) return;
            ActivePdfTab.IsDarkInvertEnabled = value;
            OnPropertyChanged();
            RequestInvalidateCanvas?.Invoke();
        }
    }

    private void OnAnnotationToolbarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    [RelayCommand]
    private void TogglePdfDarkInvert() => IsPdfDarkInvertEnabled = !IsPdfDarkInvertEnabled;

    [RelayCommand]
    private void SetToolColor(string? hex)
        => AnnotationToolbar.SetToolColor(hex, BeginAnnotationTransformSnapshot, CommitAnnotationTransform);

    [RelayCommand]
    private void SetStrokeWidth(object? widthArg)
        => AnnotationToolbar.SetStrokeWidth(widthArg, BeginAnnotationTransformSnapshot, CommitAnnotationTransform);

    [RelayCommand]
    private void SetFontSize(object? sizeArg)
        => AnnotationToolbar.SetFontSize(sizeArg, BeginAnnotationTransformSnapshot, CommitAnnotationTransform);

    [RelayCommand]
    private void SetSelectTool() => ActiveTool = PdfToolKind.Select;

    [RelayCommand]
    private void SetHandTool() => ActiveTool = PdfToolKind.Hand;

    [RelayCommand]
    private void SetHighlightTool() => ActiveTool = PdfToolKind.Highlight;

    [RelayCommand]
    private void SetUnderlineTool() => ActiveTool = PdfToolKind.Underline;

    [RelayCommand]
    private void SetDrawTool() => ActiveTool = PdfToolKind.Draw;

    [RelayCommand]
    private void SetTextTool() => ActiveTool = PdfToolKind.Text;

    [RelayCommand]
    private void SetNoteTool() => ActiveTool = PdfToolKind.Note;

    [RelayCommand]
    private void SetRedactTool() => ActiveTool = PdfToolKind.Redact;

    [RelayCommand]
    private void SetEditTextTool() => ActiveTool = PdfToolKind.EditText;

    public void AddAnnotation(PdfAnnotation annotation)
    {
        if (!HasOpenDocument || ActivePdfTab is null) return;

        PushUndoSnapshot(ActivePdfTab);
        ActivePdfTab.EditPlan.Annotations.Add(annotation);
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        SelectedAnnotation = annotation;
        NotifyAnnotationStateChanged();
        RequestInvalidateCanvas?.Invoke();
    }

    public void BeginAnnotationTransformSnapshot()
    {
        if (HasOpenDocument && ActivePdfTab is not null)
        {
            PushUndoSnapshot(ActivePdfTab);
        }
    }

    public void CommitAnnotationTransform()
    {
        if (ActivePdfTab is null) return;
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        RequestInvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void DeleteSelectedAnnotation()
    {
        if (!HasOpenDocument || ActivePdfTab is null || SelectedAnnotation is null) return;

        var toRemove = SelectedAnnotation;
        PushUndoSnapshot(ActivePdfTab);
        ActivePdfTab.EditPlan.Annotations.RemoveAll(a => a.Id == toRemove.Id);
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        SelectedAnnotation = null;
        NotifyAnnotationStateChanged();
        RequestInvalidateCanvas?.Invoke();
    }

    public void ToggleOrUpdateFormField(PdfFormField field, bool? isChecked = null, string? textValue = null)
    {
        if (!HasOpenDocument || ActivePdfTab is null || field.IsReadOnly) return;

        PushUndoSnapshot(ActivePdfTab);
        if (isChecked.HasValue)
        {
            field.IsChecked = isChecked.Value;
        }
        if (textValue is not null)
        {
            field.Value = textValue;
        }
        ActivePdfTab.EditPlan.HasChanges = true;
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(FormFields));
        RequestInvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private async Task ApplyRedactionsAndSaveAsync()
    {
        if (!HasOpenDocument || ActivePdfTab is null) return;
        var options = new PdfSaveOptions(
            UserPassword: ProtectWithPassword && !string.IsNullOrWhiteSpace(DocumentPassword) ? DocumentPassword : ActivePdfTab.Password,
            OwnerPassword: ProtectWithPassword && !string.IsNullOrWhiteSpace(DocumentPassword) ? DocumentPassword : ActivePdfTab.Password,
            UseAes256: true,
            FlattenForms: FlattenFormsOnSave,
            BurnRedactions: true,
            SourcePassword: ActivePdfTab.Password);

        try
        {
            await SaveToPathAsync(ActivePdfTab.OriginalFilePath, options);
            StatusMessage = "Redactions applied";
            ShowToast("Redactions applied");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not apply redactions: {ex.Message}";
            ShowErrorDialog($"Redaction failed — the PDF was not saved because underlying content could not be safely stripped:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void HighlightAtContext()
    {
        if (!HasOpenDocument || Pages.Count == 0) return;
        int pageIdx = Math.Clamp(ContextPageIndex, 0, Pages.Count - 1);
        var page = Pages[pageIdx];
        double left = Math.Clamp(ContextPagePointPt.XPt - 60, 8, Math.Max(8, page.WidthPoints - 130));
        double top = Math.Clamp(ContextPagePointPt.YPt - 10, 8, Math.Max(8, page.HeightPoints - 24));

        var highlight = new PdfAnnotation
        {
            PageIndex = pageIdx,
            Kind = AnnotationKind.Highlight,
            BoundsPt = new PdfRect(left, top, left + 120, top + 18),
            ColorHex = ActiveToolColorHex,
            StrokeWidthPt = ActiveStrokeWidthPt
        };
        AddAnnotation(highlight);
    }

    [RelayCommand]
    private void AddNoteAtContext()
    {
        if (!HasOpenDocument || Pages.Count == 0) return;
        int pageIdx = Math.Clamp(ContextPageIndex, 0, Pages.Count - 1);
        var page = Pages[pageIdx];
        double left = Math.Clamp(ContextPagePointPt.XPt, 8, Math.Max(8, page.WidthPoints - 138));
        double top = Math.Clamp(ContextPagePointPt.YPt, 8, Math.Max(8, page.HeightPoints - 64));

        var note = new PdfAnnotation
        {
            PageIndex = pageIdx,
            Kind = AnnotationKind.StickyNote,
            BoundsPt = new PdfRect(left, top, left + 130, top + 56),
            ColorHex = "#CA8A04",
            FontSizePt = ActiveFontSizePt,
            TextContent = "Note"
        };
        AddAnnotation(note);
        IsRightPanelOpen = true;
    }

    private void NotifyAnnotationStateChanged()
    {
        OnPropertyChanged(nameof(Annotations));
        OnPropertyChanged(nameof(PendingRedactionCount));
        OnPropertyChanged(nameof(HasPendingRedactions));
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedEditableText));
    }

    private void SyncFormFieldsFromPlan()
    {
        FormFields.Clear();
        if (ActivePdfTab is not null)
        {
            foreach (var f in ActivePdfTab.EditPlan.FormFields)
            {
                FormFields.Add(f);
            }
        }
        OnPropertyChanged(nameof(HasFormFields));
    }
}
