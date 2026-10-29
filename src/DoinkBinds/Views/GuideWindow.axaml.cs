using Avalonia;
using Avalonia.Controls;
using DoinkBinds.ViewModels;

namespace DoinkBinds.Views;

public partial class GuideWindow : Window
{
    private bool _finishing;

    public GuideWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PlaceTopRight();
        Closing += (_, _) =>
        {
            // Closing with the window's X counts as "Finish" so nothing recorded is lost.
            if (!_finishing && DataContext is GuideViewModel vm)
            {
                _finishing = true;
                vm.FinishCommand.Execute(null);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GuideViewModel vm)
                vm.CloseRequested += (_, _) =>
                {
                    _finishing = true;
                    Close();
                };
        };
    }

    private void PlaceTopRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var a = screen.WorkingArea;
        Position = new PixelPoint(a.Right - (int)((Width + 24) * screen.Scaling), a.Y + (int)(60 * screen.Scaling));
    }
}
