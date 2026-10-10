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
            _ => t.ToString()
        },
        MacroMouseButton b => b switch
        {
            MacroMouseButton.X1 => "Back (X1)",
            MacroMouseButton.X2 => "Forward (X2)",
            _ => b.ToString()
        },
        HotkeyScope s => s == HotkeyScope.Global ? "Global" : "Only when focused",
        _ => value.ToString()
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
