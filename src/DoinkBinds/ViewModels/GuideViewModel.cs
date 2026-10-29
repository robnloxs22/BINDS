using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.Vision;
using DoinkBinds.Views;

namespace DoinkBinds.ViewModels;

/// <summary>
/// Guided recording, in a small always-on-top window:
///  • <b>Step by step</b> — "what should happen next?" buttons; each one asks for the details
///    (pick a point on screen, snip an image, type the text…) and appends a step.
///  • <b>Record with pauses</b> — records normally, but stops after every click so you can name
///    it, make it "click by image", add "wait until this appears" first, or undo it.
/// </summary>
public sealed partial class GuideViewModel : ViewModelBase
{
    public enum GuideMode { Choose, Wizard, Recording, Paused }

    private readonly EditorContext _ctx;
    private readonly IMacroRecorderService _recorder;
    private readonly IShellService _shell;
    private readonly Func<MacroViewModel> _target;
    private readonly Stack<int> _groupSizes = new(); // steps added per guide action, for "undo last"
    private GuidedClick? _pendingClick;
    private MacroViewModel? _macro;

    public event EventHandler? CloseRequested;

    public ObservableCollection<string> Recent { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoose), nameof(IsWizard), nameof(IsRecording), nameof(IsPaused), nameof(HeaderText))]
    private GuideMode _mode = GuideMode.Choose;

    [ObservableProperty] private string _pausedTitle = "";
    [ObservableProperty] private Bitmap? _pausedImage;
    [ObservableProperty] private string _stepLabel = "";
    [ObservableProperty] private bool _clickByImage;
    [ObservableProperty] private bool _waitForItFirst;
    [ObservableProperty] private bool _wizardClickByImage;
    [ObservableProperty] private string _status = "";

    public bool IsChoose => Mode == GuideMode.Choose;
    public bool IsWizard => Mode == GuideMode.Wizard;
    public bool IsRecording => Mode == GuideMode.Recording;
    public bool IsPaused => Mode == GuideMode.Paused;

    public string HeaderText => Mode switch
    {
        GuideMode.Wizard => "What should happen next?",
        GuideMode.Recording => "Recording… do the next action",
        GuideMode.Paused => "Step recorded — what about it?",
        _ => "Guided recording"
    };

    public string TargetName => _macro?.Name ?? "";

    public GuideViewModel(EditorContext ctx, IMacroRecorderService recorder, IShellService shell, Func<MacroViewModel> target)
    {
        _ctx = ctx;
        _recorder = recorder;
        _shell = shell;
        _target = target;
        _recorder.ClickCompleted += OnClickCompleted;
    }

    private MacroViewModel Macro => _macro ??= _target();

    private void Added(IReadOnlyList<MacroAction> actions)
    {
        if (actions.Count == 0) return;
        Macro.AppendActions(actions);
        _groupSizes.Push(actions.Count);
        var first = Macro.Steps.Count - actions.Count;
        for (var i = 0; i < actions.Count; i++)
            Recent.Insert(0, $"{first + i + 1}. {new ActionViewModel(actions[i], 0, _ => { }).Title}");
        while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
        Status = $"{Macro.Steps.Count} steps in \"{Macro.Name}\"";
        OnPropertyChanged(nameof(TargetName));
    }

    // ================================================================== mode selection

    [RelayCommand]
    private void StartWizard()
    {
        _ = Macro;
        Mode = GuideMode.Wizard;
        Status = $"Building \"{Macro.Name}\"";
        OnPropertyChanged(nameof(TargetName));
    }

    [RelayCommand]
    private void StartRecording()
    {
        _ = Macro;
        _recorder.PauseAfterClicks = true;
        _recorder.Start();
        Mode = GuideMode.Recording;
        Status = "Click, type and scroll as usual. After every click you'll get options here.";
        OnPropertyChanged(nameof(TargetName));
        _shell.ShowIndicator(AppStatus.Recording, "Guided recording");
    }

    [RelayCommand]
    private void Finish()
    {
        if (_recorder.IsRecording)
        {
            if (Mode == GuideMode.Paused) CommitPausedClick();
            var rest = _recorder.Stop();
            _recorder.PauseAfterClicks = false;
            Added(rest);
            _shell.HideIndicator();
        }
        var count = _macro?.Steps.Count ?? 0;
        _ctx.Library.Toast(count > 0 ? $"Guided recording done — \"{_macro!.Name}\" has {count} steps" : "Guided recording closed", ToastKind.Success);
        _recorder.ClickCompleted -= OnClickCompleted;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================== record-with-pauses

    private void OnClickCompleted(object? sender, GuidedClick click) => Dispatcher.UIThread.Post(() =>
    {
        _pendingClick = click;
        PausedTitle = $"{(click.Button == MacroMouseButton.Left ? "Left" : click.Button.ToString())} click at {click.Point}";
        PausedImage = click.Snapshot is null ? null : BitmapHelper.ToBitmap(click.Snapshot);
        StepLabel = "";
        ClickByImage = false;
        WaitForItFirst = false;
        Mode = GuideMode.Paused;
        _shell.ShowIndicator(AppStatus.Paused, "Guided recording — paused");
    });

    /// <summary>Applies the paused-click options to what was recorded since the last pause and appends it.</summary>
    private void CommitPausedClick()
    {
        var actions = _recorder.Drain();
        var click = _pendingClick;
        _pendingClick = null;
        if (click is null) { Added(actions); return; }

        var downIndex = actions.FindLastIndex(a => a.Type == MacroActionType.MouseDown);
        var upIndex = actions.FindLastIndex(a => a.Type == MacroActionType.MouseUp);
        var label = string.IsNullOrWhiteSpace(StepLabel) ? null : StepLabel.Trim();
        var template = click.Snapshot;

        if (downIndex >= 0 && upIndex > downIndex && (ClickByImage || WaitForItFirst) && template is not null)
        {
            var imageCondition = new Condition
            {
                Type = ConditionType.ImageVisible, ImagePng = template.ToBase64Png(), ImageScale = template.Scale, Threshold = 0.9
            };
            var insert = new List<MacroAction>();
            if (WaitForItFirst)
                insert.Add(new MacroAction
                {
                    Type = MacroActionType.WaitFor, Condition = imageCondition.Clone(), TimeoutMs = 15_000,
                    Note = label is null ? "Wait until it appears" : $"Wait for: {label}", DelayMs = actions[downIndex].DelayMs
                });
            if (ClickByImage)
            {
                // Replace the fixed-position click (and the mouse moves leading to it) by "find this image and click it".
                var before = actions.Take(downIndex).ToList();
                var removedMoveDelay = before.Where(a => a.Type == MacroActionType.MouseMove).Sum(a => a.DelayMs);
                var find = new MacroAction
                {
                    Type = MacroActionType.FindAndClick, Condition = imageCondition, TimeoutMs = 15_000, Button = click.Button,
                    Count = 1, Note = label, DelayMs = WaitForItFirst ? 100 : removedMoveDelay + actions[downIndex].DelayMs
                };
                if (WaitForItFirst) insert[0].DelayMs = removedMoveDelay + actions[downIndex].DelayMs;
                actions = before.Where(a => a.Type != MacroActionType.MouseMove)
                    .Concat(insert)
                    .Append(find)
                    .Concat(actions.Skip(upIndex + 1))
                    .ToList();
            }
            else
            {
                actions[downIndex].Note = label;
                actions.InsertRange(downIndex, insert);
            }
        }
        else if (downIndex >= 0 && label is not null)
        {
            actions[downIndex].Note = label;
        }
        Added(actions);
    }

    [RelayCommand]
    private void Continue()
    {
        CommitPausedClick();
        Mode = GuideMode.Recording;
        _shell.ShowIndicator(AppStatus.Recording, "Guided recording");
        _recorder.Resume();
    }

    /// <summary>Discards the click that was just recorded and continues.</summary>
    [RelayCommand]
    private void DiscardClick()
    {
        _recorder.Drain();
        _pendingClick = null;
        Mode = GuideMode.Recording;
        _shell.ShowIndicator(AppStatus.Recording, "Guided recording");
        _recorder.Resume();
        Status = "Discarded that click";
    }

    // ================================================================== step-by-step wizard

    private IScreenPicker? Picker => _ctx.Picker;

    [RelayCommand]
    private async Task AddClickAsync(string? kind)
    {
        if (Picker is null) return;
        var button = kind == "right" ? MacroMouseButton.Right : MacroMouseButton.Left;
        var count = kind == "double" ? 2 : 1;

        if (WizardClickByImage)
        {
            var pick = await Picker.PickRegionAsync("Drag a box around the button / thing to click (it will be found even if it moves)");
            if (pick?.Image is null) return;
            Added(new[]
            {
                new MacroAction
                {
                    Type = MacroActionType.FindAndClick, Button = button, Count = count, TimeoutMs = 15_000, DelayMs = 300,
                    Condition = new Condition { Type = ConditionType.ImageVisible, ImagePng = pick.Image.ToBase64Png(), ImageScale = pick.Image.Scale, Threshold = 0.9 }
                }
            });
            return;
        }

        var p = await Picker.PickAsync(count == 2 ? "Click where to double-click" : $"Click where to {(button == MacroMouseButton.Right ? "right-" : "")}click");
        if (p is not { } point) return;
        Added(new[]
        {
            new MacroAction
            {
                Type = MacroActionType.MouseClick, Button = button, Count = count, DelayMs = 300,
                Position = new MousePosition { Mode = MousePositionMode.PinPoint, X = Math.Round(point.X), Y = Math.Round(point.Y) },
                Window = _ctx.Platform?.Windows.GetForegroundWindow()
            }
        });
    }

    [RelayCommand]
    private async Task AddTypeTextAsync()
    {
        var text = await _ctx.Dialogs.PromptAsync("Type text", "What should be typed? You can use {variables} like {date}.", "");
        if (string.IsNullOrEmpty(text)) return;
        Added(new[] { new MacroAction { Type = MacroActionType.TypeText, Text = text, DelayMs = 200 } });
    }

    [RelayCommand]
    private async Task AddKeyAsync()
    {
        Status = "Press the key or key combination (e.g. Ctrl+S)… Esc cancels";
        var hk = await _ctx.Hotkeys.CaptureAsync();
        Status = "";
        if (hk is null) return;
        var steps = new List<MacroAction>();
        var mods = new List<int>();
        if (hk.Modifiers.HasFlag(HotkeyModifiers.Control)) mods.Add(KeyCodes.LControl);
        if (hk.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods.Add(KeyCodes.LAlt);
        if (hk.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods.Add(KeyCodes.LShift);
        if (hk.Modifiers.HasFlag(HotkeyModifiers.Meta)) mods.Add(KeyCodes.LWin);
        foreach (var m in mods) steps.Add(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = m, DelayMs = steps.Count == 0 ? 200 : 20 });
        steps.Add(new MacroAction { Type = MacroActionType.KeyPress, KeyCode = hk.KeyCode, DelayMs = steps.Count == 0 ? 200 : 20, Note = mods.Count > 0 ? hk.ToString() : null });
        foreach (var m in Enumerable.Reverse(mods)) steps.Add(new MacroAction { Type = MacroActionType.KeyUp, KeyCode = m, DelayMs = 20 });
        Added(steps);
    }

    [RelayCommand]
    private async Task AddWaitAsync()
    {
        var s = await _ctx.Dialogs.PromptAsync("Wait", "How many seconds should it wait?", "1");
        if (s is null || !double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) || sec < 0) return;
        Added(new[] { new MacroAction { Type = MacroActionType.Wait, DelayMs = (int)(sec * 1000) } });
    }

    [RelayCommand]
    private async Task AddWaitImageAsync()
    {
        if (Picker is null) return;
        var pick = await Picker.PickRegionAsync("Drag a box around what must appear before continuing");
        if (pick?.Image is null) return;
        Added(new[]
        {
            new MacroAction
            {
                Type = MacroActionType.WaitFor, TimeoutMs = 30_000, DelayMs = 100,
                Condition = new Condition { Type = ConditionType.ImageVisible, ImagePng = pick.Image.ToBase64Png(), ImageScale = pick.Image.Scale, Threshold = 0.9 }
            }
        });
    }

    [RelayCommand]
    private async Task AddWaitTextAsync()
    {
        var text = await _ctx.Dialogs.PromptAsync("Wait for text", "Which text must appear on screen?", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        ScreenRegion? region = null;
        if (Picker is not null && await _ctx.Dialogs.ConfirmAsync("Where?", "Limit the search to an area of the screen? (Faster and more reliable.)", "Pick area"))
            region = (await Picker.PickRegionAsync("Drag a box around where the text will appear"))?.Region;
        Added(new[]
        {
            new MacroAction
            {
                Type = MacroActionType.WaitFor, TimeoutMs = 30_000, DelayMs = 100,
                Condition = new Condition { Type = ConditionType.TextVisible, Text = text.Trim(), Region = region }
            }
        });
    }

    [RelayCommand]
    private async Task AddClickTextAsync()
    {
        var text = await _ctx.Dialogs.PromptAsync("Click on text", "Which text should be clicked? (e.g. Sign in)", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        Added(new[]
        {
            new MacroAction
            {
                Type = MacroActionType.FindAndClick, TimeoutMs = 15_000, DelayMs = 200, Count = 1,
                Condition = new Condition { Type = ConditionType.TextVisible, Text = text.Trim() }, Note = $"Click \"{text.Trim()}\""
            }
        });
    }

    [RelayCommand]
    private async Task AddOpenAppAsync()
    {
        var path = await _ctx.Dialogs.PromptAsync("Open", "App path, file or website to open (leave empty to browse):", "");
        if (path is null) return;
        if (path.Length == 0)
        {
            path = await _ctx.Dialogs.PickAnyFileAsync("Choose the app or file to open") ?? "";
            var app = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
            if (OperatingSystem.IsMacOS() && app > 0) path = path[..(app + 4)];
        }
        if (path.Length == 0) return;
        Added(new[] { new MacroAction { Type = MacroActionType.OpenApp, Text = path, DelayMs = 100 } });
    }

    [RelayCommand]
    private async Task AddCheckAsync()
    {
        var text = await _ctx.Dialogs.PromptAsync("Check (test)", "Which text should be visible at this point? It will be recorded as PASS/FAIL in the run report.", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        Added(new[]
        {
            new MacroAction
            {
                Type = MacroActionType.Check, OnFail = FailAction.Skip, DelayMs = 100, Note = $"\"{text.Trim()}\" is shown",
                Condition = new Condition { Type = ConditionType.TextVisible, Text = text.Trim() }
            }
        });
    }

    [RelayCommand]
    private void AddScreenshot() =>
        Added(new[] { new MacroAction { Type = MacroActionType.Screenshot, Text = "step {time}", DelayMs = 100 } });

    [RelayCommand]
    private async Task LabelLastAsync()
    {
        if (Macro.Steps.Count == 0) return;
        var last = Macro.Steps[^1];
        var label = await _ctx.Dialogs.PromptAsync("Name this step", "Describe the last step (shown in the timeline):", last.Label);
        if (label is null) return;
        last.Label = label;
        Recent.Insert(0, $"✎ {last.Number}. {label}");
    }

    [RelayCommand]
    private void UndoLast()
    {
        if (_groupSizes.Count == 0)
        {
            Status = "Nothing to undo";
            return;
        }
        var n = _groupSizes.Pop();
        Macro.RemoveLastSteps(n);
        Recent.Insert(0, $"↶ removed {n} step{(n == 1 ? "" : "s")}");
        Status = $"{Macro.Steps.Count} steps in \"{Macro.Name}\"";
    }
}
