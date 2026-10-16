using Bibui.Platform.Wayland;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Runtime.Platform;

namespace Bibui.WaylandTests;

/// <summary>
/// Seat input without a compositor: keymaps compiled by libxkbcommon from names,
/// the repeat schedule against a fake clock, and wl_pointer events folded into
/// WindowEvents. The repeat timerfd test uses the real clock, for tens of ms.
/// </summary>
internal static unsafe class InputTests
{
    // evdev codes
    private const uint KEY_ESC = 1, KEY_1 = 2, KEY_EQUAL = 13, KEY_BACKSPACE = 14, KEY_Q = 16, KEY_W = 17, KEY_E = 18,
        KEY_ENTER = 28, KEY_LEFTCTRL = 29, KEY_A = 30, KEY_SEMICOLON = 39, KEY_APOSTROPHE = 40, KEY_LEFTSHIFT = 42,
        KEY_C = 46, KEY_LEFTALT = 56, KEY_CAPSLOCK = 58, KEY_KP1 = 79, KEY_RIGHTALT = 100, KEY_LEFT = 105, KEY_LEFTMETA = 125;

    private const uint BTN_LEFT = 0x110, BTN_RIGHT = 0x111, BTN_MIDDLE = 0x112, BTN_SIDE = 0x113;

    private static XkbKeyboard Layout(string layout, string? variant = null)
    {
        if (!XkbNative.IsAvailable) throw new SkipException("no libxkbcommon.so.0");
        try { return XkbKeyboard.FromNames(layout, variant, composeLocale: "en_US.UTF-8"); }
        catch (InvalidOperationException e) { throw new SkipException(e.Message); }
    }

    public static void Register(Harness t)
    {
        // ── Key table ────────────────────────────────────────────────────────

        t.Run("input: evdev codes map to GLFW's keys", () =>
        {
            Assert.Eq(Key.Escape, KeyTable.FromEvdev(KEY_ESC));
            Assert.Eq(Key.Number1, KeyTable.FromEvdev(KEY_1));
            Assert.Eq(Key.Number0, KeyTable.FromEvdev(11));
            Assert.Eq(Key.Q, KeyTable.FromEvdev(KEY_Q));
            Assert.Eq(Key.A, KeyTable.FromEvdev(KEY_A));
            Assert.Eq(Key.M, KeyTable.FromEvdev(50));
            Assert.Eq(Key.Enter, KeyTable.FromEvdev(KEY_ENTER));
            Assert.Eq(Key.Backspace, KeyTable.FromEvdev(KEY_BACKSPACE));
            Assert.Eq(Key.Left, KeyTable.FromEvdev(KEY_LEFT));
            Assert.Eq(Key.Delete, KeyTable.FromEvdev(111));
            Assert.Eq(Key.SuperLeft, KeyTable.FromEvdev(KEY_LEFTMETA));
            Assert.Eq(Key.SuperRight, KeyTable.FromEvdev(126));
            Assert.Eq(Key.AltRight, KeyTable.FromEvdev(KEY_RIGHTALT));
            Assert.Eq(Key.Keypad1, KeyTable.FromEvdev(KEY_KP1));
            Assert.Eq(Key.F12, KeyTable.FromEvdev(88));
            Assert.Eq(Key.F24, KeyTable.FromEvdev(194));
            Assert.Eq(Key.World2, KeyTable.FromEvdev(86));
            Assert.Eq(Key.Unknown, KeyTable.FromEvdev(84));
            Assert.Eq(Key.Unknown, KeyTable.FromEvdev(100_000));
        });

        t.Run("input: pointer buttons are numbered as GLFW numbers them", () =>
        {
            Assert.Eq(MouseButton.Left, KeyTable.Button(BTN_LEFT));
            Assert.Eq(MouseButton.Right, KeyTable.Button(BTN_RIGHT));
            Assert.Eq(MouseButton.Middle, KeyTable.Button(BTN_MIDDLE));
            Assert.Eq(MouseButton.Button4, KeyTable.Button(BTN_SIDE));
            Assert.Eq(MouseButton.Button8, KeyTable.Button(BTN_LEFT + 7));
            Assert.Eq(MouseButton.Unknown, KeyTable.Button(BTN_LEFT + 8));
            Assert.Eq(MouseButton.Unknown, KeyTable.Button(0x10f));
        });

        // ── xkbcommon ────────────────────────────────────────────────────────

        t.Run("input: xkb us — letters, Shift, Caps Lock and what is held", () =>
        {
            using var kb = Layout("us");
            Assert.Eq(Key.A, kb.KeyFor(KEY_A));
            Assert.Eq("a", kb.Press(KEY_A));
            Assert.Eq(KeyModifiers.None, kb.HeldModifiers);

            kb.UpdateKey(KEY_LEFTSHIFT, down: true);
            Assert.Eq(KeyModifiers.Shift, kb.HeldModifiers, "shift held");
            Assert.Eq("A", kb.Press(KEY_A));
            Assert.Eq("!", kb.Press(KEY_1));
            Assert.Eq(Key.Number1, kb.KeyFor(KEY_1), "Shift+1 is still Number1");
            Assert.Eq(Key.A, kb.KeyFor(KEY_A), "Shift+a is still A");
            kb.UpdateKey(KEY_LEFTSHIFT, down: false);

            // Locked, not held: Caps Lock types capitals but is not Shift.
            kb.UpdateKey(KEY_CAPSLOCK, true); kb.UpdateKey(KEY_CAPSLOCK, false);
            Assert.Eq(KeyModifiers.None, kb.HeldModifiers, "caps lock is not a held modifier");
            Assert.Eq("A", kb.Press(KEY_A));
        });

        t.Run("input: xkb — Ctrl, Alt and Super are held, Ctrl and Alt type nothing", () =>
        {
            using var kb = Layout("us");
            kb.UpdateKey(KEY_LEFTCTRL, true);
            Assert.Eq(KeyModifiers.Ctrl, kb.HeldModifiers);
            Assert.Eq(null, kb.Press(KEY_C), "Ctrl+C types nothing");
            Assert.Eq(Key.C, kb.KeyFor(KEY_C));
            kb.UpdateKey(KEY_LEFTCTRL, false);

            kb.UpdateKey(KEY_LEFTALT, true);
            Assert.Eq(KeyModifiers.Alt, kb.HeldModifiers);
            Assert.Eq(null, kb.Press(KEY_A), "Alt+a types nothing");
            kb.UpdateKey(KEY_LEFTALT, false);

            // Super is reported as Super; WindowInput folds it into Ctrl, as for the Silk host.
            kb.UpdateKey(KEY_LEFTMETA, true);
            Assert.Eq(KeyModifiers.Super, kb.HeldModifiers);
            kb.UpdateKey(KEY_LEFTSHIFT, true);
            Assert.Eq(KeyModifiers.Super | KeyModifiers.Shift, kb.HeldModifiers);
        });

        t.Run("input: xkb — control characters are keys, not text", () =>
        {
            using var kb = Layout("us");
            Assert.Eq(null, kb.Press(KEY_ENTER), "Enter");
            Assert.Eq(null, kb.Press(KEY_BACKSPACE), "Backspace");
            Assert.Eq(null, kb.Press(KEY_ESC), "Escape");
            Assert.Eq(null, kb.Press(KEY_LEFT), "Left");
            Assert.Eq(" ", kb.Press(57), "Space is text");
        });

        t.Run("input: xkb — which keys repeat", () =>
        {
            using var kb = Layout("us");
            Assert.True(kb.Repeats(KEY_A), "a repeats");
            Assert.True(kb.Repeats(KEY_BACKSPACE), "backspace repeats");
            Assert.True(!kb.Repeats(KEY_LEFTSHIFT), "shift does not");
            Assert.True(!kb.Repeats(KEY_LEFTCTRL), "ctrl does not");
        });

        t.Run("input: xkb fr — a letter key reports the letter printed on it", () =>
        {
            using var kb = Layout("fr");
            // AZERTY: the key where US has Q types a, and is A for shortcuts (Ctrl+A selects all).
            Assert.Eq("a", kb.Press(KEY_Q));
            Assert.Eq(Key.A, kb.KeyFor(KEY_Q));
            Assert.Eq(Key.Z, kb.KeyFor(KEY_W));
            // The digit row is not letters: those keep their position, as GLFW reports them.
            Assert.Eq(Key.Number1, kb.KeyFor(KEY_1));
            Assert.Eq("&", kb.Press(KEY_1));
        });

        t.Run("input: xkb ru — a non-Latin layout keeps shortcuts on their US keys", () =>
        {
            using var kb = Layout("ru");
            Assert.Eq("с", kb.Press(KEY_C), "Cyrillic es");
            Assert.Eq(Key.C, kb.KeyFor(KEY_C), "still Ctrl+C");
        });

        t.Run("input: xkb dk — æ, and AltGr is not Alt", () =>
        {
            using var kb = Layout("dk");
            Assert.Eq("æ", kb.Press(KEY_SEMICOLON));
            Assert.Eq("ø", kb.Press(KEY_APOSTROPHE));
            kb.UpdateKey(KEY_RIGHTALT, true); // AltGr: ISO_Level3_Shift
            Assert.Eq(KeyModifiers.None, kb.HeldModifiers, "AltGr is not Alt");
            Assert.Eq("@", kb.Press(3), "AltGr+2 types @");
        });

        t.Run("input: xkb — a dead key composes with the next", () =>
        {
            using var kb = Layout("us", "intl");
            Assert.Eq(null, kb.Press(KEY_APOSTROPHE), "dead acute types nothing yet");
            Assert.Eq("é", kb.Press(KEY_E));
            Assert.Eq("e", kb.Press(KEY_E), "and is used up");
            Assert.Eq(null, kb.Press(KEY_APOSTROPHE));
            kb.ResetCompose();
            Assert.Eq("e", kb.Press(KEY_E), "focus loss drops a pending sequence");
        });

        // ── The feature: keys to WindowEvents ────────────────────────────────

        t.Run("input: a press reports the key, then its text; a release the key", () =>
        {
            var events = new List<WindowEvent>();
            var input = new WaylandInput(events.Add);
            try
            {
                input.UseKeyboard(Layout("us"));
                input.OnKeyboardEnter();
                input.OnKey(KEY_A, WlKeyboard.KeyState.Pressed);
                input.OnKey(KEY_A, WlKeyboard.KeyState.Released);
                Assert.Eq("KeyDown A; KeyChar a; KeyUp A", Describe(events));
            }
            finally { input.Destroy(); }
        });

        t.Run("input: keyboard leave releases held keys and buttons, then FocusLost", () =>
        {
            var events = new List<WindowEvent>();
            var input = new WaylandInput(events.Add);
            try
            {
                var kb = Layout("us");
                input.UseKeyboard(kb);
                input.OnKeyboardEnter();
                kb.UpdateKey(KEY_LEFTSHIFT, true);
                input.OnKey(KEY_LEFTSHIFT, WlKeyboard.KeyState.Pressed);
                input.OnKey(KEY_A, WlKeyboard.KeyState.Pressed);
                Assert.Eq(KeyModifiers.Shift, input.HeldModifiers, "shift while focused");
                events.Clear();

                input.OnKeyboardLeave();
                var d = Describe(events);
                Assert.Contains(d, "KeyUp ShiftLeft");
                Assert.Contains(d, "KeyUp A");
                Assert.True(d.EndsWith("FocusLost", StringComparison.Ordinal), $"FocusLost last: {d}");
                Assert.Eq(KeyModifiers.None, input.HeldModifiers, "nothing held after leave");
                Assert.Eq(null, input.Repeat.Key, "repeat stopped");

                events.Clear();
                input.OnKey(KEY_A, WlKeyboard.KeyState.Released);
                Assert.Eq("", Describe(events), "a release after leave was already reported");
            }
            finally { input.Destroy(); }
        });

        t.Run("input: a repeat types the held key's text again, on the timerfd", () =>
        {
            var events = new List<WindowEvent>();
            var input = new WaylandInput(events.Add);
            try
            {
                input.UseKeyboard(Layout("us"));
                input.Repeat.SetInfo(rate: 200, delay: 20);
                input.OnKeyboardEnter();
                input.OnKey(KEY_A, WlKeyboard.KeyState.Pressed);
                Assert.True(input.RepeatFd >= 0, "timerfd armed");
                events.Clear();

                var fds = stackalloc Libc.PollFd[1];
                fds[0] = new Libc.PollFd { Fd = input.RepeatFd, Events = Libc.POLLIN };
                Assert.Eq(1, Libc.poll(fds, 1, 1000), "timer fired");
                input.OnRepeatTimer();
                Assert.True(events.Count >= 1, "a repeat");
                Assert.True(events.All(e => e.Kind == WindowEventKind.KeyChar && e.Char == 'a'), $"only text: {Describe(events)}");

                input.OnKey(KEY_A, WlKeyboard.KeyState.Released);
                Assert.Eq(null, input.Repeat.NextDue, "released: no repeat due");
                Assert.Eq(0, Libc.poll(fds, 1, 60), "disarmed");
            }
            finally { input.Destroy(); }
        });

        t.Run("input: a modifier pressed mid-repeat leaves the key repeating", () =>
        {
            var events = new List<WindowEvent>();
            var input = new WaylandInput(events.Add);
            try
            {
                input.UseKeyboard(Layout("us"));
                input.OnKeyboardEnter();
                input.OnKey(KEY_A, WlKeyboard.KeyState.Pressed);
                input.OnKey(KEY_LEFTSHIFT, WlKeyboard.KeyState.Pressed);
                Assert.Eq<uint?>(KEY_A, input.Repeat.Key, "a still repeats");
                input.OnKey(KEY_E, WlKeyboard.KeyState.Pressed);
                Assert.Eq<uint?>(KEY_E, input.Repeat.Key, "a new key takes it over");
                input.OnKey(KEY_A, WlKeyboard.KeyState.Released);
                Assert.Eq<uint?>(KEY_E, input.Repeat.Key, "releasing the old key does not stop the new");
            }
            finally { input.Destroy(); }
        });

        t.Run("input: no seat — nothing held, no timer, Destroy is safe", () =>
        {
            var input = new WaylandInput(_ => throw new Exception("no events without a seat"));
            Assert.Eq(KeyModifiers.None, input.HeldModifiers);
            Assert.Eq(-1, input.RepeatFd);
            Assert.True(input.Pointer.IsNull, "no pointer");
            input.Destroy();
            input.Destroy();
        });

        // ── Repeat schedule ──────────────────────────────────────────────────

        t.Run("repeat: first after the delay, then one per interval", () =>
        {
            var r = new KeyRepeat();
            r.SetInfo(rate: 25, delay: 600); // 40 ms interval
            r.Press(KEY_A, now: 1000);
            Assert.Eq(0, r.Take(1599), "before the delay");
            Assert.Eq(1, r.Take(1600), "at the delay");
            Assert.Eq<double?>(1640, r.NextDue);
            Assert.Eq(0, r.Take(1639));
            Assert.Eq(1, r.Take(1641), "one interval on");
            Assert.Eq(2, r.Take(1720), "two due at once");
            Assert.Eq<double?>(1760, r.NextDue, "kept on the beat");
        });

        t.Run("repeat: a stalled loop gets a burst, not a flood", () =>
        {
            var r = new KeyRepeat();
            r.SetInfo(25, 600);
            r.Press(KEY_A, 0);
            Assert.Eq(KeyRepeat.MaxBurst, r.Take(5000));
            Assert.Eq<double?>(5040, r.NextDue, "the beat restarts from now");
        });

        t.Run("repeat: release, another key, and rate 0", () =>
        {
            var r = new KeyRepeat();
            r.Press(KEY_A, 0);
            Assert.Eq<double?>(600, r.NextDue, "defaults: 600 ms delay");
            r.Release(KEY_E);
            Assert.Eq<uint?>(KEY_A, r.Key, "another key's release changes nothing");
            r.Press(KEY_E, 100);
            Assert.Eq<uint?>(KEY_E, r.Key, "the newest key repeats");
            r.Release(KEY_E);
            Assert.Eq(null, r.Key);
            Assert.Eq(0, r.Take(10_000), "stopped");

            r.SetInfo(rate: 0, delay: 500);
            r.Press(KEY_A, 0);
            Assert.Eq(null, r.NextDue, "rate 0: the compositor turned repeat off");
        });

        // ── Pointer frames ───────────────────────────────────────────────────

        t.Run("pointer: motions in one frame are one move, to where it ended", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(10, 10); f.Frame();
            f.Motion(11, 12); f.Motion(13.5f, 14.25f); f.Frame();
            Assert.Eq("MouseMove 10,10; MouseMove 13.5,14.25", Describe(ev));
        });

        t.Run("pointer: a move and a press together report the move first", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(0, 0); f.Frame(); ev.Clear();
            f.Button(BTN_LEFT, true); f.Motion(5, 6); f.Frame();
            f.Button(BTN_LEFT, false); f.Frame();
            Assert.Eq("MouseMove 5,6; MouseDown Left 5,6; MouseUp Left 5,6", Describe(ev));
        });

        t.Run("pointer: a press already held is not reported twice; an unheld release not at all", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(1, 1); f.Frame(); ev.Clear();
            f.Button(BTN_RIGHT, true); f.Frame();
            f.Button(BTN_RIGHT, true); f.Frame();
            f.Button(BTN_MIDDLE, false); f.Frame();
            Assert.Eq("MouseDown Right 1,1", Describe(ev));
        });

        t.Run("pointer: leave releases what is held, where it was last seen", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(4, 4); f.Frame();
            f.Button(BTN_LEFT, true); f.Motion(8, 9); f.Frame(); ev.Clear();
            f.Leave();
            Assert.Eq("MouseUp Left 8,9", Describe(ev));
            Assert.True(!f.Inside, "outside");
            ev.Clear();
            f.Motion(1, 1); f.Frame();
            Assert.Eq("", Describe(ev), "no move reported from outside");
        });

        t.Run("pointer: a wheel notch is 1.0, wheel up is positive (value120)", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(3, 4); f.Frame(); ev.Clear();
            // A notch down, as Hyprland sends it: axis value 15, value120 120.
            f.AxisValue(horizontal: false, 15f); f.AxisValue120(false, 120); f.Frame();
            // A notch up.
            f.AxisValue(false, -15f); f.AxisValue120(false, -120); f.Frame();
            // Half a notch from a high-resolution wheel.
            f.AxisValue(false, 7.5f); f.AxisValue120(false, 60); f.Frame();
            Assert.Eq("Scroll 0,-1 @3,4; Scroll 0,1 @3,4; Scroll 0,-0.5 @3,4", Describe(ev));
        });

        t.Run("pointer: axis_discrete before version 8, and the axis value without either", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(0, 0); f.Frame(); ev.Clear();
            f.AxisValue(false, 30f); f.AxisDiscrete(false, 2); f.Frame();       // two clicks, v5–7
            f.AxisValue(false, 25f); f.Frame();                                  // a touchpad: px / 10, as GLFW
            Assert.Eq("Scroll 0,-2 @0,0; Scroll 0,-2.5 @0,0", Describe(ev));
        });

        t.Run("pointer: both axes of one frame are one scroll; right is negative X, as GLFW", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(0, 0); f.Frame(); ev.Clear();
            f.AxisValue(true, 10f); f.AxisValue(false, -20f); f.Frame();
            Assert.Eq("Scroll -1,2 @0,0", Describe(ev));
            ev.Clear();
            f.AxisValue(true, -15f); f.AxisValue120(true, -120); f.Frame();
            Assert.Eq("Scroll 1,0 @0,0", Describe(ev), "a tilt left");
        });

        t.Run("pointer: values in one frame add up; an empty frame says nothing", () =>
        {
            var (f, ev) = Pointer();
            f.Enter(0, 0); f.Frame(); ev.Clear();
            f.AxisValue120(false, 60); f.AxisValue120(false, 60); f.AxisValue(false, 15f); f.Frame();
            f.Frame();
            Assert.Eq("Scroll 0,-1 @0,0", Describe(ev));
        });

        t.Run("pointer: before version 5 every event is its own frame", () =>
        {
            var (f, ev) = Pointer();
            f.Framed = false;
            f.Enter(1, 2);
            f.Motion(3, 4);
            f.Button(BTN_LEFT, true);
            f.AxisValue(false, 10f);
            Assert.Eq("MouseMove 1,2; MouseMove 3,4; MouseDown Left 3,4; Scroll 0,-1 @3,4", Describe(ev));
        });
    }

    private static (PointerFrame, List<WindowEvent>) Pointer()
    {
        var events = new List<WindowEvent>();
        return (new PointerFrame(events.Add), events);
    }

    private static string Describe(List<WindowEvent> events)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string P(float v) => v.ToString(ic);
        return string.Join("; ",
            events.Select(e => e.Kind switch
            {
                WindowEventKind.MouseMove => $"MouseMove {P(e.X)},{P(e.Y)}",
                WindowEventKind.MouseDown or WindowEventKind.MouseUp => $"{e.Kind} {e.Button} {P(e.X)},{P(e.Y)}",
                WindowEventKind.Scroll => $"Scroll {P(e.ScrollX)},{P(e.ScrollY)} @{P(e.X)},{P(e.Y)}",
                WindowEventKind.KeyDown or WindowEventKind.KeyUp => $"{e.Kind} {e.Key}",
                WindowEventKind.KeyChar => $"KeyChar {e.Char}",
                _ => e.Kind.ToString(),
            }));
    }
}
