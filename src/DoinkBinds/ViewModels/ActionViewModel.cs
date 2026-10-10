using CommunityToolkit.Mvvm.ComponentModel;
using DoinkBinds.Converters;
using DoinkBinds.Models;

namespace DoinkBinds.ViewModels;

/// <summary>Editable wrapper around one <see cref="MacroAction"/> in the timeline.</summary>
public sealed partial class ActionViewModel : ViewModelBase
{
    private readonly Action<ActionViewModel> _changed;

    public MacroAction Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number))]
    private int _index;

    /// <summary>1-based step number for display.</summary>
    public int Number => Index + 1;
    [ObservableProperty] private bool _isCurrent;

    public ActionViewModel(MacroAction model, int index, Action<ActionViewModel> changed)
    {
        Model = model;
        _index = index;
        _changed = changed;
    }

    // ------------------------------------------------------------- type

    public MacroActionType Type
    {
        get => Model.Type;
        set
        {
            if (Model.Type == value) return;
            Model.Type = value;
            if (Model.IsMouseAction && Model.Position is null && value != MacroActionType.MouseWheel)
                Model.Position = new MousePosition();
            if (value == MacroActionType.MouseWheel && Model.WheelDelta == 0)
                Model.WheelDelta = 120;
            if (Model.IsKeyAction && Model.KeyCode == 0)
                Model.KeyCode = KeyCodes.Space;
            RaiseAll();
        }
    }

    public bool IsMouse => Model.IsMouseAction;
    public bool IsKey => Model.IsKeyAction;
    public bool IsWait => Model.Type == MacroActionType.Wait;
    public bool IsButton => Model.Type is MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MouseClick;
    public bool IsWheel => Model.Type == MacroActionType.MouseWheel;
    public bool HasPosition => Model.IsMouseAction && Model.Position is not null;

    // ------------------------------------------------------------- timing

    public decimal DelayMs
    {
        get => Model.DelayMs;
        set
        {
            var v = (int)Math.Clamp(value, 0, 3_600_000);
            if (v == Model.DelayMs) return;
            Model.DelayMs = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayText));
            _changed(this);
        }
    }

    public string DelayText => Model.DelayMs >= 1000 ? $"+{Model.DelayMs / 1000.0:0.00}s" : $"+{Model.DelayMs}ms";

    // ------------------------------------------------------------- mouse

    public MousePositionMode PositionMode
    {
        get => Model.Position?.Mode ?? MousePositionMode.Absolute;
        set
        {
            if (Model.Position is null || Model.Position.Mode == value) return;
            // Conversion between modes is done by MacroViewModel (it knows the surrounding steps).
            PositionModeChangeRequested?.Invoke(this, value);
        }
    }

    internal event Action<ActionViewModel, MousePositionMode>? PositionModeChangeRequested;

    public decimal X
    {
        get => (decimal)(Model.Position?.X ?? 0);
        set => SetPosition(p => p.X = (double)value);
    }

    public decimal Y
    {
        get => (decimal)(Model.Position?.Y ?? 0);
        set => SetPosition(p => p.Y = (double)value);
    }

    public decimal MonitorNumber
    {
        get => (Model.Position?.MonitorIndex ?? 0) + 1;
        set => SetPosition(p => p.MonitorIndex = Math.Max(0, (int)value - 1));
    }

    public bool IsPercentage => Model.Position?.Mode == MousePositionMode.Percentage;

    /// <summary>Upper bound for the X/Y editors (percentages are 0–100).</summary>
    public decimal CoordinateMax => IsPercentage ? 100 : 100_000;
    public decimal CoordinateMin => Model.Position?.Mode == MousePositionMode.Relative ? -100_000 : IsPercentage ? 0 : -100_000;

    private void SetPosition(Action<MousePosition> apply)
    {
        if (Model.Position is null) return;
        apply(Model.Position);
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(MonitorNumber));
        OnPropertyChanged(nameof(Detail));
        _changed(this);
    }

    public MacroMouseButton Button
    {
        get => Model.Button;
        set
        {
            if (Model.Button == value) return;
            Model.Button = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            _changed(this);
        }
    }

    public decimal WheelNotches
    {
        get => Model.WheelDelta / 120m;
        set
        {
            var d = (int)Math.Round(value * 120);
            if (d == Model.WheelDelta) return;
            Model.WheelDelta = d;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            _changed(this);
        }
    }

    public bool Horizontal
    {
        get => Model.Horizontal;
        set
        {
            if (Model.Horizontal == value) return;
            Model.Horizontal = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            _changed(this);
        }
    }

    // ------------------------------------------------------------- keyboard

    public KeyOption? Key
    {
        get => Options.Keys.FirstOrDefault(k => k.Code == Model.KeyCode)
               ?? new KeyOption(Model.KeyCode, KeyCodes.GetName(Model.KeyCode));
        set
        {
            if (value is null || value.Code == Model.KeyCode) return;
            Model.KeyCode = value.Code;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            _changed(this);
        }
    }

    // ------------------------------------------------------------- display

    public string Title => Model.Type switch
    {
        MacroActionType.MouseMove => "Move mouse",
        MacroActionType.MouseDown => $"{ButtonName} button down",
        MacroActionType.MouseUp => $"{ButtonName} button up",
        MacroActionType.MouseClick => $"{ButtonName} click",
        MacroActionType.MouseWheel => $"Scroll {(Model.Horizontal ? (Model.WheelDelta > 0 ? "right" : "left") : (Model.WheelDelta > 0 ? "up" : "down"))} ×{Math.Abs(Model.WheelDelta / 120.0):0.#}",
        MacroActionType.KeyDown => $"{KeyCodes.GetName(Model.KeyCode)} down",
        MacroActionType.KeyUp => $"{KeyCodes.GetName(Model.KeyCode)} up",
        MacroActionType.KeyPress => $"Press {KeyCodes.GetName(Model.KeyCode)}",
        MacroActionType.Wait => "Wait",
        _ => Model.Type.ToString()
    };

    private string ButtonName => EnumDisplayConverter.Instance.Convert(Model.Button, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? "";

    public string Detail
    {
        get
        {
            var parts = new List<string>(2);
            if (Model.Position is not null && Model.IsMouseAction)
                parts.Add($"{ModeShort(Model.Position.Mode)} {Model.Position}");
            if (Model.Window is { IsEmpty: false } w)
                parts.Add(w.ProcessName);
            return string.Join("  ·  ", parts);
        }
    }

    public string WindowText => Model.Window is { IsEmpty: false } w ? w.ToString() : "Any window";

    private static string ModeShort(MousePositionMode m) => m switch
    {
        MousePositionMode.Relative => "Δ",
        MousePositionMode.Percentage => "%",
        MousePositionMode.PinPoint => "pin",
        _ => "@"
    };

    /// <summary>Called after the model was changed externally (e.g. mode conversion or pin-point pick).</summary>
    public void RaiseAll()
    {
        OnPropertyChanged(string.Empty);
        _changed(this);
    }
}
