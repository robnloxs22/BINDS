using Avalonia.Controls;
using Avalonia.Input;
using DoinkBinds.ViewModels;

namespace DoinkBinds.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
