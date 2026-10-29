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
    public IScreenCapture Capture { get; } = new MacScreenCapture();

    public void Beep()
    {
        try { NSBeep(); } catch { /* ignore */ }
    }

    public string? CheckPermissions(bool prompt)
    {
        try
        {
            var listen = CGPreflightListenEventAccess();
            var post = CGPreflightPostEventAccess() || AXIsProcessTrusted();
            var screen = CGPreflightScreenCaptureAccess();
            if (listen && post && screen) return null;

            if (prompt)
            {
                // These show the system permission prompts / register the app in System Settings.
                if (!listen) CGRequestListenEventAccess();
                if (!post) CGRequestPostEventAccess();
                if (!screen) CGRequestScreenCaptureAccess();
            }

            var missing = new List<string>();
            if (!listen) missing.Add("Input Monitoring");
            if (!post) missing.Add("Accessibility");
            if (!screen) missing.Add("Screen Recording (needed for reading the screen)");
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
public sealed partial class MacWindowService : IWindowService
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

/// <summary>
/// Captures with CGDisplayCreateImageForRect (needs Screen Recording permission).
/// Rectangles spanning several displays are stitched together.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacScreenCapture : IScreenCapture
{
    public Vision.BgraImage? Capture(double x, double y, double width, double height)
    {
        try
        {
            var rect = new CGRect { X = x, Y = y, Width = Math.Max(1, width), Height = Math.Max(1, height) };
            var ids = new uint[16];
            if (CGGetDisplaysWithRect(rect, (uint)ids.Length, ids, out var count) != 0 || count == 0) return null;

            // Output scale = scale of the display holding the most of the rectangle (2 on Retina).
            Vision.BgraImage? result = null;
            foreach (var id in ids.Take((int)count))
            {
                var db = CGDisplayBounds(id);
                var ix = Math.Max(rect.X, db.X);
                var iy = Math.Max(rect.Y, db.Y);
                var ir = Math.Min(rect.X + rect.Width, db.X + db.Width);
                var ib = Math.Min(rect.Y + rect.Height, db.Y + db.Height);
                if (ir <= ix || ib <= iy) continue;

                var local = new CGRect { X = ix - db.X, Y = iy - db.Y, Width = ir - ix, Height = ib - iy };
                var part = CaptureDisplay(id, local, ix, iy);
                if (part is null) continue;

                if (count == 1) return part;
                result ??= new Vision.BgraImage(
                    (int)Math.Round(rect.Width * part.Scale), (int)Math.Round(rect.Height * part.Scale))
                { OriginX = rect.X, OriginY = rect.Y, Scale = part.Scale };
                Blit(part, result);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static Vision.BgraImage? CaptureDisplay(uint display, CGRect localRect, double originX, double originY)
    {
        var image = CGDisplayCreateImageForRect(display, localRect);
        if (image == IntPtr.Zero) return null;
        try
        {
            var w = (int)CGImageGetWidth(image);
            var h = (int)CGImageGetHeight(image);
            var bpr = (int)CGImageGetBytesPerRow(image);
            var bpp = (int)CGImageGetBitsPerPixel(image);
            if (w <= 0 || h <= 0 || bpp != 32) return null;

            var info = CGImageGetBitmapInfo(image);
            var byteOrder = info & 0x7000;      // kCGBitmapByteOrderMask
            var alpha = info & 0x1F;            // kCGBitmapAlphaInfoMask
            var alphaFirst = alpha is 2 or 4 or 6; // PremultipliedFirst / First / NoneSkipFirst
            var little = byteOrder == 0x2000;   // kCGBitmapByteOrder32Little

            var data = CGDataProviderCopyData(CGImageGetDataProvider(image));
            if (data == IntPtr.Zero) return null;
            try
            {
                var src = new byte[CFDataGetLength(data)];
                Marshal.Copy(CFDataGetBytePtr(data), src, 0, src.Length);
                var dst = new byte[w * h * 4];
                for (var row = 0; row < h; row++)
                {
                    for (var col = 0; col < w; col++)
                    {
                        var s = row * bpr + col * 4;
                        var d = (row * w + col) * 4;
                        byte r, g, b;
                        if (little && alphaFirst) { b = src[s]; g = src[s + 1]; r = src[s + 2]; }          // BGRA in memory
                        else if (little) { r = src[s + 3]; g = src[s + 2]; b = src[s + 1]; }               // ABGR
                        else if (alphaFirst) { r = src[s + 1]; g = src[s + 2]; b = src[s + 3]; }           // ARGB
                        else { r = src[s]; g = src[s + 1]; b = src[s + 2]; }                               // RGBA
                        dst[d] = b; dst[d + 1] = g; dst[d + 2] = r; dst[d + 3] = 255;
                    }
                }
                return new Vision.BgraImage(w, h, dst) { OriginX = originX, OriginY = originY, Scale = w / localRect.Width };
            }
            finally { CFRelease(data); }
        }
        finally { CFRelease(image); }
    }

    /// <summary>Nearest-neighbour copy of a piece into the stitched result (handles mixed scales).</summary>
    private static void Blit(Vision.BgraImage part, Vision.BgraImage target)
    {
        var ox = (part.OriginX - target.OriginX) * target.Scale;
        var oy = (part.OriginY - target.OriginY) * target.Scale;
        var w = (int)Math.Round(part.Width / part.Scale * target.Scale);
        var h = (int)Math.Round(part.Height / part.Scale * target.Scale);
        for (var y = 0; y < h; y++)
        {
            var ty = (int)oy + y;
            if (ty < 0 || ty >= target.Height) continue;
            var sy = Math.Min(part.Height - 1, (int)(y * part.Scale / target.Scale));
            for (var x = 0; x < w; x++)
            {
                var tx = (int)ox + x;
                if (tx < 0 || tx >= target.Width) continue;
                var sx = Math.Min(part.Width - 1, (int)(x * part.Scale / target.Scale));
                Buffer.BlockCopy(part.Pixels, (sy * part.Width + sx) * 4, target.Pixels, (ty * target.Width + tx) * 4, 4);
            }
        }
    }
}
