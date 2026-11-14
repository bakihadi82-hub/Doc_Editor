using Avalonia;
using Avalonia.Input;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;

namespace PdfStudio.App.Controls;

public sealed partial class PdfCanvasControl
{
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

    private bool TryBeginTextSelection(PageSlot slot, PdfPoint pagePt, PointerPressedEventArgs e)
    {
        if (_vm is null) return false;

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
                return true;
            }
        }
        else
        {
            _ = EnsurePageTextLayoutAsync(slot.PageIndex);
        }

        return false;
    }

    private void UpdateTextSelectionDrag(Point pos)
    {
        if (_vm is null) return;
        var slots = ComputeLayout(_animatedZoom, out _, out _);
        if (TryFindSlotForSelection(pos, slots, out var curSlot, out var curPt) &&
            _vm.ActivePdfTab?.TextLayoutCache.TryGetValue(curSlot.PageIndex, out var layout) == true)
        {
            int idx = layout.FindNearestCharOnOrNearPoint(curPt.XPt, curPt.YPt);
            if (idx >= 0 && (_selFocusPageIndex != curSlot.PageIndex || _selFocusCharIndex != idx))
            {
                _selFocusPageIndex = curSlot.PageIndex;
                _selFocusCharIndex = idx;
                _selDidDrag = true;
                InvalidateVisual();
            }
        }
        else if (TryFindSlotForSelection(pos, slots, out var loadSlot, out _))
        {
            _ = EnsurePageTextLayoutAsync(loadSlot.PageIndex);
        }
    }

    private void CompleteTextSelectionDrag()
    {
        if (_vm is null) return;
        if (_selDidDrag || _selWasMultiClick)
        {
            CommitLiveTextSelectionToViewModel();
        }
        else
        {
            _vm.ClearPdfTextSelection();
        }
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
}
