using System.Text.Json;
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
        MousePositionMode.Window => $"window {(X >= 0 ? "+" : "")}{X:0}, {(Y >= 0 ? "+" : "")}{Y:0}",
        MousePositionMode.Found => $"found {(X >= 0 ? "+" : "")}{X:0}, {(Y >= 0 ? "+" : "")}{Y:0}",
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
/// JSON simple, stable and easy to hand-edit. Fields that a step type doesn't use are
/// left at their defaults and omitted from the file.
/// </summary>
public sealed class MacroAction
{
    public MacroActionType Type { get; set; }

    /// <summary>Delay in milliseconds to wait BEFORE this action executes (time since the previous action).</summary>
    public int DelayMs { get; set; }

    /// <summary>Mouse actions: where the action happens. Null means "wherever the cursor is".</summary>
    public MousePosition? Position { get; set; }

    public MacroMouseButton Button { get; set; }

    /// <summary>Wheel delta in Windows units (120 = one notch). Positive = up / right.</summary>
    public int WheelDelta { get; set; }

    public bool Horizontal { get; set; }

    /// <summary>Platform-neutral key code (Windows virtual-key numbering, see <see cref="KeyCodes"/>).</summary>
    public int KeyCode { get; set; }

    /// <summary>Foreground window at record time, or the target window for WindowAction / Window-relative positions.</summary>
    public WindowTarget? Window { get; set; }

    /// <summary>Step label shown in the timeline ("Click the Login button").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    // ---------------- v2 automation fields ----------------

    /// <summary>TypeText: text · OpenApp: path/URL · Notify: message · SetVariable: value · Screenshot: caption.
    /// Supports {variables}.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>OpenApp: command-line arguments.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }

    /// <summary>WaitFor / FindAndClick / If / Loop(While/Until) / Check: the condition to evaluate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Condition? Condition { get; set; }

    /// <summary>WaitFor / FindAndClick / OpenApp(wait for window): give up after this many ms (0 = forever).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TimeoutMs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FailAction OnFail { get; set; }

    /// <summary>RandomWait: upper bound of the random delay.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxDelayMs { get; set; }

    /// <summary>RunMacro: the macro to play.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TargetMacroId { get; set; }

    /// <summary>RunMacro: display name (used if the id can't be found, e.g. after import).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetMacroName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LoopKind LoopKind { get; set; }

    /// <summary>Loop(Count): number of repetitions · FindAndClick: clicks (2 = double-click).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; set; }

    /// <summary>SetVariable / ReadText: variable name (without braces).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VariableOp VariableOp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WindowOp WindowOp { get; set; }

    /// <summary>WindowAction(MoveResize): new bounds · ReadText / Screenshot: the screen area.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenRegion? Region { get; set; }

    /// <summary>WindowAction(ApplyLayout): the layout to apply.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LayoutId { get; set; }

    /// <summary>FindAndClick: click offset from the centre of the match.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OffsetX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OffsetY { get; set; }

    /// <summary>Notify: also play a sound · StopMacro: count as failure.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flag { get; set; }

    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Deep copy (JSON round-trip, so new fields can never be forgotten here).</summary>
    public MacroAction Clone() =>
        JsonSerializer.Deserialize<MacroAction>(JsonSerializer.Serialize(this, CloneOptions), CloneOptions)!;

    public bool IsMouseAction => Type is MacroActionType.MouseMove or MacroActionType.MouseDown
        or MacroActionType.MouseUp or MacroActionType.MouseClick or MacroActionType.MouseWheel;

    public bool IsKeyAction => Type is MacroActionType.KeyDown or MacroActionType.KeyUp or MacroActionType.KeyPress;

    /// <summary>Steps that open a block (indent the timeline).</summary>
    public bool OpensBlock => Type is MacroActionType.If or MacroActionType.Loop;

    public bool ClosesBlock => Type is MacroActionType.EndIf or MacroActionType.EndLoop;

    public bool UsesCondition => Type is MacroActionType.WaitFor or MacroActionType.FindAndClick or MacroActionType.If
        or MacroActionType.Check || (Type == MacroActionType.Loop && LoopKind is LoopKind.While or LoopKind.Until);
}

/// <summary>A rectangle on screen in desktop coordinates (or relative to a window).</summary>
public sealed class ScreenRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>When true, X/Y are relative to the top-left of the condition's / step's target window.</summary>
    public bool RelativeToWindow { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Width < 1 || Height < 1;

    public ScreenRegion Clone() => (ScreenRegion)MemberwiseClone();

    public override string ToString() => IsEmpty ? "Whole screen" :
        $"{Width:0}×{Height:0} at {X:0},{Y:0}{(RelativeToWindow ? " (in window)" : "")}";
}

/// <summary>
/// A screen / state condition. Groups (All = AND, Any = OR) combine children; every node can
/// be negated (NOT). Leaf nodes look at pixels, images, text, windows or variables.
/// </summary>
public sealed class Condition
{
    public ConditionType Type { get; set; } = ConditionType.ImageVisible;

    /// <summary>NOT: invert the result.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Negate { get; set; }

    /// <summary>All / Any: child conditions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Condition>? Children { get; set; }

    /// <summary>Where to look. Null/empty = all monitors.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenRegion? Region { get; set; }

    /// <summary>ImageVisible: the template image as base64 PNG.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImagePng { get; set; }

    /// <summary>ImageVisible: pixels per desktop unit when the template was captured (2 on Retina), so it can be rescaled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ImageScale { get; set; }

    /// <summary>ImageVisible: required similarity 0.5 – 1.0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Threshold { get; set; }

    /// <summary>TextVisible: text to look for · Variable: the value to compare with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextMatchMode Match { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CaseSensitive { get; set; }

    /// <summary>PixelColor / AreaColor: "#RRGGBB".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>PixelColor / AreaColor: allowed per-channel difference (0–255).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Tolerance { get; set; }

    /// <summary>AreaColor: minimum percentage of the area that must match (1–100).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Percent { get; set; }

    /// <summary>PixelColor: the point to sample.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MousePosition? Point { get; set; }

    /// <summary>WindowExists / WindowFocused, and the window for window-relative regions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowTarget? Window { get; set; }

    /// <summary>Variable: name of the variable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variable { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CompareOp Op { get; set; }

    public bool IsGroup => Type is ConditionType.All or ConditionType.Any;

    public Condition Clone()
    {
        var c = (Condition)MemberwiseClone();
        c.Children = Children?.Select(x => x.Clone()).ToList();
        c.Region = Region?.Clone();
        c.Point = Point?.Clone();
        c.Window = Window?.Clone();
        return c;
    }

    public static Condition NewLeaf(ConditionType type) => type switch
    {
        ConditionType.All or ConditionType.Any => new Condition { Type = type, Children = new() },
        ConditionType.ImageVisible => new Condition { Type = type, Threshold = 0.9 },
        ConditionType.TextVisible => new Condition { Type = type, Text = "", Match = TextMatchMode.Contains },
        ConditionType.PixelColor => new Condition { Type = type, Color = "#FF0000", Tolerance = 16, Point = new MousePosition() },
        ConditionType.AreaColor => new Condition { Type = type, Color = "#FF0000", Tolerance = 16, Percent = 30, Region = new ScreenRegion { Width = 100, Height = 100 } },
        ConditionType.Variable => new Condition { Type = type, Variable = "counter", Op = CompareOp.Equals, Text = "0" },
        _ => new Condition { Type = type, Window = new WindowTarget() }
    };

    /// <summary>One-line human description for the timeline ("Image visible AND NOT text 'Error'").</summary>
    public string Describe()
    {
        var core = Type switch
        {
            ConditionType.All => Children is { Count: > 0 } ? string.Join(" AND ", Children.Select(Wrap)) : "(empty AND)",
            ConditionType.Any => Children is { Count: > 0 } ? string.Join(" OR ", Children.Select(Wrap)) : "(empty OR)",
            ConditionType.ImageVisible => ImagePng is null ? "image (not captured)" : "image visible",
            ConditionType.TextVisible => Match == TextMatchMode.AnyText ? "any text visible" : $"text {MatchWord()} \"{Text}\"",
            ConditionType.PixelColor => $"pixel {Point} is {Color}",
            ConditionType.AreaColor => $"{Percent:0}% of area is {Color}",
            ConditionType.WindowExists => $"window \"{Window}\" is open",
            ConditionType.WindowFocused => $"window \"{Window}\" is focused",
            ConditionType.Variable => $"{{{Variable}}} {OpWord()} {Text}",
            _ => Type.ToString()
        };
        return Negate ? $"NOT {core}" : core;

        static string Wrap(Condition c) => c.IsGroup ? $"({c.Describe()})" : c.Describe();
    }

    private string MatchWord() => Match switch
    {
        TextMatchMode.Equals => "equals",
        TextMatchMode.StartsWith => "starts with",
        TextMatchMode.Regex => "matches",
        TextMatchMode.NumberGreater => "number >",
        TextMatchMode.NumberLess => "number <",
        TextMatchMode.NumberEquals => "number =",
        _ => "contains"
    };

    private string OpWord() => Op switch
    {
        CompareOp.NotEquals => "≠",
        CompareOp.Greater => ">",
        CompareOp.Less => "<",
        CompareOp.GreaterOrEqual => "≥",
        CompareOp.LessOrEqual => "≤",
        CompareOp.Contains => "contains",
        CompareOp.IsEmpty => "is empty",
        _ => "="
    };
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

    /// <summary>LoopDuration mode: keep looping for this many minutes.</summary>
    public double DurationMinutes { get; set; } = 10;

    /// <summary>Humanize: randomly vary every delay by ± this percentage (0 = exact).</summary>
    public int TimingJitterPercent { get; set; }

    /// <summary>Humanize: randomly offset clicks/moves by up to this many pixels.</summary>
    public int MouseJitterPx { get; set; }

    /// <summary>Always write a run report (reports are written anyway when the macro has Check steps or fails).</summary>
    public bool SaveReport { get; set; }

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

    /// <summary>Statistics.</summary>
    public int RunCount { get; set; }
    public DateTime? LastRunUtc { get; set; }

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
