using System.Diagnostics;
using DoinkBinds.Models;
using DoinkBinds.Platform;

namespace DoinkBinds.Services.Automation;

/// <summary>Saves and restores window arrangements ("layouts"), launching apps that aren't running.</summary>
public interface ILayoutService
{
    /// <summary>Snapshot of the currently visible windows (excluding Doink Binds).</summary>
    WindowLayout CaptureCurrent(string name);

    /// <summary>Applies a layout. Returns a list of problems (empty = everything placed).</summary>
    IReadOnlyList<string> Apply(WindowLayout layout, CancellationToken ct = default);

    /// <summary>Starts an app / file / URL (shell execute). Returns an error message or null.</summary>
    string? Launch(string command, string? arguments = null);
}

public sealed class LayoutService : ILayoutService
{
    private readonly IPlatformServices _platform;
    private readonly ILogService _log;

    public LayoutService(IPlatformServices platform, ILogService log)
    {
        _platform = platform;
        _log = log;
    }

    public WindowLayout CaptureCurrent(string name)
    {
        var own = Process.GetCurrentProcess().ProcessName;
        var layout = new WindowLayout { Name = name };
        foreach (var w in _platform.Windows.ListWindows())
        {
            if (string.Equals(w.ProcessName, own, StringComparison.OrdinalIgnoreCase) || w.IsMinimized) continue;
            layout.Entries.Add(new WindowLayoutEntry
            {
                ProcessName = w.ProcessName,
                TitleContains = "",
                LaunchCommand = "",
                X = w.X,
                Y = w.Y,
                Width = w.Width,
                Height = w.Height,
                State = w.IsMaximized ? WindowStateKind.Maximized : WindowStateKind.Normal
            });
        }
        return layout;
    }

    public IReadOnlyList<string> Apply(WindowLayout layout, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var used = new HashSet<nint>();

        // 1. Launch everything that's missing first, so apps start in parallel.
        var pending = new List<WindowLayoutEntry>();
        foreach (var e in layout.Entries)
        {
            if (Find(e, used, reserve: false) is null && !string.IsNullOrWhiteSpace(e.LaunchCommand))
            {
                var err = Launch(e.LaunchCommand);
                if (err is not null) problems.Add($"{e.ProcessName}: {err}");
                else pending.Add(e);
            }
        }

        // 2. Wait (up to LaunchWaitSeconds) for launched apps to show a window.
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, layout.LaunchWaitSeconds));
        while (pending.Count > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            pending.RemoveAll(e => Find(e, used, reserve: false) is not null);
            if (pending.Count > 0) Thread.Sleep(250);
        }

        // 3. Place each window.
        foreach (var e in layout.Entries)
        {
            if (ct.IsCancellationRequested) break;
            var w = Find(e, used, reserve: true);
            if (w is null)
            {
                problems.Add($"{Describe(e)}: window not found");
                continue;
            }
            try
            {
                if (e.State == WindowStateKind.Minimized)
                {
                    _platform.Windows.SetState(w, WindowStateKind.Minimized);
                    continue;
                }
                if (!_platform.Windows.SetBounds(w, e.X, e.Y, e.Width, e.Height))
                    problems.Add($"{Describe(e)}: could not move the window" + (OperatingSystem.IsMacOS() ? " (Accessibility permission?)" : ""));
                if (e.State == WindowStateKind.Maximized) _platform.Windows.SetState(w, WindowStateKind.Maximized);
            }
            catch (Exception ex)
            {
                problems.Add($"{Describe(e)}: {ex.Message}");
            }
        }
        return problems;
    }

    private static string Describe(WindowLayoutEntry e) =>
        string.IsNullOrEmpty(e.TitleContains) ? e.ProcessName : $"{e.ProcessName} ({e.TitleContains})";

    private WindowInfo? Find(WindowLayoutEntry e, HashSet<nint> used, bool reserve)
    {
        var w = _platform.Windows.ListWindows().FirstOrDefault(x =>
            !used.Contains(x.Handle) &&
            string.Equals(x.ProcessName, e.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(e.TitleContains) || x.Title.Contains(e.TitleContains, StringComparison.OrdinalIgnoreCase)));
        if (w is not null && reserve) used.Add(w.Handle);
        return w;
    }

    public string? Launch(string command, string? arguments = null)
    {
        try
        {
            var psi = new ProcessStartInfo(command.Trim().Trim('"')) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"Launch failed: {command}", ex);
            return ex.Message;
        }
    }
}
