using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Annotations;

namespace PdfStudio.App.ViewModels;

/// <summary>
/// Manages PDF annotation/editing tool selection, stroke/color/font properties,
/// and the currently selected annotation's editable state.
/// </summary>
public sealed partial class AnnotationToolbarViewModel : ObservableObject
{
    private readonly Action<PdfToolKind>? _onToolChanged;
    private readonly Action? _onAnnotationTextMutated;
    private readonly Action? _onInvalidateCanvas;
    private bool _syncingSelectedText;

    [ObservableProperty]
    private PdfToolKind _activeTool = PdfToolKind.Select;

    [ObservableProperty]
    private string _activeToolColorHex = "#FACC15";

    [ObservableProperty]
    private double _activeStrokeWidthPt = 2.0;

    [ObservableProperty]
    private double _activeFontSizePt = 12.0;

    [ObservableProperty]
    private PdfAnnotation? _selectedAnnotation;

    [ObservableProperty]
    private string _selectedAnnotationText = string.Empty;

    public bool IsSelectTool => ActiveTool == PdfToolKind.Select;
    public bool IsHandTool => ActiveTool == PdfToolKind.Hand;
    public bool IsHighlightTool => ActiveTool == PdfToolKind.Highlight;
    public bool IsUnderlineTool => ActiveTool == PdfToolKind.Underline;
    public bool IsDrawTool => ActiveTool == PdfToolKind.Draw;
    public bool IsTextTool => ActiveTool == PdfToolKind.Text;
    public bool IsNoteTool => ActiveTool == PdfToolKind.Note;
    public bool IsRedactTool => ActiveTool == PdfToolKind.Redact;
    public bool IsEditTextTool => ActiveTool == PdfToolKind.EditText;
    public bool IsAnnotationToolActive => ActiveTool is PdfToolKind.Highlight or PdfToolKind.Underline or PdfToolKind.Draw or PdfToolKind.Text or PdfToolKind.Note;
    public bool IsStrokeToolActive => ActiveTool is PdfToolKind.Highlight or PdfToolKind.Underline or PdfToolKind.Draw;
    public bool IsTextOrNoteToolActive => ActiveTool is PdfToolKind.Text or PdfToolKind.Note;

    public bool IsToolColorYellow => string.Equals(ActiveToolColorHex, "#FACC15", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorBlue => string.Equals(ActiveToolColorHex, "#3B82F6", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorRed => string.Equals(ActiveToolColorHex, "#EF4444", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorGreen => string.Equals(ActiveToolColorHex, "#10B981", StringComparison.OrdinalIgnoreCase);
    public bool IsToolColorPurple => string.Equals(ActiveToolColorHex, "#A855F7", StringComparison.OrdinalIgnoreCase);

    public bool IsStroke1Pt => Math.Abs(ActiveStrokeWidthPt - 1.0) < 0.1;
    public bool IsStroke2Pt => Math.Abs(ActiveStrokeWidthPt - 2.0) < 0.1;
    public bool IsStroke4Pt => Math.Abs(ActiveStrokeWidthPt - 4.0) < 0.1;

    public bool IsFontSize10 => Math.Abs(ActiveFontSizePt - 10.0) < 0.1;
    public bool IsFontSize12 => Math.Abs(ActiveFontSizePt - 12.0) < 0.1;
    public bool IsFontSize16 => Math.Abs(ActiveFontSizePt - 16.0) < 0.1;
    public bool IsFontSize20 => Math.Abs(ActiveFontSizePt - 20.0) < 0.1;

    public bool HasSelectedAnnotation => SelectedAnnotation is not null;
    public bool HasSelectedEditableText => SelectedAnnotation is { Kind: AnnotationKind.TextBox or AnnotationKind.StickyNote };

    public AnnotationToolbarViewModel(
        Action<PdfToolKind>? onToolChanged = null,
        Action? onAnnotationTextMutated = null,
        Action? onInvalidateCanvas = null)
    {
        _onToolChanged = onToolChanged;
        _onAnnotationTextMutated = onAnnotationTextMutated;
        _onInvalidateCanvas = onInvalidateCanvas;
    }

    partial void OnActiveToolChanged(PdfToolKind value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsHandTool));
        OnPropertyChanged(nameof(IsHighlightTool));
        OnPropertyChanged(nameof(IsUnderlineTool));
        OnPropertyChanged(nameof(IsDrawTool));
        OnPropertyChanged(nameof(IsTextTool));
        OnPropertyChanged(nameof(IsNoteTool));
        OnPropertyChanged(nameof(IsRedactTool));
        OnPropertyChanged(nameof(IsEditTextTool));
        OnPropertyChanged(nameof(IsAnnotationToolActive));
        OnPropertyChanged(nameof(IsStrokeToolActive));
        OnPropertyChanged(nameof(IsTextOrNoteToolActive));
        _onToolChanged?.Invoke(value);
        _onInvalidateCanvas?.Invoke();
    }

    partial void OnActiveToolColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(IsToolColorYellow));
        OnPropertyChanged(nameof(IsToolColorBlue));
        OnPropertyChanged(nameof(IsToolColorRed));
        OnPropertyChanged(nameof(IsToolColorGreen));
        OnPropertyChanged(nameof(IsToolColorPurple));
    }

    partial void OnActiveStrokeWidthPtChanged(double value)
    {
        OnPropertyChanged(nameof(IsStroke1Pt));
        OnPropertyChanged(nameof(IsStroke2Pt));
        OnPropertyChanged(nameof(IsStroke4Pt));
    }

    partial void OnActiveFontSizePtChanged(double value)
    {
        OnPropertyChanged(nameof(IsFontSize10));
        OnPropertyChanged(nameof(IsFontSize12));
        OnPropertyChanged(nameof(IsFontSize16));
        OnPropertyChanged(nameof(IsFontSize20));
    }

    partial void OnSelectedAnnotationChanged(PdfAnnotation? value)
    {
        _syncingSelectedText = true;
        try
        {
            SelectedAnnotationText = value?.TextContent ?? string.Empty;
        }
        finally
        {
            _syncingSelectedText = false;
        }

        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedEditableText));
        _onInvalidateCanvas?.Invoke();
    }

    partial void OnSelectedAnnotationTextChanged(string value)
    {
        if (_syncingSelectedText) return;

        if (SelectedAnnotation is not null &&
            SelectedAnnotation.Kind is AnnotationKind.TextBox or AnnotationKind.StickyNote &&
            !string.Equals(SelectedAnnotation.TextContent, value, StringComparison.Ordinal))
        {
            SelectedAnnotation.TextContent = value;
            _onAnnotationTextMutated?.Invoke();
            _onInvalidateCanvas?.Invoke();
        }
    }

    public void SetToolColor(string? hex, Action beginSnapshot, Action commitTransform)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        ActiveToolColorHex = hex;
        if (SelectedAnnotation is not null)
        {
            beginSnapshot();
            SelectedAnnotation.ColorHex = hex;
            commitTransform();
        }
    }

    public void SetStrokeWidth(object? widthArg, Action beginSnapshot, Action commitTransform)
    {
        if (!TryParseDoubleArg(widthArg, out double width)) return;
        ActiveStrokeWidthPt = Math.Clamp(width, 0.5, 16.0);
        if (SelectedAnnotation is not null)
        {
            beginSnapshot();
            SelectedAnnotation.StrokeWidthPt = ActiveStrokeWidthPt;
            commitTransform();
        }
    }

    public void SetFontSize(object? sizeArg, Action beginSnapshot, Action commitTransform)
    {
        if (!TryParseDoubleArg(sizeArg, out double size)) return;
        ActiveFontSizePt = Math.Clamp(size, 6.0, 48.0);
        if (SelectedAnnotation is not null &&
            SelectedAnnotation.Kind is AnnotationKind.TextBox or AnnotationKind.StickyNote)
        {
            beginSnapshot();
            SelectedAnnotation.FontSizePt = ActiveFontSizePt;
            commitTransform();
        }
    }

    public static bool TryParseDoubleArg(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }
}
