using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DoinkBinds.ViewModels;

// The IDataObject drag & drop API is marked obsolete in Avalonia 11.3 in favour of the new
// DataTransfer API, but remains fully supported in 11.x; it keeps this code simple.
#pragma warning disable CS0618

namespace DoinkBinds.Views;

/// <summary>Timeline + step editor. Handles keyboard shortcuts, multi-select and drag &amp; drop reordering.</summary>
public partial class MacroEditorView : UserControl
{
    private const string DragFormat = "doink-steps";
    private List<ActionViewModel>? _dragging;

    public MacroEditorView()
    {
        InitializeComponent();
        StepList.SelectionChanged += (_, _) => (DataContext as MacroViewModel)?.SetSelection(StepList.SelectedItems?.OfType<ActionViewModel>() ?? Enumerable.Empty<ActionViewModel>());
        StepList.AddHandler(KeyDownEvent, StepList_KeyDown, RoutingStrategies.Tunnel);
        StepList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        StepList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        StepList.AddHandler(DragDrop.DropEvent, OnDrop);
        StepList.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropLine.IsVisible = false);
    }

    private MacroViewModel? Vm => DataContext as MacroViewModel;

    private void StepList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var handled = true;
        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back when OperatingSystem.IsMacOS() && ctrl:
                vm.DeleteStepCommand.Execute(null);
                break;
            case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                vm.MoveStepUpCommand.Execute(null);
                break;
            case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                vm.MoveStepDownCommand.Execute(null);
                break;
            case Key.D when ctrl:
                vm.DuplicateStepCommand.Execute(null);
                break;
            case Key.C when ctrl:
                vm.CopyStepsCommand.Execute(null);
                break;
            case Key.X when ctrl:
                vm.CutStepsCommand.Execute(null);
                break;
            case Key.V when ctrl:
                vm.PasteStepsCommand.Execute(null);
                break;
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y when ctrl:
                vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                vm.UndoCommand.Execute(null);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    // ------------------------------------------------------------------ drag & drop

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || !e.GetCurrentPoint(StepList).Properties.IsLeftButtonPressed) return;
        // Only start a drag from the grip handle, so normal clicks still select.
        if (e.Source is not Visual v || v.FindAncestorOfType<Border>(includeSelf: true) is not { } b ||
            !(b.Classes.Contains("grip") || b.FindAncestorOfType<Border>() is { } p && p.Classes.Contains("grip")))
            return;
        if (b.DataContext is not ActionViewModel item) return;

        var selected = StepList.SelectedItems?.OfType<ActionViewModel>().ToList() ?? new List<ActionViewModel>();
        _dragging = selected.Contains(item) ? selected : new List<ActionViewModel> { item };

        var data = new DataObject();
        data.Set(DragFormat, "steps");
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            DropLine.IsVisible = false;
            _dragging = null;
        }
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DragFormat)) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DragDropEffects.Move;
        var (index, y) = DropTarget(e.GetPosition(StepList));
        DropLine.IsVisible = index >= 0;
        DropLine.Width = Math.Max(0, StepList.Bounds.Width - 24);
        Canvas.SetLeft(DropLine, 12);
        Canvas.SetTop(DropLine, y);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropLine.IsVisible = false;
        if (Vm is not { } vm || _dragging is null || !e.Data.Contains(DragFormat)) return;
        var (index, _) = DropTarget(e.GetPosition(StepList));
        if (index < 0) return;
        vm.MoveSteps(_dragging, index);
        e.Handled = true;
    }

    /// <summary>Insertion index under the pointer and the y-coordinate (in the list) for the drop line.</summary>
    private (int Index, double Y) DropTarget(Point p)
    {
        var containers = StepList.GetRealizedContainers().OfType<ListBoxItem>()
            .Select(c => (Item: c, Index: StepList.IndexFromContainer(c), Top: c.TranslatePoint(new Point(0, 0), StepList)?.Y ?? 0))
            .Where(c => c.Index >= 0)
            .OrderBy(c => c.Index)
            .ToList();
        if (containers.Count == 0) return (-1, 0);
        foreach (var c in containers)
        {
            if (p.Y < c.Top + c.Item.Bounds.Height / 2) return (c.Index, c.Top);
        }
        var last = containers[^1];
        return (last.Index + 1, last.Top + last.Item.Bounds.Height);
    }
}
