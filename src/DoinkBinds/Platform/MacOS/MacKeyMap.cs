using DoinkBinds.Models;

namespace DoinkBinds.Platform.MacOS;

/// <summary>Translates macOS Carbon virtual key codes (kVK_*) to/from our canonical Windows-style key codes.</summary>
internal static class MacKeyMap
{
    private static readonly Dictionary<int, int> MacToVk = new()
    {
        [0x00] = 'A', [0x01] = 'S', [0x02] = 'D', [0x03] = 'F', [0x04] = 'H', [0x05] = 'G', [0x06] = 'Z',
        [0x07] = 'X', [0x08] = 'C', [0x09] = 'V', [0x0B] = 'B', [0x0C] = 'Q', [0x0D] = 'W', [0x0E] = 'E',
        [0x0F] = 'R', [0x10] = 'Y', [0x11] = 'T', [0x12] = '1', [0x13] = '2', [0x14] = '3', [0x15] = '4',
        [0x16] = '6', [0x17] = '5', [0x18] = KeyCodes.EqualSign, [0x19] = '9', [0x1A] = '7', [0x1B] = KeyCodes.Minus,
        [0x1C] = '8', [0x1D] = '0', [0x1E] = KeyCodes.RBracket, [0x1F] = 'O', [0x20] = 'U', [0x21] = KeyCodes.LBracket,
        [0x22] = 'I', [0x23] = 'P', [0x24] = KeyCodes.Enter, [0x25] = 'L', [0x26] = 'J', [0x27] = KeyCodes.Quote,
        [0x28] = 'K', [0x29] = KeyCodes.Semicolon, [0x2A] = KeyCodes.Backslash, [0x2B] = KeyCodes.Comma,
        [0x2C] = KeyCodes.Slash, [0x2D] = 'N', [0x2E] = 'M', [0x2F] = KeyCodes.Period, [0x30] = KeyCodes.Tab,
        [0x31] = KeyCodes.Space, [0x32] = KeyCodes.Backtick, [0x33] = KeyCodes.Backspace, [0x35] = KeyCodes.Escape,
        [0x36] = KeyCodes.RWin, [0x37] = KeyCodes.LWin, [0x38] = KeyCodes.LShift, [0x39] = KeyCodes.CapsLock,
        [0x3A] = KeyCodes.LAlt, [0x3B] = KeyCodes.LControl, [0x3C] = KeyCodes.RShift, [0x3D] = KeyCodes.RAlt,
        [0x3E] = KeyCodes.RControl,
        [0x41] = KeyCodes.Decimal, [0x43] = KeyCodes.Multiply, [0x45] = KeyCodes.Add, [0x47] = KeyCodes.NumLock,
        [0x48] = KeyCodes.VolumeUp, [0x49] = KeyCodes.VolumeDown, [0x4A] = KeyCodes.VolumeMute,
        [0x4B] = KeyCodes.Divide, [0x4C] = KeyCodes.Separator, [0x4E] = KeyCodes.Subtract,
        [0x52] = KeyCodes.NumPad0, [0x53] = KeyCodes.NumPad0 + 1, [0x54] = KeyCodes.NumPad0 + 2,
        [0x55] = KeyCodes.NumPad0 + 3, [0x56] = KeyCodes.NumPad0 + 4, [0x57] = KeyCodes.NumPad0 + 5,
        [0x58] = KeyCodes.NumPad0 + 6, [0x59] = KeyCodes.NumPad0 + 7, [0x5B] = KeyCodes.NumPad0 + 8,
        [0x5C] = KeyCodes.NumPad0 + 9,
        [0x7A] = KeyCodes.F(1), [0x78] = KeyCodes.F(2), [0x63] = KeyCodes.F(3), [0x76] = KeyCodes.F(4),
        [0x60] = KeyCodes.F(5), [0x61] = KeyCodes.F(6), [0x62] = KeyCodes.F(7), [0x64] = KeyCodes.F(8),
        [0x65] = KeyCodes.F(9), [0x6D] = KeyCodes.F(10), [0x67] = KeyCodes.F(11), [0x6F] = KeyCodes.F(12),
        [0x69] = KeyCodes.F(13), [0x6B] = KeyCodes.F(14), [0x71] = KeyCodes.F(15), [0x6A] = KeyCodes.F(16),
        [0x40] = KeyCodes.F(17), [0x4F] = KeyCodes.F(18), [0x50] = KeyCodes.F(19), [0x5A] = KeyCodes.F(20),
        [0x72] = KeyCodes.Insert, [0x73] = KeyCodes.Home, [0x74] = KeyCodes.PageUp, [0x75] = KeyCodes.Delete,
        [0x77] = KeyCodes.End, [0x79] = KeyCodes.PageDown,
        [0x7B] = KeyCodes.Left, [0x7C] = KeyCodes.Right, [0x7D] = KeyCodes.Down, [0x7E] = KeyCodes.Up,
    };

    private static readonly Dictionary<int, int> VkToMac = BuildReverse();

    private static Dictionary<int, int> BuildReverse()
    {
        var d = MacToVk.ToDictionary(kv => kv.Value, kv => kv.Key);
        // Generic (side-less) modifiers map to the left key.
        d[KeyCodes.Shift] = 0x38;
        d[KeyCodes.Control] = 0x3B;
        d[KeyCodes.Alt] = 0x3A;
        return d;
    }

    public static int ToVirtualKey(int macKeyCode) =>
        MacToVk.TryGetValue(macKeyCode, out var vk) ? vk : 0x1000 + macKeyCode; // unknown keys round-trip

    public static int? ToMacKeyCode(int vk)
    {
        if (VkToMac.TryGetValue(vk, out var mac)) return mac;
        if (vk >= 0x1000) return vk - 0x1000;
        return null;
    }
}
