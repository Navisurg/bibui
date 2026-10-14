namespace Bibui.TextEdit;

public ref struct TextOp
{
    public System.ValueTuple<int, int> Range = (0, 0);
    public ReadOnlySpan<char> ReplaceString = default;
    public ReadOnlySpan<char> CopyString = default;
    public int NewCursor = 0;  // Cursor after applying op
    public int NewMark = 0;  // Mark after op

    public static int MaxCharsPerCodePoint = 2; // a surrogate pair

    public TextOp() { }

    public static int ScanCodePoint(ReadOnlySpan<char> str, int cursor, int delta)
    {
        int deltaChars;
        System.Buffers.OperationStatus result;
        if (delta < 0)
        {
            result = System.Text.Rune.DecodeLastFromUtf16(str[..cursor], out _, out deltaChars);
        }
        else
        {
            result = System.Text.Rune.DecodeFromUtf16(str[cursor..], out _, out deltaChars);
        }

        // TODO(k): What should we do with NeedMoreData - for now we just ignore it
        if (result == System.Buffers.OperationStatus.InvalidData)
        {
            throw new NotImplementedException("Can't handle these strings");
        }
        return deltaChars * delta; // Convert to negative or positive delta
    }

    /// <summary>
    /// Scan to the next word boundary in the direction of <paramref name="delta"/>
    /// (sign only; magnitude ignored). Returns the char offset from <paramref name="cursor"/>
    /// to the boundary — negative going left, positive going right, 0 at the edge.
    ///
    /// A "word" is a run of non-whitespace chars (punctuation included). Going forward
    /// from inside whitespace skips the whitespace and then the next word; going forward
    /// from inside a word skips the rest of the word — the usual Ctrl+Arrow behaviour.
    /// </summary>
    public static int WordScan(ReadOnlySpan<char> str, int cursor, int delta)
    {
        if (delta == 0 || str.Length == 0) return 0;

        if (delta > 0)
        {
            int i = cursor;
            while (i < str.Length && IsSpace(str[i])) i++;       // skip leading whitespace
            while (i < str.Length && !IsSpace(str[i])) i++;      // skip the word
            return i - cursor;
        }
        else
        {
            int i = cursor;
            while (i > 0 && IsSpace(str[i - 1])) i--;            // skip trailing whitespace
            while (i > 0 && !IsSpace(str[i - 1])) i--;           // skip the word
            return i - cursor;                                    // negative
        }
    }

    /// <summary>
    /// Scan to the end of the line in the direction of <paramref name="delta"/>:
    /// backwards to just after the preceding line break, forwards to just before
    /// the next one — so in a single-line buffer, to its start or its end.
    /// Returns an offset from the cursor, like the other scans.
    /// </summary>
    internal static int LineScan(ReadOnlySpan<char> str, int cursor, int delta)
    {
        if (delta == 0) return 0;

        if (delta > 0)
        {
            int i = cursor;
            while (i < str.Length && str[i] != '\n') i++;
            return i - cursor;
        }
        else
        {
            int i = cursor;
            while (i > 0 && str[i - 1] != '\n') i--;
            return i - cursor;                                    // negative
        }
    }

    private static bool IsSpace(char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r';

    /// <summary>
    /// What <paramref name="ev"/> does to <paramref name="str"/> with its
    /// caret and selection at <paramref name="state"/>. A
    /// <see cref="BibUiEventKind.Submit"/> ends the line: it puts a \n at the
    /// cursor and lets go of the selection; the selected text stays. A text
    /// that has one line does not pass it Submit.
    /// </summary>
    public static TextOp FromStateAndEvent(BibuiStr str,
                                           TextEditState state,
                                           BibUiEvent ev,
                                           Span<char> buf)
    {
        TextOp op = new();
        op.NewMark = state.Mark;

        // Submit — done with this line: a line break at the cursor, and no
        // selection after it. The selected text is kept: ending a line is not
        // a reason to lose it.
        if (ev.Kind == BibUiEventKind.Submit)
        {
            op.Range         = (state.Cursor, state.Cursor);
            op.ReplaceString = "\n";
            op.NewCursor     = state.Cursor + 1;
            op.NewMark       = op.NewCursor;
            return op;
        }

        // A move between lines: only a text with lines can say where that goes,
        // so here it changes nothing — not even the selection.
        if (ev.DeltaY != 0)
        {
            op.NewCursor = state.Cursor;
            return op;
        }

        int charsDelta =
              ev.Flags.HasFlag(TextActionFlags.WordScan) ? WordScan(str.Str, state.Cursor, ev.Delta)
            : ev.Flags.HasFlag(TextActionFlags.LineScan) ? LineScan(str.Str, state.Cursor, ev.Delta)
            : ScanCodePoint(str.Str, state.Cursor, ev.Delta);

        // On this type of select we don't move the cursor since this
        // would for delete delete the point also before the cursor
        // and not just the selection
        if (state.Cursor != state.Mark
            && ev.Flags.HasFlag(TextActionFlags.NoNavigationWithSelection))
        {
            charsDelta = 0;
        }

        // Apply the delta
        op.NewCursor = state.Cursor + charsDelta;

        // For selections we would keep mark, but otherwise
        // we set the mark to be the new cursor position.
        if ((ev.Flags & TextActionFlags.KeepMark) == 0)
        {
            op.NewMark = op.NewCursor;
        }

        // select-all — both ends at once, so it is stated rather than scanned.
        if ((ev.Flags & TextActionFlags.SelectAll) != 0)
        {
            op.NewMark = 0;
            op.NewCursor = (int)str.Length;
        }

        // copy — fill CopyString from the original selection. Does not mutate
        // the buffer or cursor/mark. Cursor/mark are preserved because the
        // Copy event sets KeepMark (see BibuiWindow). Placed BEFORE Delete so
        // Cut (Copy|Delete) still captures the selection before it's cleared.
        if ((ev.Flags & TextActionFlags.Copy) != 0)
        {
            int min = Math.Min(state.Cursor, state.Mark);
            int max = Math.Max(state.Cursor, state.Mark);
            op.CopyString = str.Str[min..max];
        }

        // delete
        if ((ev.Flags & TextActionFlags.Delete) != 0)
        {
            var min = Math.Min(op.NewCursor, state.Mark);
            var max = Math.Max(op.NewCursor, state.Mark);
            op.Range = (min, max);
            op.NewCursor = min;
            op.NewMark = min;
        }

        if (ev.CodePoint != 0)
        {
            var rune = new System.Text.Rune(ev.CodePoint);
            int written = rune.EncodeToUtf16(buf);
            op.ReplaceString = buf[..written];

            // Only matters if we replace during a selection
            int min = Math.Min(state.Cursor, state.Mark);
            int max = Math.Max(state.Cursor, state.Mark);
            op.Range = (min, max);
            // After inserting the codepoint, regardlessof whether
            // there was a selection we move the cursor to the end
            // of the written text
            op.NewCursor = min + written;
            op.NewMark = op.NewCursor;
        }

        // paste — splice ev.PasteData over the (optional) selection. Same shape
        // as a CodePoint insert; the clipboard was read at the WM boundary.
        if ((ev.Flags & TextActionFlags.Paste) != 0 && ev.PasteData is { Length: > 0 } pasted)
        {
            int min = Math.Min(state.Cursor, state.Mark);
            int max = Math.Max(state.Cursor, state.Mark);
            op.Range         = (min, max);
            op.ReplaceString = pasted;
            op.NewCursor     = min + pasted.Length;
            op.NewMark       = op.NewCursor;
        }

        return op;
    }
}

public enum TextActionFlags : UInt32
{
    KeepMark = 1 << 0, // Advance cursor but keep mark (shift modifier)
    Delete   = 1 << 1,
    Copy     = 1 << 2,
    Paste    = 1 << 3,
    NoNavigationWithSelection = 1 << 4,
    WordScan = 1 << 5,
    LineScan = 1 << 6,

    // Selects everything. The only action whose result does not depend on where
    // the caret is, which is why it cannot be said with a delta.
    SelectAll = 1 << 7,
}



public sealed class TextEditState
{
    public int Cursor = 0;
    public int Mark = 0;    
}
