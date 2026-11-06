// Minimal standalone test runner for TextOp / BibuiStr.
// No rendering — just the edit logic, in chars (UTF-16 code units), with clusters measured in the default theme's font and an emoji face.
// Run with: dotnet run --project tests/text-tests
//
// Two layers under test:
//   1. TextOp.FromStateAndEvent constructs an op (Range / ReplaceString / NewCursor / NewMark)
//      but does NOT mutate the buffer. The first sections check op fields.
//   2. BibuiStr.ReplaceRange performs the splice. The last section applies the op
//      against the buffer and checks the resulting text.

using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;
using Bibui.Text;

int passed = 0, failed = 0;
char[] charBuf = new char[TextOp.MaxCharsPerCodePoint];
// The fonts and font a move by one character measures clusters in: the default
// theme's, with the runtime tests' subset of Noto Color Emoji as the cluster face,
// as a platform sets up the system's.
var runtime = BibuiRuntime.Create();
Fonts fonts = runtime.Fonts;
fonts.UseClusterFont(Bibui.Text.FreeType.FontData.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoColorEmoji-subset.ttf")));
Font font = runtime.FontInScope();

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}");
        Console.WriteLine($"         {ex.Message}");
        failed++;
    }
}

void Eq<T>(T expected, T actual, string? message = null)
{
    if (!Equals(expected, actual))
        throw new Exception(message ?? $"Expected {expected} but got {actual}");
}

// Apply an event to (str, state) via the full pipeline: construct op,
// splice via ReplaceRange, commit cursor + mark. Then verify the
// buffer's text and the new cursor/mark.
void ApplyAndCheck(BibuiStr str, TextEditState state, BibUiEvent ev,
                   string expectedText, int expectedCursor, int expectedMark)
{
    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);
    state.Cursor = op.NewCursor;
    state.Mark   = op.NewMark;

    var actual = str.ToString();
    Eq(expectedText,   actual,       $"text after op: expected \"{expectedText}\" but got \"{actual}\"");
    Eq(expectedCursor, state.Cursor, $"cursor after op: expected {expectedCursor} but got {state.Cursor}");
    Eq(expectedMark,   state.Mark,   $"mark after op: expected {expectedMark} but got {state.Mark}");
}

Console.WriteLine("text-tests");
Console.WriteLine("==========");

// ── Single-step movement ──────────────────────────────────────────────────────

Run("ArrowLeft over ASCII moves cursor back 1 char", () =>
{
    var str    = BibuiStr.FromStrLit("hello");
    var state  = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(4, op.NewCursor, "cursor should move back 1 char");
    Eq(4, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowRight over ASCII moves cursor forward 1 char", () =>
{
    var str    = BibuiStr.FromStrLit("hello");
    var state  = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(1, op.NewCursor, "cursor should move forward 1 char");
    Eq(1, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowLeft over a surrogate-pair emoji moves cursor back 2 chars", () =>
{
    var str    = BibuiStr.FromStrLit("a🎉");
    var state  = new TextEditState { Cursor = 3, Mark = 3 };
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(1, op.NewCursor, "cursor should move back 2 chars (past the whole emoji)");
    Eq(1, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowRight over a surrogate-pair emoji moves cursor forward 2 chars", () =>
{
    var str    = BibuiStr.FromStrLit("🎉a");
    var state  = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(2, op.NewCursor, "cursor should move forward 2 chars (past the whole emoji)");
    Eq(2, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowLeft with KeepMark (shift+left) extends selection", () =>
{
    var str    = BibuiStr.FromStrLit("hello");
    var state  = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.KeepMark };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(4, op.NewCursor, "cursor should move back 1 char");
    Eq(5, op.NewMark,   "mark should stay at original position");
});

// ── Boundary cases ────────────────────────────────────────────────────────────

Run("ArrowRight at end of string keeps cursor at end", () =>
{
    var str    = BibuiStr.FromStrLit("hello");
    var state  = new TextEditState { Cursor = 5, Mark = 5 }; // already at end
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(5, op.NewCursor, "cursor should not move past end of buffer");
    Eq(5, op.NewMark,   "mark should stay at end");
});

Run("ArrowLeft at start of string keeps cursor at 0", () =>
{
    var str    = BibuiStr.FromStrLit("hello");
    var state  = new TextEditState { Cursor = 0, Mark = 0 }; // already at start
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(0, op.NewCursor, "cursor should not move below 0");
    Eq(0, op.NewMark,   "mark should stay at 0");
});

// ── Multi-step navigation through mixed ASCII and surrogate pairs ─────────────

Run("Navigate around the middle of 'a🎉b' with chained arrow keys", () =>
{
    // Chars: 'a' (1) + '🎉' (2, a surrogate pair) + 'b' (1) = 4 chars total
    var str   = BibuiStr.FromStrLit("a🎉b");
    var state = new TextEditState { Cursor = 1, Mark = 1 }; // between 'a' and emoji

    // ArrowRight: skip the emoji (2 chars) → cursor 3
    var op1 = TextOp.FromStateAndEvent(fonts, font, str, state, new BibUiEvent { Delta = +1 }, charBuf);
    Eq(3, op1.NewCursor, "after first ArrowRight, cursor should be past emoji");
    state.Cursor = op1.NewCursor;
    state.Mark   = op1.NewMark;

    // ArrowRight: step over 'b' → cursor 4 (end of string)
    var op2 = TextOp.FromStateAndEvent(fonts, font, str, state, new BibUiEvent { Delta = +1 }, charBuf);
    Eq(4, op2.NewCursor, "after second ArrowRight, cursor should be at end");
    state.Cursor = op2.NewCursor;
    state.Mark   = op2.NewMark;

    // ArrowLeft: step back over 'b' → cursor 3
    var op3 = TextOp.FromStateAndEvent(fonts, font, str, state, new BibUiEvent { Delta = -1 }, charBuf);
    Eq(3, op3.NewCursor, "after ArrowLeft, cursor should be back at 3");
    state.Cursor = op3.NewCursor;
    state.Mark   = op3.NewMark;

    // ArrowLeft: step back over the emoji (2 chars) → cursor 1
    var op4 = TextOp.FromStateAndEvent(fonts, font, str, state, new BibUiEvent { Delta = -1 }, charBuf);
    Eq(1, op4.NewCursor, "after second ArrowLeft, cursor should be back between 'a' and emoji");
});

Run("Multiple ArrowRight events at end of string leave cursor at end", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 5, Mark = 5 }; // at end of 5-char string

    // Simulate the runtime draining a burst of ArrowRight events for one frame.
    // Each event is independent; we feed the resolved state into the next call.
    for (int i = 0; i < 5; i++)
    {
        var ev = new BibUiEvent { Delta = +1 };
        var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);
        state.Cursor = op.NewCursor;
        state.Mark   = op.NewMark;
    }

    Eq(5, state.Cursor, "cursor should stay at end after repeated ArrowRight");
    Eq(5, state.Mark,   "mark should stay at end after repeated ArrowRight");
});

// ── Word-scan navigation ──────────────────────────────────────────────────────

Run("WordScan forward jumps past the current word", () =>
{
    // "hello world" — Ctrl+Right from offset 0 should land at offset 5 (end of "hello")
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(5, op.NewCursor, "cursor should land at end of 'hello'");
    Eq(5, op.NewMark,   "mark should collapse to cursor");
});

Run("WordScan backward jumps to the start of the current word", () =>
{
    // "hello world" — Ctrl+Left from offset 11 should land at offset 6 (start of "world")
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 11, Mark = 11 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(6, op.NewCursor, "cursor should land at start of 'world'");
    Eq(6, op.NewMark,   "mark should collapse to cursor");
});

// ── An emoji of several code points ───────────────────────────────────────────
//
// 🧑‍🚀 is three code points — person, ZWJ, rocket — and 5 UTF-16 chars, drawn
// as one character: the emoji face's ligature. An arrow key or Backspace
// crosses it whole, and Ctrl+Arrow treats it as a word like any other run of
// non-whitespace. Offsets are in chars, not code points: in "go 🧑‍🚀 now" the
// astronaut is chars [3..8), though only code points [3..6).

const string Astronaut = "🧑‍🚀";   // [0..5)

Run("ArrowRight over a multi-code-point emoji crosses all 5 chars of it", () =>
{
    // "go 🧑‍🚀 now" — the astronaut is [3..8)
    var str   = BibuiStr.FromStrLit($"go {Astronaut} now");
    var state = new TextEditState { Cursor = 3, Mark = 3 };
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(8, op.NewCursor, "cursor should land just past the astronaut");
    Eq(8, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowLeft over a multi-code-point emoji crosses all 5 chars of it", () =>
{
    var str   = BibuiStr.FromStrLit($"go {Astronaut} now");
    var state = new TextEditState { Cursor = 8, Mark = 8 };
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(3, op.NewCursor, "cursor should land just before the astronaut");
    Eq(3, op.NewMark,   "mark should collapse to cursor");
});

Run("Backspace after a multi-code-point emoji deletes all 5 chars of it", () =>
{
    var str   = BibuiStr.FromStrLit($"go {Astronaut} now");
    var state = new TextEditState { Cursor = 8, Mark = 8 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);

    Eq((3, 8), op.Range,   "the op should cover the whole astronaut");
    Eq("go  now", str.ToString(), "the astronaut should be gone, and nothing else");
    Eq(3, op.NewCursor,     "cursor should land where the astronaut began");
    Eq(3, op.NewMark,       "mark should collapse to cursor");
});

Run("WordScan forward over a multi-code-point emoji lands past it", () =>
{
    // From the end of "go": skip the space, then the astronaut as one word
    var str   = BibuiStr.FromStrLit($"go {Astronaut} now");
    var state = new TextEditState { Cursor = 2, Mark = 2 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(8, op.NewCursor, "cursor should land just past the astronaut");
    Eq(8, op.NewMark,   "mark should collapse to cursor");
});

Run("WordScan backward over a multi-code-point emoji lands before it", () =>
{
    // From the start of "now": skip the space, then the astronaut as one word
    var str   = BibuiStr.FromStrLit($"go {Astronaut} now");
    var state = new TextEditState { Cursor = 9, Mark = 9 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(3, op.NewCursor, "cursor should land just before the astronaut");
    Eq(3, op.NewMark,   "mark should collapse to cursor");
});

Run("WordScan treats letters and a multi-code-point emoji with no space between as one word", () =>
{
    // "hi🧑‍🚀 there" — "hi" and the astronaut are the word [0..7)
    var str   = BibuiStr.FromStrLit($"hi{Astronaut} there");
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq(7, op.NewCursor, "cursor should land just past the astronaut");
});

// ── Copy / Cut / Paste ────────────────────────────────────────────────────────

Run("Copy on a selection fills CopyString and preserves the selection", () =>
{
    // "hello world", selection over "world" (cursor=11, mark=6). A Copy event
    // sets KeepMark, so the selection survives being copied.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 11, Mark = 6 };
    var ev = new BibUiEvent { Flags = TextActionFlags.Copy | TextActionFlags.KeepMark };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);

    Eq("world", new string(op.CopyString), "CopyString should contain the selection");
    Eq(11, op.NewCursor, "cursor should be preserved by Copy (KeepMark)");
    Eq(6,  op.NewMark,   "mark should be preserved by Copy");
    Eq("hello world", str.ToString(), "buffer should be unchanged by Copy");
});

Run("Submit puts a line break at the cursor and lets go of the selection", () =>
{
    // "hello world", "o w" selected with the cursor at its end.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 7, Mark = 4 };
    var ev = new BibUiEvent { Kind = BibUiEventKind.Submit, Key = Bibui.Core.Key.Enter };
    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);
    Eq("hello w\norld", str.ToString(), "the selected text stays");
    Eq(8, op.NewCursor, "after the line break"); Eq(8, op.NewMark, "no selection");
});

Run("A move between lines changes nothing, not even the selection", () =>
{
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 7, Mark = 4 };
    var op = TextOp.FromStateAndEvent(fonts, font, str, state, new BibUiEvent { Kind = BibUiEventKind.Navigate, DeltaY = -1 }, charBuf);
    Eq(op.Range.Item1, op.Range.Item2, "nothing replaced");
    Eq(7, op.NewCursor); Eq(4, op.NewMark);
});

Run("Ctrl+Backspace deletes back to the start of the word", () =>
{
    var ev = Bibui.Core.TextEdit.BibUiEvent.FromKey(Bibui.Core.Key.Backspace, ctrl: true, shift: false);
    ApplyAndCheck(BibuiStr.FromStrLit("hello world"),
        new TextEditState { Cursor = 11, Mark = 11 }, ev, "hello ", 6, 6);
    // From inside a word, only its part before the cursor.
    ApplyAndCheck(BibuiStr.FromStrLit("hello world"),
        new TextEditState { Cursor = 9, Mark = 9 }, ev, "hello ld", 6, 6);
    // After a space, the space and the word before it, as ctrl+left would move.
    ApplyAndCheck(BibuiStr.FromStrLit("hello world"),
        new TextEditState { Cursor = 6, Mark = 6 }, ev, "world", 0, 0);
});

Run("Ctrl+Delete deletes on to the end of the word", () =>
{
    var ev = Bibui.Core.TextEdit.BibUiEvent.FromKey(Bibui.Core.Key.Delete, ctrl: true, shift: false);
    ApplyAndCheck(BibuiStr.FromStrLit("hello world"),
        new TextEditState { Cursor = 5, Mark = 5 }, ev, "hello", 5, 5);
});

Run("Ctrl+Backspace with a selection deletes only the selection", () =>
{
    var ev = Bibui.Core.TextEdit.BibUiEvent.FromKey(Bibui.Core.Key.Backspace, ctrl: true, shift: false);
    ApplyAndCheck(BibuiStr.FromStrLit("hello world"),
        new TextEditState { Cursor = 8, Mark = 6 }, ev, "hello rld", 6, 6);
});

Run("Ctrl+C is a Copy, which leaves the text alone; Ctrl+X is an Edit", () =>
{
    var copy = Bibui.Core.TextEdit.BibUiEvent.FromKey(Bibui.Core.Key.C, ctrl: true, shift: false);
    Eq(BibUiEventKind.Copy, copy.Kind);
    var str   = BibuiStr.FromStrLit("hello world");
    var op = TextOp.FromStateAndEvent(fonts, font, str, new TextEditState { Cursor = 11, Mark = 6 }, copy, charBuf);
    Eq("world", new string(op.CopyString));
    Eq(op.Range.Item1, op.Range.Item2, "nothing replaced");
    Eq(11, op.NewCursor); Eq(6, op.NewMark);

    var cut = Bibui.Core.TextEdit.BibUiEvent.FromKey(Bibui.Core.Key.X, ctrl: true, shift: false);
    Eq(BibUiEventKind.Edit, cut.Kind, "cut deletes, so it is an edit");
    ApplyAndCheck(BibuiStr.FromStrLit("hello world"),
        new TextEditState { Cursor = 11, Mark = 6 }, cut, "hello ", 6, 6);
});

Run("Paste inserts ev.PasteData at the cursor", () =>
{
    // No selection — paste lands at the cursor.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent
    {
        Flags     = TextActionFlags.Paste,
        PasteData = " THERE",
    };

    ApplyAndCheck(str, state, ev, "hello THERE world", 11, 11);
});

Run("Paste over a selection replaces it with ev.PasteData", () =>
{
    // "hello world" with "world" selected (cursor=11, mark=6) — pasting "there" replaces.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 11, Mark = 6 };
    var ev = new BibUiEvent
    {
        Flags     = TextActionFlags.Paste,
        PasteData = "there",
    };

    ApplyAndCheck(str, state, ev, "hello there", 11, 11);
});

Run("Cut (Copy | Delete) fills CopyString AND removes the selection", () =>
{
    // Same selection. Cut = Copy + Delete; CopyString fills first, Delete then
    // clears the range and collapses the cursor.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 11, Mark = 6 };
    var ev = new BibUiEvent { Flags = TextActionFlags.Copy | TextActionFlags.Delete };

    var op = TextOp.FromStateAndEvent(fonts, font, str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);
    state.Cursor = op.NewCursor;
    state.Mark   = op.NewMark;

    Eq("world",  new string(op.CopyString),    "CopyString should hold what we cut");
    Eq("hello ", str.ToString(),          "buffer should have the selection removed");
    Eq(6, state.Cursor, "cursor should collapse to min");
    Eq(6, state.Mark,   "mark should collapse to min");
});

// ── Apply operation to buffer (splice via ReplaceRange) ───────────────────────
//
// These exercise the full pipeline: build op, run ReplaceRange, commit state.
// Buffer contents are what end up on screen, so this is the contract that
// actually matters for the editor working.

Run("Backspace at end of ASCII deletes the last char", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "hell", 4, 4);
});

Run("Backspace in middle of ASCII deletes one char", () =>
{
    // "hello world", cursor at offset 6 (after the space, before 'w') — backspace removes the space
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 6, Mark = 6 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "helloworld", 5, 5);
});

Run("Backspace at start of buffer is a no-op", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "hello", 0, 0);
});

Run("Backspace over a surrogate-pair emoji removes the whole code point", () =>
{
    // "a🎉b", cursor just past the emoji (offset 3)
    var str   = BibuiStr.FromStrLit("a🎉b");
    var state = new TextEditState { Cursor = 3, Mark = 3 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "ab", 1, 1);
});

Run("Delete (forward) at start removes the first char", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "ello", 0, 0);
});

Run("Delete in middle of ASCII", () =>
{
    // "hello world", cursor between 'o' and ' ' (offset 5) — should remove the space
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "helloworld", 5, 5);
});

Run("Delete at end of buffer is a no-op", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "hello", 5, 5);
});

Run("Type ASCII codepoint at end of buffer", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { CodePoint = '!' };

    ApplyAndCheck(str, state, ev, "hello!", 6, 6);
});

Run("Type ASCII codepoint in middle of buffer", () =>
{
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { CodePoint = ',' };

    ApplyAndCheck(str, state, ev, "hello, world", 6, 6);
});

Run("Type ASCII codepoint at start of buffer", () =>
{
    var str   = BibuiStr.FromStrLit("ello");
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { CodePoint = 'h' };

    ApplyAndCheck(str, state, ev, "hello", 1, 1);
});

Run("Type an emoji code point above U+FFFF writes its 2 chars", () =>
{
    // 🎉 = U+1F389, encodes to a surrogate pair: 2 UTF-16 chars
    var str   = BibuiStr.FromStrLit("ab");
    var state = new TextEditState { Cursor = 1, Mark = 1 };
    var ev = new BibUiEvent { CodePoint = 0x1F389 };

    ApplyAndCheck(str, state, ev, "a🎉b", 3, 3);
});

Run("Typing into a selection (cursor < mark) replaces it", () =>
{
    // "hello world", selection covers "llo " (offsets 2..6)
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 2, Mark = 6 };
    var ev = new BibUiEvent { CodePoint = 'X' };

    ApplyAndCheck(str, state, ev, "heXworld", 3, 3);
});

Run("Typing 'z' over the 'hello' selection in 'hello world' yields 'z world'", () =>
{
    // Selection made by dragging left-to-right: mark anchors at 0, cursor ends at 5.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 5, Mark = 0 };
    var ev = new BibUiEvent { CodePoint = 'z' };

    ApplyAndCheck(str, state, ev, "z world", 1, 1);
});

Run("Typing into a selection (cursor > mark) replaces it", () =>
{
    // Same range, but cursor is on the right end. Mirrors the user dragging right-to-left
    // vs left-to-right; the splice should not care about direction.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 6, Mark = 2 };
    var ev = new BibUiEvent { CodePoint = 'X' };

    ApplyAndCheck(str, state, ev, "heXworld", 3, 3);
});

Run("Backspace with a selection (cursor < mark) deletes the selection", () =>
{
    // "hello world", selection covers "llo " (offsets 2..6). Backspace should delete
    // exactly the selection, NOT also one extra char before it.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 2, Mark = 6 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "heworld", 2, 2);
});

Run("Backspace with a selection (cursor > mark) deletes the selection", () =>
{
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 6, Mark = 2 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "heworld", 2, 2);
});

Run("Delete (forward) with a selection deletes the selection", () =>
{
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 2, Mark = 6 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "heworld", 2, 2);
});

Run("Sequential typing builds up the buffer", () =>
{
    var str   = BibuiStr.FromStrLit("");
    var state = new TextEditState { Cursor = 0, Mark = 0 };

    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = 'a' }, "a",   1, 1);
    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = 'b' }, "ab",  2, 2);
    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = 'c' }, "abc", 3, 3);
});

Run("Type then backspace returns to original", () =>
{
    var str   = BibuiStr.FromStrLit("hello");
    var state = new TextEditState { Cursor = 5, Mark = 5 };

    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = '!' }, "hello!", 6, 6);
    ApplyAndCheck(str, state,
        new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection },
        "hello", 5, 5);
});

Run("Deleting a large selection triggers shrink without losing content", () =>
{
    // "hello world" → Length=11, Capacity=12. Deleting a left-to-right selection of
    // 8 chars leaves newLen=3, which fires the shrink branch (3 <= 12>>2 = 3).
    // AlignedResize(3) → Capacity=4, but Length is still 11 at the time the splice
    // reads Str[range.Item2..] → AsSpan(0, 11) on a 4-char array throws.
    var str   = BibuiStr.FromStrLit("hello world");
    var state = new TextEditState { Cursor = 0, Mark = 8 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "rld", 0, 0);
});

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
if (failed > 0) Environment.Exit(1);
