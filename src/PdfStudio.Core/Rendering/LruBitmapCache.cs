using SkiaSharp;

namespace PdfStudio.Core.Rendering;

public readonly record struct BitmapCacheKey(int PageIndex, int WidthPx, int HeightPx, int RotationQuarterTurns);

/// <summary>
/// Thread-safe, byte-budgeted LRU cache for rendered SKBitmaps with deterministic disposal on eviction.
/// </summary>
public sealed class LruBitmapCache : IDisposable
{
    private readonly long _maxBytes;
    private readonly Lock _gate = new();
    private readonly Dictionary<BitmapCacheKey, LinkedListNode<CacheEntry>> _map = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _currentBytes;
    private bool _disposed;

    private sealed record CacheEntry(BitmapCacheKey Key, SKBitmap Bitmap, long ByteSize);

    public LruBitmapCache(long maxBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    public long CurrentBytes
    {
        get { lock (_gate) return _currentBytes; }
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public bool TryGet(BitmapCacheKey key, out SKBitmap? bitmap)
    {
        lock (_gate)
        {
            if (_disposed || !_map.TryGetValue(key, out var node))
            {
                bitmap = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }
    }

    /// <summary>
    /// Safely executes a synchronous draw operation while holding the cache lock
    /// so the underlying SKBitmap cannot be evicted and disposed mid-render.
    /// </summary>
    public bool TryUse(BitmapCacheKey key, Action<SKBitmap> drawAction)
    {
        ArgumentNullException.ThrowIfNull(drawAction);
        lock (_gate)
        {
            if (_disposed || !_map.TryGetValue(key, out var node))
            {
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            drawAction(node.Value.Bitmap);
            return true;
        }
    }

    /// <summary>
    /// Fallback lookup returning any cached resolution for the given page and rotation
    /// so inertial zoom renders smoothly without blank flashes.
    /// </summary>
    public bool TryUseAnyResolutionForPage(int pageIndex, int rotationQuarterTurns, Action<SKBitmap> drawAction)
    {
        ArgumentNullException.ThrowIfNull(drawAction);
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            for (var node = _lru.First; node is not null; node = node.Next)
            {
                var k = node.Value.Key;
                if (k.PageIndex == pageIndex && k.RotationQuarterTurns == rotationQuarterTurns)
                {
                    drawAction(node.Value.Bitmap);
                    return true;
                }
            }

            return false;
        }
    }

    public void Put(BitmapCacheKey key, SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        long bytes = (long)bitmap.ByteCount;
        List<SKBitmap>? evicted = null;

        lock (_gate)
        {
            if (_disposed)
            {
                bitmap.Dispose();
                return;
            }

            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.ByteSize;
                if (!ReferenceEquals(existing.Value.Bitmap, bitmap))
                {
                    (evicted ??= []).Add(existing.Value.Bitmap);
                }
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, bitmap, bytes));
            _lru.AddFirst(node);
            _map[key] = node;
            _currentBytes += bytes;

            while (_currentBytes > _maxBytes && _lru.Count > 1)
            {
                var tail = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
                _currentBytes -= tail.Value.ByteSize;
                (evicted ??= []).Add(tail.Value.Bitmap);
            }
        }

        if (evicted is not null)
        {
            foreach (var old in evicted)
            {
                old.Dispose();
            }
        }
    }

    public void Clear()
    {
        List<SKBitmap> toDispose;
        lock (_gate)
        {
            toDispose = _lru.Select(e => e.Bitmap).ToList();
            _lru.Clear();
            _map.Clear();
            _currentBytes = 0;
        }

        foreach (var bmp in toDispose)
        {
            bmp.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Clear();
    }
}

