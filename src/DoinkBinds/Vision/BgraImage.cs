using SkiaSharp;

namespace DoinkBinds.Vision;

/// <summary>
/// A simple 32-bit BGRA bitmap (top-down rows, stride = width × 4) plus the desktop area it
/// was captured from. <see cref="Scale"/> is pixels per desktop unit (2 on Retina screens),
/// so pixel positions can always be converted back to clickable desktop coordinates.
/// </summary>
public sealed class BgraImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>Desktop coordinate of the top-left pixel.</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }

    /// <summary>Pixels per desktop unit.</summary>
    public double Scale { get; init; } = 1;

    public BgraImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image must not be empty.");
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length < width * height * 4) throw new ArgumentException("Pixel buffer too small.", nameof(pixels));
    }

    public int Stride => Width * 4;

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        var i = (y * Width + x) * 4;
        Pixels[i] = b;
        Pixels[i + 1] = g;
        Pixels[i + 2] = r;
        Pixels[i + 3] = 255;
    }

    /// <summary>Converts a pixel position in this image to a desktop coordinate.</summary>
    public (double X, double Y) ToDesktop(double px, double py) => (OriginX + px / Scale, OriginY + py / Scale);

    /// <summary>Crops a rectangle (in pixels). The rectangle is clamped to the image.</summary>
    public BgraImage Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        w = Math.Clamp(w, 1, Width - x);
        h = Math.Clamp(h, 1, Height - y);
        var dst = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, dst, row * w * 4, w * 4);
        return new BgraImage(w, h, dst) { OriginX = OriginX + x / Scale, OriginY = OriginY + y / Scale, Scale = Scale };
    }

    /// <summary>Crops a desktop-coordinate rectangle.</summary>
    public BgraImage CropDesktop(double x, double y, double w, double h) =>
        Crop((int)Math.Round((x - OriginX) * Scale), (int)Math.Round((y - OriginY) * Scale),
            Math.Max(1, (int)Math.Round(w * Scale)), Math.Max(1, (int)Math.Round(h * Scale)));

    /// <summary>Bilinear resize.</summary>
    public BgraImage Resize(int newWidth, int newHeight)
    {
        newWidth = Math.Max(1, newWidth);
        newHeight = Math.Max(1, newHeight);
        var dst = new byte[newWidth * newHeight * 4];
        var sx = (double)Width / newWidth;
        var sy = (double)Height / newHeight;
        for (var y = 0; y < newHeight; y++)
        {
            var fy = Math.Max(0, (y + 0.5) * sy - 0.5);
            var y0 = Math.Min((int)fy, Height - 1);
            var y1 = Math.Min(y0 + 1, Height - 1);
            var wy = fy - y0;
            for (var x = 0; x < newWidth; x++)
            {
                var fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                var x0 = Math.Min((int)fx, Width - 1);
                var x1 = Math.Min(x0 + 1, Width - 1);
                var wx = fx - x0;
                var o = (y * newWidth + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var p00 = Pixels[(y0 * Width + x0) * 4 + c];
                    var p01 = Pixels[(y0 * Width + x1) * 4 + c];
                    var p10 = Pixels[(y1 * Width + x0) * 4 + c];
                    var p11 = Pixels[(y1 * Width + x1) * 4 + c];
                    var top = p00 + (p01 - p00) * wx;
                    var bottom = p10 + (p11 - p10) * wx;
                    dst[o + c] = (byte)Math.Clamp(Math.Round(top + (bottom - top) * wy), 0, 255);
                }
            }
        }
        return new BgraImage(newWidth, newHeight, dst)
        {
            OriginX = OriginX,
            OriginY = OriginY,
            Scale = Scale * newWidth / Width
        };
    }

    /// <summary>8-bit luminance (fast integer approximation).</summary>
    public byte[] ToGray()
    {
        var g = new byte[Width * Height];
        for (int i = 0, p = 0; i < g.Length; i++, p += 4)
            g[i] = (byte)((Pixels[p] * 29 + Pixels[p + 1] * 150 + Pixels[p + 2] * 77) >> 8);
        return g;
    }

    // ---------------- PNG / base64 ----------------

    public byte[] ToPng()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(Pixels, 0, bmp.GetPixels(), Pixels.Length);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public string ToBase64Png() => Convert.ToBase64String(ToPng());

    public static BgraImage FromPng(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png) ?? throw new InvalidDataException("Not a valid image.");
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        if (!decoded.CopyTo(bmp, SKColorType.Bgra8888))
            throw new InvalidDataException("Unsupported image format.");
        var pixels = new byte[decoded.Width * decoded.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        return new BgraImage(decoded.Width, decoded.Height, pixels);
    }

    public static BgraImage FromBase64Png(string base64) => FromPng(Convert.FromBase64String(base64));

    public void SavePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ToPng());
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }
}
