using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Forms;
using SkiaSharp;

namespace PdfStudio.App.Views;

public partial class MainWindow : Window
{
    private PageThumbnailViewModel? _dragSourceThumb;
    private Point _dragStartPoint;
    private bool _isDraggingThumbnail;
    private int _dragTargetIndex = -1;

    // Inline form field editor state
    private PdfFormField? _editingFormField;

    // Signature pad drawing state
    private readonly List<List<Point>> _signatureStrokes = [];
    private List<Point>? _activeSignatureStroke;
    private Polyline? _activeSignaturePolyline;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        PdfCanvas.RequestEditFormField += ShowInlineFormFieldEditor;
        Deactivated += (_, _) => PdfCanvas.SetSpacebarHeld(false);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PromptOnDirtyTabClose = true;
            vm.RequestOpenFilePath += PickPdfFileAsync;
            vm.RequestOpenMultipleFilePaths += PickMultiplePdfFilesAsync;
            vm.RequestSaveFilePath += PickSavePdfFileAsync;
            vm.RequestImportPngBytes += PickPngBytesAsync;
            vm.RequestClearSignaturePad += ClearSignaturePadVisuals;
            vm.RequestFocusSearchBox += FocusSearchBox;
            vm.RequestFocusPageJumpBox += FocusPageJumpBox;
            vm.RequestCopyToClipboard += CopyTextToClipboardAsync;
            vm.RequestTabCrossfade += TriggerTabCrossfade;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm && vm.IsWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsWindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                vm.WindowWidth = Width;
                vm.WindowHeight = Height;
            }
            vm.SaveSettingsSnapshot();
        }
        base.OnClosing(e);
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void FocusPageJumpBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            PageJumpTextBox.Focus();
            PageJumpTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void TriggerTabCrossfade()
    {
        if (!IsVisible) return;
        PdfCanvas.Opacity = 0.35;
        Dispatcher.UIThread.Post(() => PdfCanvas.Opacity = 1.0, DispatcherPriority.Render);
    }

    private void PageJumpTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            vm.CommitPageJumpCommand.Execute(null);
            PdfCanvas.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelPageJumpCommand.Execute(null);
            PdfCanvas.Focus();
            e.Handled = true;
        }
    }

    private void PageJumpTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.IsPageJumpEditing)
        {
            vm.CancelPageJumpCommand.Execute(null);
        }
    }

    private void FocusSearchBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            DocumentSearchBox.Focus();
            DocumentSearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.K)
        {
            vm.ToggleCommandPaletteCommand.Execute(null);
            if (vm.IsCommandPaletteOpen)
            {
                CommandSearchBox.Focus();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && DocumentSearchBox.IsFocused && vm.IsSearchBarOpen)
        {
            if (shift)
            {
                vm.PreviousSearchMatchCommand.Execute(null);
            }
            else
            {
                vm.NextSearchMatchCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        bool isTextBoxFocused = e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox;
        if (isTextBoxFocused)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.PageDown:
                vm.PageDownViewportCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageUp:
                vm.PageUpViewportCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                if (shift)
                {
                    vm.PageUpViewportCommand.Execute(null);
                }
                else
                {
                    PdfCanvas.SetSpacebarHeld(true);
                }
                e.Handled = true;
                break;
            case Key.Home:
                vm.FirstPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.End:
                vm.LastPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                if (vm.SelectedAnnotation is not null)
                {
                    vm.DeleteSelectedAnnotationCommand.Execute(null);
                }
                else
                {
                    vm.DeleteSelectedPageCommand.Execute(null);
                }
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                vm.ToggleLeftPanelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                vm.ToggleRightPanelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V:
                vm.SetSelectToolCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.H:
                vm.SetHandToolCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.U:
                vm.SetHighlightToolCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.P:
                vm.SetDrawToolCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.T:
                vm.SetTextToolCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.N:
                vm.SetNoteToolCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.R:
                vm.SetRedactToolCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            PdfCanvas.SetSpacebarHeld(false);
        }
    }

    // --- Inline Form Field Editing ---

    private void ShowInlineFormFieldEditor(PdfFormField field, Rect fieldRectDip)
    {
        _editingFormField = field;
        InlineFormEditorHost.Margin = new Thickness(Math.Max(0, fieldRectDip.X), Math.Max(0, fieldRectDip.Y), 0, 0);
        InlineFormEditorHost.Width = Math.Max(80, fieldRectDip.Width);
        InlineFormEditorHost.MinHeight = Math.Max(26, fieldRectDip.Height);
        InlineFormTextBox.Text = field.Value;
        InlineFormEditorHost.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            InlineFormTextBox.Focus();
            InlineFormTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CommitInlineFormEdit()
    {
        if (_editingFormField is not null && DataContext is MainWindowViewModel vm)
        {
            string val = InlineFormTextBox.Text ?? string.Empty;
            if (!string.Equals(_editingFormField.Value, val, StringComparison.Ordinal))
            {
                vm.ToggleOrUpdateFormField(_editingFormField, textValue: val);
            }
        }
        _editingFormField = null;
        InlineFormEditorHost.IsVisible = false;
    }

    private void InlineFormTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
        => CommitInlineFormEdit();

    private void InlineFormTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitInlineFormEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _editingFormField = null;
            InlineFormEditorHost.IsVisible = false;
            e.Handled = true;
        }
    }

    // --- Signature Modal Drawing Pad ---

    private void SignatureBackdrop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseSignatureModalCommand.Execute(null);
        }
    }

    private void ClearSignaturePadVisuals()
    {
        _signatureStrokes.Clear();
        _activeSignatureStroke = null;
        _activeSignaturePolyline = null;
        SignaturePadCanvas.Children.Clear();
    }

    private void SignaturePad_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SignaturePadCanvas).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(SignaturePadCanvas);
        _activeSignatureStroke = [pos];
        _signatureStrokes.Add(_activeSignatureStroke);

        _activeSignaturePolyline = new Polyline
        {
            Stroke = Brushes.Black,
            StrokeThickness = 2.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Points = [pos]
        };
        SignaturePadCanvas.Children.Add(_activeSignaturePolyline);
        e.Pointer.Capture(SignaturePadCanvas);
        e.Handled = true;
    }

    private void SignaturePad_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeSignatureStroke is null || _activeSignaturePolyline is null) return;
        var pos = e.GetPosition(SignaturePadCanvas);
        var clamped = new Point(
            Math.Clamp(pos.X, 0, SignaturePadCanvas.Bounds.Width),
            Math.Clamp(pos.Y, 0, SignaturePadCanvas.Bounds.Height));
        _activeSignatureStroke.Add(clamped);
        _activeSignaturePolyline.Points.Add(clamped);
    }

    private void SignaturePad_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured == SignaturePadCanvas)
        {
            e.Pointer.Capture(null);
        }
        _activeSignatureStroke = null;
        _activeSignaturePolyline = null;
    }

    private void PlaceDrawnSignature_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _signatureStrokes.Count == 0) return;

        int width = Math.Max(1, (int)Math.Round(SignaturePadCanvas.Bounds.Width > 1 ? SignaturePadCanvas.Bounds.Width : 440));
        int height = Math.Max(1, (int)Math.Round(SignaturePadCanvas.Bounds.Height > 1 ? SignaturePadCanvas.Bounds.Height : 180));

        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint
        {
            Color = new SKColor(0x11, 0x18, 0x27, 0xFF),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        foreach (var stroke in _signatureStrokes)
        {
            if (stroke.Count < 2) continue;
            using var path = new SKPath();
            path.MoveTo((float)stroke[0].X, (float)stroke[0].Y);
            for (int i = 1; i < stroke.Count; i++)
            {
                path.LineTo((float)stroke[i].X, (float)stroke[i].Y);
            }
            canvas.DrawPath(path, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        vm.ApplySignatureBytes(data.ToArray());
        ClearSignaturePadVisuals();
    }

    private void OpenPasswordBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
        {
            vm.SubmitOpenPasswordCommand.Execute(null);
            e.Handled = true;
        }
    }

    // --- Page Organizer Thumbnail Drag-and-Drop Reordering ---

    private void ThumbnailCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control ctrl && ctrl.DataContext is PageThumbnailViewModel thumb &&
            e.GetCurrentPoint(ctrl).Properties.IsLeftButtonPressed)
        {
            _dragSourceThumb = thumb;
            _dragStartPoint = e.GetPosition(ThumbnailItemsControl);
            _isDraggingThumbnail = false;
            _dragTargetIndex = -1;
            e.Pointer.Capture(ctrl);
        }
    }

    private void ThumbnailCard_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSourceThumb is null || DataContext is not MainWindowViewModel vm) return;

        var pos = e.GetPosition(ThumbnailItemsControl);
        if (!_isDraggingThumbnail)
        {
            var delta = pos - _dragStartPoint;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6.0)
            {
                return;
            }
            _isDraggingThumbnail = true;
        }

        UpdateDropIndicator(vm, pos);
    }

    private void ThumbnailCard_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control ctrl && e.Pointer.Captured == ctrl)
        {
            e.Pointer.Capture(null);
        }

        if (_dragSourceThumb is null || DataContext is not MainWindowViewModel vm)
        {
            ClearDropIndicators(DataContext as MainWindowViewModel);
            _dragSourceThumb = null;
            _isDraggingThumbnail = false;
            _dragTargetIndex = -1;
            return;
        }

        var source = _dragSourceThumb;
        bool wasDragging = _isDraggingThumbnail;
        int targetIdx = _dragTargetIndex;

        ClearDropIndicators(vm);
        _dragSourceThumb = null;
        _isDraggingThumbnail = false;
        _dragTargetIndex = -1;

        if (!wasDragging)
        {
            vm.SelectThumbnailCommand.Execute(source);
        }
        else if (targetIdx >= 0 && targetIdx != source.DisplayIndex)
        {
            vm.MovePage(source.DisplayIndex, targetIdx);
        }
    }

    private void UpdateDropIndicator(MainWindowViewModel vm, Point posInItemsControl)
    {
        ClearDropIndicators(vm);
        if (_dragSourceThumb is null || vm.PageThumbnails.Count == 0) return;

        int sourceIdx = _dragSourceThumb.DisplayIndex;
        int bestTarget = sourceIdx;
        double bestDist = double.MaxValue;
        bool insertAfter = false;

        foreach (var thumb in vm.PageThumbnails)
        {
            var container = ThumbnailItemsControl.ContainerFromIndex(thumb.DisplayIndex);
            if (container is null) continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), ThumbnailItemsControl);
            if (topLeft is null) continue;

            double top = topLeft.Value.Y;
            double bottom = top + container.Bounds.Height;
            double mid = (top + bottom) * 0.5;
            double dist = Math.Abs(posInItemsControl.Y - mid);

            if (dist < bestDist)
            {
                bestDist = dist;
                insertAfter = posInItemsControl.Y > mid;
                bestTarget = thumb.DisplayIndex;
            }
        }

        int finalIndex = bestTarget;
        if (insertAfter && bestTarget < sourceIdx)
        {
            finalIndex = bestTarget + 1;
        }
        else if (!insertAfter && bestTarget > sourceIdx)
        {
            finalIndex = bestTarget - 1;
        }

        _dragTargetIndex = Math.Clamp(finalIndex, 0, vm.PageThumbnails.Count - 1);
        if (_dragTargetIndex != sourceIdx)
        {
            if (insertAfter)
            {
                vm.PageThumbnails[bestTarget].IsDropTargetAfter = true;
            }
            else
            {
                vm.PageThumbnails[bestTarget].IsDropTargetBefore = true;
            }
        }
    }

    private static void ClearDropIndicators(MainWindowViewModel? vm)
    {
        if (vm is null) return;
        foreach (var item in vm.PageThumbnails)
        {
            item.IsDropTargetBefore = false;
            item.IsDropTargetAfter = false;
        }
    }

    // --- File Pickers & Drag-Drop ---

    private async Task<string?> PickPdfFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<IReadOnlyList<string>?> PickMultiplePdfFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Merge PDFs",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }
            ]
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        return paths.Count > 0 ? paths : null;
    }

    private async Task<string?> PickSavePdfFileAsync(string suggestedFileName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PDF",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "pdf",
            FileTypeChoices =
            [
                new FilePickerFileType("PDF File") { Patterns = ["*.pdf"] }
            ]
        });

        return file?.TryGetLocalPath();
    }

    private async Task<byte[]?> PickPngBytesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Signature",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PNG Images") { Patterns = ["*.png"] }
            ]
        });

        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        foreach (var file in files)
        {
            string? path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                await vm.LoadFileAsync(path);
            }
        }
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Border or Grid or TextBlock)
        {
            BeginMoveDrag(e);
        }
    }

    private void TitleBar_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Border or Grid or TextBlock)
        {
            ToggleMaximize();
        }
    }

    private void Canvas_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && !vm.HasOpenDocument && sender is Control ctrl)
        {
            var pt = e.GetPosition(ctrl);
            double scale = vm.ZoomPercent / 100.0;
            vm.CursorXPt = Math.Round(pt.X * 72.0 / (96.0 * scale), 1);
            vm.CursorYPt = Math.Round(pt.Y * 72.0 / (96.0 * scale), 1);
        }
    }

    private void ModalBackdrop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseCommandPaletteCommand.Execute(null);
        }
    }

    private void ThemePickerBackdrop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseThemePickerCommand.Execute(null);
        }
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
}
