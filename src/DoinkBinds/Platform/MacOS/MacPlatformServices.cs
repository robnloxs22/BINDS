using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DoinkBinds.Models;
using static DoinkBinds.Platform.MacOS.MacNative;

namespace DoinkBinds.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacPlatformServices : IPlatformServices
{
    public string Name => "macOS";
    public bool IsSupported => true;
    public IInputHook Hook { get; } = new MacInputHook();
    public IInputSimulator Input { get; } = new MacInputSimulator();
    public IWindowService Windows { get; } = new MacWindowService();
    public IScreenService Screens { get; } = new MacScreenService();

    public string? CheckPermissions(bool prompt)
    {
        try
        {
            var listen = CGPreflightListenEventAccess();
            var post = CGPreflightPostEventAccess() || AXIsProcessTrusted();
            if (listen && post) return null;

            if (prompt)
            {
                // These show the system permission prompts / register the app in System Settings.
                if (!listen) CGRequestListenEventAccess();
                if (!post) CGRequestPostEventAccess();
            }

            var missing = new List<string>();
            if (!listen) missing.Add("Input Monitoring");
            if (!post) missing.Add("Accessibility");
            return $"Doink Binds needs {string.Join(" and ", missing)} permission. Open System Settings → " +
                   "Privacy & Security, enable Doink Binds in those lists, then restart the app.";
        }
        catch (Exception ex)
        {
            return $"Could not check macOS permissions: {ex.Message}";
        }
    }
}

/// <summary>
/// Uses NSWorkspace through the Objective-C runtime. macOS only exposes window titles of other apps
/// with Screen Recording permission, so we identify windows by application name.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacWindowService : IWindowService
{
    private const nuint NSApplicationActivateIgnoringOtherApps = 1 << 1;
    private static readonly int OwnPid = Environment.ProcessId;

    static MacWindowService()
    {
        // NSWorkspace lives in AppKit; Avalonia normally loads it already, this is just a safety net.
        try { NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"); } catch { /* ignore */ }
    }

    private static IntPtr Sel(string s) => sel_registerName(s);

    private static IntPtr SharedWorkspace() =>
        msgSend_IntPtr(objc_getClass("NSWorkspace"), Sel("sharedWorkspace"));

    public WindowTarget? GetForegroundWindow()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var app = msgSend_IntPtr(SharedWorkspace(), Sel("frontmostApplication"));
            if (app == IntPtr.Zero) return null;
            var name = NSStringToString(msgSend_IntPtr(app, Sel("localizedName")));
            return new WindowTarget { ProcessName = name, Title = "" };
        }
        catch { return null; }
        finally { objc_autoreleasePoolPop(pool); }
    }

    public bool IsOwnProcessForeground()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var app = msgSend_IntPtr(SharedWorkspace(), Sel("frontmostApplication"));
            return app != IntPtr.Zero && msgSend_int(app, Sel("processIdentifier")) == OwnPid;
        }
        catch { return false; }
        finally { objc_autoreleasePoolPop(pool); }
    }

    public bool TryActivate(WindowTarget target)
    {
        if (string.IsNullOrEmpty(target.ProcessName)) return false;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var apps = msgSend_IntPtr(SharedWorkspace(), Sel("runningApplications"));
            var count = msgSend_nuint(apps, Sel("count"));
            for (nuint i = 0; i < count; i++)
            {
                var app = msgSend_IntPtr_nuint(apps, Sel("objectAtIndex:"), i);
                var name = NSStringToString(msgSend_IntPtr(app, Sel("localizedName")));
                if (string.Equals(name, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                    return msgSend_bool_nuint(app, Sel("activateWithOptions:"), NSApplicationActivateIgnoringOtherApps);
            }
            return false;
        }
        catch { return false; }
        finally { objc_autoreleasePoolPop(pool); }
    }
}

[SupportedOSPlatform("macos")]
public sealed class MacScreenService : IScreenService
{
    public IReadOnlyList<ScreenInfo> GetScreens()
    {
        try
        {
            CGGetActiveDisplayList(0, null, out var count);
            if (count == 0) return Fallback();
            var ids = new uint[count];
            CGGetActiveDisplayList(count, ids, out count);
            var main = CGMainDisplayID();

            return ids.Take((int)count)
                .Select(id => (id, rect: CGDisplayBounds(id)))
                .OrderByDescending(d => d.id == main)
                .ThenBy(d => d.rect.X)
                .ThenBy(d => d.rect.Y)
                .Select((d, i) => new ScreenInfo(i, d.rect.X, d.rect.Y, d.rect.Width, d.rect.Height, d.id == main))
                .ToList();
        }
        catch
        {
            return Fallback();
        }
    }

    private static IReadOnlyList<ScreenInfo> Fallback() => new[] { new ScreenInfo(0, 0, 0, 1440, 900, true) };
}
