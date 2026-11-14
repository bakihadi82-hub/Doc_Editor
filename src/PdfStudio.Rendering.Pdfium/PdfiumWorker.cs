using PdfStudio.Core.Rendering;

namespace PdfStudio.Rendering.Pdfium;

/// <summary>
/// Dedicated single-threaded work scheduler because native PDFium is not thread-safe.
/// Supports priority ordering (VisibleViewport > AdjacentPreload > Thumbnail).
/// </summary>
internal sealed class PdfiumWorker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly PriorityQueue<WorkItem, int> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private readonly Action? _onWorkerExit;
    private bool _disposed;

    private sealed record WorkItem(Action Run, Action Cancel, CancellationToken CancellationToken);

    public PdfiumWorker(Action? onWorkerExit = null)
    {
        _onWorkerExit = onWorkerExit;
        _thread = new Thread(RunLoop)
        {
            Name = "Pdfium-Render-Worker",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public Task<T> EnqueueAsync<T>(
        Func<T> action,
        RenderPriority priority = RenderPriority.VisibleViewport,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_disposed)
            {
                tcs.SetException(new ObjectDisposedException(nameof(PdfiumWorker)));
                return tcs.Task;
            }

            _queue.Enqueue(new WorkItem(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    tcs.TrySetResult(action());
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, () =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                }
                else
                {
                    tcs.TrySetCanceled();
                }
            }, ct), (int)priority);
            _signal.Release();
        }

        return tcs.Task;
    }

    private void RunLoop()
    {
        try
        {
            while (true)
            {
                _signal.Wait();

                WorkItem? item;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _queue.TryDequeue(out item, out _);
                }

                item?.Run();
            }
        }
        finally
        {
            try
            {
                _onWorkerExit?.Invoke();
            }
            catch
            {
                // Best-effort native cleanup on worker thread exit.
            }
        }
    }

    public void Dispose()
    {
        List<WorkItem> remaining = [];
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_queue.TryDequeue(out var item, out _))
            {
                remaining.Add(item);
            }
        }

        foreach (var item in remaining)
        {
            item.Cancel();
        }

        _signal.Release();
        _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }
}

