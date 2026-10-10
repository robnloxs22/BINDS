namespace DoinkBinds.Models;

/// <summary>The kind of a single recorded / editable macro step.</summary>
public enum MacroActionType
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseClick,   // Down + Up at one position (mostly used for manually added steps)
    MouseWheel,
    KeyDown,
    KeyUp,
    KeyPress,     // Down + Up (manually added steps)
    Wait          // Pure delay, no input
}

public enum MacroMouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

/// <summary>
/// How a mouse coordinate is interpreted at playback time.
/// </summary>
public enum MousePositionMode
{
    /// <summary>X/Y are absolute virtual-desktop coordinates (pixels on Windows, points on macOS).</summary>
    Absolute,
    /// <summary>X/Y are an offset from wherever the cursor currently is.</summary>
    Relative,
    /// <summary>X/Y are 0–100 percentages of a monitor (or the whole desktop), resolution independent.</summary>
    Percentage,
    /// <summary>Absolute coordinate that was captured with the visual pin-point picker.</summary>
    PinPoint
}

/// <summary>What a <see cref="MousePositionMode.Percentage"/> coordinate is relative to.</summary>
public enum MonitorAwareness
{
    /// <summary>The monitor the action was recorded on (falls back to primary if it no longer exists).</summary>
    RecordedMonitor,
    /// <summary>Always the primary monitor.</summary>
    PrimaryMonitor,
    /// <summary>The bounding box of all monitors.</summary>
    VirtualDesktop
}

public enum PlaybackMode
{
    Once,
    LoopCount,
    LoopInfinite
}

public enum HotkeyScope
{
    /// <summary>Works no matter which application is focused.</summary>
    Global,
    /// <summary>Only works while a Doink Binds window is focused.</summary>
    Local
}

public enum HotkeyAction
{
    StartStopRecording,
    PlayPause,
    ToggleMacros,
    StopLoop
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8   // Windows key / Command key
}

public enum AppStatus
{
    Idle,
    Recording,
    Playing,
    Paused
}
