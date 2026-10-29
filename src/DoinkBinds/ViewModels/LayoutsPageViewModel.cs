using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.ViewModels;

/// <summary>One window in a layout.</summary>
public sealed partial class LayoutEntryViewModel : ViewModelBase
{
    private readonly LayoutViewModel _owner;
    public WindowLayoutEntry Model { get; }

    public LayoutEntryViewModel(WindowLayoutEntry model, LayoutViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        _owner.Changed();
    }

    public string ProcessName { get => Model.ProcessName; set { if (value != Model.ProcessName) { Model.ProcessName = value.Trim(); Changed(); } } }
    public string TitleContains { get => Model.TitleContains; set { if (value != Model.TitleContains) { Model.TitleContains = value; Changed(); } } }
    public string LaunchCommand { get => Model.LaunchCommand; set { if (value != Model.LaunchCommand) { Model.LaunchCommand = value; Changed(); } } }
    public decimal X { get => (decimal)Model.X; set { Model.X = (double)value; Changed(); } }
    public decimal Y { get => (decimal)Model.Y; set { Model.Y = (double)value; Changed(); } }
    public decimal Width { get => (decimal)Model.Width; set { Model.Width = (double)Math.Max(50, value); Changed(); } }
    public decimal Height { get => (decimal)Model.Height; set { Model.Height = (double)Math.Max(50, value); Changed(); } }
    public WindowStateKind State { get => Model.State; set { if (value != Model.State) { Model.State = value; Changed(); } } }

    [RelayCommand] private void Remove() => _owner.RemoveEntry(this);

    /// <summary>Copies where the window is right now.</summary>
    [RelayCommand]
    private void UseCurrentPosition()
    {
        var w = _owner.Page.FindWindow(Model);
        if (w is null)
        {
            _owner.Page.Toast($"\"{Model.ProcessName}\" isn't open", ToastKind.Error);
            return;
        }
        Model.X = w.X; Model.Y = w.Y; Model.Width = w.Width; Model.Height = w.Height;
        Model.State = w.IsMaximized ? WindowStateKind.Maximized : WindowStateKind.Normal;
        OnPropertyChanged(string.Empty);
        _owner.Changed();
    }

    [RelayCommand]
    private async Task BrowseLaunchAsync()
    {
        var path = await _owner.Page.Dialogs.PickAnyFileAsync("App to launch when the window isn't open");
        if (path is null) return;
        var app = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS() && app > 0) path = path[..(app + 4)];
        LaunchCommand = path;
        OnPropertyChanged(nameof(LaunchCommand));
    }
}

public sealed partial class LayoutViewModel : ViewModelBase
{
    public LayoutsPageViewModel Page { get; }
    public WindowLayout Model { get; }
    public ObservableCollection<LayoutEntryViewModel> Entries { get; } = new();

    [ObservableProperty] private bool _isCapturingHotkey;

    public LayoutViewModel(WindowLayout model, LayoutsPageViewModel page)
    {
        Model = model;
        Page = page;
        foreach (var e in model.Entries) Entries.Add(new LayoutEntryViewModel(e, this));
    }

    public void Changed() => Page.Save();

    public string Name
    {
        get => Model.Name;
        set { var v = string.IsNullOrWhiteSpace(value) ? "Layout" : value.Trim(); if (v != Model.Name) { Model.Name = v; OnPropertyChanged(); Changed(); } }
    }

    public decimal LaunchWaitSeconds
    {
        get => Model.LaunchWaitSeconds;
        set { var v = (int)Math.Clamp(value, 1, 300); if (v != Model.LaunchWaitSeconds) { Model.LaunchWaitSeconds = v; OnPropertyChanged(); Changed(); } }
    }

    public string HotkeyText => IsCapturingHotkey ? "Press keys… (Esc to cancel)" : Model.Hotkey?.ToString() ?? "No hotkey";
    partial void OnIsCapturingHotkeyChanged(bool value) => OnPropertyChanged(nameof(HotkeyText));

    public string Summary => $"{Model.Entries.Count} window{(Model.Entries.Count == 1 ? "" : "s")}" + (Model.Hotkey is { } h ? $" · {h}" : "");

    public void RemoveEntry(LayoutEntryViewModel e)
    {
        Model.Entries.Remove(e.Model);
        Entries.Remove(e);
        OnPropertyChanged(nameof(Summary));
        Changed();
    }

    public void AddEntry(WindowLayoutEntry e)
    {
        Model.Entries.Add(e);
        Entries.Add(new LayoutEntryViewModel(e, this));
        OnPropertyChanged(nameof(Summary));
        Changed();
    }

    [RelayCommand]
    private async Task AddWindowAsync()
    {
        var windows = Page.OpenWindows();
        if (windows.Count == 0)
        {
            Page.Toast("No windows could be listed", ToastKind.Error);
            return;
        }
        var labels = windows.Select(w => w.ToString()).ToList();
        var choice = await Page.Dialogs.ChooseAsync("Add window", "Which window should be part of this layout?", labels, labels[0]);
        if (choice is null) return;
        var w = windows[labels.IndexOf(choice)];
        AddEntry(new WindowLayoutEntry
        {
            ProcessName = w.ProcessName, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height,
            State = w.IsMaximized ? WindowStateKind.Maximized : WindowStateKind.Normal
        });
    }

    [RelayCommand]
    private async Task RecaptureAsync()
    {
        if (Model.Entries.Count > 0 &&
            !await Page.Dialogs.ConfirmAsync("Capture current windows", "Replace this layout's windows with the windows that are open right now?", "Replace"))
            return;
        var snap = Page.Layouts.CaptureCurrent(Name);
        Model.Entries.Clear();
        Entries.Clear();
        foreach (var e in snap.Entries) AddEntry(e);
        Page.Toast($"Captured {snap.Entries.Count} windows", ToastKind.Success);
    }

    [RelayCommand] private void Apply() => Page.Apply(this);
    [RelayCommand] private void Delete() => Page.Delete(this);

    [RelayCommand]
    private async Task CaptureHotkeyAsync()
    {
        IsCapturingHotkey = true;
        try
        {
            var hk = await Page.Hotkeys.CaptureAsync();
            if (hk is null) return;
            var conflict = Page.Hotkeys.FindConflict(hk, ignoreMacro: Model.Id);
            if (conflict is not null)
            {
                Page.Toast($"{hk} is already used by {conflict}", ToastKind.Error);
                return;
            }
            Model.Hotkey = hk;
            Changed();
            Page.Main.RefreshTriggers();
        }
        finally
        {
            IsCapturingHotkey = false;
            OnPropertyChanged(nameof(Summary));
        }
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        Model.Hotkey = null;
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(Summary));
        Changed();
        Page.Main.RefreshTriggers();
    }
}

/// <summary>The Layouts page: saved window arrangements.</summary>
public sealed partial class LayoutsPageViewModel : ViewModelBase
{
    private readonly IAutomationStore _store;
    private readonly IPlatformServices _platform;

    public ILayoutService Layouts { get; }
    public IDialogService Dialogs { get; }
    public IHotkeyService Hotkeys { get; }
    public ILibraryActions Main { get; }

    public ObservableCollection<LayoutViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LayoutViewModel? _selected;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Items.Count == 0;

    public LayoutsPageViewModel(IAutomationStore store, ILayoutService layouts, IPlatformServices platform,
        IDialogService dialogs, IHotkeyService hotkeys, ILibraryActions main)
    {
        _store = store;
        _platform = platform;
        Layouts = layouts;
        Dialogs = dialogs;
        Hotkeys = hotkeys;
        Main = main;
        lock (_store.Data)
            foreach (var l in _store.Data.Layouts) Items.Add(new LayoutViewModel(l, this));
        Selected = Items.FirstOrDefault();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public void Toast(string message, ToastKind kind = ToastKind.Info) => Main.Toast(message, kind);
    public void Save() => _store.Save();

    public IReadOnlyList<WindowInfo> OpenWindows()
    {
        var own = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        return _platform.Windows.ListWindows()
            .Where(w => !string.Equals(w.ProcessName, own, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public WindowInfo? FindWindow(WindowLayoutEntry e) => OpenWindows().FirstOrDefault(w =>
        string.Equals(w.ProcessName, e.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(e.TitleContains) || w.Title.Contains(e.TitleContains, StringComparison.OrdinalIgnoreCase)));

    [RelayCommand]
    private void NewLayout()
    {
        var layout = Layouts.CaptureCurrent($"Layout {Items.Count + 1}");
        lock (_store.Data) _store.Data.Layouts.Add(layout);
        var vm = new LayoutViewModel(layout, this);
        Items.Add(vm);
        Selected = vm;
        Save();
        Toast(layout.Entries.Count > 0
            ? $"Saved the {layout.Entries.Count} open windows — remove the ones you don't need"
            : "New empty layout — add windows to it", ToastKind.Success);
    }

    public void Apply(LayoutViewModel l) => ApplyLayout(l.Model);

    /// <summary>Applies a layout in the background (launching apps can take a while).</summary>
    public void ApplyLayout(WindowLayout layout)
    {
        Toast($"Arranging \"{layout.Name}\"…");
        _ = Task.Run(() =>
        {
            var problems = Layouts.Apply(layout);
            Toast(problems.Count == 0 ? $"\"{layout.Name}\" applied" : $"\"{layout.Name}\": {string.Join("; ", problems)}",
                problems.Count == 0 ? ToastKind.Success : ToastKind.Error);
        });
    }

    public async void Delete(LayoutViewModel l)
    {
        if (!await Dialogs.ConfirmAsync("Delete layout", $"Delete the layout \"{l.Name}\"?", "Delete", danger: true)) return;
        lock (_store.Data) _store.Data.Layouts.Remove(l.Model);
        Items.Remove(l);
        Selected = Items.FirstOrDefault();
        Save();
        Main.RefreshTriggers();
    }
}
