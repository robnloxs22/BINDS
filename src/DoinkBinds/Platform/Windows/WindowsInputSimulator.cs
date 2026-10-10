using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DoinkBinds.Models;
using static DoinkBinds.Platform.Windows.Win32;

namespace DoinkBinds.Platform.Windows;

/// <summary>Injects input with SendInput. Every event is tagged with <see cref="Win32.DoinkMagic"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInputSimulator : IInputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public bool UseScanCodes { get; set; }

    public DesktopPoint GetCursorPosition() =>
        GetCursorPos(out var p) ? new DesktopPoint(p.X, p.Y) : default;

    public void MoveMouse(double x, double y)
    {
        var ix = (int)Math.Round(x);
        var iy = (int)Math.Round(y);

        // Absolute moves across the whole virtual desktop are expressed in 0..65535 units.
        // That is what games / raw-input apps see, so we send a real input event first...
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
        var vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((ix - vx) * 65535.0 / vw),
                    dy = (int)Math.Round((iy - vy) * 65535.0 / vh),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = DoinkMagic
                }
            }
        };
        Send(input);

        // ...then snap to the exact pixel, since the 65535 normalization can be off by one.
        if (GetCursorPos(out var p) && (p.X != ix || p.Y != iy))
            SetCursorPos(ix, iy);
    }

    public void MouseButton(MacroMouseButton button, bool down)
    {
        uint flags;
        uint data = 0;
        switch (button)
        {
            case MacroMouseButton.Left: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case MacroMouseButton.Right: flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case MacroMouseButton.Middle: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case MacroMouseButton.X1: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON1; break;
            default: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON2; break;
        }

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = DoinkMagic } }
        });
    }

    public void MouseWheel(int delta, bool horizontal)
    {
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    mouseData = unchecked((uint)delta),
                    dwExtraInfo = DoinkMagic
                }
            }
        });
    }

    public void Key(int keyCode, bool down)
    {
        var scan = (ushort)MapVirtualKey((uint)keyCode, 0 /* MAPVK_VK_TO_VSC */);
        uint flags = down ? 0 : KEYEVENTF_KEYUP;
        if (IsExtendedKey(keyCode)) flags |= KEYEVENTF_EXTENDEDKEY;

        var ki = new KEYBDINPUT { wVk = (ushort)keyCode, wScan = scan, dwFlags = flags, dwExtraInfo = DoinkMagic };
        if (UseScanCodes && scan != 0)
        {
            // Many games read DirectInput / raw scan codes and ignore virtual-key-only input.
            ki.wVk = 0;
            ki.dwFlags |= KEYEVENTF_SCANCODE;
        }

        Send(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = ki } });
    }

    /// <summary>Keys that live on the "extended" part of the keyboard need KEYEVENTF_EXTENDEDKEY.</summary>
    private static bool IsExtendedKey(int vk) => vk is
        KeyCodes.Insert or KeyCodes.Delete or KeyCodes.Home or KeyCodes.End or KeyCodes.PageUp or KeyCodes.PageDown or
        KeyCodes.Left or KeyCodes.Right or KeyCodes.Up or KeyCodes.Down or KeyCodes.RControl or KeyCodes.RAlt or
        KeyCodes.NumLock or KeyCodes.Divide or KeyCodes.PrintScreen or KeyCodes.LWin or KeyCodes.RWin or KeyCodes.Apps or
        KeyCodes.VolumeMute or KeyCodes.VolumeDown or KeyCodes.VolumeUp or KeyCodes.MediaNext or KeyCodes.MediaPrev or
        KeyCodes.MediaStop or KeyCodes.MediaPlayPause;

    private static void Send(INPUT input)
    {
        var sent = SendInput(1, new[] { input }, InputSize);
        if (sent == 0)
        {
            // Most common cause: target window runs elevated (UIPI). Surface it to the debugger only;
            // the player keeps going rather than aborting a long macro.
            System.Diagnostics.Debug.WriteLine($"SendInput failed: {Marshal.GetLastWin32Error()}");
        }
    }

    public IDisposable BeginHighPrecisionTiming()
    {
        // Default Windows timer resolution is ~15.6ms which makes Thread.Sleep very coarse.
        timeBeginPeriod(1);
        return new TimerPeriodScope();
    }

    private sealed class TimerPeriodScope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                timeEndPeriod(1);
        }
    }
}
