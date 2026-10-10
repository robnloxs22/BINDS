namespace DoinkBinds.Models;

/// <summary>
/// Platform-neutral key codes. We use the Windows virtual-key numbering as the canonical
/// representation; the macOS platform layer translates to/from Carbon key codes.
/// </summary>
public static class KeyCodes
{
    public const int Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Pause = 0x13, CapsLock = 0x14,
        Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, PrintScreen = 0x2C, Insert = 0x2D, Delete = 0x2E,
        D0 = 0x30, A = 0x41, Z = 0x5A,
        LWin = 0x5B, RWin = 0x5C, Apps = 0x5D,
        NumPad0 = 0x60, NumPad9 = 0x69, Multiply = 0x6A, Add = 0x6B, Separator = 0x6C, Subtract = 0x6D,
        Decimal = 0x6E, Divide = 0x6F,
        F1 = 0x70, F24 = 0x87,
        NumLock = 0x90, ScrollLock = 0x91,
        Shift = 0x10, Control = 0x11, Alt = 0x12,
        LShift = 0xA0, RShift = 0xA1, LControl = 0xA2, RControl = 0xA3, LAlt = 0xA4, RAlt = 0xA5,
        VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF,
        MediaNext = 0xB0, MediaPrev = 0xB1, MediaStop = 0xB2, MediaPlayPause = 0xB3,
        Semicolon = 0xBA, EqualSign = 0xBB, Comma = 0xBC, Minus = 0xBD, Period = 0xBE, Slash = 0xBF,
        Backtick = 0xC0, LBracket = 0xDB, Backslash = 0xDC, RBracket = 0xDD, Quote = 0xDE;

    public static int F(int n) => F1 + (n - 1);

    private static readonly Dictionary<int, string> Names = BuildNames();

    private static Dictionary<int, string> BuildNames()
    {
        var d = new Dictionary<int, string>
        {
            [Backspace] = "Backspace", [Tab] = "Tab", [Enter] = "Enter", [Pause] = "Pause", [CapsLock] = "Caps Lock",
            [Escape] = "Esc", [Space] = "Space", [PageUp] = "Page Up", [PageDown] = "Page Down", [End] = "End",
            [Home] = "Home", [Left] = "Left", [Up] = "Up", [Right] = "Right", [Down] = "Down",
            [PrintScreen] = "Print Screen", [Insert] = "Insert", [Delete] = "Delete",
            [LWin] = OperatingSystem.IsMacOS() ? "Left Cmd" : "Left Win",
            [RWin] = OperatingSystem.IsMacOS() ? "Right Cmd" : "Right Win",
            [Apps] = "Menu",
            [Multiply] = "Num *", [Add] = "Num +", [Separator] = "Num Enter", [Subtract] = "Num -",
            [Decimal] = "Num .", [Divide] = "Num /",
            [NumLock] = "Num Lock", [ScrollLock] = "Scroll Lock",
            [Shift] = "Shift", [Control] = "Ctrl", [Alt] = OperatingSystem.IsMacOS() ? "Option" : "Alt",
            [LShift] = "Left Shift", [RShift] = "Right Shift", [LControl] = "Left Ctrl", [RControl] = "Right Ctrl",
            [LAlt] = OperatingSystem.IsMacOS() ? "Left Option" : "Left Alt",
            [RAlt] = OperatingSystem.IsMacOS() ? "Right Option" : "Right Alt",
            [VolumeMute] = "Mute", [VolumeDown] = "Volume Down", [VolumeUp] = "Volume Up",
            [MediaNext] = "Next Track", [MediaPrev] = "Previous Track", [MediaStop] = "Stop Media",
            [MediaPlayPause] = "Play/Pause Media",
            [Semicolon] = ";", [EqualSign] = "=", [Comma] = ",", [Minus] = "-", [Period] = ".", [Slash] = "/",
            [Backtick] = "`", [LBracket] = "[", [Backslash] = "\\", [RBracket] = "]", [Quote] = "'"
        };
        for (var i = 0; i <= 9; i++) d[D0 + i] = i.ToString();
        for (var c = A; c <= Z; c++) d[c] = ((char)c).ToString();
        for (var i = 0; i <= 9; i++) d[NumPad0 + i] = $"Num {i}";
        for (var i = 1; i <= 24; i++) d[F(i)] = $"F{i}";
        return d;
    }

    public static string GetName(int keyCode) =>
        Names.TryGetValue(keyCode, out var name) ? name : $"Key 0x{keyCode:X2}";

    /// <summary>All keys that have a friendly name — used to populate the key picker.</summary>
    public static IEnumerable<KeyValuePair<int, string>> All => Names.OrderBy(k => k.Key);

    public static bool IsModifier(int keyCode) => ModifierOf(keyCode) != HotkeyModifiers.None;

    public static HotkeyModifiers ModifierOf(int keyCode) => keyCode switch
    {
        Shift or LShift or RShift => HotkeyModifiers.Shift,
        Control or LControl or RControl => HotkeyModifiers.Control,
        Alt or LAlt or RAlt => HotkeyModifiers.Alt,
        LWin or RWin => HotkeyModifiers.Meta,
        _ => HotkeyModifiers.None
    };
}
