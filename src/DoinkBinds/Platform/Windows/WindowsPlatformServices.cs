using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DoinkBinds.Models;
using static DoinkBinds.Platform.Windows.Win32;

namespace DoinkBinds.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformServices : IPlatformServices
{
    public string Name => "Windows";
    public bool IsSupported => true;
    public IInputHook Hook { get; } = new WindowsInputHook();
    public IInputSimulator Input { get; } = new WindowsInputSimulator();
    public IWindowService Windows { get; } = new WindowsWindowService();
    public IScreenService Screens { get; } = new WindowsScreenService();

    // Windows needs no special permission. (Injecting into elevated apps requires running elevated.)
    public string? CheckPermissions(bool prompt) => null;
}

[SupportedOSPlatform("windows")]
public sealed class WindowsWindowService : IWindowService
{
    private static readonly int OwnPid = Environment.ProcessId;

    // Process.GetProcessById is comparatively slow; cache names by PID during a session.
    private readonly ConcurrentDictionary<uint, string> _processNames = new();

    public WindowTarget? GetForegroundWindow()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowTarget { ProcessName = GetProcessName(pid), Title = GetTitle(hwnd) };
    }

    public bool IsOwnProcessForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnPid;
    }

    public bool TryActivate(WindowTarget target)
    {
        var hwnd = FindWindow(target);
        if (hwnd == IntPtr.Zero) return false;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // Windows blocks SetForegroundWindow from background processes ("foreground lock").
        // Attaching our input queue to the current foreground thread lifts that restriction.
        var fg = Win32.GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var myThread = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }

        if (Win32.GetForegroundWindow() == hwnd) return true;

        // Fallback: Windows allows a foreground change right after the caller "received the last
        // input event". A tagged (ignored-by-us) Alt tap around the call satisfies that rule.
        TapAlt(down: true);
        SetForegroundWindow(hwnd);
        TapAlt(down: false);
        return Win32.GetForegroundWindow() == hwnd;
    }

    private static void TapAlt(bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0x12, // VK_MENU
                    dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                    dwExtraInfo = DoinkMagic
                }
            }
        };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>Finds the best visible top-level window: exact title match first, then any window of the process.</summary>
    private IntPtr FindWindow(WindowTarget target)
    {
        IntPtr exact = IntPtr.Zero, sameProcess = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = GetTitle(hwnd);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!string.Equals(GetProcessName(pid), target.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (title == target.Title) { exact = hwnd; return false; }
            if (sameProcess == IntPtr.Zero) sameProcess = hwnd;
            return true;
        }, IntPtr.Zero);
        return exact != IntPtr.Zero ? exact : sameProcess;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private string GetProcessName(uint pid) => _processNames.GetOrAdd(pid, static p =>
    {
        try { using var proc = Process.GetProcessById((int)p); return proc.ProcessName; }
        catch { return ""; }
    });
}

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenService : IScreenService
{
    public IReadOnlyList<ScreenInfo> GetScreens()
    {
        var raw = new List<(RECT rect, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref info))
                raw.Add((info.rcMonitor, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        if (raw.Count == 0)
            return new[] { new ScreenInfo(0, 0, 0, 1920, 1080, true) };

        // Stable ordering: primary first, then left-to-right, top-to-bottom.
        return raw
            .OrderByDescending(r => r.primary)
            .ThenBy(r => r.rect.Left)
            .ThenBy(r => r.rect.Top)
            .Select((r, i) => new ScreenInfo(i, r.rect.Left, r.rect.Top,
                r.rect.Right - r.rect.Left, r.rect.Bottom - r.rect.Top, r.primary))
            .ToList();
    }
}
