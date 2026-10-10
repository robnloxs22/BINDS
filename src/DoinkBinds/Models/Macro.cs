using System.Text.Json.Serialization;

namespace DoinkBinds.Models;

/// <summary>
/// A mouse coordinate whose meaning depends on <see cref="Mode"/>.
/// All coordinates live in the platform's global desktop space
/// (physical pixels on Windows, points on macOS).
/// </summary>
public sealed class MousePosition
{
    public MousePositionMode Mode { get; set; } = MousePositionMode.Absolute;

    /// <summary>Absolute/PinPoint: desktop X. Relative: delta X. Percentage: 0–100.</summary>
    public double X { get; set; }

    /// <summary>Absolute/PinPoint: desktop Y. Relative: delta Y. Percentage: 0–100.</summary>
    public double Y { get; set; }

    /// <summary>Monitor index the point was recorded on (used by Percentage mode).</summary>
    public int MonitorIndex { get; set; }

    public MousePosition Clone() => new() { Mode = Mode, X = X, Y = Y, MonitorIndex = MonitorIndex };

    public override string ToString() => Mode switch
    {
        MousePositionMode.Relative => $"{(X >= 0 ? "+" : "")}{X:0}, {(Y >= 0 ? "+" : "")}{Y:0}",
        MousePositionMode.Percentage => $"{X:0.##}%, {Y:0.##}% (mon {MonitorIndex + 1})",
        _ => $"{X:0}, {Y:0}"
    };
}

/// <summary>Identifies the window that was in the foreground when an action was recorded.</summary>
public sealed class WindowTarget
{
    public string ProcessName { get; set; } = "";
    public string Title { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(ProcessName) && string.IsNullOrWhiteSpace(Title);

    public bool SameAs(WindowTarget? other) =>
        other is not null &&
        string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Title, other.Title, StringComparison.Ordinal);

    public WindowTarget Clone() => new() { ProcessName = ProcessName, Title = Title };

    public override string ToString() =>
        string.IsNullOrEmpty(Title) ? ProcessName : $"{ProcessName} — {Title}";
}

/// <summary>
/// One step of a macro. A flat class (rather than a type hierarchy) keeps the .doink
/// JSON simple, stable and easy to hand-edit.
/// </summary>
public sealed class MacroAction
{
    public MacroActionType Type { get; set; }

    /// <summary>Delay in milliseconds to wait BEFORE this action executes (time since the previous action).</summary>
    public int DelayMs { get; set; }

    /// <summary>Mouse actions: where the action happens. Null for MouseWheel means "wherever the cursor is".</summary>
    public MousePosition? Position { get; set; }

    public MacroMouseButton Button { get; set; }

    /// <summary>Wheel delta in Windows units (120 = one notch). Positive = up / right.</summary>
    public int WheelDelta { get; set; }

    public bool Horizontal { get; set; }

    /// <summary>Platform-neutral key code (Windows virtual-key numbering, see <see cref="KeyCodes"/>).</summary>
    public int KeyCode { get; set; }

    /// <summary>Foreground window at record time (optional).</summary>
    public WindowTarget? Window { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    public MacroAction Clone() => new()
    {
        Type = Type,
        DelayMs = DelayMs,
        Position = Position?.Clone(),
        Button = Button,
        WheelDelta = WheelDelta,
        Horizontal = Horizontal,
        KeyCode = KeyCode,
        Window = Window?.Clone(),
        Note = Note
    };

    public bool IsMouseAction => Type is MacroActionType.MouseMove or MacroActionType.MouseDown
        or MacroActionType.MouseUp or MacroActionType.MouseClick or MacroActionType.MouseWheel;

    public bool IsKeyAction => Type is MacroActionType.KeyDown or MacroActionType.KeyUp or MacroActionType.KeyPress;
}

/// <summary>Per-macro playback preferences (saved with the macro).</summary>
public sealed class PlaybackSettings
{
    public PlaybackMode Mode { get; set; } = PlaybackMode.Once;
    public int LoopCount { get; set; } = 3;
    public double Speed { get; set; } = 1.0;

    /// <summary>Bring the recorded window back to the foreground when the window target changes.</summary>
    public bool RestoreWindowFocus { get; set; } = true;

    /// <summary>Block until the recorded window is focused before continuing (with timeout).</summary>
    public bool WaitForWindow { get; set; }

    /// <summary>Delay between loop iterations in milliseconds.</summary>
    public int LoopDelayMs { get; set; } = 250;

    public PlaybackSettings Clone() => (PlaybackSettings)MemberwiseClone();
}

public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Macro";
    public string Folder { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Optional hotkey that plays this macro directly.</summary>
    public Hotkey? TriggerHotkey { get; set; }

    public PlaybackSettings Playback { get; set; } = new();
    public List<MacroAction> Actions { get; set; } = new();

    [JsonIgnore]
    public TimeSpan TotalDuration => TimeSpan.FromMilliseconds(Actions.Sum(a => (long)a.DelayMs));

    /// <summary>Deep copy with a brand new Id.</summary>
    public Macro Duplicate(string newName) => new()
    {
        Name = newName,
        Folder = Folder,
        Description = Description,
        TriggerHotkey = null, // a duplicated trigger would conflict with the original
        Playback = Playback.Clone(),
        Actions = Actions.Select(a => a.Clone()).ToList()
    };
}

/// <summary>Envelope written to .doink files. Versioned so the format can evolve.</summary>
public sealed class DoinkFile
{
    public const int CurrentVersion = 1;

    public string Format { get; set; } = "doink";
    public int Version { get; set; } = CurrentVersion;
    public string App { get; set; } = "Doink Binds";
    public Macro Macro { get; set; } = new();
}
