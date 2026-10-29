using System.Diagnostics;
using DoinkBinds.Models;
using DoinkBinds.Vision;

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

    /// <summary>Types text directly as Unicode characters (independent of keyboard layout).</summary>
    void TypeText(string text);

    /// <summary>Send keys as hardware scan codes (Windows only; ignored elsewhere).</summary>
    bool UseScanCodes { get; set; }

    /// <summary>Raises timer resolution for accurate playback timing (Windows: timeBeginPeriod).</summary>
    IDisposable BeginHighPrecisionTiming();
}

/// <summary>Foreground window inspection, activation and window management.</summary>
public interface IWindowService
{
    WindowTarget? GetForegroundWindow();
    bool TryActivate(WindowTarget target);
    bool IsOwnProcessForeground();

    /// <summary>All normal top-level windows currently on screen (front-most first).</summary>
    IReadOnlyList<WindowInfo> ListWindows();

    bool Focus(WindowInfo window);

    /// <summary>Moves/resizes so the visible frame matches the given desktop rectangle.</summary>
    bool SetBounds(WindowInfo window, double x, double y, double width, double height);

    bool SetState(WindowInfo window, WindowStateKind state);
    bool Close(WindowInfo window);
}

/// <summary>Reads pixels from the screen.</summary>
public interface IScreenCapture
{
    /// <summary>Captures a desktop rectangle. Returns null if capturing isn't possible (e.g. permission missing).</summary>
    BgraImage? Capture(double x, double y, double width, double height);
}

/// <summary>Shared window-matching rules so every platform behaves the same.</summary>
public static class WindowMatching
{
    /// <summary>Process must match; then an exact title wins, then a title containing the target title, then any window of the process.</summary>
    public static WindowInfo? Find(IReadOnlyList<WindowInfo> windows, WindowTarget target, string? titleContains = null)
    {
        var candidates = windows.Where(w =>
            string.IsNullOrWhiteSpace(target.ProcessName) ||
            string.Equals(w.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrWhiteSpace(titleContains))
            return candidates.FirstOrDefault(w => w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(target.ProcessName) && string.IsNullOrWhiteSpace(target.Title)) return null;

        if (!string.IsNullOrEmpty(target.Title))
        {
            var exact = candidates.FirstOrDefault(w => w.Title == target.Title);
            if (exact is not null) return exact;
            var partial = candidates.FirstOrDefault(w =>
                w.Title.Contains(target.Title, StringComparison.OrdinalIgnoreCase) ||
                (w.Title.Length > 0 && target.Title.Contains(w.Title, StringComparison.OrdinalIgnoreCase)));
            if (partial is not null) return partial;
            if (string.IsNullOrWhiteSpace(target.ProcessName)) return null;
        }
        return candidates.FirstOrDefault();
    }
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
    IScreenCapture Capture { get; }

    /// <summary>Plays the system alert sound.</summary>
    void Beep();

    /// <summary>Returns a user-facing message if OS permissions are missing, otherwise null.</summary>
    string? CheckPermissions(bool prompt);
}
