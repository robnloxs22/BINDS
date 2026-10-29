using System.Globalization;
using Avalonia.Data.Converters;
using DoinkBinds.Models;

namespace DoinkBinds.Converters;

/// <summary>Turns enum values into friendly UI labels.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public static readonly EnumDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "",
        MousePositionMode m => m switch
        {
            MousePositionMode.Absolute => "Absolute (x, y)",
            MousePositionMode.Relative => "Relative to cursor",
            MousePositionMode.Percentage => "Percentage of screen",
            MousePositionMode.PinPoint => "Pin-point (picked)",
            MousePositionMode.Window => "Relative to a window",
            MousePositionMode.Found => "Relative to last found image/text",
            _ => m.ToString()
        },
        MonitorAwareness a => a switch
        {
            MonitorAwareness.RecordedMonitor => "Monitor it was recorded on",
            MonitorAwareness.PrimaryMonitor => "Always the primary monitor",
            MonitorAwareness.VirtualDesktop => "Whole desktop (all monitors)",
            _ => a.ToString()
        },
        MacroActionType t => t switch
        {
            MacroActionType.MouseMove => "Mouse move",
            MacroActionType.MouseDown => "Mouse down",
            MacroActionType.MouseUp => "Mouse up",
            MacroActionType.MouseClick => "Mouse click",
            MacroActionType.MouseWheel => "Mouse wheel",
            MacroActionType.KeyDown => "Key down",
            MacroActionType.KeyUp => "Key up",
            MacroActionType.KeyPress => "Key press",
            MacroActionType.Wait => "Wait",
            MacroActionType.TypeText => "Type text",
            MacroActionType.RandomWait => "Random wait",
            MacroActionType.RunMacro => "Run another macro",
            MacroActionType.OpenApp => "Open app / file / website",
            MacroActionType.WindowAction => "Window action",
            MacroActionType.WaitFor => "Wait until…",
            MacroActionType.FindAndClick => "Find & click",
            MacroActionType.If => "If…",
            MacroActionType.Else => "Else",
            MacroActionType.EndIf => "End If",
            MacroActionType.Loop => "Loop",
            MacroActionType.EndLoop => "End Loop",
            MacroActionType.Break => "Break out of loop",
            MacroActionType.SetVariable => "Set variable",
            MacroActionType.ReadText => "Read text from screen",
            MacroActionType.Check => "Check (test)",
            MacroActionType.Notify => "Notification",
            MacroActionType.Screenshot => "Screenshot",
            MacroActionType.Comment => "Comment / label",
            MacroActionType.StopMacro => "Stop macro",
            _ => t.ToString()
        },
        MacroMouseButton b => b switch
        {
            MacroMouseButton.X1 => "Back (X1)",
            MacroMouseButton.X2 => "Forward (X2)",
            _ => b.ToString()
        },
        HotkeyScope s => s == HotkeyScope.Global ? "Global" : "Only when focused",
        ConditionType ct => ct switch
        {
            ConditionType.All => "ALL of these (AND)",
            ConditionType.Any => "ANY of these (OR)",
            ConditionType.ImageVisible => "Image is on screen",
            ConditionType.TextVisible => "Text is on screen",
            ConditionType.PixelColor => "Pixel has colour",
            ConditionType.AreaColor => "Area has colour",
            ConditionType.WindowExists => "Window is open",
            ConditionType.WindowFocused => "Window is focused",
            ConditionType.Variable => "Variable comparison",
            _ => ct.ToString()
        },
        TextMatchMode tm => tm switch
        {
            TextMatchMode.Contains => "contains",
            TextMatchMode.Equals => "is exactly",
            TextMatchMode.StartsWith => "starts with",
            TextMatchMode.Regex => "matches regex",
            TextMatchMode.NumberGreater => "has a number greater than",
            TextMatchMode.NumberLess => "has a number less than",
            TextMatchMode.NumberEquals => "has the number",
            TextMatchMode.AnyText => "has any text",
            _ => tm.ToString()
        },
        CompareOp op => op switch
        {
            CompareOp.Equals => "=",
            CompareOp.NotEquals => "≠",
            CompareOp.Greater => ">",
            CompareOp.Less => "<",
            CompareOp.GreaterOrEqual => "≥",
            CompareOp.LessOrEqual => "≤",
            CompareOp.Contains => "contains",
            CompareOp.IsEmpty => "is empty",
            _ => op.ToString()
        },
        FailAction fa => fa switch
        {
            FailAction.Stop => "Stop and show error",
            FailAction.Skip => "Continue with the next step",
            FailAction.Retry => "Keep trying forever",
            _ => fa.ToString()
        },
        LoopKind lk => lk switch
        {
            LoopKind.Count => "A number of times",
            LoopKind.While => "While a condition is true",
            LoopKind.Until => "Until a condition is true",
            LoopKind.Forever => "Forever (use Break)",
            _ => lk.ToString()
        },
        VariableOp vo => vo switch
        {
            VariableOp.Set => "Set to",
            VariableOp.Add => "Add",
            VariableOp.Subtract => "Subtract",
            VariableOp.Append => "Append text",
            _ => vo.ToString()
        },
        WindowOp wo => wo switch
        {
            WindowOp.Focus => "Bring to front",
            WindowOp.MoveResize => "Move / resize",
            WindowOp.Maximize => "Maximize",
            WindowOp.Minimize => "Minimize",
            WindowOp.Restore => "Restore",
            WindowOp.Close => "Close",
            WindowOp.ApplyLayout => "Apply a saved layout",
            WindowOp.WaitForWindow => "Wait until it opens",
            _ => wo.ToString()
        },
        RuleTriggerMode rt => rt == RuleTriggerMode.OnBecomeTrue ? "Once each time it becomes true" : "Repeatedly while true",
        WindowStateKind ws => ws.ToString(),
        _ => value.ToString()
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
