using System.Text.Json;
using DoinkBinds.Models;

namespace DoinkBinds.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
    event EventHandler? SettingsChanged;
    void NotifyChanged();
}

public sealed class SettingsService : ISettingsService
{
    private readonly ILogService _log;

    public AppSettings Settings { get; }
    public event EventHandler? SettingsChanged;

    public SettingsService(ILogService log)
    {
        _log = log;
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, AppPaths.Json);
                if (s is not null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt settings should never prevent the app from starting.
            _log.Error("Could not read settings, using defaults", ex);
            TryBackupCorrupt();
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }

    private static void TryBackupCorrupt()
    {
        try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".corrupt", true); } catch { /* ignore */ }
    }

    public void Save()
    {
        try
        {
            AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(Settings, AppPaths.Json));
        }
        catch (Exception ex)
        {
            _log.Error("Could not save settings", ex);
        }
    }

    public void NotifyChanged()
    {
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
