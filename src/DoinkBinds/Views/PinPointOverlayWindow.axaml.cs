using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using DoinkBinds.Models;
using DoinkBinds.Platform;

namespace DoinkBinds.Views;

/// <summary>Full-screen translucent crosshair overlay for one monitor.</summary>
public partial class PinPointOverlayWindow : Window
{
    private readonly IInputSimulator? _input;
    private readonly Action<DesktopPoint?>? _complete;
    private bool _done;

    // Required by the XAML loader / designer.
    public PinPointOverlayWindow()
    {
        InitializeComponent();
    }

    public PinPointOverlayWindow(Screen screen, IInputSimulator input, string? hint, Action<DesktopPoint?> complete) : this()
    {
        _input = input;
        _complete = complete;

        // Cover exactly this monitor. Bounds are in physical pixels; Width/Height are in DIPs.
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;

        if (!string.IsNullOrWhiteSpace(hint)) HintText.Text = hint;
        SetCrosshairVisible(false);

        Opened += (_, _) => Dispatcher.UIThread.Post(CenterHint, DispatcherPriority.Loaded);
        PointerMoved += (_, e) => UpdateCrosshair(e.GetPosition(Surface));
        PointerEntered += (_, e) => { SetCrosshairVisible(true); UpdateCrosshair(e.GetPosition(Surface)); };
        PointerExited += (_, _) => SetCrosshairVisible(false);
        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
    }

    private void CenterHint()
    {
        Hint.Measure(Size.Infinity);
        Canvas.SetLeft(Hint, Math.Max(0, (Bounds.Width - Hint.DesiredSize.Width) / 2));
    }

    private void SetCrosshairVisible(bool visible)
    {
        HLine.IsVisible = VLine.IsVisible = Ring.IsVisible = Dot.IsVisible = Badge.IsVisible = visible;
    }

    private void UpdateCrosshair(Point p)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;

        HLine.StartPoint = new Point(0, p.Y);
        HLine.EndPoint = new Point(w, p.Y);
        VLine.StartPoint = new Point(p.X, 0);
        VLine.EndPoint = new Point(p.X, h);

        Canvas.SetLeft(Ring, p.X - Ring.Width / 2);
        Canvas.SetTop(Ring, p.Y - Ring.Height / 2);
        Canvas.SetLeft(Dot, p.X - Dot.Width / 2);
        Canvas.SetTop(Dot, p.Y - Dot.Height / 2);

        // Show the real OS coordinate (what will be stored and replayed).
        var os = _input?.GetCursorPosition() ?? new DesktopPoint(p.X, p.Y);
        BadgeText.Text = $"{os.X:0}, {os.Y:0}";

        Badge.Measure(Size.Infinity);
        var bw = Badge.DesiredSize.Width;
        var bh = Badge.DesiredSize.Height;
        var bx = p.X + 20 + bw > w ? p.X - 20 - bw : p.X + 20;
        var by = p.Y + 20 + bh > h ? p.Y - 20 - bh : p.Y + 20;
        Canvas.SetLeft(Badge, bx);
        Canvas.SetTop(Badge, by);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) Finish(null);
        else if (props.IsLeftButtonPressed) Finish(_input?.GetCursorPosition());
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_input is null) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var pos = _input.GetCursorPosition();

        switch (e.Key)
        {
            case Key.Escape: Finish(null); break;
            case Key.Enter: Finish(pos); break;
            case Key.Left: _input.MoveMouse(pos.X - step, pos.Y); break;
            case Key.Right: _input.MoveMouse(pos.X + step, pos.Y); break;
            case Key.Up: _input.MoveMouse(pos.X, pos.Y - step); break;
            case Key.Down: _input.MoveMouse(pos.X, pos.Y + step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Finish(DesktopPoint? result)
    {
        if (_done) return;
        _done = true;
        _complete?.Invoke(result);
    }
}
