using System.Diagnostics;
using DoinkBinds.Models;

namespace DoinkBinds.Platform;

public enum RawInputKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    KeyDown,
    KeyUp
}

/// <summary>
/// A low-level input event captured by the OS hook. <see cref="Timestamp"/> is taken inside the
/// hook callback (Stopwatch ticks) so that queueing latency never distorts recorded timings.
/// </summary>
public readonly record struct RawInputEvent(
    RawInputKind Kind,
    long Timestamp,
    double X = 0,
    double Y = 0,
    MacroMouseButton Button = MacroMouseButton.Left,
    int WheelDelta = 0,
    bool Horizontal = false,
    int KeyCode = 0,
    bool IsSynthetic = false)
{
    public static long Now() => Stopwatch.GetTimestamp();

    public bool IsKey => Kind is RawInputKind.KeyDown or RawInputKind.KeyUp;
}

/// <summary>Global (system-wide) keyboard + mouse hook.</summary>
public interface IInputHook : IDisposable
{
    /// <summary>
    /// Raised on a dedicated background thread (never the OS hook thread) for every input event.
    /// <see cref="RawInputEvent.IsSynthetic"/> is true for events injected by Doink Binds itself.
    /// </summary>
    event Action<RawInputEvent>? InputReceived;

    bool IsRunning { get; }

    /// <summary>Starts the hook. Returns false (with a reason) if the OS refused, e.g. missing permission.</summary>
    bool Start(out string? error);

    void Stop();
}

/// <summary>Injects synthetic input into the OS.</summary>
public interface IInputSimulator
{
    DesktopPoint GetCursorPosition();
    void MoveMouse(double x, double y);
    void MouseButton(MacroMouseButton button, bool down);
    void MouseWheel(int delta, bool horizontal);
    void Key(int keyCode, bool down);

    /// <summary>Send keys as hardware scan codes (Windows only; ignored elsewhere).</summary>
    bool UseScanCodes { get; set; }

    /// <summary>Raises timer resolution for accurate playback timing (Windows: timeBeginPeriod).</summary>
    IDisposable BeginHighPrecisionTiming();
}

/// <summary>Foreground window inspection and activation.</summary>
public interface IWindowService
{
    WindowTarget? GetForegroundWindow();
    bool TryActivate(WindowTarget target);
    bool IsOwnProcessForeground();
}

public interface IScreenService
{
    /// <summary>All monitors in global desktop coordinates. Index 0 is always the primary monitor.</summary>
    IReadOnlyList<ScreenInfo> GetScreens();
}

/// <summary>Bundle of all OS-specific services. One implementation per supported OS.</summary>
public interface IPlatformServices
{
    string Name { get; }

    /// <summary>False on platforms where hooking/injection is not implemented (UI still works).</summary>
    bool IsSupported { get; }

    IInputHook Hook { get; }
    IInputSimulator Input { get; }
    IWindowService Windows { get; }
    IScreenService Screens { get; }

    /// <summary>Returns a user-facing message if OS permissions are missing, otherwise null.</summary>
    string? CheckPermissions(bool prompt);
}
