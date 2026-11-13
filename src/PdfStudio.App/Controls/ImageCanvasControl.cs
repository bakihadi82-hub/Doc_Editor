using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using PdfStudio.App.ViewModels;
using SkiaSharp;

namespace PdfStudio.App.Controls;

public sealed class ImageCanvasControl : Control
{
    private ImageWorkspaceViewModel? _vm;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private bool _isDraggingCrop;
    private Point _lastPointer;
    private Point _cropStartImgPt;

    public ImageCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.RequestInvalidateCanvas -= InvalidateVisual;
            _vm.RequestFitToScreen -= FitToScreen;
        }

        base.OnDataContextChanged(e);

        _vm = DataContext as ImageWorkspaceViewModel;
        if (_vm is not null)
        {
            _vm.RequestInvalidateCanvas += InvalidateVisual;
            _vm.RequestFitToScreen += FitToScreen;
            _panX = 0;
            _panY = 0;
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null)
        {
            _vm.RequestInvalidateCanvas -= InvalidateVisual;
            _vm.RequestFitToScreen -= FitToScreen;
            _vm.RequestInvalidateCanvas += InvalidateVisual;
            _vm.RequestFitToScreen += FitToScreen;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.RequestInvalidateCanvas -= InvalidateVisual;
            _vm.RequestFitToScreen -= FitToScreen;
        }
        base.OnDetachedFromVisualTree(e);
    }

    public void FitToScreen()
    {
        if (_vm is null || _vm.ImageWidth <= 0 || _vm.ImageHeight <= 0 || Bounds.Width <= 40 || Bounds.Height <= 80)
        {
            return;
        }

        double availW = Bounds.Width - 64;
        double availH = Bounds.Height - 120;
        double scale = Math.Min(availW / _vm.ImageWidth, availH / _vm.ImageHeight);
        _vm.ZoomPercent = Math.Clamp((int)Math.Round(scale * 100.0), 10, 800);
        _panX = 0;
        _panY = 0;
        InvalidateVisual();
    }

    private Rect GetImageDestRectDip()
    {
        if (_vm is null || _vm.ImageWidth <= 0 || _vm.ImageHeight <= 0)
            return default;

        double scale = _vm.ZoomPercent / 100.0;
        double w = _vm.ImageWidth * scale;
        double h = _vm.ImageHeight * scale;
        double x = (Bounds.Width - w) * 0.5 - _panX;
        double y = (Bounds.Height - h - 48) * 0.5 - _panY;
        return new Rect(x, y, w, h);
    }

    private Point ScreenToImagePixels(Point pos, Rect dest)
    {
        if (_vm is null || dest.Width <= 0 || dest.Height <= 0) return default;
        double ix = Math.Clamp((pos.X - dest.X) / dest.Width * _vm.ImageWidth, 0, _vm.ImageWidth);
        double iy = Math.Clamp((pos.Y - dest.Y) / dest.Height * _vm.ImageHeight, 0, _vm.ImageHeight);
        return new Point(ix, iy);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is null) return;
        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);

        if (props.IsLeftButtonPressed && _vm.IsCropping)
        {
            var dest = GetImageDestRectDip();
            if (dest.Contains(pos))
            {
                _isDraggingCrop = true;
                _cropStartImgPt = ScreenToImagePixels(pos, dest);
                _vm.SetCropRegion((int)_cropStartImgPt.X, (int)_cropStartImgPt.Y, 1, 1);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPointer = pos;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is null) return;
        var pos = e.GetPosition(this);

        if (_isDraggingCrop)
        {
            var dest = GetImageDestRectDip();
            var cur = ScreenToImagePixels(pos, dest);
            int x = (int)Math.Min(_cropStartImgPt.X, cur.X);
            int y = (int)Math.Min(_cropStartImgPt.Y, cur.Y);
            int w = Math.Max(1, (int)Math.Abs(cur.X - _cropStartImgPt.X));
            int h = Math.Max(1, (int)Math.Abs(cur.Y - _cropStartImgPt.Y));
            _vm.SetCropRegion(x, y, w, h);
            InvalidateVisual();
            return;
        }

        if (_isPanning)
        {
            var delta = pos - _lastPointer;
            _lastPointer = pos;
            _panX -= delta.X;
            _panY -= delta.Y;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDraggingCrop || _isPanning)
        {
            _isDraggingCrop = false;
            _isPanning = false;
            Cursor = _vm?.IsCropping == true
                ? new Cursor(StandardCursorType.Cross)
                : new Cursor(StandardCursorType.Arrow);
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm is null) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) _vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) _vm.ZoomOutCommand.Execute(null);
        }
        else
        {
            _panX -= e.Delta.X * 28;
            _panY -= e.Delta.Y * 28;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_vm?.CurrentBitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var dest = GetImageDestRectDip();
        SKRectI? cropRect = _vm.IsCropping
            ? new SKRectI(_vm.CropX, _vm.CropY, _vm.CropX + _vm.CropWidth, _vm.CropY + _vm.CropHeight)
            : null;

        context.Custom(new ImageDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            _vm.CurrentBitmap,
            dest,
            cropRect));
    }

    private sealed class ImageDrawOperation(
        Rect bounds,
        SKBitmap bitmap,
        Rect destRectDip,
        SKRectI? cropRectPx) : ICustomDrawOperation
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

            var dest = new SKRect(
                (float)destRectDip.X,
                (float)destRectDip.Y,
                (float)(destRectDip.X + destRectDip.Width),
                (float)(destRectDip.Y + destRectDip.Height));

            // Drop shadow
            using var shadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 0x55),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f),
                IsAntialias = true
            };
            var shadow = dest;
            shadow.Offset(0f, 4f);
            canvas.DrawRect(shadow, shadowPaint);

            // Transparency checkerboard inside image rect
            canvas.Save();
            canvas.ClipRect(dest);
            using var lightCheck = new SKPaint { Color = new SKColor(0xF3, 0xF4, 0xF6) };
            using var darkCheck = new SKPaint { Color = new SKColor(0xD1, 0xD5, 0xDB) };
            canvas.DrawRect(dest, lightCheck);
            const float cell = 10f;
            for (float y = dest.Top; y < dest.Bottom; y += cell)
            {
                int row = (int)((y - dest.Top) / cell);
                for (float x = dest.Left; x < dest.Right; x += cell)
                {
                    int col = (int)((x - dest.Left) / cell);
                    if (((row + col) & 1) == 1)
                    {
                        canvas.DrawRect(x, y, Math.Min(cell, dest.Right - x), Math.Min(cell, dest.Bottom - y), darkCheck);
                    }
                }
            }

            // Image
            using var skImage = SKImage.FromBitmap(bitmap);
            using var imgPaint = new SKPaint { IsAntialias = true };
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            canvas.DrawImage(skImage, dest, sampling, imgPaint);

            // Crop overlay
            if (cropRectPx is { } crop && bitmap.Width > 0 && bitmap.Height > 0)
            {
                float sx = dest.Width / bitmap.Width;
                float sy = dest.Height / bitmap.Height;
                var cr = new SKRect(
                    dest.Left + crop.Left * sx,
                    dest.Top + crop.Top * sy,
                    dest.Left + crop.Right * sx,
                    dest.Top + crop.Bottom * sy);

                using var dimPaint = new SKPaint { Color = new SKColor(0, 0, 0, 0x88), Style = SKPaintStyle.Fill };
                canvas.DrawRect(new SKRect(dest.Left, dest.Top, dest.Right, cr.Top), dimPaint);
                canvas.DrawRect(new SKRect(dest.Left, cr.Bottom, dest.Right, dest.Bottom), dimPaint);
                canvas.DrawRect(new SKRect(dest.Left, cr.Top, cr.Left, cr.Bottom), dimPaint);
                canvas.DrawRect(new SKRect(cr.Right, cr.Top, dest.Right, cr.Bottom), dimPaint);

                using var cropStroke = new SKPaint
                {
                    Color = new SKColor(0x3B, 0x82, 0xF6),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2f,
                    IsAntialias = true
                };
                canvas.DrawRect(cr, cropStroke);
            }

            canvas.Restore();
        }
    }
}
