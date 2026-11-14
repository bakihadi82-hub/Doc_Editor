using Avalonia;
using Avalonia.Input;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;

namespace PdfStudio.App.Controls;

public sealed partial class PdfCanvasControl
{
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

    // --- Pointer Interaction ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_vm is null || !_vm.HasOpenDocument) return;

        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed && TryBeginScrollbarDrag(pos, e))
        {
            return;
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
            if (_vm.ActiveTool == PdfToolKind.Select)
            {
                if (TryHandlePageAnnotationOrFormPress(pos, slot, pagePt, e))
                {
                    return;
                }

                if (TryBeginTextSelection(slot, pagePt, e))
                {
                    return;
                }

                _vm.ClearPdfTextSelection();
            }
            else if (_vm.ActiveTool == PdfToolKind.EditText)
            {
                _ = TryOpenInlineTextEditAsync(slot, pagePt);
                e.Handled = true;
                return;
            }
            else if (TryBeginAnnotationCreation(slot, pagePt, e))
            {
                return;
            }
        }

        // Fallback on empty canvas / Hand tool: smooth pan
        if (_vm.ActiveTool is PdfToolKind.Select or PdfToolKind.Hand)
        {
            BeginPan(pos, e);
        }
    }

    private bool TryBeginScrollbarDrag(Point pos, PointerPressedEventArgs e)
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
            return true;
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
            return true;
        }

        return false;
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

        UpdateScrollbarHover(pos);

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

        if (_interactionMode == DragInteractionMode.SelectText)
        {
            UpdateTextSelectionDrag(pos);
        }
        else if (_interactionMode != DragInteractionMode.None)
        {
            UpdateAnnotationDrag(pos);
        }
        else
        {
            UpdateHoverTextCursor(pos);
        }

        UpdateCursorCoordinates(pos);
    }

    private void UpdateScrollbarHover(Point pos)
    {
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

            if (_interactionMode == DragInteractionMode.SelectText)
            {
                CompleteTextSelectionDrag();
            }
            else
            {
                CompleteAnnotationDrag();
            }

            ResetInteractionState();
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
}
