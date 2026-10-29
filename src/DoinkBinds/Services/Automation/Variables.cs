using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DoinkBinds.Services.Automation;

/// <summary>
/// Named text variables shared by macros and rules ("counter", "username", "score"...).
/// Values are strings; numeric operations parse them with the invariant culture.
/// </summary>
public sealed class VariableStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public string? Get(string name) => _values.TryGetValue(Normalize(name), out var v) ? v : null;

    public void Set(string name, string value)
    {
        _values[Normalize(name)] = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string name)
    {
        if (_values.TryRemove(Normalize(name), out _)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _values.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string name) => name.Trim().Trim('{', '}').Trim();

    public static bool TryNumber(string? s, out double value) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static string FormatNumber(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
}

/// <summary>
/// Replaces {name} placeholders. Built-ins: {date} {time} {datetime} {timestamp} {newline}
/// {random:MIN-MAX}. Unknown names are left as-is so typos are visible.
/// </summary>
public static partial class TextTemplate
{
    [GeneratedRegex(@"\{([A-Za-z0-9_.\-]+)(?::([^{}]*))?\}")]
    private static partial Regex Placeholder();

    private static readonly Random Rng = new();

    public static string Expand(string? text, Func<string, string?> lookup)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text ?? "";
        return Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            var arg = m.Groups[2].Success ? m.Groups[2].Value : null;
            switch (name.ToLowerInvariant())
            {
                case "date": return DateTime.Now.ToString(arg ?? "yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "time": return DateTime.Now.ToString(arg ?? "HH:mm:ss", CultureInfo.InvariantCulture);
                case "datetime": return DateTime.Now.ToString(arg ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case "timestamp": return DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                case "newline": return "\n";
                case "random":
                {
                    var (min, max) = (1, 100);
                    if (arg is not null)
                    {
                        var parts = arg.Split('-', 2);
                        if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
                            (min, max) = (Math.Min(a, b), Math.Max(a, b));
                    }
                    lock (Rng) return Rng.Next(min, max + 1).ToString(CultureInfo.InvariantCulture);
                }
            }
            return lookup(name) ?? m.Value;
        });
    }
}
