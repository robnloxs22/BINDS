using System.Runtime.Versioning;
using DoinkBinds.Models;
using static DoinkBinds.Platform.MacOS.MacNative;

namespace DoinkBinds.Platform.MacOS;

/// <summary>
/// Window listing via CGWindowListCopyWindowInfo (titles need Screen Recording permission) and
/// window control via the Accessibility API (needs Accessibility permission).
/// </summary>
public sealed partial class MacWindowService
{
    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        var array = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, 0);
        if (array == IntPtr.Zero) return result;
        try
        {
            var n = CFArrayGetCount(array);
            for (nint i = 0; i < n; i++)
            {
                var dict = CFArrayGetValueAtIndex(array, i);
                if (Number(dict, "kCGWindowLayer") != 0) continue; // only normal app windows
                var pid = (int)Number(dict, "kCGWindowOwnerPID");
                if (pid == Environment.ProcessId) continue;

                var owner = CFStringToString(CFDictionaryGetValue(dict, CFStr("kCGWindowOwnerName")));
                var title = CFStringToString(CFDictionaryGetValue(dict, CFStr("kCGWindowName")));
                var boundsDict = CFDictionaryGetValue(dict, CFStr("kCGWindowBounds"));
                if (boundsDict == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(boundsDict, out var r)) continue;
                if (r.Width < 40 || r.Height < 40) continue; // status items, tiny helper windows

                result.Add(new WindowInfo((nint)Number(dict, "kCGWindowNumber"), pid, owner, title,
                    r.X, r.Y, r.Width, r.Height, false, false));
            }
        }
        finally
        {
            CFRelease(array);
        }
        return result;
    }

    private static long Number(IntPtr dict, string key)
    {
        var v = CFDictionaryGetValue(dict, CFStr(key));
        return v != IntPtr.Zero && CFNumberGetValue(v, kCFNumberSInt64Type, out var n) ? n : 0;
    }

    public bool Focus(WindowInfo window)
    {
        var activated = TryActivate(new WindowTarget { ProcessName = window.ProcessName });
        WithAxWindow(window, w => AXUIElementPerformAction(w, CFStr("AXRaise")) == 0);
        return activated;
    }

    public bool SetBounds(WindowInfo window, double x, double y, double width, double height) =>
        WithAxWindow(window, w =>
        {
            AXUIElementSetAttributeValue(w, CFStr("AXMinimized"), kCFBooleanFalse);
            var p = new CGPoint(x, y);
            var size = new CGSize { Width = width, Height = height };
            var pv = AXValueCreate(kAXValueCGPointType, ref p);
            var sv = AXValueCreateSize(kAXValueCGSizeType, ref size);
            try
            {
                // Position, size, then position again: some apps clamp size against the old position.
                var ok = AXUIElementSetAttributeValue(w, CFStr("AXPosition"), pv) == 0;
                ok &= AXUIElementSetAttributeValue(w, CFStr("AXSize"), sv) == 0;
                AXUIElementSetAttributeValue(w, CFStr("AXPosition"), pv);
                return ok;
            }
            finally
            {
                CFRelease(pv);
                CFRelease(sv);
            }
        });

    public bool SetState(WindowInfo window, WindowStateKind state)
    {
        switch (state)
        {
            case WindowStateKind.Minimized:
                return WithAxWindow(window, w => AXUIElementSetAttributeValue(w, CFStr("AXMinimized"), kCFBooleanTrue) == 0);
            case WindowStateKind.Maximized:
            {
                // "Zoom" to the display the window is on (leaving room for the menu bar).
                var screens = new MacScreenService().GetScreens();
                var cx = window.X + window.Width / 2;
                var cy = window.Y + window.Height / 2;
                var s = screens.FirstOrDefault(sc => sc.Contains(cx, cy), screens[0]);
                const double menuBar = 25;
                return SetBounds(window, s.X, s.Y + (s.IsPrimary ? menuBar : 0), s.Width, s.Height - (s.IsPrimary ? menuBar : 0));
            }
            default:
                return WithAxWindow(window, w => AXUIElementSetAttributeValue(w, CFStr("AXMinimized"), kCFBooleanFalse) == 0);
        }
    }

    public bool Close(WindowInfo window) =>
        WithAxWindow(window, w =>
        {
            if (AXUIElementCopyAttributeValue(w, CFStr("AXCloseButton"), out var button) != 0 || button == IntPtr.Zero)
                return false;
            try { return AXUIElementPerformAction(button, CFStr("AXPress")) == 0; }
            finally { CFRelease(button); }
        });

    /// <summary>Finds the AX window element matching <paramref name="window"/> (by title, else by position) and runs <paramref name="action"/>.</summary>
    private static bool WithAxWindow(WindowInfo window, Func<IntPtr, bool> action)
    {
        var app = AXUIElementCreateApplication(window.ProcessId);
        if (app == IntPtr.Zero) return false;
        try
        {
            if (AXUIElementCopyAttributeValue(app, CFStr("AXWindows"), out var windows) != 0 || windows == IntPtr.Zero)
                return false;
            try
            {
                var n = CFArrayGetCount(windows);
                IntPtr best = IntPtr.Zero;
                var bestScore = double.MaxValue;
                for (nint i = 0; i < n; i++)
                {
                    var w = CFArrayGetValueAtIndex(windows, i);
                    var score = 0.0;
                    if (AXUIElementCopyAttributeValue(w, CFStr("AXTitle"), out var t) == 0 && t != IntPtr.Zero)
                    {
                        var title = CFStringToString(t);
                        CFRelease(t);
                        if (window.Title.Length > 0 && title == window.Title) score -= 100_000;
                    }
                    if (AXUIElementCopyAttributeValue(w, CFStr("AXPosition"), out var pv) == 0 && pv != IntPtr.Zero)
                    {
                        if (AXValueGetValue(pv, kAXValueCGPointType, out var p))
                            score += Math.Abs(p.X - window.X) + Math.Abs(p.Y - window.Y);
                        CFRelease(pv);
                    }
                    if (score < bestScore) { bestScore = score; best = w; }
                }
                return best != IntPtr.Zero && action(best);
            }
            finally
            {
                CFRelease(windows);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            CFRelease(app);
        }
    }
}
