using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DoinkBinds.Vision;

namespace DoinkBinds.Views;

/// <summary>Converts core <see cref="BgraImage"/>s into Avalonia bitmaps for display.</summary>
public static class BitmapHelper
{
    public static WriteableBitmap ToBitmap(BgraImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        for (var y = 0; y < img.Height; y++)
            Marshal.Copy(img.Pixels, y * img.Width * 4, fb.Address + y * fb.RowBytes, img.Width * 4);
        return bmp;
    }

    public static Bitmap? FromBase64Png(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(base64));
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
