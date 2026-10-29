using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DoinkBinds.Services;

namespace DoinkBinds.Views;

public partial class NotificationWindow : Window
{
    public NotificationWindow()
    {
        InitializeComponent();
        PointerPressed += (_, _) => Close();
    }

    public void Set(string title, string message, ToastKind kind)
    {
        TitleText.Text = title.ToUpperInvariant();
        MessageText.Text = message;
        var color = kind switch
        {
            ToastKind.Error => Color.Parse("#ED4245"),
            ToastKind.Success => Color.Parse("#23A55A"),
            _ => Color.Parse("#5865F2")
        };
        Card.BorderBrush = new SolidColorBrush(color);
        Icon.Foreground = new SolidColorBrush(color);
    }
}
