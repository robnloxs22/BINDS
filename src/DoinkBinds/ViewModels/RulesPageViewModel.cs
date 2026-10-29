using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.ViewModels;

/// <summary>
/// Lets a rule's THEN-steps reuse the full macro timeline editor: edits are routed back to the
/// rule (and saved to automation.json) instead of the macro library.
/// </summary>
internal sealed class RuleActionsHost(ILibraryActions main, Action<MacroViewModel> changed) : ILibraryActions
{
    public Task RenameMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task DuplicateMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task DeleteMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task MoveMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public Task ExportMacroAsync(MacroViewModel macro) => Task.CompletedTask;
    public void PlayMacro(MacroViewModel macro) { }
    public Task RenameFolderAsync(FolderViewModel folder) => Task.CompletedTask;
    public Task DeleteFolderAsync(FolderViewModel folder) => Task.CompletedTask;
    public void NewMacroInFolder(FolderViewModel folder) { }
    public void MacroChanged(MacroViewModel macro) => changed(macro);
    public void RefreshTriggers() => main.RefreshTriggers();
    public void Toast(string message, ToastKind kind = ToastKind.Info) => main.Toast(message, kind);
}

/// <summary>One If/Then rule.</summary>
public sealed partial class RuleViewModel : ViewModelBase
{
    private readonly RulesPageViewModel _page;
    private readonly IRuleEngine _engine;

    public Rule Model { get; }
    public ConditionViewModel Condition { get; }
    public MacroViewModel Actions { get; }

    [ObservableProperty] private bool? _isTrueNow;
    [ObservableProperty] private string _statusText = "Not checked yet";
    [ObservableProperty] private string? _testResult;

    public RuleViewModel(Rule model, RulesPageViewModel page, EditorContext ctx, IRuleEngine engine)
    {
        Model = model;
        _page = page;
        _engine = engine;
        Condition = new ConditionViewModel(model.Condition, ctx, Save);

        var actionsMacro = new Macro { Id = model.Id, Name = model.Name, Actions = model.Actions };
        var host = new RuleActionsHost(ctx.Library, m =>
        {
            Model.Actions = m.Model.Actions;
            Save();
        });
        Actions = new MacroViewModel(actionsMacro, new EditorContext(ctx.Positions, ctx.Hotkeys, ctx.Settings, ctx.Dialogs, host)
        {
            Picker = ctx.Picker, Vision = ctx.Vision, Platform = ctx.Platform, Variables = ctx.Variables,
            MacroLibrary = ctx.MacroLibrary, Automation = ctx.Automation, Clipboard = ctx.Clipboard
        });
    }

    private void Save() => _page.ScheduleSave();

    public string Name
    {
        get => Model.Name;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "Untitled rule" : value.Trim();
            if (v == Model.Name) return;
            Model.Name = v;
            OnPropertyChanged();
            Save();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (value != Model.Enabled) { Model.Enabled = value; OnPropertyChanged(); Save(); } }
    }

    public RuleTriggerMode Trigger
    {
        get => Model.Trigger;
        set { if (value != Model.Trigger) { Model.Trigger = value; OnPropertyChanged(); Save(); } }
    }

    public decimal IntervalMs
    {
        get => Model.IntervalMs;
        set { var v = (int)Math.Clamp(value, 50, 3_600_000); if (v != Model.IntervalMs) { Model.IntervalMs = v; OnPropertyChanged(); Save(); } }
    }

    public decimal CooldownSeconds
    {
        get => Model.CooldownMs / 1000m;
        set { var v = (int)Math.Clamp(value * 1000, 0, 86_400_000); if (v != Model.CooldownMs) { Model.CooldownMs = v; OnPropertyChanged(); Save(); } }
    }

    public bool SkipWhileBusy
    {
        get => Model.SkipWhileBusy;
        set { if (value != Model.SkipWhileBusy) { Model.SkipWhileBusy = value; OnPropertyChanged(); Save(); } }
    }

    public decimal MaxFires
    {
        get => Model.MaxFires;
        set { var v = (int)Math.Clamp(value, 0, 1_000_000); if (v != Model.MaxFires) { Model.MaxFires = v; OnPropertyChanged(); Save(); } }
    }

    public void UpdateStatus(RuleStatus s)
    {
        IsTrueNow = s.LastResult;
        var fired = s.LastFiredUtc is { } f ? $"last fired {f.ToLocalTime():HH:mm:ss}" : "never fired";
        StatusText = $"{(s.LastResult == true ? "TRUE" : "false")} · checked in {s.LastCheckMs:0} ms · {s.FireCount}× · {fired}" +
                     (s.Detail is null ? "" : $" — {s.Detail}");
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = "Checking…";
        var rule = Model;
        var r = await Task.Run(() => _engine.Test(rule));
        TestResult = (r.Met ? "✓ The condition is TRUE right now" : "✗ The condition is false right now") + (r.Detail is null ? "" : $" — {r.Detail}");
    }

    [RelayCommand]
    private void RunNow()
    {
        if (Model.Actions.Count == 0)
        {
            _page.Toast("Add some THEN steps first", ToastKind.Error);
            return;
        }
        if (!_engine.RunNow(Model)) _page.Toast("Something is already playing", ToastKind.Error);
    }

    [RelayCommand] private void Delete() => _page.Delete(this);
    [RelayCommand] private void Duplicate() => _page.Duplicate(this);
}

/// <summary>The Rules page: background "when this, do this" automations.</summary>
public sealed partial class RulesPageViewModel : ViewModelBase
{
    private readonly IAutomationStore _store;
    private readonly IRuleEngine _engine;
    private readonly ISettingsService _settings;
    private readonly EditorContext _ctx;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _saveTimer;

    public ObservableCollection<RuleViewModel> Rules { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RuleViewModel? _selectedRule;

    public bool HasSelection => SelectedRule is not null;
    public bool IsEmpty => Rules.Count == 0;

    public RulesPageViewModel(IAutomationStore store, IRuleEngine engine, ISettingsService settings, EditorContext ctx, IDialogService dialogs)
    {
        _store = store;
        _engine = engine;
        _settings = settings;
        _ctx = ctx;
        _dialogs = dialogs;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushSave();

        lock (_store.Data)
            foreach (var r in _store.Data.Rules) Rules.Add(new RuleViewModel(r, this, ctx, engine));
        SelectedRule = Rules.FirstOrDefault();
        Rules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        _engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
            Rules.FirstOrDefault(r => r.Model.Id == s.RuleId)?.UpdateStatus(s));
    }

    public void Toast(string message, ToastKind kind = ToastKind.Info) => _ctx.Library.Toast(message, kind);

    /// <summary>Master switch: rules only watch the screen while this is on.</summary>
    public bool RulesEnabled
    {
        get => _settings.Settings.RulesEnabled;
        set
        {
            if (value == _settings.Settings.RulesEnabled) return;
            _settings.Settings.RulesEnabled = value;
            _settings.Save();
            OnPropertyChanged();
            if (value && !_engine.IsRunning) _engine.Start();
            Toast(value ? "Rules are now watching the screen" : "Rules paused", value ? ToastKind.Success : ToastKind.Info);
        }
    }

    public void RefreshMasterSwitch() => OnPropertyChanged(nameof(RulesEnabled));

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void FlushSave()
    {
        _saveTimer.Stop();
        _store.Save();
    }

    [RelayCommand]
    private void NewRule(string? kind)
    {
        var rule = new Rule { Name = $"Rule {Rules.Count + 1}" };
        switch (kind)
        {
            case "text":
                rule.Condition = new Condition { Type = ConditionType.TextVisible, Text = "Error" };
                rule.Actions.Add(new MacroAction { Type = MacroActionType.Notify, Text = "\"Error\" appeared on screen!", Flag = true });
                rule.Name = "When text appears";
                break;
            case "image":
                rule.Condition = Condition.NewLeaf(ConditionType.ImageVisible);
                rule.Actions.Add(new MacroAction { Type = MacroActionType.MouseClick, Position = new MousePosition { Mode = MousePositionMode.Found }, Count = 1 });
                rule.Name = "Click the image when it appears";
                break;
            case "window":
                rule.Condition = new Condition { Type = ConditionType.WindowExists, Window = new WindowTarget { ProcessName = "", Title = "Error" } };
                rule.Actions.Add(new MacroAction { Type = MacroActionType.KeyPress, KeyCode = KeyCodes.Enter });
                rule.Name = "Dismiss a popup window";
                break;
            default:
                rule.Actions.Add(new MacroAction { Type = MacroActionType.Notify, Text = "The rule fired!" });
                break;
        }
        lock (_store.Data) _store.Data.Rules.Add(rule);
        var vm = new RuleViewModel(rule, this, _ctx, _engine);
        Rules.Add(vm);
        SelectedRule = vm;
        FlushSave();
        if (!RulesEnabled) Toast("Turn on \"Watch the screen\" to start rules running");
    }

    public void Duplicate(RuleViewModel r)
    {
        var copy = r.Model.Clone();
        lock (_store.Data) _store.Data.Rules.Add(copy);
        var vm = new RuleViewModel(copy, this, _ctx, _engine);
        Rules.Add(vm);
        SelectedRule = vm;
        FlushSave();
    }

    public async void Delete(RuleViewModel r)
    {
        if (!await _dialogs.ConfirmAsync("Delete rule", $"Delete the rule \"{r.Name}\"?", "Delete", danger: true)) return;
        lock (_store.Data) _store.Data.Rules.Remove(r.Model);
        var i = Rules.IndexOf(r);
        Rules.Remove(r);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Clamp(i, 0, Rules.Count - 1)];
        FlushSave();
    }
}
