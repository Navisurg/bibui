using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;

namespace Bibui.Text;

/// <summary>
/// What is done with <see cref="Fonts"/>' data: its faces and styles, a font's
/// metrics, how wide a text is, the runs layout walks a text into, and where a
/// text breaks into lines. Functions over a <see cref="Fonts"/>, named for what
/// they give from what; the data they read and write is all on Fonts, each
/// field saying whose it is. The text layer; the <c>Text</c> node is the
/// runtime's, and only hands its chars and its run handle to these.
/// </summary>
public static class TextLayer
{
    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>Once a frame, with the renderer's own caches: the wraps unasked for too long go back.</summary>
    internal static void FontsEndFrame(Fonts f) => WrapsEvict(f);

    // ── Faces ────────────────────────────────────────────────────────────────

    /// <summary>The built-in Inter at <paramref name="weight"/>, for what draws text no theme set: an icon's fallback letter.</summary>
    internal static FontFace FaceBuiltIn(Fonts f, int weight) => f.Provider.Resolve(null, weight);

    // ── Styles ───────────────────────────────────────────────────────────────

    /// <summary>The style of <paramref name="face"/> at <paramref name="sizePx"/> physical px.</summary>
    internal static FontStyle StyleFromFaceSize(Fonts f, FontFace face, float sizePx)
    {
        ulong key = (ulong)(uint)face.Id << 32 | (uint)BitConverter.SingleToInt32Bits(sizePx);
        if (f.LastStyle is { } last && last.Key == key) return last;
        if (!f.Styles.TryGetValue(key, out var style))
        {
            var strike = face.Strike(sizePx);
            var m = strike.Metrics;
            f.Styles[key] = style = new FontStyle
            {
                Key = key, Face = face, Strike = strike, Metrics = new TextMetrics(m.Ascent, m.Descent, m.Leading),
            };
        }
        return f.LastStyle = style;
    }

    /// <summary>
    /// The style of <paramref name="font"/> measured at <paramref name="scale"/>: a
    /// draw measures at its canvas's scale, layout at the font's own.
    /// </summary>
    internal static FontStyle StyleFromFont(Fonts f, in Font font, float scale)
        => StyleFromFaceSize(f, font.Face ?? FaceBuiltIn(f, 400), font.Size * scale);

    // ── Line metrics ─────────────────────────────────────────────────────────

    /// <summary>
    /// The height of one line, in logical px: the font's ascent to its descent,
    /// with no leading. Lines of a wrapped text are <see cref="LineAdvanceFromSize"/> apart.
    /// </summary>
    internal static float LineBoxFromFont(Fonts f, in Font font)
        => LineHeightOfStyle(StyleFromFont(f, font, font.Scale), font.Scale);

    /// <summary>
    /// The height of one line in <paramref name="style"/>, in logical px: its
    /// ascent to its descent, over <paramref name="scale"/>, for a caller that
    /// has the style already.
    /// </summary>
    internal static float LineHeightOfStyle(FontStyle style, float scale)
        => (style.Metrics.Descent - style.Metrics.Ascent) / scale;

    /// <summary>
    /// Baseline-to-baseline distance between wrapped lines, in logical px — the
    /// leading that <see cref="LineBoxFromFont"/> does not bake into a single line.
    /// </summary>
    internal static float LineAdvanceFromSize(float fontSize) => fontSize * 1.4f;

    /// <summary>
    /// Total height of <paramref name="lines"/> stacked lines: the last line's em box
    /// plus one advance for every line above it. One line is exactly its em box.
    /// </summary>
    internal static float BlockHeightFromLines(Fonts f, int lines, in Font font)
        => LineBoxFromFont(f, font) + MathF.Max(0, lines - 1) * LineAdvanceFromSize(font.Size);

    // ── Widths ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How far past its room a run of text may measure and still be said to fit, in logical px.
    ///
    /// <para>A text's room is almost never the number it was measured at: it is handed back as
    /// its parent's width less the parent's padding, and the parent's width was the text's own
    /// width plus that padding. In floats those two trips do not cancel. Wherever the sum
    /// crosses a power of two the last bit of the width is rounded away, and the room comes
    /// back one ulp short of the text that asked for it — 110.76041 for a label measured at
    /// 110.760414. Compared exactly, a label laid out as one line was then broken as two.</para>
    ///
    /// <para>A hundredth of a logical pixel is orders of magnitude above any rounding the layout
    /// can accumulate and a fortieth of a physical pixel at scale 2.5 below anything that can be
    /// seen. Every fit decision asks <see cref="Fits"/>, so measuring and breaking cannot come
    /// to different answers over the same width.</para>
    /// </summary>
    internal const float FitSlack = 0.01f;

    /// <summary>Whether a run <paramref name="width"/> wide fits in <paramref name="room"/>. See <see cref="FitSlack"/>.</summary>
    internal static bool Fits(float width, float room) => width <= room + FitSlack;

    /// <summary>The width of <paramref name="text"/> on one line in <paramref name="font"/>, in logical px.</summary>
    internal static float WidthFromChars(Fonts f, ReadOnlySpan<char> text, in Font font)
        => text.IsEmpty ? 0f : StyleFromFont(f, font, font.Scale).Strike.Measure(text) / font.Scale;

    /// <summary>
    /// The char offset in <paramref name="text"/> nearest <paramref name="x"/>,
    /// logical px from where the text starts. See <see cref="Fonts.OffsetAt"/>.
    /// </summary>
    internal static int OffsetFromX(Fonts f, ReadOnlySpan<char> text, in Font font, float x)
    {
        if (x <= 0f || text.IsEmpty) return 0;
        int previous = 0;
        float previousWidth = 0f;
        while (previous < text.Length)
        {
            int next = previous + ScanTextElement(text, previous, +1);
            float width = WidthFromChars(f, text[..next], font);
            if (width >= x) return x - previousWidth < width - x ? previous : next;
            previous = next;
            previousWidth = width;
        }
        return text.Length;
    }

    // ── Grapheme clusters ────────────────────────────────────────────────────

    // How far back ScanTextElement looks for a certain boundary, in chars. Only
    // an element longer than this — stacked marks, a long run of flags — can
    // put the cursor inside it. TextOp.MaxLookBehind sets it.
    internal static int ElementLookBehind = 64;

    /// <summary>
    /// Scan one text element — a grapheme cluster, what reads as one character:
    /// 👍🏽, 🇩🇰, e and an accent, \r\n — in the direction of <paramref name="delta"/>
    /// (sign only). Returns the char offset from <paramref name="cursor"/>,
    /// negative going left, 0 at the edge. The cursor is assumed to be on an
    /// element boundary.
    /// </summary>
    internal static int ScanTextElement(ReadOnlySpan<char> str, int cursor, int delta)
    {
        if (delta > 0)
            return cursor < str.Length ? StringInfo.GetNextTextElementLength(str[cursor..]) : 0;
        if (delta == 0 || cursor == 0) return 0;

        // A line break is a boundary on both sides, and \r\n is one element.
        if (str[cursor - 1] == '\n') return cursor >= 2 && str[cursor - 2] == '\r' ? -2 : -1;

        // Whether a position is a boundary can depend on what comes before it (a
        // ZWJ sequence, the pairing of flags), so step back, a code point at a
        // time, to one that is certainly a boundary — within the line and
        // MaxLookBehind — and scan forward from there.
        int floor = Math.Max(LineStart(str, cursor), cursor - ElementLookBehind);
        int start = cursor;
        while (true)
        {
            Rune.DecodeLastFromUtf16(str[..start], out _, out int length);
            start -= Math.Max(1, length);
            if (start <= floor) break;

            // The pair test: a boundary .NET finds between the two code points
            // alone holds whatever came before, unless the first is a mark or a
            // format char — a ZWJ (emoji sequences) and a virama (Indic
            // conjuncts) join by what precedes them, which the pair can't see.
            Rune.DecodeLastFromUtf16(str[..start], out Rune before, out int beforeLength);
            Rune.DecodeFromUtf16(str[start..], out _, out int afterLength);
            if (Rune.GetUnicodeCategory(before) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                && StringInfo.GetNextTextElementLength(str[(start - beforeLength)..(start + afterLength)]) == beforeLength)
                break;
        }

        // The last element boundary before the cursor.
        int boundary = start;
        for (int i = start; i < cursor; i += StringInfo.GetNextTextElementLength(str[i..cursor]))
            boundary = i;
        return boundary - cursor;
    }

    // Where the line the cursor is on starts: just after the line break before it, or 0.
    private static int LineStart(ReadOnlySpan<char> str, int cursor)
    {
        int i = cursor;
        while (i > 0 && str[i - 1] != '\n') i--;
        return i;
    }

    // ── Runs ─────────────────────────────────────────────────────────────────

    /// <summary>Empties the frame's runs for a frame's layout.</summary>
    internal static void RunsBegin(Fonts f)
    {
        f.RunGlyphCount = 0;
        f.RunCount = 0;
        f.RunFrame++;
    }

    /// <summary>
    /// The runs of <paramref name="chars"/> in <paramref name="style"/>, walked
    /// into this frame's table: their glyphs and positions kept for the painter,
    /// their records pushed, and a handle to them returned. Empty text has no
    /// runs: the zero handle. Ask <see cref="RunsAreCurrent"/> first; a text
    /// whose runs are current needs no walk.
    /// </summary>
    internal static RunHandle RunsFromChars(Fonts f, ReadOnlySpan<char> chars, FontStyle style)
        => chars.IsEmpty ? default : RunsWalk(f, chars, style);

    /// <summary>
    /// Whether <paramref name="runs"/> are in this frame's table and were walked
    /// in <paramref name="style"/>: walked this frame, some, the first in the
    /// text's own style. The zero handle never is.
    /// </summary>
    internal static bool RunsAreCurrent(Fonts f, RunHandle runs, FontStyle style)
        => runs.Frame == f.RunFrame && runs.Count > 0 && f.Runs[runs.First].Style == style;

    /// <summary>The records <paramref name="runs"/> names; only for runs <see cref="RunsAreCurrent"/> says are this frame's.</summary>
    internal static ReadOnlySpan<TextRun> RunsOf(Fonts f, RunHandle runs) => f.Runs.AsSpan(runs.First, runs.Count);

    /// <summary>
    /// How wide the text <paramref name="runs"/> are of is, in logical px: its last
    /// run's pen plus that run's width, over <paramref name="scale"/>. 0 for no
    /// runs; only for a handle that is this frame's or the zero handle.
    /// </summary>
    internal static float WidthOfRuns(Fonts f, RunHandle runs, float scale)
    {
        if (runs.Count == 0) return 0f;
        ref readonly var last = ref f.Runs[runs.First + runs.Count - 1];
        return (last.X + last.Width) / scale;
    }

    // RunsFromChars' walk, a function of its own so the check before it
    // (RunsAreCurrent) and the ask around it stay small enough to inline.
    // Pushes the text's glyphs, then its runs, and returns their handle.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static RunHandle RunsWalk(Fonts f, ReadOnlySpan<char> chars, FontStyle style)
    {
        if (f.RunGlyphCount + chars.Length > f.RunGlyphs.Length)
        {
            int size = Math.Max(f.RunGlyphs.Length * 2, f.RunGlyphCount + chars.Length);
            Array.Resize(ref f.RunGlyphs, size);
            Array.Resize(ref f.RunPoints, size);
        }
        if (f.RunCount == f.Runs.Length) Array.Resize(ref f.Runs, f.Runs.Length * 2);

        int start = f.RunGlyphCount;
        int count = style.Strike.Glyphs(chars, f.RunGlyphs.AsSpan(start, chars.Length),
            f.RunPoints.AsSpan(start, chars.Length), out float width);
        f.RunGlyphCount += count;

        // One run, in the text's own style, from the text's start.
        f.Runs[f.RunCount] = new TextRun { Style = style, GlyphStart = start, GlyphCount = count, X = 0f, Width = width };
        var runs = new RunHandle(f.RunCount, 1, f.RunFrame);
        f.RunCount += 1;
        return runs;
    }

    // ── Wraps ────────────────────────────────────────────────────────────────

    /// <summary>The lines <paramref name="text"/> breaks into at <paramref name="width"/>. See <see cref="Fonts.Wrap"/>.</summary>
    internal static TextLine? LinesFromChars(Fonts f, ReadOnlySpan<char> text, in Font font, float width, TextWrap wrap)
    {
        if (text.IsEmpty) return null;
        // The same text is wrapped several times in one frame — to measure the
        // box, and to draw it — and then identically on every frame after. The
        // key says everything that moves a line break, so a changed input is a
        // different key rather than a stale one.
        var style = StyleFromFont(f, font, font.Scale);
        var key = WrapKey.For(text, width, style.Key, font.Scale, wrap);
        ref var node = ref CollectionsMarshal.GetValueRefOrAddDefault(f.Wraps, key, out bool found);
        if (!found) node.First = LinesFromCharsUncached(f, text, width, style, font.Scale, wrap);
        node.Touched = f.WrapFrame;
        return node.First;
    }

    /// <summary>Once a frame: the lines of every wrap unasked for too long go back.</summary>
    internal static void WrapsEvict(Fonts f)
    {
        foreach (var (key, node) in f.Wraps)
            if (f.WrapFrame - node.Touched > Fonts.KeepWrapFrames) f.StaleWraps.Add(key);
        foreach (var key in f.StaleWraps)
        {
            f.Wraps.Remove(key, out var node);
            for (var line = node.First; line is not null; line = line.Next)
            {
                line.NextFree = f.FirstFreeLine;
                f.FirstFreeLine = line;
            }
        }
        f.StaleWraps.Clear();
        f.WrapFrame++;
    }

    /// <summary>A line of [<paramref name="start"/>, <paramref name="end"/>), from the free list when it has one.</summary>
    internal static TextLine LineTake(Fonts f, int start, int end)
    {
        var line = f.FirstFreeLine ?? new TextLine();
        f.FirstFreeLine = line.NextFree;
        (line.Start, line.End, line.Next, line.Prev, line.NextFree) = (start, end, null, null, null);
        return line;
    }

    /// <summary>Appends <paramref name="line"/> to <paramref name="chain"/>.</summary>
    internal static void LineChainPush(ref LineChain chain, TextLine line)
    {
        if (chain.Last is { } last)
        {
            last.Next = line;
            line.Prev = last;
        }
        else chain.First = line;
        chain.Last = line;
    }

    // The text's advances, walked once; every line break below is then decided
    // by adding them up, never by measuring a slice again. Added from a line's
    // first char in order, they give what measuring that line gives, to the bit,
    // so the breaks are the ones measuring each candidate made.
    private static TextLine LinesFromCharsUncached(Fonts f, ReadOnlySpan<char> text, float maxWidth, FontStyle style, float scale, TextWrap wrap)
    {
        var chain = new LineChain();
        if (f.WrapAdvances.Length < text.Length) f.WrapAdvances = new float[Math.Max(text.Length, f.WrapAdvances.Length * 2)];
        var adv = f.WrapAdvances.AsSpan(0, text.Length);
        style.Strike.CharAdvances(text, adv);

        int idx = 0;
        while (idx <= text.Length)
        {
            int segStart = idx;
            while (idx < text.Length && text[idx] != '\n' && text[idx] != '\r') idx++;
            int segEnd = idx;

            var before = chain.Last;
            if (wrap == TextWrap.Characters) WrapCharacters(f, text, adv, segStart, segEnd, maxWidth, scale, ref chain);
            else WrapWords(f, text, adv, segStart, segEnd, maxWidth, scale, ref chain);
            // Always emit ≥1 entry per logical line so blank lines (\n\n, trailing \n)
            // produce empty lines that line up with what is drawn.
            if (chain.Last == before)
                LineChainPush(ref chain, LineTake(f, segStart, segStart));

            if (idx >= text.Length) break;
            // Consume the line break — \r\n counts as one.
            if (idx + 1 < text.Length && text[idx] == '\r' && text[idx + 1] == '\n') idx += 2;
            else idx++;
        }

        return chain.First!;
    }

    // One logical line, [segStart, segEnd), broken at single spaces. Adjacent
    // spaces make empty words. A line's text is every word from its first
    // non-empty one to the last that fits, with the spaces between them as they
    // stand in the text — so a candidate line is one slice of it, never built.
    private static void WrapWords(Fonts f, ReadOnlySpan<char> text, ReadOnlySpan<float> adv, int segStart, int segEnd,
                                  float maxWidth, float scale, ref LineChain chain)
    {
        int lineStart = segStart;   // where the line's range starts: its first word
        int textStart = -1;         // where its text starts: its first non-empty word, or none yet
        int lastEnd = segStart;     // the end of the last word that fit
        float width = 0f;           // [textStart, lastEnd) summed from textStart: what measuring it gives

        int i = segStart;
        while (i <= segEnd)
        {
            int wordStart = i;
            while (i < segEnd && text[i] != ' ') i++;
            int wordEnd = i;

            // The candidate line, [textStart or wordStart, wordEnd), summed from its start.
            float w = textStart < 0 ? Sum(adv, wordStart, wordEnd, 0f) : Sum(adv, lastEnd, wordEnd, width);
            if (!Fits(w / scale, maxWidth) && textStart >= 0)
            {
                LineChainPush(ref chain, LineTake(f, lineStart, lastEnd));
                lineStart = wordStart;
                textStart = wordEnd > wordStart ? wordStart : -1;
                width = Sum(adv, wordStart, wordEnd, 0f);
            }
            else
            {
                if (textStart < 0 && wordEnd > wordStart) textStart = wordStart;
                width = w;
            }
            lastEnd = wordEnd;

            if (i >= segEnd) break;
            i++; // skip the space separator
        }
        if (textStart >= 0)
            LineChainPush(ref chain, LineTake(f, lineStart, lastEnd));
    }

    // One logical line, [segStart, segEnd), cut into the longest runs of
    // characters that fit, each at least one character long.
    // A line grows a character at a time while it fits: advances are never
    // negative, so its width only grows, and the first character that doesn't
    // fit ends it (the longest prefix that fits, as a search over prefixes found).
    private static void WrapCharacters(Fonts f, ReadOnlySpan<char> text, ReadOnlySpan<float> adv, int segStart, int segEnd,
                                       float maxWidth, float scale, ref LineChain chain)
    {
        int start = segStart;
        while (start < segEnd)
        {
            int end = NextCharacter(text, start, segEnd);
            float w = Sum(adv, start, end, 0f);
            while (end < segEnd)
            {
                int next = NextCharacter(text, end, segEnd);
                float wider = Sum(adv, end, next, w);
                if (!Fits(wider / scale, maxWidth)) break;
                (end, w) = (next, wider);
            }
            LineChainPush(ref chain, LineTake(f, start, end));
            start = end;
        }
    }

    /// <summary>advances[from..to) added to <paramref name="sum"/>, in order: measuring goes the same way.</summary>
    private static float Sum(ReadOnlySpan<float> advances, int from, int to, float sum)
    {
        for (int k = from; k < to; k++) sum += advances[k];
        return sum;
    }

    // Where the character after the one at `at` starts, no further than `end`.
    private static int NextCharacter(ReadOnlySpan<char> text, int at, int end)
    {
        at++;
        while (at < end && char.IsLowSurrogate(text[at])) at++;
        return at;
    }
}
