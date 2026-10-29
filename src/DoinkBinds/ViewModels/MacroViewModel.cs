using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.ViewModels;

/// <summary>
/// A macro in the library + its editor state (timeline, playback options, trigger hotkey).
/// The underlying <see cref="Macro"/> is always kept in sync so saving is just serializing it.
/// </summary>
public sealed partial class MacroViewModel : ViewModelBase
{
    private const int MaxUndo = 100;

    private readonly EditorContext _ctx;
    private ObservableCollection<ActionViewModel>? _steps;

    // Undo/redo store whole-timeline snapshots (JSON); small and impossible to get out of sync.
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string? _lastSnapshot;
    private DateTime _lastEditUtc;
    private ActionViewModel? _lastEditedStep;
    private bool _restoring;

    /// <summary>Internal clipboard (also mirrored to the system clipboard as JSON).</summary>
    private static List<MacroAction>? _stepClipboard;

    public Macro Model { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveStepUpCommand), nameof(MoveStepDownCommand),
        nameof(DeleteStepCommand), nameof(DuplicateStepCommand), nameof(PickStepPositionCommand), nameof(CopyStepsCommand))]
    private ActionViewModel? _selectedStep;

    /// <summary>All selected steps (multi-select with Ctrl/Shift). Set by the view.</summary>
    public IReadOnlyList<ActionViewModel> SelectedSteps { get; private set; } = Array.Empty<ActionViewModel>();

    [ObservableProperty] private bool _isCapturingTrigger;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _structureWarning;

    public MacroViewModel(Macro model, EditorContext ctx)
    {
        Model = model;
        _ctx = ctx;
    }

    // ------------------------------------------------------------------ identity

    public Guid Id => Model.Id;

    public string Name
    {
        get => Model.Name;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim();
            if (v == Model.Name) return;
            Model.Name = v;
            OnPropertyChanged();
            Changed();
        }
    }

    public string Description
    {
        get => Model.Description;
        set
        {
            if (value == Model.Description) return;
            Model.Description = value ?? "";
            OnPropertyChanged();
            Changed();
        }
    }

    public string Folder => Model.Folder;

    public string Summary => $"{Model.Actions.Count} {(Model.Actions.Count == 1 ? "step" : "steps")} · {FormatDuration(Model.TotalDuration)}";

    public string DurationText => FormatDuration(TimeSpan.FromMilliseconds(Model.TotalDuration.TotalMilliseconds / Model.Playback.Speed));

    public string StatsText => Model.RunCount == 0
        ? "Never played"
        : $"Played {Model.RunCount}× · last {Ago(Model.LastRunUtc)}";

    private static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var d = DateTime.UtcNow - utc.Value;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min ago" :
            d.TotalDays < 1 ? $"{d.TotalHours:0} h ago" : $"{utc.Value.ToLocalTime():MMM d}";
    }

    public void RecordRun()
    {
        Model.RunCount++;
        Model.LastRunUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(StatsText));
        _ctx.Library.MacroChanged(this);
    }

    public bool HasSteps => Model.Actions.Count > 0;

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.TotalMinutes >= 1 ? t.ToString(@"m\:ss") : $"{t.TotalSeconds:0.0}s";

    // ------------------------------------------------------------------ timeline

    /// <summary>Step view models are built lazily the first time the macro is opened.</summary>
    public ObservableCollection<ActionViewModel> Steps
    {
        get
        {
            if (_steps is null)
            {
                _steps = new ObservableCollection<ActionViewModel>(Model.Actions.Select((a, i) => CreateStep(a, i)));
                Reindex();
                _lastSnapshot ??= Snapshot();
            }
            return _steps;
        }
    }

    private ActionViewModel CreateStep(MacroAction a, int i)
    {
        var vm = new ActionViewModel(a, i, OnStepEdited, _ctx);
        vm.PositionModeChangeRequested += OnStepModeChange;
        return vm;
    }

    /// <summary>Replaces all steps (used after a recording finishes / guided steps).</summary>
    public void ReplaceActions(List<MacroAction> actions)
    {
        PushUndo();
        Model.Actions = actions;
        _steps = null;
        SelectedStep = null;
        OnPropertyChanged(nameof(Steps));
        _lastSnapshot = Snapshot();
        Reindex();
        Changed();
    }

    /// <summary>Appends steps at the end (guided recording).</summary>
    public void AppendActions(IEnumerable<MacroAction> actions)
    {
        PushUndo();
        foreach (var a in actions) Steps.Add(CreateStep(a, Steps.Count));
        SyncModelFromSteps(pushUndo: false);
    }

    public void RemoveLastSteps(int count)
    {
        if (count <= 0 || Steps.Count == 0) return;
        PushUndo();
        for (var i = 0; i < count && Steps.Count > 0; i++) Steps.RemoveAt(Steps.Count - 1);
        SyncModelFromSteps(pushUndo: false);
    }

    private void SyncModelFromSteps(bool pushUndo = true)
    {
        if (pushUndo) PushUndo();
        Model.Actions = Steps.Select(s => s.Model).ToList();
        _lastSnapshot = Snapshot();
        Reindex();
        Changed();
    }

    /// <summary>Updates numbers, If/Loop indentation and the structure warning.</summary>
    private void Reindex()
    {
        if (_steps is null) return;
        var depth = 0;
        for (var i = 0; i < _steps.Count; i++)
        {
            var s = _steps[i];
            s.Index = i;
            var t = s.Model.Type;
            if (t is MacroActionType.EndIf or MacroActionType.EndLoop or MacroActionType.Else) depth = Math.Max(0, depth - 1);
            s.Indent = depth;
            if (t is MacroActionType.If or MacroActionType.Loop or MacroActionType.Else) depth++;
        }
        try
        {
            MacroPlayerService.BlockMap.Build(Model.Actions);
            StructureWarning = null;
        }
        catch (PlaybackException ex)
        {
            StructureWarning = ex.Message;
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasSteps));
        _ctx.Library.MacroChanged(this);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A field of a step was edited: coalesce rapid edits of the same step into one undo entry.</summary>
    private void OnStepEdited(ActionViewModel step)
    {
        if (_restoring) return;
        var now = DateTime.UtcNow;
        if (!(ReferenceEquals(step, _lastEditedStep) && (now - _lastEditUtc).TotalMilliseconds < 1200))
            PushUndo(captureCurrent: false);
        _lastEditedStep = step;
        _lastEditUtc = now;
        _lastSnapshot = Snapshot();
        Reindex();
        Changed();
    }

    // ------------------------------------------------------------------ undo / redo

    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(Model.Actions, AppPaths.Json);

    /// <summary>Saves the state *before* a change. Structural edits snapshot the current state; field edits use the last snapshot.</summary>
    private void PushUndo(bool captureCurrent = true)
    {
        if (_restoring) return;
        var before = captureCurrent ? Snapshot() : _lastSnapshot ?? Snapshot();
        if (_undo.Count == 0 || _undo[^1] != before) _undo.Add(before);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        _lastEditedStep = null;
        if (captureCurrent) _lastSnapshot = null; // refreshed after the structural change completes
    }

    private bool CanUndo() => _undo.Count > 0;
    private bool CanRedo() => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Snapshot());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Changed();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Snapshot());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Changed();
    }

    private void Restore(string json)
    {
        _restoring = true;
        try
        {
            var selectedIndex = SelectedStep is { } s ? Steps.IndexOf(s) : -1;
            Model.Actions = System.Text.Json.JsonSerializer.Deserialize<List<MacroAction>>(json, AppPaths.Json) ?? new();
            _steps = null;
            OnPropertyChanged(nameof(Steps));
            Reindex();
            _lastSnapshot = json;
            _lastEditedStep = null;
            SelectedStep = selectedIndex >= 0 && selectedIndex < Steps.Count ? Steps[selectedIndex] : null;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Runs many step edits as one undo entry.</summary>
    private void Batch(Action edits)
    {
        PushUndo();
        _restoring = true;
        try { edits(); }
        finally { _restoring = false; }
        _lastSnapshot = Snapshot();
        Reindex();
        Changed();
    }

    // ------------------------------------------------------------------ selection helpers

    public void SetSelection(IEnumerable<ActionViewModel> steps)
    {
        SelectedSteps = steps.Where(Steps.Contains).OrderBy(s => s.Index).ToList();
        CopyStepsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The steps an action applies to: the multi-selection, else the single selection.</summary>
    private List<ActionViewModel> Targets()
    {
        if (SelectedSteps.Count > 1) return SelectedSteps.Where(Steps.Contains).OrderBy(s => s.Index).ToList();
        return SelectedStep is { } s ? new List<ActionViewModel> { s } : new List<ActionViewModel>();
    }

    private bool HasSelection() => SelectedStep is not null || SelectedSteps.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveStepUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveStepDown() => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        var first = Steps.IndexOf(targets[0]);
        var last = Steps.IndexOf(targets[^1]);
        if ((delta < 0 && first == 0) || (delta > 0 && last == Steps.Count - 1)) return;
        PushUndo();
        if (delta < 0)
            foreach (var t in targets) { var i = Steps.IndexOf(t); Steps.Move(i, i - 1); }
        else
            foreach (var t in Enumerable.Reverse(targets)) { var i = Steps.IndexOf(t); Steps.Move(i, i + 1); }
        SyncModelFromSteps(pushUndo: false);
        SelectedStep = targets[0];
    }

    /// <summary>Drag &amp; drop: moves the given steps so they start at <paramref name="targetIndex"/>.</summary>
    public void MoveSteps(IReadOnlyList<ActionViewModel> steps, int targetIndex)
    {
        var moving = steps.Where(Steps.Contains).OrderBy(s => s.Index).ToList();
        if (moving.Count == 0) return;
        // Index in the list *without* the moved items.
        var before = moving.Count(m => Steps.IndexOf(m) < targetIndex);
        PushUndo();
        foreach (var m in moving) Steps.Remove(m);
        var insertAt = Math.Clamp(targetIndex - before, 0, Steps.Count);
        for (var i = 0; i < moving.Count; i++) Steps.Insert(insertAt + i, moving[i]);
        SyncModelFromSteps(pushUndo: false);
        SelectedStep = moving[0];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteStep()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        var i = Steps.IndexOf(targets[0]);
        PushUndo();
        foreach (var t in targets) Steps.Remove(t);
        SyncModelFromSteps(pushUndo: false);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(i, 0, Steps.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateStep()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        var at = Steps.IndexOf(targets[^1]) + 1;
        PushUndo();
        ActionViewModel? firstCopy = null;
        foreach (var t in targets)
        {
            var copy = CreateStep(t.Model.Clone(), at);
            Steps.Insert(at++, copy);
            firstCopy ??= copy;
        }
        SyncModelFromSteps(pushUndo: false);
        SelectedStep = firstCopy;
    }

    // ------------------------------------------------------------------ clipboard

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopySteps()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        _stepClipboard = targets.Select(t => t.Model.Clone()).ToList();
        if (_ctx.Clipboard is { } cb)
        {
            try
            {
                await cb.SetTextAsync(System.Text.Json.JsonSerializer.Serialize(
                    new StepClipboardData { Steps = _stepClipboard }, AppPaths.Json));
            }
            catch { /* the internal clipboard still works */ }
        }
        _ctx.Library.Toast($"Copied {targets.Count} step{(targets.Count == 1 ? "" : "s")}");
    }

    [RelayCommand]
    private async Task CutSteps()
    {
        await CopySteps();
        DeleteStep();
    }

    [RelayCommand]
    private async Task PasteSteps()
    {
        List<MacroAction>? steps = null;
        if (_ctx.Clipboard is { } cb)
        {
            try
            {
                var text = await cb.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) && text.Contains("doink-steps"))
                    steps = System.Text.Json.JsonSerializer.Deserialize<StepClipboardData>(text, AppPaths.Json)?.Steps;
            }
            catch { /* fall back to the internal clipboard */ }
        }
        steps ??= _stepClipboard?.Select(s => s.Clone()).ToList();
        if (steps is not { Count: > 0 })
        {
            _ctx.Library.Toast("Nothing to paste — copy some steps first (Ctrl+C)");
            return;
        }
        InsertSteps(steps.Select(s => s.Clone()).ToList());
        _ctx.Library.Toast($"Pasted {steps.Count} step{(steps.Count == 1 ? "" : "s")}");
    }

    private sealed class StepClipboardData
    {
        public string Format { get; set; } = "doink-steps";
        public List<MacroAction> Steps { get; set; } = new();
    }

    /// <summary>Inserts steps after the selection (or at the end) and selects the first.</summary>
    public void InsertSteps(List<MacroAction> actions)
    {
        if (actions.Count == 0) return;
        var index = Targets() is { Count: > 0 } t ? Steps.IndexOf(t[^1]) + 1 : Steps.Count;
        PushUndo();
        ActionViewModel? first = null;
        foreach (var a in actions)
        {
            var vm = CreateStep(a, index);
            Steps.Insert(index++, vm);
            first ??= vm;
        }
        SyncModelFromSteps(pushUndo: false);
        SelectedStep = first;
    }

    // ------------------------------------------------------------------ adding steps

    /// <summary>
    /// Adds a new step after the selection (or at the end). Parameter = <see cref="MacroActionType"/> name.
    /// If / Loop automatically get their End If / End Loop, wrapping the selected steps when several are selected.
    /// </summary>
    [RelayCommand]
    private void AddStep(string? typeName)
    {
        if (!Enum.TryParse<MacroActionType>(typeName, out var type)) type = MacroActionType.Wait;
        var action = StepFactory.Create(type);
        if (action.IsMouseAction && action.Position is not null)
        {
            var cursor = _ctx.Positions.GetCursorPosition();
            action.Position = _ctx.Positions.FromAbsolute(cursor, _ctx.Settings.Settings.DefaultMouseMode is MousePositionMode.Relative
                or MousePositionMode.Window or MousePositionMode.Found
                ? MousePositionMode.Absolute : _ctx.Settings.Settings.DefaultMouseMode, cursor);
        }

        var closer = type switch
        {
            MacroActionType.If => MacroActionType.EndIf,
            MacroActionType.Loop => MacroActionType.EndLoop,
            _ => (MacroActionType?)null
        };

        var targets = Targets();
        if (closer is { } end && targets.Count > 1)
        {
            // Wrap the selection: If … [selected steps] … End If
            PushUndo();
            var start = Steps.IndexOf(targets[0]);
            var stop = Steps.IndexOf(targets[^1]);
            var open = CreateStep(action, start);
            Steps.Insert(start, open);
            Steps.Insert(stop + 2, CreateStep(StepFactory.Create(end), stop + 2));
            SyncModelFromSteps(pushUndo: false);
            SelectedStep = open;
            return;
        }

        var list = new List<MacroAction> { action };
        if (closer is { } c) list.Add(StepFactory.Create(c));
        InsertSteps(list);
    }

    [RelayCommand]
    private void AddTemplate(string? name)
    {
        var t = StepFactory.Templates.FirstOrDefault(x => x.Name == name);
        if (t is null) return;
        InsertSteps(t.Build());
        _ctx.Library.Toast($"Added template \"{t.Name}\" — check each step's settings on the right");
    }

    public IReadOnlyList<StepFactory.Template> Templates => StepFactory.Templates;

    [RelayCommand]
    private async Task ClearStepsAsync()
    {
        if (Steps.Count == 0) return;
        if (!await _ctx.Dialogs.ConfirmAsync("Clear all steps", $"Remove all {Steps.Count} steps from \"{Name}\"? (You can undo this.)", "Clear", danger: true))
            return;
        PushUndo();
        Steps.Clear();
        SelectedStep = null;
        SyncModelFromSteps(pushUndo: false);
    }

    /// <summary>Removes all MouseMove steps (keeps clicks/keys) and folds their delays into the next step.</summary>
    [RelayCommand]
    private void RemoveMouseMoves()
    {
        var kept = new List<MacroAction>();
        var carry = 0;
        foreach (var a in Model.Actions)
        {
            if (a.Type == MacroActionType.MouseMove && kept.Count > 0)
            {
                carry += a.DelayMs;
                continue;
            }
            a.DelayMs += carry;
            carry = 0;
            kept.Add(a);
        }
        var removed = Model.Actions.Count - kept.Count;
        ReplaceActions(kept);
        _ctx.Library.Toast(removed == 0 ? "No mouse moves to remove" : $"Removed {removed} mouse-move steps");
    }

    /// <summary>Scales every delay (e.g. 0.5 = twice as fast permanently).</summary>
    [RelayCommand]
    private async Task ScaleDelaysAsync()
    {
        var input = await _ctx.Dialogs.PromptAsync("Scale all delays",
            "Multiply every step delay by this factor (e.g. 0.5 halves all waits, 2 doubles them):", "1.0");
        if (input is null) return;
        if (!double.TryParse(input.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) || f <= 0 || f > 100)
        {
            _ctx.Library.Toast("Please enter a number greater than 0", ToastKind.Error);
            return;
        }
        Batch(() => { foreach (var s in Steps) s.DelayMs = (decimal)Math.Round(s.Model.DelayMs * f); });
    }

    /// <summary>Converts all mouse steps to one positioning mode.</summary>
    [RelayCommand]
    private void ConvertAllPositions(string? modeName)
    {
        if (!Enum.TryParse<MousePositionMode>(modeName, out var mode)) return;
        var previous = _ctx.Positions.GetCursorPosition();
        Batch(() =>
        {
            foreach (var s in Steps)
            {
                if (s.Model.Position is not { } pos) continue;
                var absolute = _ctx.Positions.Resolve(pos, previous);
                s.Model.Position = _ctx.Positions.FromAbsolute(absolute, mode, previous);
                previous = absolute;
                s.RaiseAll();
            }
        });
        _ctx.Library.Toast($"Converted mouse steps to {mode} positioning");
    }

    private void OnStepModeChange(ActionViewModel step, MousePositionMode mode)
    {
        if (step.Model.Position is not { } pos) return;
        var previous = AbsoluteBefore(Steps.IndexOf(step));
        step.Model.Position = _ctx.Positions.Convert(pos, mode, previous);
        step.RaiseAll();
    }

    /// <summary>
    /// Estimates where the cursor will be just before step <paramref name="index"/> by replaying
    /// positions from the current cursor. Needed to convert to/from Relative mode.
    /// </summary>
    private Models.DesktopPoint AbsoluteBefore(int index)
    {
        var p = _ctx.Positions.GetCursorPosition();
        for (var i = 0; i < index && i < Steps.Count; i++)
        {
            if (Steps[i].Model.Position is { } pos)
                p = _ctx.Positions.Resolve(pos, p);
        }
        return p;
    }

    /// <summary>Opens the crosshair overlay and stores the picked point in the selected step.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PickStepPositionAsync()
    {
        if (SelectedStep is not { } step) return;
        if (!step.Model.IsMouseAction)
        {
            _ctx.Library.Toast("Select a mouse step to pick a position", ToastKind.Error);
            return;
        }

        var picked = await _ctx.Positions.PickPointAsync($"Click to set the position for step {step.Index + 1}");
        if (picked is not { } point) return;

        var current = step.Model.Position?.Mode ?? MousePositionMode.PinPoint;
        // Absolute picks are stored as PinPoint so the timeline shows they were placed visually.
        var mode = current == MousePositionMode.Absolute ? MousePositionMode.PinPoint : current;
        step.Model.Position = _ctx.Positions.FromAbsolute(point, mode, AbsoluteBefore(step.Index));
        step.RaiseAll();
        _ctx.Library.Toast($"Position set to {point}", ToastKind.Success);
    }

    /// <summary>Adds a new click step at a visually picked point.</summary>
    [RelayCommand]
    private async Task AddPinPointClickAsync()
    {
        var picked = await _ctx.Positions.PickPointAsync("Click where the macro should click");
        if (picked is not { } point) return;

        var index = SelectedStep is { } sel ? Steps.IndexOf(sel) + 1 : Steps.Count;
        var action = new MacroAction
        {
            Type = MacroActionType.MouseClick,
            DelayMs = 100,
            Button = MacroMouseButton.Left,
            Position = _ctx.Positions.FromAbsolute(point, MousePositionMode.PinPoint, point)
        };
        var vm = CreateStep(action, index);
        Steps.Insert(index, vm);
        SyncModelFromSteps();
        SelectedStep = vm;
    }

    // ------------------------------------------------------------------ playback options

    public bool IsModeOnce
    {
        get => Model.Playback.Mode == PlaybackMode.Once;
        set { if (value) SetMode(PlaybackMode.Once); }
    }

    public bool IsModeLoopCount
    {
        get => Model.Playback.Mode == PlaybackMode.LoopCount;
        set { if (value) SetMode(PlaybackMode.LoopCount); }
    }

    public bool IsModeInfinite
    {
        get => Model.Playback.Mode == PlaybackMode.LoopInfinite;
        set { if (value) SetMode(PlaybackMode.LoopInfinite); }
    }

    public bool IsModeDuration
    {
        get => Model.Playback.Mode == PlaybackMode.LoopDuration;
        set { if (value) SetMode(PlaybackMode.LoopDuration); }
    }

    private void SetMode(PlaybackMode mode)
    {
        if (Model.Playback.Mode == mode) return;
        Model.Playback.Mode = mode;
        OnPropertyChanged(nameof(IsModeOnce));
        OnPropertyChanged(nameof(IsModeLoopCount));
        OnPropertyChanged(nameof(IsModeInfinite));
        OnPropertyChanged(nameof(IsModeDuration));
        Changed();
    }

    public decimal DurationMinutes
    {
        get => (decimal)Model.Playback.DurationMinutes;
        set
        {
            var v = (double)Math.Clamp(value, 0.1m, 100_000m);
            if (Math.Abs(v - Model.Playback.DurationMinutes) < 0.0001) return;
            Model.Playback.DurationMinutes = v;
            OnPropertyChanged();
            Changed();
        }
    }

    public decimal TimingJitterPercent
    {
        get => Model.Playback.TimingJitterPercent;
        set { var v = (int)Math.Clamp(value, 0, 90); if (v != Model.Playback.TimingJitterPercent) { Model.Playback.TimingJitterPercent = v; OnPropertyChanged(); Changed(); } }
    }

    public decimal MouseJitterPx
    {
        get => Model.Playback.MouseJitterPx;
        set { var v = (int)Math.Clamp(value, 0, 100); if (v != Model.Playback.MouseJitterPx) { Model.Playback.MouseJitterPx = v; OnPropertyChanged(); Changed(); } }
    }

    public bool SaveReport
    {
        get => Model.Playback.SaveReport;
        set { if (value != Model.Playback.SaveReport) { Model.Playback.SaveReport = value; OnPropertyChanged(); Changed(); } }
    }

    public decimal LoopCount
    {
        get => Model.Playback.LoopCount;
        set
        {
            var v = (int)Math.Clamp(value, 1, 1_000_000);
            if (v == Model.Playback.LoopCount) return;
            Model.Playback.LoopCount = v;
            OnPropertyChanged();
            Changed();
        }
    }

    public decimal LoopDelayMs
    {
        get => Model.Playback.LoopDelayMs;
        set
        {
            var v = (int)Math.Clamp(value, 0, 3_600_000);
            if (v == Model.Playback.LoopDelayMs) return;
            Model.Playback.LoopDelayMs = v;
            OnPropertyChanged();
            Changed();
        }
    }

    public double Speed
    {
        get => Model.Playback.Speed;
        set
        {
            // Snap to 0.05 steps so the slider produces tidy values.
            var v = Math.Round(Math.Clamp(value, 0.25, 3.0) * 20) / 20;
            if (Math.Abs(v - Model.Playback.Speed) < 0.001) return;
            Model.Playback.Speed = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedText));
            Changed();
        }
    }

    public string SpeedText => $"{Model.Playback.Speed:0.##}×";

    [RelayCommand]
    private void SetSpeed(string value)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            Speed = v;
    }

    public bool RestoreWindowFocus
    {
        get => Model.Playback.RestoreWindowFocus;
        set
        {
            if (value == Model.Playback.RestoreWindowFocus) return;
            Model.Playback.RestoreWindowFocus = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public bool WaitForWindow
    {
        get => Model.Playback.WaitForWindow;
        set
        {
            if (value == Model.Playback.WaitForWindow) return;
            Model.Playback.WaitForWindow = value;
            OnPropertyChanged();
            Changed();
        }
    }

    // ------------------------------------------------------------------ trigger hotkey

    public string TriggerHotkeyText => IsCapturingTrigger ? "Press keys… (Esc to cancel)" : Model.TriggerHotkey?.ToString() ?? "Not set";
    public bool HasTrigger => Model.TriggerHotkey is not null;

    partial void OnIsCapturingTriggerChanged(bool value) => OnPropertyChanged(nameof(TriggerHotkeyText));

    [RelayCommand]
    private async Task CaptureTriggerAsync()
    {
        IsCapturingTrigger = true;
        try
        {
            var hk = await _ctx.Hotkeys.CaptureAsync();
            if (hk is null) return;

            var conflict = _ctx.Hotkeys.FindConflict(hk, ignoreMacro: Id);
            if (conflict is not null)
            {
                _ctx.Library.Toast($"{hk} is already used by {conflict}", ToastKind.Error);
                return;
            }

            Model.TriggerHotkey = hk;
            Changed();
            _ctx.Library.RefreshTriggers();
            _ctx.Library.Toast($"\"{Name}\" will play when you press {hk}", ToastKind.Success);
        }
        finally
        {
            IsCapturingTrigger = false;
            OnPropertyChanged(nameof(HasTrigger));
        }
    }

    [RelayCommand]
    private void ClearTrigger()
    {
        Model.TriggerHotkey = null;
        OnPropertyChanged(nameof(TriggerHotkeyText));
        OnPropertyChanged(nameof(HasTrigger));
        Changed();
        _ctx.Library.RefreshTriggers();
    }

    // ------------------------------------------------------------------ playback highlight

    private ActionViewModel? _current;

    public void SetCurrentStep(int index)
    {
        if (_steps is null) return;
        if (_current is not null) _current.IsCurrent = false;
        _current = index >= 0 && index < _steps.Count ? _steps[index] : null;
        if (_current is not null) _current.IsCurrent = true;
    }

    // ------------------------------------------------------------------ library item commands (context menu)

    [RelayCommand] private Task Rename() => _ctx.Library.RenameMacroAsync(this);
    [RelayCommand] private Task Duplicate() => _ctx.Library.DuplicateMacroAsync(this);
    [RelayCommand] private Task Delete() => _ctx.Library.DeleteMacroAsync(this);
    [RelayCommand] private Task Move() => _ctx.Library.MoveMacroAsync(this);
    [RelayCommand] private Task Export() => _ctx.Library.ExportMacroAsync(this);
    [RelayCommand] private void Play() => _ctx.Library.PlayMacro(this);

    public void NotifyRenamed()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Folder));
    }
}
