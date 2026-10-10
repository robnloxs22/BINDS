using DoinkBinds.Models;
using DoinkBinds.Platform;

namespace DoinkBinds.Services;

/// <summary>
/// Global + window-local hotkeys built on top of the shared low-level input hook.
/// Using the hook (instead of RegisterHotKey / Carbon hotkeys) lets the user bind any key
/// combination identically on Windows and macOS, and choose Global vs Local per binding.
/// </summary>
public interface IHotkeyService
{
    /// <summary>Raised (on a background thread) when a built-in command hotkey is pressed.</summary>
    event EventHandler<HotkeyAction>? CommandTriggered;

    /// <summary>Raised (on a background thread) when a macro's trigger hotkey is pressed.</summary>
    event EventHandler<Guid>? MacroTriggered;

    /// <summary>Replaces the set of per-macro trigger hotkeys.</summary>
    void SetMacroTriggers(IEnumerable<(Guid MacroId, Hotkey Hotkey)> triggers);

    /// <summary>True if <paramref name="keyCode"/> is the main key of any binding (the recorder skips those).</summary>
    bool IsHotkeyKey(int keyCode);

    /// <summary>Waits for the user to press a key combination. Esc alone cancels (returns null).</summary>
    Task<Hotkey?> CaptureAsync(CancellationToken ct = default);

    /// <summary>Returns a human-readable description of what already uses <paramref name="hotkey"/>, or null.</summary>
    string? FindConflict(Hotkey hotkey, HotkeyAction? ignoreAction = null, Guid? ignoreMacro = null);

    /// <summary>Lookup for macro names (used in conflict messages).</summary>
    Func<Guid, string?>? MacroNameResolver { get; set; }
}

public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly IPlatformServices _platform;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    private readonly HashSet<int> _pressed = new();
    private List<(Guid MacroId, Hotkey Hotkey)> _macroTriggers = new();
    private TaskCompletionSource<Hotkey?>? _capture;

    public event EventHandler<HotkeyAction>? CommandTriggered;
    public event EventHandler<Guid>? MacroTriggered;
    public Func<Guid, string?>? MacroNameResolver { get; set; }

    public HotkeyService(IPlatformServices platform, ISettingsService settings)
    {
        _platform = platform;
        _settings = settings;
        _platform.Hook.InputReceived += OnInput;
    }

    public void SetMacroTriggers(IEnumerable<(Guid MacroId, Hotkey Hotkey)> triggers)
    {
        lock (_gate) _macroTriggers = triggers.ToList();
    }

    public bool IsHotkeyKey(int keyCode)
    {
        lock (_gate)
        {
            return _settings.Settings.Hotkeys.Any(h => h.Hotkey?.KeyCode == keyCode)
                   || _macroTriggers.Any(t => t.Hotkey.KeyCode == keyCode);
        }
    }

    public Task<Hotkey?> CaptureAsync(CancellationToken ct = default)
    {
        // Capturing relies on the global hook; without it no key would ever arrive.
        if (!_platform.Hook.IsRunning) return Task.FromResult<Hotkey?>(null);

        lock (_gate)
        {
            _capture?.TrySetResult(null);
            _capture = new TaskCompletionSource<Hotkey?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcs = _capture;
            ct.Register(() => tcs.TrySetResult(null));
            return tcs.Task;
        }
    }

    public string? FindConflict(Hotkey hotkey, HotkeyAction? ignoreAction = null, Guid? ignoreMacro = null)
    {
        foreach (var b in _settings.Settings.Hotkeys)
        {
            if (b.Action != ignoreAction && hotkey.Equals(b.Hotkey))
                return $"\"{Describe(b.Action)}\"";
        }

        lock (_gate)
        {
            foreach (var (id, hk) in _macroTriggers)
            {
                if (id != ignoreMacro && hotkey.Equals(hk))
                    return $"macro \"{MacroNameResolver?.Invoke(id) ?? "?"}\"";
            }
        }
        return null;
    }

    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.StartStopRecording => "Start / Stop recording",
        HotkeyAction.PlayPause => "Play / Pause",
        HotkeyAction.ToggleMacros => "Toggle macros on / off",
        HotkeyAction.StopLoop => "Stop playback",
        _ => action.ToString()
    };

    private HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        foreach (var k in _pressed) m |= KeyCodes.ModifierOf(k);
        return m;
    }

    private void OnInput(RawInputEvent e)
    {
        // Our own injected playback must never fire hotkeys (a macro that types F6 would toggle recording!).
        if (!e.IsKey || e.IsSynthetic) return;

        HotkeyAction? command = null;
        Guid? macro = null;

        lock (_gate)
        {
            if (e.Kind == RawInputKind.KeyUp)
            {
                _pressed.Remove(e.KeyCode);
                return;
            }

            // Ignore OS auto-repeat: only the first KeyDown of a held key counts.
            if (!_pressed.Add(e.KeyCode)) return;
            if (KeyCodes.IsModifier(e.KeyCode)) return;

            var mods = CurrentModifiers();

            if (_capture is { } capture)
            {
                _capture = null;
                capture.TrySetResult(e.KeyCode == KeyCodes.Escape && mods == HotkeyModifiers.None
                    ? null
                    : new Hotkey(e.KeyCode, mods));
                return;
            }

            var binding = _settings.Settings.Hotkeys.FirstOrDefault(b => b.Hotkey?.Matches(e.KeyCode, mods) == true);
            if (binding is not null)
            {
                if (ScopeAllows(binding.Scope)) command = binding.Action;
            }
            else
            {
                var trig = _macroTriggers.FirstOrDefault(t => t.Hotkey.Matches(e.KeyCode, mods));
                if (trig.Hotkey is not null && ScopeAllows(_settings.Settings.MacroTriggerScope))
                    macro = trig.MacroId;
            }
        }

        // Raise outside the lock.
        if (command is { } c) CommandTriggered?.Invoke(this, c);
        if (macro is { } id) MacroTriggered?.Invoke(this, id);
    }

    private bool ScopeAllows(HotkeyScope scope) =>
        scope == HotkeyScope.Global || _platform.Windows.IsOwnProcessForeground();

    public void Dispose() => _platform.Hook.InputReceived -= OnInput;
}
