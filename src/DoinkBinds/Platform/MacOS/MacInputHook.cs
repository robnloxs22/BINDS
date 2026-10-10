using System.Runtime.Versioning;
using DoinkBinds.Models;
using static DoinkBinds.Platform.MacOS.MacNative;

namespace DoinkBinds.Platform.MacOS;

/// <summary>
/// Listen-only CGEventTap running on a dedicated CFRunLoop thread.
/// Requires the "Input Monitoring" privacy permission (System Settings → Privacy &amp; Security).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacInputHook : InputHookBase
{
    private readonly CGEventTapCallBack _callback; // must stay referenced while the tap exists
    private Thread? _thread;
    private IntPtr _tap, _runLoop;

    public MacInputHook() => _callback = TapCallback;

    private static ulong Mask(params uint[] types) => types.Aggregate(0UL, (m, t) => m | (1UL << (int)t));

    protected override bool StartHook(out string? error)
    {
        using var ready = new ManualResetEventSlim(false);
        string? startError = null;

        _thread = new Thread(() =>
        {
            var mask = Mask(kCGEventLeftMouseDown, kCGEventLeftMouseUp, kCGEventRightMouseDown, kCGEventRightMouseUp,
                kCGEventMouseMoved, kCGEventLeftMouseDragged, kCGEventRightMouseDragged, kCGEventKeyDown,
                kCGEventKeyUp, kCGEventFlagsChanged, kCGEventScrollWheel, kCGEventOtherMouseDown,
                kCGEventOtherMouseUp, kCGEventOtherMouseDragged);

            _tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionListenOnly,
                mask, _callback, IntPtr.Zero);

            if (_tap == IntPtr.Zero)
            {
                startError = "macOS refused to create the input event tap. Grant Doink Binds \"Input Monitoring\" " +
                             "and \"Accessibility\" access in System Settings → Privacy & Security, then restart the app.";
                ready.Set();
                return;
            }

            var source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
            _runLoop = CFRunLoopGetCurrent();
            CFRunLoopAddSource(_runLoop, source, kCFRunLoopCommonModes);
            CGEventTapEnable(_tap, true);
            ready.Set();

            CFRunLoopRun(); // blocks until CFRunLoopStop

            CGEventTapEnable(_tap, false);
            CFRelease(source);
            CFRelease(_tap);
            _tap = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "DoinkBinds.MacEventTap"
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));

        error = startError;
        return startError is null;
    }

    protected override void StopHook()
    {
        if (_runLoop != IntPtr.Zero) CFRunLoopStop(_runLoop);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _runLoop = IntPtr.Zero;
    }

    private IntPtr TapCallback(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo)
    {
        try
        {
            if (type is kCGEventTapDisabledByTimeout or kCGEventTapDisabledByUserInput)
            {
                // macOS disables taps that are too slow; turn it straight back on.
                if (_tap != IntPtr.Zero) CGEventTapEnable(_tap, true);
                return evt;
            }

            var ts = RawInputEvent.Now();
            var synthetic = CGEventGetIntegerValueField(evt, kCGEventSourceUserData) == DoinkMagic;
            var loc = CGEventGetLocation(evt);

            switch (type)
            {
                case kCGEventMouseMoved:
                case kCGEventLeftMouseDragged:
                case kCGEventRightMouseDragged:
                case kCGEventOtherMouseDragged:
                    Enqueue(new RawInputEvent(RawInputKind.MouseMove, ts, loc.X, loc.Y, IsSynthetic: synthetic));
                    break;

                case kCGEventLeftMouseDown:
                case kCGEventLeftMouseUp:
                case kCGEventRightMouseDown:
                case kCGEventRightMouseUp:
                case kCGEventOtherMouseDown:
                case kCGEventOtherMouseUp:
                {
                    var down = type is kCGEventLeftMouseDown or kCGEventRightMouseDown or kCGEventOtherMouseDown;
                    var button = type switch
                    {
                        kCGEventLeftMouseDown or kCGEventLeftMouseUp => MacroMouseButton.Left,
                        kCGEventRightMouseDown or kCGEventRightMouseUp => MacroMouseButton.Right,
                        _ => CGEventGetIntegerValueField(evt, kCGMouseEventButtonNumber) switch
                        {
                            3 => MacroMouseButton.X1,
                            4 => MacroMouseButton.X2,
                            _ => MacroMouseButton.Middle
                        }
                    };
                    Enqueue(new RawInputEvent(down ? RawInputKind.MouseDown : RawInputKind.MouseUp, ts, loc.X, loc.Y,
                        Button: button, IsSynthetic: synthetic));
                    break;
                }

                case kCGEventScrollWheel:
                {
                    // Line deltas; we store Windows-style units (120 per notch).
                    var v = CGEventGetIntegerValueField(evt, kCGScrollWheelEventDeltaAxis1);
                    var h = CGEventGetIntegerValueField(evt, kCGScrollWheelEventDeltaAxis2);
                    if (v != 0)
                        Enqueue(new RawInputEvent(RawInputKind.MouseWheel, ts, loc.X, loc.Y, WheelDelta: (int)v * 120, IsSynthetic: synthetic));
                    if (h != 0)
                        Enqueue(new RawInputEvent(RawInputKind.MouseWheel, ts, loc.X, loc.Y, WheelDelta: (int)h * 120, Horizontal: true, IsSynthetic: synthetic));
                    break;
                }

                case kCGEventKeyDown:
                case kCGEventKeyUp:
                {
                    var mac = (int)CGEventGetIntegerValueField(evt, kCGKeyboardEventKeycode);
                    Enqueue(new RawInputEvent(type == kCGEventKeyDown ? RawInputKind.KeyDown : RawInputKind.KeyUp, ts,
                        KeyCode: MacKeyMap.ToVirtualKey(mac), IsSynthetic: synthetic));
                    break;
                }

                case kCGEventFlagsChanged:
                    HandleFlagsChanged(evt, ts, synthetic);
                    break;
            }
        }
        catch
        {
            // Exceptions must never escape into native code.
        }
        return evt;
    }

    /// <summary>
    /// Modifier keys don't produce KeyDown/KeyUp on macOS, only FlagsChanged. We work out
    /// whether the modifier went down or up from the device-dependent (left/right) flag bits.
    /// </summary>
    private void HandleFlagsChanged(IntPtr evt, long ts, bool synthetic)
    {
        var mac = (int)CGEventGetIntegerValueField(evt, kCGKeyboardEventKeycode);
        var flags = CGEventGetFlags(evt);

        ulong? bit = mac switch
        {
            0x38 => NX_DEVICELSHIFTKEYMASK,
            0x3C => NX_DEVICERSHIFTKEYMASK,
            0x3B => NX_DEVICELCTLKEYMASK,
            0x3E => NX_DEVICERCTLKEYMASK,
            0x3A => NX_DEVICELALTKEYMASK,
            0x3D => NX_DEVICERALTKEYMASK,
            0x37 => NX_DEVICELCMDKEYMASK,
            0x36 => NX_DEVICERCMDKEYMASK,
            _ => null
        };

        var vk = MacKeyMap.ToVirtualKey(mac);
        if (bit is { } b)
        {
            var down = (flags & b) != 0;
            Enqueue(new RawInputEvent(down ? RawInputKind.KeyDown : RawInputKind.KeyUp, ts, KeyCode: vk, IsSynthetic: synthetic));
        }
        else if (mac == 0x39) // Caps Lock: report a full press
        {
            Enqueue(new RawInputEvent(RawInputKind.KeyDown, ts, KeyCode: vk, IsSynthetic: synthetic));
            Enqueue(new RawInputEvent(RawInputKind.KeyUp, ts, KeyCode: vk, IsSynthetic: synthetic));
        }
    }
}
