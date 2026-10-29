using System.Globalization;
using System.Text.RegularExpressions;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Vision;

namespace DoinkBinds.Services.Automation;

/// <summary>Outcome of evaluating a condition. <see cref="Found"/> is the centre of the matched image/text (desktop coords).</summary>
public sealed record ConditionResult(bool Met, DesktopPoint? Found = null, string? Text = null, string? Detail = null)
{
    public static readonly ConditionResult False = new(false);
}

/// <summary>Per-evaluation context: variables and a default window for window-relative regions.</summary>
public sealed class VisionContext
{
    public Func<string, string?> GetVariable { get; init; } = _ => null;
    public WindowTarget? DefaultWindow { get; init; }

    /// <summary>Captures made during one evaluation, so AND/OR trees don't capture the same area twice.</summary>
    internal Dictionary<(int, int, int, int), BgraImage?> Captures { get; } = new();
    internal IReadOnlyList<WindowInfo>? Windows { get; set; }
}

/// <summary>Reads the screen: evaluates conditions, reads text, captures areas.</summary>
public interface IScreenVision
{
    ConditionResult Evaluate(Condition condition, VisionContext context);

    /// <summary>Captures a region (null/empty = all monitors). Null if capturing isn't possible.</summary>
    BgraImage? Capture(ScreenRegion? region, WindowTarget? window, VisionContext? context = null);

    /// <summary>OCR of a region; lines joined with newlines.</summary>
    string ReadText(ScreenRegion? region, WindowTarget? window, VisionContext? context = null);

    /// <summary>Desktop rectangle for a region (resolving window-relative regions). Null if the window isn't open.</summary>
    (double X, double Y, double W, double H)? ResolveRegion(ScreenRegion? region, WindowTarget? window, VisionContext? context = null);

    /// <summary>Top-left of a window's visible frame, if the window is open.</summary>
    DesktopPoint? WindowOrigin(WindowTarget? window);

    bool IsOcrAvailable { get; }
}

public sealed class ScreenVision : IScreenVision
{
    private readonly IPlatformServices _platform;
    private readonly IMousePositionService _positions;
    private readonly IOcrEngine _ocr;
    private readonly ILogService _log;

    // Decoded templates are cached by their base64 text (templates rarely change).
    private readonly Dictionary<string, BgraImage> _templates = new();
    private string? _ocrError;

    public ScreenVision(IPlatformServices platform, IMousePositionService positions, IOcrEngine ocr, ILogService log)
    {
        _platform = platform;
        _positions = positions;
        _ocr = ocr;
        _log = log;
    }

    public bool IsOcrAvailable => _ocrError is null;

    // ------------------------------------------------------------------ evaluation

    public ConditionResult Evaluate(Condition c, VisionContext ctx)
    {
        ConditionResult r;
        try
        {
            r = c.Type switch
            {
                ConditionType.All => EvaluateAll(c, ctx),
                ConditionType.Any => EvaluateAny(c, ctx),
                ConditionType.ImageVisible => ImageVisible(c, ctx),
                ConditionType.TextVisible => TextVisible(c, ctx),
                ConditionType.PixelColor => PixelColor(c, ctx),
                ConditionType.AreaColor => AreaColor(c, ctx),
                ConditionType.WindowExists => WindowExists(c, ctx),
                ConditionType.WindowFocused => WindowFocused(c),
                ConditionType.Variable => VariableCompare(c, ctx),
                _ => ConditionResult.False
            };
        }
        catch (Exception ex)
        {
            _log.Error($"Condition '{c.Describe()}' failed", ex);
            r = new ConditionResult(false, Detail: ex.Message);
        }
        return c.Negate ? new ConditionResult(!r.Met, Detail: r.Detail) : r;
    }

    private ConditionResult EvaluateAll(Condition c, VisionContext ctx)
    {
        if (c.Children is not { Count: > 0 }) return new ConditionResult(true);
        DesktopPoint? found = null;
        string? text = null;
        foreach (var child in c.Children)
        {
            var r = Evaluate(child, ctx);
            if (!r.Met) return new ConditionResult(false, Detail: r.Detail ?? $"'{child.Describe()}' is false");
            found ??= r.Found;
            text ??= r.Text;
        }
        return new ConditionResult(true, found, text);
    }

    private ConditionResult EvaluateAny(Condition c, VisionContext ctx)
    {
        if (c.Children is not { Count: > 0 }) return ConditionResult.False;
        foreach (var child in c.Children)
        {
            var r = Evaluate(child, ctx);
            if (r.Met) return r;
        }
        return new ConditionResult(false, Detail: "none of the alternatives is true");
    }

    private ConditionResult ImageVisible(Condition c, VisionContext ctx)
    {
        if (string.IsNullOrEmpty(c.ImagePng)) return new ConditionResult(false, Detail: "no image captured for this check");
        var hay = Capture(c.Region, c.Window, ctx);
        if (hay is null) return new ConditionResult(false, Detail: CaptureProblem(c.Region, c.Window, ctx));

        var template = Template(c.ImagePng);
        // Rescale templates captured on a screen with a different pixel density (e.g. Retina vs non-Retina).
        if (c.ImageScale > 0 && Math.Abs(c.ImageScale - hay.Scale) > 0.01)
        {
            var f = hay.Scale / c.ImageScale;
            template = template.Resize((int)Math.Round(template.Width * f), (int)Math.Round(template.Height * f));
        }

        var threshold = c.Threshold > 0 ? c.Threshold : 0.9;
        var m = ImageMatcher.Find(hay, template, threshold);
        if (m is null) return new ConditionResult(false, Detail: "image not found");
        var (x, y) = hay.ToDesktop(m.CenterX, m.CenterY);
        return new ConditionResult(true, new DesktopPoint(x, y), Detail: $"found at {x:0},{y:0} ({m.Score:P0} match)");
    }

    private ConditionResult TextVisible(Condition c, VisionContext ctx)
    {
        var img = Capture(c.Region, c.Window, ctx);
        if (img is null) return new ConditionResult(false, Detail: CaptureProblem(c.Region, c.Window, ctx));
        var lines = Ocr(img);
        if (lines is null) return new ConditionResult(false, Detail: _ocrError);

        var all = string.Join("\n", lines.Select(l => l.Text));
        var wanted = TextTemplate.Expand(c.Text, ctx.GetVariable);
        var cmp = c.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Find the line that satisfies the match so clicks can go to the text.
        foreach (var line in lines)
        {
            if (!Matches(line.Text, wanted, c.Match, cmp, c.CaseSensitive, out var index, out var length)) continue;
            // Estimate the position of the matched substring inside the line.
            var frac = line.Text.Length == 0 || index < 0 ? 0.5 : (index + length / 2.0) / line.Text.Length;
            var (x, y) = img.ToDesktop(line.X + line.Width * frac, line.Y + line.Height / 2.0);
            return new ConditionResult(true, new DesktopPoint(x, y), line.Text, $"read \"{line.Text}\"");
        }
        // Some matches (numbers spanning words, regex over several lines) need the whole text.
        if (Matches(all, wanted, c.Match, cmp, c.CaseSensitive, out _, out _))
        {
            var (x, y) = img.ToDesktop(img.Width / 2.0, img.Height / 2.0);
            return new ConditionResult(true, new DesktopPoint(x, y), all, $"read \"{Short(all)}\"");
        }
        return new ConditionResult(false, Text: all, Detail: all.Length == 0 ? "no text found" : $"read \"{Short(all)}\"");
    }

    private static string Short(string s) => s.Length > 80 ? s[..77].Replace('\n', ' ') + "…" : s.Replace('\n', ' ');

    internal static bool Matches(string text, string wanted, TextMatchMode mode, StringComparison cmp, bool caseSensitive,
        out int index, out int length)
    {
        index = -1;
        length = wanted.Length;
        switch (mode)
        {
            case TextMatchMode.AnyText:
                index = 0; length = text.Length;
                return text.Trim().Length > 0;
            case TextMatchMode.Equals:
                index = 0;
                return string.Equals(Normalize(text), Normalize(wanted), cmp);
            case TextMatchMode.StartsWith:
                index = 0;
                return Normalize(text).StartsWith(Normalize(wanted), cmp);
            case TextMatchMode.Regex:
                try
                {
                    var m = Regex.Match(text, wanted, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                    index = m.Index; length = m.Length;
                    return m.Success;
                }
                catch (ArgumentException) { return false; }
            case TextMatchMode.NumberGreater:
            case TextMatchMode.NumberLess:
            case TextMatchMode.NumberEquals:
            {
                if (!VariableStore.TryNumber(wanted, out var target)) return false;
                foreach (Match m in NumberRegex().Matches(text))
                {
                    if (!VariableStore.TryNumber(m.Value.Replace(",", ""), out var v)) continue;
                    var ok = mode switch
                    {
                        TextMatchMode.NumberGreater => v > target,
                        TextMatchMode.NumberLess => v < target,
                        _ => Math.Abs(v - target) < 1e-9
                    };
                    if (ok) { index = m.Index; length = m.Length; return true; }
                }
                return false;
            }
            default:
                // OCR often reads "Log In" as "LogIn": compare with and without spaces.
                index = text.IndexOf(wanted, cmp);
                if (index >= 0) return true;
                var squashedText = text.Replace(" ", "");
                var squashedWanted = wanted.Replace(" ", "");
                return squashedWanted.Length > 0 && squashedText.Contains(squashedWanted, cmp);
        }
    }

    private static string Normalize(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    private static Regex NumberRegex() => NumberPattern;
    private static readonly Regex NumberPattern = new(@"-?\d{1,3}(?:,\d{3})+(?:\.\d+)?|-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    private ConditionResult PixelColor(Condition c, VisionContext ctx)
    {
        if (!BgraImage.TryParseHex(c.Color, out var r, out var g, out var b))
            return new ConditionResult(false, Detail: "invalid colour");
        var pos = c.Point ?? new MousePosition();
        var origin = pos.Mode == MousePositionMode.Window ? WindowOrigin(c.Window ?? ctx.DefaultWindow) : null;
        var p = _positions.Resolve(pos, _positions.GetCursorPosition(), new PositionContext(origin, null));
        var img = _platform.Capture.Capture(Math.Floor(p.X), Math.Floor(p.Y), 1, 1);
        if (img is null) return new ConditionResult(false, Detail: "screen capture not available");
        var (pr, pg, pb) = img.GetPixel(0, 0);
        var tol = Math.Max(0, c.Tolerance);
        var ok = Math.Abs(pr - r) <= tol && Math.Abs(pg - g) <= tol && Math.Abs(pb - b) <= tol;
        return new ConditionResult(ok, ok ? p : null, BgraImage.ToHex(pr, pg, pb), $"pixel is {BgraImage.ToHex(pr, pg, pb)}");
    }

    private ConditionResult AreaColor(Condition c, VisionContext ctx)
    {
        if (!BgraImage.TryParseHex(c.Color, out var r, out var g, out var b))
            return new ConditionResult(false, Detail: "invalid colour");
        var img = Capture(c.Region, c.Window, ctx);
        if (img is null) return new ConditionResult(false, Detail: CaptureProblem(c.Region, c.Window, ctx));
        var tol = Math.Max(0, c.Tolerance);
        long hits = 0, sx = 0, sy = 0;
        var px = img.Pixels;
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
        {
            var i = (y * img.Width + x) * 4;
            if (Math.Abs(px[i + 2] - r) <= tol && Math.Abs(px[i + 1] - g) <= tol && Math.Abs(px[i] - b) <= tol)
            {
                hits++;
                sx += x;
                sy += y;
            }
        }
        var percent = hits * 100.0 / (img.Width * img.Height);
        var need = c.Percent > 0 ? c.Percent : 1;
        if (percent < need) return new ConditionResult(false, Detail: $"{percent:0.#}% of the area matches");
        var (dx, dy) = img.ToDesktop((double)sx / hits, (double)sy / hits);
        return new ConditionResult(true, new DesktopPoint(dx, dy), Detail: $"{percent:0.#}% of the area matches");
    }

    private ConditionResult WindowExists(Condition c, VisionContext ctx)
    {
        var w = FindWindow(c.Window, ctx);
        return w is null
            ? new ConditionResult(false, Detail: $"no window \"{c.Window}\"")
            : new ConditionResult(true, new DesktopPoint(w.X + w.Width / 2, w.Y + w.Height / 2), w.Title);
    }

    private ConditionResult WindowFocused(Condition c)
    {
        var fg = _platform.Windows.GetForegroundWindow();
        var t = c.Window;
        if (fg is null || t is null) return ConditionResult.False;
        var ok = (string.IsNullOrWhiteSpace(t.ProcessName) || string.Equals(fg.ProcessName, t.ProcessName, StringComparison.OrdinalIgnoreCase))
                 && (string.IsNullOrWhiteSpace(t.Title) || fg.Title.Contains(t.Title, StringComparison.OrdinalIgnoreCase));
        return new ConditionResult(ok, Text: fg.Title, Detail: $"focused: {fg}");
    }

    private static ConditionResult VariableCompare(Condition c, VisionContext ctx)
    {
        var name = VariableStore.Normalize(c.Variable ?? "");
        var value = ctx.GetVariable(name) ?? "";
        var other = TextTemplate.Expand(c.Text, ctx.GetVariable);
        var numeric = VariableStore.TryNumber(value, out var a) & VariableStore.TryNumber(other, out var b);
        var ok = c.Op switch
        {
            CompareOp.Equals => numeric ? Math.Abs(a - b) < 1e-9 : string.Equals(value, other, StringComparison.OrdinalIgnoreCase),
            CompareOp.NotEquals => numeric ? Math.Abs(a - b) >= 1e-9 : !string.Equals(value, other, StringComparison.OrdinalIgnoreCase),
            CompareOp.Greater => numeric && a > b,
            CompareOp.Less => numeric && a < b,
            CompareOp.GreaterOrEqual => numeric && a >= b,
            CompareOp.LessOrEqual => numeric && a <= b,
            CompareOp.Contains => value.Contains(other, StringComparison.OrdinalIgnoreCase),
            CompareOp.IsEmpty => value.Length == 0,
            _ => false
        };
        return new ConditionResult(ok, Text: value, Detail: $"{{{name}}} = \"{value}\"");
    }

    // ------------------------------------------------------------------ capture / OCR / windows

    public (double X, double Y, double W, double H)? ResolveRegion(ScreenRegion? region, WindowTarget? window, VisionContext? context = null)
    {
        if (region is null || region.IsEmpty)
        {
            // Empty region relative to a window = the whole window; otherwise all monitors.
            if (region is { RelativeToWindow: true })
            {
                var win = FindWindow(window ?? context?.DefaultWindow, context);
                return win is null ? null : (win.X, win.Y, win.Width, win.Height);
            }
            var v = MousePositionService.VirtualDesktop(_positions.GetScreens());
            return (v.X, v.Y, v.Width, v.Height);
        }
        if (!region.RelativeToWindow) return (region.X, region.Y, region.Width, region.Height);

        var w = FindWindow(window ?? context?.DefaultWindow, context);
        if (w is null) return null;
        return (w.X + region.X, w.Y + region.Y, region.Width, region.Height);
    }

    public BgraImage? Capture(ScreenRegion? region, WindowTarget? window, VisionContext? context = null)
    {
        var rect = ResolveRegion(region, window, context);
        if (rect is not { } r) return null;
        var key = ((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.W), (int)Math.Round(r.H));
        if (context is not null && context.Captures.TryGetValue(key, out var cached)) return cached;
        var img = _platform.Capture.Capture(r.X, r.Y, r.W, r.H);
        if (context is not null) context.Captures[key] = img;
        return img;
    }

    private string CaptureProblem(ScreenRegion? region, WindowTarget? window, VisionContext ctx) =>
        ResolveRegion(region, window, ctx) is null
            ? $"window \"{window ?? ctx.DefaultWindow}\" is not open"
            : "screen capture not available" + (OperatingSystem.IsMacOS() ? " (grant Screen Recording permission)" : "");

    public string ReadText(ScreenRegion? region, WindowTarget? window, VisionContext? context = null)
    {
        var img = Capture(region, window, context);
        if (img is null) return "";
        var lines = Ocr(img);
        return lines is null ? "" : string.Join("\n", lines.Select(l => l.Text));
    }

    private IReadOnlyList<OcrLine>? Ocr(BgraImage img)
    {
        try
        {
            // Small text is read much better when upscaled a little.
            if (img.Height < 24 && img.Height > 0)
            {
                var f = 24.0 / img.Height;
                img = img.Resize((int)(img.Width * f), (int)(img.Height * f));
            }
            var lines = _ocr.Recognize(img);
            _ocrError = null;
            return lines;
        }
        catch (Exception ex)
        {
            _ocrError = $"text reading is unavailable: {ex.Message}";
            _log.Error("OCR failed", ex);
            return null;
        }
    }

    public DesktopPoint? WindowOrigin(WindowTarget? window)
    {
        var w = FindWindow(window, null);
        return w is null ? null : new DesktopPoint(w.X, w.Y);
    }

    private WindowInfo? FindWindow(WindowTarget? target, VisionContext? ctx)
    {
        if (target is null || target.IsEmpty) return null;
        var windows = ctx?.Windows ?? _platform.Windows.ListWindows();
        if (ctx is not null) ctx.Windows = windows;
        // Titles change a lot (documents, tabs): match "title contains" rather than exact.
        return windows.FirstOrDefault(w =>
            (string.IsNullOrWhiteSpace(target.ProcessName) || string.Equals(w.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(target.Title) || w.Title.Contains(target.Title, StringComparison.OrdinalIgnoreCase)));
    }

    private BgraImage Template(string base64)
    {
        lock (_templates)
        {
            if (_templates.TryGetValue(base64, out var t)) return t;
            if (_templates.Count > 200) _templates.Clear();
            t = BgraImage.FromBase64Png(base64);
            _templates[base64] = t;
            return t;
        }
    }
}
