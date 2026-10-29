using DoinkBinds.Models;
using DoinkBinds.Platform;

namespace DoinkBinds.Services;

/// <summary>Visual crosshair picker. Implemented in the UI layer (see Views/PinPointPicker).</summary>
public interface IPinPointPicker
{
    /// <summary>Shows the overlay on every monitor; returns the clicked desktop point or null if cancelled.</summary>
    Task<DesktopPoint?> PickAsync(string? hint = null);
}

/// <summary>Extra reference points for the Window and Found positioning modes.</summary>
public sealed record PositionContext(DesktopPoint? WindowOrigin, DesktopPoint? Found);

/// <summary>
/// Converts between the mouse positioning modes and resolves positions to real
/// desktop coordinates at playback time (multi-monitor aware).
/// </summary>
public interface IMousePositionService
{
    IReadOnlyList<ScreenInfo> GetScreens();
    DesktopPoint GetCursorPosition();

    /// <summary>Builds a position in <paramref name="mode"/> from an absolute desktop point.</summary>
    MousePosition FromAbsolute(DesktopPoint point, MousePositionMode mode, DesktopPoint previous, PositionContext? context = null);

    /// <summary>Resolves a stored position to a desktop point. <paramref name="current"/> is used for Relative mode.</summary>
    DesktopPoint Resolve(MousePosition position, DesktopPoint current, PositionContext? context = null);

    /// <summary>Converts a position to another mode, preserving where it points on the current screen layout.</summary>
    MousePosition Convert(MousePosition position, MousePositionMode newMode, DesktopPoint previous);

    /// <summary>Visual pin-point capture.</summary>
    Task<DesktopPoint?> PickPointAsync(string? hint = null);
}

public sealed class MousePositionService : IMousePositionService
{
    /// <summary>MonitorIndex value meaning "percentage of the whole virtual desktop".</summary>
    public const int VirtualDesktopIndex = -1;

    private readonly IPlatformServices _platform;
    private readonly ISettingsService _settings;
    private readonly IPinPointPicker _picker;

    public MousePositionService(IPlatformServices platform, ISettingsService settings, IPinPointPicker picker)
    {
        _platform = platform;
        _settings = settings;
        _picker = picker;
    }

    public IReadOnlyList<ScreenInfo> GetScreens()
    {
        var screens = _platform.Screens.GetScreens();
        return screens.Count > 0 ? screens : new[] { new ScreenInfo(0, 0, 0, 1920, 1080, true) };
    }

    public DesktopPoint GetCursorPosition() => _platform.Input.GetCursorPosition();

    public Task<DesktopPoint?> PickPointAsync(string? hint = null) => _picker.PickAsync(hint);

    public MousePosition FromAbsolute(DesktopPoint point, MousePositionMode mode, DesktopPoint previous, PositionContext? context = null)
    {
        switch (mode)
        {
            case MousePositionMode.Window:
            {
                var o = context?.WindowOrigin ?? default;
                return new MousePosition { Mode = mode, X = Math.Round(point.X - o.X), Y = Math.Round(point.Y - o.Y) };
            }
            case MousePositionMode.Found:
            {
                var f = context?.Found ?? point;
                return new MousePosition { Mode = mode, X = Math.Round(point.X - f.X), Y = Math.Round(point.Y - f.Y) };
            }

            case MousePositionMode.Relative:
                return new MousePosition { Mode = mode, X = point.X - previous.X, Y = point.Y - previous.Y };

            case MousePositionMode.Percentage:
            {
                var screens = GetScreens();
                if (_settings.Settings.MonitorAwareness == MonitorAwareness.VirtualDesktop)
                {
                    var v = VirtualBounds(screens);
                    return new MousePosition
                    {
                        Mode = mode,
                        X = Pct(point.X, v.X, v.Width),
                        Y = Pct(point.Y, v.Y, v.Height),
                        MonitorIndex = VirtualDesktopIndex
                    };
                }

                var s = ScreenAt(screens, point);
                return new MousePosition
                {
                    Mode = mode,
                    X = Pct(point.X, s.X, s.Width),
                    Y = Pct(point.Y, s.Y, s.Height),
                    MonitorIndex = s.Index
                };
            }

            default: // Absolute + PinPoint
                return new MousePosition { Mode = mode, X = Math.Round(point.X), Y = Math.Round(point.Y), MonitorIndex = ScreenAt(GetScreens(), point).Index };
        }
    }

    public DesktopPoint Resolve(MousePosition position, DesktopPoint current, PositionContext? context = null)
    {
        var screens = GetScreens();
        DesktopPoint p;
        switch (position.Mode)
        {
            case MousePositionMode.Window:
            {
                // Without a window (not found / not specified) the offset is treated as absolute.
                var o = context?.WindowOrigin ?? default;
                p = new DesktopPoint(o.X + position.X, o.Y + position.Y);
                break;
            }
            case MousePositionMode.Found:
            {
                var f = context?.Found ?? current;
                p = new DesktopPoint(f.X + position.X, f.Y + position.Y);
                break;
            }

            case MousePositionMode.Relative:
                p = new DesktopPoint(current.X + position.X, current.Y + position.Y);
                break;

            case MousePositionMode.Percentage:
            {
                ScreenInfo frame;
                if (position.MonitorIndex == VirtualDesktopIndex)
                    frame = VirtualBounds(screens);
                else if (_settings.Settings.MonitorAwareness == MonitorAwareness.PrimaryMonitor)
                    frame = screens[0];
                else
                    frame = screens.FirstOrDefault(s => s.Index == position.MonitorIndex, screens[0]); // monitor unplugged → primary

                p = new DesktopPoint(
                    frame.X + Math.Clamp(position.X, 0, 100) / 100.0 * (frame.Width - 1),
                    frame.Y + Math.Clamp(position.Y, 0, 100) / 100.0 * (frame.Height - 1));
                break;
            }

            default:
                p = new DesktopPoint(position.X, position.Y);
                break;
        }

        return ClampToScreens(screens, p);
    }

    public MousePosition Convert(MousePosition position, MousePositionMode newMode, DesktopPoint previous)
    {
        if (position.Mode == newMode) return position.Clone();
        var absolute = Resolve(position, previous);
        return FromAbsolute(absolute, newMode, previous);
    }

    /// <summary>Bounding box of all monitors.</summary>
    public static ScreenInfo VirtualDesktop(IReadOnlyList<ScreenInfo> screens) => VirtualBounds(screens);

    // ---------- helpers ----------

    private static double Pct(double v, double origin, double size) =>
        size <= 1 ? 0 : Math.Round(Math.Clamp((v - origin) / (size - 1) * 100.0, 0, 100), 3);

    private static ScreenInfo ScreenAt(IReadOnlyList<ScreenInfo> screens, DesktopPoint p) =>
        screens.FirstOrDefault(s => s.Contains(p.X, p.Y), Nearest(screens, p));

    private static ScreenInfo Nearest(IReadOnlyList<ScreenInfo> screens, DesktopPoint p) =>
        screens.OrderBy(s => DistanceSq(s, p)).First();

    private static double DistanceSq(ScreenInfo s, DesktopPoint p)
    {
        var dx = p.X < s.X ? s.X - p.X : p.X >= s.Right ? p.X - s.Right + 1 : 0;
        var dy = p.Y < s.Y ? s.Y - p.Y : p.Y >= s.Bottom ? p.Y - s.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    private static ScreenInfo VirtualBounds(IReadOnlyList<ScreenInfo> screens)
    {
        var x = screens.Min(s => s.X);
        var y = screens.Min(s => s.Y);
        return new ScreenInfo(VirtualDesktopIndex, x, y, screens.Max(s => s.Right) - x, screens.Max(s => s.Bottom) - y, false);
    }

    /// <summary>
    /// Keeps the point on a real monitor. Monitors may have gaps between them (different sizes/offsets),
    /// so clamping to the virtual bounding box alone is not enough.
    /// </summary>
    private static DesktopPoint ClampToScreens(IReadOnlyList<ScreenInfo> screens, DesktopPoint p)
    {
        if (screens.Any(s => s.Contains(p.X, p.Y))) return p;
        var s = Nearest(screens, p);
        return new DesktopPoint(Math.Clamp(p.X, s.X, s.Right - 1), Math.Clamp(p.Y, s.Y, s.Bottom - 1));
    }
}
