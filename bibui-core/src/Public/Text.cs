
namespace Bibui.Core.TextEdit;

public ref struct TextOp
{
    public System.ValueTuple<int, int> Range = (0, 0);  // The chars [start, end) the op replaces
    public ReadOnlySpan<char> ReplaceString = default;
    public ReadOnlySpan<char> CopyString = default;
    public int NewCursor = 0;  // Cursor after applying op, in chars
    public int NewMark = 0;  // Mark after op, in chars

    public static int MaxCharsPerCodePoint = 2; // a surrogate pair

    public TextOp() { }

    // How far back moving left looks for a certain cluster boundary, in chars.
    // Only a cluster longer than this — stacked marks, a long run of flags — can
    // put the cursor inside it. The text layer's (TextLayer.ElementLookBehind).
    public static int MaxLookBehind
    {
        get => TextLayer.ElementLookBehind;
        set => TextLayer.ElementLookBehind = value;
    }

    /// <summary>
    /// What <paramref name="ev"/> does to <paramref name="str"/> with its
    /// caret and selection at <paramref name="state"/>. A
    /// <see cref="BibUiEventKind.Submit"/> ends the line: it puts a \n at the
    /// cursor and lets go of the selection; the selected text stays. A text
    /// that has one line does not pass it Submit.
    ///
    /// <para>A move by one character crosses one cluster as the text layer sets
    /// <paramref name="str"/> in <paramref name="font"/> (TextLayer.ScanCluster):
    /// 👍🏽, 🇩🇰, e and an accent; by a word or to a line's end, TextLayer.ScanWord
    /// and ScanLine.</para>
    ///
    /// <para><paramref name="buf"/> holds a typed code point, encoded: at least
    /// <see cref="MaxCharsPerCodePoint"/> chars.</para>
    /// </summary>
    public static TextOp FromStateAndEvent(Fonts fonts,
                                           in Font font,
                                           BibuiStr str,
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

        // A move by a character or a word goes by the clusters the text is set in.
        var style = TextLayer.StyleFromFont(fonts, font, font.Scale);
        int cursor = state.Cursor;
        int charsDelta =
              ev.Flags.HasFlag(TextActionFlags.WordScan) ? TextLayer.ScanWord(fonts, style, str.Str, cursor, ev.Delta) - cursor
            : ev.Flags.HasFlag(TextActionFlags.LineScan) ? TextLayer.ScanLine(str.Str, cursor, ev.Delta) - cursor
            : TextLayer.ScanCluster(fonts, style, str.Str, cursor, ev.Delta, out _) - cursor;

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
        // Copy event sets KeepMark (see BibUiEvent.FromKey). Placed BEFORE Delete so
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

        // A code point that is no character (half a surrogate pair, past U+10FFFF)
        // inserts nothing: input never throws.
        if (ev.CodePoint != 0 && System.Text.Rune.TryCreate(ev.CodePoint, out var rune))
        {
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



/// <summary>
/// Where the caret and the other end of the selection are in a text.
///
/// <para>Every offset in text editing — Cursor and Mark here, and
/// TextOp's Range, NewCursor and NewMark, and what its scans return —
/// counts UTF-16 code units: chars, the index into a string or a
/// Span&lt;char&gt;. Not code points (👨 is one code point but two chars),
/// and not what reads as one character (👨‍👩‍👧 is one text element but
/// five code points and eight chars). A cursor always sits on a text
/// element boundary.</para>
/// </summary>
public sealed class TextEditState
{
    public int Cursor = 0;  // In chars, on a text element boundary
    public int Mark = 0;    // In chars, on a text element boundary
}
