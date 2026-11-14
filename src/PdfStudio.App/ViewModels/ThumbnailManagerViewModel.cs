using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Rendering;
using SkiaSharp;

namespace PdfStudio.App.ViewModels;

/// <summary>
/// Manages virtualized, viewport-prioritized page thumbnails and bounded LRU bitmap caching.
/// </summary>
public sealed class ThumbnailManagerViewModel : ObservableObject
{
    public const int DefaultVisibleThumbnailCount = 6;
    public const int ThumbnailOverscanCount = 6;

    private readonly Func<bool> _isRenderEngineOpen;
    private readonly Func<RenderRequest, Task<SKBitmap>> _renderPageAsync;
    private CancellationTokenSource? _thumbnailCts;
    private readonly Lock _thumbnailGate = new();
    private readonly Dictionary<int, CancellationTokenSource> _inFlightThumbnails = new();
    private readonly LinkedList<int> _thumbnailLruOrder = new();
    private readonly Dictionary<int, LinkedListNode<int>> _thumbnailLruNodes = new();
    private int _thumbnailVisibleStart;
    private int _thumbnailVisibleEnd = DefaultVisibleThumbnailCount - 1;
    private int _thumbnailWindowStart;
    private int _thumbnailWindowEnd = DefaultVisibleThumbnailCount + ThumbnailOverscanCount - 1;

    public int MaxCachedThumbnails { get; set; } = 36;

    public int CachedThumbnailCount
    {
        get
        {
            lock (_thumbnailGate)
            {
                return _thumbnailLruOrder.Count;
            }
        }
    }

    public ObservableCollection<PageThumbnailViewModel> PageThumbnails { get; } = [];

    public event Action? RequestUpdateThumbnailViewport;

    public ThumbnailManagerViewModel(
        Func<bool> isRenderEngineOpen,
        Func<RenderRequest, Task<SKBitmap>> renderPageAsync)
    {
        _isRenderEngineOpen = isRenderEngineOpen ?? throw new ArgumentNullException(nameof(isRenderEngineOpen));
        _renderPageAsync = renderPageAsync ?? throw new ArgumentNullException(nameof(renderPageAsync));
    }

    public void Rebuild(IReadOnlyList<PdfPageInfo> pages, int currentPage)
    {
        Cancel();
        DisposeAll();
        for (int i = 0; i < pages.Count; i++)
        {
            PageThumbnails.Add(new PageThumbnailViewModel(i, pages[i]));
        }
        UpdateSelection(currentPage);
        StartLazyLoading(currentPage);
    }

    public void UpdateSelection(int currentPage)
    {
        int selectedIdx = currentPage - 1;
        for (int i = 0; i < PageThumbnails.Count; i++)
        {
            PageThumbnails[i].IsSelected = i == selectedIdx;
        }
    }

    public void StartLazyLoading(int currentPage)
    {
        if (!_isRenderEngineOpen() || PageThumbnails.Count == 0) return;

        int firstVisible = Math.Clamp(currentPage - 1, 0, PageThumbnails.Count - 1);
        int lastVisible = Math.Min(PageThumbnails.Count - 1, firstVisible + DefaultVisibleThumbnailCount - 1);
        UpdateViewport(firstVisible, lastVisible);
        RequestUpdateThumbnailViewport?.Invoke();
    }

    public void UpdateViewport(int firstVisibleIndex, int lastVisibleIndex)
    {
        if (!_isRenderEngineOpen() || PageThumbnails.Count == 0) return;

        int count = PageThumbnails.Count;
        int clampedFirst = Math.Clamp(Math.Min(firstVisibleIndex, lastVisibleIndex), 0, count - 1);
        int clampedLast = Math.Clamp(Math.Max(firstVisibleIndex, lastVisibleIndex), 0, count - 1);
        int windowStart = Math.Max(0, clampedFirst - ThumbnailOverscanCount);
        int windowEnd = Math.Min(count - 1, clampedLast + ThumbnailOverscanCount);

        var prioritized = BuildPrioritizedThumbnailIndices(clampedFirst, clampedLast, windowStart, windowEnd);
        int maxActive = Math.Max((clampedLast - clampedFirst) + 1, Math.Max(1, MaxCachedThumbnails));
        if (prioritized.Count > maxActive)
        {
            prioritized.RemoveRange(maxActive, prioritized.Count - maxActive);
        }

        var activeSet = prioritized.ToHashSet();

        List<CancellationTokenSource> toCancel = [];
        List<int>? evictions;
        var toStart = new List<(PageThumbnailViewModel Item, CancellationTokenSource ItemCts, CancellationToken BatchToken)>();

        lock (_thumbnailGate)
        {
            _thumbnailVisibleStart = clampedFirst;
            _thumbnailVisibleEnd = clampedLast;
            _thumbnailWindowStart = windowStart;
            _thumbnailWindowEnd = windowEnd;

            _thumbnailCts ??= new CancellationTokenSource();
            var batchToken = _thumbnailCts.Token;

            foreach (var kvp in _inFlightThumbnails.Where(k => !activeSet.Contains(k.Key)).ToList())
            {
                toCancel.Add(kvp.Value);
                _inFlightThumbnails.Remove(kvp.Key);
            }

            // Refresh MRU order for already-cached items inside the active window (visible pages freshest).
            for (int i = prioritized.Count - 1; i >= 0; i--)
            {
                int idx = prioritized[i];
                if (_thumbnailLruNodes.TryGetValue(idx, out var node))
                {
                    _thumbnailLruOrder.Remove(node);
                    _thumbnailLruOrder.AddFirst(node);
                }
            }

            evictions = CollectEvictionsLocked(windowStart, windowEnd);

            foreach (int idx in prioritized)
            {
                if (idx < 0 || idx >= PageThumbnails.Count) continue;
                var item = PageThumbnails[idx];
                if (item.PageInfo.IsBlankPage || item.ThumbnailBitmap is not null || _inFlightThumbnails.ContainsKey(idx))
                {
                    continue;
                }

                var itemCts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
                _inFlightThumbnails[idx] = itemCts;
                toStart.Add((item, itemCts, batchToken));
            }
        }

        foreach (var cts in toCancel)
        {
            CancelSafely(cts);
        }

        if (evictions is { Count: > 0 })
        {
            DisposeEvictedThumbnailsOnUiThread(evictions);
        }

        foreach (var (item, itemCts, batchToken) in toStart)
        {
            _ = LoadThumbnailsAsync(item, itemCts, batchToken);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? batchCts;
        List<CancellationTokenSource> inFlight;
        lock (_thumbnailGate)
        {
            batchCts = _thumbnailCts;
            _thumbnailCts = null;
            inFlight = _inFlightThumbnails.Values.ToList();
            _inFlightThumbnails.Clear();
        }

        if (batchCts is not null)
        {
            CancelSafely(batchCts);
            batchCts.Dispose();
        }

        foreach (var itemCts in inFlight)
        {
            CancelSafely(itemCts);
        }
    }

    public void DisposeAll()
    {
        lock (_thumbnailGate)
        {
            _thumbnailLruOrder.Clear();
            _thumbnailLruNodes.Clear();
        }

        foreach (var t in PageThumbnails)
        {
            t.ThumbnailBitmap?.Dispose();
            t.ThumbnailBitmap = null;
        }
        PageThumbnails.Clear();
    }

    private static List<int> BuildPrioritizedThumbnailIndices(
        int visibleStart,
        int visibleEnd,
        int windowStart,
        int windowEnd)
    {
        var order = new List<int>((windowEnd - windowStart) + 1);
        for (int i = visibleStart; i <= visibleEnd; i++)
        {
            order.Add(i);
        }

        int maxOffset = Math.Max(visibleStart - windowStart, windowEnd - visibleEnd);
        for (int offset = 1; offset <= maxOffset; offset++)
        {
            int below = visibleEnd + offset;
            if (below <= windowEnd) order.Add(below);

            int above = visibleStart - offset;
            if (above >= windowStart) order.Add(above);
        }

        return order;
    }

    private async Task LoadThumbnailsAsync(PageThumbnailViewModel item, CancellationTokenSource itemCts, CancellationToken batchToken)
    {
        const int targetWidthPx = 280;
        var ct = itemCts.Token;
        SKBitmap? skBmp = null;

        try
        {
            if (ct.IsCancellationRequested || batchToken.IsCancellationRequested) return;

            var page = item.PageInfo;
            if (page.IsBlankPage) return;

            double aspect = page.WidthPoints > 0 ? page.HeightPoints / page.WidthPoints : 1.3;
            int targetHeightPx = Math.Max(1, (int)Math.Round(targetWidthPx * aspect));
            int rot = ((page.RotationDegrees / 90) % 4 + 4) % 4;

            skBmp = await _renderPageAsync(new RenderRequest(
                page.EffectiveSourcePageIndex,
                targetWidthPx,
                targetHeightPx,
                rot,
                RenderPriority.Thumbnail,
                ct));

            if (ct.IsCancellationRequested || batchToken.IsCancellationRequested) return;

            var avaloniaBmp = ConvertToAvaloniaBitmap(skBmp);
            if (Dispatcher.UIThread.CheckAccess())
            {
                CommitThumbnailBitmap(item, avaloniaBmp, ct, batchToken);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    CommitThumbnailBitmap(item, avaloniaBmp, ct, batchToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on viewport scroll, tab switch, or close
        }
        catch
        {
            // Ignore transient thumbnail errors during tab switch or close
        }
        finally
        {
            skBmp?.Dispose();
            lock (_thumbnailGate)
            {
                if (_inFlightThumbnails.TryGetValue(item.DisplayIndex, out var current) &&
                    ReferenceEquals(current, itemCts))
                {
                    _inFlightThumbnails.Remove(item.DisplayIndex);
                }
            }
            itemCts.Dispose();
        }
    }

    private void CommitThumbnailBitmap(
        PageThumbnailViewModel item,
        WriteableBitmap avaloniaBmp,
        CancellationToken ct,
        CancellationToken batchToken)
    {
        int idx = item.DisplayIndex;
        bool accept;
        List<int>? evictIndices = null;

        lock (_thumbnailGate)
        {
            accept = !ct.IsCancellationRequested &&
                     !batchToken.IsCancellationRequested &&
                     idx >= _thumbnailWindowStart &&
                     idx <= _thumbnailWindowEnd;
            if (accept)
            {
                if (_thumbnailLruNodes.TryGetValue(idx, out var existingNode))
                {
                    _thumbnailLruOrder.Remove(existingNode);
                    _thumbnailLruNodes.Remove(idx);
                }

                var node = new LinkedListNode<int>(idx);
                _thumbnailLruOrder.AddFirst(node);
                _thumbnailLruNodes[idx] = node;
                if (idx < _thumbnailVisibleStart || idx > _thumbnailVisibleEnd)
                {
                    for (int v = _thumbnailVisibleEnd; v >= _thumbnailVisibleStart; v--)
                    {
                        if (_thumbnailLruNodes.TryGetValue(v, out var visibleNode))
                        {
                            _thumbnailLruOrder.Remove(visibleNode);
                            _thumbnailLruOrder.AddFirst(visibleNode);
                        }
                    }
                }
                evictIndices = CollectEvictionsLocked(_thumbnailWindowStart, _thumbnailWindowEnd);
            }
        }

        if (!accept || idx < 0 || idx >= PageThumbnails.Count || !ReferenceEquals(PageThumbnails[idx], item))
        {
            if (accept)
            {
                lock (_thumbnailGate)
                {
                    if (_thumbnailLruNodes.TryGetValue(idx, out var node))
                    {
                        _thumbnailLruOrder.Remove(node);
                        _thumbnailLruNodes.Remove(idx);
                    }
                }
            }

            avaloniaBmp.Dispose();
            return;
        }

        var previous = item.ThumbnailBitmap;
        item.ThumbnailBitmap = avaloniaBmp;
        if (previous is not null && !ReferenceEquals(previous, avaloniaBmp))
        {
            previous.Dispose();
        }

        if (evictIndices is { Count: > 0 })
        {
            DisposeEvictedThumbnailsOnUiThread(evictIndices);
        }
    }

    private List<int>? CollectEvictionsLocked(int windowStart, int windowEnd)
    {
        int limit = Math.Max(1, MaxCachedThumbnails);
        if (_thumbnailLruOrder.Count <= limit) return null;

        List<int>? evicted = null;
        void EvictMatching(Func<int, bool> predicate)
        {
            var node = _thumbnailLruOrder.Last;
            while (_thumbnailLruOrder.Count > limit && node is not null)
            {
                var prev = node.Previous;
                if (predicate(node.Value))
                {
                    _thumbnailLruOrder.Remove(node);
                    _thumbnailLruNodes.Remove(node.Value);
                    evicted ??= [];
                    evicted.Add(node.Value);
                }
                node = prev;
            }
        }

        // Evict off-window entries first, then non-visible overscan items, preserving active visible pages.
        EvictMatching(i => i < windowStart || i > windowEnd);
        EvictMatching(i => i < _thumbnailVisibleStart || i > _thumbnailVisibleEnd);
        EvictMatching(_ => true);

        return evicted;
    }

    private void DisposeEvictedThumbnailsOnUiThread(List<int> evictIndices)
    {
        void ClearBitmaps()
        {
            foreach (int evictIdx in evictIndices)
            {
                bool stillCached;
                lock (_thumbnailGate)
                {
                    stillCached = _thumbnailLruNodes.ContainsKey(evictIdx);
                }

                if (stillCached || evictIdx < 0 || evictIdx >= PageThumbnails.Count) continue;

                var thumb = PageThumbnails[evictIdx];
                var bmp = thumb.ThumbnailBitmap;
                if (bmp is not null)
                {
                    thumb.ThumbnailBitmap = null;
                    bmp.Dispose();
                }
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ClearBitmaps();
        }
        else
        {
            Dispatcher.UIThread.Post(ClearBitmaps, DispatcherPriority.Background);
        }
    }

    private static void CancelSafely(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by completing task.
        }
    }

    private static WriteableBitmap ConvertToAvaloniaBitmap(SKBitmap skBitmap)
    {
        var wb = new WriteableBitmap(
            new PixelSize(skBitmap.Width, skBitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var fb = wb.Lock();
        int rowBytes = Math.Min(fb.RowBytes, skBitmap.RowBytes);
        unsafe
        {
            byte* src = (byte*)skBitmap.GetPixels();
            byte* dst = (byte*)fb.Address;
            for (int y = 0; y < skBitmap.Height; y++)
            {
                Buffer.MemoryCopy(
                    src + (long)y * skBitmap.RowBytes,
                    dst + (long)y * fb.RowBytes,
                    fb.RowBytes,
                    rowBytes);
            }
        }

        return wb;
    }
}
