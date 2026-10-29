using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoinkBinds.Models;
using DoinkBinds.Services;
using DoinkBinds.Services.Automation;
using DoinkBinds.Views;

namespace DoinkBinds.ViewModels;

/// <summary>
/// Editable wrapper around a <see cref="Condition"/> node. Groups (AND / OR) hold child view models;
/// every change is written straight into the model and reported through <c>changed</c>.
/// </summary>
public sealed partial class ConditionViewModel : ViewModelBase
{
    private readonly EditorContext _ctx;
    private readonly Action _changed;

    public Condition Model { get; }
    public ConditionViewModel? Parent { get; }
    public ObservableCollection<ConditionViewModel> Children { get; } = new();

    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testPassed;
    [ObservableProperty] private bool _isTesting;

    public ConditionViewModel(Condition model, EditorContext ctx, Action changed, ConditionViewModel? parent = null)
    {
        Model = model;
        _ctx = ctx;
        _changed = changed;
        Parent = parent;
        RebuildChildren();
    }

    private void RebuildChildren()
    {
        Children.Clear();
        foreach (var c in Model.Children ?? new List<Condition>())
            Children.Add(new ConditionViewModel(c, _ctx, _changed, this));
    }

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (name is not null) OnPropertyChanged(name);
        OnPropertyChanged(nameof(Summary));
        _changed();
        Parent?.OnPropertyChanged(nameof(Summary));
    }

    public string Summary => Model.Describe();
    public bool HasParent => Parent is not null;

    // ------------------------------------------------------------------ type

    public ConditionType Type
    {
        get => Model.Type;
        set
        {
            if (value == Model.Type) return;
            var fresh = Condition.NewLeaf(value);
            // Keep what still makes sense: NOT, search area and window.
            fresh.Negate = Model.Negate;
            if (value is ConditionType.ImageVisible or ConditionType.TextVisible or ConditionType.AreaColor && Model.Region is not null)
                fresh.Region = Model.Region;
            if (value == ConditionType.All || value == ConditionType.Any)
                fresh.Children = Model.IsGroup ? Model.Children : new List<Condition> { Model.Clone() };
            CopyFrom(fresh);
            RebuildChildren();
            OnPropertyChanged(string.Empty);
            Changed(null);
        }
    }

    private void CopyFrom(Condition c)
    {
        Model.Type = c.Type; Model.Negate = c.Negate; Model.Children = c.Children; Model.Region = c.Region;
        Model.ImagePng = c.ImagePng; Model.ImageScale = c.ImageScale; Model.Threshold = c.Threshold; Model.Text = c.Text;
        Model.Match = c.Match; Model.CaseSensitive = c.CaseSensitive; Model.Color = c.Color; Model.Tolerance = c.Tolerance;
        Model.Percent = c.Percent; Model.Point = c.Point; Model.Window = c.Window; Model.Variable = c.Variable; Model.Op = c.Op;
    }

    public bool IsGroup => Model.IsGroup;
    public bool IsImage => Model.Type == ConditionType.ImageVisible;
    public bool IsText => Model.Type == ConditionType.TextVisible;
    public bool IsPixel => Model.Type == ConditionType.PixelColor;
    public bool IsArea => Model.Type == ConditionType.AreaColor;
    public bool IsWindow => Model.Type is ConditionType.WindowExists or ConditionType.WindowFocused;
    public bool IsVariable => Model.Type == ConditionType.Variable;
    public bool UsesRegion => IsImage || IsText || IsArea;
    public bool UsesColor => IsPixel || IsArea;

    public bool Negate
    {
        get => Model.Negate;
        set { if (value != Model.Negate) { Model.Negate = value; Changed(); } }
    }

    // ------------------------------------------------------------------ group

    [RelayCommand]
    private void AddChild(string? typeName)
    {
        if (!Enum.TryParse<ConditionType>(typeName, out var type)) type = ConditionType.ImageVisible;
        Model.Children ??= new List<Condition>();
        var c = Condition.NewLeaf(type);
        Model.Children.Add(c);
        Children.Add(new ConditionViewModel(c, _ctx, _changed, this));
        Changed(nameof(Children));
    }

    [RelayCommand]
    private void Remove()
    {
        if (Parent is null) return;
        Parent.Model.Children?.Remove(Model);
        Parent.Children.Remove(this);
        Parent.Changed(nameof(Children));
    }

    /// <summary>Wraps this (root) condition in an AND group so more conditions can be added.</summary>
    [RelayCommand]
    private void WrapInGroup(string? typeName)
    {
        var groupType = typeName == "Any" ? ConditionType.Any : ConditionType.All;
        if (Model.IsGroup)
        {
            AddChild(ConditionType.ImageVisible.ToString());
            return;
        }
        var inner = Model.Clone();
        inner.Negate = Model.Negate;
        CopyFrom(new Condition { Type = groupType, Children = new List<Condition> { inner, Condition.NewLeaf(ConditionType.TextVisible) } });
        RebuildChildren();
        OnPropertyChanged(string.Empty);
        Changed(null);
    }

    // ------------------------------------------------------------------ region

    public string RegionText => Model.Region is { IsEmpty: false } r ? r.ToString() : (Model.Region?.RelativeToWindow == true ? "Whole window" : "Whole screen");

    public bool RegionRelativeToWindow
    {
        get => Model.Region?.RelativeToWindow == true;
        set
        {
            Model.Region ??= new ScreenRegion();
            if (Model.Region.RelativeToWindow == value) return;
            // Convert between absolute and window-relative so the area stays where it is now.
            if (!Model.Region.IsEmpty && _ctx.Vision?.WindowOrigin(Model.Window) is { } o)
            {
                Model.Region.X += value ? -o.X : o.X;
                Model.Region.Y += value ? -o.Y : o.Y;
            }
            Model.Region.RelativeToWindow = value;
            Model.Window ??= new WindowTarget();
            OnPropertyChanged(nameof(RegionText));
            OnPropertyChanged(nameof(IsWindowRelevant));
            Changed();
        }
    }

    [RelayCommand]
    private async Task PickRegionAsync()
    {
        if (_ctx.Picker is null) return;
        var pick = await _ctx.Picker.PickRegionAsync(IsText ? "Drag a box around where the text appears" : "Drag a box around the search area");
        if (pick is null) return;
        SetRegion(pick.Region);
    }

    private void SetRegion(ScreenRegion region)
    {
        var relative = Model.Region?.RelativeToWindow == true;
        if (relative && _ctx.Vision?.WindowOrigin(Model.Window) is { } o)
        {
            region.X -= o.X;
            region.Y -= o.Y;
            region.RelativeToWindow = true;
        }
        Model.Region = region;
        OnPropertyChanged(nameof(RegionText));
        Changed();
    }

    [RelayCommand]
    private void ClearRegion()
    {
        var rel = Model.Region?.RelativeToWindow == true;
        Model.Region = rel ? new ScreenRegion { RelativeToWindow = true } : null;
        OnPropertyChanged(nameof(RegionText));
        Changed();
    }

    // ------------------------------------------------------------------ image

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail => _thumbnail ??= BitmapHelper.FromBase64Png(Model.ImagePng);
    public bool HasImage => !string.IsNullOrEmpty(Model.ImagePng);
    public string ImageInfo => HasImage && Thumbnail is { } t ? $"{t.PixelSize.Width}×{t.PixelSize.Height} px" : "No image yet";

    /// <summary>Snips the template image from the screen.</summary>
    [RelayCommand]
    private async Task CaptureImageAsync()
    {
        if (_ctx.Picker is null) return;
        var pick = await _ctx.Picker.PickRegionAsync("Drag a box tightly around the button / icon / picture to look for");
        if (pick?.Image is null)
        {
            if (pick is not null) _ctx.Library.Toast("Couldn't capture the screen" + (OperatingSystem.IsMacOS() ? " — grant Screen Recording permission" : ""), ToastKind.Error);
            return;
        }
        SetImage(pick.Image);
        if (Model.Threshold <= 0) Model.Threshold = 0.9;
    }

    public void SetImage(DoinkBinds.Vision.BgraImage image)
    {
        Model.ImagePng = image.ToBase64Png();
        Model.ImageScale = image.Scale;
        _thumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(ImageInfo));
        Changed();
    }

    public double Threshold
    {
        get => Model.Threshold > 0 ? Model.Threshold : 0.9;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0.5, 1.0), 2);
            if (Math.Abs(v - Model.Threshold) < 0.001) return;
            Model.Threshold = v;
            OnPropertyChanged(nameof(ThresholdText));
            Changed();
        }
    }

    public string ThresholdText => $"{Threshold * 100:0}% similar";

    // ------------------------------------------------------------------ text

    public string Text
    {
        get => Model.Text ?? "";
        set { if (value != Model.Text) { Model.Text = value; Changed(); } }
    }

    public TextMatchMode Match
    {
        get => Model.Match;
        set { if (value != Model.Match) { Model.Match = value; OnPropertyChanged(nameof(NeedsText)); Changed(); } }
    }

    public bool NeedsText => Model.Match != TextMatchMode.AnyText;

    public bool CaseSensitive
    {
        get => Model.CaseSensitive;
        set { if (value != Model.CaseSensitive) { Model.CaseSensitive = value; Changed(); } }
    }

    // ------------------------------------------------------------------ colour

    public string Color
    {
        get => Model.Color ?? "#FF0000";
        set
        {
            var v = value.Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            if (v == Model.Color) return;
            Model.Color = v.ToUpperInvariant();
            OnPropertyChanged(nameof(ColorBrush));
            Changed();
        }
    }

    public IBrush ColorBrush => DoinkBinds.Vision.BgraImage.TryParseHex(Model.Color, out var r, out var g, out var b)
        ? new SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, b))
        : Brushes.Transparent;

    public decimal Tolerance
    {
        get => Model.Tolerance;
        set { var v = (int)Math.Clamp(value, 0, 255); if (v != Model.Tolerance) { Model.Tolerance = v; Changed(); } }
    }

    public decimal Percent
    {
        get => (decimal)(Model.Percent > 0 ? Model.Percent : 1);
        set { var v = (double)Math.Clamp(value, 0.1m, 100m); if (Math.Abs(v - Model.Percent) > 0.0001) { Model.Percent = v; Changed(); } }
    }

    public string PointText => Model.Point is { } p ? p.ToString() : "Not set";

    /// <summary>Pixel: picks the point and its colour in one go.</summary>
    [RelayCommand]
    private async Task PickColorAsync()
    {
        if (_ctx.Picker is null) return;
        var pick = await _ctx.Picker.PickColorAsync(IsPixel ? "Click the exact pixel to watch" : "Click a pixel with the colour to look for");
        if (pick is null) return;
        Model.Color = pick.Hex;
        if (IsPixel) Model.Point = new MousePosition { Mode = MousePositionMode.Absolute, X = Math.Round(pick.Point.X), Y = Math.Round(pick.Point.Y) };
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(PointText));
        Changed();
    }

    // ------------------------------------------------------------------ window

    public bool IsWindowRelevant => IsWindow || Model.Region?.RelativeToWindow == true;

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

    [RelayCommand]
    private async Task PickWindowAsync()
    {
        var target = await WindowChooser.ChooseAsync(_ctx, "Which window?");
        if (target is null) return;
        Model.Window = target;
        OnPropertyChanged(nameof(WindowProcess));
        OnPropertyChanged(nameof(WindowTitle));
        Changed();
    }

    // ------------------------------------------------------------------ variable

    public string Variable
    {
        get => Model.Variable ?? "";
        set { if (value != Model.Variable) { Model.Variable = value.Trim().Trim('{', '}'); Changed(); } }
    }

    public CompareOp Op
    {
        get => Model.Op;
        set { if (value != Model.Op) { Model.Op = value; Changed(); } }
    }

    // ------------------------------------------------------------------ test

    [RelayCommand]
    private async Task TestAsync()
    {
        if (_ctx.Vision is null) return;
        IsTesting = true;
        TestResult = "Checking…";
        try
        {
            var vars = _ctx.Variables;
            var cond = Model.Clone();
            var r = await Task.Run(() => _ctx.Vision.Evaluate(cond, new VisionContext { GetVariable = n => vars?.Get(n) }));
            TestPassed = r.Met;
            TestResult = (r.Met ? "✓ TRUE" : "✗ FALSE") + (r.Detail is null ? "" : $" — {r.Detail}");
        }
        catch (Exception ex)
        {
            TestPassed = false;
            TestResult = $"Error: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}

/// <summary>Shared "pick a window from the open windows" dialog.</summary>
public static class WindowChooser
{
    public static async Task<WindowTarget?> ChooseAsync(EditorContext ctx, string title)
    {
        var windows = ctx.Platform?.Windows.ListWindows()
            .Where(w => !string.Equals(w.ProcessName, System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<WindowInfo>();
        if (windows.Count == 0)
        {
            var typed = await ctx.Dialogs.PromptAsync(title, "No windows could be listed. Type the app/process name (e.g. chrome, notepad, Safari):", "");
            return string.IsNullOrWhiteSpace(typed) ? null : new WindowTarget { ProcessName = typed.Trim() };
        }
        var labels = windows.Select(w => w.ToString()).ToList();
        var choice = await ctx.Dialogs.ChooseAsync(title, "Pick one of the open windows:", labels, labels[0]);
        if (choice is null) return null;
        var w = windows[labels.IndexOf(choice)];
        return new WindowTarget { ProcessName = w.ProcessName, Title = w.Title };
    }
}
