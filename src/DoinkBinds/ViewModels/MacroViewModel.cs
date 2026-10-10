using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Services;

namespace DoinkBinds.ViewModels;

/// <summary>
/// A macro in the library + its editor state (timeline, playback options, trigger hotkey).
/// The underlying <see cref="Macro"/> is always kept in sync so saving is just serializing it.
/// </summary>
public sealed partial class MacroViewModel : ViewModelBase
{
    private readonly EditorContext _ctx;
    private ObservableCollection<ActionViewModel>? _steps;

    public Macro Model { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveStepUpCommand), nameof(MoveStepDownCommand),
        nameof(DeleteStepCommand), nameof(DuplicateStepCommand), nameof(PickStepPositionCommand))]
    private ActionViewModel? _selectedStep;

    [ObservableProperty] private bool _isCapturingTrigger;
    [ObservableProperty] private bool _isPlaying;

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
            }
            return _steps;
        }
    }

    private ActionViewModel CreateStep(MacroAction a, int i)
    {
        var vm = new ActionViewModel(a, i, _ => Changed());
        vm.PositionModeChangeRequested += OnStepModeChange;
        return vm;
    }

    /// <summary>Replaces all steps (used after a recording finishes).</summary>
    public void ReplaceActions(List<MacroAction> actions)
    {
        Model.Actions = actions;
        _steps = null;
        SelectedStep = null;
        OnPropertyChanged(nameof(Steps));
        Changed();
    }

    private void SyncModelFromSteps()
    {
        Model.Actions = Steps.Select(s => s.Model).ToList();
        for (var i = 0; i < Steps.Count; i++) Steps[i].Index = i;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasSteps));
        _ctx.Library.MacroChanged(this);
    }

    private bool HasSelection() => SelectedStep is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveStepUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveStepDown() => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (SelectedStep is not { } step) return;
        var i = Steps.IndexOf(step);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Steps.Count) return;
        Steps.Move(i, j);
        SyncModelFromSteps();
        SelectedStep = step;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteStep()
    {
        if (SelectedStep is not { } step) return;
        var i = Steps.IndexOf(step);
        Steps.Remove(step);
        SyncModelFromSteps();
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(i, 0, Steps.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateStep()
    {
        if (SelectedStep is not { } step) return;
        var i = Steps.IndexOf(step);
        var copy = CreateStep(step.Model.Clone(), i + 1);
        Steps.Insert(i + 1, copy);
        SyncModelFromSteps();
        SelectedStep = copy;
    }

    /// <summary>Adds a new step after the selection (or at the end). Parameter = <see cref="MacroActionType"/> name.</summary>
    [RelayCommand]
    private void AddStep(string? typeName)
    {
        if (!Enum.TryParse<MacroActionType>(typeName, out var type)) type = MacroActionType.Wait;

        var action = new MacroAction { Type = type, DelayMs = type == MacroActionType.Wait ? 500 : 100 };
        if (action.IsMouseAction)
        {
            var cursor = _ctx.Positions.GetCursorPosition();
            action.Position = _ctx.Positions.FromAbsolute(cursor, _ctx.Settings.Settings.DefaultMouseMode == MousePositionMode.Relative
                ? MousePositionMode.Absolute : _ctx.Settings.Settings.DefaultMouseMode, cursor);
        }
        if (type == MacroActionType.MouseWheel) action.WheelDelta = 120;
        if (action.IsKeyAction) action.KeyCode = KeyCodes.Space;

        var index = SelectedStep is { } sel ? Steps.IndexOf(sel) + 1 : Steps.Count;
        var vm = CreateStep(action, index);
        Steps.Insert(index, vm);
        SyncModelFromSteps();
        SelectedStep = vm;
    }

    [RelayCommand]
    private async Task ClearStepsAsync()
    {
        if (Steps.Count == 0) return;
        if (!await _ctx.Dialogs.ConfirmAsync("Clear all steps", $"Remove all {Steps.Count} steps from \"{Name}\"?", "Clear", danger: true))
            return;
        Steps.Clear();
        SelectedStep = null;
        SyncModelFromSteps();
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
        foreach (var s in Steps) s.DelayMs = (decimal)Math.Round(s.Model.DelayMs * f);
    }

    /// <summary>Converts all mouse steps to one positioning mode.</summary>
    [RelayCommand]
    private void ConvertAllPositions(string? modeName)
    {
        if (!Enum.TryParse<MousePositionMode>(modeName, out var mode)) return;
        var previous = _ctx.Positions.GetCursorPosition();
        foreach (var s in Steps)
        {
            if (s.Model.Position is not { } pos) continue;
            var absolute = _ctx.Positions.Resolve(pos, previous);
            s.Model.Position = _ctx.Positions.FromAbsolute(absolute, mode, previous);
            previous = absolute;
            s.RaiseAll();
        }
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

    private void SetMode(PlaybackMode mode)
    {
        if (Model.Playback.Mode == mode) return;
        Model.Playback.Mode = mode;
        OnPropertyChanged(nameof(IsModeOnce));
        OnPropertyChanged(nameof(IsModeLoopCount));
        OnPropertyChanged(nameof(IsModeInfinite));
        Changed();
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
