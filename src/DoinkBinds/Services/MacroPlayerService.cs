using System.Diagnostics;
using System.Globalization;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.Services;

public sealed record PlaybackProgress(Guid MacroId, int Iteration, int TotalIterations, int StepIndex, int StepCount);

public sealed record PlaybackResult(Guid MacroId, bool Completed, bool Cancelled, string? Error, int IterationsCompleted,
    RunReport? Report = null);

/// <summary>
/// Plays macros: timing (speed, humanize), looping (once / N / forever / for a duration), pause/resume,
/// window focus, and the automation steps — conditions, If/Else, loops, variables, screen reading,
/// windows, notifications and test reports.
/// </summary>
public interface IMacroPlayerService
{
    AppStatus State { get; }
    Guid? CurrentMacroId { get; }

    event EventHandler<AppStatus>? StateChanged;
    event EventHandler<PlaybackProgress>? ProgressChanged;
    event EventHandler<PlaybackResult>? Finished;

    /// <summary>
    /// Starts playback on a background thread. Returns false if something is already playing.
    /// <paramref name="found"/> seeds the "last found" point (rules pass where their condition matched).
    /// </summary>
    bool Play(Macro macro, PlaybackSettings settings, DesktopPoint? found = null);

    void TogglePause();
    void Stop();
}

public sealed class MacroPlayerService : IMacroPlayerService
{
    private const int ClickHoldMs = 25;
    private const int ProgressIntervalMs = 33;
    private const int PollMs = 100;
    private const int MaxNesting = 10;
    private const int DefaultCharDelayMs = 12;

    private readonly IPlatformServices _platform;
    private readonly IMousePositionService _positions;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IScreenVision _vision;
    private readonly VariableStore _variables;
    private readonly IMacroLibraryService _library;
    private readonly ILayoutService _layouts;
    private readonly IAutomationStore _automation;
    private readonly IReportService _reports;
    private readonly INotificationService _notifier;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _running = new(true); // reset = paused
    private volatile AppStatus _state = AppStatus.Idle;

    public MacroPlayerService(IPlatformServices platform, IMousePositionService positions, ISettingsService settings,
        ILogService log, IScreenVision vision, VariableStore variables, IMacroLibraryService library,
        ILayoutService layouts, IAutomationStore automation, IReportService reports, INotificationService notifier)
    {
        _platform = platform;
        _positions = positions;
        _settings = settings;
        _log = log;
        _vision = vision;
        _variables = variables;
        _library = library;
        _layouts = layouts;
        _automation = automation;
        _reports = reports;
        _notifier = notifier;
    }

    public AppStatus State => _state;
    public Guid? CurrentMacroId { get; private set; }

    public event EventHandler<AppStatus>? StateChanged;
    public event EventHandler<PlaybackProgress>? ProgressChanged;
    public event EventHandler<PlaybackResult>? Finished;

    public bool Play(Macro macro, PlaybackSettings settings, DesktopPoint? found = null)
    {
        lock (_gate)
        {
            if (_state != AppStatus.Idle) return false;

            // Validate block structure up front so the user gets a clear message instead of a half-run.
            try
            {
                BlockMap.Build(macro.Actions);
            }
            catch (PlaybackException ex)
            {
                ThreadPool.QueueUserWorkItem(_ => Finished?.Invoke(this, new PlaybackResult(macro.Id, false, false, ex.Message, 0)));
                return true;
            }

            _cts = new CancellationTokenSource();
            _running.Set();
            CurrentMacroId = macro.Id;
            SetState(AppStatus.Playing);

            // Snapshot so edits in the UI during playback can't change the running sequence.
            var actions = macro.Actions.Select(a => a.Clone()).ToList();
            var opts = settings.Clone();
            opts.Speed = Math.Clamp(opts.Speed, 0.25, 3.0);
            var token = _cts.Token;
            var report = _reports.Begin(macro);

            var thread = new Thread(() => Run(macro, actions, opts, report, token, found))
            {
                IsBackground = true,
                Name = "DoinkBinds.Player",
                Priority = ThreadPriority.AboveNormal
            };
            thread.Start();
            return true;
        }
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            if (_state == AppStatus.Playing)
            {
                _running.Reset();
                SetState(AppStatus.Paused);
            }
            else if (_state == AppStatus.Paused)
            {
                _running.Set();
                SetState(AppStatus.Playing);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _running.Set(); // wake a paused player so it can exit
        }
    }

    private void SetState(AppStatus s)
    {
        _state = s;
        StateChanged?.Invoke(this, s);
    }

    // ================================================================== run state

    /// <summary>Everything that lives for one playback (shared by nested RunMacro calls).</summary>
    private sealed class RunContext
    {
        public required Macro Macro;
        public required PlaybackSettings Opts;
        public required CancellationToken Ct;
        public required RunReport Report;
        public readonly HeldInput Held = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly Random Rng = new();
        public double ScheduleMs;
        public DesktopPoint? Found;
        public WindowTarget? LastCheckedWindow;
        public DateTime? Deadline;
        public int Depth;
        public int TopStepIndex;
        public int TopStepCount;
        public int Iteration;
        public int TotalIterations;
        public long LastProgressMs = -ProgressIntervalMs;
        public int SpinGuard;
        public bool HasChecks;

        public string? Var(string name) => name.ToLowerInvariant() switch
        {
            "iteration" => Iteration.ToString(CultureInfo.InvariantCulture),
            "found.x" => Found is { } f ? ((int)f.X).ToString(CultureInfo.InvariantCulture) : null,
            "found.y" => Found is { } g ? ((int)g.Y).ToString(CultureInfo.InvariantCulture) : null,
            _ => null
        };
    }

    /// <summary>Ends playback early (StopMacro step / duration elapsed) without being an error.</summary>
    private sealed class EndRunException(bool failed, string? message) : Exception(message)
    {
        public bool Failed { get; } = failed;
    }

    // ================================================================== playback thread

    private void Run(Macro macro, List<MacroAction> actions, PlaybackSettings opts, RunReport report, CancellationToken ct,
        DesktopPoint? found)
    {
        var ctx = new RunContext { Macro = macro, Opts = opts, Ct = ct, Report = report, TopStepCount = actions.Count, Found = found };
        string? error = null;
        var cancelled = false;
        var failedByStep = false;

        using var precision = _platform.Input.BeginHighPrecisionTiming();
        try
        {
            ctx.TotalIterations = opts.Mode switch
            {
                PlaybackMode.Once => 1,
                PlaybackMode.LoopCount => Math.Max(1, opts.LoopCount),
                _ => 0 // infinite / duration
            };
            if (opts.Mode == PlaybackMode.LoopDuration)
                ctx.Deadline = DateTime.UtcNow.AddMinutes(Math.Max(0.005, opts.DurationMinutes));

            ctx.HasChecks = actions.Any(a => a.Type == MacroActionType.Check);

            while (ctx.TotalIterations == 0 || ctx.Iteration < ctx.TotalIterations)
            {
                ct.ThrowIfCancellationRequested();
                ctx.LastCheckedWindow = null;
                ctx.ScheduleMs = ctx.Clock.Elapsed.TotalMilliseconds;

                ExecuteSequence(actions, ctx, top: true);

                // Release anything the macro left pressed before the next loop starts.
                ctx.Held.ReleaseAll(_platform.Input);
                ctx.Iteration++;
                ctx.Report.Iterations = ctx.Iteration;

                if (ctx.Deadline is { } d && DateTime.UtcNow >= d) break;
                if ((ctx.TotalIterations == 0 || ctx.Iteration < ctx.TotalIterations) && opts.LoopDelayMs > 0)
                    WaitUntil(ctx, ctx.Clock.Elapsed.TotalMilliseconds + opts.LoopDelayMs / opts.Speed);
            }
        }
        catch (EndRunException end)
        {
            failedByStep = end.Failed;
            if (end.Failed) error = end.Message ?? "The macro stopped itself with a failure.";
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex is PlaybackException ? ex.Message : $"Playback failed: {ex.Message}";
            _log.Error("Playback failed", ex);
        }
        finally
        {
            // Never leave keys or mouse buttons stuck down, whatever happened.
            try { ctx.Held.ReleaseAll(_platform.Input); } catch (Exception ex) { _log.Error("Release failed", ex); }

            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
                CurrentMacroId = null;
                _running.Set();
                SetState(AppStatus.Idle);
            }

            // Report: written when asked for, when the macro has checks, or when something failed.
            var anyFailedCheck = report.Entries.Any(e => !e.Passed);
            report.FinishedUtc = DateTime.UtcNow;
            report.Error = error;
            report.Result = error is not null || failedByStep ? "Failed"
                : cancelled ? "Stopped"
                : anyFailedCheck ? "Failed" : "Passed";
            var saveReport = opts.SaveReport || ctx.HasChecks || error is not null || report.Entries.Count > 0;
            if (saveReport) _reports.Save(report);

            Finished?.Invoke(this, new PlaybackResult(macro.Id, error is null && !cancelled && !failedByStep, cancelled, error,
                ctx.Iteration, saveReport ? report : null));
        }
    }

    // ================================================================== sequence + flow control

    private void ExecuteSequence(List<MacroAction> actions, RunContext ctx, bool top)
    {
        var blocks = BlockMap.Build(actions);
        var loopCounters = new Dictionary<int, int>();
        var pc = 0;

        while (pc < actions.Count)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            if (ctx.Deadline is { } deadline && DateTime.UtcNow >= deadline)
                throw new EndRunException(false, null);

            var a = actions[pc];
            if (top) ctx.TopStepIndex = pc;

            // ---- timing (applies to every step, including loop markers each time round) ----
            var delay = a.Type == MacroActionType.RandomWait
                ? ctx.Rng.Next(Math.Min(a.DelayMs, a.MaxDelayMs), Math.Max(a.DelayMs, a.MaxDelayMs) + 1)
                : a.DelayMs;
            if (ctx.Opts.TimingJitterPercent > 0 && delay > 0)
            {
                var j = ctx.Opts.TimingJitterPercent / 100.0;
                delay = (int)Math.Max(0, delay * (1 + (ctx.Rng.NextDouble() * 2 - 1) * j));
            }
            if (delay > 0) ctx.SpinGuard = 0;
            else if (++ctx.SpinGuard % 500 == 0) Thread.Sleep(1); // don't peg a CPU core on empty loops
            ctx.ScheduleMs += delay / ctx.Opts.Speed;
            ctx.ScheduleMs = WaitUntil(ctx, ctx.ScheduleMs);

            ReportProgress(ctx);

            switch (a.Type)
            {
                case MacroActionType.If:
                {
                    var r = EvaluateTimed(a, ctx);
                    if (r.Met)
                    {
                        if (r.Found is { } f) ctx.Found = f;
                        pc++;
                    }
                    else
                    {
                        pc = blocks.ElseOf.TryGetValue(pc, out var e) ? e + 1 : blocks.EndOf[pc] + 1;
                    }
                    continue;
                }
                case MacroActionType.Else:
                    pc = blocks.EndOf[pc] + 1; // then-branch finished: skip the else-branch
                    continue;
                case MacroActionType.EndIf:
                case MacroActionType.Comment:
                    pc++;
                    continue;
                case MacroActionType.Loop:
                {
                    loopCounters.TryGetValue(pc, out var count);
                    bool again;
                    switch (a.LoopKind)
                    {
                        case LoopKind.Count: again = count < Math.Max(0, a.Count); break;
                        case LoopKind.While: again = EvaluateTimed(a, ctx).Met; break;
                        case LoopKind.Until: again = !EvaluateTimed(a, ctx).Met; break;
                        default: again = true; break;
                    }
                    if (again)
                    {
                        loopCounters[pc] = count + 1;
                        _variables.Set("loop", (count + 1).ToString(CultureInfo.InvariantCulture));
                        pc++;
                    }
                    else
                    {
                        loopCounters.Remove(pc);
                        pc = blocks.EndOf[pc] + 1;
                    }
                    continue;
                }
                case MacroActionType.EndLoop:
                    pc = blocks.StartOf[pc];
                    continue;
                case MacroActionType.Break:
                {
                    var loop = blocks.LoopOfBreak[pc];
                    loopCounters.Remove(loop);
                    pc = blocks.EndOf[loop] + 1;
                    continue;
                }
                case MacroActionType.StopMacro:
                    throw new EndRunException(a.Flag, string.IsNullOrWhiteSpace(a.Text) ? null : Expand(a.Text, ctx));
                default:
                    EnsureWindowForInput(a, ctx);
                    Execute(a, ctx, pc);
                    pc++;
                    continue;
            }
        }
    }

    private void ReportProgress(RunContext ctx)
    {
        var now = ctx.Clock.ElapsedMilliseconds;
        if (now - ctx.LastProgressMs < ProgressIntervalMs && ctx.TopStepIndex != ctx.TopStepCount - 1) return;
        ctx.LastProgressMs = now;
        ProgressChanged?.Invoke(this, new PlaybackProgress(ctx.Macro.Id, ctx.Iteration + 1, ctx.TotalIterations,
            ctx.TopStepIndex, ctx.TopStepCount));
    }

    /// <summary>Matching If/Else/EndIf and Loop/EndLoop/Break indices for a step list.</summary>
    internal sealed class BlockMap
    {
        public readonly Dictionary<int, int> EndOf = new();    // If/Else/Loop -> EndIf/EndLoop
        public readonly Dictionary<int, int> ElseOf = new();   // If -> Else
        public readonly Dictionary<int, int> StartOf = new();  // EndLoop -> Loop
        public readonly Dictionary<int, int> LoopOfBreak = new();

        public static BlockMap Build(IReadOnlyList<MacroAction> actions)
        {
            var map = new BlockMap();
            var stack = new Stack<(int Index, MacroActionType Type)>();
            for (var i = 0; i < actions.Count; i++)
            {
                switch (actions[i].Type)
                {
                    case MacroActionType.If:
                    case MacroActionType.Loop:
                        stack.Push((i, actions[i].Type));
                        break;
                    case MacroActionType.Else:
                        if (stack.Count == 0 || stack.Peek().Type != MacroActionType.If || map.ElseOf.ContainsKey(stack.Peek().Index))
                            throw new PlaybackException($"Step {i + 1}: \"Else\" has no matching \"If\".");
                        map.ElseOf[stack.Peek().Index] = i;
                        break;
                    case MacroActionType.EndIf:
                    {
                        if (stack.Count == 0 || stack.Peek().Type != MacroActionType.If)
                            throw new PlaybackException($"Step {i + 1}: \"End If\" has no matching \"If\".");
                        var open = stack.Pop().Index;
                        map.EndOf[open] = i;
                        if (map.ElseOf.TryGetValue(open, out var e)) map.EndOf[e] = i;
                        break;
                    }
                    case MacroActionType.EndLoop:
                    {
                        if (stack.Count == 0 || stack.Peek().Type != MacroActionType.Loop)
                            throw new PlaybackException($"Step {i + 1}: \"End Loop\" has no matching \"Loop\".");
                        var open = stack.Pop().Index;
                        map.EndOf[open] = i;
                        map.StartOf[i] = open;
                        break;
                    }
                    case MacroActionType.Break:
                    {
                        var loop = stack.FirstOrDefault(s => s.Type == MacroActionType.Loop);
                        if (loop.Type != MacroActionType.Loop)
                            throw new PlaybackException($"Step {i + 1}: \"Break\" must be inside a loop.");
                        map.LoopOfBreak[i] = loop.Index;
                        break;
                    }
                }
            }
            if (stack.Count > 0)
            {
                var (index, type) = stack.Peek();
                throw new PlaybackException($"Step {index + 1}: \"{(type == MacroActionType.If ? "If" : "Loop")}\" is missing its \"{(type == MacroActionType.If ? "End If" : "End Loop")}\".");
            }
            return map;
        }
    }

    // ================================================================== timing helpers

    /// <summary>
    /// High-precision wait until <paramref name="targetMs"/>. Sleeps coarsely, then spins for the last
    /// ~1.5 ms. Handles pause by shifting the schedule. Returns the (possibly shifted) target.
    /// </summary>
    private double WaitUntil(RunContext ctx, double targetMs)
    {
        var ct = ctx.Ct;
        var clock = ctx.Clock;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!_running.IsSet)
            {
                var pausedAt = clock.Elapsed.TotalMilliseconds;
                _running.Wait(ct);
                targetMs += clock.Elapsed.TotalMilliseconds - pausedAt;
                continue;
            }

            var remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return targetMs;

            if (remaining > 20)
                ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(remaining - 10, 50))); // wakes instantly on Stop
            else if (remaining > 1.5)
                Thread.Sleep(1);
            else
                Thread.SpinWait(40);
        }
    }

    /// <summary>Sleeps for real time (pausable, cancellable) and moves the schedule along so later steps don't rush.</summary>
    private void Sleep(RunContext ctx, int ms)
    {
        var target = ctx.Clock.Elapsed.TotalMilliseconds + ms;
        WaitUntil(ctx, target);
        ctx.ScheduleMs = Math.Max(ctx.ScheduleMs, ctx.Clock.Elapsed.TotalMilliseconds);
    }

    // ================================================================== window focus

    private void EnsureWindowForInput(MacroAction a, RunContext ctx)
    {
        var opts = ctx.Opts;
        var isInput = a.IsMouseAction || a.IsKeyAction || a.Type == MacroActionType.TypeText;
        if (!isInput || a.Type == MacroActionType.MouseMove) return;
        if (a.Window is not { IsEmpty: false } w || !(opts.RestoreWindowFocus || opts.WaitForWindow)) return;
        if (a.Position?.Mode == MousePositionMode.Window) return; // window-relative steps resolve the window themselves
        if (w.SameAs(ctx.LastCheckedWindow)) return;
        ctx.LastCheckedWindow = w;
        var before = ctx.Clock.Elapsed.TotalMilliseconds;
        EnsureWindow(w, ctx);
        ctx.ScheduleMs += ctx.Clock.Elapsed.TotalMilliseconds - before;
    }

    private void EnsureWindow(WindowTarget target, RunContext ctx)
    {
        bool IsTargetFocused()
        {
            var fg = _platform.Windows.GetForegroundWindow();
            return fg is not null && string.Equals(fg.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase);
        }

        if (IsTargetFocused()) return;

        if (ctx.Opts.RestoreWindowFocus)
        {
            _platform.Windows.TryActivate(target);
            ctx.Ct.WaitHandle.WaitOne(150); // give the window manager a moment
        }

        if (!ctx.Opts.WaitForWindow) return;

        var timeout = _settings.Settings.WaitForWindowTimeoutMs;
        var sw = Stopwatch.StartNew();
        while (!IsTargetFocused())
        {
            ctx.Ct.ThrowIfCancellationRequested();
            _running.Wait(ctx.Ct);
            if (timeout > 0 && sw.ElapsedMilliseconds > timeout)
                throw new PlaybackException($"Timed out after {timeout / 1000.0:0.#}s waiting for \"{target}\" to be focused.");
            if (ctx.Opts.RestoreWindowFocus && sw.ElapsedMilliseconds % 2000 < 100)
                _platform.Windows.TryActivate(target);
            ctx.Ct.WaitHandle.WaitOne(PollMs);
        }
    }

    // ================================================================== conditions

    private VisionContext Vision(RunContext ctx, MacroAction a) => new()
    {
        GetVariable = n => ctx.Var(n) ?? _variables.Get(n),
        DefaultWindow = a.Window
    };

    private ConditionResult Evaluate(MacroAction a, RunContext ctx) =>
        a.Condition is null ? new ConditionResult(false, Detail: "no condition set") : _vision.Evaluate(a.Condition, Vision(ctx, a));

    /// <summary>Evaluates and keeps the schedule honest (screen reading can take a while).</summary>
    private ConditionResult EvaluateTimed(MacroAction a, RunContext ctx)
    {
        var r = Evaluate(a, ctx);
        ctx.ScheduleMs = Math.Max(ctx.ScheduleMs, ctx.Clock.Elapsed.TotalMilliseconds);
        return r;
    }

    /// <summary>Polls a condition until true or timeout. Retry = never time out.</summary>
    private ConditionResult WaitForCondition(MacroAction a, RunContext ctx, int defaultTimeoutMs)
    {
        var timeout = a.TimeoutMs > 0 ? a.TimeoutMs : defaultTimeoutMs;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            _running.Wait(ctx.Ct);
            var r = Evaluate(a, ctx);
            if (r.Met)
            {
                ctx.ScheduleMs = Math.Max(ctx.ScheduleMs, ctx.Clock.Elapsed.TotalMilliseconds);
                return r;
            }
            if (a.OnFail != FailAction.Retry && timeout > 0 && sw.ElapsedMilliseconds >= timeout)
            {
                ctx.ScheduleMs = Math.Max(ctx.ScheduleMs, ctx.Clock.Elapsed.TotalMilliseconds);
                return r;
            }
            ctx.Ct.WaitHandle.WaitOne(PollMs);
        }
    }

    /// <summary>Applies the step's OnFail policy: records the failure (with a screenshot) and stops or continues.</summary>
    private void Fail(MacroAction a, RunContext ctx, int index, string message)
    {
        var shot = _vision.Capture(null, null);
        var file = shot is null ? null : _reports.AddScreenshot(ctx.Report, shot, $"step{index + 1}-failed");
        ctx.Report.Entries.Add(new ReportEntry
        {
            StepNumber = index + 1,
            Description = Describe(a),
            Passed = false,
            Message = message,
            Screenshot = file
        });
        if (a.OnFail == FailAction.Stop)
            throw new PlaybackException($"Step {index + 1} ({Describe(a)}): {message}");
    }

    private static string Describe(MacroAction a) =>
        !string.IsNullOrWhiteSpace(a.Note) ? a.Note! :
        a.Condition is not null ? $"{a.Type}: {a.Condition.Describe()}" : a.Type.ToString();

    // ================================================================== step execution

    private string Expand(string? text, RunContext ctx) => TextTemplate.Expand(text, n => ctx.Var(n) ?? _variables.Get(n));

    private PositionContext PositionContextFor(MacroAction a, RunContext ctx)
    {
        var origin = a.Position?.Mode == MousePositionMode.Window ? _vision.WindowOrigin(a.Window) : null;
        if (a.Position?.Mode == MousePositionMode.Window && origin is null)
            throw new PlaybackException($"Window \"{a.Window}\" is not open (needed for a window-relative position).");
        return new PositionContext(origin, ctx.Found);
    }

    private void MoveTo(MacroAction a, RunContext ctx)
    {
        if (a.Position is not { } pos) return;
        var input = _platform.Input;
        var target = _positions.Resolve(pos, input.GetCursorPosition(), PositionContextFor(a, ctx));
        target = Jitter(target, pos.Mode, ctx);
        input.MoveMouse(target.X, target.Y);
    }

    private static DesktopPoint Jitter(DesktopPoint p, MousePositionMode mode, RunContext ctx)
    {
        var j = ctx.Opts.MouseJitterPx;
        if (j <= 0 || mode == MousePositionMode.Relative) return p;
        return new DesktopPoint(p.X + ctx.Rng.Next(-j, j + 1), p.Y + ctx.Rng.Next(-j, j + 1));
    }

    private void Execute(MacroAction a, RunContext ctx, int index)
    {
        var input = _platform.Input;
        var held = ctx.Held;
        var ct = ctx.Ct;

        switch (a.Type)
        {
            // ---------------- recorded input ----------------
            case MacroActionType.MouseMove:
                MoveTo(a, ctx);
                break;

            case MacroActionType.MouseDown:
                MoveTo(a, ctx);
                input.MouseButton(a.Button, true);
                held.Buttons.Add(a.Button);
                break;

            case MacroActionType.MouseUp:
                MoveTo(a, ctx);
                input.MouseButton(a.Button, false);
                held.Buttons.Remove(a.Button);
                break;

            case MacroActionType.MouseClick:
                MoveTo(a, ctx);
                Click(a.Button, Math.Max(1, a.Count), ct);
                break;

            case MacroActionType.MouseWheel:
                MoveTo(a, ctx);
                input.MouseWheel(a.WheelDelta, a.Horizontal);
                break;

            case MacroActionType.KeyDown:
                input.Key(a.KeyCode, true);
                held.Keys.Add(a.KeyCode);
                break;

            case MacroActionType.KeyUp:
                input.Key(a.KeyCode, false);
                held.Keys.Remove(a.KeyCode);
                break;

            case MacroActionType.KeyPress:
                input.Key(a.KeyCode, true);
                ct.WaitHandle.WaitOne(ClickHoldMs);
                input.Key(a.KeyCode, false);
                break;

            case MacroActionType.Wait:
            case MacroActionType.RandomWait:
                break; // the delay already happened

            // ---------------- text ----------------
            case MacroActionType.TypeText:
            {
                var text = Expand(a.Text, ctx);
                var perChar = a.Count > 0 ? a.Count : DefaultCharDelayMs;
                var e = StringInfo.GetTextElementEnumerator(text);
                while (e.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    _running.Wait(ct);
                    input.TypeText(e.GetTextElement());
                    if (perChar > 0) Sleep(ctx, (int)Math.Max(1, perChar / ctx.Opts.Speed));
                }
                break;
            }

            // ---------------- screen reading ----------------
            case MacroActionType.WaitFor:
            {
                var r = WaitForCondition(a, ctx, defaultTimeoutMs: 0);
                if (r.Met)
                {
                    if (r.Found is { } f) ctx.Found = f;
                }
                else Fail(a, ctx, index, $"timed out after {(a.TimeoutMs / 1000.0):0.#}s — {r.Detail ?? "condition not met"}");
                break;
            }

            case MacroActionType.FindAndClick:
            {
                var r = WaitForCondition(a, ctx, defaultTimeoutMs: 0);
                if (!r.Met || r.Found is null)
                {
                    Fail(a, ctx, index, r.Met ? "nothing to click (the condition doesn't locate anything)" : $"not found — {r.Detail}");
                    break;
                }
                ctx.Found = r.Found;
                var p = Jitter(new DesktopPoint(r.Found.Value.X + a.OffsetX, r.Found.Value.Y + a.OffsetY), MousePositionMode.Absolute, ctx);
                input.MoveMouse(p.X, p.Y);
                ct.WaitHandle.WaitOne(30);
                Click(a.Button, Math.Max(1, a.Count), ct);
                break;
            }

            case MacroActionType.Check:
            {
                var r = Evaluate(a, ctx);
                ctx.ScheduleMs = Math.Max(ctx.ScheduleMs, ctx.Clock.Elapsed.TotalMilliseconds);
                if (r.Met)
                {
                    if (r.Found is { } f) ctx.Found = f;
                    ctx.Report.Entries.Add(new ReportEntry
                    {
                        StepNumber = index + 1, Description = Describe(a), Passed = true, Message = r.Detail
                    });
                }
                else Fail(a, ctx, index, r.Detail ?? "check failed");
                break;
            }

            case MacroActionType.ReadText:
            {
                var text = _vision.ReadText(a.Region, a.Window, Vision(ctx, a)).Trim();
                var name = string.IsNullOrWhiteSpace(a.VariableName) ? "text" : VariableStore.Normalize(a.VariableName);
                _variables.Set(name, text);
                ctx.ScheduleMs = Math.Max(ctx.ScheduleMs, ctx.Clock.Elapsed.TotalMilliseconds);
                break;
            }

            case MacroActionType.Screenshot:
            {
                var img = _vision.Capture(a.Region, a.Window, Vision(ctx, a));
                var caption = string.IsNullOrWhiteSpace(a.Text) ? "screenshot" : Expand(a.Text, ctx);
                var file = img is null ? null : _reports.AddScreenshot(ctx.Report, img, caption);
                ctx.Report.Entries.Add(new ReportEntry
                {
                    StepNumber = index + 1, Description = caption, Passed = img is not null,
                    Message = img is null ? "screen capture not available" : null, Screenshot = file
                });
                break;
            }

            // ---------------- variables ----------------
            case MacroActionType.SetVariable:
            {
                var name = VariableStore.Normalize(a.VariableName ?? "");
                if (name.Length == 0) throw new PlaybackException($"Step {index + 1}: the variable has no name.");
                var value = Expand(a.Text, ctx);
                var current = _variables.Get(name) ?? "";
                switch (a.VariableOp)
                {
                    case VariableOp.Add:
                    case VariableOp.Subtract:
                    {
                        VariableStore.TryNumber(current, out var c);
                        if (!VariableStore.TryNumber(value, out var v)) v = 1;
                        _variables.Set(name, VariableStore.FormatNumber(a.VariableOp == VariableOp.Add ? c + v : c - v));
                        break;
                    }
                    case VariableOp.Append:
                        _variables.Set(name, current + value);
                        break;
                    default:
                        _variables.Set(name, value);
                        break;
                }
                break;
            }

            // ---------------- macros / apps / windows ----------------
            case MacroActionType.RunMacro:
            {
                var target = (a.TargetMacroId is { } id ? _library.Macros.FirstOrDefault(m => m.Id == id) : null)
                             ?? _library.Macros.FirstOrDefault(m => string.Equals(m.Name, a.TargetMacroName, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    throw new PlaybackException($"Step {index + 1}: macro \"{a.TargetMacroName ?? "?"}\" was not found.");
                if (ctx.Depth >= MaxNesting)
                    throw new PlaybackException($"Step {index + 1}: macros call each other too deeply (more than {MaxNesting} levels).");
                ctx.Depth++;
                try
                {
                    ExecuteSequence(target.Actions.Select(x => x.Clone()).ToList(), ctx, top: false);
                }
                finally
                {
                    ctx.Depth--;
                }
                break;
            }

            case MacroActionType.OpenApp:
            {
                var cmd = Expand(a.Text, ctx);
                if (string.IsNullOrWhiteSpace(cmd)) throw new PlaybackException($"Step {index + 1}: nothing to open.");
                var err = _layouts.Launch(cmd, Expand(a.Arguments, ctx));
                if (err is not null) { Fail(a, ctx, index, $"could not open \"{cmd}\": {err}"); break; }
                if (a.Window is { IsEmpty: false } w && a.TimeoutMs > 0)
                {
                    var sw = Stopwatch.StartNew();
                    while (FindWindow(w) is null)
                    {
                        if (sw.ElapsedMilliseconds > a.TimeoutMs) { Fail(a, ctx, index, $"\"{w}\" did not open"); break; }
                        Sleep(ctx, PollMs * 2);
                    }
                }
                break;
            }

            case MacroActionType.WindowAction:
                ExecuteWindowAction(a, ctx, index);
                break;

            // ---------------- feedback ----------------
            case MacroActionType.Notify:
                _notifier.Notify(ctx.Macro.Name, Expand(a.Text, ctx), ToastKind.Info);
                if (a.Flag) _platform.Beep();
                break;
        }
    }

    private void Click(MacroMouseButton button, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            _platform.Input.MouseButton(button, true);
            ct.WaitHandle.WaitOne(ClickHoldMs);
            _platform.Input.MouseButton(button, false);
            if (i < count - 1) ct.WaitHandle.WaitOne(60);
        }
    }

    private WindowInfo? FindWindow(WindowTarget target) =>
        _platform.Windows.ListWindows().FirstOrDefault(w =>
            (string.IsNullOrWhiteSpace(target.ProcessName) || string.Equals(w.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(target.Title) || w.Title.Contains(target.Title, StringComparison.OrdinalIgnoreCase)));

    private void ExecuteWindowAction(MacroAction a, RunContext ctx, int index)
    {
        if (a.WindowOp == WindowOp.ApplyLayout)
        {
            var layout = _automation.Data.Layouts.FirstOrDefault(l => l.Id == a.LayoutId);
            if (layout is null) { Fail(a, ctx, index, "the window layout no longer exists"); return; }
            var problems = _layouts.Apply(layout, ctx.Ct);
            if (problems.Count > 0) Fail(a, ctx, index, string.Join("; ", problems));
            return;
        }

        if (a.Window is not { IsEmpty: false } target) { Fail(a, ctx, index, "no window chosen"); return; }

        var timeout = a.TimeoutMs > 0 ? a.TimeoutMs : a.WindowOp == WindowOp.WaitForWindow ? 0 : 3000;
        var sw = Stopwatch.StartNew();
        WindowInfo? w;
        while ((w = FindWindow(target)) is null)
        {
            if ((a.OnFail != FailAction.Retry && timeout > 0 && sw.ElapsedMilliseconds >= timeout))
            {
                Fail(a, ctx, index, $"window \"{target}\" not found");
                return;
            }
            Sleep(ctx, PollMs * 2);
        }

        var ok = a.WindowOp switch
        {
            WindowOp.Focus => _platform.Windows.Focus(w),
            WindowOp.Maximize => _platform.Windows.SetState(w, WindowStateKind.Maximized),
            WindowOp.Minimize => _platform.Windows.SetState(w, WindowStateKind.Minimized),
            WindowOp.Restore => _platform.Windows.SetState(w, WindowStateKind.Normal),
            WindowOp.Close => _platform.Windows.Close(w),
            WindowOp.MoveResize when a.Region is { IsEmpty: false } r => _platform.Windows.SetBounds(w, r.X, r.Y, r.Width, r.Height),
            WindowOp.WaitForWindow => true,
            _ => false
        };
        if (!ok) Fail(a, ctx, index, $"could not {a.WindowOp} \"{target}\"" + (OperatingSystem.IsMacOS() ? " (Accessibility permission?)" : ""));
        else if (a.WindowOp == WindowOp.Focus) ctx.Ct.WaitHandle.WaitOne(150);
    }

    /// <summary>Tracks what the macro currently holds down so it can always be released.</summary>
    private sealed class HeldInput
    {
        public readonly HashSet<int> Keys = new();
        public readonly HashSet<MacroMouseButton> Buttons = new();

        public void ReleaseAll(IInputSimulator input)
        {
            foreach (var k in Keys.ToList()) input.Key(k, false);
            foreach (var b in Buttons.ToList()) input.MouseButton(b, false);
            Keys.Clear();
            Buttons.Clear();
        }
    }
}

public sealed class PlaybackException(string message) : Exception(message);
