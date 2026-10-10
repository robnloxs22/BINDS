using DoinkBinds.Platform.MacOS;
using DoinkBinds.Platform.Null;
using DoinkBinds.Platform.Windows;

namespace DoinkBinds.Platform;

public static class PlatformFactory
{
    /// <summary>Picks the right OS implementation at runtime.</summary>
    public static IPlatformServices Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatformServices();
        if (OperatingSystem.IsMacOS()) return new MacPlatformServices();
        return new NullPlatformServices();
    }
}
