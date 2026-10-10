using System.Collections.Concurrent;
using System.Diagnostics;

namespace DoinkBinds.Platform;

/// <summary>
/// Shared plumbing for OS hooks. OS hook callbacks must return extremely quickly
/// (Windows silently removes low-level hooks that exceed LowLevelHooksTimeout, and macOS
/// disables slow event taps), so callbacks only enqueue events and a separate dispatcher
/// thread raises <see cref="InputReceived"/>.
/// </summary>
public abstract class InputHookBase : IInputHook
{
    private BlockingCollection<RawInputEvent>? _queue;
    private Thread? _dispatchThread;

    public event Action<RawInputEvent>? InputReceived;

    public bool IsRunning { get; private set; }

    public bool Start(out string? error)
    {
        error = null;
        if (IsRunning) return true;

        _queue = new BlockingCollection<RawInputEvent>(new ConcurrentQueue<RawInputEvent>());
        _dispatchThread = new Thread(DispatchLoop) { IsBackground = true, Name = "DoinkBinds.InputDispatch" };
        _dispatchThread.Start();

        if (!StartHook(out error))
        {
            _queue.CompleteAdding();
            return false;
        }

        IsRunning = true;
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { StopHook(); } catch (Exception ex) { Debug.WriteLine(ex); }
        _queue?.CompleteAdding();
    }

    /// <summary>Called from the OS hook thread. Must be cheap.</summary>
    protected void Enqueue(in RawInputEvent e)
    {
        var q = _queue;
        if (q is { IsAddingCompleted: false })
        {
            try { q.Add(e); } catch (InvalidOperationException) { /* shutting down */ }
        }
    }

    private void DispatchLoop()
    {
        var q = _queue!;
        foreach (var e in q.GetConsumingEnumerable())
        {
            try
            {
                InputReceived?.Invoke(e);
            }
            catch (Exception ex)
            {
                // Never let a subscriber kill the dispatcher thread.
                Debug.WriteLine($"Input subscriber failed: {ex}");
            }
        }
    }

    protected abstract bool StartHook(out string? error);
    protected abstract void StopHook();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
