using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.ViewModels;

/// <summary>
/// The application's main view model: owns the macro library tree, the recording/playback
/// workflow, hotkey dispatch and app-wide status (toasts, indicator, permission banner).
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, ILibraryActions, IDisposable
{
    private readonly IPlatformServices _platform;
    private readonly IMacroLibraryService _library;
    private readonly IMacroRecorderService _recorder;
    private readonly IMacroPlayerService _player;
    private readonly IHotkeyService _hotkeys;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogService _log;
    private readonly IRuleEngine _rules;
    private readonly IAutomationStore _automation;
    private readonly EditorContext _ctx;

    private readonly List<MacroViewModel> _allMacros = new();
    private readonly HashSet<MacroViewModel> _dirty = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _recordTimer;
    private readonly DispatcherTimer _toastTimer;

    private MacroViewModel? _recordingTarget;
    private MacroViewModel? _playingMacro;
    private CancellationTokenSource? _countdownCts;

    public MainWindowViewModel(
        IPlatformServices platform,
        IMacroLibraryService library,
        IMacroRecorderService recorder,
        IMacroPlayerService player,
        IHotkeyService hotkeys,
        ISettingsService settings,
        IMousePositionService positions,
        IDialogService dialogs,
        IShellService shell,
        ILogService log,
        IScreenPicker picker,
        IScreenVision vision,
        VariableStore variables,
        IAutomationStore automation,
        IRuleEngine rules,
        ILayoutService layouts,
        IReportService reports,
        IClipboardService clipboard,
        IStartupService startup)
    {
        _platform = platform;
        _library = library;
        _recorder = recorder;
        _player = player;
        _hotkeys = hotkeys;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _log = log;
        _rules = rules;
        _automation = automation;
        _ctx = new EditorContext(positions, hotkeys, settings, dialogs, this)
        {
            Picker = picker,
            Vision = vision,
            Platform = platform,
            Variables = variables,
            MacroLibrary = library,
            Automation = automation,
            Clipboard = clipboard
        };

        Settings = new SettingsViewModel(settings, hotkeys, platform, shell, positions, Toast, startup);
        Rules = new RulesPageViewModel(automation, rules, settings, _ctx, dialogs);
        Layouts = new LayoutsPageViewModel(automation, layouts, platform, dialogs, hotkeys, this);
        Reports = new ReportsPageViewModel(reports, shell, dialogs);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => FlushSaves();

        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _recordTimer.Tick += (_, _) => UpdateRecordingInfo();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { IsToastVisible = false; _toastTimer.Stop(); };

        _hotkeys.MacroNameResolver = id => _allMacros.FirstOrDefault(m => m.Id == id)?.Name;
        _hotkeys.CommandTriggered += (_, a) => Dispatcher.UIThread.Post(() => OnHotkeyCommand(a));
        _hotkeys.MacroTriggered += (_, id) => Dispatcher.UIThread.Post(() => OnMacroTrigger(id));
        _recorder.ActionRecorded += (_, a) => Dispatcher.UIThread.Post(() => AddLiveAction(a));
        _player.StateChanged += (_, s) => Dispatcher.UIThread.Post(() => OnPlayerStateChanged(s));
        _player.ProgressChanged += (_, p) => Dispatcher.UIThread.Post(() => OnPlayerProgress(p));
        _player.Finished += (_, r) => Dispatcher.UIThread.Post(() => OnPlayerFinished(r));
    }

    // =====================================================================================
    // Startup
    // =====================================================================================

    /// <summary>Loads the library and starts the global input hook. Called once the window is shown.</summary>
    public void Initialize()
    {
        _platform.Input.UseScanCodes = _settings.Settings.UseScanCodes;
        _shell.ApplyTheme(_settings.Settings.Theme);

        try
        {
            _library.Load(out var errors);
            if (!_settings.Settings.SamplesInstalled)
            {
                var n = _library.InstallSamples();
                _settings.Settings.SamplesInstalled = true;
                _settings.Save();
                if (n > 0) _log.Info($"Installed {n} sample macros");
            }
            if (errors.Count > 0)
                Toast($"{errors.Count} macro file(s) could not be read — see doink.log", ToastKind.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Library load failed", ex);
            Toast($"Could not load the macro library: {ex.Message}", ToastKind.Error);
        }

        RebuildTree();
        var last = _settings.Settings.LastSelectedMacroId;
        SelectedMacro = _allMacros.FirstOrDefault(m => m.Id == last) ?? _allMacros.FirstOrDefault();
        RefreshTriggers();
        ImportFromCommandLine();
        Reports.Refresh();

        // Background rules: the thread always runs but only evaluates while "Watch the screen" is on.
        _rules.IsSuspended = () => _recorder.IsRecording || IsCountingDown || _guide is not null;
        _rules.Start();

        // Start the global hook (recording + hotkeys depend on it).
        var permission = _platform.CheckPermissions(prompt: true);
        if (!_platform.Hook.Start(out var hookError))
        {
            PlatformWarning = hookError ?? permission ?? "Global input hook unavailable.";
            _log.Error($"Hook start failed: {PlatformWarning}");
        }
        else if (permission is not null)
        {
            PlatformWarning = permission;
        }
    }

    /// <summary>"DoinkBinds.exe path\to\macro.doink" (e.g. double-click with the installer's file association) imports it.</summary>
    private void ImportFromCommandLine()
    {
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (!arg.EndsWith(AppPaths.MacroExtension, StringComparison.OrdinalIgnoreCase) || !File.Exists(arg)) continue;
            try
            {
                var macro = _library.Import(arg, "");
                RebuildTree();
                SelectedMacro = _allMacros.FirstOrDefault(m => m.Id == macro.Id);
                Toast($"Imported \"{macro.Name}\"", ToastKind.Success);
            }
            catch (Exception ex)
            {
                _log.Error($"Command-line import failed: {arg}", ex);
                Toast($"Could not open {Path.GetFileName(arg)}: {ex.Message}", ToastKind.Error);
            }
        }
    }

    // =====================================================================================
    // Library tree / search / selection
    // =====================================================================================

    public ObservableCollection<FolderViewModel> Folders { get; } = new();

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => RebuildTree();

    /// <summary>Bound to TreeView.SelectedItem (can be a folder or a macro).</summary>
    [ObservableProperty] private object? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(ExportSelectedCommand))]
    private MacroViewModel? _selectedMacro;

    public bool HasSelection => SelectedMacro is not null;
    public bool IsLibraryEmpty => _allMacros.Count == 0;

    partial void OnSelectedNodeChanged(object? value)
    {
        if (value is MacroViewModel m) SelectedMacro = m;
    }

    partial void OnSelectedMacroChanged(MacroViewModel? value)
    {
        if (value is not null && !ReferenceEquals(SelectedNode, value)) SelectedNode = value;
        _settings.Settings.LastSelectedMacroId = value?.Id;
    }

    private void RebuildTree()
    {
        var selected = SelectedMacro;
        var expanded = Folders.ToDictionary(f => f.Name, f => f.IsExpanded);

        // Keep view models alive across rebuilds (they hold editor state).
        var byId = _allMacros.ToDictionary(m => m.Id);
        _allMacros.Clear();
        foreach (var m in _library.Macros)
            _allMacros.Add(byId.TryGetValue(m.Id, out var vm) && ReferenceEquals(vm.Model, m) ? vm : new MacroViewModel(m, _ctx));

        var query = SearchText.Trim();
        Folders.Clear();
        foreach (var folderName in _library.Folders)
        {
            var folder = new FolderViewModel(folderName, this)
            {
                IsExpanded = !expanded.TryGetValue(folderName, out var e) || e || query.Length > 0
            };

            var macros = _allMacros
                .Where(m => string.Equals(m.Folder, folderName, StringComparison.OrdinalIgnoreCase))
                .Where(m => query.Length == 0
                            || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || m.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || folderName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var m in macros) folder.Macros.Add(m);

            // While searching, hide folders with no hits (but always show root when not searching).
            if (query.Length > 0 && folder.Macros.Count == 0) continue;
            Folders.Add(folder);
        }

        OnPropertyChanged(nameof(IsLibraryEmpty));
        if (selected is not null && _allMacros.Contains(selected))
        {
            SelectedNode = null; // force TreeView to re-sync after the collection rebuild
            SelectedNode = selected;
        }
        else if (selected is not null)
        {
            SelectedMacro = null;
        }
    }

    // =====================================================================================
    // Library commands
    // =====================================================================================

    [RelayCommand]
    private void NewMacro()
    {
        var folder = SelectedMacro?.Folder ?? (SelectedNode as FolderViewModel)?.Name ?? "";
        CreateMacro(folder, "New Macro");
    }

    public void NewMacroInFolder(FolderViewModel folder) => CreateMacro(folder.Name, "New Macro");

    private MacroViewModel CreateMacro(string folder, string baseName)
    {
        var name = baseName;
        for (var i = 2; _library.Macros.Any(m => m.Folder == folder && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} {i}";

        var macro = new Macro
        {
            Name = name,
            Folder = folder,
            Playback = new PlaybackSettings()
        };
        if (!TrySave(macro)) return new MacroViewModel(macro, _ctx);
        RebuildTree();
        var vm = _allMacros.First(m => m.Id == macro.Id);
        SelectedMacro = vm;
        return vm;
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = await _dialogs.PromptAsync("New folder", "Folder name:", "New Folder", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _library.CreateFolder(name);
            RebuildTree();
        }
        catch (Exception ex)
        {
            Toast($"Could not create folder: {ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Import macro");
        if (path is null) return;
        try
        {
            var folder = SelectedMacro?.Folder ?? (SelectedNode as FolderViewModel)?.Name ?? "";
            var macro = _library.Import(path, folder);
            if (!await ConfirmRiskySteps(macro))
            {
                _library.Delete(macro);
                Toast("Import cancelled");
                return;
            }
            RebuildTree();
            SelectedMacro = _allMacros.FirstOrDefault(m => m.Id == macro.Id);
            Toast($"Imported \"{macro.Name}\"", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error($"Import failed: {path}", ex);
            await _dialogs.ShowMessageAsync("Import failed", $"That file could not be imported.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Safety: a macro from someone else can open programs or close windows. Show exactly what it
    /// would launch before it's added to the library.
    /// </summary>
    private async Task<bool> ConfirmRiskySteps(Macro macro)
    {
        var risky = macro.Actions
            .Where(a => a.Type == MacroActionType.OpenApp || (a.Type == MacroActionType.WindowAction && a.WindowOp == WindowOp.Close))
            .Select(a => a.Type == MacroActionType.OpenApp ? $"• opens: {a.Text}{(string.IsNullOrEmpty(a.Arguments) ? "" : " " + a.Arguments)}" : $"• closes window: {a.Window}")
            .Distinct()
            .ToList();
        if (risky.Count == 0) return true;
        return await _dialogs.ConfirmAsync("This macro runs programs",
            $"\"{macro.Name}\" contains steps that start programs or close windows:\n\n{string.Join("\n", risky.Take(12))}" +
            (risky.Count > 12 ? $"\n…and {risky.Count - 12} more" : "") +
            "\n\nOnly import macros from people you trust.", "Import anyway", danger: true);
    }

    /// <summary>Creates an auto-clicker macro (click where the cursor is, N times per second, until stopped).</summary>
    [RelayCommand]
    private async Task NewAutoClickerAsync()
    {
        var input = await _dialogs.PromptAsync("New auto-clicker", "Clicks per second (1–50):", "10", "Create");
        if (input is null) return;
        if (!double.TryParse(input.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cps) || cps <= 0)
        {
            Toast("Please enter a number of clicks per second", ToastKind.Error);
            return;
        }
        cps = Math.Clamp(cps, 0.1, 50);
        var vm = CreateMacro(SelectedMacro?.Folder ?? "", $"Auto-clicker {cps:0.#}/s");
        vm.ReplaceActions(new List<MacroAction>
        {
            new() { Type = MacroActionType.MouseClick, Button = MacroMouseButton.Left, Count = 1, Position = null, DelayMs = (int)Math.Round(1000 / cps) }
        });
        vm.Model.Playback.Mode = PlaybackMode.LoopInfinite;
        vm.Model.Playback.LoopDelayMs = 0;
        vm.Model.Playback.RestoreWindowFocus = false;
        vm.Model.Description = "Clicks wherever the mouse is. Press Play (or set a trigger hotkey), F9 stops.";
        MacroChanged(vm);
        FlushSaves();
        SelectedMacro = vm;
        Toast($"Auto-clicker ready — {HotkeyText(HotkeyAction.PlayPause)} starts it, {HotkeyText(HotkeyAction.StopLoop)} stops it", ToastKind.Success);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ExportSelectedAsync() => SelectedMacro is { } m ? ExportMacroAsync(m) : Task.CompletedTask;

    public async Task ExportMacroAsync(MacroViewModel macro)
    {
        FlushSaves();
        var path = await _dialogs.PickSaveFileAsync("Export macro", AppPaths.SanitizeName(macro.Name) + AppPaths.MacroExtension);
        if (path is null) return;
        try
        {
            if (!path.EndsWith(AppPaths.MacroExtension, StringComparison.OrdinalIgnoreCase)) path += AppPaths.MacroExtension;
            _library.Export(macro.Model, path);
            Toast($"Exported to {Path.GetFileName(path)}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Export failed", ex);
            Toast($"Export failed: {ex.Message}", ToastKind.Error);
        }
    }

    public async Task RenameMacroAsync(MacroViewModel macro)
    {
        var name = await _dialogs.PromptAsync("Rename macro", "New name:", macro.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        macro.Name = name;
        FlushSaves();
        RebuildTree();
    }

    public async Task DuplicateMacroAsync(MacroViewModel macro)
    {
        FlushSaves();
        try
        {
            var copy = _library.Duplicate(macro.Model);
            RebuildTree();
            SelectedMacro = _allMacros.FirstOrDefault(m => m.Id == copy.Id);
            Toast($"Duplicated as \"{copy.Name}\"", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Toast($"Duplicate failed: {ex.Message}", ToastKind.Error);
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private Task DeleteSelectedAsync() => SelectedMacro is { } m ? DeleteMacroAsync(m) : Task.CompletedTask;

    public async Task DeleteMacroAsync(MacroViewModel macro)
    {
        if (_player.CurrentMacroId == macro.Id || _recordingTarget == macro)
        {
            Toast("Stop playback/recording before deleting this macro", ToastKind.Error);
            return;
        }
        if (!await _dialogs.ConfirmAsync("Delete macro", $"Delete \"{macro.Name}\"? This cannot be undone.", "Delete", danger: true))
            return;
        try
        {
            _dirty.Remove(macro);
            _library.Delete(macro.Model);
            if (SelectedMacro == macro) SelectedMacro = null;
            RebuildTree();
            RefreshTriggers();
            SelectedMacro ??= _allMacros.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Toast($"Delete failed: {ex.Message}", ToastKind.Error);
        }
    }

    public async Task MoveMacroAsync(MacroViewModel macro)
    {
        var folders = _library.Folders.Select(f => f.Length == 0 ? "My Macros" : f).ToList();
        var current = macro.Folder.Length == 0 ? "My Macros" : macro.Folder;
        var choice = await _dialogs.ChooseAsync("Move macro", $"Move \"{macro.Name}\" to:", folders, current);
        if (choice is null) return;
        var target = choice == "My Macros" ? "" : choice;
        try
        {
            FlushSaves();
            _library.MoveToFolder(macro.Model, target);
            macro.NotifyRenamed();
            RebuildTree();
        }
        catch (Exception ex)
        {
            Toast($"Move failed: {ex.Message}", ToastKind.Error);
        }
    }

    public async Task RenameFolderAsync(FolderViewModel folder)
    {
        if (folder.IsRoot) { Toast("The root folder can't be renamed"); return; }
        var name = await _dialogs.PromptAsync("Rename folder", "New folder name:", folder.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            FlushSaves();
            _library.RenameFolder(folder.Name, name);
            foreach (var m in _allMacros) m.NotifyRenamed();
            RebuildTree();
        }
        catch (Exception ex)
        {
            Toast(ex.Message, ToastKind.Error);
        }
    }

    public async Task DeleteFolderAsync(FolderViewModel folder)
    {
        if (folder.IsRoot) return;
        var count = _library.Macros.Count(m => string.Equals(m.Folder, folder.Name, StringComparison.OrdinalIgnoreCase));
        var msg = count == 0
            ? $"Delete the folder \"{folder.Name}\"?"
            : $"Delete the folder \"{folder.Name}\"? Its {count} macro(s) will be moved to My Macros.";
        if (!await _dialogs.ConfirmAsync("Delete folder", msg, "Delete", danger: true)) return;
        try
        {
            FlushSaves();
            _library.DeleteFolder(folder.Name);
            foreach (var m in _allMacros) m.NotifyRenamed();
            RebuildTree();
        }
        catch (Exception ex)
        {
            Toast($"Could not delete folder: {ex.Message}", ToastKind.Error);
        }
    }

    // =====================================================================================
    // Saving (debounced auto-save)
    // =====================================================================================

    public void MacroChanged(MacroViewModel macro)
    {
        _dirty.Add(macro);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSaves()
    {
        _saveTimer.Stop();
        foreach (var m in _dirty.ToList())
        {
            if (_library.Macros.Contains(m.Model)) TrySave(m.Model);
        }
        _dirty.Clear();
    }

    private bool TrySave(Macro macro)
    {
        try
        {
            _library.Save(macro);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Save failed for {macro.Name}", ex);
            Toast($"Could not save \"{macro.Name}\": {ex.Message}", ToastKind.Error);
            return false;
        }
    }

    /// <summary>Per-macro and per-layout hotkeys share the trigger mechanism (both are identified by Guid).</summary>
    public void RefreshTriggers()
    {
        List<WindowLayout> layouts;
        lock (_automation.Data) layouts = _automation.Data.Layouts.ToList();
        _hotkeys.SetMacroTriggers(_library.Macros
            .Where(m => m.TriggerHotkey is not null)
            .Select(m => (m.Id, m.TriggerHotkey!))
            .Concat(layouts.Where(l => l.Hotkey is not null).Select(l => (l.Id, l.Hotkey!))));
    }

    // =====================================================================================
    // Status
    // =====================================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(IsPlaying), nameof(IsPaused), nameof(IsIdle),
        nameof(IsBusy), nameof(StatusText), nameof(RecordButtonTip), nameof(PlayButtonTip))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StopCommand), nameof(ToggleRecordCommand))]
    private AppStatus _status = AppStatus.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isCountingDown;

    [ObservableProperty] private int _countdownValue;

    public bool IsRecording => Status == AppStatus.Recording;
    public bool IsPlaying => Status == AppStatus.Playing;
    public bool IsPaused => Status == AppStatus.Paused;
    public bool IsIdle => Status == AppStatus.Idle && !IsCountingDown;
    public bool IsBusy => !IsIdle;

    public string StatusText => IsCountingDown ? $"Starting in {CountdownValue}…" : Status switch
    {
        AppStatus.Recording => "Recording",
        AppStatus.Playing => "Playing",
        AppStatus.Paused => "Paused",
        _ => "Idle"
    };

    public string RecordButtonTip => IsRecording ? $"Stop recording ({HotkeyText(HotkeyAction.StartStopRecording)})" : $"Record ({HotkeyText(HotkeyAction.StartStopRecording)})";
    public string PlayButtonTip => IsPlaying ? $"Pause ({HotkeyText(HotkeyAction.PlayPause)})" : $"Play ({HotkeyText(HotkeyAction.PlayPause)})";
    public string StopButtonTip => $"Stop ({HotkeyText(HotkeyAction.StopLoop)})";

    private string HotkeyText(HotkeyAction a) =>
        _settings.Settings.Hotkeys.FirstOrDefault(h => h.Action == a)?.Hotkey?.ToString() ?? "no hotkey";

    public bool MacrosEnabled
    {
        get => _settings.Settings.MacrosEnabled;
        set
        {
            if (value == _settings.Settings.MacrosEnabled) return;
            _settings.Settings.MacrosEnabled = value;
            _settings.Save();
            OnPropertyChanged();
            Toast(value ? "Macro hotkeys enabled" : "Macro hotkeys disabled", value ? ToastKind.Success : ToastKind.Info);
        }
    }

    [ObservableProperty] private string? _platformWarning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;

    // Live recording view
    public ObservableCollection<string> LiveActions { get; } = new();
    [ObservableProperty] private string _recordingInfo = "";

    // Toast
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private bool _isToastError;
    [ObservableProperty] private bool _isToastSuccess;

    public void Toast(string message, ToastKind kind = ToastKind.Info)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Toast(message, kind));
            return;
        }
        ToastMessage = message;
        IsToastError = kind == ToastKind.Error;
        IsToastSuccess = kind == ToastKind.Success;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 6 : 3.5);
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast() => IsToastVisible = false;

    [RelayCommand]
    private void DismissWarning() => PlatformWarning = null;

    // =====================================================================================
    // Pages (Macros / Rules / Layouts / Reports)
    // =====================================================================================

    public RulesPageViewModel Rules { get; }
    public LayoutsPageViewModel Layouts { get; }
    public ReportsPageViewModel Reports { get; }

    public enum Page { Macros, Rules, Layouts, Reports }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMacrosPage), nameof(IsRulesPage), nameof(IsLayoutsPage), nameof(IsReportsPage))]
    private Page _currentPage = Page.Macros;

    public bool IsMacrosPage { get => CurrentPage == Page.Macros; set { if (value) CurrentPage = Page.Macros; } }
    public bool IsRulesPage { get => CurrentPage == Page.Rules; set { if (value) CurrentPage = Page.Rules; } }
    public bool IsLayoutsPage { get => CurrentPage == Page.Layouts; set { if (value) CurrentPage = Page.Layouts; } }
    public bool IsReportsPage { get => CurrentPage == Page.Reports; set { if (value) { CurrentPage = Page.Reports; Reports.Refresh(); } } }

    // =====================================================================================
    // Guided recording
    // =====================================================================================

    private GuideViewModel? _guide;

    [RelayCommand]
    private void OpenGuide()
    {
        if (_guide is not null) return;
        if (Status != AppStatus.Idle)
        {
            Toast("Stop recording / playback first", ToastKind.Error);
            return;
        }
        CurrentPage = Page.Macros;
        _guide = new GuideViewModel(_ctx, _recorder, _shell, () =>
        {
            // Build into the selected macro if it's empty, else a new one (never overwrite work).
            var target = SelectedMacro is { HasSteps: false } empty ? empty : CreateMacro(SelectedMacro?.Folder ?? "", $"Guided {DateTime.Now:MMM d HH.mm}");
            SelectedMacro = target;
            return target;
        });
        _guide.CloseRequested += (_, _) =>
        {
            _guide = null;
            FlushSaves();
            _shell.RestoreMainWindow();
        };
        _shell.ShowGuide(_guide);
        if (_settings.Settings.MinimizeWhileRecording) _shell.MinimizeMainWindow();
    }

    // =====================================================================================
    // Tray / window lifetime
    // =====================================================================================

    public bool CloseToTray => _settings.Settings.CloseToTray;
    public bool StartMinimized => _settings.Settings.StartMinimized;

    [RelayCommand]
    private void ToggleRulesFromTray() => Rules.RulesEnabled = !Rules.RulesEnabled;

    [RelayCommand]
    private void ToggleMacrosFromTray() => MacrosEnabled = !MacrosEnabled;

    [RelayCommand]
    private void StopFromTray() => Stop();

    // =====================================================================================
    // Settings panel
    // =====================================================================================

    public SettingsViewModel Settings { get; }

    [ObservableProperty] private bool _isSettingsOpen;

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
        if (!IsSettingsOpen)
        {
            OnPropertyChanged(nameof(RecordButtonTip));
            OnPropertyChanged(nameof(PlayButtonTip));
            OnPropertyChanged(nameof(StopButtonTip));
        }
    }

    // =====================================================================================
    // Hotkeys
    // =====================================================================================

    private void OnHotkeyCommand(HotkeyAction action)
    {
        // The toggle key always works; everything else respects the on/off switch.
        if (action == HotkeyAction.ToggleMacros)
        {
            MacrosEnabled = !MacrosEnabled;
            return;
        }
        if (action == HotkeyAction.StopLoop)
        {
            Stop(); // stopping is always allowed — it's the safety valve
            return;
        }
        if (!MacrosEnabled) return;

        switch (action)
        {
            case HotkeyAction.StartStopRecording:
                ToggleRecord();
                break;
            case HotkeyAction.PlayPause:
                PlayPause();
                break;
            case HotkeyAction.ToggleRules:
                Rules.RulesEnabled = !Rules.RulesEnabled;
                break;
            case HotkeyAction.GuidedRecording:
                OpenGuide();
                break;
        }
    }

    private void OnMacroTrigger(Guid id)
    {
        if (!MacrosEnabled || IsRecording || IsCountingDown) return;

        WindowLayout? layout;
        lock (_automation.Data) layout = _automation.Data.Layouts.FirstOrDefault(l => l.Id == id);
        if (layout is not null)
        {
            Layouts.ApplyLayout(layout);
            return;
        }

        var macro = _allMacros.FirstOrDefault(m => m.Id == id);
        if (macro is null) return;

        // Pressing a macro's trigger while it plays stops it (toggle behaviour).
        if (_player.CurrentMacroId == id)
        {
            _player.Stop();
            return;
        }
        if (_player.State == AppStatus.Idle) StartPlayback(macro);
    }

    // =====================================================================================
    // Recording
    // =====================================================================================

    private bool CanToggleRecord() => Status is AppStatus.Idle or AppStatus.Recording;

    [RelayCommand(CanExecute = nameof(CanToggleRecord))]
    private void ToggleRecord()
    {
        if (IsCountingDown)
        {
            _countdownCts?.Cancel();
            return;
        }
        if (IsRecording) StopRecording();
        else if (Status == AppStatus.Idle) _ = StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (!_platform.Hook.IsRunning)
        {
            await _dialogs.ShowMessageAsync("Recording unavailable",
                PlatformWarning ?? "The global input hook is not running, so input can't be recorded.");
            return;
        }

        // Record into the selected macro if it's empty, otherwise into a fresh macro (never overwrite work).
        var target = SelectedMacro is { HasSteps: false } empty
            ? empty
            : CreateMacro(SelectedMacro?.Folder ?? "", $"Recording {DateTime.Now:MMM d HH.mm}");
        _recordingTarget = target;
        SelectedMacro = target;
        LiveActions.Clear();

        var countdown = _settings.Settings.RecordCountdownSeconds;
        if (countdown > 0)
        {
            _countdownCts = new CancellationTokenSource();
            IsCountingDown = true;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsBusy));
            try
            {
                for (var i = countdown; i > 0; i--)
                {
                    CountdownValue = i;
                    OnPropertyChanged(nameof(StatusText));
                    if (_settings.Settings.ShowRecordingIndicator)
                        _shell.ShowIndicator(AppStatus.Recording, $"Recording in {i}…");
                    await Task.Delay(1000, _countdownCts.Token);
                }
            }
            catch (TaskCanceledException)
            {
                _recordingTarget = null;
                _shell.HideIndicator();
                return;
            }
            finally
            {
                IsCountingDown = false;
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        if (_settings.Settings.MinimizeWhileRecording) _shell.MinimizeMainWindow();

        _recorder.Start();
        Status = AppStatus.Recording;
        _recordTimer.Start();
        UpdateRecordingInfo();
    }

    private void StopRecording()
    {
        if (!_recorder.IsRecording) return;
        var actions = _recorder.Stop();
        _recordTimer.Stop();
        Status = AppStatus.Idle;
        _shell.HideIndicator();
        if (_settings.Settings.MinimizeWhileRecording) _shell.RestoreMainWindow();

        if (_recordingTarget is { } target)
        {
            target.ReplaceActions(actions);
            FlushSaves();
            SelectedMacro = target;
            Toast(actions.Count == 0
                ? "Nothing was recorded"
                : $"Recorded {actions.Count} steps into \"{target.Name}\"", actions.Count == 0 ? ToastKind.Info : ToastKind.Success);
        }
        _recordingTarget = null;
    }

    private void AddLiveAction(MacroAction a)
    {
        if (!IsRecording) return;
        var vm = new ActionViewModel(a, 0, _ => { });
        LiveActions.Insert(0, $"{vm.Title}   {vm.DelayText}");
        while (LiveActions.Count > 12) LiveActions.RemoveAt(LiveActions.Count - 1);
    }

    private void UpdateRecordingInfo()
    {
        if (!IsRecording) return;
        var t = _recorder.Elapsed;
        RecordingInfo = $"{t:mm\\:ss} · {_recorder.ActionCount} steps";
        if (_settings.Settings.ShowRecordingIndicator)
            _shell.ShowIndicator(AppStatus.Recording, $"REC  {RecordingInfo}");
    }

    // =====================================================================================
    // Playback
    // =====================================================================================

    private bool CanPlayPause() => (Status == AppStatus.Idle && SelectedMacro is not null) || IsPlaying || IsPaused;

    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private void PlayPause()
    {
        if (IsRecording || IsCountingDown) return;
        if (IsPlaying || IsPaused)
        {
            _player.TogglePause();
            return;
        }
        if (SelectedMacro is { } m) StartPlayback(m);
    }

    public void PlayMacro(MacroViewModel macro)
    {
        if (Status != AppStatus.Idle) return;
        SelectedMacro = macro;
        StartPlayback(macro);
    }

    private void StartPlayback(MacroViewModel macro)
    {
        if (!_platform.IsSupported)
        {
            Toast("Playback isn't supported on this operating system", ToastKind.Error);
            return;
        }
        if (!macro.HasSteps)
        {
            Toast($"\"{macro.Name}\" has no steps yet — record something first");
            return;
        }

        FlushSaves();
        _playingMacro = macro;
        _ = macro.Steps; // make sure step view models exist for highlighting
        if (_player.Play(macro.Model, macro.Model.Playback))
        {
            macro.IsPlaying = true;
            macro.RecordRun();
            if (_settings.Settings.MinimizeWhilePlaying) _shell.MinimizeMainWindow();
        }
    }

    private bool CanStop() => Status != AppStatus.Idle;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (IsCountingDown) _countdownCts?.Cancel();
        if (IsRecording) StopRecording();
        if (_player.State != AppStatus.Idle) _player.Stop();
    }

    private void OnPlayerStateChanged(AppStatus s)
    {
        if (IsRecording) return;
        Status = s;
        if (s == AppStatus.Idle) return;
        UpdatePlaybackIndicator();
    }

    private void OnPlayerProgress(PlaybackProgress p)
    {
        var loops = p.TotalIterations == 0 ? "∞" : p.TotalIterations.ToString();
        ProgressText = $"Loop {p.Iteration}/{loops} · Step {p.StepIndex + 1}/{p.StepCount}";
        ProgressValue = p.StepCount == 0 ? 0 : (p.StepIndex + 1) * 100.0 / p.StepCount;
        if (_playingMacro is { } m && m.Id == p.MacroId) m.SetCurrentStep(p.StepIndex);
        UpdatePlaybackIndicator();
    }

    private void UpdatePlaybackIndicator()
    {
        if (!_settings.Settings.ShowRecordingIndicator || Status == AppStatus.Idle) return;
        var name = _playingMacro?.Name ?? "Macro";
        _shell.ShowIndicator(Status, $"{(IsPaused ? "Paused" : "Playing")} {name}  ·  {ProgressText}");
    }

    private void OnPlayerFinished(PlaybackResult r)
    {
        Status = AppStatus.Idle;
        _shell.HideIndicator();
        if (_playingMacro is { } m)
        {
            m.IsPlaying = false;
            m.SetCurrentStep(-1);
        }
        _playingMacro = null;
        ProgressText = "";
        ProgressValue = 0;
        if (_settings.Settings.MinimizeWhilePlaying) _shell.RestoreMainWindow();

        if (r.Report is { Result: "Failed" } && r.Error is null) Toast($"Finished with failed checks — see Reports ({r.Report.FailedCount} failed)", ToastKind.Error);
        else if (r.Error is not null) Toast(r.Error + (r.Report is not null ? " (details in Reports)" : ""), ToastKind.Error);
        else if (r.Cancelled) Toast("Playback stopped");
        else Toast(r.IterationsCompleted > 1 ? $"Finished {r.IterationsCompleted} loops" : "Playback finished", ToastKind.Success);
    }

    // =====================================================================================
    // Shutdown
    // =====================================================================================

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _countdownCts?.Cancel();
            if (_recorder.IsRecording)
            {
                // Don't lose an in-progress recording when the window is closed.
                var actions = _recorder.Stop();
                if (_recordingTarget is { } t && actions.Count > 0) t.ReplaceActions(actions);
            }
            _player.Stop();
            _rules.Stop();
            Rules.FlushSave();
            FlushSaves();
            _settings.Save();
            _platform.Hook.Stop();
        }
        catch (Exception ex)
        {
            _log.Error("Shutdown error", ex);
        }
    }
}
