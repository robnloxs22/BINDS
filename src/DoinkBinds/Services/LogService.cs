using System.Diagnostics;

namespace DoinkBinds.Services;

public interface ILogService
{
    void Info(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Tiny thread-safe file logger (doink.log in the data folder, truncated when it grows past 1 MB).</summary>
public sealed class LogService : ILogService
{
    private readonly object _gate = new();

    public LogService()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var fi = new FileInfo(AppPaths.LogFile);
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
        }
        catch { /* logging must never crash the app */ }
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (_gate)
        {
            try { File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine); }
            catch { /* ignore */ }
        }
    }
}
