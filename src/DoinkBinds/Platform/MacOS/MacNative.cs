using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DoinkBinds.Platform.MacOS;

/// <summary>CoreGraphics / CoreFoundation / Objective-C runtime declarations.</summary>
[SupportedOSPlatform("macos")]
internal static class MacNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>Written into kCGEventSourceUserData of injected events so our own tap can recognize them.</summary>
    public const long DoinkMagic = 0x0D01_4B00;

    // CGEventType
    public const uint kCGEventLeftMouseDown = 1, kCGEventLeftMouseUp = 2, kCGEventRightMouseDown = 3,
        kCGEventRightMouseUp = 4, kCGEventMouseMoved = 5, kCGEventLeftMouseDragged = 6,
        kCGEventRightMouseDragged = 7, kCGEventKeyDown = 10, kCGEventKeyUp = 11, kCGEventFlagsChanged = 12,
        kCGEventScrollWheel = 22, kCGEventOtherMouseDown = 25, kCGEventOtherMouseUp = 26,
        kCGEventOtherMouseDragged = 27, kCGEventTapDisabledByTimeout = 0xFFFFFFFE,
        kCGEventTapDisabledByUserInput = 0xFFFFFFFF;

    // CGEventField
    public const int kCGMouseEventClickState = 1, kCGMouseEventButtonNumber = 3, kCGKeyboardEventKeycode = 9,
        kCGScrollWheelEventDeltaAxis1 = 11, kCGScrollWheelEventDeltaAxis2 = 12, kCGEventSourceUserData = 42;

    // CGEventFlags
    public const ulong kCGEventFlagMaskAlphaShift = 0x10000, kCGEventFlagMaskShift = 0x20000,
        kCGEventFlagMaskControl = 0x40000, kCGEventFlagMaskAlternate = 0x80000, kCGEventFlagMaskCommand = 0x100000;

    // Device-dependent (left/right) modifier bits from IOLLEvent.h
    public const ulong NX_DEVICELCTLKEYMASK = 0x1, NX_DEVICELSHIFTKEYMASK = 0x2, NX_DEVICERSHIFTKEYMASK = 0x4,
        NX_DEVICELCMDKEYMASK = 0x8, NX_DEVICERCMDKEYMASK = 0x10, NX_DEVICELALTKEYMASK = 0x20,
        NX_DEVICERALTKEYMASK = 0x40, NX_DEVICERCTLKEYMASK = 0x2000;

    public const uint kCGHIDEventTap = 0, kCGSessionEventTap = 1;
    public const uint kCGHeadInsertEventTap = 0;
    public const uint kCGEventTapOptionListenOnly = 1;
    public const uint kCGScrollEventUnitLine = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;
        public CGPoint(double x, double y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CGEventTapCallBack(IntPtr proxy, uint type, IntPtr cgEvent, IntPtr userInfo);

    // ---- Event taps ----
    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest,
        CGEventTapCallBack callback, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    public static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightListenEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestListenEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightPostEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestPostEventAccess();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXIsProcessTrusted();

    // ---- Events ----
    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    public static extern CGPoint CGEventGetLocation(IntPtr cgEvent);

    [DllImport(CoreGraphics)]
    public static extern long CGEventGetIntegerValueField(IntPtr cgEvent, int field);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetIntegerValueField(IntPtr cgEvent, int field, long value);

    [DllImport(CoreGraphics)]
    public static extern ulong CGEventGetFlags(IntPtr cgEvent);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetFlags(IntPtr cgEvent, ulong flags);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint location, uint mouseButton);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    /// <summary>Non-variadic variant (macOS 10.13+). The classic CGEventCreateScrollWheelEvent is variadic and unsafe to P/Invoke on arm64.</summary>
    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(uint tap, IntPtr cgEvent);

    [DllImport(CoreGraphics)]
    public static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    [DllImport(CoreGraphics)]
    public static extern int CGAssociateMouseAndMouseCursorPosition([MarshalAs(UnmanagedType.I1)] bool connected);

    // ---- Displays ----
    [DllImport(CoreGraphics)]
    public static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[]? activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    public static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    public static extern uint CGMainDisplayID();

    // ---- CoreFoundation ----
    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, nint order);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopStop(IntPtr rl);

    private static IntPtr? _commonModes;

    /// <summary>kCFRunLoopCommonModes is an exported global variable, not a function.</summary>
    public static IntPtr kCFRunLoopCommonModes
    {
        get
        {
            if (_commonModes is { } v) return v;
            var lib = NativeLibrary.Load(CoreFoundation);
            var addr = NativeLibrary.GetExport(lib, "kCFRunLoopCommonModes");
            _commonModes = Marshal.ReadIntPtr(addr);
            return _commonModes.Value;
        }
    }

    // ---- Unicode typing ----
    [DllImport(CoreGraphics)]
    public static extern void CGEventKeyboardSetUnicodeString(IntPtr cgEvent, nuint length, [In] char[] chars);

    // ---- Screen capture ----
    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    public static extern int CGGetDisplaysWithRect(CGRect rect, uint maxDisplays, [Out] uint[] displays, out uint matchingDisplayCount);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGDisplayCreateImageForRect(uint display, CGRect rect);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetWidth(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetHeight(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetBytesPerRow(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetBitsPerPixel(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern uint CGImageGetBitmapInfo(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGImageGetDataProvider(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGDataProviderCopyData(IntPtr provider);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    public static extern nint CFDataGetLength(IntPtr data);

    // ---- Window list ----
    public const uint kCGWindowListOptionOnScreenOnly = 1, kCGWindowListExcludeDesktopElements = 16;

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);

    // ---- CoreFoundation collections ----
    public const uint kCFStringEncodingUTF8 = 0x08000100;
    public const int kCFNumberSInt32Type = 3, kCFNumberSInt64Type = 4, kCFNumberDoubleType = 13;

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string str, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern nint CFStringGetLength(IntPtr str);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern nuint CFGetTypeID(IntPtr cf);

    [DllImport(CoreFoundation)]
    public static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundation)]
    public static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CFNumberGetValue(IntPtr number, int type, out long value);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CFBooleanGetValue(IntPtr boolean);

    private static readonly Dictionary<string, IntPtr> CfStrings = new();

    /// <summary>Cached, never-released CFString for a constant key.</summary>
    public static IntPtr CFStr(string s)
    {
        lock (CfStrings)
        {
            if (!CfStrings.TryGetValue(s, out var p))
            {
                p = CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);
                CfStrings[s] = p;
            }
            return p;
        }
    }

    public static string CFStringToString(IntPtr str)
    {
        if (str == IntPtr.Zero || CFGetTypeID(str) != CFStringGetTypeID()) return "";
        var len = CFStringGetLength(str);
        var buf = new byte[len * 4 + 1];
        return CFStringGetCString(str, buf, buf.Length, kCFStringEncodingUTF8)
            ? System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0) is var n and >= 0 ? n : buf.Length)
            : "";
    }

    private static IntPtr GetExport(string lib, string name)
    {
        var h = NativeLibrary.Load(lib);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(h, name));
    }

    private static IntPtr? _cfTrue, _cfFalse;
    public static IntPtr kCFBooleanTrue => _cfTrue ??= GetExport(CoreFoundation, "kCFBooleanTrue");
    public static IntPtr kCFBooleanFalse => _cfFalse ??= GetExport(CoreFoundation, "kCFBooleanFalse");

    // ---- Accessibility (moving / resizing other apps' windows) ----
    public const int kAXValueCGPointType = 1, kAXValueCGSizeType = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        public double Width;
        public double Height;
    }

    [DllImport(ApplicationServices)]
    public static extern IntPtr AXUIElementCreateApplication(int pid);

    [DllImport(ApplicationServices)]
    public static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [DllImport(ApplicationServices)]
    public static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);

    [DllImport(ApplicationServices)]
    public static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);

    [DllImport(ApplicationServices)]
    public static extern IntPtr AXValueCreate(int type, ref CGPoint value);

    [DllImport(ApplicationServices, EntryPoint = "AXValueCreate")]
    public static extern IntPtr AXValueCreateSize(int type, ref CGSize value);

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXValueGetValue(IntPtr value, int type, out CGPoint point);

    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXValueGetValueSize(IntPtr value, int type, out CGSize size);

    // ---- AppKit ----
    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
    public static extern void NSBeep();

    // ---- Objective-C runtime (used for NSWorkspace) ----
    [DllImport(ObjC)]
    public static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    public static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    public static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_IntPtr_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern int msgSend_int(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nuint msgSend_nuint(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool msgSend_bool_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    /// <summary>Reads an NSString into a managed string (via -UTF8String).</summary>
    public static string NSStringToString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return "";
        var utf8 = msgSend_IntPtr(nsString, sel_registerName("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8) ?? "";
    }
}
