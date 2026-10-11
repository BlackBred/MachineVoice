using Avalonia.Input;

namespace MachineVoice.App;

/// <summary>Carbon virtual-key codes the recorder can assign, and how a chord is shown in the menu.</summary>
static class HotkeyText
{
    static readonly Dictionary<Key, int> Codes = new()
    {
        [Key.A] = 0x00, [Key.S] = 0x01, [Key.D] = 0x02, [Key.F] = 0x03, [Key.H] = 0x04, [Key.G] = 0x05,
        [Key.Z] = 0x06, [Key.X] = 0x07, [Key.C] = 0x08, [Key.V] = 0x09, [Key.B] = 0x0B, [Key.Q] = 0x0C,
        [Key.W] = 0x0D, [Key.E] = 0x0E, [Key.R] = 0x0F, [Key.Y] = 0x10, [Key.T] = 0x11,
        [Key.D1] = 0x12, [Key.D2] = 0x13, [Key.D3] = 0x14, [Key.D4] = 0x15, [Key.D6] = 0x16, [Key.D5] = 0x17,
        [Key.D9] = 0x19, [Key.D7] = 0x1A, [Key.D8] = 0x1C, [Key.D0] = 0x1D,
        [Key.O] = 0x1F, [Key.U] = 0x20, [Key.I] = 0x22, [Key.P] = 0x23,
        [Key.L] = 0x25, [Key.J] = 0x26, [Key.K] = 0x28, [Key.N] = 0x2D, [Key.M] = 0x2E,
        [Key.Space] = 0x31,
    };

    static readonly Dictionary<int, string> Names = new()
    {
        [0x00] = "A", [0x01] = "S", [0x02] = "D", [0x03] = "F", [0x04] = "H", [0x05] = "G",
        [0x06] = "Z", [0x07] = "X", [0x08] = "C", [0x09] = "V", [0x0B] = "B", [0x0C] = "Q",
        [0x0D] = "W", [0x0E] = "E", [0x0F] = "R", [0x10] = "Y", [0x11] = "T",
        [0x12] = "1", [0x13] = "2", [0x14] = "3", [0x15] = "4", [0x16] = "6", [0x17] = "5",
        [0x19] = "9", [0x1A] = "7", [0x1C] = "8", [0x1D] = "0",
        [0x1F] = "O", [0x20] = "U", [0x22] = "I", [0x23] = "P",
        [0x25] = "L", [0x26] = "J", [0x28] = "K", [0x2D] = "N", [0x2E] = "M",
        [0x31] = "␣",
    };

    public static bool TryCode(Key key, out int code) => Codes.TryGetValue(key, out code);

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public static int Modifiers(KeyModifiers modifiers)
    {
        var bits = 0;
        if (modifiers.HasFlag(KeyModifiers.Control))
            bits |= 0x1000;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            bits |= 0x0800;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            bits |= 0x0200;
        if (modifiers.HasFlag(KeyModifiers.Meta))
            bits |= 0x0100;
        return bits;
    }

    public static string Format(int keyCode, int modifiers)
    {
        var text = "";
        if ((modifiers & 0x1000) != 0)
            text += "⌃";
        if ((modifiers & 0x0800) != 0)
            text += "⌥";
        if ((modifiers & 0x0200) != 0)
            text += "⇧";
        if ((modifiers & 0x0100) != 0)
            text += "⌘";
        return text + (Names.TryGetValue(keyCode, out var name) ? name : "?");
    }
}
