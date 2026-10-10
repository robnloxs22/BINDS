using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;

namespace DoinkBinds.Views;

/// <summary>
/// Opens a <see cref="PinPointOverlayWindow"/> on every monitor and resolves with the point the
/// user clicked (in OS desktop coordinates), or null if they cancelled.
/// </summary>
public sealed class PinPointPicker(IPlatformServices platform) : IPinPointPicker
{
    private Task<DesktopPoint?>? _active;

    public Task<DesktopPoint?> PickAsync(string? hint = null)
    {
        // Re-entrancy guard: a second request while picking just joins the first.
        if (_active is { IsCompleted: false }) return _active;
        _active = Dispatcher.UIThread.InvokeAsync(() => ShowOverlays(hint));
        return _active;
    }

    private static Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private Task<DesktopPoint?> ShowOverlays(string? hint)
    {
        var tcs = new TaskCompletionSource<DesktopPoint?>();
        var owner = MainWindow;
        var screens = owner?.Screens.All;
        if (owner is null || screens is null || screens.Count == 0)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        var overlays = new List<PinPointOverlayWindow>();

        void Complete(DesktopPoint? result)
        {
            foreach (var o in overlays) o.Close();
            overlays.Clear();
            owner.Activate();
            tcs.TrySetResult(result);
        }

        var cursor = platform.Input.GetCursorPosition();
        PinPointOverlayWindow? focusTarget = null;

        foreach (var screen in screens)
        {
            var overlay = new PinPointOverlayWindow(screen, platform.Input, hint, Complete);
            overlays.Add(overlay);
            overlay.Show();

            // Give keyboard focus to the overlay under the cursor so arrow keys / Esc work immediately.
            var b = screen.Bounds;
            if (OperatingSystem.IsWindows() && cursor.X >= b.X && cursor.X < b.Right && cursor.Y >= b.Y && cursor.Y < b.Bottom)
                focusTarget = overlay;
        }

        (focusTarget ?? overlays[0]).Activate();
        return tcs.Task;
    }
}
