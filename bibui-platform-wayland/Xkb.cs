using System.Runtime.InteropServices;
using System.Text;
using Bibui.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>
/// libxkbcommon's entry points: the keymap the compositor sends, the
/// state its modifiers event updates, keysyms, UTF-8 text, and dead keys and
/// Compose sequences through the compose table. Blittable signatures only —
/// the assembly disables runtime marshalling, so this compiles the same under
/// Native AOT.
/// </summary>
internal static unsafe class XkbNative
{
    public const string Library = "libxkbcommon.so.0";

    /// <summary>True when libxkbcommon can be loaded on this machine.</summary>
    public static bool IsAvailable => NativeLibrary.TryLoad(Library, out _);

    public const int XKB_KEYMAP_FORMAT_TEXT_V1 = 1;
    public const int XKB_KEY_UP = 0, XKB_KEY_DOWN = 1;
    public const int XKB_STATE_MODS_DEPRESSED = 1, XKB_STATE_MODS_LATCHED = 2, XKB_STATE_MODS_LOCKED = 4, XKB_STATE_MODS_EFFECTIVE = 8;
    public const uint XKB_MOD_INVALID = 0xffffffff;
    public const int XKB_COMPOSE_FEED_ACCEPTED = 1;
    public const int XKB_COMPOSE_NOTHING = 0, XKB_COMPOSE_COMPOSING = 1, XKB_COMPOSE_COMPOSED = 2, XKB_COMPOSE_CANCELLED = 3;

    /// <summary>struct xkb_rule_names.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RuleNames
    {
        public byte* Rules, Model, Layout, Variant, Options;
    }

    [DllImport(Library, ExactSpelling = true)] public static extern nint xkb_context_new(int flags);
    [DllImport(Library, ExactSpelling = true)] public static extern void xkb_context_unref(nint context);

    [DllImport(Library, ExactSpelling = true)] public static extern nint xkb_keymap_new_from_buffer(nint context, byte* buffer, nuint length, int format, int flags);
    [DllImport(Library, ExactSpelling = true)] public static extern nint xkb_keymap_new_from_names(nint context, RuleNames* names, int flags);
    [DllImport(Library, ExactSpelling = true)] public static extern void xkb_keymap_unref(nint keymap);
    [DllImport(Library, ExactSpelling = true)] public static extern uint xkb_keymap_mod_get_index(nint keymap, byte* name);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_keymap_key_repeats(nint keymap, uint key);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_keymap_key_get_syms_by_level(nint keymap, uint key, uint layout, uint level, uint** symsOut);

    [DllImport(Library, ExactSpelling = true)] public static extern nint xkb_state_new(nint keymap);
    [DllImport(Library, ExactSpelling = true)] public static extern void xkb_state_unref(nint state);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_state_update_mask(nint state, uint depressedMods, uint latchedMods, uint lockedMods, uint depressedLayout, uint latchedLayout, uint lockedLayout);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_state_update_key(nint state, uint key, int direction);
    [DllImport(Library, ExactSpelling = true)] public static extern uint xkb_state_key_get_one_sym(nint state, uint key);
    [DllImport(Library, ExactSpelling = true)] public static extern uint xkb_state_key_get_layout(nint state, uint key);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_state_key_get_utf8(nint state, uint key, byte* buffer, nuint size);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_state_mod_index_is_active(nint state, uint index, int type);

    [DllImport(Library, ExactSpelling = true)] public static extern nint xkb_compose_table_new_from_locale(nint context, byte* locale, int flags);
    [DllImport(Library, ExactSpelling = true)] public static extern void xkb_compose_table_unref(nint table);
    [DllImport(Library, ExactSpelling = true)] public static extern nint xkb_compose_state_new(nint table, int flags);
    [DllImport(Library, ExactSpelling = true)] public static extern void xkb_compose_state_unref(nint state);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_compose_state_feed(nint state, uint keysym);
    [DllImport(Library, ExactSpelling = true)] public static extern void xkb_compose_state_reset(nint state);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_compose_state_get_status(nint state);
    [DllImport(Library, ExactSpelling = true)] public static extern int xkb_compose_state_get_utf8(nint state, byte* buffer, nuint size);
}

/// <summary>
/// One keyboard's xkb state: a keymap, the state the compositor's modifiers
/// event keeps, and the Compose state that turns dead keys and sequences into
/// text. Answers the three questions the host asks of a key press — which
/// <see cref="Key"/>, what text, does it repeat — and which modifiers are held.
///
/// <para>Keycodes in and out are evdev codes, as wl_keyboard.key sends them;
/// xkb's are those plus 8.</para>
/// </summary>
internal sealed unsafe class XkbKeyboard : IDisposable
{
    private nint _context, _keymap, _state, _composeTable, _compose;
    private uint _shift, _ctrl, _alt, _super;

    private XkbKeyboard(nint context) => _context = context;

    /// <summary>A keyboard with no keymap yet: the compositor sends one with wl_keyboard.keymap.</summary>
    public static XkbKeyboard Create()
    {
        nint ctx = XkbNative.xkb_context_new(0);
        if (ctx == 0) throw new InvalidOperationException("xkb_context_new failed");
        return new XkbKeyboard(ctx);
    }

    /// <summary>
    /// A keyboard whose keymap is compiled from RMLVO names — what tests use,
    /// with no compositor. Null names take xkbcommon's defaults.
    /// </summary>
    public static XkbKeyboard FromNames(string? layout, string? variant = null, string? options = null, string? composeLocale = null)
    {
        var kb = Create();
        byte* l = WlUtf8Of(layout), v = WlUtf8Of(variant), o = WlUtf8Of(options);
        try
        {
            var names = new XkbNative.RuleNames { Layout = l, Variant = v, Options = o };
            nint keymap = XkbNative.xkb_keymap_new_from_names(kb._context, &names, 0);
            if (keymap == 0) { kb.Dispose(); throw new InvalidOperationException($"xkb could not compile layout '{layout}'"); }
            kb.Adopt(keymap, composeLocale);
            return kb;
        }
        finally { Free(l); Free(v); Free(o); }
    }

    /// <summary>True once a keymap has been loaded.</summary>
    public bool HasKeymap => _state != 0;

    /// <summary>
    /// Replaces the keymap with the one in <paramref name="text"/> (the mapped
    /// keymap fd, NUL-terminated within <paramref name="size"/>). False when xkb
    /// cannot compile it; the previous keymap is then kept.
    /// </summary>
    public bool LoadKeymap(byte* text, nuint size, string? composeLocale)
    {
        nuint len = 0;
        while (len < size && text[len] != 0) len++;
        nint keymap = XkbNative.xkb_keymap_new_from_buffer(_context, text, len, XkbNative.XKB_KEYMAP_FORMAT_TEXT_V1, 0);
        if (keymap == 0) return false;
        Adopt(keymap, composeLocale);
        return true;
    }

    private void Adopt(nint keymap, string? composeLocale)
    {
        nint state = XkbNative.xkb_state_new(keymap);
        if (state == 0) { XkbNative.xkb_keymap_unref(keymap); throw new InvalidOperationException("xkb_state_new failed"); }
        ReleaseKeymap();
        _keymap = keymap;
        _state = state;
        _shift = ModIndex("Shift");
        _ctrl = ModIndex("Control");
        _alt = ModIndex("Mod1");
        _super = ModIndex("Mod4");

        // The compose table is the locale's, not the keymap's, but it is
        // loaded with the first keymap so a keyboard that never gets one pays
        // nothing for it.
        if (_composeTable == 0)
        {
            byte* loc = WlUtf8Of(composeLocale ?? Locale());
            try { _composeTable = XkbNative.xkb_compose_table_new_from_locale(_context, loc, 0); }
            finally { Free(loc); }
        }
        if (_composeTable != 0 && _compose == 0) _compose = XkbNative.xkb_compose_state_new(_composeTable, 0);
        else if (_compose != 0) XkbNative.xkb_compose_state_reset(_compose);
    }

    /// <summary>The locale whose Compose table applies: LC_ALL, LC_CTYPE, LANG, else C — as xkbcommon's own clients choose it.</summary>
    private static string Locale()
    {
        foreach (var name in new[] { "LC_ALL", "LC_CTYPE", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) return v;
        return "C";
    }

    private uint ModIndex(string name)
    {
        byte* p = WlUtf8Of(name);
        try { return XkbNative.xkb_keymap_mod_get_index(_keymap, p); }
        finally { Free(p); }
    }

    /// <summary>wl_keyboard.modifiers: the compositor's word on what is depressed, latched, locked, and which layout.</summary>
    public void UpdateMask(uint depressed, uint latched, uint locked, uint group)
    {
        if (_state != 0) XkbNative.xkb_state_update_mask(_state, depressed, latched, locked, 0, 0, group);
    }

    /// <summary>
    /// Feeds a key straight into the state, as if the compositor had. Only a
    /// test does this — a Wayland client is told the modifiers rather than
    /// working them out — so a modifier can be pressed with no compositor.
    /// </summary>
    public void UpdateKey(uint evdev, bool down)
    {
        if (_state != 0) XkbNative.xkb_state_update_key(_state, evdev + 8, down ? XkbNative.XKB_KEY_DOWN : XkbNative.XKB_KEY_UP);
    }

    /// <summary>
    /// The modifiers held: depressed or latched, not locked — Caps Lock is not
    /// Shift — the same as the Silk host, which reads which modifier keys are
    /// down. Super is reported as Super; BibuiWindow folds it into Ctrl.
    /// </summary>
    public KeyModifiers HeldModifiers
    {
        get
        {
            if (_state == 0) return KeyModifiers.None;
            const int held = XkbNative.XKB_STATE_MODS_DEPRESSED | XkbNative.XKB_STATE_MODS_LATCHED;
            var m = KeyModifiers.None;
            if (Active(_shift, held)) m |= KeyModifiers.Shift;
            if (Active(_ctrl, held)) m |= KeyModifiers.Ctrl;
            if (Active(_alt, held)) m |= KeyModifiers.Alt;
            if (Active(_super, held)) m |= KeyModifiers.Super;
            return m;
        }
    }

    private bool Active(uint index, int type) =>
        index != XkbNative.XKB_MOD_INVALID && XkbNative.xkb_state_mod_index_is_active(_state, index, type) > 0;

    /// <summary>
    /// Ctrl or Alt in effect, which makes a key a shortcut rather than typing:
    /// GLFW sends no character for it, and neither does this host.
    /// </summary>
    public bool SuppressesText =>
        _state != 0 && (Active(_ctrl, XkbNative.XKB_STATE_MODS_EFFECTIVE) || Active(_alt, XkbNative.XKB_STATE_MODS_EFFECTIVE));

    /// <summary>The keysym the key produces now, with the modifiers in effect.</summary>
    public uint Keysym(uint evdev) => _state == 0 ? 0 : XkbNative.xkb_state_key_get_one_sym(_state, evdev + 8);

    /// <summary>
    /// The keysym on the key's first level in the active layout — what is
    /// printed on it, unshifted. 0 when it has none.
    /// </summary>
    public uint BaseKeysym(uint evdev)
    {
        if (_state == 0) return 0;
        uint key = evdev + 8;
        uint layout = XkbNative.xkb_state_key_get_layout(_state, key);
        uint* syms;
        int n = XkbNative.xkb_keymap_key_get_syms_by_level(_keymap, key, layout == uint.MaxValue ? 0 : layout, 0, &syms);
        return n == 1 ? syms[0] : 0;
    }

    /// <summary>
    /// The key a press reports: the letter printed on it when it is a Latin
    /// letter in the active layout (Ctrl+Z is Ctrl+Z on AZERTY and Dvorak too),
    /// otherwise the key at that position on a US keyboard, as GLFW reports
    /// every key — so digits, punctuation and every key of a non-Latin layout
    /// keep working as shortcuts.
    /// </summary>
    public Key KeyFor(uint evdev)
    {
        var letter = KeyTable.LetterFromKeysym(BaseKeysym(evdev));
        return letter != Key.Unknown ? letter : KeyTable.FromEvdev(evdev);
    }

    /// <summary>Whether the keymap says this key repeats when held (letters do, modifiers do not).</summary>
    public bool Repeats(uint evdev) => _keymap != 0 && XkbNative.xkb_keymap_key_repeats(_keymap, evdev + 8) != 0;

    /// <summary>
    /// The text a press of <paramref name="evdev"/> types, fed through Compose:
    /// null while a dead key or sequence is pending, for a cancelled sequence,
    /// for control characters (Enter, Tab, Backspace: those are keys, not
    /// text) and while Ctrl or Alt is held.
    /// </summary>
    public string? Press(uint evdev)
    {
        if (_state == 0) return null;
        if (_compose != 0)
        {
            uint sym = Keysym(evdev);
            if (XkbNative.xkb_compose_state_feed(_compose, sym) == XkbNative.XKB_COMPOSE_FEED_ACCEPTED)
            {
                switch (XkbNative.xkb_compose_state_get_status(_compose))
                {
                    case XkbNative.XKB_COMPOSE_COMPOSING:
                        return null;
                    case XkbNative.XKB_COMPOSE_CANCELLED:
                        XkbNative.xkb_compose_state_reset(_compose);
                        return null;
                    case XkbNative.XKB_COMPOSE_COMPOSED:
                    {
                        byte* buf = stackalloc byte[64];
                        int n = XkbNative.xkb_compose_state_get_utf8(_compose, buf, 64);
                        XkbNative.xkb_compose_state_reset(_compose);
                        return SuppressesText ? null : Printable(buf, n);
                    }
                }
            }
        }
        return Text(evdev);
    }

    /// <summary>The text the key types with the modifiers in effect, without Compose: what a repeat of it types.</summary>
    public string? Text(uint evdev)
    {
        if (_state == 0 || SuppressesText) return null;
        byte* buf = stackalloc byte[64];
        int n = XkbNative.xkb_state_key_get_utf8(_state, evdev + 8, buf, 64);
        return Printable(buf, n);
    }

    /// <summary>Drops a half-typed Compose sequence (focus left the window).</summary>
    public void ResetCompose()
    {
        if (_compose != 0) XkbNative.xkb_compose_state_reset(_compose);
    }

    private static string? Printable(byte* buf, int n)
    {
        if (n <= 0 || n >= 64) return null;
        var s = Encoding.UTF8.GetString(buf, n);
        foreach (var c in s)
            if (char.IsControl(c)) return null;
        return s;
    }

    private void ReleaseKeymap()
    {
        if (_state != 0) XkbNative.xkb_state_unref(_state);
        if (_keymap != 0) XkbNative.xkb_keymap_unref(_keymap);
        _state = _keymap = 0;
    }

    public void Dispose()
    {
        ReleaseKeymap();
        if (_compose != 0) XkbNative.xkb_compose_state_unref(_compose);
        if (_composeTable != 0) XkbNative.xkb_compose_table_unref(_composeTable);
        if (_context != 0) XkbNative.xkb_context_unref(_context);
        _compose = _composeTable = _context = 0;
    }

    private static byte* WlUtf8Of(string? s) => Protocol.WlUtf8.Alloc(s);
    private static void Free(byte* p) => Protocol.WlUtf8.Free(p);
}
