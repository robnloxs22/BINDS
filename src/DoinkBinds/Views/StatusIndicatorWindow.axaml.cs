using Avalonia;
using Avalonia.Controls;
using DoinkBinds.Models;

namespace DoinkBinds.Views;

public partial class StatusIndicatorWindow : Window
{
    public StatusIndicatorWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PlaceTopCenter();
        Opened += (_, _) => PlaceTopCenter();
    }

    public void Update(AppStatus status, string text)
    {
        Label.Text = text;
        Dot.Classes.Set("recording", status == AppStatus.Recording);
        Dot.Classes.Set("playing", status == AppStatus.Playing);
        Dot.Classes.Set("paused", status == AppStatus.Paused);
    }

    /// <summary>Top-center of the primary monitor's working area.</summary>
    private void PlaceTopCenter()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var widthPx = (int)(Bounds.Width * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - widthPx) / 2, area.Y + (int)(10 * screen.Scaling));
    }
}
