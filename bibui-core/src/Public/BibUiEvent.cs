
namespace Bibui.Core.TextEdit;

/// <summary>
/// What an event is. Consumers filter on this before inspecting fields, so a raw
/// keydown of 'Z' is not mistaken for a text-insertion intent.
/// </summary>
public enum BibUiEventKind
{
    Null,       // unset / sentinel — also the default for events not yet tagged
    Press,      // any other key down. A shortcut filters on BibUiEvent.IsKeyDown, not on this
    Text,       // typed character (layout-resolved, IME-composed); CodePoint is set
    Navigate,   // cursor movement intent (arrows / home / end); Delta is set
    Edit,       // mutating intent (delete / backspace / cut / paste); Flags carry the action

    // Mouse. These carry X/Y of their own rather than reading the pointer,
    // because the pointer is one value for the whole frame: a press made while
    // the mouse is travelling would otherwise hit-test wherever it finished.
    MouseDown,  // Button and X/Y are set
    MouseUp,    // Button and X/Y are set
    MouseMove,  // X/Y are set. At most one per frame — see BibuiRuntime.BeginBuild
    Release,    // key up. Key is set; the only consumer is key repeat
    Scroll,     // X/Y plus ScrollX/ScrollY

    // Enter: the user is done with what they are entering. What that means is
    // the consumer's — a form sends, a dialog confirms, and to TextOp it ends
    // the line. Key is Enter. Last, so no kind before it changes number.
    Submit,

    // Copy the selection: it changes nothing, so a text that may not be edited
    // can still take it. Flags carry Copy and KeepMark. Cut, which also
    // deletes, is an Edit.
    Copy,
}

/// <summary>
/// One input event carrying both the raw OS info (Key, modifiers, Char, mouse
/// button and position) and the already-translated editing intent (Flags, Delta,
/// CodePoint), so that no consumer translates a second time: global handlers read
/// Key/Ctrl/Shift, text widgets read the intent via TextOp.FromStateAndEvent.
///
/// Mutable, so the translation at the WM boundary builds one up in a single
/// accumulating pass.
///
/// <para>An event the runtime queues lives until the frame's <c>EndBuild</c>: the
/// runtime takes it from a pool that every frame end empties at once, and hands it
/// out again the next frame. Read what you need while building; do not keep it.</para>
/// </summary>
public sealed class BibUiEvent
{
    public BibUiEventKind Kind { get; set; } = BibUiEventKind.Null;

    // ── The frame's list ─────────────────────────────────────────────────────
    // rt: the runtime's. Linked as events are queued, unlinked all at once at
    // the end of the frame.

    /// <summary>The next event this frame, in the order they happened; null after the last.</summary>
    public BibUiEvent? Next { get; internal set; }

    /// <summary>Whether something has taken it (<see cref="Eat"/>). <c>UnconsumedEvents</c> skips it from then on.</summary>
    public bool Consumed { get; private set; }

    /// <summary>Takes the event: whoever asks after this does not see it.</summary>
    public void Eat() => Consumed = true;

    // ── Raw OS info ──────────────────────────────────────────────────────────
    public Key Key { get; set; } = Key.Unknown;

    /// <summary>
    /// A key went down, whatever kind the text fields' translation gave it:
    /// <see cref="BibUiEventKind.Press"/>, <see cref="BibUiEventKind.Navigate"/>
    /// (arrows, Home, End, Ctrl+A), <see cref="BibUiEventKind.Submit"/> (Enter),
    /// <see cref="BibUiEventKind.Edit"/> (Backspace, Delete, Ctrl+X, Ctrl+V) or
    /// <see cref="BibUiEventKind.Copy"/> (Ctrl+C). What an app's shortcut handler
    /// filters on before it reads <see cref="Key"/>: filtering on Press alone never
    /// sees ↑, ↓ or Enter.
    ///
    /// <para>A printable key arrives twice: this key-down, and a
    /// <see cref="BibUiEventKind.Text"/> with the character it typed, which is not a
    /// key-down. So a key that gives a field the keyboard — <c>/</c> to search —
    /// must also eat its Text, or the field, focused by the time the Text is read,
    /// types the <c>/</c>. Both are in the same frame's
    /// <see cref="Bibui.Core.Runtime.BibuiRuntime.UnconsumedEvents"/>, the key-down first:
    /// remember that the key focused the field, and eat the Text that follows it.</para>
    /// </summary>
    public bool IsKeyDown => Kind is BibUiEventKind.Press or BibUiEventKind.Navigate
        or BibUiEventKind.Submit or BibUiEventKind.Edit or BibUiEventKind.Copy;

    /// <summary>
    /// What was held when this happened, stamped where the event was made.
    ///
    /// On the event rather than asked for afterwards, because "afterwards" is a
    /// different moment: a modifier released between the press and the frame
    /// that reads it would be gone, and ctrl-click would become click. The whole
    /// set rather than two bools, so Alt and Super are expressible at all.
    /// </summary>
    public KeyModifiers Modifiers { get; set; }

    public bool Ctrl
    {
        get => (Modifiers & KeyModifiers.Ctrl) != 0;
        set => Modifiers = value ? Modifiers | KeyModifiers.Ctrl : Modifiers & ~KeyModifiers.Ctrl;
    }

    public bool Shift
    {
        get => (Modifiers & KeyModifiers.Shift) != 0;
        set => Modifiers = value ? Modifiers | KeyModifiers.Shift : Modifiers & ~KeyModifiers.Shift;
    }

    public char? Char { get; set; }

    /// <summary>
    /// A key down the keyboard repeated because the key is held, not a new press
    /// (<see cref="Bibui.Core.Runtime.Platform.WindowEvent.Repeat"/>). A text field takes
    /// repeats like presses, so a held Backspace keeps deleting; a shortcut that
    /// toggles something should skip them, and one that moves should not.
    /// </summary>
    public bool Repeat { get; set; }

    // ── Mouse ────────────────────────────────────────────────────────────────
    // Where a press or a wheel notch happened, in logical pixels. Set for the
    // mouse kinds only; a key event has no position and leaves them at zero.
    public MouseButton Button { get; set; } = MouseButton.Unknown;
    public float X { get; set; }
    public float Y { get; set; }
    public float ScrollX { get; set; }
    public float ScrollY { get; set; }

    /// <summary>
    /// 1 for a single press, 2 for a double, 3 for a triple. Counted at the raw
    /// stream, where the clock is: the two presses land in different frames, so
    /// nothing downstream could work it out from one batch. On the event rather
    /// than derived, so a synthesised press can just say it is a double-click.
    /// </summary>
    public int ClickCount { get; set; } = 1;

    // ── Translated text-editing intent (filled by BeginBuild) ───────────────
    public TextActionFlags Flags { get; set; }
    public int Delta { get; set; }

    /// <summary>
    /// For a <see cref="BibUiEventKind.Navigate"/>: how many lines to move, up
    /// when negative. Only a text with lines can say what that means, so
    /// <see cref="TextOp"/> leaves a vertical move alone.
    /// </summary>
    public int DeltaY { get; set; }
    public uint CodePoint { get; set; }

    /// <summary>
    /// The text carried by a Paste event. The clipboard is read at the WM
    /// boundary and stashed here, so TextOp.FromStateAndEvent does no I/O.
    /// </summary>
    public string? PasteData { get; set; }

    /// <summary>Back to what <c>new()</c> gives, for the pool to hand out again.</summary>
    internal void Reset()
    {
        Kind = BibUiEventKind.Null; Next = null; Consumed = false;
        Key = Key.Unknown; Modifiers = KeyModifiers.None; Char = null; Repeat = false;
        Button = MouseButton.Unknown; X = Y = ScrollX = ScrollY = 0f; ClickCount = 1;
        Flags = 0; Delta = DeltaY = 0; CodePoint = 0; PasteData = null;
    }

    /// <summary>Everything but the list link and the consumed flag, from <paramref name="from"/>.</summary>
    internal void CopyFrom(BibUiEvent from)
    {
        Kind = from.Kind;
        Key = from.Key; Modifiers = from.Modifiers; Char = from.Char; Repeat = from.Repeat;
        Button = from.Button; X = from.X; Y = from.Y; ScrollX = from.ScrollX; ScrollY = from.ScrollY;
        ClickCount = from.ClickCount;
        Flags = from.Flags; Delta = from.Delta; DeltaY = from.DeltaY; CodePoint = from.CodePoint; PasteData = from.PasteData;
    }

    // ── Actions a keystroke and a menu item both ask for ─────────────────────
    // They accumulate onto the event rather than returning a fresh one, so the
    // key path keeps the Key/Ctrl/Shift it has already stamped on.

    /// <summary>
    /// Select everything. The only text action whose result does not depend on
    /// where the caret is, which is why it carries no delta.
    /// </summary>
    public void SelectAll()
    {
        Kind = BibUiEventKind.Navigate;
        Flags |= TextActionFlags.SelectAll;
    }

    /// <summary>
    /// Copy the selection. KeepMark stops the trailing collapse-mark step from
    /// running, so the selection being copied survives being copied.
    /// </summary>
    public void Copy()
    {
        Kind = BibUiEventKind.Copy;
        Flags |= TextActionFlags.Copy | TextActionFlags.KeepMark;
    }

    /// <summary>
    /// Splice clipboard text in at the caret; the caller reads the clipboard,
    /// because that is I/O. A single-line field cannot hold a line break, so a
    /// pasted one is collapsed to a space here rather than in the splice, which
    /// has no idea what shape of field the text goes into.
    /// </summary>
    public void Paste(string? clipboardText)
    {
        Kind = BibUiEventKind.Edit;
        Flags |= TextActionFlags.Paste;
        if (string.IsNullOrEmpty(clipboardText)) return;

        var oneLine = clipboardText.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        PasteData = oneLine;
    }

    /// <summary>
    /// Translates a key press into a text-edit intent, with the modifier state supplied rather
    /// than read from a keyboard. Every caller has it to hand already — the window pump captures
    /// it with the event, and a test port (<c>Bibui.Automation</c>) synthesises presses over a socket
    /// with no keyboard at all — so both produce identical events. Duplicating this switch is
    /// how the two would drift. <paramref name="clipboard"/> is read on Ctrl+V; null means
    /// there is no clipboard, and the paste is empty.
    /// </summary>
    public static BibUiEvent FromKey(Key key, bool ctrl, bool shift, Func<string?>? clipboard = null)
    {
        var ev = new BibUiEvent();
        FillFromKey(ev, key, ctrl, shift, clipboard);
        return ev;
    }

    // FromKey into an event the caller has: the runtime's, from its pool.
    internal static void FillFromKey(BibUiEvent ev, Key key, bool ctrl, bool shift, Func<string?>? clipboard)
    {
        ev.Key   = key;
        ev.Ctrl  = ctrl;
        ev.Shift = shift;

        // Handle modifiers
        if (ev.Shift)
        {
            ev.Flags |= TextActionFlags.KeepMark;
        }

        // All other inputs. Kind is assigned per-case so consumers can filter
        // (text widgets only care about Text/Navigate/Edit; global handlers
        // get Press for everything else).
        switch (key)
        {
            case Key.Left:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.Delta  = -1;
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.Right:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.Delta  = 1;
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.Backspace:
                ev.Kind   = BibUiEventKind.Edit;
                ev.Flags |= TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection;
                ev.Delta  = -1;
                // With ctrl, back to the start of the word, as ctrl+left moves.
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.Delete:
                ev.Kind   = BibUiEventKind.Edit;
                ev.Flags |= TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection;
                ev.Delta  = 1;
                // With ctrl, on to the end of the word, as ctrl+right moves.
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.C when ev.Ctrl:
                ev.Copy();
                break;
            case Key.Home:
                ev.Kind   = BibUiEventKind.Navigate;
                // Backwards: LineScan takes its direction from the delta, the
                // same way WordScan does.
                ev.Delta  = -1;
                ev.Flags |= TextActionFlags.LineScan;
                break;
            case Key.End:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.Delta  = 1;
                ev.Flags |= TextActionFlags.LineScan;
                break;
            case Key.A when ev.Ctrl:
                ev.SelectAll();
                break;
            case Key.Enter:
                ev.Kind = BibUiEventKind.Submit;
                break;
            case Key.Up:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.DeltaY = -1;
                break;
            case Key.Down:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.DeltaY = 1;
                break;
            case Key.X when ev.Ctrl:
                // Cut = Copy + Delete; the Copy block fills CopyString first,
                // then Delete clears the range and collapses the cursor. Said as
                // a copy that also deletes, because that is what it is.
                ev.Copy();
                ev.Flags |= TextActionFlags.Delete;
                ev.Kind = BibUiEventKind.Edit;   // it deletes, so it is an edit
                break;
            case Key.V when ev.Ctrl:
                // Read the clipboard at the WM boundary so the event stays
                // I/O-free (and so the splice is testable without touching the
                // system clipboard).
                // No clipboard (null) pastes nothing: the host's clipboard
                // arrives through the argument, as the runtime's ClipboardGet.
                ev.Paste(clipboard?.Invoke());
                break;
            default:
                ev.Kind = BibUiEventKind.Press;
                break;
        }
    }
}
