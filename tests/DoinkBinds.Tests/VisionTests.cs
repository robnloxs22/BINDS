using System.Diagnostics;
using DoinkBinds.Vision;
using SkiaSharp;
using Xunit.Abstractions;

namespace DoinkBinds.Tests;

internal static class TestImages
{
    private static readonly Lazy<SKTypeface> Font = new(() =>
        SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Inter-Regular.otf")) ?? SKTypeface.Default);

    /// <summary>Renders text (dark on light, like a typical UI) into a BgraImage.</summary>
    public static BgraImage RenderText(int width, int height, params (string Text, float X, float Y, float Size)[] items)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(245, 246, 248));
        using var paint = new SKPaint { Color = new SKColor(30, 31, 34), IsAntialias = true, Typeface = Font.Value };
        foreach (var (text, x, y, size) in items)
        {
            paint.TextSize = size;
            canvas.DrawText(text, x, y, paint);
        }
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var pixels = new byte[width * height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        return new BgraImage(width, height, pixels);
    }

    /// <summary>Pseudo-random noisy "screen" with some coloured blocks.</summary>
    public static BgraImage NoiseScreen(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var img = new BgraImage(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = (byte)(((x / 7) * 31 + (y / 5) * 17 + rnd.Next(0, 40)) & 0xFF);
            img.SetPixel(x, y, v, (byte)(255 - v), (byte)((v * 3) & 0xFF));
        }
        return img;
    }
}

public class VisionTests(ITestOutputHelper output)
{
    private static readonly Lazy<PaddleOcrEngine> Ocr = new(() => new PaddleOcrEngine());

    [Fact]
    public void Matcher_finds_exact_template_in_noisy_screen()
    {
        var screen = TestImages.NoiseScreen(1280, 720, 1);
        var template = screen.Crop(913, 401, 64, 40);
        var sw = Stopwatch.StartNew();
        var m = ImageMatcher.Find(screen, template, 0.95);
        output.WriteLine($"match in {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(m);
        Assert.Equal(913, m!.X);
        Assert.Equal(401, m.Y);
        Assert.True(m.Score > 0.99);
    }

    [Fact]
    public void Matcher_rejects_missing_template_and_tolerates_small_changes()
    {
        var screen = TestImages.NoiseScreen(800, 600, 2);
        var other = TestImages.NoiseScreen(200, 200, 99).Crop(10, 10, 50, 30);
        Assert.Null(ImageMatcher.Find(screen, other, 0.9));

        // Slight brightness change (e.g. hover state) still matches at 0.9.
        var template = screen.Crop(300, 200, 48, 48);
        for (var i = 0; i < template.Pixels.Length; i += 4)
            for (var c = 0; c < 3; c++) template.Pixels[i + c] = (byte)Math.Min(255, template.Pixels[i + c] + 8);
        var m = ImageMatcher.Find(screen, template, 0.9);
        Assert.NotNull(m);
        Assert.Equal(300, m!.X);
        Assert.Equal(200, m.Y);
    }

    [Fact]
    public void Png_round_trip()
    {
        var img = TestImages.NoiseScreen(37, 23, 3);
        var back = BgraImage.FromBase64Png(img.ToBase64Png());
        Assert.Equal(img.Width, back.Width);
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void Ocr_reads_single_line_label()
    {
        var img = TestImages.RenderText(360, 48, ("Victory 1250", 12, 34, 28));
        var sw = Stopwatch.StartNew();
        var lines = Ocr.Value.Recognize(img);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms: " + string.Join(" | ", lines.Select(l => $"{l.Text} ({l.Confidence:0.00})")));
        Assert.Contains(lines, l => l.Text.Contains("Victory") && l.Text.Contains("1250"));
    }

    [Fact]
    public void Ocr_detects_multiple_lines_with_positions()
    {
        var img = TestImages.RenderText(900, 400,
            ("Username", 40, 80, 26),
            ("Password", 40, 180, 26),
            ("Sign in to continue", 420, 320, 30));
        var sw = Stopwatch.StartNew();
        var lines = Ocr.Value.Recognize(img);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms: " + string.Join(" | ", lines.Select(l => $"{l.Text} @{l.X},{l.Y} {l.Width}x{l.Height}")));
        var user = Assert.Single(lines, l => l.Text.Contains("Username"));
        Assert.InRange(user.Y + user.Height / 2, 50, 90);
        Assert.Contains(lines, l => l.Text.Contains("Password"));
        var sign = Assert.Single(lines, l => l.Text.Contains("Sign in"));
        Assert.True(sign.X > 380);
    }
}
