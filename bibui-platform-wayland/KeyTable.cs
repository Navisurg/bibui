
using Bibui.Core;

namespace Bibui.Platform.Wayland;

/// <summary>
/// Linux evdev key codes (what wl_keyboard.key sends) to the
/// <see cref="Key"/> values WindowEvent carries (bibui's own, numbered
/// as GLFW numbers them). The table is GLFW's own for Wayland, so a key reports here what it reports
/// through the Silk host: by position on a US keyboard.
/// </summary>
internal static class KeyTable
{
    private static readonly Key[] Evdev = Build();

    /// <summary>The key at evdev code <paramref name="code"/>, or <see cref="Key.Unknown"/>.</summary>
    public static Key FromEvdev(uint code) => code < (uint)Evdev.Length ? Evdev[code] : Key.Unknown;

    /// <summary>A Latin letter keysym (a–z, A–Z) as its key, anything else <see cref="Key.Unknown"/>.</summary>
    public static Key LetterFromKeysym(uint keysym) => keysym switch
    {
        >= 'a' and <= 'z' => Key.A + (int)(keysym - 'a'),
        >= 'A' and <= 'Z' => Key.A + (int)(keysym - 'A'),
        _ => Key.Unknown,
    };

    /// <summary>A pointer button's evdev code (BTN_LEFT 0x110 onwards) as GLFW numbers it, or <see cref="MouseButton.Unknown"/>.</summary>
    public static MouseButton Button(uint code)
    {
        const uint BtnLeft = 0x110;
        uint i = code - BtnLeft;
        // GLFW: button - BTN_LEFT, up to GLFW_MOUSE_BUTTON_LAST (the eighth).
        return code >= BtnLeft && i <= 7 ? (MouseButton)(int)i : MouseButton.Unknown;
    }

    private static Key[] Build()
    {
        var t = new Key[256];
        Array.Fill(t, Key.Unknown);

        t[1] = Key.Escape;
        for (int i = 0; i < 9; i++) t[2 + i] = Key.Number1 + i; // KEY_1..KEY_9
        t[11] = Key.Number0;
        t[12] = Key.Minus;
        t[13] = Key.Equal;
        t[14] = Key.Backspace;
        t[15] = Key.Tab;
        Row(t, 16, "QWERTYUIOP");
        t[26] = Key.LeftBracket;
        t[27] = Key.RightBracket;
        t[28] = Key.Enter;
        t[29] = Key.ControlLeft;
        Row(t, 30, "ASDFGHJKL");
        t[39] = Key.Semicolon;
        t[40] = Key.Apostrophe;
        t[41] = Key.GraveAccent;
        t[42] = Key.ShiftLeft;
        t[43] = Key.BackSlash;
        Row(t, 44, "ZXCVBNM");
        t[51] = Key.Comma;
        t[52] = Key.Period;
        t[53] = Key.Slash;
        t[54] = Key.ShiftRight;
        t[55] = Key.KeypadMultiply;
        t[56] = Key.AltLeft;
        t[57] = Key.Space;
        t[58] = Key.CapsLock;
        for (int i = 0; i < 10; i++) t[59 + i] = Key.F1 + i;      // KEY_F1..KEY_F10
        t[69] = Key.NumLock;
        t[70] = Key.ScrollLock;
        t[71] = Key.Keypad7; t[72] = Key.Keypad8; t[73] = Key.Keypad9; t[74] = Key.KeypadSubtract;
        t[75] = Key.Keypad4; t[76] = Key.Keypad5; t[77] = Key.Keypad6; t[78] = Key.KeypadAdd;
        t[79] = Key.Keypad1; t[80] = Key.Keypad2; t[81] = Key.Keypad3;
        t[82] = Key.Keypad0; t[83] = Key.KeypadDecimal;
        t[86] = Key.World2;                                         // KEY_102ND: the key left of Z on ISO boards
        t[87] = Key.F11;
        t[88] = Key.F12;
        t[96] = Key.KeypadEnter;
        t[97] = Key.ControlRight;
        t[98] = Key.KeypadDivide;
        t[99] = Key.PrintScreen;                                    // KEY_SYSRQ
        t[100] = Key.AltRight;
        t[102] = Key.Home;
        t[103] = Key.Up;
        t[104] = Key.PageUp;
        t[105] = Key.Left;
        t[106] = Key.Right;
        t[107] = Key.End;
        t[108] = Key.Down;
        t[109] = Key.PageDown;
        t[110] = Key.Insert;
        t[111] = Key.Delete;
        t[117] = Key.KeypadEqual;
        t[119] = Key.Pause;
        t[125] = Key.SuperLeft;
        t[126] = Key.SuperRight;
        t[127] = Key.Menu;                                          // KEY_COMPOSE
        for (int i = 0; i < 12; i++) t[183 + i] = Key.F13 + i;     // KEY_F13..KEY_F24
        return t;

        static void Row(Key[] t, int start, string letters)
        {
            for (int i = 0; i < letters.Length; i++) t[start + i] = Key.A + (letters[i] - 'A');
        }
    }
}
