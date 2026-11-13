using System.IO.Pipes;
using System.Text;

namespace PdfStudio.App.Services;

/// <summary>
/// Coordinates single-instance Mutex ownership and forwards incoming File Explorer arguments
/// over a local Named Pipe so new PDFs open as tabs in the existing window.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    public const string DefaultMutexName = @"Local\PdfStudio_SingleInstance_Mutex_v1";
    public const string DefaultPipeName = "PdfStudio_SingleInstance_Pipe_v1";
    public const string ActivateCommand = "__ACTIVATE__";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly Action<string> _onMessageReceived;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenerTask;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, Action<string> onMessageReceived)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _onMessageReceived = onMessageReceived;
        _listenerTask = Task.Run(ListenLoopAsync);
    }

    public static bool TryAcquirePrimaryInstance(
        Action<string> onMessageReceived,
        out SingleInstanceCoordinator? coordinator,
        string mutexName = DefaultMutexName,
        string pipeName = DefaultPipeName)
    {
        ArgumentNullException.ThrowIfNull(onMessageReceived);

        var mutex = new Mutex(initiallyOwned: true, name: mutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            coordinator = null;
            return false;
        }

        coordinator = new SingleInstanceCoordinator(mutex, pipeName, onMessageReceived);
        return true;
    }

    public static async Task<bool> SendArgumentsToPrimaryInstanceAsync(
        string[] args,
        string pipeName = DefaultPipeName,
        int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs).ConfigureAwait(false);

            string payload = args is { Length: > 0 } && !string.IsNullOrWhiteSpace(args[0])
                ? Path.GetFullPath(args[0])
                : ActivateCommand;

            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(payload).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _onMessageReceived(line.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        try
        {
            // Nudge any blocked WaitForConnectionAsync cleanly on shutdown
            using var wakeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            wakeClient.Connect(50);
        }
        catch
        {
            // Ignore wake failure if already torn down
        }

        try { _listenerTask.Wait(200); } catch { /* ignore */ }
        _cts.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* ignore if not owned */ }
        _mutex.Dispose();
    }
}

