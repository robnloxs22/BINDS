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
    Wait,         // Pure delay, no input

    // ---- v2: automation steps ----
    TypeText,     // Types a (variable-interpolated) string
    RandomWait,   // Waits a random time between DelayMs and MaxDelayMs
    RunMacro,     // Plays another macro from the library (shares variables)
    OpenApp,      // Launches an app, file or URL
    WindowAction, // Focus / move / resize / maximize / minimize / close a window, or apply a layout
    WaitFor,      // Waits until a condition is true (image/text/color/window/variable)
    FindAndClick, // Finds an image or text on screen and clicks it
    If,           // Runs the following steps only if a condition is true ...
    Else,         // ... otherwise these ...
    EndIf,        // ... until here
    Loop,         // Repeats the steps up to EndLoop (count / while / until / forever)
    EndLoop,
    Break,        // Leaves the innermost loop
    SetVariable,  // Sets / adds to a variable
    ReadText,     // Reads text from a screen area into a variable (OCR)
    Check,        // Test assertion: records pass/fail in the run report
    Notify,       // Shows a notification (and optionally plays a sound)
    Screenshot,   // Saves a screenshot of an area into the run report
    Comment,      // Does nothing; a label in the timeline
    StopMacro     // Ends playback immediately (optionally as a failure)
}

/// <summary>Kinds of condition nodes. Groups combine children; the rest are leaves.</summary>
public enum ConditionType
{
    All,            // AND
    Any,            // OR
    ImageVisible,
    TextVisible,
    PixelColor,
    AreaColor,
    WindowExists,
    WindowFocused,
    Variable
}

public enum TextMatchMode
{
    Contains,
    Equals,
    StartsWith,
    Regex,
    NumberGreater,
    NumberLess,
    NumberEquals,
    AnyText
}

public enum CompareOp
{
    Equals,
    NotEquals,
    Greater,
    Less,
    GreaterOrEqual,
    LessOrEqual,
    Contains,
    IsEmpty
}

/// <summary>What happens when a WaitFor / FindAndClick / Check step fails.</summary>
public enum FailAction
{
    Stop,      // stop playback with an error (default)
    Skip,      // carry on with the next step
    Retry      // try the step again (until playback is stopped)
}

public enum LoopKind
{
    Count,
    While,
    Until,
    Forever
}

public enum VariableOp
{
    Set,
    Add,
    Subtract,
    Append
}

public enum WindowOp
{
    Focus,
    MoveResize,
    Maximize,
    Minimize,
    Restore,
    Close,
    ApplyLayout,
    WaitForWindow
}

public enum RuleTriggerMode
{
    /// <summary>Fire once each time the condition changes from false to true.</summary>
    OnBecomeTrue,
    /// <summary>Fire repeatedly while true (respecting the cooldown).</summary>
    WhileTrue
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
    PinPoint,
    /// <summary>X/Y are an offset from the top-left corner of the step's target window (follows the window when it moves).</summary>
    Window,
    /// <summary>X/Y are an offset from the centre of the last image/text found by a WaitFor / FindAndClick / If step.</summary>
    Found
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
    LoopInfinite,
    LoopDuration
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
    StopLoop,
    ToggleRules,
    GuidedRecording
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
