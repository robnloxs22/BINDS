using DoinkBinds.Models;

namespace DoinkBinds.ViewModels;

/// <summary>Creates steps with sensible defaults, and the ready-made templates for the "Templates" menu.</summary>
public static class StepFactory
{
    public static MacroAction Create(MacroActionType type)
    {
        var a = new MacroAction { Type = type, DelayMs = 100 };
        ApplyDefaults(a);
        return a;
    }

    /// <summary>Fills in whatever the step type needs but doesn't have yet (used when the type changes too).</summary>
    public static void ApplyDefaults(MacroAction a)
    {
        switch (a.Type)
        {
            case MacroActionType.MouseMove:
            case MacroActionType.MouseDown:
            case MacroActionType.MouseUp:
                a.Position ??= new MousePosition();
                break;
            case MacroActionType.MouseClick:
                a.Position ??= new MousePosition();
                if (a.Count <= 0) a.Count = 1;
                break;
            case MacroActionType.MouseWheel:
                if (a.WheelDelta == 0) a.WheelDelta = -120;
                break;
            case MacroActionType.KeyDown:
            case MacroActionType.KeyUp:
            case MacroActionType.KeyPress:
                if (a.KeyCode == 0) a.KeyCode = KeyCodes.Enter;
                break;
            case MacroActionType.Wait:
                if (a.DelayMs <= 100) a.DelayMs = 1000;
                break;
            case MacroActionType.RandomWait:
                if (a.DelayMs <= 0) a.DelayMs = 500;
                if (a.MaxDelayMs <= a.DelayMs) a.MaxDelayMs = a.DelayMs * 3;
                break;
            case MacroActionType.TypeText:
                a.Text ??= "";
                break;
            case MacroActionType.WaitFor:
                a.Condition ??= Condition.NewLeaf(ConditionType.ImageVisible);
                if (a.TimeoutMs <= 0) a.TimeoutMs = 15_000;
                break;
            case MacroActionType.FindAndClick:
                a.Condition ??= Condition.NewLeaf(ConditionType.ImageVisible);
                if (a.TimeoutMs <= 0) a.TimeoutMs = 10_000;
                if (a.Count <= 0) a.Count = 1;
                break;
            case MacroActionType.If:
            case MacroActionType.Check:
                a.Condition ??= Condition.NewLeaf(ConditionType.ImageVisible);
                if (a.Type == MacroActionType.Check && a.OnFail == FailAction.Stop) a.OnFail = FailAction.Skip;
                a.DelayMs = Math.Min(a.DelayMs, 100);
                break;
            case MacroActionType.Loop:
                if (a.Count <= 0) a.Count = 3;
                if (a.LoopKind is LoopKind.While or LoopKind.Until) a.Condition ??= Condition.NewLeaf(ConditionType.ImageVisible);
                a.DelayMs = 0;
                break;
            case MacroActionType.Else:
            case MacroActionType.EndIf:
            case MacroActionType.EndLoop:
            case MacroActionType.Break:
            case MacroActionType.Comment:
                a.DelayMs = 0;
                break;
            case MacroActionType.SetVariable:
                a.VariableName ??= "counter";
                a.Text ??= "1";
                break;
            case MacroActionType.ReadText:
                a.VariableName ??= "text";
                a.Region ??= new ScreenRegion();
                break;
            case MacroActionType.Notify:
                a.Text ??= "Macro finished!";
                break;
            case MacroActionType.Screenshot:
                a.Text ??= "screenshot";
                break;
            case MacroActionType.WindowAction:
                a.Window ??= new WindowTarget();
                break;
            case MacroActionType.OpenApp:
                a.Text ??= "";
                break;
            case MacroActionType.StopMacro:
                a.Text ??= "";
                break;
        }
    }

    // ------------------------------------------------------------------ templates

    public sealed record Template(string Name, string Description, Func<List<MacroAction>> Build);

    private static MacroAction A(MacroActionType t, Action<MacroAction>? init = null)
    {
        var a = Create(t);
        init?.Invoke(a);
        return a;
    }

    public static IReadOnlyList<Template> Templates { get; } = new List<Template>
    {
        new("Auto-clicker", "Clicks where the mouse is, 10 times a second, until stopped (F9).", () => new()
        {
            A(MacroActionType.Loop, a => { a.LoopKind = LoopKind.Forever; a.Note = "Auto-clicker"; }),
            A(MacroActionType.MouseClick, a => { a.Position = null; a.DelayMs = 100; }),
            A(MacroActionType.EndLoop)
        }),
        new("Click an image when it appears", "Waits for a button/icon to show up, then clicks it.", () => new()
        {
            A(MacroActionType.FindAndClick, a => { a.Note = "Click the button when it appears"; a.TimeoutMs = 30_000; })
        }),
        new("Refresh until text appears", "Presses F5 every few seconds until some text is on screen.", () => new()
        {
            A(MacroActionType.Loop, a => { a.LoopKind = LoopKind.Until; a.Condition = new Condition { Type = ConditionType.TextVisible, Text = "In stock" }; a.Note = "Until the text shows up"; }),
            A(MacroActionType.KeyPress, a => { a.KeyCode = KeyCodes.F(5); a.Note = "Refresh"; }),
            A(MacroActionType.Wait, a => a.DelayMs = 3000),
            A(MacroActionType.EndLoop),
            A(MacroActionType.Notify, a => { a.Text = "The text appeared!"; a.Flag = true; })
        }),
        new("Fill in a login form", "Clicks the username box, types {username}, Tab, {password}, Enter.", () => new()
        {
            A(MacroActionType.FindAndClick, a => { a.Note = "Click the username box"; }),
            A(MacroActionType.TypeText, a => a.Text = "{username}"),
            A(MacroActionType.KeyPress, a => a.KeyCode = KeyCodes.Tab),
            A(MacroActionType.TypeText, a => a.Text = "{password}"),
            A(MacroActionType.KeyPress, a => a.KeyCode = KeyCodes.Enter),
            A(MacroActionType.Check, a => { a.Note = "Logged in?"; a.Condition = new Condition { Type = ConditionType.TextVisible, Text = "Welcome" }; })
        }),
        new("If image is visible … else …", "Branches depending on what's on screen.", () => new()
        {
            A(MacroActionType.If, a => a.Note = "If the image is visible"),
            A(MacroActionType.Comment, a => a.Note = "…steps when it IS visible"),
            A(MacroActionType.Else),
            A(MacroActionType.Comment, a => a.Note = "…steps when it is NOT visible"),
            A(MacroActionType.EndIf)
        }),
        new("Repeat 10 times with a counter", "A loop that counts {counter} from 1 to 10.", () => new()
        {
            A(MacroActionType.SetVariable, a => { a.VariableName = "counter"; a.Text = "0"; a.VariableOp = VariableOp.Set; }),
            A(MacroActionType.Loop, a => { a.LoopKind = LoopKind.Count; a.Count = 10; }),
            A(MacroActionType.SetVariable, a => { a.VariableName = "counter"; a.Text = "1"; a.VariableOp = VariableOp.Add; }),
            A(MacroActionType.Comment, a => a.Note = "…your steps here (use {counter})"),
            A(MacroActionType.EndLoop)
        }),
        new("Keep awake (mouse jiggle)", "Nudges the mouse every 4 minutes so the PC doesn't sleep.", () => new()
        {
            A(MacroActionType.Loop, a => a.LoopKind = LoopKind.Forever),
            A(MacroActionType.MouseMove, a => { a.Position = new MousePosition { Mode = MousePositionMode.Relative, X = 2, Y = 0 }; a.DelayMs = 240_000; }),
            A(MacroActionType.MouseMove, a => { a.Position = new MousePosition { Mode = MousePositionMode.Relative, X = -2, Y = 0 }; a.DelayMs = 200; }),
            A(MacroActionType.EndLoop)
        }),
        new("Screenshot every minute", "Saves a screenshot into the run report every 60 seconds.", () => new()
        {
            A(MacroActionType.Loop, a => a.LoopKind = LoopKind.Forever),
            A(MacroActionType.Screenshot, a => { a.Text = "{time}"; a.DelayMs = 0; }),
            A(MacroActionType.Wait, a => a.DelayMs = 60_000),
            A(MacroActionType.EndLoop)
        }),
        new("Open app and wait for its window", "Starts an app, waits until its window is open, brings it to the front.", () => new()
        {
            A(MacroActionType.OpenApp, a => { a.Text = OperatingSystem.IsWindows() ? "notepad.exe" : "/System/Applications/TextEdit.app"; }),
            A(MacroActionType.WindowAction, a => { a.WindowOp = WindowOp.WaitForWindow; a.Window = new WindowTarget { ProcessName = OperatingSystem.IsWindows() ? "notepad" : "TextEdit" }; a.TimeoutMs = 15_000; }),
            A(MacroActionType.WindowAction, a => { a.WindowOp = WindowOp.Focus; a.Window = new WindowTarget { ProcessName = OperatingSystem.IsWindows() ? "notepad" : "TextEdit" }; })
        }),
        new("Test: check text is shown", "A test step: records PASS/FAIL in the run report.", () => new()
        {
            A(MacroActionType.Check, a => { a.Note = "Page shows the title"; a.Condition = new Condition { Type = ConditionType.TextVisible, Text = "Dashboard" }; }),
            A(MacroActionType.Screenshot, a => a.Text = "after check")
        })
    };
}
