using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;

namespace DoinkBinds.ViewModels;

/// <summary>One row in the hotkey table.</summary>
public sealed partial class HotkeyRowViewModel : ViewModelBase
{
    private readonly HotkeyBinding _binding;
    private readonly IHotkeyService _hotkeys;
    private readonly ISettingsService _settings;
    private readonly Action<string, ToastKind> _toast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyText))]
    private bool _isCapturing;

    public HotkeyRowViewModel(HotkeyBinding binding, IHotkeyService hotkeys, ISettingsService settings, Action<string, ToastKind> toast)
    {
        _binding = binding;
        _hotkeys = hotkeys;
        _settings = settings;
        _toast = toast;
    }

    public HotkeyAction Action => _binding.Action;
    public string Label => HotkeyService.Describe(_binding.Action);

    public string Description => _binding.Action switch
    {
        HotkeyAction.StartStopRecording => "Begin recording a new macro, press again to finish.",
        HotkeyAction.PlayPause => "Play the selected macro, or pause / resume playback.",
        HotkeyAction.ToggleMacros => "Turn all macro hotkeys on or off (this key always works).",
        HotkeyAction.StopLoop => "Stop playback immediately, including infinite loops.",
        _ => ""
    };

    public string HotkeyText => IsCapturing ? "Press keys… (Esc to cancel)" : _binding.Hotkey?.ToString() ?? "Not set";

    public HotkeyScope Scope
    {
        get => _binding.Scope;
        set
        {
            if (_binding.Scope == value) return;
            _binding.Scope = value;
            OnPropertyChanged();
            _settings.NotifyChanged();
        }
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        IsCapturing = true;
        try
        {
            var hk = await _hotkeys.CaptureAsync();
            if (hk is null) return;
            var conflict = _hotkeys.FindConflict(hk, ignoreAction: _binding.Action);
            if (conflict is not null)
            {
                _toast($"{hk} is already used by {conflict}", ToastKind.Error);
                return;
            }
            _binding.Hotkey = hk;
            _settings.NotifyChanged();
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _binding.Hotkey = null;
        OnPropertyChanged(nameof(HotkeyText));
        _settings.NotifyChanged();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(Scope));
    }
}

/// <summary>Backs the Settings panel. Every change is applied and persisted immediately.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IPlatformServices _platform;
    private readonly IShellService _shell;
    private readonly IMousePositionService _positions;

    [ObservableProperty] private string? _permissionMessage;

    public SettingsViewModel(ISettingsService settings, IHotkeyService hotkeys, IPlatformServices platform,
        IShellService shell, IMousePositionService positions, Action<string, ToastKind> toast)
    {
        _settings = settings;
        _platform = platform;
        _shell = shell;
        _positions = positions;
        Hotkeys = new ObservableCollection<HotkeyRowViewModel>(
            settings.Settings.Hotkeys.Select(b => new HotkeyRowViewModel(b, hotkeys, settings, toast)));
        PermissionMessage = platform.CheckPermissions(prompt: false);
    }

    private AppSettings S => _settings.Settings;

    private void Set<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        OnPropertyChanged(name);
        _settings.NotifyChanged();
    }

    // ---------------- Hotkeys
    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; }

    public HotkeyScope MacroTriggerScope
    {
        get => S.MacroTriggerScope;
        set => Set(S.MacroTriggerScope, value, v => S.MacroTriggerScope = v);
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        var defaults = AppSettings.DefaultHotkeys();
        foreach (var b in S.Hotkeys)
        {
            var d = defaults.First(x => x.Action == b.Action);
            b.Hotkey = d.Hotkey;
            b.Scope = d.Scope;
        }
        foreach (var row in Hotkeys) row.Refresh();
        _settings.NotifyChanged();
    }

    // ---------------- Recording
    public MousePositionMode DefaultMouseMode
    {
        get => S.DefaultMouseMode;
        set => Set(S.DefaultMouseMode, value, v => S.DefaultMouseMode = v);
    }

    public bool RecordMouseMovement
    {
        get => S.RecordMouseMovement;
        set => Set(S.RecordMouseMovement, value, v => S.RecordMouseMovement = v);
    }

    public decimal MouseMoveSampleMs
    {
        get => S.MouseMoveSampleMs;
        set => Set(S.MouseMoveSampleMs, (int)Math.Clamp(value, 1, 1000), v => S.MouseMoveSampleMs = v);
    }

    public decimal MouseMoveMinDistance
    {
        get => S.MouseMoveMinDistance;
        set => Set(S.MouseMoveMinDistance, (int)Math.Clamp(value, 0, 100), v => S.MouseMoveMinDistance = v);
    }

    public bool RecordWindowInfo
    {
        get => S.RecordWindowInfo;
        set => Set(S.RecordWindowInfo, value, v => S.RecordWindowInfo = v);
    }

    public bool IgnoreOwnWindow
    {
        get => S.IgnoreOwnWindow;
        set => Set(S.IgnoreOwnWindow, value, v => S.IgnoreOwnWindow = v);
    }

    public bool ShowRecordingIndicator
    {
        get => S.ShowRecordingIndicator;
        set => Set(S.ShowRecordingIndicator, value, v => S.ShowRecordingIndicator = v);
    }

    public bool MinimizeWhileRecording
    {
        get => S.MinimizeWhileRecording;
        set => Set(S.MinimizeWhileRecording, value, v => S.MinimizeWhileRecording = v);
    }

    public decimal RecordCountdownSeconds
    {
        get => S.RecordCountdownSeconds;
        set => Set(S.RecordCountdownSeconds, (int)Math.Clamp(value, 0, 10), v => S.RecordCountdownSeconds = v);
    }

    // ---------------- Playback
    public MonitorAwareness MonitorAwareness
    {
        get => S.MonitorAwareness;
        set => Set(S.MonitorAwareness, value, v => S.MonitorAwareness = v);
    }

    public decimal WaitForWindowTimeoutSeconds
    {
        get => S.WaitForWindowTimeoutMs / 1000m;
        set => Set(S.WaitForWindowTimeoutMs, (int)Math.Clamp(value * 1000, 0, 600_000), v => S.WaitForWindowTimeoutMs = v);
    }

    public bool UseScanCodes
    {
        get => S.UseScanCodes;
        set => Set(S.UseScanCodes, value, v => { S.UseScanCodes = v; _platform.Input.UseScanCodes = v; });
    }

    public bool MinimizeWhilePlaying
    {
        get => S.MinimizeWhilePlaying;
        set => Set(S.MinimizeWhilePlaying, value, v => S.MinimizeWhilePlaying = v);
    }

    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMac => OperatingSystem.IsMacOS();

    // ---------------- Appearance
    public string Theme
    {
        get => S.Theme;
        set => Set(S.Theme, value, v => { S.Theme = v; _shell.ApplyTheme(v); });
    }

    // ---------------- About / diagnostics
    public string DataFolder => AppPaths.DataRoot;
    public string PlatformName => _platform.Name;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string MonitorSummary =>
        string.Join(Environment.NewLine, _positions.GetScreens().Select(s => s.ToString()));

    [RelayCommand]
    private void RefreshMonitors() => OnPropertyChanged(nameof(MonitorSummary));

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(AppPaths.DataRoot);

    [RelayCommand]
    private void RequestPermissions()
    {
        PermissionMessage = _platform.CheckPermissions(prompt: true);
    }
}
