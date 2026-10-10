namespace DoinkBinds.Models;

/// <summary>A monitor's bounds in the platform's global desktop coordinate space.</summary>
public readonly record struct ScreenInfo(int Index, double X, double Y, double Width, double Height, bool IsPrimary)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public override string ToString() =>
        $"Monitor {Index + 1}{(IsPrimary ? " (primary)" : "")} — {Width:0}×{Height:0} at {X:0},{Y:0}";
}

/// <summary>A simple point in desktop coordinates.</summary>
public readonly record struct DesktopPoint(double X, double Y)
{
    public override string ToString() => $"{X:0}, {Y:0}";
}
