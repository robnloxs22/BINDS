using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.Vision;

namespace DoinkBinds.Views;

/// <summary>
/// All on-screen pickers:
///  • <see cref="PickAsync"/> — live crosshair overlay on every monitor (pin-point positions)
///  • <see cref="PickRegionAsync"/> — snipping-tool style box on a frozen screenshot (image templates, OCR areas)
///  • <see cref="PickColorAsync"/> — click a pixel on a frozen screenshot with a magnifier
/// Doink Binds' own windows are hidden while picking so they never get in the way (or in the screenshot).
/// </summary>
public sealed class PinPointPicker(IPlatformServices platform) : IScreenPicker
{
    private Task? _active;

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    public Task<DesktopPoint?> PickAsync(string? hint = null) =>
        Guard(() => WithOwnWindowsHidden(async () =>
        {
            var tcs = new TaskCompletionSource<DesktopPoint?>();
            var overlays = new List<Window>();
            var cursor = platform.Input.GetCursorPosition();
            foreach (var screen in Screens())
            {
                var o = new PinPointOverlayWindow(screen, platform.Input, hint, r =>
                {
                    foreach (var w in overlays) w.Close();
                    tcs.TrySetResult(r);
                });
                overlays.Add(o);
                o.Show();
            }
            Focus(overlays, cursor);
            return await tcs.Task;
        }, hideDelayMs: 120));

    public Task<RegionPick?> PickRegionAsync(string? hint = null) =>
        Guard(() => WithOwnWindowsHidden(async () =>
        {
            var frozen = CaptureDesktop();
            var result = await ShowFrozen(RegionPickerWindow.PickMode.Region, frozen, hint);
            if (result is not ScreenRegion region) return null;

            // Crop from the frozen screenshot when we have one (exactly what the user saw);
            // otherwise capture the area live now that the overlay is gone.
            BgraImage? image = null;
            try
            {
                image = frozen?.CropDesktop(region.X, region.Y, region.Width, region.Height)
                        ?? platform.Capture.Capture(region.X, region.Y, region.Width, region.Height);
            }
            catch
            {
                // capture is best effort
            }
            return new RegionPick(region, image);
        }, hideDelayMs: 250));

    public Task<ColorPick?> PickColorAsync(string? hint = null) =>
        Guard(() => WithOwnWindowsHidden(async () =>
        {
            var frozen = CaptureDesktop();
            var result = await ShowFrozen(RegionPickerWindow.PickMode.Color, frozen, hint);
            if (result is not DesktopPoint p) return null;

            var img = frozen ?? platform.Capture.Capture(Math.Floor(p.X), Math.Floor(p.Y), 1, 1);
            if (img is null) return new ColorPick(p, "#000000");
            var px = Math.Clamp((int)Math.Round((p.X - img.OriginX) * img.Scale), 0, img.Width - 1);
            var py = Math.Clamp((int)Math.Round((p.Y - img.OriginY) * img.Scale), 0, img.Height - 1);
            var (r, g, b) = img.GetPixel(px, py);
            return new ColorPick(p, BgraImage.ToHex(r, g, b));
        }, hideDelayMs: 250));

    // ------------------------------------------------------------------ helpers

    /// <summary>Only one picker at a time; a second request while picking waits for (and ignores) the first.</summary>
    private Task<T?> Guard<T>(Func<Task<T?>> start)
    {
        if (_active is { IsCompleted: false }) return Task.FromResult<T?>(default);
        var t = Dispatcher.UIThread.InvokeAsync(start);
        _active = t;
        return t;
    }

    private IReadOnlyList<Screen> Screens()
    {
        var anyWindow = Desktop?.MainWindow ?? Desktop?.Windows.FirstOrDefault();
        return anyWindow?.Screens.All ?? Array.Empty<Screen>();
    }

    private BgraImage? CaptureDesktop()
    {
        try
        {
            var v = MousePositionService.VirtualDesktop(platform.Screens.GetScreens());
            return platform.Capture.Capture(v.X, v.Y, v.Width, v.Height);
        }
        catch
        {
            return null;
        }
    }

    private async Task<object?> ShowFrozen(RegionPickerWindow.PickMode mode, BgraImage? frozen, string? hint)
    {
        var tcs = new TaskCompletionSource<object?>();
        var overlays = new List<Window>();
        var avaloniaScreens = Screens();
        var platformScreens = platform.Screens.GetScreens();
        var cursor = platform.Input.GetCursorPosition();

        // Pair Avalonia screens with platform screens (both sorted left→right, top→bottom) so each
        // overlay can show the frozen pixels of its own monitor.
        var aSorted = avaloniaScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        var pSorted = platformScreens.OrderBy(s => s.X).ThenBy(s => s.Y).ToList();
        var paired = aSorted.Count == pSorted.Count;

        for (var i = 0; i < aSorted.Count; i++)
        {
            BgraImage? background = null;
            if (frozen is not null && paired)
            {
                var ps = pSorted[i];
                try { background = frozen.CropDesktop(ps.X, ps.Y, ps.Width, ps.Height); } catch { /* no background */ }
            }
            var o = new RegionPickerWindow(aSorted[i], mode, platform.Input, background, frozen, hint, r =>
            {
                foreach (var w in overlays) w.Close();
                tcs.TrySetResult(r);
            });
            overlays.Add(o);
            o.Show();
        }
        if (overlays.Count == 0) return null;
        Focus(overlays, cursor);
        return await tcs.Task;
    }

    private static void Focus(List<Window> overlays, DesktopPoint cursor)
    {
        // Keyboard focus goes to the overlay under the cursor (Windows uses the same pixel coordinates).
        var target = overlays.FirstOrDefault(o =>
            OperatingSystem.IsWindows() && o.Position.X <= cursor.X && cursor.X < o.Position.X + o.Width * o.RenderScaling &&
            o.Position.Y <= cursor.Y && cursor.Y < o.Position.Y + o.Height * o.RenderScaling) ?? overlays.FirstOrDefault();
        target?.Activate();
    }

    /// <summary>Hides every visible Doink Binds window, runs the picker, then restores them.</summary>
    private static async Task<T?> WithOwnWindowsHidden<T>(Func<Task<T?>> pick, int hideDelayMs)
    {
        var hidden = Desktop?.Windows
            .Where(w => w.IsVisible && w is not PinPointOverlayWindow and not RegionPickerWindow
                        and not NotificationWindow and not StatusIndicatorWindow)
            .ToList() ?? new List<Window>();
        foreach (var w in hidden) w.Hide();
        try
        {
            if (hidden.Count > 0) await Task.Delay(hideDelayMs); // let the window manager repaint behind us
            return await pick();
        }
        finally
        {
            foreach (var w in hidden) w.Show();
            hidden.FirstOrDefault()?.Activate();
        }
    }
}
