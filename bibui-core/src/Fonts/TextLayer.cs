using System.Runtime.InteropServices;
using Bibui.Core.Theming;
using Bibui.Core.TextEdit;

namespace Bibui.Core;

/// <summary>
/// What is done with <see cref="Fonts"/>' data: its faces and styles, a font's
/// metrics, how wide a text is, the runs layout walks a text into, and where a
/// text breaks into lines. Functions over a <see cref="Fonts"/>, named for what
/// they give from what; the data they read and write is all on Fonts, each
/// field saying whose it is. The text layer; the <see cref="Text"/> node is the
/// runtime's, and only hands its chars and its stamp to these.
/// </summary>
public static class TextLayer
{
    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Once a frame, with the renderer's own caches: the wraps unasked for too
    /// long go back, and the slots resolve again next frame, so the next frame's
    /// typography is what they hold. The faces they resolve to come from
    /// FontProvider's cache, which keeps what a (family, weight) first resolved
    /// to: a family registered after it was first asked for still gets the face
    /// it got then.
    /// </summary>
    internal static void FontsEndFrame(Fonts f)
    {
        WrapsEvict(f);
        f.SlotTypography = null;
    }

    // ── Faces ────────────────────────────────────────────────────────────────

    /// <summary>The built-in Inter at <paramref name="weight"/>, for what draws text no theme set: an icon's fallback letter.</summary>
    internal static FontFace FaceBuiltIn(Fonts f, int weight) => f.Provider.Resolve(null, weight);

    /// <summary>The face <paramref name="slot"/> resolves to at <paramref name="weight"/> under <paramref name="theme"/>'s typography.</summary>
    internal static FontFace FaceFromSlot(Fonts f, FontSlot slot, FontWeight weight, ThemeTokens theme)
    {
        if (!ReferenceEquals(theme.Typography, f.SlotTypography))
        {
            int regular = FontResolution.ResolveWeight(FontWeight.Regular, theme);
            int bold = FontResolution.ResolveWeight(FontWeight.Bold, theme);
            for (int s = 0; s < f.SlotFaces.Length / 2; s++)
            {
                string? family = FontResolution.ResolveFamily((FontSlot)s, theme);
                f.SlotFaces[s * 2] = f.Provider.Resolve(family, regular);
                f.SlotFaces[s * 2 + 1] = f.Provider.Resolve(family, bold);
            }
            f.SlotTypography = theme.Typography;
        }
        return f.SlotFaces[(int)slot * 2 + (weight == FontWeight.Bold ? 1 : 0)];
    }

    // ── Fonts ────────────────────────────────────────────────────────────────

    /// <summary>The font <paramref name="text"/> is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontFromText(Fonts f, Text text, ThemeTokens theme, float scale)
        => FontFromSize(f, text.Size, text.Family, text.Weight, theme, scale);

    /// <summary>The font a text with these settings is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontFromSize(Fonts f, FontSize size, FontSlot family, FontWeight weight, ThemeTokens theme, float scale)
        => new(size.In(theme), FaceFromSlot(f, family, weight, theme), scale);

    /// <summary>The theme's font size for a size step, in logical px.</summary>
    internal static float SizeFromStep(ComponentSize step, ThemeTokens theme) => step switch
    {
        ComponentSize.Sm => theme.Typography.SizeSm,
        ComponentSize.Lg => theme.Typography.SizeLg,
        ComponentSize.Xl => theme.Typography.SizeXl,
        ComponentSize.Xxl => theme.Typography.Size2Xl,
        _ => theme.Typography.SizeMd,
    };

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
    {
        var m = StyleFromFont(f, font, font.Scale).Metrics;
        return (m.Descent - m.Ascent) / font.Scale;
    }

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
    /// How large <paramref name="text"/> is: as wide as its chars on one line,
    /// and one line tall. A text is one line whatever room it is given; what
    /// breaks into lines is composed from texts, as a multi-line field is.
    /// </summary>
    internal static Bibui.Core.Draw.Size SizeFromText(Fonts f, Text text, in Font font)
    {
        // One style for both: its runs give the width, its metrics the height.
        var style = StyleFromFont(f, font, font.Scale);
        var m = style.Metrics;
        return new(RunsFromText(f, text, style, font.Scale), (m.Descent - m.Ascent) / font.Scale);
    }

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
            int next = previous + TextOp.ScanTextElement(text, previous, +1);
            float width = WidthFromChars(f, text[..next], font);
            if (width >= x) return x - previousWidth < width - x ? previous : next;
            previous = next;
            previousWidth = width;
        }
        return text.Length;
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
    /// The width of <paramref name="text"/> in <paramref name="style"/>, in logical
    /// px, found by walking it into the frame's runs: its glyphs and positions are
    /// kept for the painter, its runs pushed, and the text is stamped with where
    /// they are. Asked again in the same frame and style (layout asks once per
    /// axis), it answers from the stamp.
    /// </summary>
    internal static float RunsFromText(Fonts f, Text text, FontStyle style, float scale)
    {
        // Empty text is no run: nothing to walk and nothing for the painter to draw.
        if (text.Content.IsEmpty) return 0f;
        if (text.RunFrame == f.RunFrame && f.Runs[text.RunStart].Style == style)
        {
            ref readonly var last = ref f.Runs[text.RunStart + text.RunCount - 1];
            return (last.X + last.Width) / scale;
        }
        return RunsWalk(f, text, style, scale);
    }

    // RunsFromText's miss: the walk, a function of its own so the ask that
    // answers from the stamp stays small enough for layout to inline. Pushes
    // the text's glyphs, then its runs, and stamps it with where they are.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static float RunsWalk(Fonts f, Text text, FontStyle style, float scale)
    {
        var content = text.Content;
        if (f.RunGlyphCount + content.Length > f.RunGlyphs.Length)
        {
            int size = Math.Max(f.RunGlyphs.Length * 2, f.RunGlyphCount + content.Length);
            Array.Resize(ref f.RunGlyphs, size);
            Array.Resize(ref f.RunPoints, size);
        }
        if (f.RunCount == f.Runs.Length) Array.Resize(ref f.Runs, f.Runs.Length * 2);

        int start = f.RunGlyphCount;
        int count = style.Strike.Glyphs(content, f.RunGlyphs.AsSpan(start, content.Length),
            f.RunPoints.AsSpan(start, content.Length), out float width);
        f.RunGlyphCount += count;

        // One run, in the text's own style, from the text's start.
        f.Runs[f.RunCount] = new TextRun { Style = style, GlyphStart = start, GlyphCount = count, X = 0f, Width = width };
        (text.RunStart, text.RunCount, text.RunFrame) = (f.RunCount, 1, f.RunFrame);
        f.RunCount += 1;
        return width / scale;
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
