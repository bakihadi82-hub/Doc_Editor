using Avalonia;
using Avalonia.Input;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Forms;
using SkiaSharp;

namespace PdfStudio.App.Controls;

public sealed partial class PdfCanvasControl
{
    private bool TryHandlePageAnnotationOrFormPress(
        Point pos,
        PageSlot slot,
        PdfPoint pagePt,
        PointerPressedEventArgs e)
    {
        if (_vm is null) return false;

        // 1. Check interactive form field clicks in Select mode
        var hitField = FindFormFieldAtPoint(slot.PageIndex, pagePt);
        if (hitField is not null)
        {
            if (hitField.Kind == PdfFormFieldKind.CheckBox)
            {
                _vm.ToggleOrUpdateFormField(hitField, isChecked: !hitField.IsChecked);
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
            }
            e.Handled = true;
            return true;
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
                return true;
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
            return true;
        }

        _vm.SelectedAnnotation = null;
        return false;
    }

    private bool TryBeginAnnotationCreation(PageSlot slot, PdfPoint pagePt, PointerPressedEventArgs e)
    {
        if (_vm is null) return false;

        if (_vm.ActiveTool is PdfToolKind.Highlight or PdfToolKind.Underline or PdfToolKind.Redact)
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
            return true;
        }

        if (_vm.ActiveTool == PdfToolKind.Draw)
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
            return true;
        }

        if (_vm.ActiveTool is PdfToolKind.Text or PdfToolKind.Note)
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
            return true;
        }

        return false;
    }

    private void UpdateAnnotationDrag(Point pos)
    {
        if (_vm is null || _interactionPageIndex < 0) return;

        var slots = ComputeLayout(_animatedZoom, out _, out _);
        if (_interactionPageIndex >= slots.Count) return;

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

    private void CompleteAnnotationDrag()
    {
        if (_vm is null) return;

        if ((_interactionMode == DragInteractionMode.CreateRectAnnotation ||
             _interactionMode == DragInteractionMode.FreehandDraw) &&
            _draftAnnotation is not null)
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
        else if (_hasModifiedManipulatedAnnotation)
        {
            _vm.CommitAnnotationTransform();
        }
    }

    private void ResetInteractionState()
    {
        _interactionMode = DragInteractionMode.None;
        _interactionPageIndex = -1;
        _draftAnnotation = null;
        _manipulatedAnnotation = null;
        _initialInkPointsPt = null;
        _hasModifiedManipulatedAnnotation = false;
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

    private bool IsNearBottomRightHandle(Point viewportPos, PageSlot slot, PdfAnnotation ann, double dipPerPt)
    {
        double hx = slot.LeftDip - _scrollOffsetX + ann.BoundsPt.Right * dipPerPt;
        double hy = slot.TopDip - _scrollOffsetY + ann.BoundsPt.Bottom * dipPerPt;
        return Math.Abs(viewportPos.X - hx) <= 10.0 && Math.Abs(viewportPos.Y - hy) <= 10.0;
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
}
