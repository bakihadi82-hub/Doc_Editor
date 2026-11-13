using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.Core.Formats;
using PdfStudio.Editing.PdfSharp;
using SkiaSharp;

namespace PdfStudio.App.ViewModels;

public sealed partial class ImageWorkspaceViewModel : DocumentTabViewModel
{
    private const int MaxUndoSnapshots = 20;
    private readonly List<SKBitmap> _undoStack = [];
    private readonly List<SKBitmap> _redoStack = [];
    private bool _suppressAspectSync;

    public SKBitmap? CurrentBitmap { get; private set; }

    [ObservableProperty]
    private int _imageWidth;

    [ObservableProperty]
    private int _imageHeight;

    [ObservableProperty]
    private int _zoomPercent = 100;

    [ObservableProperty]
    private bool _isCropping;

    [ObservableProperty]
    private int _cropX;

    [ObservableProperty]
    private int _cropY;

    [ObservableProperty]
    private int _cropWidth;

    [ObservableProperty]
    private int _cropHeight;

    [ObservableProperty]
    private bool _isResizePanelOpen;

    [ObservableProperty]
    private int _resizeWidth = 800;

    [ObservableProperty]
    private int _resizeHeight = 600;

    [ObservableProperty]
    private bool _lockAspectRatio = true;

    public override bool CanUndo => _undoStack.Count > 0;
    public override bool CanRedo => _redoStack.Count > 0;

    public event Action? RequestInvalidateCanvas;
    public event Action? RequestFitToScreen;

    public ImageWorkspaceViewModel(string filePath)
        : base(filePath, DocumentFormatKind.Image)
    {
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var bmp = await ImageDocumentService.LoadAsync(OriginalFilePath, ct);
        SetBitmap(bmp, resetHistory: true);
    }

    public void LoadBitmap(SKBitmap bitmap)
    {
        SetBitmap(ImageDocumentService.CloneBitmap(bitmap), resetHistory: true);
    }

    private void SetBitmap(SKBitmap bitmap, bool resetHistory)
    {
        if (resetHistory)
        {
            ClearHistory();
            CurrentBitmap?.Dispose();
        }

        CurrentBitmap = bitmap;
        ImageWidth = bitmap.Width;
        ImageHeight = bitmap.Height;
        ResizeWidth = bitmap.Width;
        ResizeHeight = bitmap.Height;
        ResetCropRegion();
        UpdateMetadataBadges();
        RequestInvalidateCanvas?.Invoke();
    }

    partial void OnZoomPercentChanged(int value)
    {
        UpdateMetadataBadges();
        RequestInvalidateCanvas?.Invoke();
    }

    partial void OnResizeWidthChanged(int value)
    {
        if (_suppressAspectSync || !LockAspectRatio || ImageWidth <= 0 || value <= 0) return;
        _suppressAspectSync = true;
        ResizeHeight = Math.Max(1, (int)Math.Round(value * (ImageHeight / (double)ImageWidth)));
        _suppressAspectSync = false;
    }

    partial void OnResizeHeightChanged(int value)
    {
        if (_suppressAspectSync || !LockAspectRatio || ImageHeight <= 0 || value <= 0) return;
        _suppressAspectSync = true;
        ResizeWidth = Math.Max(1, (int)Math.Round(value * (ImageWidth / (double)ImageHeight)));
        _suppressAspectSync = false;
    }

    private void PushUndoAndReplace(SKBitmap nextBitmap)
    {
        if (CurrentBitmap is not null)
        {
            _undoStack.Add(CurrentBitmap);
            if (_undoStack.Count > MaxUndoSnapshots)
            {
                _undoStack[0].Dispose();
                _undoStack.RemoveAt(0);
            }
        }
        foreach (var r in _redoStack) r.Dispose();
        _redoStack.Clear();

        SetBitmap(nextBitmap, resetHistory: false);
        IsDirty = true;
        NotifyUndoRedo();
    }

    [RelayCommand]
    private void Rotate90Cw()
    {
        if (CurrentBitmap is null) return;
        PushUndoAndReplace(ImageDocumentService.Rotate90Cw(CurrentBitmap));
    }

    [RelayCommand]
    private void Rotate90Ccw()
    {
        if (CurrentBitmap is null) return;
        PushUndoAndReplace(ImageDocumentService.Rotate90Ccw(CurrentBitmap));
    }

    [RelayCommand]
    private void FlipHorizontal()
    {
        if (CurrentBitmap is null) return;
        PushUndoAndReplace(ImageDocumentService.FlipHorizontal(CurrentBitmap));
    }

    [RelayCommand]
    private void FlipVertical()
    {
        if (CurrentBitmap is null) return;
        PushUndoAndReplace(ImageDocumentService.FlipVertical(CurrentBitmap));
    }

    [RelayCommand]
    private void ToggleCropMode()
    {
        IsCropping = !IsCropping;
        if (IsCropping)
        {
            ResetCropRegion();
        }
        RequestInvalidateCanvas?.Invoke();
    }

    public void SetCropRegion(int x, int y, int width, int height)
    {
        if (CurrentBitmap is null) return;
        CropX = Math.Clamp(x, 0, Math.Max(0, ImageWidth - 1));
        CropY = Math.Clamp(y, 0, Math.Max(0, ImageHeight - 1));
        CropWidth = Math.Clamp(width, 1, Math.Max(1, ImageWidth - CropX));
        CropHeight = Math.Clamp(height, 1, Math.Max(1, ImageHeight - CropY));
        RequestInvalidateCanvas?.Invoke();
    }

    private void ResetCropRegion()
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return;
        int insetX = Math.Max(0, ImageWidth / 10);
        int insetY = Math.Max(0, ImageHeight / 10);
        CropX = insetX;
        CropY = insetY;
        CropWidth = Math.Max(1, ImageWidth - insetX * 2);
        CropHeight = Math.Max(1, ImageHeight - insetY * 2);
    }

    [RelayCommand]
    private void ApplyCrop()
    {
        if (CurrentBitmap is null) return;
        var rect = new SKRectI(CropX, CropY, CropX + CropWidth, CropY + CropHeight);
        PushUndoAndReplace(ImageDocumentService.Crop(CurrentBitmap, rect));
        IsCropping = false;
    }

    [RelayCommand]
    private void CancelCrop()
    {
        IsCropping = false;
        RequestInvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void OpenResizePanel()
    {
        ResizeWidth = ImageWidth;
        ResizeHeight = ImageHeight;
        IsResizePanelOpen = true;
    }

    [RelayCommand]
    private void ApplyResize()
    {
        if (CurrentBitmap is null) return;
        IsResizePanelOpen = false;
        if (ResizeWidth == ImageWidth && ResizeHeight == ImageHeight) return;
        PushUndoAndReplace(ImageDocumentService.Resize(CurrentBitmap, ResizeWidth, ResizeHeight));
    }

    [RelayCommand]
    private void CancelResize() => IsResizePanelOpen = false;

    [RelayCommand]
    private void ZoomIn() => ZoomPercent = Math.Clamp((int)Math.Round(ZoomPercent * 1.25), 10, 800);

    [RelayCommand]
    private void ZoomOut() => ZoomPercent = Math.Clamp((int)Math.Round(ZoomPercent / 1.25), 10, 800);

    [RelayCommand]
    private void ResetZoom() => ZoomPercent = 100;

    [RelayCommand]
    private void FitToScreen() => RequestFitToScreen?.Invoke();

    public void Undo()
    {
        if (_undoStack.Count == 0 || CurrentBitmap is null) return;
        _redoStack.Add(CurrentBitmap);
        var prev = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        SetBitmap(prev, resetHistory: false);
        IsDirty = _undoStack.Count > 0;
        NotifyUndoRedo();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0 || CurrentBitmap is null) return;
        _undoStack.Add(CurrentBitmap);
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        SetBitmap(next, resetHistory: false);
        IsDirty = true;
        NotifyUndoRedo();
    }

    public async Task SaveAsync(string targetPath, CancellationToken ct = default)
    {
        if (CurrentBitmap is null) return;
        await ImageDocumentService.SaveAtomicAsync(targetPath, CurrentBitmap, ct);
        OriginalFilePath = targetPath;
        WorkingFilePath = targetPath;
        ClearHistory();
        IsDirty = false;
        NotifyUndoRedo();
        RefreshFileMetadata();
        UpdateMetadataBadges();
    }

    public async Task ExportToPdfAsync(string targetPdfPath, CancellationToken ct = default)
    {
        if (CurrentBitmap is null) return;
        await PdfFormatExporter.ExportImageAsync(CurrentBitmap, targetPdfPath, ct);
    }

    public void UpdateMetadataBadges()
    {
        MicroMetadataBadge = $"{ImageWidth}×{ImageHeight}";
        ContextStatusText = $"{ImageWidth} × {ImageHeight} px • {FormatBadgeText} • {ZoomPercent}%";
    }

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void ClearHistory()
    {
        foreach (var b in _undoStack) b.Dispose();
        _undoStack.Clear();
        foreach (var b in _redoStack) b.Dispose();
        _redoStack.Clear();
    }

    public override ValueTask DisposeAsync()
    {
        ClearHistory();
        CurrentBitmap?.Dispose();
        CurrentBitmap = null;
        return ValueTask.CompletedTask;
    }
}
