namespace DoinkBinds.Models;

/// <summary>Application-wide settings persisted to settings.json in the user's app-data folder.</summary>
public sealed class AppSettings
{
    public List<HotkeyBinding> Hotkeys { get; set; } = DefaultHotkeys();

    /// <summary>Scope used for per-macro trigger hotkeys.</summary>
    public HotkeyScope MacroTriggerScope { get; set; } = HotkeyScope.Global;

    // ---- Recording ----
    public MousePositionMode DefaultMouseMode { get; set; } = MousePositionMode.Absolute;
    public bool RecordMouseMovement { get; set; } = true;

    /// <summary>Minimum milliseconds between two recorded mouse-move samples (reduces noise / file size).</summary>
    public int MouseMoveSampleMs { get; set; } = 15;

    /// <summary>Ignore mouse moves smaller than this many pixels/points.</summary>
    public int MouseMoveMinDistance { get; set; } = 2;

    public bool RecordWindowInfo { get; set; } = true;

    /// <summary>Clicks/keys made while Doink Binds itself is focused are not recorded.</summary>
    public bool IgnoreOwnWindow { get; set; } = true;

    public bool ShowRecordingIndicator { get; set; } = true;
    public bool MinimizeWhileRecording { get; set; }

    /// <summary>Countdown in seconds before recording starts (0 = immediately).</summary>
    public int RecordCountdownSeconds { get; set; }

    // ---- Playback ----
    public MonitorAwareness MonitorAwareness { get; set; } = MonitorAwareness.RecordedMonitor;
    public int WaitForWindowTimeoutMs { get; set; } = 10_000;

    /// <summary>Windows only: send keys as hardware scan codes (better compatibility with games).</summary>
    public bool UseScanCodes { get; set; }

    public bool MinimizeWhilePlaying { get; set; }

    // ---- Appearance ----
    /// <summary>"Dark" (default), "Light" or "System".</summary>
    public string Theme { get; set; } = "Dark";

    // ---- Background ----
    /// <summary>Master switch for If/Then rules watching the screen in the background.</summary>
    public bool RulesEnabled { get; set; }

    /// <summary>Closing the window keeps Doink Binds running in the system tray.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool StartWithSystem { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>Number of run reports to keep on disk.</summary>
    public int MaxReports { get; set; } = 100;

    // ---- State ----
    public bool MacrosEnabled { get; set; } = true;
    public Guid? LastSelectedMacroId { get; set; }
    public bool SamplesInstalled { get; set; }

    public static List<HotkeyBinding> DefaultHotkeys() => new()
    {
        new() { Action = HotkeyAction.StartStopRecording, Hotkey = new Hotkey(KeyCodes.F(6)) },
        new() { Action = HotkeyAction.PlayPause, Hotkey = new Hotkey(KeyCodes.F(7)) },
        new() { Action = HotkeyAction.ToggleMacros, Hotkey = new Hotkey(KeyCodes.F(8)) },
        new() { Action = HotkeyAction.StopLoop, Hotkey = new Hotkey(KeyCodes.F(9)) },
        new() { Action = HotkeyAction.ToggleRules, Hotkey = new Hotkey(KeyCodes.F(10)) },
        new() { Action = HotkeyAction.GuidedRecording, Hotkey = new Hotkey(KeyCodes.F(6), HotkeyModifiers.Shift) },
    };

    /// <summary>Makes sure every action has a binding entry (e.g. after an upgrade added a new action).</summary>
    public void Normalize()
    {
        var defaults = DefaultHotkeys();
        foreach (var d in defaults)
        {
            if (Hotkeys.All(h => h.Action != d.Action))
                Hotkeys.Add(d);
        }
        Hotkeys = Hotkeys.GroupBy(h => h.Action).Select(g => g.First()).OrderBy(h => h.Action).ToList();
        MouseMoveSampleMs = Math.Clamp(MouseMoveSampleMs, 1, 1000);
        MouseMoveMinDistance = Math.Clamp(MouseMoveMinDistance, 0, 100);
        WaitForWindowTimeoutMs = Math.Clamp(WaitForWindowTimeoutMs, 0, 600_000);
        RecordCountdownSeconds = Math.Clamp(RecordCountdownSeconds, 0, 10);
    }
}
