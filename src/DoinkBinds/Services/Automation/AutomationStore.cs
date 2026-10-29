using System.Text.Json;
using DoinkBinds.Models;

namespace DoinkBinds.Services.Automation;

/// <summary>Persists If/Then rules and window layouts in automation.json (data folder).</summary>
public interface IAutomationStore
{
    RuleSet Data { get; }
    event EventHandler? Changed;
    void Save();
}

public sealed class AutomationStore : IAutomationStore
{
    private readonly ILogService _log;
    private static string FilePath => Path.Combine(AppPaths.DataRoot, "automation.json");

    public RuleSet Data { get; }
    public event EventHandler? Changed;

    public AutomationStore(ILogService log)
    {
        _log = log;
        Data = Load();
    }

    private RuleSet Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(FilePath), AppPaths.Json) ?? new RuleSet();
        }
        catch (Exception ex)
        {
            _log.Error("Could not read automation.json — starting empty (a backup was kept)", ex);
            try { File.Copy(FilePath, FilePath + ".corrupt", true); } catch { /* ignore */ }
        }
        return new RuleSet();
    }

    public void Save()
    {
        try
        {
            lock (Data) AppPaths.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(Data, AppPaths.Json));
        }
        catch (Exception ex)
        {
            _log.Error("Could not save automation.json", ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
