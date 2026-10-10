using System.Runtime.Versioning;
using DoinkBinds.Models;
using static DoinkBinds.Platform.MacOS.MacNative;

namespace DoinkBinds.Platform.MacOS;

/// <summary>
/// Posts synthetic events with CGEventPost. Requires the "Accessibility" privacy permission.
/// Tracks held buttons and modifiers so that drags and Shift+click etc. are reproduced faithfully.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacInputSimulator : IInputSimulator
{
    private readonly object _gate = new();
    private readonly HashSet<MacroMouseButton> _heldButtons = new();
    private ulong _modifierFlags;

    // Double-click detection: macOS apps rely on kCGMouseEventClickState rather than timing.
    private MacroMouseButton _lastDownButton;
    private DateTime _lastDownTime;
    private CGPoint _lastDownPos;
    private long _clickCount;

    public bool UseScanCodes { get; set; } // not applicable on macOS

    public DesktopPoint GetCursorPosition()
    {
        var e = CGEventCreate(IntPtr.Zero);
        if (e == IntPtr.Zero) return default;
        try
        {
            var p = CGEventGetLocation(e);
            return new DesktopPoint(p.X, p.Y);
        }
        finally { CFRelease(e); }
    }

    public void MoveMouse(double x, double y)
    {
        lock (_gate)
        {
            // While a button is held, apps expect "dragged" events rather than "moved".
            uint type = kCGEventMouseMoved;
            uint button = 0;
            if (_heldButtons.Contains(MacroMouseButton.Left)) type = kCGEventLeftMouseDragged;
            else if (_heldButtons.Contains(MacroMouseButton.Right)) { type = kCGEventRightMouseDragged; button = 1; }
            else if (_heldButtons.Count > 0) { type = kCGEventOtherMouseDragged; button = 2; }

            Post(CGEventCreateMouseEvent(IntPtr.Zero, type, new CGPoint(x, y), button));
        }
    }

    public void MouseButton(MacroMouseButton button, bool down)
    {
        lock (_gate)
        {
            var pos = GetCursorPosition();
            var pt = new CGPoint(pos.X, pos.Y);
            var (type, cgButton) = (button, down) switch
            {
                (MacroMouseButton.Left, true) => (kCGEventLeftMouseDown, 0u),
                (MacroMouseButton.Left, false) => (kCGEventLeftMouseUp, 0u),
                (MacroMouseButton.Right, true) => (kCGEventRightMouseDown, 1u),
                (MacroMouseButton.Right, false) => (kCGEventRightMouseUp, 1u),
                (MacroMouseButton.Middle, _) => (down ? kCGEventOtherMouseDown : kCGEventOtherMouseUp, 2u),
                (MacroMouseButton.X1, _) => (down ? kCGEventOtherMouseDown : kCGEventOtherMouseUp, 3u),
                _ => (down ? kCGEventOtherMouseDown : kCGEventOtherMouseUp, 4u),
            };

            if (down)
            {
                var now = DateTime.UtcNow;
                var near = Math.Abs(pt.X - _lastDownPos.X) < 4 && Math.Abs(pt.Y - _lastDownPos.Y) < 4;
                _clickCount = button == _lastDownButton && near && (now - _lastDownTime).TotalMilliseconds < 500
                    ? _clickCount + 1 : 1;
                _lastDownButton = button;
                _lastDownTime = now;
                _lastDownPos = pt;
                _heldButtons.Add(button);
            }
            else
            {
                _heldButtons.Remove(button);
            }

            var e = CGEventCreateMouseEvent(IntPtr.Zero, type, pt, cgButton);
            if (e != IntPtr.Zero)
            {
                CGEventSetIntegerValueField(e, kCGMouseEventClickState, Math.Max(1, _clickCount));
                if (cgButton > 2) CGEventSetIntegerValueField(e, kCGMouseEventButtonNumber, cgButton);
            }
            Post(e);
        }
    }

    public void MouseWheel(int delta, bool horizontal)
    {
        // Convert Windows-style units (120/notch) back to lines, at least one line.
        var lines = delta / 120;
        if (lines == 0) lines = Math.Sign(delta);
        Post(horizontal
            ? CGEventCreateScrollWheelEvent2(IntPtr.Zero, kCGScrollEventUnitLine, 2, 0, lines, 0)
            : CGEventCreateScrollWheelEvent2(IntPtr.Zero, kCGScrollEventUnitLine, 1, lines, 0, 0));
    }

    public void Key(int keyCode, bool down)
    {
        if (MacKeyMap.ToMacKeyCode(keyCode) is not { } mac) return;

        lock (_gate)
        {
            var flag = KeyCodes.ModifierOf(keyCode) switch
            {
                HotkeyModifiers.Shift => kCGEventFlagMaskShift,
                HotkeyModifiers.Control => kCGEventFlagMaskControl,
                HotkeyModifiers.Alt => kCGEventFlagMaskAlternate,
                HotkeyModifiers.Meta => kCGEventFlagMaskCommand,
                _ => 0UL
            };
            if (flag != 0)
                _modifierFlags = down ? _modifierFlags | flag : _modifierFlags & ~flag;

            Post(CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)mac, down));
        }
    }

    private void Post(IntPtr e)
    {
        if (e == IntPtr.Zero) return;
        try
        {
            CGEventSetIntegerValueField(e, kCGEventSourceUserData, DoinkMagic);
            // Apply currently held modifiers so Cmd+C, Shift+click, etc. work.
            var flags = CGEventGetFlags(e) & ~(kCGEventFlagMaskShift | kCGEventFlagMaskControl |
                                               kCGEventFlagMaskAlternate | kCGEventFlagMaskCommand);
            CGEventSetFlags(e, flags | _modifierFlags);
            CGEventPost(kCGHIDEventTap, e);
        }
        finally
        {
            CFRelease(e);
        }
    }

    public IDisposable BeginHighPrecisionTiming() => new Null.NoopDisposable();
}
