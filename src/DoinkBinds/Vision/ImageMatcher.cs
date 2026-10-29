namespace DoinkBinds.Vision;

/// <summary>A template match inside a haystack image (pixel coordinates of the haystack).</summary>
public sealed record MatchResult(int X, int Y, int Width, int Height, double Score)
{
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}

/// <summary>
/// Finds a small image (template) inside a larger screenshot, at full resolution (so every
/// pixel offset is considered, even in repetitive UIs like lists and grids).
///
///  1. Pick ~32 "probe" pixels from the template, preferring high-contrast edge pixels
///     (they discriminate best) spread across the template.
///  2. For every haystack position, compare the probes first and reject the position as soon as
///     the running difference is too big — almost all positions die after 1–3 probes.
///  3. Score the survivors with the mean absolute colour difference over the whole template,
///     with early exit once a survivor can't beat the best so far.
/// Score = 1 − meanDiff / 255 (1.0 = pixel perfect).
/// </summary>
public static class ImageMatcher
{
    private const int ProbeCount = 32;

    public static MatchResult? Find(BgraImage haystack, BgraImage needle, double threshold = 0.9)
    {
        if (needle.Width > haystack.Width || needle.Height > haystack.Height) return null;
        threshold = Math.Clamp(threshold, 0.3, 1.0);

        var probes = PickProbes(needle);
        // Single pixels vary more than the average (anti-aliasing, hover tint), so give probes slack.
        var perProbeAllowed = Math.Max(36.0, (1 - threshold) * 255 * 3 * 1.5);

        var hp = haystack.Pixels;
        var np = needle.Pixels;
        var hw = haystack.Width;
        var maxX = haystack.Width - needle.Width;
        var maxY = haystack.Height - needle.Height;

        // Pre-compute probe offsets/colours.
        var pOffset = new int[probes.Length];
        var pB = new int[probes.Length];
        var pG = new int[probes.Length];
        var pR = new int[probes.Length];
        for (var i = 0; i < probes.Length; i++)
        {
            var (px, py) = probes[i];
            pOffset[i] = (py * hw + px) * 4;
            var ni = (py * needle.Width + px) * 4;
            pB[i] = np[ni];
            pG[i] = np[ni + 1];
            pR[i] = np[ni + 2];
        }

        // Rows are scanned in parallel; each worker keeps its own best, merged at the end.
        var gate = new object();
        MatchResult? best = null;
        var bestScore = threshold; // shared lower bound (read racily: only used to prune)
        Parallel.For(0, maxY + 1, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            (y, state) =>
            {
                var rowBase = y * hw * 4;
                for (var x = 0; x <= maxX; x++)
                {
                    var b = rowBase + x * 4;
                    double sum = 0;
                    var ok = true;
                    for (var i = 0; i < pOffset.Length; i++)
                    {
                        var o = b + pOffset[i];
                        sum += Math.Abs(hp[o] - pB[i]) + Math.Abs(hp[o + 1] - pG[i]) + Math.Abs(hp[o + 2] - pR[i]);
                        if (sum > perProbeAllowed * (i + 1)) { ok = false; break; }
                    }
                    if (!ok) continue;

                    var floor = Volatile.Read(ref bestScore);
                    var score = ColorScore(haystack, x, y, needle, floor);
                    if (score < floor) continue;
                    lock (gate)
                    {
                        if (best is null || score > best.Score || (score == best.Score && (y < best.Y || (y == best.Y && x < best.X))))
                        {
                            best = new MatchResult(x, y, needle.Width, needle.Height, score);
                            bestScore = Math.Max(bestScore, score);
                        }
                    }
                    if (score > 0.999) state.Stop(); // pixel perfect
                }
            });
        return best;
    }

    /// <summary>
    /// Chooses probe pixels: the highest-gradient pixels, keeping them spatially spread out,
    /// topped up with a regular grid so flat templates still get probes everywhere.
    /// </summary>
    private static (int X, int Y)[] PickProbes(BgraImage t)
    {
        var gray = t.ToGray();
        int w = t.Width, h = t.Height;
        var scored = new List<(int X, int Y, int G)>(w * h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = gray[y * w + x];
            var gx = x + 1 < w ? Math.Abs(gray[y * w + x + 1] - c) : 0;
            var gy = y + 1 < h ? Math.Abs(gray[(y + 1) * w + x] - c) : 0;
            scored.Add((x, y, gx + gy));
        }

        var minDist = Math.Max(1, Math.Min(w, h) / 6);
        var chosen = new List<(int X, int Y)>(ProbeCount);
        foreach (var p in scored.OrderByDescending(p => p.G))
        {
            if (chosen.Count >= ProbeCount * 3 / 4 || p.G < 8) break;
            if (chosen.Any(c => Math.Abs(c.X - p.X) < minDist && Math.Abs(c.Y - p.Y) < minDist)) continue;
            chosen.Add((p.X, p.Y));
        }

        // Grid fill.
        var grid = (int)Math.Ceiling(Math.Sqrt(ProbeCount - chosen.Count + 1));
        for (var gy = 0; gy < grid && chosen.Count < ProbeCount; gy++)
        for (var gx = 0; gx < grid && chosen.Count < ProbeCount; gx++)
        {
            var px = Math.Min(w - 1, (int)((gx + 0.5) * w / grid));
            var py = Math.Min(h - 1, (int)((gy + 0.5) * h / grid));
            if (!chosen.Contains((px, py))) chosen.Add((px, py));
        }
        return chosen.ToArray();
    }

    /// <summary>1 − mean absolute colour difference / 255, with early exit once it can't reach <paramref name="mustBeat"/>.</summary>
    private static double ColorScore(BgraImage hay, int x, int y, BgraImage needle, double mustBeat)
    {
        var total = (double)needle.Width * needle.Height * 3 * 255;
        var allowed = (1 - mustBeat) * total;
        long diff = 0;
        var hp = hay.Pixels;
        var np = needle.Pixels;
        for (var row = 0; row < needle.Height; row++)
        {
            var h = ((y + row) * hay.Width + x) * 4;
            var n = row * needle.Width * 4;
            for (var col = 0; col < needle.Width; col++, h += 4, n += 4)
                diff += Math.Abs(hp[h] - np[n]) + Math.Abs(hp[h + 1] - np[n + 1]) + Math.Abs(hp[h + 2] - np[n + 2]);
            if (diff > allowed + 1) return 0;
        }
        return 1 - diff / total;
    }
}
