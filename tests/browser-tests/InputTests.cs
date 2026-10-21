using System.Text;
using System.Text.RegularExpressions;
using Bibui.Core;
using Bibui.Platform.Browser;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.TextEdit;

namespace Bibui.BrowserTests;

/// <summary>What the canvas's input records mean: keys, buttons, modifiers, text, the wheel.</summary>
internal static class InputTests
{
    public static void Register(Harness t)
    {
        t.Run("keys: KeyboardEvent.code names the physical key, as GLFW does", () =>
        {
            Assert.Eq(Key.A, BrowserKeys.FromCode("KeyA"));
            Assert.Eq(Key.Z, BrowserKeys.FromCode("KeyZ"));
            Assert.Eq(Key.Number0, BrowserKeys.FromCode("Digit0"));
            Assert.Eq(Key.Number9, BrowserKeys.FromCode("Digit9"));
            Assert.Eq(Key.Keypad5, BrowserKeys.FromCode("Numpad5"));
            Assert.Eq(Key.F1, BrowserKeys.FromCode("F1"));
            Assert.Eq(Key.F12, BrowserKeys.FromCode("F12"));
            Assert.Eq(Key.Left, BrowserKeys.FromCode("ArrowLeft"));
            Assert.Eq(Key.Down, BrowserKeys.FromCode("ArrowDown"));
            Assert.Eq(Key.Home, BrowserKeys.FromCode("Home"));
            Assert.Eq(Key.Enter, BrowserKeys.FromCode("Enter"));
            Assert.Eq(Key.KeypadEnter, BrowserKeys.FromCode("NumpadEnter"));
            Assert.Eq(Key.Backspace, BrowserKeys.FromCode("Backspace"));
            Assert.Eq(Key.Delete, BrowserKeys.FromCode("Delete"));
            Assert.Eq(Key.Tab, BrowserKeys.FromCode("Tab"));
            Assert.Eq(Key.Escape, BrowserKeys.FromCode("Escape"));
            Assert.Eq(Key.Space, BrowserKeys.FromCode("Space"));
            Assert.Eq(Key.Apostrophe, BrowserKeys.FromCode("Quote"));
            Assert.Eq(Key.GraveAccent, BrowserKeys.FromCode("Backquote"));
            Assert.Eq(Key.SuperLeft, BrowserKeys.FromCode("MetaLeft"));
            Assert.Eq(Key.Unknown, BrowserKeys.FromCode("Lang1"));
            Assert.Eq(Key.Unknown, BrowserKeys.FromCode(""));
        });

        t.Run("keys: DOM buttons are bibui's (DOM 1 is the middle button, 2 the right)", () =>
        {
            Assert.Eq(MouseButton.Left, BrowserKeys.FromDomButton(0));
            Assert.Eq(MouseButton.Middle, BrowserKeys.FromDomButton(1));
            Assert.Eq(MouseButton.Right, BrowserKeys.FromDomButton(2));
            Assert.Eq(MouseButton.Unknown, BrowserKeys.FromDomButton(-1));
        });

        t.Run("keys: Meta is Super, which the runtime folds into Ctrl", () =>
        {
            Assert.Eq(KeyModifiers.None, BrowserKeys.FromDomModifiers(0));
            Assert.Eq(KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Super, BrowserKeys.FromDomModifiers(15));
            Assert.Eq(KeyModifiers.Super, BrowserKeys.FromDomModifiers(BrowserKeys.DomMeta));
        });

        t.Run("keys: a key types its one character, unless Ctrl or Meta makes it a shortcut", () =>
        {
            Assert.Eq(new Rune('a'), BrowserKeys.TextOf("a", KeyModifiers.None, false));
            Assert.Eq(new Rune('A'), BrowserKeys.TextOf("A", KeyModifiers.Shift, false));
            Assert.Eq(new Rune('é'), BrowserKeys.TextOf("é", KeyModifiers.None, false));
            Assert.Eq(new Rune(0x1F600), BrowserKeys.TextOf("😀", KeyModifiers.None, false));
            Assert.Eq(new Rune(' '), BrowserKeys.TextOf(" ", KeyModifiers.None, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("c", KeyModifiers.Ctrl, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("c", KeyModifiers.Super, false));
            Assert.Eq(new Rune('ä'), BrowserKeys.TextOf("ä", KeyModifiers.Alt, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("Enter", KeyModifiers.None, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("ArrowLeft", KeyModifiers.None, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("Dead", KeyModifiers.None, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("ab", KeyModifiers.None, false));
            Assert.Eq<Rune?>(null, BrowserKeys.TextOf("\t", KeyModifiers.None, false));
        });

        t.Run("keys: AltGr, which browsers report as Ctrl+Alt, types and is no shortcut", () =>
        {
            int ctrlAlt = BrowserKeys.DomCtrl | BrowserKeys.DomAlt;
            var held = BrowserKeys.HeldForKey(ctrlAlt, altGraph: true);
            Assert.Eq(KeyModifiers.None, held);
            Assert.Eq(new Rune('@'), BrowserKeys.TextOf("@", held, true));
            Assert.Eq(KeyModifiers.Ctrl | KeyModifiers.Alt, BrowserKeys.HeldForKey(ctrlAlt, altGraph: false));
        });

        t.Run("records: pointer, wheel, keys, focus and paste become WindowEvents, in order", () =>
        {
            var (data, strings) = Records(
                (BrowserInput.Move, [10.5, 20, -1, 0], []),
                (BrowserInput.Down, [11, 21, 0, BrowserKeys.DomShift], []),
                (BrowserInput.Up, [12, 22, 2, 0], []),
                (BrowserInput.Wheel, [5, 6, 0, 100, 0, 0], []),
                (BrowserInput.KeyDown, [0, 1, 0, 0], ["KeyA", "a"]),
                (BrowserInput.KeyDown, [0, 1, BrowserKeys.DomCtrl, 0], ["KeyC", "c"]),
                (BrowserInput.KeyUp, [0, 0], ["KeyA"]),
                (BrowserInput.FocusLost, [], []),
                (BrowserInput.Paste, [0], ["pasted text"]));
            var events = new List<WindowEvent>();
            string? pasted = null;
            BrowserInput.Decode(data, strings, events, ref pasted);

            Assert.Eq(11, events.Count, "events");
            Assert.Eq(WindowEventKind.MouseMove, events[0].Kind);
            Assert.Eq(10.5f, events[0].X);
            Assert.Eq(20f, events[0].Y);
            Assert.Eq(WindowEventKind.MouseDown, events[1].Kind);
            Assert.Eq(MouseButton.Left, events[1].Button);
            Assert.Eq(KeyModifiers.Shift, events[1].Modifiers);
            Assert.Eq(WindowEventKind.MouseUp, events[2].Kind);
            Assert.Eq(MouseButton.Right, events[2].Button);
            Assert.Eq(WindowEventKind.Scroll, events[3].Kind);
            Assert.Eq(-1f, events[3].ScrollY, "a 100 px wheel down is one notch down");
            Assert.Eq(WindowEventKind.KeyDown, events[4].Kind);
            Assert.Eq(Key.A, events[4].Key);
            Assert.Eq(WindowEventKind.KeyChar, events[5].Kind, "the character follows its key-down");
            Assert.Eq(new Rune('a'), events[5].CodePoint);
            Assert.Eq(Key.C, events[6].Key);
            Assert.Eq(KeyModifiers.Ctrl, events[6].Modifiers);
            Assert.Eq(WindowEventKind.KeyUp, events[7].Kind, "Ctrl+C types nothing");
            Assert.Eq(WindowEventKind.FocusLost, events[8].Kind);
            Assert.Eq("pasted text", pasted);
            Assert.Eq(WindowEventKind.KeyDown, events[9].Kind);
            Assert.Eq(Key.V, events[9].Key);
            Assert.Eq(KeyModifiers.Ctrl, events[9].Modifiers);
            Assert.Eq(WindowEventKind.KeyUp, events[10].Kind);
        });

        t.Run("records: a key-down the browser repeats is a KeyRepeat, and types again", () =>
        {
            var (data, strings) = Records(
                (BrowserInput.KeyDown, [0, 1, 0, 0, 0], ["Backspace", "Backspace"]),
                (BrowserInput.KeyDown, [0, 1, 0, 0, 1], ["Backspace", "Backspace"]),
                (BrowserInput.KeyDown, [0, 1, 0, 0, 1], ["KeyA", "a"]));
            var events = new List<WindowEvent>();
            string? pasted = null;
            BrowserInput.Decode(data, strings, events, ref pasted);

            Assert.Eq(4, events.Count, "events");
            Assert.True(!events[0].Repeat, "the press is no repeat");
            Assert.True(events[1].Repeat && events[1].Key == Key.Backspace, "held: repeated");
            Assert.True(events[2].Repeat && events[2].Key == Key.A, "a held letter repeats");
            Assert.Eq(new Rune('a'), events[3].CodePoint, "and types again");
        });

        t.Run("records: the header says the canvas's size, ratio and whether it is in view", () =>
        {
            var state = BrowserInput.ReadHeader([1, 792, 400, 1.25, 0]);
            Assert.Eq(new BrowserInput.CanvasState(true, 792, 400, 1.25f, 0), state);
            Assert.Eq(default, BrowserInput.ReadHeader([1, 2]));
            var events = new List<WindowEvent>();
            string? pasted = null;
            BrowserInput.Decode([1, 10, 10, 1, 3, BrowserInput.Move, 1, 2], [], events, ref pasted);
            Assert.Eq(0, events.Count, "a cut-off record is not read");
        });

        t.Run("records: the wheel is in notches from pixels, lines or pages", () =>
        {
            Assert.Eq((0f, -1f), BrowserInput.Notches(0, 100, 0));
            Assert.Eq((1f, 0f), BrowserInput.Notches(-100, 0, 0));
            Assert.Eq((0f, -1f), BrowserInput.Notches(0, 3, 1));
            Assert.Eq((0f, 3f), BrowserInput.Notches(0, -1, 2));
        });

        t.Run("records: the runtime makes intents of them as it does a desktop window's", () =>
        {
            var runtime = BibuiRuntime.Create();
            var (data, strings) = Records(
                (BrowserInput.KeyDown, [0, 1, 0, 0], ["ArrowLeft", "ArrowLeft"]),
                (BrowserInput.KeyDown, [0, 1, 0, 0], ["Enter", "Enter"]),
                (BrowserInput.KeyDown, [0, 1, 0, 0], ["Backspace", "Backspace"]),
                (BrowserInput.KeyDown, [0, 1, BrowserKeys.DomMeta, 0], ["KeyC", "c"]),
                (BrowserInput.KeyDown, [0, 1, 0, 0], ["KeyQ", "q"]));
            var events = new List<WindowEvent>();
            string? pasted = null;
            BrowserInput.Decode(data, strings, events, ref pasted);
            runtime.BeginBuild(events);
            var kinds = runtime.UnconsumedEvents.Select(e => e.Kind).ToList();
            runtime.EndBuild();
            Assert.Eq(string.Join(",", new[] { BibUiEventKind.Navigate, BibUiEventKind.Submit, BibUiEventKind.Edit,
                BibUiEventKind.Copy, BibUiEventKind.Press, BibUiEventKind.Text }), string.Join(",", kinds), "kinds");
        });

        t.Run("records: bibui-browser.js writes the layout BrowserInput reads", () =>
        {
            string js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bibui-browser.js"));
            Assert.Eq(BrowserInput.Stride.ToString(), Regex.Match(js, @"const STRIDE = (\d+);").Groups[1].Value, "stride");
            var kinds = new Dictionary<string, int>
            {
                ["MOVE"] = BrowserInput.Move, ["DOWN"] = BrowserInput.Down, ["UP"] = BrowserInput.Up,
                ["WHEEL"] = BrowserInput.Wheel, ["KEY_DOWN"] = BrowserInput.KeyDown, ["KEY_UP"] = BrowserInput.KeyUp,
                ["FOCUS_LOST"] = BrowserInput.FocusLost, ["PASTE"] = BrowserInput.Paste,
            };
            foreach (var (name, value) in kinds)
                Assert.Eq(value.ToString(), Regex.Match(js, $@"\b{name} = (\d+)").Groups[1].Value, name);
            Assert.True(js.Contains($"[s.visible ? 1 : 0, s.width, s.height, s.ratio, count, ...s.records]"), "the header is the one ReadHeader reads");
        });

        t.Run("cursor: each shape is a CSS cursor", () =>
        {
            Assert.Eq("pointer", CanvasCursor.Css(CursorShape.Pointer));
            Assert.Eq("text", CanvasCursor.Css(CursorShape.Text));
            Assert.Eq("ew-resize", CanvasCursor.Css(CursorShape.ResizeHorizontal));
            Assert.Eq("ns-resize", CanvasCursor.Css(CursorShape.ResizeVertical));
            Assert.Eq("", CanvasCursor.Css(CursorShape.Default));
        });
    }

    // Records as bibui-browser.js writes them: a header, then each record
    // padded to the stride, its string indexes into one shared array.
    private static (double[] Data, List<string> Strings) Records(params (int Kind, double[] Fields, string[] Strings)[] records)
    {
        var data = new List<double> { 1, 100, 100, 1, records.Length };
        var strings = new List<string>();
        foreach (var (kind, fields, texts) in records)
        {
            int baseIndex = strings.Count;
            strings.AddRange(texts);
            var row = new double[BrowserInput.Stride];
            row[0] = kind;
            for (int i = 0; i < fields.Length; i++) row[i + 1] = fields[i];
            // String fields of a record index its own strings; shift them to the shared array.
            if (texts.Length > 0)
            {
                int[] stringFields = kind switch
                {
                    BrowserInput.KeyDown => [1, 2],
                    BrowserInput.KeyUp or BrowserInput.Paste => [1],
                    _ => [],
                };
                foreach (int f in stringFields) row[f] += baseIndex;
            }
            data.AddRange(row);
        }
        return (data.ToArray(), strings);
    }
}
