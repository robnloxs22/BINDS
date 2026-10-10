using DoinkBinds.Models;

namespace DoinkBinds.Platform.Null;

/// <summary>
/// Fallback for unsupported operating systems (e.g. Linux). The UI and macro library work,
/// but nothing is hooked or injected.
/// </summary>
public sealed class NullPlatformServices : IPlatformServices
{
    public string Name => "Unsupported OS";
    public bool IsSupported => false;
    public IInputHook Hook { get; } = new NullHook();
    public IInputSimulator Input { get; } = new NullSimulator();
    public IWindowService Windows { get; } = new NullWindows();
    public IScreenService Screens { get; } = new NullScreens();

    public string? CheckPermissions(bool prompt) =>
        "Recording and playback are only supported on Windows and macOS. You can still browse and edit macros.";

    private sealed class NullHook : InputHookBase
    {
        protected override bool StartHook(out string? error)
        {
            error = "Global input hooks are not supported on this OS.";
            return false;
        }

        protected override void StopHook() { }
    }

    private sealed class NullSimulator : IInputSimulator
    {
        private DesktopPoint _pos;
        public DesktopPoint GetCursorPosition() => _pos;
        public void MoveMouse(double x, double y) => _pos = new DesktopPoint(x, y);
        public void MouseButton(MacroMouseButton button, bool down) { }
        public void MouseWheel(int delta, bool horizontal) { }
        public void Key(int keyCode, bool down) { }
        public bool UseScanCodes { get; set; }
        public IDisposable BeginHighPrecisionTiming() => new NoopDisposable();
    }

    private sealed class NullWindows : IWindowService
    {
        public WindowTarget? GetForegroundWindow() => null;
        public bool TryActivate(WindowTarget target) => false;
        public bool IsOwnProcessForeground() => false;
    }

    private sealed class NullScreens : IScreenService
    {
        public IReadOnlyList<ScreenInfo> GetScreens() => new[] { new ScreenInfo(0, 0, 0, 1920, 1080, true) };
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public void Dispose() { }
}
