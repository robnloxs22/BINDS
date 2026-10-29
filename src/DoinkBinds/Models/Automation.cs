using System.Text.Json.Serialization;

namespace DoinkBinds.Models;

/// <summary>
/// A background "IF condition THEN steps" rule. The rule engine evaluates <see cref="Condition"/>
/// every <see cref="IntervalMs"/> and runs <see cref="Actions"/> (any macro steps, including
/// RunMacro / FindAndClick / Notify / WindowAction) when it fires.
/// </summary>
public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    public Condition Condition { get; set; } = Condition.NewLeaf(ConditionType.ImageVisible);
    public List<MacroAction> Actions { get; set; } = new();
    public RuleTriggerMode Trigger { get; set; } = RuleTriggerMode.OnBecomeTrue;

    /// <summary>How often to check (ms). 250 = 4× per second.</summary>
    public int IntervalMs { get; set; } = 250;

    /// <summary>Minimum time between two firings (ms).</summary>
    public int CooldownMs { get; set; } = 1000;

    /// <summary>Skip firing while a macro is already playing (otherwise wait for it to finish).</summary>
    public bool SkipWhileBusy { get; set; } = true;

    /// <summary>Stop the rule after it fired this many times (0 = unlimited).</summary>
    public int MaxFires { get; set; }

    public Rule Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name + " copy",
        Enabled = false,
        Condition = Condition.Clone(),
        Actions = Actions.Select(a => a.Clone()).ToList(),
        Trigger = Trigger,
        IntervalMs = IntervalMs,
        CooldownMs = CooldownMs,
        SkipWhileBusy = SkipWhileBusy,
        MaxFires = MaxFires
    };
}

public sealed class RuleSet
{
    public int Version { get; set; } = 1;
    public List<Rule> Rules { get; set; } = new();
    public List<WindowLayout> Layouts { get; set; } = new();
}

public enum WindowStateKind
{
    Normal,
    Maximized,
    Minimized
}

/// <summary>One window in a saved layout: which app, where it goes, and how to start it if it isn't running.</summary>
public sealed class WindowLayoutEntry
{
    public string ProcessName { get; set; } = "";

    /// <summary>Optional: only windows whose title contains this.</summary>
    public string TitleContains { get; set; } = "";

    /// <summary>Optional: app path / URL / command to launch when no matching window exists.</summary>
    public string LaunchCommand { get; set; } = "";

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 600;
    public WindowStateKind State { get; set; }

    public WindowLayoutEntry Clone() => (WindowLayoutEntry)MemberwiseClone();
}

/// <summary>A named arrangement of app windows that can be restored with one click or hotkey.</summary>
public sealed class WindowLayout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New layout";
    public Hotkey? Hotkey { get; set; }
    public List<WindowLayoutEntry> Entries { get; set; } = new();

    /// <summary>Seconds to wait for launched apps to show their window.</summary>
    public int LaunchWaitSeconds { get; set; } = 15;
}

/// <summary>A window currently on screen.</summary>
public sealed record WindowInfo(nint Handle, int ProcessId, string ProcessName, string Title,
    double X, double Y, double Width, double Height, bool IsMinimized, bool IsMaximized)
{
    public WindowTarget ToTarget() => new() { ProcessName = ProcessName, Title = Title };
    public override string ToString() => string.IsNullOrEmpty(Title) ? ProcessName : $"{ProcessName} — {Title}";
}

// ---------------- Run reports (testing) ----------------

public sealed class ReportEntry
{
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public int StepNumber { get; set; }
    public string Description { get; set; } = "";
    public bool Passed { get; set; }
    public string? Message { get; set; }

    /// <summary>File name of a screenshot inside the report folder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Screenshot { get; set; }
}

public sealed class RunReport
{
    public Guid MacroId { get; set; }
    public string MacroName { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }

    /// <summary>"Passed", "Failed", "Stopped" or "Error".</summary>
    public string Result { get; set; } = "Running";
    public string? Error { get; set; }
    public int Iterations { get; set; }
    public List<ReportEntry> Entries { get; set; } = new();

    [JsonIgnore] public string? Folder { get; set; }

    public int PassedCount => Entries.Count(e => e.Passed);
    public int FailedCount => Entries.Count(e => !e.Passed);
}
