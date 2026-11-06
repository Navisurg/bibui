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

    // ── Fitting ──────────────────────────────────────────────────────────────

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
    /// over <paramref name="scale"/>, as layout's walk sets them, for code that
    /// measures without laying out (a word to break a line at, a caret's x). A text the fast path
    /// takes whole is summed here, as RunsFast sums it: the same advances, in the same
    /// order, from 0, so the same float. Any other is walked (RunsFromChars, WidthOfRuns).
    /// </summary>
    public static float WidthFromChars(Fonts f, ReadOnlySpan<char> chars, FontStyle style, float scale)
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

    // How far back ScanCluster looks for a certain boundary, in chars. Only a
    // cluster longer than this — stacked marks, a long run of flags — can put the
    // cursor inside it.
    internal const int ElementLookBehind = 64;

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
        clusterStyle.Strike.Shape(cluster, run);
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

    /// <summary>
    /// Every line <paramref name="chars"/> breaks into at <paramref name="width"/>
    /// logical px in <paramref name="style"/> (<see cref="LineFromText"/>, from the
    /// first to the one that ends at the text's end), kept: the same text, width,
    /// style, scale and wrap give the same lines without breaking them again. Good
    /// until the frame ends. An empty text is one empty line.
    /// </summary>
    public static ReadOnlySpan<TextLine> LinesFromText(Fonts f, ReadOnlySpan<char> chars, FontStyle style, float scale,
        float width, TextWrap wrap)
    {
        // The key says everything that moves a line break, so a changed input is a
        // different key rather than a stale one.
        var key = WrapKey.For(chars, width, style.Key, scale, wrap);
        ref var node = ref CollectionsMarshal.GetValueRefOrAddDefault(f.Wraps, key, out bool found);
        if (!found)
        {
            node = f.FirstFreeWrap ?? new WrapNode();
            f.FirstFreeWrap = node.NextFree;
            node.NextFree = null;
            int n = 0;
            for (int at = 0; ; )
            {
                var line = LineFromText(f, chars, style, scale, width, wrap, at);
                if (n == node.Lines.Length) Array.Resize(ref node.Lines, n * 2);
                node.Lines[n++] = line;
                if (line.End == chars.Length) break;
                at = line.Next;
            }
            node.Count = n;
        }
        node!.Touched = f.WrapFrame;
        return node.Lines.AsSpan(0, node.Count);
    }

    /// <summary>Once a frame: the wraps unasked for too long go on the free list, their arrays with them.</summary>
    internal static void WrapsEvict(Fonts f)
    {
        foreach (var (key, node) in f.Wraps)
            if (f.WrapFrame - node.Touched > Fonts.KeepWrapFrames) f.StaleWraps.Add(key);
        foreach (var key in f.StaleWraps)
        {
            f.Wraps.Remove(key, out var node);
            node!.NextFree = f.FirstFreeWrap;
            f.FirstFreeWrap = node;
        }
        f.StaleWraps.Clear();
        f.WrapFrame++;
    }

    /// <summary>
    /// The line that starts at <paramref name="chars"/>[<paramref name="at"/>], a
    /// line's start (0, or a line's <see cref="TextLine.Next"/>), as chars breaks at
    /// <paramref name="width"/> logical px in <paramref name="style"/>, measured over
    /// <paramref name="scale"/>. Not kept: <see cref="LinesFromText"/> keeps them.
    ///
    /// <para><see cref="TextWrap.Words"/> is greedy at single spaces: a line holds
    /// as many words as fit, from its first word with text, and a word wider than
    /// the width is a line of its own, not broken. The space a line breaks at
    /// belongs to neither line; spaces with no text after them on their line are
    /// that line's. <see cref="TextWrap.Characters"/> is greedy at clusters
    /// (<see cref="ScanCluster"/>), at least one a line. Either way a line break
    /// (\n, \r, \r\n) always ends a line, and a blank line is a line of its own.</para>
    /// </summary>
    public static TextLine LineFromText(Fonts f, ReadOnlySpan<char> chars, FontStyle style, float scale,
        float width, TextWrap wrap, int at)
    {
        // The hard line from `at`: [at, end), and where the one after it starts.
        int n = chars[at..].IndexOfAny('\n', '\r');
        int end = n < 0 ? chars.Length : at + n;
        int after = end == chars.Length ? end
            : chars[end] == '\r' && end + 1 < chars.Length && chars[end + 1] == '\n' ? end + 2 : end + 1;

        if (wrap == TextWrap.Characters)
        {
            if (at == end) return new TextLine(at, at, after);
            int stop = ScanCluster(f, style, chars, at, +1, out float clusters);
            while (stop < end)
            {
                int next = ScanCluster(f, style, chars, stop, +1, out float advance);
                if (!Fits((clusters + advance) / scale, width)) break;
                (stop, clusters) = (next, clusters + advance);
            }
            return new TextLine(at, stop, stop == end ? after : stop);
        }

        // Words, in one pass: each word's chars summed as the walk sums them
        // (WordFast), a word the fast path does not take whole walked. Widths in
        // physical px; whole 64ths, so a line's sum of words and spaces is what
        // measuring the line gives.
        var direct = style.Strike.Direct;
        if (!GlyphDirect(direct, ' ', out _, out float space)) space = WidthOfRuns(f, RunsFromChars(f, " ", style), 1f);
        int textStart = -1;    // the line's first word with text, none yet
        int lastEnd = at;      // the end of the last word that fit
        float lineWidth = 0f;  // [textStart, lastEnd)
        int i = at;
        while (true)
        {
            int wordStart = i;
            float word = 0f;
            i = WordFast(chars, i, end, direct, ref word);
            if (i < end && chars[i] != ' ')
            {
                // A char the fast path does not take: the word is walked.
                int s = chars[i..end].IndexOf(' ');
                i = s < 0 ? end : i + s;
                word = WidthOfRuns(f, RunsFromChars(f, chars[wordStart..i], style), 1f);
            }
            float w = textStart < 0 ? word : lineWidth + space + word;
            if (textStart >= 0 && !Fits(w / scale, width))
            {
                // Breaks before this word, past the space, when text follows on the
                // hard line; spaces with no text after them are the line's own.
                if (chars[wordStart..end].IndexOfAnyExcept(' ') >= 0) return new TextLine(at, lastEnd, wordStart);
                return new TextLine(at, end, after);
            }
            if (textStart < 0 && i > wordStart) textStart = wordStart;
            lineWidth = w;
            lastEnd = i;
            if (i >= end) break;
            i++;   // the space between words
        }
        return new TextLine(at, end, after);
    }

    // A word's fast stretch: from i, every char before a space or end that the direct
    // table takes, its advance added to width. A function of its own, so the loop's
    // values stay in registers, as RunsFast's do.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int WordFast(ReadOnlySpan<char> chars, int i, int end, Bibui.Text.FreeType.FontStrike.DirectGlyph[] direct, ref float width)
    {
        float pen = width;
        for (char c; i < end && (c = chars[i]) != ' ' && GlyphDirect(direct, c, out _, out float advance); i++) pen += advance;
        width = pen;
        return i;
    }
}
