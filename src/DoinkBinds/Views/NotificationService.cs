using Avalonia.Threading;
using DoinkBinds.Services;

namespace DoinkBinds.Views;

/// <summary>Stacks up to four floating notifications in the top-right corner; each fades after a few seconds.</summary>
public sealed class NotificationService : INotificationService
{
    private const int MaxVisible = 4;
    private readonly List<NotificationWindow> _open = new();

    public void Notify(string title, string message, ToastKind kind = ToastKind.Info) => Dispatcher.UIThread.Post(() =>
    {
        while (_open.Count >= MaxVisible)
        {
            _open[0].Close();
        }

        var w = new NotificationWindow();
        w.Set(title, message, kind);
        w.Closed += (_, _) =>
        {
            _open.Remove(w);
            Relayout();
        };
        _open.Add(w);
        w.Show();
        Relayout();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 8 : 5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            w.Close();
        };
        timer.Start();
    });

    private void Relayout()
    {
        var y = 0.0;
        for (var i = 0; i < _open.Count; i++)
        {
            var h = _open[i].Bounds.Height > 0 ? _open[i].Bounds.Height : 80;
            if (_open[i].Screens.Primary is { } screen)
            {
                var area = screen.WorkingArea;
                var s = screen.Scaling;
                _open[i].Position = new Avalonia.PixelPoint(area.Right - (int)((_open[i].Width + 12) * s), area.Y + (int)((12 + y) * s));
            }
            y += h + 4;
        }
    }
}
