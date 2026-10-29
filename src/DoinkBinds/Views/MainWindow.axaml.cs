using Avalonia.Controls;
using Avalonia.Input;
using DoinkBinds.ViewModels;

namespace DoinkBinds.Views;

public partial class MainWindow : Window
{
    /// <summary>Set by the tray "Quit" item so closing really exits instead of hiding.</summary>
    public bool ReallyClose { get; set; }

    private bool _toldAboutTray;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            // "Close to tray": keep hotkeys and rules alive in the background.
            if (!ReallyClose && e.CloseReason == WindowCloseReason.WindowClosing &&
                DataContext is MainWindowViewModel { CloseToTray: true } && App.HasTray)
            {
                e.Cancel = true;
                Hide();
                if (!_toldAboutTray)
                {
                    _toldAboutTray = true;
                    App.Notify("Doink Binds is still running", "Hotkeys and rules keep working. Use the tray icon to open or quit.");
                }
            }
        };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc closes the settings sheet.
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { IsSettingsOpen: true } vm)
        {
            vm.IsSettingsOpen = false;
            e.Handled = true;
        }
    }
}
