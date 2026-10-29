using System.Net;
using System.Text;
using System.Text.Json;
using DoinkBinds.Models;
using DoinkBinds.Vision;

namespace DoinkBinds.Services.Automation;

/// <summary>
/// Test/run reports: one folder per run under Reports/, containing report.json, report.html
/// and any screenshots (failures are screenshotted automatically).
/// </summary>
public interface IReportService
{
    string ReportsDirectory { get; }
    RunReport Begin(Macro macro);

    /// <summary>Saves a screenshot into the report's folder; returns the file name.</summary>
    string? AddScreenshot(RunReport report, BgraImage image, string label);

    void Save(RunReport report);
    IReadOnlyList<RunReport> List();
    void Delete(RunReport report);
    void DeleteAll();
    string HtmlPath(RunReport report);
    event EventHandler? Changed;
}

public sealed class ReportService : IReportService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public event EventHandler? Changed;
    public string ReportsDirectory => Path.Combine(AppPaths.DataRoot, "Reports");

    public ReportService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    public RunReport Begin(Macro macro) => new() { MacroId = macro.Id, MacroName = macro.Name, StartedUtc = DateTime.UtcNow };

    private string EnsureFolder(RunReport r)
    {
        if (r.Folder is null)
        {
            var name = $"{r.StartedUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss} {AppPaths.SanitizeName(r.MacroName)}";
            var dir = Path.Combine(ReportsDirectory, name);
            for (var i = 2; Directory.Exists(dir); i++) dir = Path.Combine(ReportsDirectory, $"{name} ({i})");
            Directory.CreateDirectory(dir);
            r.Folder = dir;
        }
        return r.Folder;
    }

    public string? AddScreenshot(RunReport report, BgraImage image, string label)
    {
        try
        {
            var dir = EnsureFolder(report);
            var file = $"{report.Entries.Count + 1:000}-{AppPaths.SanitizeName(label)}.png";
            image.SavePng(Path.Combine(dir, file));
            return file;
        }
        catch (Exception ex)
        {
            _log.Error("Could not save report screenshot", ex);
            return null;
        }
    }

    public void Save(RunReport report)
    {
        try
        {
            var dir = EnsureFolder(report);
            File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, AppPaths.Json));
            File.WriteAllText(Path.Combine(dir, "report.html"), RenderHtml(report));
            Prune();
        }
        catch (Exception ex)
        {
            _log.Error("Could not save run report", ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Prune()
    {
        var keep = Math.Max(5, _settings.Settings.MaxReports);
        var dirs = Directory.Exists(ReportsDirectory)
            ? new DirectoryInfo(ReportsDirectory).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).ToList()
            : new List<DirectoryInfo>();
        foreach (var d in dirs.Skip(keep))
        {
            try { d.Delete(true); } catch { /* in use */ }
        }
    }

    public IReadOnlyList<RunReport> List()
    {
        var list = new List<RunReport>();
        if (!Directory.Exists(ReportsDirectory)) return list;
        foreach (var dir in Directory.EnumerateDirectories(ReportsDirectory))
        {
            var json = Path.Combine(dir, "report.json");
            if (!File.Exists(json)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<RunReport>(File.ReadAllText(json), AppPaths.Json);
                if (r is null) continue;
                r.Folder = dir;
                list.Add(r);
            }
            catch { /* skip unreadable */ }
        }
        return list.OrderByDescending(r => r.StartedUtc).ToList();
    }

    public void Delete(RunReport report)
    {
        if (report.Folder is not null && Directory.Exists(report.Folder)) Directory.Delete(report.Folder, true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteAll()
    {
        if (Directory.Exists(ReportsDirectory)) Directory.Delete(ReportsDirectory, true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string HtmlPath(RunReport report) => Path.Combine(EnsureFolder(report), "report.html");

    private static string RenderHtml(RunReport r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var color = r.Result == "Passed" ? "#23a55a" : r.Result == "Stopped" ? "#f0b232" : "#ed4245";
        var duration = (r.FinishedUtc ?? DateTime.UtcNow) - r.StartedUtc;
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{E(r.MacroName)}} — Doink Binds report</title>
            <style>
            body{font-family:Inter,Segoe UI,Helvetica,Arial,sans-serif;background:#1e1f22;color:#f2f3f5;margin:0;padding:32px}
            .card{background:#2b2d31;border-radius:12px;padding:20px 24px;margin-bottom:16px;border:1px solid #3f4147}
            h1{margin:0 0 6px;font-size:22px} .muted{color:#949ba4}
            .pill{display:inline-block;padding:4px 12px;border-radius:999px;font-weight:700;background:{{color}}22;color:{{color}}}
            table{width:100%;border-collapse:collapse} td,th{text-align:left;padding:10px 8px;border-top:1px solid #3f4147;vertical-align:top}
            .ok{color:#23a55a;font-weight:700} .bad{color:#ed4245;font-weight:700} img{max-width:480px;border-radius:8px;border:1px solid #3f4147;margin-top:8px}
            </style></head><body>
            <div class="card"><h1>{{E(r.MacroName)}}</h1>
            <span class="pill">{{E(r.Result)}}</span>
            <p class="muted">Started {{r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}} · Duration {{duration:hh\:mm\:ss}} · Loops {{r.Iterations}} · {{r.PassedCount}} passed · {{r.FailedCount}} failed</p>
            """);
        if (r.Error is not null) sb.Append($"<p class=\"bad\">{E(r.Error)}</p>");
        sb.Append("</div><div class=\"card\"><table><tr><th>Time</th><th>Step</th><th>Check</th><th>Result</th></tr>");
        foreach (var e in r.Entries)
        {
            sb.Append($"<tr><td class=\"muted\">{e.TimeUtc.ToLocalTime():HH:mm:ss.fff}</td><td>{e.StepNumber}</td><td>{E(e.Description)}");
            if (!string.IsNullOrEmpty(e.Message)) sb.Append($"<div class=\"muted\">{E(e.Message)}</div>");
            if (e.Screenshot is not null) sb.Append($"<div><a href=\"{E(e.Screenshot)}\"><img src=\"{E(e.Screenshot)}\"></a></div>");
            sb.Append($"</td><td class=\"{(e.Passed ? "ok" : "bad")}\">{(e.Passed ? "PASS" : "FAIL")}</td></tr>");
        }
        if (r.Entries.Count == 0) sb.Append("<tr><td colspan=4 class=\"muted\">No checks were recorded in this run.</td></tr>");
        sb.Append("</table></div><p class=\"muted\">Generated by Doink Binds</p></body></html>");
        return sb.ToString();
    }
}
