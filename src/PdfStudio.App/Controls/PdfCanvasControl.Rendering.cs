using Avalonia;
using Avalonia.Media;
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

public sealed partial class PdfCanvasControl
{
    private static readonly SKColor DarkInvertPaperColor = new(0x1A, 0x1C, 0x24);

    /// <summary>
    /// 4x5 color-matrix inverting bright paper pages to warm dark slate (#1A1C24 -> #EDE8E1 text).
    /// </summary>
    public static readonly float[] DarkInvertWarmColorMatrix =
    [
        -0.827f,  0.000f,  0.000f, 0.0f, 0.929f,
         0.000f, -0.800f,  0.000f, 0.0f, 0.910f,
         0.000f,  0.000f, -0.741f, 0.0f, 0.882f,
         0.000f,  0.000f,  0.000f, 1.0f, 0.000f
    ];

    public static SKColorFilter CreateDarkInvertColorFilter() =>
        SKColorFilter.CreateColorMatrix(DarkInvertWarmColorMatrix);

    /// <summary>
    /// Applies the Skia dark-mode inversion color filter to a single color sample.
    /// </summary>
    public static SKColor TransformColorForDarkInvert(SKColor color)
    {
        using var src = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        src.SetPixel(0, 0, color);
        using var dst = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        using var filter = CreateDarkInvertColorFilter();
        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(src, 0, 0, paint);
        return dst.GetPixel(0, 0);
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
                _vm.IsPdfDarkInvertEnabled,
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

    private sealed class PdfViewportDrawOperation(
        Rect bounds,
        LruBitmapCache cache,
        IReadOnlyList<VisiblePageRenderItem> pages,
        bool isDarkMode,
        bool isDarkInvertEnabled,
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

            using var paperPaint = new SKPaint
            {
                Color = isDarkInvertEnabled ? DarkInvertPaperColor : SKColors.White,
                Style = SKPaintStyle.Fill
            };
            using var borderPaint = new SKPaint
            {
                Color = pageBorderColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = false
            };
            using var darkInvertFilter = isDarkInvertEnabled ? CreateDarkInvertColorFilter() : null;
            using var bitmapPaint = new SKPaint { IsAntialias = true, ColorFilter = darkInvertFilter };
            using var rawBitmapPaint = new SKPaint { IsAntialias = true };

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

                DrawPageTextEdits(canvas, dest, page, scaleX, scaleY);
                DrawHoveredTextBlock(canvas, dest, page, scaleX, scaleY);
                DrawSelectionBoxes(canvas, dest, page, scaleX, scaleY);
                DrawFormFields(canvas, dest, page, uiFont, scaleX, scaleY);
                DrawAnnotations(canvas, dest, page, uiFont, hqSampling, rawBitmapPaint, scaleX, scaleY);
                DrawSearchMatches(canvas, dest, page, scaleX, scaleY, matchFill, matchStroke, activeMatchFill, activeMatchStroke);

                var outerBorderRect = dest;
                outerBorderRect.Inflate(0.5f, 0.5f);
                canvas.DrawRect(outerBorderRect, borderPaint);
            }

            DrawScrollBars(canvas);
        }

        private void DrawPageTextEdits(SKCanvas canvas, SKRect dest, VisiblePageRenderItem page, double scaleX, double scaleY)
        {
            foreach (var te in page.TextEdits)
            {
                var tr = ToScreenRect(dest, te.OriginalBoundsPt, scaleX, scaleY);
                tr.Inflate(1.5f, 1.5f);
                using var coverPaint = new SKPaint
                {
                    Color = isDarkInvertEnabled ? DarkInvertPaperColor : SKColors.White,
                    Style = SKPaintStyle.Fill
                };
                canvas.DrawRect(tr, coverPaint);

                if (!string.IsNullOrEmpty(te.NewText))
                {
                    using var editTypeface = SKTypeface.FromFamilyName(string.IsNullOrWhiteSpace(te.FontFamily) ? "Arial" : te.FontFamily);
                    using var editFont = new SKFont(editTypeface, (float)(Math.Max(6.0, te.FontSizePt) * scaleY));
                    var textColor = ParseSkColor(te.ColorHex, SKColors.Black);
                    if (isDarkInvertEnabled)
                    {
                        textColor = TransformColorForDarkInvert(textColor);
                    }
                    using var editPaint = new SKPaint { Color = textColor, IsAntialias = true };
                    float baselineY = tr.Top + (float)(te.FontSizePt * 0.85 * scaleY);
                    var lines = te.NewText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    float lineAdvance = (float)(te.FontSizePt * 1.2 * scaleY);
                    for (int li = 0; li < lines.Length; li++)
                    {
                        canvas.DrawText(lines[li], tr.Left + 1.5f, baselineY + li * lineAdvance, SKTextAlign.Left, editFont, editPaint);
                    }
                }
            }
        }

        private static void DrawHoveredTextBlock(SKCanvas canvas, SKRect dest, VisiblePageRenderItem page, double scaleX, double scaleY)
        {
            if (page.HoveredTextBlockBoundsPt is not { } hoverPt) return;
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

        private static void DrawSelectionBoxes(SKCanvas canvas, SKRect dest, VisiblePageRenderItem page, double scaleX, double scaleY)
        {
            if (page.SelectionBoxesPt.Count == 0) return;
            using var selPaint = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 0x52), Style = SKPaintStyle.Fill, IsAntialias = true };
            foreach (var box in page.SelectionBoxesPt)
            {
                var sr = ToScreenRect(dest, box, scaleX, scaleY);
                sr.Inflate(1f, 1f);
                canvas.DrawRoundRect(sr, 2f, 2f, selPaint);
            }
        }

        private void DrawFormFields(SKCanvas canvas, SKRect dest, VisiblePageRenderItem page, SKFont uiFont, double scaleX, double scaleY)
        {
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
                    var textColor = isDarkInvertEnabled
                        ? new SKColor(0xED, 0xE8, 0xE1, 0xFF)
                        : new SKColor(0x11, 0x18, 0x27, 0xFF);
                    using var valPaint = new SKPaint { Color = textColor, IsAntialias = true };
                    uiFont.Size = Math.Clamp(fr.Height * 0.65f, 9f, 14f);
                    canvas.DrawText(field.Value, fr.Left + 4f, fr.MidY + uiFont.Size * 0.35f, SKTextAlign.Left, uiFont, valPaint);
                }
            }
        }

        private void DrawAnnotations(
            SKCanvas canvas,
            SKRect dest,
            VisiblePageRenderItem page,
            SKFont uiFont,
            SKSamplingOptions hqSampling,
            SKPaint rawBitmapPaint,
            double scaleX,
            double scaleY)
        {
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
                            canvas.DrawImage(sigImg, ar, hqSampling, rawBitmapPaint);
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
        }

        private static void DrawSearchMatches(
            SKCanvas canvas,
            SKRect dest,
            VisiblePageRenderItem page,
            double scaleX,
            double scaleY,
            SKPaint matchFill,
            SKPaint matchStroke,
            SKPaint activeMatchFill,
            SKPaint activeMatchStroke)
        {
            if (page.Matches.Count == 0 || page.Info.WidthPoints <= 0 || page.Info.HeightPoints <= 0) return;

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

        private void DrawScrollBars(SKCanvas canvas)
        {
            if (scrollbarOpacity <= 0.01) return;

            byte ScaleAlpha(byte baseAlpha) => (byte)Math.Clamp(Math.Round(baseAlpha * scrollbarOpacity), 0, 255);

            var trackColor = isDarkMode
                ? new SKColor(0xFF, 0xFF, 0xFF, ScaleAlpha(0x18))
                : new SKColor(0x11, 0x18, 0x27, ScaleAlpha(0x12));

            if (vBar.IsVisible)
            {
                var thumbColor = isDarkMode
                    ? new SKColor(0xD1, 0xD5, 0xDB, ScaleAlpha(vBarActive ? (byte)0xB0 : (byte)0x68))
                    : new SKColor(0x4B, 0x55, 0x63, ScaleAlpha(vBarActive ? (byte)0xB0 : (byte)0x68));

                DrawPillScrollBar(canvas, vBar, trackColor, thumbColor);
            }

            if (hBar.IsVisible)
            {
                var thumbColor = isDarkMode
                    ? new SKColor(0xD1, 0xD5, 0xDB, ScaleAlpha(hBarActive ? (byte)0xB0 : (byte)0x68))
                    : new SKColor(0x4B, 0x55, 0x63, ScaleAlpha(hBarActive ? (byte)0xB0 : (byte)0x68));

                DrawPillScrollBar(canvas, hBar, trackColor, thumbColor);
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
