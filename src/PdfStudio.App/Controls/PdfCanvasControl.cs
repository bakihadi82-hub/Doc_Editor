using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
public sealed partial class PdfCanvasControl : Control
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

    private readonly record struct ScrollBarMetrics(
        bool IsVisible,
        Rect TrackRect,
        Rect ThumbRect,
        Rect HitRect);

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
            or nameof(MainWindowViewModel.SelectedAnnotation)
            or nameof(MainWindowViewModel.IsPdfDarkInvertEnabled))
        {
            InvalidateAndSchedule();
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.ActiveTool) or nameof(MainWindowViewModel.IsHandTool))
        {
            UpdateCursorForTool();
        }
    }

    private void InvalidateAndSchedule()
    {
        ClampScrollOffsets();
        InvalidateVisual();
        ScheduleVisiblePageRenders();
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
}
