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
    //
    // The text is walked in its own face, a glyph per code point, as fast as the
    // strike's tables go. Only a char the fast loop does not take (at or above
    // 256) is looked at again: one that starts a grapheme cluster the text's face
    // cannot set (ClusterStart) ends the run, and the cluster, with every cluster
    // after it that goes the same way, is a run of its own in the cluster face,
    // shaped by HarfBuzz (ClusterShape). Then the text's face goes on. Plain text
    // never leaves the fast loop and its one-char step, and with no cluster face
    // set up (the built-in Inter) nothing is looked at again at all.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static RunHandle RunsWalk(Fonts f, ReadOnlySpan<char> chars, FontStyle style)
    {
        var strike = style.Strike;
        var clusterFace = f.ClusterFace;
        bool clusters = clusterFace != style.Face;
        RunsGlyphRoom(f, f.RunGlyphCount + chars.Length);

        int first = f.RunCount;
        int n = f.RunGlyphCount;
        int runGlyph = n, runChar = 0;   // where the run being walked starts, in glyphs and in chars
        float x = 0f, pen = 0f;          // its pen from the text's start, and the pen within it
        int i = 0;
        while (i < chars.Length)
        {
            (i, n, pen) = Bibui.Text.FreeType.FontStrike.GlyphsDirect(chars, i, strike.Direct, f.RunGlyphs, f.RunPoints, n, pen);
            if (i == chars.Length) break;

            int s = clusters ? ClusterStart(chars, i, runChar, style.Face, clusterFace) : -1;
            if (s >= 0)
            {
                // The cluster's chars before i went into the text's run, a glyph
                // per code point: take them back, and the pen with them.
                int back = CodePointCount(chars[s..i]);
                if (back > 0) { n -= back; pen = f.RunPoints[n].X; }

                // The text's run so far: always the first (it says what the text
                // was walked in), others when they have glyphs.
                if (f.RunCount == first || n > runGlyph) { RunPush(f, style, runGlyph, n - runGlyph, x, pen); x += pen; }

                // The clusters' run: this one, and every one after it that goes the same way.
                var clusterStyle = StyleFromFaceSize(f, clusterFace, strike.Size);
                int clusterGlyph = n;
                pen = 0f;
                do
                {
                    int e = s + StringInfo.GetNextTextElementLength(chars[s..]);
                    n = ClusterShape(f, clusterStyle, chars[s..e], n, ref pen);
                    s = e;
                }
                while (s < chars.Length && ClusterStart(chars, s, s, style.Face, clusterFace) == s);
                RunPush(f, clusterStyle, clusterGlyph, n - clusterGlyph, x, pen);
                x += pen;

                (i, runGlyph, runChar, pen) = (s, n, s, 0f);
                RunsGlyphRoom(f, n + chars.Length - i);
                continue;
            }

            var (glyph, advance, next) = strike.Next(chars, i);
            f.RunGlyphs[n] = glyph;
            f.RunPoints[n] = new(pen, 0f);
            pen += advance;
            n++;
            i = next;
        }
        if (f.RunCount == first || n > runGlyph) RunPush(f, style, runGlyph, n - runGlyph, x, pen);
        f.RunGlyphCount = n;
        return new RunHandle(first, f.RunCount - first, f.RunFrame);
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

    // One grapheme cluster in the cluster face, appended at n with the pen at
    // pen: its glyphs (a ligature makes 👍🏽 or 🧑‍🚀 one), each at the pen plus
    // its place in the cluster, and the pen moved by the cluster's width. Returns
    // the glyph count after it. The shape is HarfBuzz's, made once a cluster and
    // style and kept in Fonts.Clusters by a hash of the chars seeded with the
    // style's key; a hit and a miss place the glyphs the same way, to the bit.
    private static int ClusterShape(Fonts f, FontStyle clusterStyle, ReadOnlySpan<char> cluster, int n, ref float pen)
    {
        ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(MemoryMarshal.AsBytes(cluster), unchecked((long)clusterStyle.Key));
        if (f.ClusterSlots.TryGetValue(hash, out int slot))
        {
            ClusterLruUnlink(f, slot);
            ClusterLruPushFront(f, slot);
            return ClusterCopy(f, ref f.Clusters[slot], n, ref pen);
        }

        var run = clusterStyle.Strike.Font.Engine.ScratchRun;
        clusterStyle.Strike.Shape(cluster, run, Bibui.Text.FreeType.ShapeOptions.HarfBuzzDefault);
        var shape = new ClusterShape { Hash = hash, GlyphCount = run.Count };
        if (run.Count > 8)
        {
            // Too long to keep (a font with no ligature for a long sequence): placed as shaped, every walk.
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
        float advance = 0f;
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
        return ClusterCopy(f, ref f.Clusters[slot], n, ref pen);
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
