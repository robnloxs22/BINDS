using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.ViewModels;

public sealed class ReportEntryViewModel(ReportEntry entry, string? folder)
{
    public ReportEntry Entry { get; } = entry;
    public bool Passed => Entry.Passed;
    public string Time => Entry.TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Step => $"Step {Entry.StepNumber}";
    public string Description => Entry.Description;
    public string? Message => Entry.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Entry.Message);

    private Bitmap? _thumb;
    private bool _loaded;

    public Bitmap? Screenshot
    {
        get
        {
            if (_loaded) return _thumb;
            _loaded = true;
            if (Entry.Screenshot is null || folder is null) return null;
            try
            {
                var path = Path.Combine(folder, Entry.Screenshot);
                if (File.Exists(path))
                {
                    using var fs = File.OpenRead(path);
                    _thumb = Bitmap.DecodeToWidth(fs, 480);
                }
            }
            catch { /* ignore broken images */ }
            return _thumb;
        }
    }

    public bool HasScreenshot => Entry.Screenshot is not null;
}

public sealed class ReportItemViewModel(RunReport report)
{
    public RunReport Report { get; } = report;
    public string MacroName => Report.MacroName;
    public string Result => Report.Result;
    public bool Passed => Report.Result == "Passed";
    public bool Failed => Report.Result is "Failed" or "Error";
    public string When => Report.StartedUtc.ToLocalTime().ToString("MMM d, HH:mm:ss");
    public string Counts => $"{Report.PassedCount} passed · {Report.FailedCount} failed · {Report.Iterations} loop{(Report.Iterations == 1 ? "" : "s")}";
    public string Duration => Report.FinishedUtc is { } f ? $"{(f - Report.StartedUtc).TotalSeconds:0.0}s" : "";
    public IReadOnlyList<ReportEntryViewModel> Entries { get; } = report.Entries.Select(e => new ReportEntryViewModel(e, report.Folder)).ToList();
    public bool HasEntries => Report.Entries.Count > 0;
}

/// <summary>The Reports page: pass/fail logs of test runs, with screenshots.</summary>
public sealed partial class ReportsPageViewModel : ViewModelBase
{
    private readonly IReportService _reports;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;

    public ObservableCollection<ReportItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ReportItemViewModel? _selected;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Items.Count == 0;

    public ReportsPageViewModel(IReportService reports, IShellService shell, IDialogService dialogs)
    {
        _reports = reports;
        _shell = shell;
        _dialogs = dialogs;
        _reports.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    public void Refresh()
    {
        var selected = Selected?.Report.Folder;
        Items.Clear();
        foreach (var r in _reports.List()) Items.Add(new ReportItemViewModel(r));
        Selected = Items.FirstOrDefault(i => i.Report.Folder == selected) ?? Items.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenHtml()
    {
        if (Selected is { } s) _shell.OpenFile(_reports.HtmlPath(s.Report));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected?.Report.Folder is { } f) _shell.OpenFolder(f);
        else _shell.OpenFolder(_reports.ReportsDirectory);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } s) return;
        _reports.Delete(s.Report);
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        if (Items.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Delete all reports", $"Delete all {Items.Count} run reports and their screenshots?", "Delete all", danger: true)) return;
        _reports.DeleteAll();
    }
}
