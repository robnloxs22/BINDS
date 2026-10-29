using System.Diagnostics;
using System.Runtime.CompilerServices;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;
using DoinkBinds.Vision;

namespace DoinkBinds.Tests;

internal static class TestSetup
{
    /// <summary>Point the app's data folder at a temp directory before AppPaths is first touched.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doink-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DOINK_DATA_DIR", dir);
    }
}

/// <summary>In-memory platform: synchronous hook, recording simulator, two monitors.</summary>
public sealed class FakePlatform : IPlatformServices
{
    public string Name => "Fake";
    public bool IsSupported => true;
    public FakeHook FakeHook { get; } = new();
    public FakeSimulator FakeInput { get; } = new();
    public FakeWindows FakeWindows { get; } = new();
    public FakeScreens FakeScreens { get; } = new();
    public FakeCapture FakeCapture { get; } = new();
    public IScreenCapture Capture => FakeCapture;
    public int Beeps;
    public void Beep() => Interlocked.Increment(ref Beeps);
    public IInputHook Hook => FakeHook;
    public IInputSimulator Input => FakeInput;
    public IWindowService Windows => FakeWindows;
    public IScreenService Screens => FakeScreens;
    public string? CheckPermissions(bool prompt) => null;
}

public sealed class FakeHook : IInputHook
{
    public event Action<RawInputEvent>? InputReceived;
    public bool IsRunning { get; private set; }
    public bool Start(out string? error) { error = null; IsRunning = true; return true; }
    public void Stop() => IsRunning = false;
    public void Dispose() { }

    private long _clockMs;

    /// <summary>Raises an event "<paramref name="afterMs"/>" milliseconds after the previous one.</summary>
    public void Raise(RawInputKind kind, int afterMs = 0, double x = 0, double y = 0, int key = 0,
        MacroMouseButton button = MacroMouseButton.Left, int wheel = 0, bool synthetic = false)
    {
        if (_clockMs == 0) _clockMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        _clockMs += afterMs;
        var ts = _clockMs * Stopwatch.Frequency / 1000;
        InputReceived?.Invoke(new RawInputEvent(kind, ts, x, y, button, wheel, false, key, synthetic));
    }
}

public sealed class FakeSimulator : IInputSimulator
{
    public readonly List<string> Log = new();
    public DesktopPoint Cursor;
    public bool UseScanCodes { get; set; }

    public DesktopPoint GetCursorPosition() => Cursor;

    public void MoveMouse(double x, double y)
    {
        Cursor = new DesktopPoint(x, y);
        lock (Log) Log.Add($"move {x:0.##},{y:0.##}");
    }

    public void MouseButton(MacroMouseButton button, bool down) { lock (Log) Log.Add($"{button} {(down ? "down" : "up")}"); }
    public void MouseWheel(int delta, bool horizontal) { lock (Log) Log.Add($"wheel {delta}"); }
    public void Key(int keyCode, bool down) { lock (Log) Log.Add($"key {keyCode:X2} {(down ? "down" : "up")}"); }
    public void TypeText(string text) { lock (Log) Log.Add($"type {text}"); }
    public IDisposable BeginHighPrecisionTiming() => new Noop();

    public List<string> Snapshot() { lock (Log) return Log.ToList(); }

    private sealed class Noop : IDisposable { public void Dispose() { } }
}

public sealed class FakeWindows : IWindowService
{
    public WindowTarget? Foreground = new() { ProcessName = "notepad", Title = "Untitled" };
    public bool OwnForeground;
    public WindowTarget? GetForegroundWindow() => Foreground?.Clone();
    public bool TryActivate(WindowTarget target) { Foreground = target.Clone(); return true; }
    public bool IsOwnProcessForeground() => OwnForeground;

    public List<WindowInfo> Windows = new();
    public readonly List<string> Ops = new();
    public IReadOnlyList<WindowInfo> ListWindows() => Windows.ToList();
    public bool Focus(WindowInfo window) { Ops.Add($"focus {window.ProcessName}"); Foreground = window.ToTarget(); return true; }
    public bool SetBounds(WindowInfo window, double x, double y, double width, double height)
    {
        Ops.Add($"bounds {window.ProcessName} {x},{y},{width},{height}");
        var i = Windows.FindIndex(w => w.Handle == window.Handle);
        if (i >= 0) Windows[i] = window with { X = x, Y = y, Width = width, Height = height };
        return true;
    }
    public bool SetState(WindowInfo window, WindowStateKind state) { Ops.Add($"state {window.ProcessName} {state}"); return true; }
    public bool Close(WindowInfo window) { Ops.Add($"close {window.ProcessName}"); Windows.RemoveAll(w => w.Handle == window.Handle); return true; }
}

/// <summary>A fake "screen": an image placed at desktop (0,0). Capture crops it.</summary>
public sealed class FakeCapture : IScreenCapture
{
    public DoinkBinds.Vision.BgraImage? Screen;
    public int Captures;

    public DoinkBinds.Vision.BgraImage? Capture(double x, double y, double width, double height)
    {
        Interlocked.Increment(ref Captures);
        var s = Screen;
        if (s is null) return null;
        return s.Crop((int)x, (int)y, (int)width, (int)height);
    }
}

/// <summary>Primary 1920×1080 at (0,0) and a 2560×1440 monitor to its left at (-2560, -200).</summary>
public sealed class FakeScreens : IScreenService
{
    public List<ScreenInfo> List = new()
    {
        new ScreenInfo(0, 0, 0, 1920, 1080, true),
        new ScreenInfo(1, -2560, -200, 2560, 1440, false)
    };
    public IReadOnlyList<ScreenInfo> GetScreens() => List;
}

public sealed class NullPicker : IPinPointPicker
{
    public Task<DesktopPoint?> PickAsync(string? hint = null) => Task.FromResult<DesktopPoint?>(null);
}

public sealed class NullLog : ILogService
{
    public void Info(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

public sealed class FakeNotifier : INotificationService
{
    public readonly List<string> Messages = new();
    public void Notify(string title, string message, ToastKind kind = ToastKind.Info) { lock (Messages) Messages.Add(message); }
}

public sealed class MemorySettings : ISettingsService
{
    public AppSettings Settings { get; } = new();
    public event EventHandler? SettingsChanged;
    public void Save() { }
    public void NotifyChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Shares one loaded OCR engine across tests (model loading takes ~1 s).</summary>
public sealed class LazyOcr : DoinkBinds.Vision.IOcrEngine
{
    public IReadOnlyList<DoinkBinds.Vision.OcrLine> Recognize(DoinkBinds.Vision.BgraImage image) => Harness.SharedOcr.Value.Recognize(image);
}

/// <summary>Wires the real services onto the fake platform.</summary>
public sealed class Harness
{
    public FakePlatform Platform { get; } = new();
    public MemorySettings Settings { get; } = new();
    public HotkeyService Hotkeys { get; }
    public MousePositionService Positions { get; }
    public MacroRecorderService Recorder { get; }
    public MacroPlayerService Player { get; }
    public ScreenVision Vision { get; }
    public VariableStore Variables { get; } = new();
    public MacroLibraryService Library { get; } = new(new NullLog());
    public LayoutService Layouts { get; }
    public AutomationStore Automation { get; } = new(new NullLog());
    public ReportService Reports { get; }
    public FakeNotifier Notifier { get; } = new();
    public static readonly Lazy<PaddleOcrEngine> SharedOcr = new(() => new PaddleOcrEngine());

    public Harness()
    {
        Hotkeys = new HotkeyService(Platform, Settings);
        Positions = new MousePositionService(Platform, Settings, new NullPicker());
        Recorder = new MacroRecorderService(Platform, Hotkeys, Positions, Settings);
        Vision = new ScreenVision(Platform, Positions, new LazyOcr(), new NullLog());
        Layouts = new LayoutService(Platform, new NullLog());
        Reports = new ReportService(Settings, new NullLog());
        Player = new MacroPlayerService(Platform, Positions, Settings, new NullLog(), Vision, Variables, Library,
            Layouts, Automation, Reports, Notifier);
        Platform.FakeHook.Start(out _); // the real app starts the hook during MainWindowViewModel.Initialize
    }

    /// <summary>Plays and blocks until finished (with a safety timeout).</summary>
    public PlaybackResult PlayAndWait(Macro macro, PlaybackSettings? settings = null, int timeoutMs = 10_000)
    {
        using var done = new ManualResetEventSlim();
        PlaybackResult? result = null;
        void OnFinished(object? s, PlaybackResult r) { result = r; done.Set(); }
        Player.Finished += OnFinished;
        try
        {
            Assert.True(Player.Play(macro, settings ?? macro.Playback));
            Assert.True(done.Wait(timeoutMs), "playback did not finish in time");
            return result!;
        }
        finally
        {
            Player.Finished -= OnFinished;
        }
    }
}
