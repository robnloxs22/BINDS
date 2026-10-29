using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Converters;
using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;

namespace DoinkBinds.ViewModels;

/// <summary>A macro reference for the "Run another macro" picker.</summary>
public sealed record MacroRef(Guid Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A layout reference for "Apply a saved layout".</summary>
public sealed record LayoutRef(Guid Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Editable wrapper around one <see cref="MacroAction"/> in the timeline.</summary>
public sealed partial class ActionViewModel : ViewModelBase
{
    private readonly Action<ActionViewModel> _changed;
    private readonly EditorContext? _ctx;
    private ConditionViewModel? _condition;

    public MacroAction Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number))]
    private int _index;

    /// <summary>1-based step number for display.</summary>
    public int Number => Index + 1;

    [ObservableProperty] private bool _isCurrent;

    /// <summary>Nesting depth inside If / Loop blocks (set by the macro view model).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndentMargin))]
    private int _indent;

    public Thickness IndentMargin => new(Indent * 18, 0, 0, 0);

    [ObservableProperty] private string? _readPreview;

    public ActionViewModel(MacroAction model, int index, Action<ActionViewModel> changed, EditorContext? ctx = null)
    {
        Model = model;
        _index = index;
        _changed = changed;
        _ctx = ctx;
    }

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (name is not null) OnPropertyChanged(name);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        _changed(this);
    }

    // ================================================================== type

    public MacroActionType Type
    {
        get => Model.Type;
        set
        {
            if (Model.Type == value) return;
            Model.Type = value;
            StepFactory.ApplyDefaults(Model);
            _condition = null;
            RaiseAll();
        }
    }

    private MacroActionType T => Model.Type;

    // Category flags (badge colour + icon in the timeline)
    public bool IsMouse => Model.IsMouseAction;
    public bool IsKey => Model.IsKeyAction || T == MacroActionType.TypeText;
    public bool IsWait => T is MacroActionType.Wait or MacroActionType.RandomWait;
    public bool IsLogic => T is MacroActionType.If or MacroActionType.Else or MacroActionType.EndIf or MacroActionType.Loop
        or MacroActionType.EndLoop or MacroActionType.Break or MacroActionType.StopMacro or MacroActionType.SetVariable;
    public bool IsScreen => T is MacroActionType.WaitFor or MacroActionType.FindAndClick or MacroActionType.Check
        or MacroActionType.ReadText or MacroActionType.Screenshot;
    public bool IsSystem => T is MacroActionType.RunMacro or MacroActionType.OpenApp or MacroActionType.WindowAction or MacroActionType.Notify;
    public bool IsComment => T == MacroActionType.Comment;
    public bool IsBlockMarker => T is MacroActionType.Else or MacroActionType.EndIf or MacroActionType.EndLoop;

    // Editor section flags
    public bool IsButton => T is MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MouseClick;
    public bool IsClick => T == MacroActionType.MouseClick;
    public bool IsWheel => T == MacroActionType.MouseWheel;
    public bool IsKeyStep => Model.IsKeyAction;
    public bool IsTypeText => T == MacroActionType.TypeText;
    public bool IsRandomWait => T == MacroActionType.RandomWait;
    public bool IsRunMacro => T == MacroActionType.RunMacro;
    public bool IsOpenApp => T == MacroActionType.OpenApp;
    public bool IsWindowAction => T == MacroActionType.WindowAction;
    public bool IsFindAndClick => T == MacroActionType.FindAndClick;
    public bool IsLoop => T == MacroActionType.Loop;
    public bool IsLoopCount => IsLoop && Model.LoopKind == LoopKind.Count;
    public bool IsSetVariable => T == MacroActionType.SetVariable;
    public bool IsReadText => T == MacroActionType.ReadText;
    public bool IsNotify => T == MacroActionType.Notify;
    public bool IsScreenshot => T == MacroActionType.Screenshot;
    public bool IsStop => T == MacroActionType.StopMacro;
    public bool UsesCondition => Model.UsesCondition;
    public bool UsesTimeout => T is MacroActionType.WaitFor or MacroActionType.FindAndClick or MacroActionType.WindowAction or MacroActionType.OpenApp;
    public bool UsesOnFail => T is MacroActionType.WaitFor or MacroActionType.FindAndClick or MacroActionType.Check
        or MacroActionType.WindowAction or MacroActionType.OpenApp;
    public bool UsesRegion => T is MacroActionType.ReadText or MacroActionType.Screenshot || (IsWindowAction && Model.WindowOp == WindowOp.MoveResize);
    public bool IsMoveResize => IsWindowAction && Model.WindowOp == WindowOp.MoveResize;
    public bool IsApplyLayout => IsWindowAction && Model.WindowOp == WindowOp.ApplyLayout;
    public bool UsesWindowTarget => (IsWindowAction && !IsApplyLayout) || IsOpenApp || Model.Position?.Mode == MousePositionMode.Window;
    public bool UsesButton => IsButton || IsFindAndClick;
    public bool UsesClickCount => IsClick || IsFindAndClick;
    public bool UsesText => IsTypeText || IsNotify || IsOpenApp || IsSetVariable || IsScreenshot || IsStop;
    public bool HasDelay => !IsBlockMarker && !IsComment;

    public string TextLabel => T switch
    {
        MacroActionType.TypeText => "TEXT TO TYPE",
        MacroActionType.Notify => "MESSAGE",
        MacroActionType.OpenApp => "APP PATH, FILE OR WEBSITE",
        MacroActionType.SetVariable => "VALUE",
        MacroActionType.Screenshot => "CAPTION",
        MacroActionType.StopMacro => "MESSAGE (OPTIONAL)",
        _ => "TEXT"
    };

    public string TextHint => T switch
    {
        MacroActionType.TypeText => "Hello {username}! — use {variables}, {date}, {random:1-6}",
        MacroActionType.OpenApp => OperatingSystem.IsWindows() ? @"C:\Windows\notepad.exe or https://example.com" : "/Applications/Safari.app or https://example.com",
        MacroActionType.SetVariable => "1, some text, or {other}",
        _ => ""
    };

    // ================================================================== common fields

    public string Label
    {
        get => Model.Note ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == Model.Note) return;
            Model.Note = v;
            Changed();
        }
    }

    public decimal DelayMs
    {
        get => Model.DelayMs;
        set
        {
            var v = (int)Math.Clamp(value, 0, 86_400_000);
            if (v == Model.DelayMs) return;
            Model.DelayMs = v;
            OnPropertyChanged(nameof(DelayText));
            Changed();
        }
    }

    public decimal MaxDelayMs
    {
        get => Model.MaxDelayMs;
        set
        {
            var v = (int)Math.Clamp(value, 0, 86_400_000);
            if (v == Model.MaxDelayMs) return;
            Model.MaxDelayMs = v;
            OnPropertyChanged(nameof(DelayText));
            Changed();
        }
    }

    public string DelayText => IsBlockMarker || IsComment ? "" :
        T == MacroActionType.RandomWait ? $"{Fmt(Model.DelayMs)}–{Fmt(Model.MaxDelayMs)}" : $"+{Fmt(Model.DelayMs)}";

    private static string Fmt(int ms) => ms >= 60_000 ? $"{ms / 60000.0:0.#}m" : ms >= 1000 ? $"{ms / 1000.0:0.##}s" : $"{ms}ms";

    public string Text
    {
        get => Model.Text ?? "";
        set { if (value != Model.Text) { Model.Text = value; Changed(); } }
    }

    public string Arguments
    {
        get => Model.Arguments ?? "";
        set { var v = string.IsNullOrWhiteSpace(value) ? null : value; if (v != Model.Arguments) { Model.Arguments = v; Changed(); } }
    }

    public decimal TimeoutSeconds
    {
        get => Model.TimeoutMs / 1000m;
        set { var v = (int)Math.Clamp(value * 1000, 0, 86_400_000); if (v != Model.TimeoutMs) { Model.TimeoutMs = v; Changed(); } }
    }

    public FailAction OnFail
    {
        get => Model.OnFail;
        set { if (value != Model.OnFail) { Model.OnFail = value; Changed(); } }
    }

    public bool Flag
    {
        get => Model.Flag;
        set { if (value != Model.Flag) { Model.Flag = value; Changed(); } }
    }

    // ================================================================== mouse

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

    public bool HasPosition => Model.IsMouseAction && Model.Position is not null;

    /// <summary>Button / click / wheel steps can act wherever the cursor is instead of moving first.</summary>
    public bool UseCurrentCursor
    {
        get => Model.IsMouseAction && Model.Position is null;
        set
        {
            if (value == UseCurrentCursor || T == MacroActionType.MouseMove) return;
            Model.Position = value ? null : new MousePosition { X = _ctx?.Positions.GetCursorPosition().X ?? 0, Y = _ctx?.Positions.GetCursorPosition().Y ?? 0 };
            OnPropertyChanged(nameof(HasPosition));
            RaisePositionProps();
            Changed();
        }
    }

    public bool CanUseCurrentCursor => Model.IsMouseAction && T != MacroActionType.MouseMove;

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
    public decimal CoordinateMax => IsPercentage ? 100 : 100_000;
    public decimal CoordinateMin => IsPercentage ? 0 : -100_000;

    private void SetPosition(Action<MousePosition> apply)
    {
        if (Model.Position is null) return;
        apply(Model.Position);
        RaisePositionProps();
        Changed(null);
    }

    private void RaisePositionProps()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(MonitorNumber));
        OnPropertyChanged(nameof(PositionMode));
        OnPropertyChanged(nameof(IsPercentage));
        OnPropertyChanged(nameof(UsesWindowTarget));
    }

    public MacroMouseButton Button
    {
        get => Model.Button;
        set { if (Model.Button != value) { Model.Button = value; Changed(); } }
    }

    public decimal ClickCount
    {
        get => Math.Max(1, Model.Count);
        set { var v = (int)Math.Clamp(value, 1, 10); if (v != Model.Count) { Model.Count = v; Changed(); } }
    }

    public decimal WheelNotches
    {
        get => Model.WheelDelta / 120m;
        set
        {
            var d = (int)Math.Round(value * 120);
            if (d == Model.WheelDelta) return;
            Model.WheelDelta = d;
            Changed();
        }
    }

    public bool Horizontal
    {
        get => Model.Horizontal;
        set { if (Model.Horizontal != value) { Model.Horizontal = value; Changed(); } }
    }

    public decimal OffsetX
    {
        get => Model.OffsetX;
        set { var v = (int)value; if (v != Model.OffsetX) { Model.OffsetX = v; Changed(); } }
    }

    public decimal OffsetY
    {
        get => Model.OffsetY;
        set { var v = (int)value; if (v != Model.OffsetY) { Model.OffsetY = v; Changed(); } }
    }

    // ================================================================== keyboard / text

    public KeyOption? Key
    {
        get => Options.Keys.FirstOrDefault(k => k.Code == Model.KeyCode)
               ?? new KeyOption(Model.KeyCode, KeyCodes.GetName(Model.KeyCode));
        set
        {
            if (value is null || value.Code == Model.KeyCode) return;
            Model.KeyCode = value.Code;
            Changed();
        }
    }

    /// <summary>TypeText: delay between characters.</summary>
    public decimal CharDelayMs
    {
        get => Model.Count;
        set { var v = (int)Math.Clamp(value, 0, 5000); if (v != Model.Count) { Model.Count = v; Changed(); } }
    }

    // ================================================================== conditions / loops

    public ConditionViewModel? Condition
    {
        get
        {
            if (!UsesCondition || _ctx is null) return null;
            Model.Condition ??= Models.Condition.NewLeaf(ConditionType.ImageVisible);
            return _condition ??= new ConditionViewModel(Model.Condition, _ctx, () => Changed(null));
        }
    }

    public LoopKind LoopKind
    {
        get => Model.LoopKind;
        set
        {
            if (value == Model.LoopKind) return;
            Model.LoopKind = value;
            StepFactory.ApplyDefaults(Model);
            _condition = null;
            OnPropertyChanged(nameof(UsesCondition));
            OnPropertyChanged(nameof(Condition));
            OnPropertyChanged(nameof(IsLoopCount));
            Changed();
        }
    }

    public decimal Count
    {
        get => Model.Count;
        set { var v = (int)Math.Clamp(value, 0, 10_000_000); if (v != Model.Count) { Model.Count = v; Changed(); } }
    }

    // ================================================================== variables

    public string VariableName
    {
        get => Model.VariableName ?? "";
        set { var v = value.Trim().Trim('{', '}'); if (v != Model.VariableName) { Model.VariableName = v; Changed(); } }
    }

    public VariableOp VariableOp
    {
        get => Model.VariableOp;
        set { if (value != Model.VariableOp) { Model.VariableOp = value; Changed(); } }
    }

    // ================================================================== region (ReadText / Screenshot / MoveResize)

    public string RegionText => Model.Region is { IsEmpty: false } r ? r.ToString() : IsMoveResize ? "Not set" : "Whole screen";

    public decimal RegionX { get => (decimal)(Model.Region?.X ?? 0); set => SetRegion(r => r.X = (double)value); }
    public decimal RegionY { get => (decimal)(Model.Region?.Y ?? 0); set => SetRegion(r => r.Y = (double)value); }
    public decimal RegionWidth { get => (decimal)(Model.Region?.Width ?? 0); set => SetRegion(r => r.Width = (double)value); }
    public decimal RegionHeight { get => (decimal)(Model.Region?.Height ?? 0); set => SetRegion(r => r.Height = (double)value); }

    private void SetRegion(Action<ScreenRegion> apply)
    {
        Model.Region ??= new ScreenRegion();
        apply(Model.Region);
        OnPropertyChanged(nameof(RegionText));
        Changed(null);
    }

    private void RaiseRegion()
    {
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(RegionX));
        OnPropertyChanged(nameof(RegionY));
        OnPropertyChanged(nameof(RegionWidth));
        OnPropertyChanged(nameof(RegionHeight));
    }

    [RelayCommand]
    private async Task PickRegionAsync()
    {
        if (_ctx?.Picker is null) return;
        var pick = await _ctx.Picker.PickRegionAsync(IsMoveResize ? "Drag where the window should go" : "Drag a box around the area");
        if (pick is null) return;
        Model.Region = pick.Region;
        RaiseRegion();
        Changed(null);
    }

    [RelayCommand]
    private void ClearRegion()
    {
        Model.Region = new ScreenRegion();
        RaiseRegion();
        Changed(null);
    }

    /// <summary>MoveResize: copy the target window's current position/size.</summary>
    [RelayCommand]
    private void UseWindowCurrentBounds()
    {
        if (_ctx?.Platform is null || Model.Window is null) return;
        var w = _ctx.Platform.Windows.ListWindows().FirstOrDefault(x =>
            (string.IsNullOrWhiteSpace(Model.Window.ProcessName) || string.Equals(x.ProcessName, Model.Window.ProcessName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(Model.Window.Title) || x.Title.Contains(Model.Window.Title, StringComparison.OrdinalIgnoreCase)));
        if (w is null)
        {
            _ctx.Library.Toast($"Window \"{Model.Window}\" is not open", ToastKind.Error);
            return;
        }
        Model.Region = new ScreenRegion { X = w.X, Y = w.Y, Width = w.Width, Height = w.Height };
        RaiseRegion();
        Changed(null);
    }

    [RelayCommand]
    private async Task TestReadTextAsync()
    {
        if (_ctx?.Vision is null) return;
        ReadPreview = "Reading…";
        var region = Model.Region?.Clone();
        var window = Model.Window?.Clone();
        var text = await Task.Run(() => _ctx.Vision.ReadText(region, window));
        ReadPreview = string.IsNullOrWhiteSpace(text)
            ? (_ctx.Vision.IsOcrAvailable ? "(no text found)" : "Text reading is unavailable on this computer")
            : $"\"{text}\"";
    }

    // ================================================================== windows

    public WindowOp WindowOp
    {
        get => Model.WindowOp;
        set
        {
            if (value == Model.WindowOp) return;
            Model.WindowOp = value;
            if (value == WindowOp.MoveResize) Model.Region ??= new ScreenRegion();
            OnPropertyChanged(nameof(IsMoveResize));
            OnPropertyChanged(nameof(IsApplyLayout));
            OnPropertyChanged(nameof(UsesRegion));
            OnPropertyChanged(nameof(UsesWindowTarget));
            RaiseRegion();
            Changed();
        }
    }

    public string WindowProcess
    {
        get => Model.Window?.ProcessName ?? "";
        set
        {
            Model.Window ??= new WindowTarget();
            if (value == Model.Window.ProcessName) return;
            Model.Window.ProcessName = value.Trim();
            Changed();
        }
    }

    public string WindowTitle
    {
        get => Model.Window?.Title ?? "";
        set
        {
            Model.Window ??= new WindowTarget();
            if (value == Model.Window.Title) return;
            Model.Window.Title = value;
            Changed();
        }
    }

    public string WindowText => Model.Window is { IsEmpty: false } w ? w.ToString() : "Any window";

    [RelayCommand]
    private async Task PickWindowAsync()
    {
        if (_ctx is null) return;
        var t = await WindowChooser.ChooseAsync(_ctx, "Which window?");
        if (t is null) return;
        Model.Window = t;
        OnPropertyChanged(nameof(WindowProcess));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(WindowText));
        Changed(null);
    }

    public IReadOnlyList<LayoutRef> Layouts =>
        _ctx?.Automation?.Data.Layouts.Select(l => new LayoutRef(l.Id, l.Name)).ToList() ?? new List<LayoutRef>();

    public LayoutRef? SelectedLayout
    {
        get => Layouts.FirstOrDefault(l => l.Id == Model.LayoutId);
        set { if (value is not null && value.Id != Model.LayoutId) { Model.LayoutId = value.Id; Changed(); } }
    }

    // ================================================================== run macro / open app

    public IReadOnlyList<MacroRef> Macros =>
        _ctx?.MacroLibrary?.Macros.OrderBy(m => m.Name).Select(m => new MacroRef(m.Id, m.Name)).ToList() ?? new List<MacroRef>();

    public MacroRef? SelectedMacro
    {
        get => Macros.FirstOrDefault(m => m.Id == Model.TargetMacroId);
        set
        {
            if (value is null || value.Id == Model.TargetMacroId) return;
            Model.TargetMacroId = value.Id;
            Model.TargetMacroName = value.Name;
            Changed();
        }
    }

    [RelayCommand]
    private async Task BrowseAppAsync()
    {
        if (_ctx is null) return;
        var path = await _ctx.Dialogs.PickAnyFileAsync("Choose an app or file to open");
        if (path is null) return;
        // macOS: picking a file inside an .app bundle → open the bundle itself.
        var app = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS() && app > 0) path = path[..(app + 4)];
        Text = path;
        OnPropertyChanged(nameof(Text));
    }

    // ================================================================== display

    public string Title => !string.IsNullOrWhiteSpace(Model.Note) && !IsMouse && !Model.IsKeyAction ? Model.Note! : DefaultTitle;

    private string DefaultTitle => T switch
    {
        MacroActionType.MouseMove => "Move mouse",
        MacroActionType.MouseDown => $"{ButtonName} button down",
        MacroActionType.MouseUp => $"{ButtonName} button up",
        MacroActionType.MouseClick => Model.Count >= 2 ? $"{ButtonName} double-click" : $"{ButtonName} click",
        MacroActionType.MouseWheel => $"Scroll {(Model.Horizontal ? (Model.WheelDelta > 0 ? "right" : "left") : (Model.WheelDelta > 0 ? "up" : "down"))} ×{Math.Abs(Model.WheelDelta / 120.0):0.#}",
        MacroActionType.KeyDown => $"{KeyCodes.GetName(Model.KeyCode)} down",
        MacroActionType.KeyUp => $"{KeyCodes.GetName(Model.KeyCode)} up",
        MacroActionType.KeyPress => $"Press {KeyCodes.GetName(Model.KeyCode)}",
        MacroActionType.Wait => "Wait",
        MacroActionType.RandomWait => "Random wait",
        MacroActionType.TypeText => $"Type \"{Clip(Model.Text, 40)}\"",
        MacroActionType.RunMacro => $"Run macro \"{Model.TargetMacroName ?? "?"}\"",
        MacroActionType.OpenApp => $"Open {Clip(System.IO.Path.GetFileName(Model.Text?.TrimEnd('/', '\\')) is { Length: > 0 } f ? f : Model.Text, 40)}",
        MacroActionType.WindowAction => Model.WindowOp == WindowOp.ApplyLayout ? $"Apply layout \"{SelectedLayout?.Name ?? "?"}\"" : $"{Display(Model.WindowOp)}: {WindowText}",
        MacroActionType.WaitFor => $"Wait until {Model.Condition?.Describe() ?? "…"}",
        MacroActionType.FindAndClick => $"Find & click {Model.Condition?.Describe() ?? "…"}",
        MacroActionType.If => $"If {Model.Condition?.Describe() ?? "…"}",
        MacroActionType.Else => "Else",
        MacroActionType.EndIf => "End If",
        MacroActionType.Loop => Model.LoopKind switch
        {
            LoopKind.Count => $"Repeat {Model.Count} times",
            LoopKind.While => $"Repeat while {Model.Condition?.Describe() ?? "…"}",
            LoopKind.Until => $"Repeat until {Model.Condition?.Describe() ?? "…"}",
            _ => "Repeat forever"
        },
        MacroActionType.EndLoop => "End Loop",
        MacroActionType.Break => "Break out of loop",
        MacroActionType.SetVariable => Model.VariableOp switch
        {
            VariableOp.Add => $"{{{Model.VariableName}}} += {Model.Text}",
            VariableOp.Subtract => $"{{{Model.VariableName}}} −= {Model.Text}",
            VariableOp.Append => $"{{{Model.VariableName}}} append \"{Clip(Model.Text, 30)}\"",
            _ => $"{{{Model.VariableName}}} = {Clip(Model.Text, 30)}"
        },
        MacroActionType.ReadText => $"Read text → {{{Model.VariableName}}}",
        MacroActionType.Check => $"Check: {Model.Condition?.Describe() ?? "…"}",
        MacroActionType.Notify => $"Notify: {Clip(Model.Text, 40)}",
        MacroActionType.Screenshot => "Screenshot",
        MacroActionType.Comment => Model.Note ?? "Comment",
        MacroActionType.StopMacro => Model.Flag ? "Stop macro (failed)" : "Stop macro",
        _ => Model.Type.ToString()
    };

    private static string Display(object e) =>
        EnumDisplayConverter.Instance.Convert(e, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? e.ToString()!;

    private static string Clip(string? s, int n) => s is null ? "" : s.Length > n ? s[..(n - 1)] + "…" : s.Replace('\n', ' ');

    private string ButtonName => Display(Model.Button);

    public string Detail
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Model.Note) && (IsMouse || Model.IsKeyAction)) parts.Add(Model.Note!);
            if (Model.IsMouseAction)
                parts.Add(Model.Position is { } p ? $"{ModeShort(p.Mode)} {p}" : "at cursor");
            if (IsFindAndClick && (Model.OffsetX != 0 || Model.OffsetY != 0)) parts.Add($"offset {Model.OffsetX:+0;-0},{Model.OffsetY:+0;-0}");
            if (Model.TimeoutMs > 0 && UsesTimeout) parts.Add($"timeout {Model.TimeoutMs / 1000.0:0.#}s");
            if (UsesOnFail && Model.OnFail != FailAction.Stop) parts.Add(Model.OnFail == FailAction.Skip ? "continue on fail" : "retry forever");
            if (Model.Window is { IsEmpty: false } w && (IsMouse || Model.IsKeyAction)) parts.Add(w.ProcessName);
            if (UsesRegion && Model.Region is { IsEmpty: false } r) parts.Add(r.ToString());
            return string.Join("  ·  ", parts);
        }
    }

    private static string ModeShort(MousePositionMode m) => m switch
    {
        MousePositionMode.Relative => "Δ",
        MousePositionMode.Percentage => "%",
        MousePositionMode.PinPoint => "pin",
        MousePositionMode.Window => "",
        MousePositionMode.Found => "",
        _ => "@"
    };

    /// <summary>Called after the model was changed externally (e.g. mode conversion or pin-point pick).</summary>
    public void RaiseAll()
    {
        _condition = null;
        OnPropertyChanged(string.Empty);
        _changed(this);
    }
}
