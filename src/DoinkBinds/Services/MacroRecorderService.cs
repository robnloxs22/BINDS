using System.Diagnostics;
using DoinkBinds.Models;
using DoinkBinds.Platform;

namespace DoinkBinds.Services;

/// <summary>Records mouse, keyboard, timings and foreground-window info into a list of <see cref="MacroAction"/>.</summary>
public interface IMacroRecorderService
{
    bool IsRecording { get; }
    int ActionCount { get; }
    TimeSpan Elapsed { get; }

    /// <summary>Raised on a background thread for every captured action (for the live view).</summary>
    event EventHandler<MacroAction>? ActionRecorded;

    void Start();

    /// <summary>Stops and returns the cleaned-up list of actions.</summary>
    List<MacroAction> Stop();
}

public sealed class MacroRecorderService : IMacroRecorderService, IDisposable
{
    private readonly IPlatformServices _platform;
    private readonly IHotkeyService _hotkeys;
    private readonly IMousePositionService _positions;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    private readonly List<MacroAction> _actions = new();
    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<MacroMouseButton> _buttonsDown = new();
    private readonly Stopwatch _clock = new();

    private AppSettings _cfg = new();
    private long _lastEmitTs;
    private long _lastMoveTs;
    private DesktopPoint _lastPos;
    private (DesktopPoint Point, long Ts)? _pendingMove;
    private WindowTarget? _lastWindow;
    private volatile bool _recording;

    public event EventHandler<MacroAction>? ActionRecorded;

    public MacroRecorderService(IPlatformServices platform, IHotkeyService hotkeys,
        IMousePositionService positions, ISettingsService settings)
    {
        _platform = platform;
        _hotkeys = hotkeys;
        _positions = positions;
        _settings = settings;
        _platform.Hook.InputReceived += OnInput;
    }

    public bool IsRecording => _recording;
    public int ActionCount { get { lock (_gate) return _actions.Count; } }
    public TimeSpan Elapsed => _clock.Elapsed;

    public void Start()
    {
        lock (_gate)
        {
            _actions.Clear();
            _keysDown.Clear();
            _buttonsDown.Clear();
            _pendingMove = null;
            _cfg = _settings.Settings;
            _lastWindow = _cfg.RecordWindowInfo ? _platform.Windows.GetForegroundWindow() : null;
            _lastPos = _positions.GetCursorPosition();
            _lastEmitTs = _lastMoveTs = Stopwatch.GetTimestamp();

            // Start by putting the cursor where it was when recording began, so playback
            // begins from the same place (meaningless in Relative mode, so skipped there).
            if (_cfg.RecordMouseMovement && _cfg.DefaultMouseMode != MousePositionMode.Relative)
            {
                Emit(new MacroAction
                {
                    Type = MacroActionType.MouseMove,
                    Position = _positions.FromAbsolute(_lastPos, _cfg.DefaultMouseMode, _lastPos),
                    Window = _lastWindow?.Clone()
                }, _lastEmitTs);
            }

            _clock.Restart();
            _recording = true;
        }
    }

    public List<MacroAction> Stop()
    {
        lock (_gate)
        {
            _recording = false;
            _clock.Stop();
            FlushPendingMove();

            // Keys still held now were pressed as part of the stop hotkey chord (e.g. Ctrl of Ctrl+F6)
            // or are otherwise unbalanced. Drop their trailing KeyDowns so playback never leaves a key stuck.
            foreach (var key in _keysDown)
            {
                var lastUp = _actions.FindLastIndex(a => a.Type == MacroActionType.KeyUp && a.KeyCode == key);
                for (var i = _actions.Count - 1; i > lastUp; i--)
                {
                    if (_actions[i].Type == MacroActionType.KeyDown && _actions[i].KeyCode == key)
                        _actions.RemoveAt(i);
                }
            }

            // Same for mouse buttons that were never released.
            foreach (var button in _buttonsDown)
            {
                _actions.Add(new MacroAction { Type = MacroActionType.MouseUp, Button = button, DelayMs = 10 });
            }

            var result = _actions.ToList();
            _actions.Clear();
            return result;
        }
    }

    private void OnInput(RawInputEvent e)
    {
        if (!_recording || e.IsSynthetic) return;

        lock (_gate)
        {
            if (!_recording) return;
            try
            {
                Handle(e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Recorder error: {ex}");
            }
        }
    }

    private void Handle(RawInputEvent e)
    {
        switch (e.Kind)
        {
            case RawInputKind.MouseMove:
                HandleMove(e);
                return;

            case RawInputKind.KeyDown:
            case RawInputKind.KeyUp:
                HandleKey(e);
                return;

            case RawInputKind.MouseDown:
            case RawInputKind.MouseUp:
                HandleButton(e);
                return;

            case RawInputKind.MouseWheel:
                if (IgnoreBecauseOwnWindow()) return;
                FlushPendingMove();
                var p = new DesktopPoint(e.X, e.Y);
                Emit(new MacroAction
                {
                    Type = MacroActionType.MouseWheel,
                    WheelDelta = e.WheelDelta,
                    Horizontal = e.Horizontal,
                    Position = MakePosition(p),
                    Window = CurrentWindow()
                }, e.Timestamp);
                return;
        }
    }

    private void HandleMove(RawInputEvent e)
    {
        if (!_cfg.RecordMouseMovement) return;
        var p = new DesktopPoint(e.X, e.Y);

        var sampleTicks = _cfg.MouseMoveSampleMs * Stopwatch.Frequency / 1000;
        var dist = Math.Max(Math.Abs(p.X - _lastPos.X), Math.Abs(p.Y - _lastPos.Y));

        if (e.Timestamp - _lastMoveTs < sampleTicks || dist < _cfg.MouseMoveMinDistance)
        {
            // Too soon / too small: remember it so the final resting point is never lost.
            _pendingMove = (p, e.Timestamp);
            return;
        }

        if (IgnoreBecauseOwnWindow())
        {
            _pendingMove = null;
            return;
        }

        _pendingMove = null;
        EmitMove(p, e.Timestamp);
    }

    private void EmitMove(DesktopPoint p, long ts)
    {
        _lastMoveTs = ts;
        Emit(new MacroAction
        {
            Type = MacroActionType.MouseMove,
            Position = MakePosition(p),
            Window = _lastWindow?.Clone()
        }, ts);
    }

    private void FlushPendingMove()
    {
        if (_pendingMove is { } pm)
        {
            _pendingMove = null;
            if (pm.Point != _lastPos) EmitMove(pm.Point, pm.Ts);
        }
    }

    private void HandleButton(RawInputEvent e)
    {
        var down = e.Kind == RawInputKind.MouseDown;
        if (down)
        {
            if (IgnoreBecauseOwnWindow()) return;
            _buttonsDown.Add(e.Button);
        }
        else if (!_buttonsDown.Remove(e.Button))
        {
            // Release of a press we never recorded (e.g. the click on the Record button) — skip.
            return;
        }

        FlushPendingMove();
        Emit(new MacroAction
        {
            Type = down ? MacroActionType.MouseDown : MacroActionType.MouseUp,
            Button = e.Button,
            Position = MakePosition(new DesktopPoint(e.X, e.Y)),
            Window = CurrentWindow()
        }, e.Timestamp);
    }

    private void HandleKey(RawInputEvent e)
    {
        // Keys that trigger Doink Binds commands (F6, F7 ...) are not part of the macro.
        if (_hotkeys.IsHotkeyKey(e.KeyCode)) return;

        var down = e.Kind == RawInputKind.KeyDown;
        if (down)
        {
            if (!_keysDown.Contains(e.KeyCode) && IgnoreBecauseOwnWindow()) return;
            _keysDown.Add(e.KeyCode); // repeats are kept: they reproduce "holding" a key faithfully
        }
        else if (!_keysDown.Remove(e.KeyCode))
        {
            return; // release of a key pressed before recording started
        }

        FlushPendingMove();
        Emit(new MacroAction
        {
            Type = down ? MacroActionType.KeyDown : MacroActionType.KeyUp,
            KeyCode = e.KeyCode,
            Window = CurrentWindow()
        }, e.Timestamp);
    }

    private MousePosition MakePosition(DesktopPoint p)
    {
        var pos = _positions.FromAbsolute(p, _cfg.DefaultMouseMode, _lastPos);
        _lastPos = p;
        return pos;
    }

    private WindowTarget? CurrentWindow()
    {
        if (!_cfg.RecordWindowInfo) return null;
        var w = _platform.Windows.GetForegroundWindow();
        if (w is not null) _lastWindow = w;
        return _lastWindow?.Clone();
    }

    private bool IgnoreBecauseOwnWindow() =>
        _cfg.IgnoreOwnWindow && _platform.Windows.IsOwnProcessForeground();

    private void Emit(MacroAction action, long ts)
    {
        // Timing comes from the hook timestamps, so it is exact regardless of processing latency.
        var delayTicks = Math.Max(0, ts - _lastEmitTs);
        action.DelayMs = (int)Math.Round(delayTicks * 1000.0 / Stopwatch.Frequency);
        _lastEmitTs = Math.Max(_lastEmitTs, ts);
        _actions.Add(action);
        ActionRecorded?.Invoke(this, action);
    }

    public void Dispose() => _platform.Hook.InputReceived -= OnInput;
}
