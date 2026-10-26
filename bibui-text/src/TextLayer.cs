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

    /// <summary>
    /// Makes <paramref name="font"/>, opened in <paramref name="f"/>'s engine, the
    /// face grapheme clusters are set in; bytes FreeType cannot open leave it as
    /// it was. The same bytes open as one face however often they are handed over.
    /// </summary>
    internal static void ClusterFaceFromData(Fonts f, Bibui.Text.FreeType.FontData font)
    {
        // The font is the system's, handed over at start-up: one FreeType cannot
        // open leaves the system without a cluster face, not the app.
        try { f.ClusterFace = f.Provider.Face(f.Engine.Load(font)); }
        catch (InvalidOperationException) { }
    }

    // ── Styles ───────────────────────────────────────────────────────────────

    /// <summary>The style of <paramref name="face"/> at <paramref name="sizePx"/> physical px.</summary>
    public static FontStyle StyleFromFaceSize(Fonts f, FontFace face, float sizePx)
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
    public static FontStyle StyleFromFont(Fonts f, in Font font, float scale)
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
    [Obsolete("Not on the curated text path that layout and drawing use; to be moved onto it or removed.")]
    internal static float WidthFromChars(Fonts f, ReadOnlySpan<char> text, in Font font)
        => text.IsEmpty ? 0f : StyleFromFont(f, font, font.Scale).Strike.Measure(text) / font.Scale;

    /// <summary>
    /// The char offset in <paramref name="text"/> nearest <paramref name="x"/>,
    /// logical px from where the text starts. See <see cref="Fonts.OffsetAt"/>.
    /// </summary>
    [Obsolete("Not on the curated text path that layout and drawing use; to be moved onto it or removed.")]
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

    // How far back ScanCluster (and ScanTextElement) looks for a certain
    // boundary, in chars. Only a cluster longer than this — stacked marks, a long
    // run of flags — can put the cursor inside it. TextOp.MaxLookBehind sets it.
    internal static int ElementLookBehind = 64;

    /// <summary>
    /// Scan one text element — a grapheme cluster, what reads as one character:
    /// 👍🏽, 🇩🇰, e and an accent, \r\n — in the direction of <paramref name="delta"/>
    /// (sign only). Returns the char offset from <paramref name="cursor"/>,
    /// negative going left, 0 at the edge. The cursor is assumed to be on an
    /// element boundary.
    /// </summary>
    [Obsolete("Not on the curated text path that layout and drawing use; to be moved onto it or removed.")]
    public static int ScanTextElement(ReadOnlySpan<char> str, int cursor, int delta)
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
        int floor = Math.Max(ScanLine(str, cursor, -1), cursor - ElementLookBehind);
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

    /// <summary>
    /// How wide <paramref name="chars"/> are in <paramref name="style"/>, in logical px
    /// over <paramref name="scale"/>, as layout's walk sets them, for usage code that
    /// measures without laying out (a word to break a line at). A text the fast path
    /// takes whole is summed here, as RunsFast sums it: the same advances, in the same
    /// order, from 0, so the same float. Any other is walked (RunsFromChars, WidthOfRuns).
    /// </summary>
    internal static float WidthFromChars(Fonts f, ReadOnlySpan<char> chars, FontStyle style, float scale)
    {
        var direct = style.Strike.Direct;
        float pen = 0f;
        int i = 0;
        for (; i < chars.Length && GlyphDirect(direct, chars[i], out _, out float advance); i++) pen += advance;
        return i == chars.Length ? pen / scale : WidthOfRuns(f, RunsFromChars(f, chars, style), scale);
    }

    // ── Measuring blocks ─────────────────────────────────────────────────────
    //
    // What measuring a text is made of, for layout's walk and for usage code
    // alike: each builds its own loop from these two, so both measure with the
    // same code. GlyphDirect is the fast path's one char; GlyphNext the step for
    // everything else.

    /// <summary>
    /// The fast path's one char: <paramref name="c"/>'s glyph and advance (physical
    /// px) from <paramref name="strike"/>'s direct table, when it is below 256 and
    /// its entry is filled; false for any other char, which <see cref="GlyphNext"/>
    /// measures (and fills the entry of, for a char below 256).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool GlyphDirect(Bibui.Text.FreeType.FontStrike strike, char c, out ushort glyph, out float advance)
        => GlyphDirect(strike.Direct, c, out glyph, out advance);

    // The same, on the strike's table read once by a loop that asks it of many chars.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool GlyphDirect(Bibui.Text.FreeType.FontStrike.DirectGlyph[] direct, char c, out ushort glyph, out float advance)
    {
        if (c < 256 && direct[c].Filled)
        {
            (glyph, advance) = (direct[c].Glyph, direct[c].Advance);
            return true;
        }
        (glyph, advance) = (0, 0f);
        return false;
    }

    /// <summary>
    /// The step at <paramref name="chars"/>[<paramref name="i"/>] the fast path does
    /// not take: a grapheme cluster the cluster face sets (ClusterStart: a code point
    /// the text's face lacks, or a mark that asks for emoji; its shape from the
    /// cluster cache, or HarfBuzz), else one code point in <paramref name="style"/>'s
    /// face (<see cref="Bibui.Text.FreeType.FontStrike.Next"/>).
    /// </summary>
    public static GlyphStep GlyphNext(Fonts f, FontStyle style, ReadOnlySpan<char> chars, int i)
    {
        var clusterFace = f.ClusterFace;
        if (clusterFace != style.Face)
        {
            int s = ClusterStart(chars, i, 0, style.Face, clusterFace);
            if (s >= 0)
            {
                int e = s + StringInfo.GetNextTextElementLength(chars[s..]);
                var clusterStyle = StyleFromFaceSize(f, clusterFace, style.Strike.Size);
                int slot = ClusterSlotOf(f, clusterStyle, chars[s..e], out float clusterAdvance);
                return new GlyphStep(s, e, clusterAdvance, clusterStyle, clusterFace: true, glyph: 0, slot);
            }
        }
        var (glyph, advance, next) = style.Strike.Next(chars, i);
        return new GlyphStep(i, next, advance, style, clusterFace: false, glyph, slot: 0);
    }

    /// <summary>
    /// The cluster next to <paramref name="chars"/>[<paramref name="i"/>] in the
    /// direction of <paramref name="delta"/> (sign only), as the walk sets it: the
    /// index at its far side, i at the text's edge or for a delta of 0, and its width
    /// (physical px) in <paramref name="advance"/>. i is on a cluster boundary.
    ///
    /// <para>Forward it is one <see cref="GlyphDirect"/> or <see cref="GlyphNext"/> step
    /// and the steps after it that belong to it: one that starts back over it is it,
    /// set whole in the cluster face (1️⃣), and one with no width sits on it (an
    /// accent). A char below 256 never belongs: it is the direct table's, a step of its
    /// own, so it always starts a cluster. Back, it is back to such a char, no more
    /// than <see cref="ElementLookBehind"/> chars, and forward from there to i.</para>
    /// </summary>
    // NOTE(k): for a scan done once (a key press, a click). It takes the step after
    // the cluster to see whether it belongs, and throws it away when not, so a loop
    // over many clusters takes every GlyphNext step twice; a hot loop calls
    // GlyphDirect and GlyphNext itself, as the walk does.
    public static int ScanCluster(Fonts f, FontStyle style, ReadOnlySpan<char> chars, int i, int delta, out float advance)
    {
        advance = 0f;
        if (delta < 0)
        {
            if (i <= 0) return 0;
            int floor = Math.Max(0, i - ElementLookBehind);
            int start = i - 1;
            while (start > floor && chars[start] >= 256) start--;

            // Forward to the last cluster that starts before i.
            for (int next = ScanCluster(f, style, chars, start, +1, out advance); next < i; next = ScanCluster(f, style, chars, start, +1, out advance))
                start = next;
            return start;
        }
        if (delta == 0 || i >= chars.Length) return i;

        int end = i + 1;
        if (!GlyphDirect(style.Strike, chars[i], out _, out advance))
        {
            var first = GlyphNext(f, style, chars, i);
            (end, advance) = (first.Next, first.Advance);
        }

        // The steps after it that belong to it.
        while (end < chars.Length && chars[end] >= 256)
        {
            var step = GlyphNext(f, style, chars, end);
            if (step.Start == end && step.Advance != 0f) break;
            if (step.Start < end) advance = step.Advance;
            end = step.Next;
        }
        return end;
    }

    /// <summary>
    /// The word next to <paramref name="chars"/>[<paramref name="i"/>] in the direction
    /// of <paramref name="delta"/> (sign only): the index at its far side, over the
    /// spaces next to i and then the word, a cluster at a time (<see cref="ScanCluster"/>);
    /// i at the text's edge or for a delta of 0. A word is a run of clusters that are
    /// not spaces, punctuation included (Ctrl+← →). A cluster is a space when its first
    /// char is: a space, a tab or a line break.
    /// </summary>
    // NOTE(k): for a scan done once, as ScanCluster.
    public static int ScanWord(Fonts f, FontStyle style, ReadOnlySpan<char> chars, int i, int delta)
    {
        if (delta > 0)
        {
            while (i < chars.Length && IsWordSpace(chars[i])) i = ScanCluster(f, style, chars, i, +1, out _);
            while (i < chars.Length && !IsWordSpace(chars[i])) i = ScanCluster(f, style, chars, i, +1, out _);
        }
        else if (delta < 0)
        {
            int start;
            while (i > 0 && IsWordSpace(chars[start = ScanCluster(f, style, chars, i, -1, out _)])) i = start;
            while (i > 0 && !IsWordSpace(chars[start = ScanCluster(f, style, chars, i, -1, out _)])) i = start;
        }
        return i;
    }

    /// <summary>
    /// The end of the line <paramref name="chars"/>[<paramref name="i"/>] is on, in the
    /// direction of <paramref name="delta"/> (sign only): back to just after the line
    /// break before i or the text's start, forward to just before the next one or the
    /// text's end; i for a delta of 0. A line break is below 256, so it always starts a
    /// cluster (<see cref="ScanCluster"/>): a search for it lands on cluster boundaries,
    /// with no cluster scan, however long the line.
    /// </summary>
    public static int ScanLine(ReadOnlySpan<char> chars, int i, int delta)
    {
        if (delta < 0) return chars[..i].LastIndexOf('\n') + 1;
        if (delta == 0) return i;
        int n = chars[i..].IndexOf('\n');
        return n < 0 ? chars.Length : i + n;
    }

    // Whether a cluster that starts with c is a space between words.
    private static bool IsWordSpace(char c) => c is ' ' or '\t' or '\n' or '\r';

    // RunsFromChars' walk, a function of its own so the check before it
    // (RunsAreCurrent) and the ask around it stay small enough to inline.
    // Pushes the text's glyphs, then its runs, and returns their handle.
    //
    // Layout's loop over the measuring blocks: stretches of the fast path's
    // chars (GlyphDirect) in the text's face, and a GlyphNext step for each char
    // they do not take. A cluster set in the cluster face ends the text's run,
    // and it and the clusters after it are a run of their own; then the text's
    // face goes on. The runs are the loop's own bookkeeping; the measuring is the
    // blocks'.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static RunHandle RunsWalk(Fonts f, ReadOnlySpan<char> chars, FontStyle style)
    {
        var strike = style.Strike;
        RunsGlyphRoom(f, f.RunGlyphCount + chars.Length);
        Span<ushort> glyphs = f.RunGlyphs;
        Span<System.Numerics.Vector2> points = f.RunPoints;

        int first = f.RunCount;
        int n = f.RunGlyphCount;
        int runGlyph = n;                // where the run being walked starts
        var runStyle = style;            // and what it is set in
        float x = 0f, pen = 0f;          // its pen from the text's start, and the pen within it
        int i = 0;
        while (i < chars.Length)
        {
            if (runStyle == style)
            {
                // In the text's face: the fast stretch from here, then the step it stops at.
                (i, n, pen) = RunsFast(chars, i, strike.Direct, glyphs, points, n, pen);
                if (i == chars.Length) break;
            }
            else if (GlyphDirect(strike.Direct, chars[i], out _, out _))
            {
                // After a cluster, a fast char goes back to the text's face: a run of its own.
                RunPush(f, runStyle, runGlyph, n - runGlyph, x, pen);
                x += pen;
                (runStyle, runGlyph, pen) = (style, n, 0f);
                continue;
            }

            var step = GlyphNext(f, style, chars, i);
            if (step.ClusterFace)
            {
                if (runStyle == style)
                {
                    // The cluster's chars before i went into the text's run, a glyph
                    // per code point: take them back, and the pen with them.
                    int back = CodePointCount(chars[step.Start..i]);
                    if (back > 0) { n -= back; pen = points[n].X; }
                    // The text's run so far: always the first (it says what the text
                    // was walked in), others when they have glyphs.
                    if (f.RunCount == first || n > runGlyph) { RunPush(f, style, runGlyph, n - runGlyph, x, pen); x += pen; }
                    (runStyle, runGlyph, pen) = (step.Style, n, 0f);
                }
                n = ClusterPlace(f, step, n, ref pen);
                RunsGlyphRoom(f, n + chars.Length - step.Next);
                glyphs = f.RunGlyphs;
                points = f.RunPoints;
            }
            else
            {
                if (runStyle != style) { RunPush(f, runStyle, runGlyph, n - runGlyph, x, pen); x += pen; (runStyle, runGlyph, pen) = (style, n, 0f); }
                glyphs[n] = step.Glyph;
                points[n] = new(pen, 0f);
                pen += step.Advance;
                n++;
            }
            i = step.Next;
        }
        if (f.RunCount == first || n > runGlyph) RunPush(f, runStyle, runGlyph, n - runGlyph, x, pen);
        f.RunGlyphCount = n;
        return new RunHandle(first, f.RunCount - first, f.RunFrame);
    }

    // Layout's fast stretch: from i, every char GlyphDirect takes, its glyph and
    // point written at n. A function of its own, with nothing else live in it, so
    // the JIT keeps the index, the count and the pen in registers.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (int I, int N, float Pen) RunsFast(ReadOnlySpan<char> chars, int i, Bibui.Text.FreeType.FontStrike.DirectGlyph[] direct,
        Span<ushort> glyphs, Span<System.Numerics.Vector2> points, int n, float pen)
    {
        for (; i < chars.Length && GlyphDirect(direct, chars[i], out ushort glyph, out float advance); i++, n++)
        {
            glyphs[n] = glyph;
            points[n] = new(pen, 0f);
            pen += advance;
        }
        return (i, n, pen);
    }

    // A run of a text, pushed onto the frame's table.
    private static void RunPush(Fonts f, FontStyle style, int glyphStart, int glyphCount, float x, float width)
    {
        if (f.RunCount == f.Runs.Length) Array.Resize(ref f.Runs, f.Runs.Length * 2);
        f.Runs[f.RunCount++] = new TextRun { Style = style, GlyphStart = glyphStart, GlyphCount = glyphCount, X = x, Width = width };
    }

    // Room in the frame's glyph buffers for `need` glyphs from their start: grown, never shrunk.
    private static void RunsGlyphRoom(Fonts f, int need)
    {
        if (need <= f.RunGlyphs.Length) return;
        int size = Math.Max(f.RunGlyphs.Length * 2, need);
        Array.Resize(ref f.RunGlyphs, size);
        Array.Resize(ref f.RunPoints, size);
    }

    // ── Grapheme clusters in the walk ────────────────────────────────────────

    // Whether the code point at i, which the walk's fast loop did not take,
    // belongs to a grapheme cluster the cluster face sets rather than the text's
    // face; where that cluster starts if so (no earlier than floor), -1 if not.
    // Two ways: a mark that asks for emoji (U+FE0F, a ZWJ, a keycap, a skin tone)
    // in a cluster whose base the cluster face has (1️⃣, ❤️, 👍🏽, 🧑‍🚀), or a code
    // point the text's face lacks and the cluster face has (👍, 🇩🇰).
    private static int ClusterStart(ReadOnlySpan<char> chars, int i, int floor, FontFace face, FontFace clusterFace)
    {
        Rune.DecodeFromUtf16(chars[i..], out Rune rune, out _);
        int cp = rune.Value;
        if (cp is 0xFE0F or 0x200D or 0x20E3 || cp is >= 0x1F3FB and <= 0x1F3FF)
        {
            // The mark joins what comes before it: back to where its cluster starts.
            int s = i;
            while (s > floor && i - s < ElementLookBehind)
            {
                Rune.DecodeLastFromUtf16(chars[..s], out _, out int length);
                int j = s - Math.Max(1, length);
                if (j + StringInfo.GetNextTextElementLength(chars[j..]) <= i) break;
                s = j;
            }
            Rune.DecodeFromUtf16(chars[s..], out Rune bas, out _);
            return clusterFace.HasGlyph(bas.Value) ? s : -1;
        }
        return !face.HasGlyph(cp) && clusterFace.HasGlyph(cp) ? i : -1;
    }

    // How many code points chars holds: how many glyphs the text's face gave them.
    private static int CodePointCount(ReadOnlySpan<char> chars)
    {
        int count = 0;
        for (int i = 0; i < chars.Length; i++, count++)
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) i++;
        return count;
    }

    // A grapheme cluster's shape in the cluster face, by a hash of its chars
    // seeded with the style's key: its slot in Fonts.Clusters, made by HarfBuzz
    // on a miss and kept; 0 when the shape is too long to keep (more than eight
    // glyphs), which leaves it in the engine's scratch run. Its advance either way.
    private static int ClusterSlotOf(Fonts f, FontStyle clusterStyle, ReadOnlySpan<char> cluster, out float advance)
    {
        ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(MemoryMarshal.AsBytes(cluster), unchecked((long)clusterStyle.Key));
        if (f.ClusterSlots.TryGetValue(hash, out int slot))
        {
            ClusterLruUnlink(f, slot);
            ClusterLruPushFront(f, slot);
            advance = f.Clusters[slot].Advance;
            return slot;
        }

        var run = clusterStyle.Strike.Font.Engine.ScratchRun;
        clusterStyle.Strike.Shape(cluster, run, Bibui.Text.FreeType.ShapeOptions.HarfBuzzDefault);
        advance = 0f;
        if (run.Count > 8)
        {
            foreach (float a in run.Advances) advance += a;
            return 0;
        }
        var shape = new ClusterShape { Hash = hash, GlyphCount = run.Count };
        for (int k = 0; k < run.Count; k++)
        {
            shape.Glyphs[k] = (ushort)run.Glyphs[k];
            shape.Points[k] = new(advance + run.XOffsets[k], run.YOffsets[k]);
            advance += run.Advances[k];
        }
        shape.Advance = advance;

        // A slot: the next never used, else the least recently used, its key removed.
        if (f.ClusterUsed < Fonts.ClusterCapacity) slot = ++f.ClusterUsed;
        else
        {
            slot = f.Clusters[0].PrevLru;
            ClusterLruUnlink(f, slot);
            f.ClusterSlots.Remove(f.Clusters[slot].Hash);
        }
        f.Clusters[slot] = shape;
        f.ClusterSlots[hash] = slot;
        ClusterLruPushFront(f, slot);
        return slot;
    }

    // A cluster step's glyphs into the frame's buffers at n, from the pen at pen:
    // its kept shape's, or the scratch run's for one too long to keep. Returns the
    // glyph count after it. A kept shape and a shaped one place glyphs the same way.
    private static int ClusterPlace(Fonts f, in GlyphStep step, int n, ref float pen)
    {
        if (step.Slot > 0) return ClusterCopy(f, ref f.Clusters[step.Slot], n, ref pen);
        var run = step.Style.Strike.Font.Engine.ScratchRun;
        RunsGlyphRoom(f, n + run.Count);
        float at = 0f;
        for (int k = 0; k < run.Count; k++, n++)
        {
            f.RunGlyphs[n] = (ushort)run.Glyphs[k];
            f.RunPoints[n] = new(pen + (at + run.XOffsets[k]), run.YOffsets[k]);
            at += run.Advances[k];
        }
        pen += at;
        return n;
    }

    // A kept shape's glyphs into the frame's buffers at n, from the pen at pen.
    private static int ClusterCopy(Fonts f, ref ClusterShape shape, int n, ref float pen)
    {
        RunsGlyphRoom(f, n + shape.GlyphCount);
        for (int k = 0; k < shape.GlyphCount; k++, n++)
        {
            f.RunGlyphs[n] = shape.Glyphs[k];
            f.RunPoints[n] = new(pen + shape.Points[k].X, shape.Points[k].Y);
        }
        pen += shape.Advance;
        return n;
    }

    // The LRU ring through Fonts.Clusters[0]: its NextLru the most recent, its PrevLru the least.
    private static void ClusterLruUnlink(Fonts f, int slot)
    {
        ref var c = ref f.Clusters[slot];
        f.Clusters[c.PrevLru].NextLru = c.NextLru;
        f.Clusters[c.NextLru].PrevLru = c.PrevLru;
    }

    private static void ClusterLruPushFront(Fonts f, int slot)
    {
        ref var c = ref f.Clusters[slot];
        c.PrevLru = 0;
        c.NextLru = f.Clusters[0].NextLru;
        f.Clusters[c.NextLru].PrevLru = slot;
        f.Clusters[0].NextLru = slot;
    }

    // ── Wraps ────────────────────────────────────────────────────────────────

    /// <summary>The lines <paramref name="text"/> breaks into at <paramref name="width"/>. See <see cref="Fonts.Wrap"/>.</summary>
    [Obsolete("Not on the curated text path that layout and drawing use; to be moved onto it or removed.")]
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
    [Obsolete("Not on the curated text path that layout and drawing use; to be moved onto it or removed.")]
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
