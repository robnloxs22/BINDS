using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoinkBinds.Services;

/// <summary>Resolves where Doink Binds stores its data. Never hard-codes absolute paths.</summary>
public static class AppPaths
{
    public const string MacroExtension = ".doink";

    /// <summary>
    /// Data root:
    ///  1. DOINK_DATA_DIR environment variable, if set
    ///  2. "./Data" next to the executable when a file named "portable.txt" exists there (portable mode)
    ///  3. %APPDATA%\DoinkBinds on Windows, ~/Library/Application Support/DoinkBinds on macOS, ~/.config/DoinkBinds elsewhere
    /// </summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    public static string MacrosDirectory => Path.Combine(DataRoot, "Macros");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LogFile => Path.Combine(DataRoot, "doink.log");
    public static string SamplesDirectory => Path.Combine(AppContext.BaseDirectory, "Samples");

    private static string ResolveDataRoot()
    {
        var env = Environment.GetEnvironmentVariable("DOINK_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            return Path.Combine(exeDir, "Data");

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "DoinkBinds");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoinkBinds");
    }

    /// <summary>Shared JSON options: human-readable, enums as strings, tolerant on read.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Writes a file atomically (temp file + move) so a crash never leaves half-written JSON.</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Removes characters that are illegal in file / folder names on any OS.</summary>
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }
}
