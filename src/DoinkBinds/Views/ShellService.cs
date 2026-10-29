using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using DoinkBinds.Models;
using DoinkBinds.Services;

namespace DoinkBinds.Views;

/// <summary>Avalonia implementation of <see cref="IShellService"/>.</summary>
public sealed class ShellService(ILogService log) : IShellService
{
    private StatusIndicatorWindow? _indicator;

    private static Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void ShowIndicator(AppStatus status, string text) => Ui(() =>
    {
        _indicator ??= new StatusIndicatorWindow();
        _indicator.Update(status, text);
        if (!_indicator.IsVisible) _indicator.Show();
    });

    public void HideIndicator() => Ui(() =>
    {
        _indicator?.Close();
        _indicator = null;
    });

    public void MinimizeMainWindow() => Ui(() =>
    {
        if (MainWindow is { } w) w.WindowState = WindowState.Minimized;
    });

    public void RestoreMainWindow() => Ui(() =>
    {
        if (MainWindow is { } w)
        {
            w.WindowState = WindowState.Normal;
            w.Activate();
        }
    });

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", $"\"{path}\"");
            else
                Process.Start("xdg-open", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            log.Error($"Could not open folder {path}", ex);
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", $"\"{path}\"");
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start("xdg-open", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            log.Error($"Could not open {path}", ex);
        }
    }

    public void ShowGuide(object guideViewModel) => Ui(() =>
    {
        var w = new GuideWindow { DataContext = guideViewModel };
        w.Show();
    });

    public void ApplyTheme(string theme) => Ui(() =>
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "System" => ThemeVariant.Default,
                _ => ThemeVariant.Dark
            };
        }
    });

    private static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
