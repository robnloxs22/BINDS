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
    public IScreenCapture Capture { get; } = new WindowsScreenCapture();

    public void Beep() => MessageBeep(0x40 /* MB_ICONASTERISK */);

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
        return hwnd != IntPtr.Zero && Activate(hwnd);
    }

    private static bool Activate(IntPtr hwnd)
    {
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

    // ------------------------------------------------------------------ window management

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = GetTitle(hwnd);
            if (title.Length == 0) return true;

            // Skip tool windows and "cloaked" (hidden UWP / other virtual desktop) windows.
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            var r = VisibleBounds(hwnd);
            list.Add(new WindowInfo(hwnd, (int)pid, GetProcessName(pid), title,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IsIconic(hwnd), IsZoomed(hwnd)));
            return true;
        }, IntPtr.Zero);
        return list; // EnumWindows already enumerates in Z-order, front-most first
    }

    /// <summary>The visible frame (without the invisible resize borders Windows 10/11 add).</summary>
    private static RECT VisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
            return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public bool Focus(WindowInfo window) => Activate(window.Handle);

    public bool SetBounds(WindowInfo window, double x, double y, double width, double height)
    {
        var hwnd = window.Handle;
        if (IsIconic(hwnd) || IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // SetWindowPos works on the outer rectangle; compensate for the invisible borders so the
        // *visible* frame lands exactly where requested.
        GetWindowRect(hwnd, out var outer);
        var visible = VisibleBounds(hwnd);
        var left = visible.Left - outer.Left;
        var top = visible.Top - outer.Top;
        var right = outer.Right - visible.Right;
        var bottom = outer.Bottom - visible.Bottom;

        return SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(x) - left, (int)Math.Round(y) - top,
            (int)Math.Round(width) + left + right, (int)Math.Round(height) + top + bottom,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public bool SetState(WindowInfo window, WindowStateKind state) => ShowWindow(window.Handle, state switch
    {
        WindowStateKind.Maximized => SW_MAXIMIZE,
        WindowStateKind.Minimized => SW_MINIMIZE,
        _ => SW_RESTORE
    });

    public bool Close(WindowInfo window) => PostMessage(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    private string GetProcessName(uint pid) => _processNames.GetOrAdd(pid, static p =>
    {
        try { using var proc = Process.GetProcessById((int)p); return proc.ProcessName; }
        catch { return ""; }
    });
}

/// <summary>GDI BitBlt capture from the virtual-screen DC (works across all monitors, physical pixels).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCapture : IScreenCapture
{
    public Vision.BgraImage? Capture(double x, double y, double width, double height)
    {
        var w = Math.Max(1, (int)Math.Round(width));
        var h = Math.Max(1, (int)Math.Round(height));
        var ix = (int)Math.Round(x);
        var iy = (int)Math.Round(y);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32
        };
        var bmp = CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memDc, bmp);
        try
        {
            if (bmp == IntPtr.Zero || !BitBlt(memDc, 0, 0, w, h, screenDc, ix, iy, SRCCOPY | CAPTUREBLT))
                return null;
            var pixels = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha undefined
            return new Vision.BgraImage(w, h, pixels) { OriginX = ix, OriginY = iy, Scale = 1 };
        }
        finally
        {
            SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
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
