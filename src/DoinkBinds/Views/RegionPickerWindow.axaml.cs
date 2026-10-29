using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DoinkBinds.Models;
using DoinkBinds.Platform;
using DoinkBinds.Vision;

namespace DoinkBinds.Views;

public partial class RegionPickerWindow : Window
{
    public enum PickMode { Region, Color }

    private readonly PickMode _mode;
    private readonly IInputSimulator? _input;
    private readonly BgraImage? _frozenDesktop;
    private readonly Action<object?>? _complete;
    private Point? _dragStartLocal;
    private DesktopPoint _dragStartDesktop;
    private bool _done;

    public RegionPickerWindow() => InitializeComponent();

    /// <param name="frozenScreen">Screenshot of this monitor (shown as background), or null.</param>
    /// <param name="frozenDesktop">Screenshot of the whole desktop (for the colour magnifier), or null.</param>
    /// <param name="complete">Receives a desktop rectangle (Region mode) or a DesktopPoint (Color mode), or null if cancelled.</param>
    public RegionPickerWindow(Screen screen, PickMode mode, IInputSimulator input, BgraImage? frozenScreen,
        BgraImage? frozenDesktop, string? hint, Action<object?> complete) : this()
    {
        _mode = mode;
        _input = input;
        _frozenDesktop = frozenDesktop;
        _complete = complete;

        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;

        if (frozenScreen is not null)
        {
            Frozen.Source = BitmapHelper.ToBitmap(frozenScreen);
            Background = Brushes.Black;
        }

        HintText.Text = hint ?? (mode == PickMode.Region ? "Drag a box around the area" : "Click the colour you want");
        HintSub.Text = mode == PickMode.Region
            ? "Release to confirm · Esc or right-click cancels"
            : "Arrow keys nudge by 1 px · Enter confirms · Esc cancels";

        UpdateDim(null);
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Hint.Measure(Size.Infinity);
            Canvas.SetLeft(Hint, Math.Max(0, (Bounds.Width - Hint.DesiredSize.Width) / 2));
            UpdateDim(null);
        }, DispatcherPriority.Loaded);

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerExited += (_, _) => Magnifier.IsVisible = false;
        KeyDown += OnKey;
    }

    private DesktopPoint CursorPos() => _input?.GetCursorPosition() ?? default;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) { Finish(null); return; }
        if (!props.IsLeftButtonPressed) return;

        if (_mode == PickMode.Color)
        {
            Finish(CursorPos());
            return;
        }
        _dragStartLocal = e.GetPosition(Surface);
        _dragStartDesktop = CursorPos();
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(Surface);
        if (_mode == PickMode.Color)
        {
            UpdateMagnifier(p);
            return;
        }
        if (_dragStartLocal is not { } s) return;
        var rect = new Rect(s, p).Normalize();
        UpdateDim(rect);
        var a = _dragStartDesktop;
        var b = CursorPos();
        SizeText.Text = $"{Math.Abs(b.X - a.X):0} × {Math.Abs(b.Y - a.Y):0}";
        SizeBadge.IsVisible = true;
        Canvas.SetLeft(SizeBadge, rect.X);
        Canvas.SetTop(SizeBadge, Math.Max(0, rect.Y - 26));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode != PickMode.Region || _dragStartLocal is null) return;
        var a = _dragStartDesktop;
        var b = CursorPos();
        _dragStartLocal = null;
        e.Pointer.Capture(null);
        var w = Math.Abs(b.X - a.X);
        var h = Math.Abs(b.Y - a.Y);
        if (w < 4 || h < 4)
        {
            UpdateDim(null);
            SizeBadge.IsVisible = false;
            return; // a click, not a drag: keep waiting
        }
        Finish(new ScreenRegion { X = Math.Min(a.X, b.X), Y = Math.Min(a.Y, b.Y), Width = w, Height = h });
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(null); e.Handled = true; return; }
        if (_mode != PickMode.Color || _input is null) return;
        var c = CursorPos();
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Enter: Finish(c); break;
            case Key.Left: _input.MoveMouse(c.X - step, c.Y); break;
            case Key.Right: _input.MoveMouse(c.X + step, c.Y); break;
            case Key.Up: _input.MoveMouse(c.X, c.Y - step); break;
            case Key.Down: _input.MoveMouse(c.X, c.Y + step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void UpdateDim(Rect? sel)
    {
        var w = Bounds.Width > 0 ? Bounds.Width : Width;
        var h = Bounds.Height > 0 ? Bounds.Height : Height;
        if (sel is not { } r || _mode == PickMode.Color)
        {
            Place(DimTop, 0, 0, w, h);
            Place(DimBottom, 0, 0, 0, 0);
            Place(DimLeft, 0, 0, 0, 0);
            Place(DimRight, 0, 0, 0, 0);
            Selection.IsVisible = false;
            if (_mode == PickMode.Color) DimTop.Fill = new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0));
            return;
        }
        Place(DimTop, 0, 0, w, r.Top);
        Place(DimBottom, 0, r.Bottom, w, Math.Max(0, h - r.Bottom));
        Place(DimLeft, 0, r.Top, r.Left, r.Height);
        Place(DimRight, r.Right, r.Top, Math.Max(0, w - r.Right), r.Height);
        Selection.IsVisible = true;
        Place(Selection, r.X, r.Y, r.Width, r.Height);
    }

    private static void Place(Control c, double x, double y, double w, double h)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        c.Width = Math.Max(0, w);
        c.Height = Math.Max(0, h);
    }

    private void UpdateMagnifier(Point local)
    {
        Magnifier.IsVisible = true;
        var mx = local.X + 24 + 150 > Bounds.Width ? local.X - 24 - 150 : local.X + 24;
        var my = local.Y + 24 + 180 > Bounds.Height ? local.Y - 24 - 180 : local.Y + 24;
        Canvas.SetLeft(Magnifier, mx);
        Canvas.SetTop(Magnifier, my);

        var c = CursorPos();
        if (_frozenDesktop is { } img)
        {
            var px = (int)Math.Round((c.X - img.OriginX) * img.Scale);
            var py = (int)Math.Round((c.Y - img.OriginY) * img.Scale);
            if (px >= 0 && py >= 0 && px < img.Width && py < img.Height)
            {
                const int r = 5;
                var x0 = Math.Clamp(px - r, 0, Math.Max(0, img.Width - (2 * r + 1)));
                var y0 = Math.Clamp(py - r, 0, Math.Max(0, img.Height - (2 * r + 1)));
                Zoom.Source = BitmapHelper.ToBitmap(img.Crop(x0, y0, 2 * r + 1, 2 * r + 1));
                var (cr, cg, cb) = img.GetPixel(px, py);
                Swatch.Background = new SolidColorBrush(Color.FromRgb(cr, cg, cb));
                HexText.Text = BgraImage.ToHex(cr, cg, cb);
                return;
            }
        }
        HexText.Text = $"{c.X:0}, {c.Y:0}";
    }

    private void Finish(object? result)
    {
        if (_done) return;
        _done = true;
        _complete?.Invoke(result);
    }
}
