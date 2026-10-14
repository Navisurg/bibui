// Minimal standalone test runner for TextOp / BibuiStr.
// No Skia, no runtime, no rendering — just byte-level edit logic.
// Run with: dotnet run --project tests/text-tests
//
// Two layers under test:
//   1. TextOp.FromStateAndEvent constructs an op (Range / ReplaceString / NewCursor / NewMark)
//      but does NOT mutate the buffer. The first sections check op fields.
//   2. BibuiStr.ReplaceRange performs the byte splice. The last section applies the op
//      against the buffer and checks the resulting bytes.

using System.Text;
using Bibui;
using Bibui.TextEdit;

int passed = 0, failed = 0;
char[] charBuf = new char[TextOp.MaxCharsPerCodePoint];

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
// buffer's UTF-8 contents and the new cursor/mark.
void ApplyAndCheck(BibuiStr str, TextEditState state, BibUiEvent ev,
                   string expectedText, int expectedCursor, int expectedMark)
{
    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);
    state.Cursor = op.NewCursor;
    state.Mark   = op.NewMark;

    var actual = Encoding.UTF8.GetString(str.Str);
    Eq(expectedText,   actual,       $"text after op: expected \"{expectedText}\" but got \"{actual}\"");
    Eq(expectedCursor, state.Cursor, $"cursor after op: expected {expectedCursor} but got {state.Cursor}");
    Eq(expectedMark,   state.Mark,   $"mark after op: expected {expectedMark} but got {state.Mark}");
}

Console.WriteLine("text-tests");
Console.WriteLine("==========");

// ── Single-step movement ──────────────────────────────────────────────────────

Run("ArrowLeft over ASCII moves cursor back 1 byte", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state  = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(4, op.NewCursor, "cursor should move back 1 byte");
    Eq(4, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowRight over ASCII moves cursor forward 1 byte", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state  = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(1, op.NewCursor, "cursor should move forward 1 byte");
    Eq(1, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowLeft over a 4-byte emoji moves cursor back 4 bytes", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("a🎉"));
    var state  = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(1, op.NewCursor, "cursor should move back 4 bytes (past the whole emoji)");
    Eq(1, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowRight over a 4-byte emoji moves cursor forward 4 bytes", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("🎉a"));
    var state  = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(4, op.NewCursor, "cursor should move forward 4 bytes (past the whole emoji)");
    Eq(4, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowLeft with KeepMark (shift+left) extends selection", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state  = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.KeepMark };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(4, op.NewCursor, "cursor should move back 1 byte");
    Eq(5, op.NewMark,   "mark should stay at original position");
});

// ── Boundary cases ────────────────────────────────────────────────────────────

Run("ArrowRight at end of string keeps cursor at end", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state  = new TextEditState { Cursor = 5, Mark = 5 }; // already at end
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(5, op.NewCursor, "cursor should not move past end of buffer");
    Eq(5, op.NewMark,   "mark should stay at end");
});

Run("ArrowLeft at start of string keeps cursor at 0", () =>
{
    var str    = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state  = new TextEditState { Cursor = 0, Mark = 0 }; // already at start
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(0, op.NewCursor, "cursor should not move below 0");
    Eq(0, op.NewMark,   "mark should stay at 0");
});

// ── Multi-step navigation through mixed ASCII + multi-byte content ────────────

Run("Navigate around the middle of 'a🎉b' with chained arrow keys", () =>
{
    // Bytes: 'a' (1) + '🎉' (4) + 'b' (1) = 6 bytes total
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("a🎉b"));
    var state = new TextEditState { Cursor = 1, Mark = 1 }; // between 'a' and emoji

    // ArrowRight: skip the emoji (4 bytes) → cursor 5
    var op1 = TextOp.FromStateAndEvent(str, state, new BibUiEvent { Delta = +1 }, charBuf);
    Eq(5, op1.NewCursor, "after first ArrowRight, cursor should be past emoji");
    state.Cursor = op1.NewCursor;
    state.Mark   = op1.NewMark;

    // ArrowRight: step over 'b' → cursor 6 (end of string)
    var op2 = TextOp.FromStateAndEvent(str, state, new BibUiEvent { Delta = +1 }, charBuf);
    Eq(6, op2.NewCursor, "after second ArrowRight, cursor should be at end");
    state.Cursor = op2.NewCursor;
    state.Mark   = op2.NewMark;

    // ArrowLeft: step back over 'b' → cursor 5
    var op3 = TextOp.FromStateAndEvent(str, state, new BibUiEvent { Delta = -1 }, charBuf);
    Eq(5, op3.NewCursor, "after ArrowLeft, cursor should be back at 5");
    state.Cursor = op3.NewCursor;
    state.Mark   = op3.NewMark;

    // ArrowLeft: step back over the emoji (4 bytes) → cursor 1
    var op4 = TextOp.FromStateAndEvent(str, state, new BibUiEvent { Delta = -1 }, charBuf);
    Eq(1, op4.NewCursor, "after second ArrowLeft, cursor should be back between 'a' and emoji");
});

Run("Multiple ArrowRight events at end of string leave cursor at end", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 5, Mark = 5 }; // at end of 5-byte string

    // Simulate the runtime draining a burst of ArrowRight events for one frame.
    // Each event is independent; we feed the resolved state into the next call.
    for (int i = 0; i < 5; i++)
    {
        var ev = new BibUiEvent { Delta = +1 };
        var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);
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
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(5, op.NewCursor, "cursor should land at end of 'hello'");
    Eq(5, op.NewMark,   "mark should collapse to cursor");
});

Run("WordScan backward jumps to the start of the current word", () =>
{
    // "hello world" — Ctrl+Left from offset 11 should land at offset 6 (start of "world")
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 11, Mark = 11 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(6, op.NewCursor, "cursor should land at start of 'world'");
    Eq(6, op.NewMark,   "mark should collapse to cursor");
});

// ── An emoji of several code points ───────────────────────────────────────────
//
// 👨‍👩‍👧 is five code points — man, ZWJ, woman, ZWJ, girl — and 8 UTF-16 chars,
// drawn as one character. An arrow key or Backspace crosses it whole, and
// Ctrl+Arrow treats it as a word like any other run of non-whitespace.
// Offsets are in chars, not code points: in "go 👨‍👩‍👧 now" the family is
// chars [3..11), though only code points [3..8).

const string Family = "👨‍👩‍👧";   // [0..8)

Run("ArrowRight over a multi-code-point emoji crosses all 8 chars of it", () =>
{
    // "go 👨‍👩‍👧 now" — the family is [3..11)
    var str   = BibuiStr.FromStrLit($"go {Family} now");
    var state = new TextEditState { Cursor = 3, Mark = 3 };
    var ev = new BibUiEvent { Delta = +1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(11, op.NewCursor, "cursor should land just past the family");
    Eq(11, op.NewMark,   "mark should collapse to cursor");
});

Run("ArrowLeft over a multi-code-point emoji crosses all 8 chars of it", () =>
{
    var str   = BibuiStr.FromStrLit($"go {Family} now");
    var state = new TextEditState { Cursor = 11, Mark = 11 };
    var ev = new BibUiEvent { Delta = -1 };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(3, op.NewCursor, "cursor should land just before the family");
    Eq(3, op.NewMark,   "mark should collapse to cursor");
});

Run("Backspace after a multi-code-point emoji deletes all 8 chars of it", () =>
{
    var str   = BibuiStr.FromStrLit($"go {Family} now");
    var state = new TextEditState { Cursor = 11, Mark = 11 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);

    Eq((3, 11), op.Range,   "the op should cover the whole family");
    Eq("go  now", str.ToString(), "the family should be gone, and nothing else");
    Eq(3, op.NewCursor,     "cursor should land where the family began");
    Eq(3, op.NewMark,       "mark should collapse to cursor");
});

Run("WordScan forward over a multi-code-point emoji lands past it", () =>
{
    // From the end of "go": skip the space, then the family as one word
    var str   = BibuiStr.FromStrLit($"go {Family} now");
    var state = new TextEditState { Cursor = 2, Mark = 2 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(11, op.NewCursor, "cursor should land just past the family");
    Eq(11, op.NewMark,   "mark should collapse to cursor");
});

Run("WordScan backward over a multi-code-point emoji lands before it", () =>
{
    // From the start of "now": skip the space, then the family as one word
    var str   = BibuiStr.FromStrLit($"go {Family} now");
    var state = new TextEditState { Cursor = 12, Mark = 12 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(3, op.NewCursor, "cursor should land just before the family");
    Eq(3, op.NewMark,   "mark should collapse to cursor");
});

Run("WordScan treats letters and a multi-code-point emoji with no space between as one word", () =>
{
    // "hi👨‍👩‍👧 there" — "hi" and the family are the word [0..10)
    var str   = BibuiStr.FromStrLit($"hi{Family} there");
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.WordScan };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq(10, op.NewCursor, "cursor should land just past the family");
});

// ── Copy / Cut / Paste ────────────────────────────────────────────────────────

Run("Copy on a selection fills CopyString and preserves the selection", () =>
{
    // "hello world", selection over "world" (cursor=11, mark=6). A Copy event
    // sets KeepMark, so the selection survives being copied.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 11, Mark = 6 };
    var ev = new BibUiEvent { Flags = TextActionFlags.Copy | TextActionFlags.KeepMark };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);

    Eq("world", Encoding.UTF8.GetString(op.CopyString), "CopyString should contain the selection bytes");
    Eq(11, op.NewCursor, "cursor should be preserved by Copy (KeepMark)");
    Eq(6,  op.NewMark,   "mark should be preserved by Copy");
    Eq("hello world", Encoding.UTF8.GetString(str.Str), "buffer should be unchanged by Copy");
});

Run("Submit puts a line break at the cursor and lets go of the selection", () =>
{
    // "hello world", "o w" selected with the cursor at its end.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 7, Mark = 4 };
    var ev = new BibUiEvent { Kind = BibUiEventKind.Submit, Key = Bibui.Key.Enter };
    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);
    Eq("hello w\norld", Encoding.UTF8.GetString(str.Str), "the selected text stays");
    Eq(8, op.NewCursor, "after the line break"); Eq(8, op.NewMark, "no selection");
});

Run("A move between lines changes nothing, not even the selection", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 7, Mark = 4 };
    var op = TextOp.FromStateAndEvent(str, state, new BibUiEvent { Kind = BibUiEventKind.Navigate, DeltaY = -1 }, charBuf);
    Eq(op.Range.Item1, op.Range.Item2, "nothing replaced");
    Eq(7, op.NewCursor); Eq(4, op.NewMark);
});

Run("Ctrl+Backspace deletes back to the start of the word", () =>
{
    var ev = Bibui.Runtime.BibuiWindow.BibUiEventFromKey(Bibui.Key.Backspace, ctrl: true, shift: false);
    ApplyAndCheck(BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world")),
        new TextEditState { Cursor = 11, Mark = 11 }, ev, "hello ", 6, 6);
    // From inside a word, only its part before the cursor.
    ApplyAndCheck(BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world")),
        new TextEditState { Cursor = 9, Mark = 9 }, ev, "hello ld", 6, 6);
    // After a space, the space and the word before it, as ctrl+left would move.
    ApplyAndCheck(BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world")),
        new TextEditState { Cursor = 6, Mark = 6 }, ev, "world", 0, 0);
});

Run("Ctrl+Delete deletes on to the end of the word", () =>
{
    var ev = Bibui.Runtime.BibuiWindow.BibUiEventFromKey(Bibui.Key.Delete, ctrl: true, shift: false);
    ApplyAndCheck(BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world")),
        new TextEditState { Cursor = 5, Mark = 5 }, ev, "hello", 5, 5);
});

Run("Ctrl+Backspace with a selection deletes only the selection", () =>
{
    var ev = Bibui.Runtime.BibuiWindow.BibUiEventFromKey(Bibui.Key.Backspace, ctrl: true, shift: false);
    ApplyAndCheck(BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world")),
        new TextEditState { Cursor = 8, Mark = 6 }, ev, "hello rld", 6, 6);
});

Run("Ctrl+C is a Copy, which leaves the text alone; Ctrl+X is an Edit", () =>
{
    var copy = Bibui.Runtime.BibuiWindow.BibUiEventFromKey(Bibui.Key.C, ctrl: true, shift: false);
    Eq(BibUiEventKind.Copy, copy.Kind);
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var op = TextOp.FromStateAndEvent(str, new TextEditState { Cursor = 11, Mark = 6 }, copy, charBuf);
    Eq("world", Encoding.UTF8.GetString(op.CopyString));
    Eq(op.Range.Item1, op.Range.Item2, "nothing replaced");
    Eq(11, op.NewCursor); Eq(6, op.NewMark);

    var cut = Bibui.Runtime.BibuiWindow.BibUiEventFromKey(Bibui.Key.X, ctrl: true, shift: false);
    Eq(BibUiEventKind.Edit, cut.Kind, "cut deletes, so it is an edit");
    ApplyAndCheck(BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world")),
        new TextEditState { Cursor = 11, Mark = 6 }, cut, "hello ", 6, 6);
});

Run("Paste inserts ev.PasteData at the cursor", () =>
{
    // No selection — paste lands at the cursor.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent
    {
        Flags     = TextActionFlags.Paste,
        PasteData = Encoding.UTF8.GetBytes(" THERE"),
    };

    ApplyAndCheck(str, state, ev, "hello THERE world", 11, 11);
});

Run("Paste over a selection replaces it with ev.PasteData", () =>
{
    // "hello world" with "world" selected (cursor=11, mark=6) — pasting "there" replaces.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 11, Mark = 6 };
    var ev = new BibUiEvent
    {
        Flags     = TextActionFlags.Paste,
        PasteData = Encoding.UTF8.GetBytes("there"),
    };

    ApplyAndCheck(str, state, ev, "hello there", 11, 11);
});

Run("Cut (Copy | Delete) fills CopyString AND removes the selection", () =>
{
    // Same selection. Cut = Copy + Delete; CopyString fills first, Delete then
    // clears the range and collapses the cursor.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 11, Mark = 6 };
    var ev = new BibUiEvent { Flags = TextActionFlags.Copy | TextActionFlags.Delete };

    var op = TextOp.FromStateAndEvent(str, state, ev, charBuf);
    str.ReplaceRange(op.Range, op.ReplaceString);
    state.Cursor = op.NewCursor;
    state.Mark   = op.NewMark;

    Eq("world",  Encoding.UTF8.GetString(op.CopyString),    "CopyString should hold what we cut");
    Eq("hello ", Encoding.UTF8.GetString(str.Str),          "buffer should have the selection removed");
    Eq(6, state.Cursor, "cursor should collapse to min");
    Eq(6, state.Mark,   "mark should collapse to min");
});

// ── Apply operation to buffer (splice via ReplaceRange) ───────────────────────
//
// These exercise the full pipeline: build op, run ReplaceRange, commit state.
// Buffer contents are what end up on screen, so this is the contract that
// actually matters for the editor working.

Run("Backspace at end of ASCII deletes last byte", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "hell", 4, 4);
});

Run("Backspace in middle of ASCII deletes one byte", () =>
{
    // "hello world", cursor at offset 6 (after the space, before 'w') — backspace removes the space
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 6, Mark = 6 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "helloworld", 5, 5);
});

Run("Backspace at start of buffer is a no-op", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "hello", 0, 0);
});

Run("Backspace over a 4-byte emoji removes the whole codepoint", () =>
{
    // "a🎉b", cursor just past the emoji (offset 5)
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("a🎉b"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "ab", 1, 1);
});

Run("Delete (forward) at start removes first byte", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "ello", 0, 0);
});

Run("Delete in middle of ASCII", () =>
{
    // "hello world", cursor between 'o' and ' ' (offset 5) — should remove the space
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "helloworld", 5, 5);
});

Run("Delete at end of buffer is a no-op", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "hello", 5, 5);
});

Run("Type ASCII codepoint at end of buffer", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { CodePoint = '!' };

    ApplyAndCheck(str, state, ev, "hello!", 6, 6);
});

Run("Type ASCII codepoint in middle of buffer", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };
    var ev = new BibUiEvent { CodePoint = ',' };

    ApplyAndCheck(str, state, ev, "hello, world", 6, 6);
});

Run("Type ASCII codepoint at start of buffer", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("ello"));
    var state = new TextEditState { Cursor = 0, Mark = 0 };
    var ev = new BibUiEvent { CodePoint = 'h' };

    ApplyAndCheck(str, state, ev, "hello", 1, 1);
});

Run("Type a 4-byte emoji codepoint writes 4 UTF-8 bytes", () =>
{
    // 🎉 = U+1F389, encodes to 4 UTF-8 bytes
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("ab"));
    var state = new TextEditState { Cursor = 1, Mark = 1 };
    var ev = new BibUiEvent { CodePoint = 0x1F389 };

    ApplyAndCheck(str, state, ev, "a🎉b", 5, 5);
});

Run("Typing into a selection (cursor < mark) replaces it", () =>
{
    // "hello world", selection covers "llo " (offsets 2..6)
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 2, Mark = 6 };
    var ev = new BibUiEvent { CodePoint = 'X' };

    ApplyAndCheck(str, state, ev, "heXworld", 3, 3);
});

Run("Typing 'z' over the 'hello' selection in 'hello world' yields 'z world'", () =>
{
    // Selection made by dragging left-to-right: mark anchors at 0, cursor ends at 5.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 5, Mark = 0 };
    var ev = new BibUiEvent { CodePoint = 'z' };

    ApplyAndCheck(str, state, ev, "z world", 1, 1);
});

Run("Typing into a selection (cursor > mark) replaces it", () =>
{
    // Same range, but cursor is on the right end. Mirrors the user dragging right-to-left
    // vs left-to-right; the splice should not care about direction.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 6, Mark = 2 };
    var ev = new BibUiEvent { CodePoint = 'X' };

    ApplyAndCheck(str, state, ev, "heXworld", 3, 3);
});

Run("Backspace with a selection (cursor < mark) deletes the selection", () =>
{
    // "hello world", selection covers "llo " (offsets 2..6). Backspace should delete
    // exactly the selection, NOT also one extra char before it.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 2, Mark = 6 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "heworld", 2, 2);
});

Run("Backspace with a selection (cursor > mark) deletes the selection", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 6, Mark = 2 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "heworld", 2, 2);
});

Run("Delete (forward) with a selection deletes the selection", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 2, Mark = 6 };
    var ev = new BibUiEvent { Delta = +1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "heworld", 2, 2);
});

Run("Sequential typing builds up the buffer", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes(""));
    var state = new TextEditState { Cursor = 0, Mark = 0 };

    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = 'a' }, "a",   1, 1);
    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = 'b' }, "ab",  2, 2);
    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = 'c' }, "abc", 3, 3);
});

Run("Type then backspace returns to original", () =>
{
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello"));
    var state = new TextEditState { Cursor = 5, Mark = 5 };

    ApplyAndCheck(str, state, new BibUiEvent { CodePoint = '!' }, "hello!", 6, 6);
    ApplyAndCheck(str, state,
        new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection },
        "hello", 5, 5);
});

Run("Deleting a large selection triggers shrink without losing content", () =>
{
    // "hello world" → Length=11, Capacity=12. Deleting a left-to-right selection of
    // 8 bytes leaves newLen=3, which fires the shrink branch (3 <= 12>>2 = 3).
    // AlignedResize(3) → Capacity=4, but Length is still 11 at the time the splice
    // reads Str[range.Item2..] → AsSpan(0, 11) on a 4-byte array throws.
    var str   = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes("hello world"));
    var state = new TextEditState { Cursor = 0, Mark = 8 };
    var ev = new BibUiEvent { Delta = -1, Flags = TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection };

    ApplyAndCheck(str, state, ev, "rld", 0, 0);
});

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
if (failed > 0) Environment.Exit(1);
