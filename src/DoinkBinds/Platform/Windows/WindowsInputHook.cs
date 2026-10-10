using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DoinkBinds.Models;
using static DoinkBinds.Platform.Windows.Win32;

namespace DoinkBinds.Platform.Windows;

/// <summary>
/// WH_KEYBOARD_LL + WH_MOUSE_LL hooks running on their own thread with a Win32 message loop.
/// Low-level hooks are called in the context of the installing thread, which therefore must
/// pump messages — that's what <see cref="HookThread"/> does.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInputHook : InputHookBase
{
    // Delegates must be kept alive for as long as the hooks exist, or the GC will collect them
    // and Windows will call into freed memory.
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook, _mouseHook;

    public WindowsInputHook()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    protected override bool StartHook(out string? error)
    {
        using var ready = new ManualResetEventSlim(false);
        string? startError = null;

        _thread = new Thread(() => HookThread(ready, e => startError = e))
        {
            IsBackground = true,
            Name = "DoinkBinds.Win32Hook",
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));

        error = startError;
        return startError is null && _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
    }

    private void HookThread(ManualResetEventSlim ready, Action<string> fail)
    {
        _threadId = GetCurrentThreadId();
        var hMod = GetModuleHandle(null);

        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);

        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            fail($"Could not install input hooks (Win32 error {Marshal.GetLastWin32Error()}).");
            Unhook();
            ready.Set();
            return;
        }

        ready.Set();

        // Standard message pump. GetMessage returns 0 on WM_QUIT and -1 on error.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Unhook();
    }

    private void Unhook()
    {
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    protected override void StopHook()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ts = RawInputEvent.Now();
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var kind = msg is WM_KEYDOWN or WM_SYSKEYDOWN ? RawInputKind.KeyDown : RawInputKind.KeyUp;
            Enqueue(new RawInputEvent(kind, ts, KeyCode: (int)data.vkCode,
                IsSynthetic: data.dwExtraInfo == DoinkMagic));
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ts = RawInputEvent.Now();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var synthetic = data.dwExtraInfo == DoinkMagic;
            double x = data.pt.X, y = data.pt.Y;
            var hiWord = (short)((data.mouseData >> 16) & 0xFFFF);

            RawInputEvent? e = (int)wParam switch
            {
                WM_MOUSEMOVE => new RawInputEvent(RawInputKind.MouseMove, ts, x, y, IsSynthetic: synthetic),
                WM_LBUTTONDOWN => Btn(RawInputKind.MouseDown, MacroMouseButton.Left),
                WM_LBUTTONUP => Btn(RawInputKind.MouseUp, MacroMouseButton.Left),
                WM_RBUTTONDOWN => Btn(RawInputKind.MouseDown, MacroMouseButton.Right),
                WM_RBUTTONUP => Btn(RawInputKind.MouseUp, MacroMouseButton.Right),
                WM_MBUTTONDOWN => Btn(RawInputKind.MouseDown, MacroMouseButton.Middle),
                WM_MBUTTONUP => Btn(RawInputKind.MouseUp, MacroMouseButton.Middle),
                WM_XBUTTONDOWN => Btn(RawInputKind.MouseDown, hiWord == 2 ? MacroMouseButton.X2 : MacroMouseButton.X1),
                WM_XBUTTONUP => Btn(RawInputKind.MouseUp, hiWord == 2 ? MacroMouseButton.X2 : MacroMouseButton.X1),
                WM_MOUSEWHEEL => new RawInputEvent(RawInputKind.MouseWheel, ts, x, y, WheelDelta: hiWord, IsSynthetic: synthetic),
                WM_MOUSEHWHEEL => new RawInputEvent(RawInputKind.MouseWheel, ts, x, y, WheelDelta: hiWord, Horizontal: true, IsSynthetic: synthetic),
                _ => null
            };

            if (e is { } ev) Enqueue(ev);

            RawInputEvent Btn(RawInputKind k, MacroMouseButton b) =>
                new(k, ts, x, y, Button: b, IsSynthetic: synthetic);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
