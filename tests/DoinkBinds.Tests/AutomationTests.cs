using System.Diagnostics;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;
using DoinkBinds.Vision;

namespace DoinkBinds.Tests;

/// <summary>Small builders that keep the tests readable.</summary>
internal static class S
{
    public static MacroAction Set(string name, string value, VariableOp op = VariableOp.Set) =>
        new() { Type = MacroActionType.SetVariable, VariableName = name, Text = value, VariableOp = op };

    public static MacroAction If(Condition c) => new() { Type = MacroActionType.If, Condition = c };
    public static MacroAction Else() => new() { Type = MacroActionType.Else };
    public static MacroAction EndIf() => new() { Type = MacroActionType.EndIf };
    public static MacroAction Loop(int count) => new() { Type = MacroActionType.Loop, LoopKind = LoopKind.Count, Count = count };
    public static MacroAction LoopUntil(Condition c) => new() { Type = MacroActionType.Loop, LoopKind = LoopKind.Until, Condition = c };
    public static MacroAction EndLoop() => new() { Type = MacroActionType.EndLoop };
    public static MacroAction Break() => new() { Type = MacroActionType.Break };
    public static MacroAction Type(string text) => new() { Type = MacroActionType.TypeText, Text = text, Count = 1 };
    public static MacroAction Notify(string text) => new() { Type = MacroActionType.Notify, Text = text };

    public static Condition Var(string name, CompareOp op, string value) =>
        new() { Type = ConditionType.Variable, Variable = name, Op = op, Text = value };

    public static Macro Macro(params MacroAction[] actions) => new() { Name = "t", Actions = actions.ToList() };

    public static PlaybackSettings Once => new() { RestoreWindowFocus = false, LoopDelayMs = 0 };
}

public class FlowControlTests
{
    [Fact]
    public void If_else_follows_variable_condition()
    {
        var h = new Harness();
        h.Variables.Set("mode", "b");
        var macro = S.Macro(
            S.If(S.Var("mode", CompareOp.Equals, "a")), S.Notify("A"),
            S.Else(), S.Notify("not A"),
            S.EndIf(),
            S.If(new Condition { Type = ConditionType.Variable, Variable = "mode", Op = CompareOp.Equals, Text = "a", Negate = true }),
            S.Notify("NOT a"), S.EndIf());

        var r = h.PlayAndWait(macro, S.Once);
        Assert.True(r.Completed, r.Error);
        Assert.Equal(new[] { "not A", "NOT a" }, h.Notifier.Messages);
    }

    [Fact]
    public void Count_loops_nest_and_break_works()
    {
        var h = new Harness();
        var macro = S.Macro(
            S.Set("outer", "0"), S.Set("inner", "0"),
            S.Loop(3), S.Set("outer", "1", VariableOp.Add),
                S.Loop(4), S.Set("inner", "1", VariableOp.Add),
                    S.If(S.Var("inner", CompareOp.GreaterOrEqual, "100")), S.Break(), S.EndIf(),
                S.EndLoop(),
            S.EndLoop(),
            S.Set("b", "0"),
            S.Loop(10), S.Set("b", "1", VariableOp.Add), S.If(S.Var("b", CompareOp.Equals, "2")), S.Break(), S.EndIf(), S.EndLoop());

        var r = h.PlayAndWait(macro, S.Once);
        Assert.True(r.Completed, r.Error);
        Assert.Equal("3", h.Variables.Get("outer"));
        Assert.Equal("12", h.Variables.Get("inner"));
        Assert.Equal("2", h.Variables.Get("b"));
    }

    [Fact]
    public void Loop_until_condition_and_loop_counter_variable()
    {
        var h = new Harness();
        var macro = S.Macro(
            S.Set("n", "0"),
            S.LoopUntil(S.Var("n", CompareOp.GreaterOrEqual, "5")), S.Set("n", "1", VariableOp.Add), S.Set("last", "{loop}"), S.EndLoop());
        var r = h.PlayAndWait(macro, S.Once);
        Assert.True(r.Completed, r.Error);
        Assert.Equal("5", h.Variables.Get("n"));
        Assert.Equal("5", h.Variables.Get("last"));
    }

    [Fact]
    public void Unbalanced_blocks_give_clear_error_without_running()
    {
        var h = new Harness();
        var r = h.PlayAndWait(S.Macro(S.If(S.Var("x", CompareOp.IsEmpty, "")), S.Notify("hi")), S.Once);
        Assert.False(r.Completed);
        Assert.Contains("missing its \"End If\"", r.Error);
        Assert.Empty(h.Notifier.Messages);

        r = h.PlayAndWait(S.Macro(S.Break()), S.Once);
        Assert.Contains("must be inside a loop", r.Error);
    }

    [Fact]
    public void Type_text_expands_variables_and_types_each_character()
    {
        var h = new Harness();
        h.Variables.Set("user", "doink");
        var r = h.PlayAndWait(S.Macro(S.Type("Hi {user}!")), S.Once);
        Assert.True(r.Completed, r.Error);
        var typed = string.Concat(h.Platform.FakeInput.Snapshot().Where(l => l.StartsWith("type ")).Select(l => l[5..]));
        Assert.Equal("Hi doink!", typed);
    }

    [Fact]
    public void Stop_macro_step_ends_run_as_failure_with_message()
    {
        var h = new Harness();
        var r = h.PlayAndWait(S.Macro(S.Notify("before"),
            new MacroAction { Type = MacroActionType.StopMacro, Flag = true, Text = "Login failed" },
            S.Notify("after")), S.Once);
        Assert.False(r.Completed);
        Assert.Equal("Login failed", r.Error);
        Assert.Equal(new[] { "before" }, h.Notifier.Messages);
    }

    [Fact]
    public void Run_macro_shares_variables_and_guards_recursion()
    {
        var h = new Harness();
        h.Library.Load(out _);
        var child = new Macro { Name = "Child " + Guid.NewGuid().ToString("N")[..6], Actions = { S.Set("fromChild", "yes") } };
        h.Library.Save(child);
        var r = h.PlayAndWait(S.Macro(new MacroAction { Type = MacroActionType.RunMacro, TargetMacroId = child.Id }), S.Once);
        Assert.True(r.Completed, r.Error);
        Assert.Equal("yes", h.Variables.Get("fromChild"));

        var self = new Macro { Name = "Self " + Guid.NewGuid().ToString("N")[..6] };
        self.Actions.Add(new MacroAction { Type = MacroActionType.RunMacro, TargetMacroId = self.Id });
        h.Library.Save(self);
        r = h.PlayAndWait(self, S.Once);
        Assert.Contains("too deeply", r.Error);
    }

    [Fact]
    public void Loop_for_duration_stops_on_time()
    {
        var h = new Harness();
        var sw = Stopwatch.StartNew();
        var r = h.PlayAndWait(S.Macro(new MacroAction { Type = MacroActionType.Wait, DelayMs = 50 }),
            new PlaybackSettings { Mode = PlaybackMode.LoopDuration, DurationMinutes = 0.01, RestoreWindowFocus = false, LoopDelayMs = 0 });
        Assert.True(r.Completed, r.Error);
        Assert.InRange(sw.ElapsedMilliseconds, 500, 2500); // 0.6 s
        Assert.True(r.IterationsCompleted >= 5);
    }

    [Fact]
    public void Humanize_jitter_stays_within_bounds()
    {
        var h = new Harness();
        var steps = Enumerable.Range(0, 30).Select(_ => new MacroAction
        {
            Type = MacroActionType.MouseMove, Position = new MousePosition { X = 500, Y = 500 }
        }).ToArray();
        var r = h.PlayAndWait(S.Macro(steps), new PlaybackSettings { MouseJitterPx = 5, RestoreWindowFocus = false });
        Assert.True(r.Completed, r.Error);
        var moves = h.Platform.FakeInput.Snapshot().Where(l => l.StartsWith("move ")).Select(l => l[5..].Split(',').Select(double.Parse).ToArray()).ToList();
        Assert.All(moves, m => { Assert.InRange(m[0], 495, 505); Assert.InRange(m[1], 495, 505); });
        Assert.True(moves.Select(m => m[0]).Distinct().Count() > 1, "jitter should vary positions");
    }
}

public class ScreenConditionTests
{
    private static Harness WithScreen(BgraImage screen)
    {
        var h = new Harness();
        h.Platform.FakeCapture.Screen = screen;
        h.Platform.FakeScreens.List = new() { new ScreenInfo(0, 0, 0, screen.Width, screen.Height, true) };
        return h;
    }

    [Fact]
    public void Find_and_click_image_clicks_its_centre_plus_offset()
    {
        var screen = TestImages.NoiseScreen(800, 600, 7);
        var h = WithScreen(screen);
        var template = screen.Crop(500, 300, 40, 30);
        var macro = S.Macro(new MacroAction
        {
            Type = MacroActionType.FindAndClick,
            Condition = new Condition { Type = ConditionType.ImageVisible, ImagePng = template.ToBase64Png(), Threshold = 0.9 },
            OffsetX = 5,
            TimeoutMs = 1000
        });
        var r = h.PlayAndWait(macro, S.Once);
        Assert.True(r.Completed, r.Error);
        var log = h.Platform.FakeInput.Snapshot();
        Assert.Contains("move 525,315", log);
        Assert.True(log.IndexOf("move 525,315") < log.IndexOf("Left down"));
    }

    [Fact]
    public void Wait_for_times_out_stops_and_writes_report_with_screenshot()
    {
        var screen = TestImages.NoiseScreen(400, 300, 8);
        var h = WithScreen(screen);
        var missing = TestImages.NoiseScreen(50, 50, 9).Crop(0, 0, 30, 30);
        var macro = S.Macro(
            new MacroAction
            {
                Type = MacroActionType.WaitFor, TimeoutMs = 300,
                Condition = new Condition { Type = ConditionType.ImageVisible, ImagePng = missing.ToBase64Png(), Threshold = 0.9 }
            },
            S.Notify("should not run"));
        var r = h.PlayAndWait(macro, S.Once);
        Assert.False(r.Completed);
        Assert.Contains("timed out", r.Error);
        Assert.Empty(h.Notifier.Messages);
        Assert.NotNull(r.Report);
        Assert.Equal("Failed", r.Report!.Result);
        var entry = Assert.Single(r.Report.Entries);
        Assert.False(entry.Passed);
        Assert.True(File.Exists(Path.Combine(r.Report.Folder!, entry.Screenshot!)));
        Assert.True(File.Exists(Path.Combine(r.Report.Folder!, "report.html")));
    }

    [Fact]
    public void Checks_record_pass_and_fail_and_continue_when_asked()
    {
        var screen = new BgraImage(200, 100);
        for (var y = 0; y < 100; y++) for (var x = 0; x < 200; x++) screen.SetPixel(x, y, x < 100 ? (byte)255 : (byte)0, 0, 0);
        var h = WithScreen(screen);
        var macro = S.Macro(
            new MacroAction
            {
                Type = MacroActionType.Check, Note = "left is red", OnFail = FailAction.Skip,
                Condition = new Condition { Type = ConditionType.PixelColor, Color = "#FF0000", Tolerance = 5, Point = new MousePosition { X = 10, Y = 10 } }
            },
            new MacroAction
            {
                Type = MacroActionType.Check, Note = "right is red", OnFail = FailAction.Skip,
                Condition = new Condition { Type = ConditionType.PixelColor, Color = "#FF0000", Tolerance = 5, Point = new MousePosition { X = 150, Y = 10 } }
            },
            new MacroAction
            {
                Type = MacroActionType.Check, Note = "half is red", OnFail = FailAction.Skip,
                Condition = new Condition { Type = ConditionType.AreaColor, Color = "#FF0000", Tolerance = 5, Percent = 45, Region = new ScreenRegion { Width = 200, Height = 100 } }
            },
            S.Notify("done"));
        var r = h.PlayAndWait(macro, S.Once);
        Assert.Equal(new[] { "done" }, h.Notifier.Messages);
        Assert.Equal("Failed", r.Report!.Result);
        Assert.Equal(new[] { true, false, true }, r.Report.Entries.Select(e => e.Passed));
    }

    [Fact]
    public void Text_conditions_read_the_screen_with_ocr()
    {
        var screen = TestImages.RenderText(900, 500, ("Level complete", 60, 90, 30), ("Score: 4520", 60, 200, 30), ("Play again", 600, 420, 28));
        var h = WithScreen(screen);
        var macro = S.Macro(
            S.If(new Condition
            {
                Type = ConditionType.All, Children = new()
                {
                    new Condition { Type = ConditionType.TextVisible, Text = "level complete" },
                    new Condition { Type = ConditionType.TextVisible, Text = "4000", Match = TextMatchMode.NumberGreater },
                    new Condition { Type = ConditionType.TextVisible, Text = "Game over", Negate = true }
                }
            }),
            S.Notify("won"),
            S.EndIf(),
            new MacroAction { Type = MacroActionType.ReadText, VariableName = "score", Region = new ScreenRegion { X = 40, Y = 160, Width = 400, Height = 60 } },
            new MacroAction { Type = MacroActionType.FindAndClick, TimeoutMs = 3000, Condition = new Condition { Type = ConditionType.TextVisible, Text = "Play again" } });
        var r = h.PlayAndWait(macro, S.Once, 60_000);
        Assert.True(r.Completed, r.Error);
        Assert.Equal(new[] { "won" }, h.Notifier.Messages);
        Assert.Contains("4520", h.Variables.Get("score"));
        var move = h.Platform.FakeInput.Snapshot().Last(l => l.StartsWith("move "))[5..].Split(',').Select(double.Parse).ToArray();
        Assert.InRange(move[0], 600, 760);
        Assert.InRange(move[1], 390, 430);
    }

    [Fact]
    public void Window_relative_positions_and_window_actions()
    {
        var h = new Harness();
        h.Platform.FakeWindows.Windows = new()
        {
            new WindowInfo(1, 10, "notepad", "notes.txt - Notepad", 300, 200, 800, 600, false, false),
            new WindowInfo(2, 11, "chrome", "Google - Chrome", 0, 0, 1000, 700, false, false)
        };
        var win = new WindowTarget { ProcessName = "notepad", Title = "notes" };
        var macro = S.Macro(
            new MacroAction { Type = MacroActionType.MouseClick, Window = win, Position = new MousePosition { Mode = MousePositionMode.Window, X = 50, Y = 40 } },
            new MacroAction { Type = MacroActionType.WindowAction, WindowOp = WindowOp.MoveResize, Window = win, Region = new ScreenRegion { X = 0, Y = 0, Width = 960, Height = 1080 } },
            new MacroAction { Type = MacroActionType.WindowAction, WindowOp = WindowOp.Close, Window = new WindowTarget { ProcessName = "chrome" } });
        var r = h.PlayAndWait(macro, S.Once);
        Assert.True(r.Completed, r.Error);
        Assert.Contains("move 350,240", h.Platform.FakeInput.Snapshot());
        Assert.Contains("bounds notepad 0,0,960,1080", h.Platform.FakeWindows.Ops);
        Assert.Contains("close chrome", h.Platform.FakeWindows.Ops);

        // Window conditions
        var exists = h.Vision.Evaluate(new Condition { Type = ConditionType.WindowExists, Window = win }, new VisionContext());
        Assert.True(exists.Met);
        var gone = h.Vision.Evaluate(new Condition { Type = ConditionType.WindowExists, Window = new WindowTarget { ProcessName = "chrome" } }, new VisionContext());
        Assert.False(gone.Met);
    }

    [Fact]
    public void Any_group_returns_first_true_child()
    {
        var h = new Harness();
        h.Variables.Set("a", "5");
        var c = new Condition
        {
            Type = ConditionType.Any, Children = new()
            {
                S.Var("a", CompareOp.Less, "3"),
                S.Var("a", CompareOp.Greater, "4")
            }
        };
        Assert.True(h.Vision.Evaluate(c, new VisionContext { GetVariable = h.Variables.Get }).Met);
        c.Negate = true;
        Assert.False(h.Vision.Evaluate(c, new VisionContext { GetVariable = h.Variables.Get }).Met);
        Assert.Equal("NOT {a} < 3 OR {a} > 4", c.Describe());
    }
}

public class RuleAndLayoutTests
{
    [Fact]
    public void Rule_fires_on_rising_edge_respecting_cooldown()
    {
        var h = new Harness();
        h.Settings.Settings.RulesEnabled = true;
        var rule = new Rule
        {
            Name = "ready", Condition = S.Var("state", CompareOp.Equals, "ready"),
            Actions = { S.Set("fired", "1", VariableOp.Add) }, IntervalMs = 0, CooldownMs = 0
        };
        h.Automation.Data.Rules.Add(rule);
        var engine = new RuleEngine(h.Automation, h.Vision, h.Player, h.Variables, h.Settings, new NullLog());

        void TickAndSettle(DateTime t)
        {
            engine.Tick(t);
            var sw = Stopwatch.StartNew();
            while (h.Player.State != AppStatus.Idle && sw.ElapsedMilliseconds < 3000) Thread.Sleep(5);
            Thread.Sleep(20);
        }

        var t0 = DateTime.UtcNow;
        TickAndSettle(t0);                          // false
        h.Variables.Set("state", "ready");
        TickAndSettle(t0.AddSeconds(1));            // rising edge → fire
        TickAndSettle(t0.AddSeconds(2));            // still true → no fire (edge mode)
        h.Variables.Set("state", "busy");
        TickAndSettle(t0.AddSeconds(3));
        h.Variables.Set("state", "ready");
        TickAndSettle(t0.AddSeconds(4));            // fires again
        Assert.Equal("2", h.Variables.Get("fired"));
        Assert.Equal(2, engine.Statuses[rule.Id].FireCount);

        // Master switch off → nothing happens.
        h.Settings.Settings.RulesEnabled = false;
        h.Variables.Set("state", "x");
        TickAndSettle(t0.AddSeconds(5));
        h.Variables.Set("state", "ready");
        TickAndSettle(t0.AddSeconds(6));
        Assert.Equal("2", h.Variables.Get("fired"));
    }

    [Fact]
    public void Layout_apply_moves_matching_windows_and_reports_missing()
    {
        var h = new Harness();
        h.Platform.FakeWindows.Windows = new()
        {
            new WindowInfo(1, 10, "code", "proj - Visual Studio Code", 0, 0, 500, 500, false, false),
            new WindowInfo(2, 11, "chrome", "Docs - Chrome", 0, 0, 500, 500, false, false)
        };
        var layout = new WindowLayout
        {
            Name = "Dev", LaunchWaitSeconds = 1, Entries =
            {
                new WindowLayoutEntry { ProcessName = "code", X = 0, Y = 0, Width = 960, Height = 1080 },
                new WindowLayoutEntry { ProcessName = "chrome", X = 960, Y = 0, Width = 960, Height = 1080, State = WindowStateKind.Maximized },
                new WindowLayoutEntry { ProcessName = "slack", X = 0, Y = 0, Width = 400, Height = 400 }
            }
        };
        var problems = h.Layouts.Apply(layout);
        Assert.Contains("bounds code 0,0,960,1080", h.Platform.FakeWindows.Ops);
        Assert.Contains("bounds chrome 960,0,960,1080", h.Platform.FakeWindows.Ops);
        Assert.Contains("state chrome Maximized", h.Platform.FakeWindows.Ops);
        Assert.Single(problems, p => p.Contains("slack"));

        var captured = h.Layouts.CaptureCurrent("snap");
        Assert.Equal(2, captured.Entries.Count);
    }

    [Fact]
    public void Text_template_builtins()
    {
        Assert.Equal("a-1-b", TextTemplate.Expand("a-{x}-b", n => n == "x" ? "1" : null));
        Assert.Equal("{unknown}", TextTemplate.Expand("{unknown}", _ => null));
        var r = int.Parse(TextTemplate.Expand("{random:5-7}", _ => null));
        Assert.InRange(r, 5, 7);
        Assert.Equal(DateTime.Now.ToString("yyyy"), TextTemplate.Expand("{date:yyyy}", _ => null));
    }
}
