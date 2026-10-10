namespace DoinkBinds.Models;

/// <summary>A key plus optional modifiers, e.g. Ctrl+Shift+F6.</summary>
public sealed class Hotkey : IEquatable<Hotkey>
{
    public int KeyCode { get; set; }
    public HotkeyModifiers Modifiers { get; set; }

    public Hotkey() { }

    public Hotkey(int keyCode, HotkeyModifiers modifiers = HotkeyModifiers.None)
    {
        KeyCode = keyCode;
        Modifiers = modifiers;
    }

    public bool Matches(int keyCode, HotkeyModifiers modifiers) => keyCode == KeyCode && modifiers == Modifiers;

    public override string ToString()
    {
        var parts = new List<string>(5);
        var mac = OperatingSystem.IsMacOS();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add(mac ? "Option" : "Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add(mac ? "Cmd" : "Win");
        parts.Add(KeyCodes.GetName(KeyCode));
        return string.Join(" + ", parts);
    }

    public bool Equals(Hotkey? other) => other is not null && other.KeyCode == KeyCode && other.Modifiers == Modifiers;
    public override bool Equals(object? obj) => Equals(obj as Hotkey);
    public override int GetHashCode() => HashCode.Combine(KeyCode, Modifiers);

    public Hotkey Clone() => new(KeyCode, Modifiers);
}

/// <summary>A user-configurable binding for one of the app's built-in commands.</summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public Hotkey? Hotkey { get; set; }
    public HotkeyScope Scope { get; set; } = HotkeyScope.Global;

    public HotkeyBinding Clone() => new() { Action = Action, Hotkey = Hotkey?.Clone(), Scope = Scope };
}
