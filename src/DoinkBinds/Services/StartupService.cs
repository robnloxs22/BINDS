using System.Runtime.Versioning;

namespace DoinkBinds.Services;

/// <summary>"Start Doink Binds when I log in" (HKCU Run key on Windows, a LaunchAgent on macOS).</summary>
public interface IStartupService
{
    bool IsSupported { get; }
    bool IsEnabled { get; }

    /// <summary>Returns an error message, or null on success.</summary>
    string? SetEnabled(bool enabled);
}

public sealed class StartupService(ILogService log) : IStartupService
{
    private const string Name = "DoinkBinds";
    private const string MacLabel = "com.doink.binds";

    private static string ExePath => Environment.ProcessPath ?? "";

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsGet() is not null;
                if (OperatingSystem.IsMacOS()) return File.Exists(MacPlistPath);
            }
            catch (Exception ex)
            {
                log.Error("Could not read startup setting", ex);
            }
            return false;
        }
    }

    public string? SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows()) WindowsSet(enabled);
            else if (OperatingSystem.IsMacOS()) MacSet(enabled);
            else return "Not supported on this operating system.";
            return null;
        }
        catch (Exception ex)
        {
            log.Error("Could not change startup setting", ex);
            return ex.Message;
        }
    }

    // ---- Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run ----

    [SupportedOSPlatform("windows")]
    private static string? WindowsGet()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(Name) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsSet(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(Name, $"\"{ExePath}\" --minimized");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    // ---- macOS: ~/Library/LaunchAgents/com.doink.binds.plist ----

    private static string MacPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacLabel + ".plist");

    private static void MacSet(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(MacPlistPath)) File.Delete(MacPlistPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
        var exe = System.Security.SecurityElement.Escape(ExePath);
        File.WriteAllText(MacPlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{MacLabel}</string>
                <key>ProgramArguments</key>
                <array><string>{exe}</string><string>--minimized</string></array>
                <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }
}
