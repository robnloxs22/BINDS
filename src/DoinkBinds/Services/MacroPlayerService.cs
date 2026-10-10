using System.Diagnostics;
using DoinkBinds.Models;
using DoinkBinds.Platform;

namespace DoinkBinds.Services;

public sealed record PlaybackProgress(Guid MacroId, int Iteration, int TotalIterations, int StepIndex, int StepCount);

public sealed record PlaybackResult(Guid MacroId, bool Completed, bool Cancelled, string? Error, int IterationsCompleted);

/// <summary>Plays macros back with speed control, looping, pause/resume and window-focus handling.</summary>
public interface IMacroPlayerService
{
    AppStatus State { get; }
    Guid? CurrentMacroId { get; }

    event EventHandler<AppStatus>? StateChanged;
    event EventHandler<PlaybackProgress>? ProgressChanged;
    event EventHandler<PlaybackResult>? Finished;

    /// <summary>Starts playback on a background thread. Returns false if something is already playing.</summary>
    bool Play(Macro macro, PlaybackSettings settings);

    void TogglePause();
    void Stop();
}

public sealed class MacroPlayerService : IMacroPlayerService
{
    private const int MinStepGapMs = 0;
    private const int ClickHoldMs = 25;
    private const int ProgressIntervalMs = 33;

    private readonly IPlatformServices _platform;
    private readonly IMousePositionService _positions;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _running = new(true); // reset = paused
    private volatile AppStatus _state = AppStatus.Idle;

    public MacroPlayerService(IPlatformServices platform, IMousePositionService positions,
        ISettingsService settings, ILogService log)
    {
        _platform = platform;
        _positions = positions;
        _settings = settings;
        _log = log;
    }

    public AppStatus State => _state;
    public Guid? CurrentMacroId { get; private set; }

    public event EventHandler<AppStatus>? StateChanged;
    public event EventHandler<PlaybackProgress>? ProgressChanged;
    public event EventHandler<PlaybackResult>? Finished;

    public bool Play(Macro macro, PlaybackSettings settings)
    {
        lock (_gate)
        {
            if (_state != AppStatus.Idle) return false;

            _cts = new CancellationTokenSource();
            _running.Set();
            CurrentMacroId = macro.Id;
            SetState(AppStatus.Playing);

            // Snapshot so edits in the UI during playback can't change the running sequence.
            var actions = macro.Actions.Select(a => a.Clone()).ToList();
            var opts = settings.Clone();
            opts.Speed = Math.Clamp(opts.Speed, 0.25, 3.0);
            var token = _cts.Token;

            var thread = new Thread(() => Run(macro.Id, actions, opts, token))
            {
                IsBackground = true,
                Name = "DoinkBinds.Player",
                Priority = ThreadPriority.AboveNormal
            };
            thread.Start();
            return true;
        }
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            if (_state == AppStatus.Playing)
            {
                _running.Reset();
                SetState(AppStatus.Paused);
            }
            else if (_state == AppStatus.Paused)
            {
                _running.Set();
                SetState(AppStatus.Playing);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _running.Set(); // wake a paused player so it can exit
        }
    }

    private void SetState(AppStatus s)
    {
        _state = s;
        StateChanged?.Invoke(this, s);
    }

    // ------------------------------------------------------------------ playback thread

    private void Run(Guid macroId, List<MacroAction> actions, PlaybackSettings opts, CancellationToken ct)
    {
        var held = new HeldInput();
        var iterations = 0;
        string? error = null;
        var cancelled = false;

        using var precision = _platform.Input.BeginHighPrecisionTiming();
        try
        {
            var total = opts.Mode switch
            {
                PlaybackMode.Once => 1,
                PlaybackMode.LoopCount => Math.Max(1, opts.LoopCount),
                _ => 0 // infinite
            };

            var clock = Stopwatch.StartNew();
            long lastProgress = -ProgressIntervalMs;

            while (total == 0 || iterations < total)
            {
                ct.ThrowIfCancellationRequested();
                WindowTarget? lastCheckedWindow = null;

                // Each iteration is scheduled against an absolute timeline so small per-step
                // overheads never accumulate into drift.
                double scheduleMs = clock.Elapsed.TotalMilliseconds;

                for (var i = 0; i < actions.Count; i++)
                {
                    var action = actions[i];
                    scheduleMs += Math.Max(MinStepGapMs, action.DelayMs) / opts.Speed;
                    scheduleMs = WaitUntil(clock, scheduleMs, ct);

                    if (action.Window is { IsEmpty: false } w && (opts.RestoreWindowFocus || opts.WaitForWindow)
                        && !w.SameAs(lastCheckedWindow) && action.Type != MacroActionType.MouseMove)
                    {
                        lastCheckedWindow = w;
                        var before = clock.Elapsed.TotalMilliseconds;
                        EnsureWindow(w, opts, ct);
                        // Time spent waiting for a window shouldn't make following steps rush.
                        scheduleMs += clock.Elapsed.TotalMilliseconds - before;
                    }

                    Execute(action, held, ct);

                    var now = clock.ElapsedMilliseconds;
                    if (now - lastProgress >= ProgressIntervalMs || i == actions.Count - 1)
                    {
                        lastProgress = now;
                        ProgressChanged?.Invoke(this, new PlaybackProgress(macroId, iterations + 1, total, i, actions.Count));
                    }
                }

                // Release anything the macro left pressed before the next loop starts.
                held.ReleaseAll(_platform.Input);
                iterations++;

                if ((total == 0 || iterations < total) && opts.LoopDelayMs > 0)
                    WaitUntil(clock, clock.Elapsed.TotalMilliseconds + opts.LoopDelayMs / opts.Speed, ct);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex is PlaybackException ? ex.Message : $"Playback failed: {ex.Message}";
            _log.Error("Playback failed", ex);
        }
        finally
        {
            // Never leave keys or mouse buttons stuck down, whatever happened.
            try { held.ReleaseAll(_platform.Input); } catch (Exception ex) { _log.Error("Release failed", ex); }

            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
                CurrentMacroId = null;
                _running.Set();
                SetState(AppStatus.Idle);
            }

            Finished?.Invoke(this, new PlaybackResult(macroId, error is null && !cancelled, cancelled, error, iterations));
        }
    }

    /// <summary>
    /// High-precision wait until <paramref name="targetMs"/> on <paramref name="clock"/>.
    /// Sleeps coarsely, then spins for the last ~1.5 ms. Handles pause by shifting the schedule.
    /// Returns the (possibly shifted) target.
    /// </summary>
    private double WaitUntil(Stopwatch clock, double targetMs, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!_running.IsSet)
            {
                var pausedAt = clock.Elapsed.TotalMilliseconds;
                _running.Wait(ct);
                targetMs += clock.Elapsed.TotalMilliseconds - pausedAt;
                continue;
            }

            var remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return targetMs;

            if (remaining > 20)
                ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(remaining - 10, 50))); // wakes instantly on Stop
            else if (remaining > 1.5)
                Thread.Sleep(1);
            else
                Thread.SpinWait(40);
        }
    }

    private void EnsureWindow(WindowTarget target, PlaybackSettings opts, CancellationToken ct)
    {
        bool IsTargetFocused()
        {
            var fg = _platform.Windows.GetForegroundWindow();
            return fg is not null && string.Equals(fg.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase);
        }

        if (IsTargetFocused()) return;

        if (opts.RestoreWindowFocus)
        {
            _platform.Windows.TryActivate(target);
            ct.WaitHandle.WaitOne(150); // give the window manager a moment
        }

        if (!opts.WaitForWindow) return;

        var timeout = _settings.Settings.WaitForWindowTimeoutMs;
        var sw = Stopwatch.StartNew();
        while (!IsTargetFocused())
        {
            ct.ThrowIfCancellationRequested();
            _running.Wait(ct);
            if (timeout > 0 && sw.ElapsedMilliseconds > timeout)
                throw new PlaybackException($"Timed out after {timeout / 1000.0:0.#}s waiting for \"{target}\" to be focused.");
            if (opts.RestoreWindowFocus && sw.ElapsedMilliseconds % 2000 < 100)
                _platform.Windows.TryActivate(target);
            ct.WaitHandle.WaitOne(100);
        }
    }

    private void Execute(MacroAction a, HeldInput held, CancellationToken ct)
    {
        var input = _platform.Input;

        void MoveTo(MousePosition? pos)
        {
            if (pos is null) return;
            var target = _positions.Resolve(pos, input.GetCursorPosition());
            input.MoveMouse(target.X, target.Y);
        }

        switch (a.Type)
        {
            case MacroActionType.MouseMove:
                MoveTo(a.Position);
                break;

            case MacroActionType.MouseDown:
                MoveTo(a.Position);
                input.MouseButton(a.Button, true);
                held.Buttons.Add(a.Button);
                break;

            case MacroActionType.MouseUp:
                MoveTo(a.Position);
                input.MouseButton(a.Button, false);
                held.Buttons.Remove(a.Button);
                break;

            case MacroActionType.MouseClick:
                MoveTo(a.Position);
                input.MouseButton(a.Button, true);
                ct.WaitHandle.WaitOne(ClickHoldMs);
                input.MouseButton(a.Button, false);
                break;

            case MacroActionType.MouseWheel:
                MoveTo(a.Position);
                input.MouseWheel(a.WheelDelta, a.Horizontal);
                break;

            case MacroActionType.KeyDown:
                input.Key(a.KeyCode, true);
                held.Keys.Add(a.KeyCode);
                break;

            case MacroActionType.KeyUp:
                input.Key(a.KeyCode, false);
                held.Keys.Remove(a.KeyCode);
                break;

            case MacroActionType.KeyPress:
                input.Key(a.KeyCode, true);
                ct.WaitHandle.WaitOne(ClickHoldMs);
                input.Key(a.KeyCode, false);
                break;

            case MacroActionType.Wait:
                break; // the delay already happened
        }
    }

    /// <summary>Tracks what the macro currently holds down so it can always be released.</summary>
    private sealed class HeldInput
    {
        public readonly HashSet<int> Keys = new();
        public readonly HashSet<MacroMouseButton> Buttons = new();

        public void ReleaseAll(IInputSimulator input)
        {
            foreach (var k in Keys.ToList()) input.Key(k, false);
            foreach (var b in Buttons.ToList()) input.MouseButton(b, false);
            Keys.Clear();
            Buttons.Clear();
        }
    }
}

public sealed class PlaybackException(string message) : Exception(message);
