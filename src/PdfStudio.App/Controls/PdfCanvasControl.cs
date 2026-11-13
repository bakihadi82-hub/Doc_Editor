using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Forms;
using PdfStudio.Core.Rendering;
using SkiaSharp;

namespace PdfStudio.App.Controls;

/// <summary>
/// Interactive multi-page PDF canvas supporting physical-pixel crisp rendering,
/// inertial zoom, custom pill scrollbars, search highlights, annotations,
/// true redaction previews, interactive form fields, and signature stamps.
/// </summary>
public sealed class PdfCanvasControl : Control
{
    private const double PageGapDip = 16.0;
    private const double ViewportMarginDip = 24.0;
    private const double BottomToolbarClearanceDip = 76.0;
    private const double MinZoomPercent = 25.0;
    private const double MaxZoomPercent = 400.0;

    // Pill scrollbar geometry & motion specs (6px -> 8px hover, 120ms transitions, 350ms auto-collapse)
    private const double ScrollBarRestThickness = 6.0;
    private const double ScrollBarHoverThickness = 8.0;
    private const double ScrollBarMargin = 4.0;
    private const double ScrollBarHitMargin = 16.0;
    private const double MinThumbLengthDip = 32.0;

    private MainWindowViewModel? _vm;
    private double _scrollOffsetX;
    private double _scrollOffsetY;
    private double _animatedZoom = 100.0;
    private double _targetZoom = 100.0;
    private readonly DispatcherTimer _scrollTimer;
    private readonly DispatcherTimer _scrollbarHideTimer;
    private readonly DispatcherTimer _scrollbarAnimTimer;
    private double _targetScrollY;
    private bool _isSmoothScrolling;

    // Custom pill scrollbar visual state
    private double _scrollbarOpacity;
    private double _scrollbarTargetOpacity;
    private double _vBarThickness = ScrollBarRestThickness;
    private double _vBarTargetThickness = ScrollBarRestThickness;
    private double _hBarThickness = ScrollBarRestThickness;
    private double _hBarTargetThickness = ScrollBarRestThickness;
    private bool _isHoveringVBar;
    private bool _isHoveringHBar;
    private bool _isDraggingVThumb;
    private bool _isDraggingHThumb;
    private double _thumbDragGrabOffset;

    // Panning & annotation interaction state
    private bool _isPanning;
    private bool _isSpacebarHeld;
    private Point _lastPanPoint;
    private readonly Dictionary<BitmapCacheKey, CancellationTokenSource> _inFlight = new();
    private readonly Dictionary<Guid, SKBitmap> _signatureBitmapCache = new();

    private enum DragInteractionMode
    {
        None,
        CreateRectAnnotation,
        FreehandDraw,
        MoveAnnotation,
        ResizeAnnotation,
        SelectText
    }

    private DragInteractionMode _interactionMode;
    private int _interactionPageIndex = -1;
    private PdfPoint _interactionStartPt;
    private PdfAnnotation? _draftAnnotation;
    private PdfAnnotation? _manipulatedAnnotation;
    private PdfRect _initialBoundsPt = new(0, 0, 0, 0);
    private List<PdfPoint>? _initialInkPointsPt;
    private bool _hasModifiedManipulatedAnnotation;

    // Text selection & EditText hover state
    private readonly HashSet<int> _textLayoutLoading = [];
    private int _selAnchorPageIndex = -1;
    private int _selAnchorCharIndex = -1;
    private int _selFocusPageIndex = -1;
    private int _selFocusCharIndex = -1;
    private bool _selDidDrag;
    private bool _selWasMultiClick;
    private int _hoveredTextBlockPageIndex = -1;
    private PdfTextBlock? _hoveredTextBlock;

    public double ScrollOffsetX => _scrollOffsetX;
    public double ScrollOffsetY => _scrollOffsetY;
    public Vector Offset => new(_scrollOffsetX, _scrollOffsetY);
    public double CurrentZoom => _animatedZoom;
    public double MaxScrollX { get; private set; }
    public double MaxScrollY { get; private set; }

    public event Action<PdfFormField, Rect>? RequestEditFormField;
    public event Action<PdfAnnotation>? RequestFocusAnnotationEditor;

    private sealed record PageSlot(
        int PageIndex,
        PdfPageInfo Info,
        double TopDip,
        double LeftDip,
        double WidthDip,
        double HeightDip);

    private sealed record VisiblePageRenderItem(
        int PageIndex,
        PdfPageInfo Info,
        Rect DestRectDip,
        BitmapCacheKey ExactKey,
        IReadOnlyList<PdfTextMatch> Matches,
        PdfTextMatch? ActiveMatch,
        IReadOnlyList<PdfAnnotation> Annotations,
        IReadOnlyList<PdfFormField> FormFields,
        Guid? SelectedAnnotationId,
        IReadOnlyList<PdfRect> SelectionBoxesPt,
        IReadOnlyList<PdfTextEdit> TextEdits,
        PdfRect? HoveredTextBlockBoundsPt);

    public PdfCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += OnScrollTimerTick;

        _scrollbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _scrollbarHideTimer.Tick += OnScrollbarHideTimerTick;

        _scrollbarAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollbarAnimTimer.Tick += OnScrollbarAnimTimerTick;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.RequestScrollToPage -= ScrollToPage;
            _vm.RequestScrollToMatch -= ScrollToMatch;
            _vm.RequestScrollViewportByFraction -= ScrollViewportByFraction;
            _vm.RequestFitToWidth -= FitToWidth;
            _vm.RequestFitToPage -= FitToPage;
            _vm.RequestInvalidateCanvas -= InvalidateAndSchedule;
            _vm.RequestTargetZoom -= AnimateToZoom;
            _vm.RequestShowPdfSelectionUpdated -= OnPdfSelectionUpdated;
        }

        base.OnDataContextChanged(e);

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.RequestScrollToPage += ScrollToPage;
            _vm.RequestScrollToMatch += ScrollToMatch;
            _vm.RequestScrollViewportByFraction += ScrollViewportByFraction;
            _vm.RequestFitToWidth += FitToWidth;
            _vm.RequestFitToPage += FitToPage;
            _vm.RequestInvalidateCanvas += InvalidateAndSchedule;
            _vm.RequestTargetZoom += AnimateToZoom;
            _vm.RequestShowPdfSelectionUpdated += OnPdfSelectionUpdated;
            _animatedZoom = _vm.ZoomPercent;
            _targetZoom = _vm.ZoomPercent;
            UpdateCursorForTool();
            InvalidateAndSchedule();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scrollTimer.Stop();
        _scrollbarHideTimer.Stop();
        _scrollbarAnimTimer.Stop();
        CancelAllInFlight();
        ClearSignatureCache();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScrollOffsets();
        InvalidateAndSchedule();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.HasOpenDocument)
            or nameof(MainWindowViewModel.IsDarkMode)
            or nameof(MainWindowViewModel.SelectedTheme)
            or nameof(MainWindowViewModel.SelectedAnnotation))
        {
            InvalidateAndSchedule();
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.ActiveTool) or nameof(MainWindowViewModel.IsHandTool))
        {
            UpdateCursorForTool();
        }
    }

    public void SetSpacebarHeld(bool isHeld)
    {
        if (_isSpacebarHeld == isHeld) return;
        _isSpacebarHeld = isHeld;
        UpdateCursorForTool();
    }

    private void UpdateCursorForTool()
    {
        if (_vm is null) return;
        if (_isPanning)
        {
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return;
        }
        if (_isSpacebarHeld)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }
        Cursor = _vm.ActiveTool switch
        {
            PdfToolKind.Hand => new Cursor(StandardCursorType.Hand),
            PdfToolKind.Select => new Cursor(StandardCursorType.Arrow),
            PdfToolKind.EditText => new Cursor(StandardCursorType.Arrow),
            PdfToolKind.Text or PdfToolKind.Note => new Cursor(StandardCursorType.Ibeam),
            _ => new Cursor(StandardCursorType.Cross)
        };
    }

    private void OnPdfSelectionUpdated()
    {
        if (_vm?.ActivePdfTab?.ActiveSelection is null)
        {
            _selAnchorPageIndex = -1;
            _selAnchorCharIndex = -1;
            _selFocusPageIndex = -1;
            _selFocusCharIndex = -1;
        }
        InvalidateVisual();
    }

    private async Task EnsurePageTextLayoutAsync(int displayPageIndex)
    {
        if (_vm is null || !_textLayoutLoading.Add(displayPageIndex)) return;
        try
        {
            await _vm.GetOrLoadPageTextLayoutAsync(displayPageIndex);
        }
        catch
        {
            // Ignore transient close/dispose races
        }
        finally
        {
            _textLayoutLoading.Remove(displayPageIndex);
        }
    }

    private void InvalidateAndSchedule()
    {
        ClampScrollOffsets();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
    }

    public void ScrollToPage(int zeroBasedPageIndex)
    {
        if (_vm is null || _vm.Pages.Count == 0) return;

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        int idx = Math.Clamp(zeroBasedPageIndex, 0, slots.Count - 1);
        var slot = slots[idx];

        _scrollTimer.Stop();
        _isSmoothScrolling = false;
        _scrollOffsetY = Math.Max(0, slot.TopDip - ViewportMarginDip);
        ClampScrollOffsets();
        WakeScrollbars();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
        SyncOverlayPositions();
    }

    public void ScrollToMatch(PdfTextMatch match)
    {
        if (_vm is null || _vm.Pages.Count == 0) return;
        if (match.Bounds.Count == 0)
        {
            ScrollToPage(match.PageIndex);
            return;
        }

        var slots = ComputeLayout(_animatedZoom, out double totalW, out double totalH);
        int idx = Math.Clamp(match.PageIndex, 0, slots.Count - 1);
        var slot = slots[idx];

        var firstBox = match.Bounds[0];
        double midPtY = (firstBox.Top + firstBox.Bottom) * 0.5;
        double offsetInPageDip = ((slot.Info.HeightPoints - midPtY) / Math.Max(1.0, slot.Info.HeightPoints)) * slot.HeightDip;
        double desiredY = slot.TopDip + offsetInPageDip - Bounds.Height * 0.5;

        double midPtX = (firstBox.Left + firstBox.Right) * 0.5;
        double xInPageDip = (midPtX / Math.Max(1.0, slot.Info.WidthPoints)) * slot.WidthDip;
        double docX = slot.LeftDip + xInPageDip;
        if (docX < _scrollOffsetX + 40 || docX > _scrollOffsetX + Bounds.Width - 40)
        {
            double maxX = Math.Max(0, totalW - Bounds.Width);
            _scrollOffsetX = Math.Clamp(docX - Bounds.Width * 0.5, 0, maxX);
        }

        double maxY = Math.Max(0, totalH - Bounds.Height);
        _targetScrollY = Math.Clamp(desiredY, 0, maxY);
        _isSmoothScrolling = true;
        WakeScrollbars();
        if (!_scrollTimer.IsEnabled)
        {
            _scrollTimer.Start();
        }
    }

    public void ScrollViewportByFraction(double fraction)
    {
        if (_vm is null || _vm.Pages.Count == 0 || Bounds.Height <= 0) return;

        ComputeLayout(_animatedZoom, out _, out double totalH);
        double maxY = Math.Max(0, totalH - Bounds.Height);
        double baseOffset = _isSmoothScrolling ? _targetScrollY : _scrollOffsetY;
        _targetScrollY = Math.Clamp(baseOffset + Bounds.Height * fraction, 0, maxY);
        _isSmoothScrolling = true;
        WakeScrollbars();
        if (!_scrollTimer.IsEnabled)
        {
            _scrollTimer.Start();
        }
    }

    private void OnScrollTimerTick(object? sender, EventArgs e)
    {
        double diff = _targetScrollY - _scrollOffsetY;
        if (Math.Abs(diff) < 1.0)
        {
            _scrollOffsetY = _targetScrollY;
            _isSmoothScrolling = false;
            _scrollTimer.Stop();
        }
        else
        {
            _scrollOffsetY += diff * 0.35;
        }

        ClampScrollOffsets();
        UpdateCurrentPageFromViewport();
        InvalidateVisual();
        SyncOverlayPositions();
        if (!_isSmoothScrolling)
        {
            ScheduleVisiblePageRenders();
        }
    }

    public void FitToWidth()
    {
        if (_vm is null || _vm.Pages.Count == 0 || Bounds.Width <= 64) return;

        int pageIdx = Math.Clamp(_vm.CurrentPage - 1, 0, _vm.Pages.Count - 1);
        double pageWidthPt = Math.Max(1.0, _vm.Pages[pageIdx].WidthPoints);
        double availableDip = Math.Max(100.0, Bounds.Width - ViewportMarginDip * 2.0);
        double fitZoom = availableDip / (pageWidthPt * (96.0 / 72.0)) * 100.0;

        _scrollOffsetX = 0;
        SnapZoom(Math.Clamp(fitZoom, MinZoomPercent, MaxZoomPercent));
    }

    public void FitToPage()
    {
        if (_vm is null || _vm.Pages.Count == 0 || Bounds.Width <= 64 || Bounds.Height <= 96) return;

        int pageIdx = Math.Clamp(_vm.CurrentPage - 1, 0, _vm.Pages.Count - 1);
        var page = _vm.Pages[pageIdx];
        double availableW = Math.Max(100.0, Bounds.Width - ViewportMarginDip * 2.0);
        double availableH = Math.Max(100.0, Bounds.Height - ViewportMarginDip - BottomToolbarClearanceDip);

        double zoomW = availableW / (Math.Max(1.0, page.WidthPoints) * (96.0 / 72.0)) * 100.0;
        double zoomH = availableH / (Math.Max(1.0, page.HeightPoints) * (96.0 / 72.0)) * 100.0;
        double fitZoom = Math.Clamp(Math.Min(zoomW, zoomH), MinZoomPercent, MaxZoomPercent);

        _scrollOffsetX = 0;
        SnapZoom(fitZoom);
        ScrollToPage(pageIdx);
    }

    private void AnimateToZoom(int targetZoomPercent)
    {
        ApplyZoomAnchored(targetZoomPercent, new Point(Bounds.Width * 0.5, Bounds.Height * 0.5));
    }

    private void SnapZoom(double zoomPercent)
    {
        _scrollTimer.Stop();
        _isSmoothScrolling = false;
        _targetZoom = zoomPercent;
        _animatedZoom = zoomPercent;
        _vm?.SetZoomPercentFromViewport((int)Math.Round(zoomPercent));
        InvalidateMeasure();
        UpdateLayout();
        ClampScrollOffsets();
        WakeScrollbars();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
        SyncOverlayPositions();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Mark Handled immediately so no parent ScrollViewer ever scrolls vertically while zooming
            e.Handled = true;
            if (_vm is null || !_vm.HasOpenDocument) return;

            double factor = e.Delta.Y > 0 ? 1.15 : (1.0 / 1.15);
            double nextZoom = Math.Clamp(_animatedZoom * factor, MinZoomPercent, MaxZoomPercent);
            ApplyZoomAnchored(nextZoom, e.GetPosition(this));
            return;
        }

        base.OnPointerWheelChanged(e);
        if (_vm is null || !_vm.HasOpenDocument) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && Math.Abs(e.Delta.Y) > 0.001)
        {
            _scrollOffsetX -= e.Delta.Y * 56.0;
        }
        else
        {
            _scrollOffsetY -= e.Delta.Y * 56.0;
            _scrollOffsetX -= e.Delta.X * 56.0;
        }

        ClampScrollOffsets();
        UpdateCurrentPageFromViewport();
        WakeScrollbars();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
        SyncOverlayPositions();
        e.Handled = true;
    }

    /// <summary>
    /// Atomically updates zoom and scroll offsets in a single layout pass so the document point
    /// under <paramref name="anchorDip"/> stays stationary without timer shake.
    /// </summary>
    public void ApplyZoomAnchored(double targetZoomPercent, Point anchorDip)
    {
        if (_vm is null || _vm.Pages.Count == 0) return;

        double clampedZoom = Math.Clamp(targetZoomPercent, MinZoomPercent, MaxZoomPercent);
        if (Math.Abs(clampedZoom - _animatedZoom) < 0.01)
        {
            return;
        }

        _scrollTimer.Stop();
        _isSmoothScrolling = false;

        var oldSlots = ComputeLayout(_animatedZoom, out _, out _);
        double docX = _scrollOffsetX + anchorDip.X;
        double docY = _scrollOffsetY + anchorDip.Y;

        PageSlot? anchorSlot = null;
        double bestDist = double.MaxValue;
        foreach (var slot in oldSlots)
        {
            if (docY >= slot.TopDip && docY <= slot.TopDip + slot.HeightDip)
            {
                anchorSlot = slot;
                break;
            }
            double center = slot.TopDip + slot.HeightDip * 0.5;
            double dist = Math.Abs(center - docY);
            if (dist < bestDist)
            {
                bestDist = dist;
                anchorSlot = slot;
            }
        }

        double relX = 0.5;
        double relY = 0.0;
        int anchorPageIndex = 0;
        if (anchorSlot is not null)
        {
            anchorPageIndex = anchorSlot.PageIndex;
            relX = (docX - anchorSlot.LeftDip) / Math.Max(1.0, anchorSlot.WidthDip);
            relY = (docY - anchorSlot.TopDip) / Math.Max(1.0, anchorSlot.HeightDip);
        }

        _targetZoom = clampedZoom;
        _animatedZoom = clampedZoom;
        _vm.SetZoomPercentFromViewport((int)Math.Round(clampedZoom));

        InvalidateMeasure();
        UpdateLayout();

        var newSlots = ComputeLayout(_animatedZoom, out _, out _);
        if (newSlots.Count > 0)
        {
            int idx = Math.Clamp(anchorPageIndex, 0, newSlots.Count - 1);
            var targetSlot = newSlots[idx];
            double newDocX = targetSlot.LeftDip + relX * targetSlot.WidthDip;
            double newDocY = targetSlot.TopDip + relY * targetSlot.HeightDip;
            _scrollOffsetX = Math.Clamp(newDocX - anchorDip.X, 0, MaxScrollX);
            _scrollOffsetY = Math.Clamp(newDocY - anchorDip.Y, 0, MaxScrollY);
        }
        else
        {
            ClampScrollOffsets();
        }

        UpdateCurrentPageFromViewport();
        WakeScrollbars();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
        SyncOverlayPositions();
    }

    // --- Scrollbar Timers & Geometry ---

    private void WakeScrollbars()
    {
        ComputeScrollBarLayout(out var vBar, out var hBar);
        if (!vBar.IsVisible && !hBar.IsVisible) return;

        _scrollbarTargetOpacity = 1.0;
        if (!_scrollbarAnimTimer.IsEnabled)
        {
            _scrollbarAnimTimer.Start();
        }

        if (!_isHoveringVBar && !_isHoveringHBar && !_isDraggingVThumb && !_isDraggingHThumb)
        {
            _scrollbarHideTimer.Stop();
            _scrollbarHideTimer.Start();
        }
        else
        {
            _scrollbarHideTimer.Stop();
        }
    }

    private void OnScrollbarHideTimerTick(object? sender, EventArgs e)
    {
        _scrollbarHideTimer.Stop();
        if (_isHoveringVBar || _isHoveringHBar || _isDraggingVThumb || _isDraggingHThumb)
        {
            return;
        }

        _scrollbarTargetOpacity = 0.0;
        if (!_scrollbarAnimTimer.IsEnabled)
        {
            _scrollbarAnimTimer.Start();
        }
    }

    private void OnScrollbarAnimTimerTick(object? sender, EventArgs e)
    {
        const double step = 16.0 / 120.0; // 120ms transition
        bool changed = false;

        _scrollbarOpacity = StepToward(_scrollbarOpacity, _scrollbarTargetOpacity, step, ref changed);
        _vBarThickness = StepToward(_vBarThickness, _vBarTargetThickness, (ScrollBarHoverThickness - ScrollBarRestThickness) * step * 1.5, ref changed);
        _hBarThickness = StepToward(_hBarThickness, _hBarTargetThickness, (ScrollBarHoverThickness - ScrollBarRestThickness) * step * 1.5, ref changed);

        if (!changed)
        {
            _scrollbarAnimTimer.Stop();
        }
        InvalidateVisual();
    }

    private static double StepToward(double current, double target, double maxDelta, ref bool changed)
    {
        double diff = target - current;
        if (Math.Abs(diff) <= 0.005)
        {
            if (Math.Abs(current - target) > 0.0001) changed = true;
            return target;
        }
        changed = true;
        return current + Math.Clamp(diff, -maxDelta, maxDelta);
    }

    private readonly record struct ScrollBarMetrics(
        bool IsVisible,
        Rect TrackRect,
        Rect ThumbRect,
        Rect HitRect);

    private void ComputeScrollBarLayout(out ScrollBarMetrics vertical, out ScrollBarMetrics horizontal)
    {
        ComputeLayout(_animatedZoom, out double totalW, out double totalH);
        double viewW = Bounds.Width;
        double viewH = Bounds.Height;

        bool needV = totalH > viewH + 1.0 && viewH > 40;
        bool needH = totalW > viewW + 1.0 && viewW > 40;

        if (needV)
        {
            double thickness = _vBarThickness;
            double trackTop = ScrollBarMargin;
            double trackBottom = viewH - ScrollBarMargin - (needH ? 14.0 : 0.0);
            double trackHeight = Math.Max(24.0, trackBottom - trackTop);
            double trackLeft = viewW - ScrollBarMargin - thickness;

            double thumbHeight = Math.Clamp((viewH / totalH) * trackHeight, MinThumbLengthDip, trackHeight);
            double maxOffset = Math.Max(1.0, totalH - viewH);
            double fraction = Math.Clamp(_scrollOffsetY / maxOffset, 0.0, 1.0);
            double thumbTop = trackTop + fraction * Math.Max(0.0, trackHeight - thumbHeight);

            vertical = new ScrollBarMetrics(
                true,
                new Rect(trackLeft, trackTop, thickness, trackHeight),
                new Rect(trackLeft, thumbTop, thickness, thumbHeight),
                new Rect(viewW - ScrollBarHitMargin, trackTop, ScrollBarHitMargin, trackHeight));
        }
        else
        {
            vertical = default;
        }

        if (needH)
        {
            double thickness = _hBarThickness;
            double trackLeft = ScrollBarMargin;
            double trackRight = viewW - ScrollBarMargin - (needV ? 14.0 : 0.0);
            double trackWidth = Math.Max(24.0, trackRight - trackLeft);
            double trackTop = viewH - ScrollBarMargin - thickness;

            double thumbWidth = Math.Clamp((viewW / totalW) * trackWidth, MinThumbLengthDip, trackWidth);
            double maxOffset = Math.Max(1.0, totalW - viewW);
            double fraction = Math.Clamp(_scrollOffsetX / maxOffset, 0.0, 1.0);
            double thumbLeft = trackLeft + fraction * Math.Max(0.0, trackWidth - thumbWidth);

            horizontal = new ScrollBarMetrics(
                true,
                new Rect(trackLeft, trackTop, trackWidth, thickness),
                new Rect(thumbLeft, trackTop, thumbWidth, thickness),
                new Rect(trackLeft, viewH - ScrollBarHitMargin, trackWidth, ScrollBarHitMargin));
        }
        else
        {
            horizontal = default;
        }
    }

    // --- Pointer Interaction ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_vm is null || !_vm.HasOpenDocument) return;

        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed)
        {
            ComputeScrollBarLayout(out var vBar, out var hBar);
            if (vBar.IsVisible && vBar.HitRect.Contains(pos))
            {
                _isDraggingVThumb = true;
                WakeScrollbars();
                if (vBar.ThumbRect.Contains(pos))
                {
                    _thumbDragGrabOffset = pos.Y - vBar.ThumbRect.Y;
                }
                else
                {
                    _thumbDragGrabOffset = vBar.ThumbRect.Height * 0.5;
                    ScrubVerticalScrollBar(pos.Y, vBar);
                }
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (hBar.IsVisible && hBar.HitRect.Contains(pos))
            {
                _isDraggingHThumb = true;
                WakeScrollbars();
                if (hBar.ThumbRect.Contains(pos))
                {
                    _thumbDragGrabOffset = pos.X - hBar.ThumbRect.X;
                }
                else
                {
                    _thumbDragGrabOffset = hBar.ThumbRect.Width * 0.5;
                    ScrubHorizontalScrollBar(pos.X, hBar);
                }
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (props.IsRightButtonPressed)
        {
            if (TryHitPage(pos, out var ctxSlot, out var ctxPt))
            {
                _vm.ContextPageIndex = ctxSlot.PageIndex;
                _vm.ContextPagePointPt = ctxPt;
                _vm.SetCurrentPageFromScroll(ctxSlot.PageIndex + 1);
            }
            return;
        }

        if (props.IsMiddleButtonPressed)
        {
            BeginPan(pos, e);
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        if (_isSpacebarHeld)
        {
            BeginPan(pos, e);
            return;
        }

        if (TryHitPage(pos, out var slot, out var pagePt))
        {
            // 1. Check interactive form field clicks in Select mode
            if (_vm.ActiveTool == PdfToolKind.Select)
            {
                var hitField = FindFormFieldAtPoint(slot.PageIndex, pagePt);
                if (hitField is not null)
                {
                    if (hitField.Kind == PdfFormFieldKind.CheckBox)
                    {
                        _vm.ToggleOrUpdateFormField(hitField, isChecked: !hitField.IsChecked);
                        e.Handled = true;
                        return;
                    }
                    else
                    {
                        double dipPerPt = slot.WidthDip / Math.Max(1.0, slot.Info.WidthPoints);
                        var fieldRectDip = new Rect(
                            slot.LeftDip - _scrollOffsetX + hitField.BoundsPt.Left * dipPerPt,
                            slot.TopDip - _scrollOffsetY + hitField.BoundsPt.Top * dipPerPt,
                            (hitField.BoundsPt.Right - hitField.BoundsPt.Left) * dipPerPt,
                            (hitField.BoundsPt.Bottom - hitField.BoundsPt.Top) * dipPerPt);
                        RequestEditFormField?.Invoke(hitField, fieldRectDip);
                        e.Handled = true;
                        return;
                    }
                }

                // 2. Check existing annotation resize handle or hit box
                if (_vm.SelectedAnnotation is { } selected && selected.PageIndex == slot.PageIndex)
                {
                    double dipPerPt = slot.WidthDip / Math.Max(1.0, slot.Info.WidthPoints);
                    if (IsNearBottomRightHandle(pos, slot, selected, dipPerPt))
                    {
                        _interactionMode = DragInteractionMode.ResizeAnnotation;
                        _interactionPageIndex = slot.PageIndex;
                        _interactionStartPt = pagePt;
                        _manipulatedAnnotation = selected;
                        _initialBoundsPt = selected.BoundsPt;
                        _hasModifiedManipulatedAnnotation = false;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }

                var hitAnn = FindAnnotationAtPoint(slot.PageIndex, pagePt);
                if (hitAnn is not null)
                {
                    _vm.ClearPdfTextSelection();
                    _vm.SelectedAnnotation = hitAnn;
                    _interactionMode = DragInteractionMode.MoveAnnotation;
                    _interactionPageIndex = slot.PageIndex;
                    _interactionStartPt = pagePt;
                    _manipulatedAnnotation = hitAnn;
                    _initialBoundsPt = hitAnn.BoundsPt;
                    _initialInkPointsPt = [.. hitAnn.InkPointsPt];
                    _hasModifiedManipulatedAnnotation = false;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }

                _vm.SelectedAnnotation = null;

                // 3. Check PDF text character hit for true text selection
                if (_vm.ActivePdfTab?.TextLayoutCache.TryGetValue(slot.PageIndex, out var layout) == true)
                {
                    int charIdx = layout.HitTestChar(pagePt.XPt, pagePt.YPt, 6.0);
                    if (charIdx >= 0)
                    {
                        _interactionMode = DragInteractionMode.SelectText;
                        _interactionPageIndex = slot.PageIndex;
                        _selAnchorPageIndex = slot.PageIndex;
                        _selFocusPageIndex = slot.PageIndex;

                        if (e.ClickCount >= 3)
                        {
                            var (start, end) = layout.GetLineRangeAt(charIdx);
                            _selAnchorCharIndex = start;
                            _selFocusCharIndex = end;
                            _selDidDrag = true;
                            _selWasMultiClick = true;
                        }
                        else if (e.ClickCount == 2)
                        {
                            var (start, end) = layout.GetWordRangeAt(charIdx);
                            _selAnchorCharIndex = start;
                            _selFocusCharIndex = end;
                            _selDidDrag = true;
                            _selWasMultiClick = true;
                        }
                        else
                        {
                            _selAnchorCharIndex = charIdx;
                            _selFocusCharIndex = charIdx;
                            _selDidDrag = false;
                            _selWasMultiClick = false;
                        }

                        e.Pointer.Capture(this);
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                }
                else
                {
                    _ = EnsurePageTextLayoutAsync(slot.PageIndex);
                }

                _vm.ClearPdfTextSelection();
            }
            else if (_vm.ActiveTool == PdfToolKind.EditText)
            {
                _ = TryOpenInlineTextEditAsync(slot, pagePt);
                e.Handled = true;
                return;
            }
            else if (_vm.ActiveTool is PdfToolKind.Highlight or PdfToolKind.Underline or PdfToolKind.Redact)
            {
                _interactionMode = DragInteractionMode.CreateRectAnnotation;
                _interactionPageIndex = slot.PageIndex;
                _interactionStartPt = pagePt;
                var kind = _vm.ActiveTool switch
                {
                    PdfToolKind.Highlight => AnnotationKind.Highlight,
                    PdfToolKind.Underline => AnnotationKind.Underline,
                    _ => AnnotationKind.Redaction
                };
                _draftAnnotation = new PdfAnnotation
                {
                    PageIndex = slot.PageIndex,
                    Kind = kind,
                    BoundsPt = new PdfRect(pagePt.XPt, pagePt.YPt, pagePt.XPt, pagePt.YPt),
                    ColorHex = kind == AnnotationKind.Redaction ? "#111827" : _vm.ActiveToolColorHex,
                    StrokeWidthPt = _vm.ActiveStrokeWidthPt
                };
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            else if (_vm.ActiveTool == PdfToolKind.Draw)
            {
                _interactionMode = DragInteractionMode.FreehandDraw;
                _interactionPageIndex = slot.PageIndex;
                _draftAnnotation = new PdfAnnotation
                {
                    PageIndex = slot.PageIndex,
                    Kind = AnnotationKind.FreehandDraw,
                    ColorHex = _vm.ActiveToolColorHex,
                    StrokeWidthPt = _vm.ActiveStrokeWidthPt,
                    BoundsPt = new PdfRect(pagePt.XPt, pagePt.YPt, pagePt.XPt, pagePt.YPt),
                    InkPointsPt = [pagePt]
                };
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            else if (_vm.ActiveTool is PdfToolKind.Text or PdfToolKind.Note)
            {
                var kind = _vm.ActiveTool == PdfToolKind.Text ? AnnotationKind.TextBox : AnnotationKind.StickyNote;
                double w = kind == AnnotationKind.StickyNote ? 130.0 : 120.0;
                double h = kind == AnnotationKind.StickyNote ? 56.0 : Math.Max(22.0, _vm.ActiveFontSizePt * 1.8);
                double left = Math.Clamp(pagePt.XPt, 4, Math.Max(4, slot.Info.WidthPoints - w));
                double top = Math.Clamp(pagePt.YPt, 4, Math.Max(4, slot.Info.HeightPoints - h));

                var ann = new PdfAnnotation
                {
                    PageIndex = slot.PageIndex,
                    Kind = kind,
                    BoundsPt = new PdfRect(left, top, left + w, top + h),
                    ColorHex = kind == AnnotationKind.TextBox ? _vm.ActiveToolColorHex : "#CA8A04",
                    FontSizePt = _vm.ActiveFontSizePt,
                    TextContent = kind == AnnotationKind.TextBox ? "Text" : "Note"
                };

                _vm.AddAnnotation(ann);
                _vm.SetSelectToolCommand.Execute(null);
                RequestFocusAnnotationEditor?.Invoke(ann);
                e.Handled = true;
                return;
            }
        }

        // Fallback on empty canvas / Hand tool: smooth pan
        if (_vm.ActiveTool is PdfToolKind.Select or PdfToolKind.Hand)
        {
            BeginPan(pos, e);
        }
    }

    private void BeginPan(Point pos, PointerPressedEventArgs e)
    {
        _isPanning = true;
        _lastPanPoint = pos;
        UpdateCursorForTool();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is null || !_vm.HasOpenDocument) return;

        var pos = e.GetPosition(this);

        if (_isDraggingVThumb)
        {
            ComputeScrollBarLayout(out var vBar, out _);
            if (vBar.IsVisible)
            {
                ScrubVerticalScrollBar(pos.Y, vBar);
            }
            e.Handled = true;
            return;
        }

        if (_isDraggingHThumb)
        {
            ComputeScrollBarLayout(out _, out var hBar);
            if (hBar.IsVisible)
            {
                ScrubHorizontalScrollBar(pos.X, hBar);
            }
            e.Handled = true;
            return;
        }

        // Scrollbar hover & wake detection
        ComputeScrollBarLayout(out var vMetrics, out var hMetrics);
        bool overV = vMetrics.IsVisible && vMetrics.HitRect.Contains(pos);
        bool overH = hMetrics.IsVisible && hMetrics.HitRect.Contains(pos);
        if (overV != _isHoveringVBar || overH != _isHoveringHBar)
        {
            _isHoveringVBar = overV;
            _isHoveringHBar = overH;
            _vBarTargetThickness = overV ? ScrollBarHoverThickness : ScrollBarRestThickness;
            _hBarTargetThickness = overH ? ScrollBarHoverThickness : ScrollBarRestThickness;
            WakeScrollbars();
        }
        else if (overV || overH)
        {
            WakeScrollbars();
        }

        if (_isPanning)
        {
            var delta = pos - _lastPanPoint;
            _lastPanPoint = pos;
            _scrollOffsetX -= delta.X;
            _scrollOffsetY -= delta.Y;
            ClampScrollOffsets();
            UpdateCurrentPageFromViewport();
            WakeScrollbars();
            InvalidateVisual();
            ScheduleVisiblePageRenders();
            SyncOverlayPositions();
            UpdateCursorCoordinates(pos);
            return;
        }

        if (_interactionMode != DragInteractionMode.None && _interactionPageIndex >= 0)
        {
            var slots = ComputeLayout(_animatedZoom, out _, out _);
            if (_interactionPageIndex < slots.Count)
            {
                var slot = slots[_interactionPageIndex];
                var pt = ScreenToPagePoints(pos, slot, clampToPage: true);

                if (_interactionMode == DragInteractionMode.CreateRectAnnotation && _draftAnnotation is not null)
                {
                    _draftAnnotation.BoundsPt = new PdfRect(
                        Math.Min(_interactionStartPt.XPt, pt.XPt),
                        Math.Min(_interactionStartPt.YPt, pt.YPt),
                        Math.Max(_interactionStartPt.XPt, pt.XPt),
                        Math.Max(_interactionStartPt.YPt, pt.YPt));
                    InvalidateVisual();
                }
                else if (_interactionMode == DragInteractionMode.FreehandDraw && _draftAnnotation is not null)
                {
                    var last = _draftAnnotation.InkPointsPt[^1];
                    if (Math.Abs(pt.XPt - last.XPt) + Math.Abs(pt.YPt - last.YPt) >= 1.5)
                    {
                        _draftAnnotation.InkPointsPt.Add(pt);
                        _draftAnnotation.BoundsPt = ComputeInkBounds(_draftAnnotation.InkPointsPt);
                        InvalidateVisual();
                    }
                }
                else if (_interactionMode == DragInteractionMode.MoveAnnotation && _manipulatedAnnotation is not null)
                {
                    double dx = pt.XPt - _interactionStartPt.XPt;
                    double dy = pt.YPt - _interactionStartPt.YPt;
                    if (Math.Abs(dx) + Math.Abs(dy) > 0.5 && !_hasModifiedManipulatedAnnotation)
                    {
                        _vm.BeginAnnotationTransformSnapshot();
                        _hasModifiedManipulatedAnnotation = true;
                    }

                    if (_hasModifiedManipulatedAnnotation)
                    {
                        _manipulatedAnnotation.BoundsPt = new PdfRect(
                            _initialBoundsPt.Left + dx,
                            _initialBoundsPt.Top + dy,
                            _initialBoundsPt.Right + dx,
                            _initialBoundsPt.Bottom + dy);

                        if (_initialInkPointsPt is not null && _manipulatedAnnotation.InkPointsPt.Count == _initialInkPointsPt.Count)
                        {
                            for (int i = 0; i < _initialInkPointsPt.Count; i++)
                            {
                                _manipulatedAnnotation.InkPointsPt[i] = new PdfPoint(
                                    _initialInkPointsPt[i].XPt + dx,
                                    _initialInkPointsPt[i].YPt + dy);
                            }
                        }
                        InvalidateVisual();
                    }
                }
                else if (_interactionMode == DragInteractionMode.ResizeAnnotation && _manipulatedAnnotation is not null)
                {
                    double nextRight = Math.Max(_initialBoundsPt.Left + 16.0, pt.XPt);
                    double nextBottom = Math.Max(_initialBoundsPt.Top + 12.0, pt.YPt);
                    if (!_hasModifiedManipulatedAnnotation)
                    {
                        _vm.BeginAnnotationTransformSnapshot();
                        _hasModifiedManipulatedAnnotation = true;
                    }
                    _manipulatedAnnotation.BoundsPt = new PdfRect(
                        _initialBoundsPt.Left,
                        _initialBoundsPt.Top,
                        nextRight,
                        nextBottom);
                    InvalidateVisual();
                }
            }
        }

        if (_interactionMode == DragInteractionMode.SelectText)
        {
            var slots = ComputeLayout(_animatedZoom, out _, out _);
            if (TryFindSlotForSelection(pos, slots, out var curSlot, out var curPt) &&
                _vm.ActivePdfTab?.TextLayoutCache.TryGetValue(curSlot.PageIndex, out var layout) == true)
            {
                int idx = layout.FindNearestCharOnOrNearPoint(curPt.XPt, curPt.YPt);
                if (idx >= 0)
                {
                    if (_selFocusPageIndex != curSlot.PageIndex || _selFocusCharIndex != idx)
                    {
                        _selFocusPageIndex = curSlot.PageIndex;
                        _selFocusCharIndex = idx;
                        _selDidDrag = true;
                        InvalidateVisual();
                    }
                }
            }
            else if (TryFindSlotForSelection(pos, slots, out var loadSlot, out _))
            {
                _ = EnsurePageTextLayoutAsync(loadSlot.PageIndex);
            }
        }
        else if (_interactionMode == DragInteractionMode.None)
        {
            UpdateHoverTextCursor(pos);
        }

        UpdateCursorCoordinates(pos);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isDraggingVThumb || _isDraggingHThumb)
        {
            _isDraggingVThumb = false;
            _isDraggingHThumb = false;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            WakeScrollbars();
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            UpdateCursorForTool();
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_interactionMode != DragInteractionMode.None)
        {
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);

            if (_interactionMode == DragInteractionMode.SelectText && _vm is not null)
            {
                if (_selDidDrag || _selWasMultiClick)
                {
                    CommitLiveTextSelectionToViewModel();
                }
                else
                {
                    _vm.ClearPdfTextSelection();
                }
            }
            else if ((_interactionMode == DragInteractionMode.CreateRectAnnotation ||
                 _interactionMode == DragInteractionMode.FreehandDraw) &&
                _draftAnnotation is not null && _vm is not null)
            {
                var r = _draftAnnotation.BoundsPt;
                bool valid = _interactionMode == DragInteractionMode.FreehandDraw
                    ? _draftAnnotation.InkPointsPt.Count >= 2
                    : (r.Right - r.Left >= 6.0 || r.Bottom - r.Top >= 6.0);

                if (valid)
                {
                    if (_draftAnnotation.Kind == AnnotationKind.Underline && r.Bottom - r.Top < 4.0)
                    {
                        _draftAnnotation.BoundsPt = new PdfRect(r.Left, r.Bottom - 2.0, r.Right, r.Bottom);
                    }
                    _vm.AddAnnotation(_draftAnnotation);
                }
            }
            else if (_hasModifiedManipulatedAnnotation && _vm is not null)
            {
                _vm.CommitAnnotationTransform();
            }

            _interactionMode = DragInteractionMode.None;
            _interactionPageIndex = -1;
            _draftAnnotation = null;
            _manipulatedAnnotation = null;
            _initialInkPointsPt = null;
            _hasModifiedManipulatedAnnotation = false;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_isHoveringVBar || _isHoveringHBar)
        {
            _isHoveringVBar = false;
            _isHoveringHBar = false;
            _vBarTargetThickness = ScrollBarRestThickness;
            _hBarTargetThickness = ScrollBarRestThickness;
            WakeScrollbars();
        }
    }

    private void ScrubVerticalScrollBar(double pointerY, ScrollBarMetrics vBar)
    {
        double travel = Math.Max(1.0, vBar.TrackRect.Height - vBar.ThumbRect.Height);
        double thumbTop = Math.Clamp(pointerY - _thumbDragGrabOffset, vBar.TrackRect.Y, vBar.TrackRect.Y + travel);
        double fraction = (thumbTop - vBar.TrackRect.Y) / travel;

        ComputeLayout(_animatedZoom, out _, out double totalH);
        double maxY = Math.Max(0.0, totalH - Bounds.Height);
        _scrollTimer.Stop();
        _isSmoothScrolling = false;
        _scrollOffsetY = fraction * maxY;
        ClampScrollOffsets();
        UpdateCurrentPageFromViewport();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
        SyncOverlayPositions();
    }

    private void ScrubHorizontalScrollBar(double pointerX, ScrollBarMetrics hBar)
    {
        double travel = Math.Max(1.0, hBar.TrackRect.Width - hBar.ThumbRect.Width);
        double thumbLeft = Math.Clamp(pointerX - _thumbDragGrabOffset, hBar.TrackRect.X, hBar.TrackRect.X + travel);
        double fraction = (thumbLeft - hBar.TrackRect.X) / travel;

        ComputeLayout(_animatedZoom, out double totalW, out _);
        double maxX = Math.Max(0.0, totalW - Bounds.Width);
        _scrollOffsetX = fraction * maxX;
        ClampScrollOffsets();
        InvalidateVisual();
        SyncOverlayPositions();
    }

    private bool TryHitPage(Point viewportPos, out PageSlot hitSlot, out PdfPoint pagePt)
    {
        var slots = ComputeLayout(_animatedZoom, out _, out _);
        double docX = viewportPos.X + _scrollOffsetX;
        double docY = viewportPos.Y + _scrollOffsetY;

        foreach (var slot in slots)
        {
            if (docX >= slot.LeftDip && docX <= slot.LeftDip + slot.WidthDip &&
                docY >= slot.TopDip && docY <= slot.TopDip + slot.HeightDip)
            {
                hitSlot = slot;
                pagePt = ScreenToPagePoints(viewportPos, slot, clampToPage: true);
                return true;
            }
        }

        hitSlot = null!;
        pagePt = default;
        return false;
    }

    private PdfPoint ScreenToPagePoints(Point viewportPos, PageSlot slot, bool clampToPage)
    {
        double docX = viewportPos.X + _scrollOffsetX;
        double docY = viewportPos.Y + _scrollOffsetY;
        double xPt = (docX - slot.LeftDip) / Math.Max(1.0, slot.WidthDip) * slot.Info.WidthPoints;
        double yPt = (docY - slot.TopDip) / Math.Max(1.0, slot.HeightDip) * slot.Info.HeightPoints;
        if (clampToPage)
        {
            xPt = Math.Clamp(xPt, 0, slot.Info.WidthPoints);
            yPt = Math.Clamp(yPt, 0, slot.Info.HeightPoints);
        }
        return new PdfPoint(xPt, yPt);
    }

    private PdfFormField? FindFormFieldAtPoint(int pageIndex, PdfPoint pt)
    {
        if (_vm is null) return null;
        for (int i = _vm.FormFields.Count - 1; i >= 0; i--)
        {
            var f = _vm.FormFields[i];
            if (f.PageIndex != pageIndex) continue;
            var r = f.BoundsPt;
            if (pt.XPt >= r.Left - 2 && pt.XPt <= r.Right + 2 && pt.YPt >= r.Top - 2 && pt.YPt <= r.Bottom + 2)
            {
                return f;
            }
        }
        return null;
    }

    private PdfAnnotation? FindAnnotationAtPoint(int pageIndex, PdfPoint pt)
    {
        if (_vm is null) return null;
        var list = _vm.Annotations;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var ann = list[i];
            if (ann.PageIndex != pageIndex) continue;
            var r = ann.BoundsPt;
            double pad = ann.Kind == AnnotationKind.Underline ? 8.0 : 4.0;
            if (pt.XPt >= r.Left - pad && pt.XPt <= r.Right + pad &&
                pt.YPt >= r.Top - pad && pt.YPt <= r.Bottom + pad)
            {
                return ann;
            }
        }
        return null;
    }

    private static bool IsNearBottomRightHandle(Point viewportPos, PageSlot slot, PdfAnnotation ann, double dipPerPt)
    {
        double hx = slot.LeftDip - 0 + ann.BoundsPt.Right * dipPerPt; // doc space X
        double hy = slot.TopDip + ann.BoundsPt.Bottom * dipPerPt;    // doc space Y
        return false; // overload replaced cleanly below using viewportPos check
    }

    private bool IsNearBottomRightHandle(Point viewportPos, PageSlot slot, PdfAnnotation ann, double dipPerPt, bool _)
    {
        double hx = slot.LeftDip - _scrollOffsetX + ann.BoundsPt.Right * dipPerPt;
        double hy = slot.TopDip - _scrollOffsetY + ann.BoundsPt.Bottom * dipPerPt;
        return Math.Abs(viewportPos.X - hx) <= 10.0 && Math.Abs(viewportPos.Y - hy) <= 10.0;
    }

    private bool IsNearBottomRightHandle(Point viewportPos, PageSlot slot, PdfAnnotation ann)
    {
        double dipPerPt = slot.WidthDip / Math.Max(1.0, slot.Info.WidthPoints);
        return IsNearBottomRightHandle(viewportPos, slot, ann, dipPerPt, true);
    }

    private static PdfRect ComputeInkBounds(IReadOnlyList<PdfPoint> pts)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            if (p.XPt < minX) minX = p.XPt;
            if (p.YPt < minY) minY = p.YPt;
            if (p.XPt > maxX) maxX = p.XPt;
            if (p.YPt > maxY) maxY = p.YPt;
        }
        return new PdfRect(minX, minY, maxX, maxY);
    }

    private void UpdateCursorCoordinates(Point viewportPos)
    {
        if (_vm is null || _vm.Pages.Count == 0) return;

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        double docX = viewportPos.X + _scrollOffsetX;
        double docY = viewportPos.Y + _scrollOffsetY;

        foreach (var slot in slots)
        {
            if (docY >= slot.TopDip && docY <= slot.TopDip + slot.HeightDip)
            {
                double xPt = Math.Clamp((docX - slot.LeftDip) / slot.WidthDip * slot.Info.WidthPoints, 0, slot.Info.WidthPoints);
                double yPt = Math.Clamp((docY - slot.TopDip) / slot.HeightDip * slot.Info.HeightPoints, 0, slot.Info.HeightPoints);
                _vm.CursorXPt = Math.Round(xPt, 1);
                _vm.CursorYPt = Math.Round(yPt, 1);
                return;
            }
        }
    }

    private void UpdateCurrentPageFromViewport()
    {
        if (_vm is null || _vm.Pages.Count == 0) return;

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        double viewCenterY = _scrollOffsetY + Bounds.Height * 0.4;

        int bestIdx = 0;
        double bestDist = double.MaxValue;
        foreach (var slot in slots)
        {
            double pageCenter = slot.TopDip + slot.HeightDip * 0.5;
            double dist = Math.Abs(pageCenter - viewCenterY);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = slot.PageIndex;
            }
        }

        _vm.SetCurrentPageFromScroll(bestIdx + 1);
    }

    private double GetRenderScale() => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private static double SnapDipToDevicePixel(double dip, double renderScale) =>
        Math.Round(dip * renderScale) / renderScale;

    private List<PageSlot> ComputeLayout(double zoomPercent, out double totalWidthDip, out double totalHeightDip)
    {
        var slots = new List<PageSlot>();
        if (_vm is null || _vm.Pages.Count == 0)
        {
            totalWidthDip = 0;
            totalHeightDip = 0;
            MaxScrollX = 0;
            MaxScrollY = 0;
            return slots;
        }

        double renderScale = Math.Max(1.0, GetRenderScale());
        double zoom = zoomPercent / 100.0;
        double maxPageWidthDip = 0;

        var sizes = new List<(double W, double H)>(_vm.Pages.Count);
        foreach (var p in _vm.Pages)
        {
            double logicalWidth = p.WidthPoints * (96.0 / 72.0);
            double logicalHeight = p.HeightPoints * (96.0 / 72.0);
            int widthPx = Math.Max(1, (int)Math.Round(logicalWidth * renderScale * zoom));
            int heightPx = Math.Max(1, (int)Math.Round(logicalHeight * renderScale * zoom));
            double wDip = widthPx / renderScale;
            double hDip = heightPx / renderScale;
            sizes.Add((wDip, hDip));
            if (wDip > maxPageWidthDip) maxPageWidthDip = wDip;
        }

        totalWidthDip = Math.Max(Bounds.Width, maxPageWidthDip + ViewportMarginDip * 2.0);
        double y = ViewportMarginDip;

        for (int i = 0; i < _vm.Pages.Count; i++)
        {
            var p = _vm.Pages[i];
            var (wDip, hDip) = sizes[i];
            double leftDip = SnapDipToDevicePixel((totalWidthDip - wDip) * 0.5, renderScale);
            double topDip = SnapDipToDevicePixel(y, renderScale);
            slots.Add(new PageSlot(p.PageIndex, p, topDip, leftDip, wDip, hDip));
            y += hDip + PageGapDip;
        }

        totalHeightDip = y - PageGapDip + BottomToolbarClearanceDip;
        MaxScrollX = Math.Max(0, totalWidthDip - Bounds.Width);
        MaxScrollY = Math.Max(0, totalHeightDip - Bounds.Height);
        return slots;
    }

    private void ClampScrollOffsets()
    {
        ComputeLayout(_animatedZoom, out _, out _);
        _scrollOffsetX = Math.Clamp(_scrollOffsetX, 0, MaxScrollX);
        _scrollOffsetY = Math.Clamp(_scrollOffsetY, 0, MaxScrollY);
    }

    private static BitmapCacheKey MakeKey(PageSlot slot, double renderScale)
    {
        int wPx = Math.Max(1, (int)Math.Round(slot.WidthDip * renderScale));
        int hPx = Math.Max(1, (int)Math.Round(slot.HeightDip * renderScale));
        int rot = ((slot.Info.RotationDegrees / 90) % 4 + 4) % 4;
        return new BitmapCacheKey(slot.Info.EffectiveSourcePageIndex, wPx, hPx, rot);
    }

    private void ScheduleVisiblePageRenders()
    {
        if (_vm is null || !_vm.HasOpenDocument || _vm.Pages.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        double viewTop = _scrollOffsetY;
        double viewBottom = _scrollOffsetY + Bounds.Height;
        double renderScale = GetRenderScale();

        int firstVisible = -1;
        int lastVisible = -1;

        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.TopDip + s.HeightDip >= viewTop && s.TopDip <= viewBottom)
            {
                if (firstVisible < 0) firstVisible = i;
                lastVisible = i;
            }
        }

        if (firstVisible < 0) return;

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            var visSlot = slots[i];
            if (!visSlot.Info.IsBlankPage && _vm.ActivePdfTab?.TextLayoutCache.ContainsKey(visSlot.PageIndex) == false)
            {
                _ = EnsurePageTextLayoutAsync(visSlot.PageIndex);
            }
        }

        int startIdx = Math.Max(0, firstVisible - 1);
        int endIdx = Math.Min(slots.Count - 1, lastVisible + 1);
        var desiredKeys = new HashSet<BitmapCacheKey>();

        for (int i = startIdx; i <= endIdx; i++)
        {
            var slot = slots[i];
            if (slot.Info.IsBlankPage) continue;
            var key = MakeKey(slot, renderScale);
            desiredKeys.Add(key);

            if (_vm.BitmapCache.TryGet(key, out _))
            {
                continue;
            }

            if (_inFlight.ContainsKey(key))
            {
                continue;
            }

            var priority = (i >= firstVisible && i <= lastVisible)
                ? RenderPriority.VisibleViewport
                : RenderPriority.AdjacentPreload;

            var cts = new CancellationTokenSource();
            _inFlight[key] = cts;
            _ = RenderAndCacheAsync(key, priority, cts);
        }

        foreach (var kvp in _inFlight.ToList())
        {
            if (!desiredKeys.Contains(kvp.Key))
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
                _inFlight.Remove(kvp.Key);
            }
        }
    }

    private async Task RenderAndCacheAsync(BitmapCacheKey key, RenderPriority priority, CancellationTokenSource cts)
    {
        if (_vm is null) return;
        try
        {
            var bmp = await _vm.RenderEngine.RenderPageAsync(new RenderRequest(
                key.PageIndex,
                key.WidthPx,
                key.HeightPx,
                key.RotationQuarterTurns,
                priority,
                cts.Token));

            if (cts.IsCancellationRequested)
            {
                bmp.Dispose();
                return;
            }

            _vm.BitmapCache.Put(key, bmp);
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
        catch (Exception)
        {
            // Ignore transient shutdown/document-close errors
        }
        finally
        {
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
            {
                _inFlight.Remove(key);
                cts.Dispose();
            }
        }
    }

    private void CancelAllInFlight()
    {
        foreach (var cts in _inFlight.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _inFlight.Clear();
    }

    private SKBitmap? GetOrDecodeSignatureBitmap(PdfAnnotation ann)
    {
        if (ann.SignaturePngBytes is not { Length: > 0 } bytes) return null;
        if (_signatureBitmapCache.TryGetValue(ann.Id, out var cached))
        {
            return cached;
        }
        var decoded = SKBitmap.Decode(bytes);
        if (decoded is not null)
        {
            _signatureBitmapCache[ann.Id] = decoded;
        }
        return decoded;
    }

    private void ClearSignatureCache()
    {
        foreach (var bmp in _signatureBitmapCache.Values)
        {
            bmp.Dispose();
        }
        _signatureBitmapCache.Clear();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_vm is null || !_vm.HasOpenDocument || _vm.Pages.Count == 0) return;

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        double renderScale = Math.Max(1.0, GetRenderScale());
        double snappedScrollX = SnapDipToDevicePixel(_scrollOffsetX, renderScale);
        double snappedScrollY = SnapDipToDevicePixel(_scrollOffsetY, renderScale);
        double viewTop = snappedScrollY;
        double viewBottom = snappedScrollY + Bounds.Height;

        var visibleItems = new List<VisiblePageRenderItem>();
        var activeMatch = _vm.ActiveSearchMatch;
        Guid? selectedAnnId = _vm.SelectedAnnotation?.Id;

        foreach (var slot in slots)
        {
            if (slot.TopDip + slot.HeightDip < viewTop || slot.TopDip > viewBottom)
            {
                continue;
            }

            var destRect = new Rect(
                SnapDipToDevicePixel(slot.LeftDip - snappedScrollX, renderScale),
                SnapDipToDevicePixel(slot.TopDip - snappedScrollY, renderScale),
                slot.WidthDip,
                slot.HeightDip);

            var key = MakeKey(slot, renderScale);
            var pageMatches = _vm.SearchMatches
                .Where(m => m.PageIndex == slot.PageIndex)
                .ToList();

            var pageAnnots = _vm.Annotations
                .Where(a => a.PageIndex == slot.PageIndex)
                .ToList();

            if (_draftAnnotation is not null && _draftAnnotation.PageIndex == slot.PageIndex)
            {
                pageAnnots.Add(_draftAnnotation);
            }

            var pageFields = _vm.FormFields
                .Where(f => f.PageIndex == slot.PageIndex)
                .ToList();

            var selectionBoxes = GetSelectionBoxesForPage(slot.PageIndex);
            var textEdits = _vm.ActivePdfTab?.EditPlan.TextEdits
                .Where(te => te.PageIndex == slot.PageIndex)
                .ToList() ?? [];
            PdfRect? hoveredBlock = (_vm.ActiveTool == PdfToolKind.EditText &&
                                     _hoveredTextBlockPageIndex == slot.PageIndex)
                ? _hoveredTextBlock?.BoundsPt
                : null;

            visibleItems.Add(new VisiblePageRenderItem(
                slot.PageIndex,
                slot.Info,
                destRect,
                key,
                pageMatches,
                activeMatch,
                pageAnnots,
                pageFields,
                selectedAnnId,
                selectionBoxes,
                textEdits,
                hoveredBlock));
        }

        ComputeScrollBarLayout(out var vBar, out var hBar);

        if (visibleItems.Count > 0 || _scrollbarOpacity > 0.01)
        {
            context.Custom(new PdfViewportDrawOperation(
                new Rect(0, 0, Bounds.Width, Bounds.Height),
                _vm.BitmapCache,
                visibleItems,
                _vm.IsDarkMode,
                _vm.SelectedTheme.PageBorderSkColor,
                _vm.SelectedTheme.PageShadowSkColor,
                vBar,
                hBar,
                _scrollbarOpacity,
                _isHoveringVBar || _isDraggingVThumb,
                _isHoveringHBar || _isDraggingHThumb,
                GetOrDecodeSignatureBitmap));
        }
    }

    private PdfTextSelectionSpan? GetActiveSelectionSpan()
    {
        if (_interactionMode == DragInteractionMode.SelectText &&
            _selAnchorPageIndex >= 0 && _selFocusPageIndex >= 0 &&
            _selAnchorCharIndex >= 0 && _selFocusCharIndex >= 0)
        {
            return new PdfTextSelectionSpan(_selAnchorPageIndex, _selAnchorCharIndex, _selFocusPageIndex, _selFocusCharIndex).Normalized();
        }
        return _vm?.ActivePdfTab?.ActiveSelection?.Normalized();
    }

    private IReadOnlyList<PdfRect> GetSelectionBoxesForPage(int pageIndex)
    {
        var span = GetActiveSelectionSpan();
        if (span is null || _vm?.ActivePdfTab is null) return [];
        if (pageIndex < span.StartPageIndex || pageIndex > span.EndPageIndex) return [];
        if (!_vm.ActivePdfTab.TextLayoutCache.TryGetValue(pageIndex, out var layout) || layout.Chars.Count == 0)
        {
            return [];
        }

        int start = pageIndex == span.StartPageIndex ? span.StartCharIndex : 0;
        int end = pageIndex == span.EndPageIndex ? span.EndCharIndex : layout.Chars.Count - 1;
        return layout.GetMergedLineBoxes(start, end);
    }

    private bool TryFindSlotForSelection(Point viewportPos, List<PageSlot> slots, out PageSlot slot, out PdfPoint pagePt)
    {
        if (TryHitPage(viewportPos, out slot, out pagePt))
        {
            return true;
        }

        if (slots.Count == 0)
        {
            slot = default!;
            pagePt = new PdfPoint(0, 0);
            return false;
        }

        double docY = viewportPos.Y + _scrollOffsetY;
        PageSlot best = slots[0];
        double bestDist = double.MaxValue;
        foreach (var s in slots)
        {
            double dist = docY < s.TopDip ? s.TopDip - docY
                : docY > s.TopDip + s.HeightDip ? docY - (s.TopDip + s.HeightDip)
                : 0;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = s;
            }
        }

        slot = best;
        pagePt = ScreenToPagePoints(viewportPos, best, clampToPage: true);
        return true;
    }

    private void UpdateHoverTextCursor(Point pos)
    {
        if (_vm is null) return;
        if (_isPanning || _isSpacebarHeld)
        {
            UpdateCursorForTool();
            return;
        }

        if (_vm.ActiveTool == PdfToolKind.Select)
        {
            if (TryHitPage(pos, out var slot, out var pt))
            {
                if (FindFormFieldAtPoint(slot.PageIndex, pt) is not null || FindAnnotationAtPoint(slot.PageIndex, pt) is not null)
                {
                    Cursor = new Cursor(StandardCursorType.Hand);
                    return;
                }

                if (_vm.ActivePdfTab?.TextLayoutCache.TryGetValue(slot.PageIndex, out var layout) == true)
                {
                    Cursor = layout.HitTestChar(pt.XPt, pt.YPt, 6.0) >= 0
                        ? new Cursor(StandardCursorType.Ibeam)
                        : new Cursor(StandardCursorType.Arrow);
                    return;
                }

                _ = EnsurePageTextLayoutAsync(slot.PageIndex);
            }
            Cursor = new Cursor(StandardCursorType.Arrow);
            return;
        }

        if (_vm.ActiveTool == PdfToolKind.EditText)
        {
            PdfTextBlock? nextHover = null;
            int nextPage = -1;
            if (TryHitPage(pos, out var slot, out var pt))
            {
                if (_vm.ActivePdfTab?.TextLayoutCache.TryGetValue(slot.PageIndex, out var layout) == true)
                {
                    nextHover = layout.HitTestBlock(pt.XPt, pt.YPt, 6.0);
                    nextPage = nextHover is not null ? slot.PageIndex : -1;
                }
                else
                {
                    _ = EnsurePageTextLayoutAsync(slot.PageIndex);
                }
            }

            Cursor = nextHover is not null
                ? new Cursor(StandardCursorType.Ibeam)
                : new Cursor(StandardCursorType.Arrow);

            if (!Equals(nextHover, _hoveredTextBlock) || nextPage != _hoveredTextBlockPageIndex)
            {
                _hoveredTextBlock = nextHover;
                _hoveredTextBlockPageIndex = nextPage;
                InvalidateVisual();
            }
        }
    }

    private void CommitLiveTextSelectionToViewModel()
    {
        if (_vm?.ActivePdfTab is null) return;
        var span = GetActiveSelectionSpan();
        if (span is null)
        {
            _vm.ClearPdfTextSelection();
            return;
        }

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        var sb = new System.Text.StringBuilder();
        var lineBoxesPt = new List<(int PageIndex, PdfRect BoundsPt)>();
        Rect? anchorDip = null;

        for (int p = span.StartPageIndex; p <= span.EndPageIndex; p++)
        {
            if (!_vm.ActivePdfTab.TextLayoutCache.TryGetValue(p, out var layout) || layout.Chars.Count == 0)
            {
                continue;
            }

            int start = p == span.StartPageIndex ? span.StartCharIndex : 0;
            int end = p == span.EndPageIndex ? span.EndCharIndex : layout.Chars.Count - 1;
            string pageText = layout.ExtractRangeText(start, end);
            if (!string.IsNullOrEmpty(pageText))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(pageText);
            }

            var boxes = layout.GetMergedLineBoxes(start, end);
            foreach (var box in boxes)
            {
                lineBoxesPt.Add((p, box));
            }

            if (anchorDip is null && boxes.Count > 0 && p < slots.Count)
            {
                var slot = slots[p];
                double sx = slot.Info.WidthPoints > 0 ? slot.WidthDip / slot.Info.WidthPoints : 1.0;
                double sy = slot.Info.HeightPoints > 0 ? slot.HeightDip / slot.Info.HeightPoints : 1.0;
                var first = boxes[0];
                double left = slot.LeftDip - _scrollOffsetX + first.Left * sx;
                double top = slot.TopDip - _scrollOffsetY + first.Top * sy;
                double w = Math.Max(8, (first.Right - first.Left) * sx);
                double h = Math.Max(10, (first.Bottom - first.Top) * sy);
                anchorDip = new Rect(left, top, w, h);
            }
        }

        string selectedText = sb.ToString().Trim();
        if (string.IsNullOrEmpty(selectedText) || lineBoxesPt.Count == 0 || anchorDip is null)
        {
            _vm.ClearPdfTextSelection();
            return;
        }

        _vm.UpdatePdfTextSelection(span, selectedText, lineBoxesPt, anchorDip.Value, Bounds.Width, Bounds.Height);
    }

    private void SyncOverlayPositions()
    {
        if (_vm?.ActivePdfTab is null) return;

        if (_vm.ActivePdfTab.ActiveSelection is not null && !string.IsNullOrEmpty(_vm.SelectedPdfText))
        {
            CommitLiveTextSelectionToViewModel();
        }

        if (_vm.IsInlinePdfTextEditOpen && _vm.TryGetEditingPdfBlockInfo(out int editPageIdx, out var blockPt, out _))
        {
            var slots = ComputeLayout(_animatedZoom, out _, out _);
            if (editPageIdx >= 0 && editPageIdx < slots.Count)
            {
                var slot = slots[editPageIdx];
                double sx = slot.Info.WidthPoints > 0 ? slot.WidthDip / slot.Info.WidthPoints : 1.0;
                double sy = slot.Info.HeightPoints > 0 ? slot.HeightDip / slot.Info.HeightPoints : 1.0;
                var boundsDip = new Rect(
                    slot.LeftDip - _scrollOffsetX + blockPt.Left * sx,
                    slot.TopDip - _scrollOffsetY + blockPt.Top * sy,
                    Math.Max(60, (blockPt.Right - blockPt.Left) * sx),
                    Math.Max(20, (blockPt.Bottom - blockPt.Top) * sy));
                _vm.UpdateInlinePdfTextEditLayout(boundsDip, _animatedZoom / 100.0);
            }
        }
    }

    private async Task TryOpenInlineTextEditAsync(PageSlot slot, PdfPoint pagePt)
    {
        if (_vm?.ActivePdfTab is null) return;
        var layout = await _vm.GetOrLoadPageTextLayoutAsync(slot.PageIndex);
        if (layout is null) return;

        var existingEdit = _vm.ActivePdfTab.EditPlan.TextEdits.LastOrDefault(ed =>
            ed.PageIndex == slot.PageIndex &&
            pagePt.XPt >= ed.OriginalBoundsPt.Left - 4 && pagePt.XPt <= ed.OriginalBoundsPt.Right + 4 &&
            pagePt.YPt >= ed.OriginalBoundsPt.Top - 4 && pagePt.YPt <= ed.OriginalBoundsPt.Bottom + 4);

        var block = layout.HitTestBlock(pagePt.XPt, pagePt.YPt, 6.0);
        if (block is null && existingEdit is not null)
        {
            block = new PdfTextBlock(
                slot.PageIndex,
                0,
                0,
                existingEdit.OriginalBoundsPt,
                existingEdit.FontSizePt,
                existingEdit.OriginalText,
                existingEdit.FontFamily);
        }

        if (block is null)
        {
            _vm.CancelInlinePdfTextEditCommand.Execute(null);
            return;
        }

        double sx = slot.Info.WidthPoints > 0 ? slot.WidthDip / slot.Info.WidthPoints : 1.0;
        double sy = slot.Info.HeightPoints > 0 ? slot.HeightDip / slot.Info.HeightPoints : 1.0;
        var boundsDip = new Rect(
            slot.LeftDip - _scrollOffsetX + block.BoundsPt.Left * sx,
            slot.TopDip - _scrollOffsetY + block.BoundsPt.Top * sy,
            Math.Max(60, (block.BoundsPt.Right - block.BoundsPt.Left) * sx),
            Math.Max(20, (block.BoundsPt.Bottom - block.BoundsPt.Top) * sy));

        _vm.BeginInlinePdfTextEdit(slot.PageIndex, block, boundsDip, _animatedZoom / 100.0, existingEdit);
    }

    private sealed class PdfViewportDrawOperation(
        Rect bounds,
        LruBitmapCache cache,
        IReadOnlyList<VisiblePageRenderItem> pages,
        bool isDarkMode,
        SKColor pageBorderColor,
        SKColor pageShadowColor,
        ScrollBarMetrics vBar,
        ScrollBarMetrics hBar,
        double scrollbarOpacity,
        bool vBarActive,
        bool hBarActive,
        Func<PdfAnnotation, SKBitmap?> resolveSignatureBitmap) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var hqSampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            using var shadowPaint = new SKPaint
            {
                Color = pageShadowColor,
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12f)
            };

            using var paperPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            using var borderPaint = new SKPaint
            {
                Color = pageBorderColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = false
            };
            using var bitmapPaint = new SKPaint { IsAntialias = true };

            using var matchFill = new SKPaint { Color = new SKColor(0xFA, 0xCC, 0x15, 0x66), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var matchStroke = new SKPaint { Color = new SKColor(0xEA, 0xB3, 0x08, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            using var activeMatchFill = new SKPaint { Color = new SKColor(0xF9, 0x73, 0x16, 0x99), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var activeMatchStroke = new SKPaint { Color = new SKColor(0xEA, 0x58, 0x0C, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 2.0f, IsAntialias = true };

            using var textTypeface = SKTypeface.FromFamilyName("Arial");
            using var uiFont = new SKFont(textTypeface, 12f);

            foreach (var page in pages)
            {
                var dest = new SKRect(
                    (float)page.DestRectDip.X,
                    (float)page.DestRectDip.Y,
                    (float)(page.DestRectDip.X + page.DestRectDip.Width),
                    (float)(page.DestRectDip.Y + page.DestRectDip.Height));

                var shadowRect = dest;
                shadowRect.Offset(0f, 6f);
                canvas.DrawRect(shadowRect, shadowPaint);
                canvas.DrawRect(dest, paperPaint);

                if (!page.Info.IsBlankPage)
                {
                    bool drewExact = cache.TryUse(page.ExactKey, bmp =>
                    {
                        using var skImg = SKImage.FromBitmap(bmp);
                        canvas.DrawImage(skImg, dest, hqSampling, bitmapPaint);
                    });

                    if (!drewExact)
                    {
                        cache.TryUseAnyResolutionForPage(page.ExactKey.PageIndex, page.ExactKey.RotationQuarterTurns, bmp =>
                        {
                            using var skImg = SKImage.FromBitmap(bmp);
                            canvas.DrawImage(skImg, dest, hqSampling, bitmapPaint);
                        });
                    }
                }

                double scaleX = page.Info.WidthPoints > 0 ? page.DestRectDip.Width / page.Info.WidthPoints : 1.0;
                double scaleY = page.Info.HeightPoints > 0 ? page.DestRectDip.Height / page.Info.HeightPoints : 1.0;

                // Live In-Place PDF Text Edits preview (white-out original glyphs & paint replacement text)
                foreach (var te in page.TextEdits)
                {
                    var tr = ToScreenRect(dest, te.OriginalBoundsPt, scaleX, scaleY);
                    tr.Inflate(1.5f, 1.5f);
                    using var coverPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(tr, coverPaint);

                    if (!string.IsNullOrEmpty(te.NewText))
                    {
                        using var editTypeface = SKTypeface.FromFamilyName(string.IsNullOrWhiteSpace(te.FontFamily) ? "Arial" : te.FontFamily);
                        using var editFont = new SKFont(editTypeface, (float)(Math.Max(6.0, te.FontSizePt) * scaleY));
                        using var editPaint = new SKPaint { Color = ParseSkColor(te.ColorHex, SKColors.Black), IsAntialias = true };
                        float baselineY = tr.Top + (float)(te.FontSizePt * 0.85 * scaleY);
                        var lines = te.NewText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                        float lineAdvance = (float)(te.FontSizePt * 1.2 * scaleY);
                        for (int li = 0; li < lines.Length; li++)
                        {
                            canvas.DrawText(lines[li], tr.Left + 1.5f, baselineY + li * lineAdvance, SKTextAlign.Left, editFont, editPaint);
                        }
                    }
                }

                // Hovered editable text block indicator in EditText (E) mode
                if (page.HoveredTextBlockBoundsPt is { } hoverPt)
                {
                    var hr = ToScreenRect(dest, hoverPt, scaleX, scaleY);
                    hr.Inflate(3f, 2f);
                    using var dashEffect = SKPathEffect.CreateDash([4f, 3f], 0f);
                    using var hoverStroke = new SKPaint
                    {
                        Color = new SKColor(0x3B, 0x82, 0xF6, 0xD0),
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1.25f,
                        PathEffect = dashEffect,
                        IsAntialias = true
                    };
                    canvas.DrawRoundRect(hr, 2f, 2f, hoverStroke);
                }

                // Active PDF text selection highlight boxes
                if (page.SelectionBoxesPt.Count > 0)
                {
                    using var selPaint = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 0x52), Style = SKPaintStyle.Fill, IsAntialias = true };
                    foreach (var box in page.SelectionBoxesPt)
                    {
                        var sr = ToScreenRect(dest, box, scaleX, scaleY);
                        sr.Inflate(1f, 1f);
                        canvas.DrawRoundRect(sr, 2f, 2f, selPaint);
                    }
                }

                // Interactive Form Fields
                foreach (var field in page.FormFields)
                {
                    var fr = ToScreenRect(dest, field.BoundsPt, scaleX, scaleY);
                    using var fieldFill = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 0x18), Style = SKPaintStyle.Fill, IsAntialias = true };
                    using var fieldStroke = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 0x99), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
                    canvas.DrawRoundRect(fr, 2f, 2f, fieldFill);
                    canvas.DrawRoundRect(fr, 2f, 2f, fieldStroke);

                    if (field.Kind == PdfFormFieldKind.CheckBox && field.IsChecked)
                    {
                        using var checkPaint = new SKPaint { Color = new SKColor(0x25, 0x63, 0xEB, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
                        using var path = new SKPath();
                        path.MoveTo(fr.Left + fr.Width * 0.22f, fr.Top + fr.Height * 0.54f);
                        path.LineTo(fr.Left + fr.Width * 0.44f, fr.Top + fr.Height * 0.76f);
                        path.LineTo(fr.Left + fr.Width * 0.78f, fr.Top + fr.Height * 0.24f);
                        canvas.DrawPath(path, checkPaint);
                    }
                    else if (!string.IsNullOrEmpty(field.Value))
                    {
                        using var valPaint = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27, 0xFF), IsAntialias = true };
                        uiFont.Size = Math.Clamp(fr.Height * 0.65f, 9f, 14f);
                        canvas.DrawText(field.Value, fr.Left + 4f, fr.MidY + uiFont.Size * 0.35f, SKTextAlign.Left, uiFont, valPaint);
                    }
                }

                // Phase 4 Annotations & Redactions
                foreach (var ann in page.Annotations)
                {
                    var ar = ToScreenRect(dest, ann.BoundsPt, scaleX, scaleY);
                    bool isSelected = page.SelectedAnnotationId == ann.Id;

                    switch (ann.Kind)
                    {
                        case AnnotationKind.Highlight:
                        {
                            var baseColor = ParseSkColor(ann.ColorHex, new SKColor(0xFA, 0xCC, 0x15));
                            using var p = new SKPaint { Color = baseColor.WithAlpha(0x66), Style = SKPaintStyle.Fill, IsAntialias = true };
                            if (ann.HighlightRectsPt.Count > 0)
                            {
                                foreach (var hr in ann.HighlightRectsPt)
                                {
                                    canvas.DrawRoundRect(ToScreenRect(dest, hr, scaleX, scaleY), 2f, 2f, p);
                                }
                            }
                            else
                            {
                                canvas.DrawRoundRect(ar, 2f, 2f, p);
                            }
                            break;
                        }
                        case AnnotationKind.Underline:
                        {
                            var baseColor = ParseSkColor(ann.ColorHex, new SKColor(0xEA, 0xB3, 0x08));
                            using var p = new SKPaint { Color = baseColor.WithAlpha(0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.0f, (float)(ann.StrokeWidthPt * scaleY)), IsAntialias = true };
                            if (ann.HighlightRectsPt.Count > 0)
                            {
                                foreach (var hr in ann.HighlightRectsPt)
                                {
                                    var sr = ToScreenRect(dest, hr, scaleX, scaleY);
                                    canvas.DrawLine(sr.Left, sr.Bottom, sr.Right, sr.Bottom, p);
                                }
                            }
                            else
                            {
                                canvas.DrawLine(ar.Left, ar.Bottom, ar.Right, ar.Bottom, p);
                            }
                            break;
                        }
                        case AnnotationKind.Strikeout:
                        {
                            var baseColor = ParseSkColor(ann.ColorHex, new SKColor(0xEF, 0x44, 0x44));
                            using var p = new SKPaint { Color = baseColor.WithAlpha(0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.25f, (float)(ann.StrokeWidthPt * scaleY)), IsAntialias = true };
                            if (ann.HighlightRectsPt.Count > 0)
                            {
                                foreach (var hr in ann.HighlightRectsPt)
                                {
                                    var sr = ToScreenRect(dest, hr, scaleX, scaleY);
                                    float midY = (sr.Top + sr.Bottom) * 0.52f;
                                    canvas.DrawLine(sr.Left, midY, sr.Right, midY, p);
                                }
                            }
                            else
                            {
                                float midY = (ar.Top + ar.Bottom) * 0.52f;
                                canvas.DrawLine(ar.Left, midY, ar.Right, midY, p);
                            }
                            break;
                        }
                        case AnnotationKind.FreehandDraw:
                        {
                            if (ann.InkPointsPt.Count >= 2)
                            {
                                var baseColor = ParseSkColor(ann.ColorHex, new SKColor(0x3B, 0x82, 0xF6));
                                using var p = new SKPaint
                                {
                                    Color = baseColor.WithAlpha(0xFF),
                                    Style = SKPaintStyle.Stroke,
                                    StrokeWidth = Math.Max(1.5f, (float)(ann.StrokeWidthPt * scaleX)),
                                    StrokeCap = SKStrokeCap.Round,
                                    StrokeJoin = SKStrokeJoin.Round,
                                    IsAntialias = true
                                };
                                using var path = new SKPath();
                                path.MoveTo((float)(dest.Left + ann.InkPointsPt[0].XPt * scaleX), (float)(dest.Top + ann.InkPointsPt[0].YPt * scaleY));
                                for (int i = 1; i < ann.InkPointsPt.Count; i++)
                                {
                                    path.LineTo((float)(dest.Left + ann.InkPointsPt[i].XPt * scaleX), (float)(dest.Top + ann.InkPointsPt[i].YPt * scaleY));
                                }
                                canvas.DrawPath(path, p);
                            }
                            break;
                        }
                        case AnnotationKind.TextBox:
                        {
                            var baseColor = ParseSkColor(ann.ColorHex, new SKColor(0xDC, 0x26, 0x26));
                            using var txtPaint = new SKPaint { Color = baseColor.WithAlpha(0xFF), IsAntialias = true };
                            double fontPt = ann.FontSizePt > 1.0 ? ann.FontSizePt : 12.0;
                            uiFont.Size = (float)(fontPt * scaleY);
                            string label = string.IsNullOrWhiteSpace(ann.TextContent) ? "Text" : ann.TextContent;
                            canvas.DrawText(label, ar.Left + 2f, ar.Top + (float)((fontPt + 2.0) * scaleY), SKTextAlign.Left, uiFont, txtPaint);
                            break;
                        }
                        case AnnotationKind.StickyNote:
                        {
                            using var fill = new SKPaint { Color = new SKColor(0xFE, 0xF0, 0x8A, 0xF2), Style = SKPaintStyle.Fill, IsAntialias = true };
                            using var stroke = new SKPaint { Color = new SKColor(0xCA, 0x8A, 0x04, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, IsAntialias = true };
                            canvas.DrawRoundRect(ar, 4f, 4f, fill);
                            canvas.DrawRoundRect(ar, 4f, 4f, stroke);
                            using var noteTxt = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27, 0xFF), IsAntialias = true };
                            uiFont.Size = (float)(10.0 * scaleY);
                            string label = string.IsNullOrWhiteSpace(ann.TextContent) ? "Note" : ann.TextContent;
                            canvas.DrawText(label, ar.Left + 6f, ar.Top + (float)(15.0 * scaleY), SKTextAlign.Left, uiFont, noteTxt);
                            break;
                        }
                        case AnnotationKind.Redaction:
                        {
                            using var fill = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27, 0xD9), Style = SKPaintStyle.Fill, IsAntialias = true };
                            using var stroke = new SKPaint { Color = new SKColor(0xEF, 0x44, 0x44, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                            canvas.DrawRect(ar, fill);
                            canvas.DrawRect(ar, stroke);
                            break;
                        }
                        case AnnotationKind.SignatureStamp:
                        {
                            var sigBmp = resolveSignatureBitmap(ann);
                            if (sigBmp is not null)
                            {
                                using var sigImg = SKImage.FromBitmap(sigBmp);
                                canvas.DrawImage(sigImg, ar, hqSampling, bitmapPaint);
                            }
                            break;
                        }
                    }

                    if (isSelected)
                    {
                        using var selStroke = new SKPaint
                        {
                            Color = new SKColor(0x3B, 0x82, 0xF6, 0xFF),
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 1.5f,
                            IsAntialias = true
                        };
                        var outline = ar;
                        outline.Inflate(2f, 2f);
                        canvas.DrawRect(outline, selStroke);

                        var handle = new SKRect(outline.Right - 4f, outline.Bottom - 4f, outline.Right + 4f, outline.Bottom + 4f);
                        using var handleFill = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 0xFF), Style = SKPaintStyle.Fill };
                        using var handleStroke = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f };
                        canvas.DrawRect(handle, handleFill);
                        canvas.DrawRect(handle, handleStroke);
                    }
                }

                // High-visibility search bounding boxes
                if (page.Matches.Count > 0 && page.Info.WidthPoints > 0 && page.Info.HeightPoints > 0)
                {
                    foreach (var match in page.Matches)
                    {
                        bool isActive = page.ActiveMatch is not null &&
                                        match.PageIndex == page.ActiveMatch.PageIndex &&
                                        match.CharIndex == page.ActiveMatch.CharIndex;

                        var fillPaint = isActive ? activeMatchFill : matchFill;
                        var strokePaint = isActive ? activeMatchStroke : matchStroke;

                        foreach (var span in MergeLineBoxes(match.Bounds))
                        {
                            float left = (float)(dest.Left + span.Left * scaleX);
                            float right = (float)(dest.Left + span.Right * scaleX);
                            float top = (float)(dest.Top + (page.Info.HeightPoints - span.Top) * scaleY);
                            float bottom = (float)(dest.Top + (page.Info.HeightPoints - span.Bottom) * scaleY);

                            var highlightRect = new SKRect(left, top, right, bottom);
                            highlightRect.Inflate(2f, 2f);
                            canvas.DrawRoundRect(highlightRect, 2f, 2f, fillPaint);
                            canvas.DrawRoundRect(highlightRect, 2f, 2f, strokePaint);
                        }
                    }
                }

                var outerBorderRect = dest;
                outerBorderRect.Inflate(0.5f, 0.5f);
                canvas.DrawRect(outerBorderRect, borderPaint);
            }

            // Custom Sleek Pill Scrollbars with fast 350ms auto-collapse & 120ms transitions
            if (scrollbarOpacity > 0.01)
            {
                byte alphaScale(byte baseAlpha) => (byte)Math.Clamp(Math.Round(baseAlpha * scrollbarOpacity), 0, 255);

                var trackColor = isDarkMode
                    ? new SKColor(0xFF, 0xFF, 0xFF, alphaScale(0x18))
                    : new SKColor(0x11, 0x18, 0x27, alphaScale(0x12));

                if (vBar.IsVisible)
                {
                    var thumbColor = isDarkMode
                        ? new SKColor(0xD1, 0xD5, 0xDB, alphaScale(vBarActive ? (byte)0xB0 : (byte)0x68))
                        : new SKColor(0x4B, 0x55, 0x63, alphaScale(vBarActive ? (byte)0xB0 : (byte)0x68));

                    DrawPillScrollBar(canvas, vBar, trackColor, thumbColor);
                }

                if (hBar.IsVisible)
                {
                    var thumbColor = isDarkMode
                        ? new SKColor(0xD1, 0xD5, 0xDB, alphaScale(hBarActive ? (byte)0xB0 : (byte)0x68))
                        : new SKColor(0x4B, 0x55, 0x63, alphaScale(hBarActive ? (byte)0xB0 : (byte)0x68));

                    DrawPillScrollBar(canvas, hBar, trackColor, thumbColor);
                }
            }
        }

        private static void DrawPillScrollBar(SKCanvas canvas, ScrollBarMetrics metrics, SKColor trackColor, SKColor thumbColor)
        {
            var trackRect = new SKRect(
                (float)metrics.TrackRect.X,
                (float)metrics.TrackRect.Y,
                (float)metrics.TrackRect.Right,
                (float)metrics.TrackRect.Bottom);
            float trackRadius = Math.Min(trackRect.Width, trackRect.Height) * 0.5f;
            using var trackPaint = new SKPaint { Color = trackColor, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(trackRect, trackRadius, trackRadius, trackPaint);

            var thumbRect = new SKRect(
                (float)metrics.ThumbRect.X,
                (float)metrics.ThumbRect.Y,
                (float)metrics.ThumbRect.Right,
                (float)metrics.ThumbRect.Bottom);
            float thumbRadius = Math.Min(thumbRect.Width, thumbRect.Height) * 0.5f;
            using var thumbPaint = new SKPaint { Color = thumbColor, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, thumbPaint);
        }

        private static SKColor ParseSkColor(string? hex, SKColor fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return SKColor.TryParse(hex, out var parsed) ? parsed : fallback;
        }

        private static SKRect ToScreenRect(SKRect pageDest, PdfRect boundsPt, double scaleX, double scaleY) => new(
            (float)(pageDest.Left + Math.Min(boundsPt.Left, boundsPt.Right) * scaleX),
            (float)(pageDest.Top + Math.Min(boundsPt.Top, boundsPt.Bottom) * scaleY),
            (float)(pageDest.Left + Math.Max(boundsPt.Left, boundsPt.Right) * scaleX),
            (float)(pageDest.Top + Math.Max(boundsPt.Top, boundsPt.Bottom) * scaleY));

        private static IEnumerable<PdfRect> MergeLineBoxes(IReadOnlyList<PdfRect> boxes)
        {
            if (boxes.Count == 0) yield break;

            var current = boxes[0];
            for (int i = 1; i < boxes.Count; i++)
            {
                var next = boxes[i];
                if (Math.Abs(next.Bottom - current.Bottom) < 3.0)
                {
                    current = new PdfRect(
                        Math.Min(current.Left, next.Left),
                        Math.Max(current.Top, next.Top),
                        Math.Max(current.Right, next.Right),
                        Math.Min(current.Bottom, next.Bottom));
                }
                else
                {
                    yield return current;
                    current = next;
                }
            }
            yield return current;
        }
    }
}
