using System.Buffers;
using System.Text;
using Bibui.Core;

namespace Bibui.Platform.Browser;

/// <summary>
/// What the browser's input events mean in bibui's terms: a
/// <c>KeyboardEvent.code</c> as a <see cref="Key"/>, a DOM button as a
/// <see cref="MouseButton"/>, the modifier bits bibui-browser.js packs as
/// <see cref="KeyModifiers"/>, and whether a key-down typed a character.
/// </summary>
internal static class BrowserKeys
{
    /// <summary>
    /// The key a <c>KeyboardEvent.code</c> names. Codes name a physical key by its
    /// place on a US layout, as bibui's <see cref="Key"/> does (it is GLFW's), so a
    /// shortcut on Ctrl+Z is the same key on every layout, as on the desktop hosts.
    /// </summary>
    public static Key FromCode(string code) => Codes.GetValueOrDefault(code, Key.Unknown);

    /// <summary>A DOM <c>MouseEvent.button</c>: 0 is the left button, 1 the middle, 2 the right.</summary>
    public static MouseButton FromDomButton(int button) => button switch
    {
        0 => MouseButton.Left,
        1 => MouseButton.Middle,
        2 => MouseButton.Right,
        3 => MouseButton.Button4,
        4 => MouseButton.Button5,
        _ => MouseButton.Unknown,
    };

    // The bits bibui-browser.js packs: shiftKey, ctrlKey, altKey, metaKey.
    public const int DomShift = 1, DomCtrl = 2, DomAlt = 4, DomMeta = 8;

    /// <summary>
    /// The modifiers held. Meta (Cmd on macOS, the Windows key elsewhere) is
    /// <see cref="KeyModifiers.Super"/>, which the runtime folds into Ctrl for
    /// shortcuts, as it does on the desktop hosts.
    /// </summary>
    public static KeyModifiers FromDomModifiers(int bits)
    {
        var m = KeyModifiers.None;
        if ((bits & DomShift) != 0) m |= KeyModifiers.Shift;
        if ((bits & DomCtrl) != 0) m |= KeyModifiers.Ctrl;
        if ((bits & DomAlt) != 0) m |= KeyModifiers.Alt;
        if ((bits & DomMeta) != 0) m |= KeyModifiers.Super;
        return m;
    }

    /// <summary>
    /// The character a key-down typed, from its <c>KeyboardEvent.key</c>: one
    /// character that is not a control character, with no Ctrl or Meta held,
    /// which make it a shortcut. AltGr, which some layouts need to type
    /// characters, is reported by browsers as Ctrl+Alt; with
    /// <paramref name="altGraph"/> it types. A named key (<c>"Enter"</c>,
    /// <c>"ArrowLeft"</c>) and a dead key (<c>"Dead"</c>) type nothing.
    /// </summary>
    public static Rune? TextOf(string key, KeyModifiers held, bool altGraph)
    {
        if (!altGraph && (held & (KeyModifiers.Ctrl | KeyModifiers.Super)) != 0) return null;
        if (Rune.DecodeFromUtf16(key, out var rune, out int used) != OperationStatus.Done || used != key.Length) return null;
        return Rune.IsControl(rune) ? null : rune;
    }

    /// <summary>
    /// The modifiers a key-down reports. With AltGr the browser's Ctrl and Alt
    /// are the AltGr key itself, not a shortcut, so they are dropped.
    /// </summary>
    public static KeyModifiers HeldForKey(int bits, bool altGraph)
    {
        var m = FromDomModifiers(bits);
        return altGraph ? m & ~(KeyModifiers.Ctrl | KeyModifiers.Alt) : m;
    }

    private static readonly Dictionary<string, Key> Codes = BuildCodes();

    private static Dictionary<string, Key> BuildCodes()
    {
        var k = new Dictionary<string, Key>(StringComparer.Ordinal)
        {
            ["Space"] = Key.Space, ["Quote"] = Key.Apostrophe, ["Comma"] = Key.Comma, ["Minus"] = Key.Minus,
            ["Period"] = Key.Period, ["Slash"] = Key.Slash, ["Semicolon"] = Key.Semicolon, ["Equal"] = Key.Equal,
            ["BracketLeft"] = Key.LeftBracket, ["Backslash"] = Key.BackSlash, ["BracketRight"] = Key.RightBracket,
            ["Backquote"] = Key.GraveAccent, ["IntlBackslash"] = Key.World1,
            ["Escape"] = Key.Escape, ["Enter"] = Key.Enter, ["Tab"] = Key.Tab, ["Backspace"] = Key.Backspace,
            ["Insert"] = Key.Insert, ["Delete"] = Key.Delete,
            ["ArrowRight"] = Key.Right, ["ArrowLeft"] = Key.Left, ["ArrowDown"] = Key.Down, ["ArrowUp"] = Key.Up,
            ["PageUp"] = Key.PageUp, ["PageDown"] = Key.PageDown, ["Home"] = Key.Home, ["End"] = Key.End,
            ["CapsLock"] = Key.CapsLock, ["ScrollLock"] = Key.ScrollLock, ["NumLock"] = Key.NumLock,
            ["PrintScreen"] = Key.PrintScreen, ["Pause"] = Key.Pause,
            ["NumpadDecimal"] = Key.KeypadDecimal, ["NumpadDivide"] = Key.KeypadDivide,
            ["NumpadMultiply"] = Key.KeypadMultiply, ["NumpadSubtract"] = Key.KeypadSubtract,
            ["NumpadAdd"] = Key.KeypadAdd, ["NumpadEnter"] = Key.KeypadEnter, ["NumpadEqual"] = Key.KeypadEqual,
            ["ShiftLeft"] = Key.ShiftLeft, ["ControlLeft"] = Key.ControlLeft, ["AltLeft"] = Key.AltLeft,
            ["MetaLeft"] = Key.SuperLeft, ["ShiftRight"] = Key.ShiftRight, ["ControlRight"] = Key.ControlRight,
            ["AltRight"] = Key.AltRight, ["MetaRight"] = Key.SuperRight, ["ContextMenu"] = Key.Menu,
        };
        for (int i = 0; i < 26; i++) k["Key" + (char)('A' + i)] = Key.A + i;
        for (int i = 0; i < 10; i++)
        {
            k["Digit" + i] = Key.Number0 + i;
            k["Numpad" + i] = Key.Keypad0 + i;
        }
        for (int i = 1; i <= 24; i++) k["F" + i] = Key.F1 + (i - 1);
        return k;
    }
}
