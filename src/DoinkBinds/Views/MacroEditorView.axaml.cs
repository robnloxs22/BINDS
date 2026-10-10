using Avalonia.Controls;
using Avalonia.Input;
using DoinkBinds.ViewModels;

namespace DoinkBinds.Views;

/// <summary>Timeline + step editor for the selected macro.</summary>
public partial class MacroEditorView : UserControl
{
    public MacroEditorView()
    {
        InitializeComponent();
    }

    private void StepList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MacroViewModel vm) return;

        switch (e.Key)
        {
            case Key.Delete:
                vm.DeleteStepCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                vm.MoveStepUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                vm.MoveStepDownCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.DuplicateStepCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
