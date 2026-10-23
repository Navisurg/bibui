using Bibui.Core.Draw;
using Bibui.Core.Theming;
using Bibui.Core.FreeType;
using Bibui.Core.TextEdit;

namespace Bibui.Core;

/// <summary>
/// Everything about text that is not drawing it: the typefaces, the runs a
/// text splits into by typeface, a font's metrics, how wide a text is, and
/// where it breaks into lines at a width. Text is UTF-16 throughout. Layout, the
/// painter and the runtime all ask this one instance, so a text measured by any
/// of them is a cache hit for the others, and what is drawn is what was measured.
///
/// <para>It knows nothing about a canvas, and nothing about a raster backend:
/// faces, metrics and measurements come from the FreeType + HarfBuzz engine it
/// owns, so whichever backend draws, layout is the same. Sizes are logical px; a measurement is
/// taken at the physical size, <c>logical × scale</c>, because hinting moves
/// glyph advances with the pixel size. A <see cref="Font"/> carries its scale,
/// so what takes one needs nothing else.</para>
///
/// <para>The runtime hands out the renderer's own instance as
/// <c>runtime.Fonts</c>, and the font a text built at some point would get as
/// <c>runtime.FontInScope()</c>.</para>
/// </summary>
public sealed class Fonts : IDisposable
{
    private readonly List<Action> _endFrames = [];
    private readonly List<IDisposable> _disposables = [];

    // The one text engine every configuration measures with, whatever draws:
    // confined to the rendering thread, as Fonts is.
    private readonly TextEngine _engine = new();
    private readonly FontProvider _provider;
    private readonly ResourceCache<MeasureKey, float> _measures;
    private readonly ResourceCache<WrapKey, TextLine> _lines;
    private readonly ResourceCache<ShapeKey, ShapedText> _shapes;

    /// <summary>Fonts on a text engine of their own, which the renderer owns with them.</summary>
    internal Fonts()
    {
        _provider = new FontProvider(_engine, Create<RunsKey, TextRun>());
        _measures = Create<MeasureKey, float>();
        _lines = Create<WrapKey, TextLine>();
        _shapes = Create<ShapeKey, ShapedText>();
    }

    private ResourceCache<TKey, TValue> Create<TKey, TValue>() where TKey : notnull
    {
        var cache = new ResourceCache<TKey, TValue>(keepFrames: 60);
        _endFrames.Add(cache.EndFrame);
        _disposables.Add(cache);
        return cache;
    }

    /// <summary>Evicts what was not asked for; called once per frame, with the renderer's own caches.</summary>
    internal void EndFrame()
    {
        foreach (var endFrame in _endFrames) endFrame();
        // The slots resolve again next frame, so a font registered in between
        // is picked up without anything to invalidate.
        _slotTypography = null;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        _engine.Dispose();
    }

    // ── Typefaces and runs ───────────────────────────────────────────────────

    /// <summary>The built-in Inter at <paramref name="weight"/>, for what draws text no theme set: an icon's fallback letter.</summary>
    internal FontFace BuiltIn(int weight) => _provider.Resolve(null, weight);

    /// <summary>
    /// <paramref name="text"/> split into runs of one face each. With no fallback
    /// per character yet, that is one run in <paramref name="face"/>. The first
    /// run, the rest linked from it; null for empty text.
    /// </summary>
    internal TextRun? SplitRuns(ReadOnlySpan<char> text, FontFace face) => _provider.SplitRuns(text, face);

    // ── A theme's faces ──────────────────────────────────────────────────────

    // The face each slot resolves to at each weight, [slot * 2 + bold], for the
    // typography they were resolved from. A family is named only here: a text
    // asks by slot, and the slot's face is an index. Resolved again when a
    // text asks under other typography, and once a frame (EndFrame), so it is
    // never older than the frame asking.
    private readonly FontFace[] _slotFaces = new FontFace[8];
    private TypographyTokens? _slotTypography;

    private FontFace FaceOf(FontSlot slot, FontWeight weight, ThemeTokens theme)
    {
        if (!ReferenceEquals(theme.Typography, _slotTypography))
        {
            int regular = FontResolution.ResolveWeight(FontWeight.Regular, theme);
            int bold = FontResolution.ResolveWeight(FontWeight.Bold, theme);
            for (int s = 0; s < _slotFaces.Length / 2; s++)
            {
                string? family = FontResolution.ResolveFamily((FontSlot)s, theme);
                _slotFaces[s * 2] = _provider.Resolve(family, regular);
                _slotFaces[s * 2 + 1] = _provider.Resolve(family, bold);
            }
            _slotTypography = theme.Typography;
        }
        return _slotFaces[(int)slot * 2 + (weight == FontWeight.Bold ? 1 : 0)];
    }

    // ── A text node's font ───────────────────────────────────────────────────

    /// <summary>The font <paramref name="text"/> is set in, measured at <paramref name="scale"/>.</summary>
    internal Font FontOf(Text text, ThemeTokens theme, float scale)
        => FontOf(text.Size, text.Family, text.Weight, theme, scale);

    /// <summary>The font a text with these settings is set in, measured at <paramref name="scale"/>.</summary>
    internal Font FontOf(FontSize size, FontSlot family, FontWeight weight, ThemeTokens theme, float scale)
        => new(size.In(theme), FaceOf(family, weight, theme), scale);

    // ── Styles ───────────────────────────────────────────────────────────────

    // A face at a physical size, with what text asks of it. Found by the face's
    // id and the size's bits in one 64-bit key, so a lookup hashes no name.
    // Never evicted: an app sets text in a handful, and their strikes are kept
    // by the engine for its life anyway.
    private readonly Dictionary<ulong, FontStyle> _styles = new();

    /// <summary>The style of <paramref name="face"/> at <paramref name="sizePx"/> physical px.</summary>
    internal FontStyle StyleOf(FontFace face, float sizePx)
    {
        ulong key = (ulong)(uint)face.Id << 32 | (uint)BitConverter.SingleToInt32Bits(sizePx);
        if (_styles.TryGetValue(key, out var style)) return style;
        var strike = face.Strike(sizePx);
        var m = strike.Metrics;
        return _styles[key] = new FontStyle(key, face, strike, new TextMetrics(m.Ascent, m.Descent, m.Leading));
    }

    /// <summary>
    /// The style of <paramref name="font"/> measured at <paramref name="scale"/>: a
    /// draw measures at its canvas's scale, layout at the font's own.
    /// </summary>
    internal FontStyle StyleOf(in Font font, float scale) => StyleOf(font.Face ?? BuiltIn(400), font.Size * scale);

    /// <summary>The theme's font size for a size step, in logical px.</summary>
    internal static float SizeOf(ComponentSize step, ThemeTokens theme) => step switch
    {
        ComponentSize.Sm => theme.Typography.SizeSm,
        ComponentSize.Lg => theme.Typography.SizeLg,
        ComponentSize.Xl => theme.Typography.SizeXl,
        ComponentSize.Xxl => theme.Typography.Size2Xl,
        _ => theme.Typography.SizeMd,
    };

    // ── Line metrics ─────────────────────────────────────────────────────────

    /// <summary>
    /// The height of one line, in logical px: the font's ascent to its descent,
    /// with no leading. Lines of a wrapped text are <see cref="LineAdvance"/> apart.
    /// </summary>
    internal float LineBox(in Font font)
    {
        var m = StyleOf(font, font.Scale).Metrics;
        return (m.Descent - m.Ascent) / font.Scale;
    }

    /// <summary>
    /// Baseline-to-baseline distance between wrapped lines, in logical px — the
    /// leading that <see cref="LineBox"/> does not bake into a single line.
    /// </summary>
    internal static float LineAdvance(float fontSize) => fontSize * 1.4f;

    /// <summary>
    /// Total height of <paramref name="lines"/> stacked lines: the last line's em box
    /// plus one advance for every line above it. One line is exactly its em box.
    /// </summary>
    internal float BlockHeight(int lines, in Font font)
        => LineBox(font) + MathF.Max(0, lines - 1) * LineAdvance(font.Size);

    /// <summary>
    /// The height of one line of text in <paramref name="font"/>, in logical px:
    /// the font's ascent to its descent, with no leading. What layout gives a
    /// one-line text.
    /// </summary>
    public float LineHeight(in Font font) => LineBox(font);

    /// <summary>
    /// The distance from one line's top to the next's in <paramref name="font"/>,
    /// in logical px: what layout puts between the lines of a wrapped text. At
    /// least <see cref="LineHeight"/>; the difference is the leading.
    /// </summary>
    public float LineAdvance(in Font font) => LineAdvance(font.Size);

    // ── A text node's size ───────────────────────────────────────────────────

    /// <summary>
    /// How large <paramref name="text"/> is: as wide as its chars on one line,
    /// and one line tall. A text is one line whatever room it is given; what
    /// breaks into lines is composed from texts, as a multi-line field is.
    /// </summary>
    internal Bibui.Core.Draw.Size Measure(Text text, in Font font)
        => new(Measure(text.Content, font), LineHeight(font));

    // ── Public: measuring in a Font ──────────────────────────────────────────

    /// <summary>
    /// The width of <paramref name="text"/> on one line in
    /// <paramref name="font"/>, in logical px. The same number layout gives a
    /// text with that content, from the same cache.
    /// </summary>
    public float Measure(ReadOnlySpan<char> text, in Font font)
    {
        if (text.IsEmpty) return 0f;
        var style = StyleOf(font, font.Scale);
        var key = MeasureKey.For(text, style.Key);
        return _measures.TryGet(key, out float width)
            ? width
            : _measures.Add(key, MeasureRuns(text, style) / font.Scale);
    }

    /// <summary>
    /// The char offset in <paramref name="text"/> nearest <paramref name="x"/>,
    /// logical px from where the text starts: a text element boundary, the one
    /// after a character when <paramref name="x"/> is past its middle. 0 at or before
    /// the start, the length past the end.
    ///
    /// <para>Each candidate is the width of the chars before it, measured as a
    /// text of its own — which is how a text split at that offset is drawn.</para>
    /// </summary>
    public int OffsetAt(ReadOnlySpan<char> text, in Font font, float x)
    {
        if (x <= 0f || text.IsEmpty) return 0;
        int previous = 0;
        float previousWidth = 0f;
        while (previous < text.Length)
        {
            int next = previous + TextOp.ScanTextElement(text, previous, +1);
            float width = Measure(text[..next], font);
            if (width >= x) return x - previousWidth < width - x ? previous : next;
            previous = next;
            previousWidth = width;
        }
        return text.Length;
    }

    // ── Measuring ────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Width in physical px of <paramref name="text"/> in <paramref name="style"/>,
    /// summed over the run split. Measuring the whole text against one typeface
    /// would be wrong wherever fallback applies.
    /// </summary>
    internal float MeasureRuns(ReadOnlySpan<char> text, FontStyle style)
    {
        float total = 0f;
        for (var run = SplitRuns(text, style.Face); run is not null; run = run.Next)
        {
            var strike = run.Face == style.Face ? style.Strike : StyleOf(run.Face, style.Strike.Size).Strike;
            total += strike.Measure(text[run.Start..run.End]);
        }
        return total;
    }


    // ── Shaping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One run of <paramref name="text"/> as glyphs in <paramref name="face"/> at
    /// <paramref name="sizePx"/> physical px: one glyph per code point through the
    /// face's cmap, each at the sum of the linear advances before it. Measuring
    /// sums the same advances in the same order, so what is drawn is what was
    /// measured. Cached, so the same text frame after frame is shaped once.
    /// </summary>
    internal ShapedText Shape(ReadOnlySpan<char> text, FontFace face, float sizePx)
    {
        var key = ShapeKey.For(text, face, sizePx);
        return _shapes.TryGet(key, out var shaped) ? shaped : _shapes.Add(key, ShapeUncached(text, face, sizePx));
    }

    private ShapedText ShapeUncached(ReadOnlySpan<char> text, FontFace face, float sizePx)
    {
        var run = _engine.ScratchRun;
        face.Strike(sizePx).Shape(text, run);
        var glyphs = new ushort[run.Count];
        var positions = new System.Numerics.Vector2[run.Count];
        float pen = 0f;
        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = (ushort)run.Glyphs[i];
            positions[i] = new(pen, 0f);
            pen += run.Advances[i];
        }
        run.Clear();
        return new ShapedText(glyphs, positions, pen);
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lines <paramref name="text"/> breaks into at <paramref name="width"/>
    /// logical px in <paramref name="font"/>: the first, the rest linked from it;
    /// null for empty text. Offsets are chars into <paramref name="text"/>.
    ///
    /// <para><see cref="TextWrap.Words"/>, the default, is greedy at spaces: a
    /// line holds as many words as fit, and a word wider than the width is a
    /// line of its own, not broken. The space a line breaks at belongs to
    /// neither line — it lies between one line's <see cref="TextLine.End"/> and
    /// the next one's <see cref="TextLine.Start"/>.</para>
    /// <para><see cref="TextWrap.Characters"/> is greedy at characters: a line
    /// holds as many as fit, at least one, and the next starts where it ends.
    /// Spaces are characters like any other.</para>
    /// <para>Either way a line break (\n, \r, \r\n) always starts a new line,
    /// and a blank line is a line of its own.</para>
    /// </summary>
    public TextLine? Wrap(ReadOnlySpan<char> text, in Font font, float width, TextWrap wrap = TextWrap.Words)
    {
        if (text.IsEmpty) return null;
        // The same text is wrapped several times in one frame — to measure the
        // box, and to draw it — and then identically on every frame after. The
        // key says everything that moves a line break, so a changed input is a
        // different key rather than a stale one.
        var style = StyleOf(font, font.Scale);
        var key = WrapKey.For(text, width, style.Key, font.Scale, wrap);
        return _lines.TryGet(key, out var lines) ? lines : _lines.Add(key, WrapUncached(text, width, style, font.Scale, wrap));
    }

    private TextLine WrapUncached(ReadOnlySpan<char> text, float maxWidth, FontStyle style, float scale, TextWrap wrap)
    {
        var result = new Lines();

        int idx = 0;
        while (idx <= text.Length)
        {
            int segStart = idx;
            while (idx < text.Length && text[idx] != '\n' && text[idx] != '\r') idx++;
            int segEnd = idx;

            var before = result.Last;
            if (wrap == TextWrap.Characters) WrapCharacters(text, segStart, segEnd, maxWidth, style, scale, ref result);
            else WrapSegment(text, segStart, segEnd, maxWidth, style, scale, ref result);
            // Always emit ≥1 entry per logical line so blank lines (\n\n, trailing \n)
            // produce empty lines that line up with what is drawn.
            if (result.Last == before)
                result.Add(new TextLine(segStart, segStart));

            if (idx >= text.Length) break;
            // Consume the line break — \r\n counts as one.
            if (idx + 1 < text.Length && text[idx] == '\r' && text[idx + 1] == '\n') idx += 2;
            else idx++;
        }

        return result.First!;
    }

    // The chain being built: its first line, which is what is kept, and its last, which is appended to.
    private struct Lines
    {
        public TextLine? First;
        public TextLine? Last;

        public void Add(TextLine line) => Last = First is null ? First = line : Last!.Append(line);
    }

    // One logical line, [segStart, segEnd), broken at single spaces. Adjacent
    // spaces make empty words. A line's text is every word from its first
    // non-empty one to the last that fits, with the spaces between them as they
    // stand in the text — so a candidate line is one slice of it, never built.
    private void WrapSegment(ReadOnlySpan<char> text, int segStart, int segEnd, float maxWidth, FontStyle style,
                             float scale, ref Lines result)
    {
        int lineStart = segStart;   // where the line's range starts: its first word
        int textStart = -1;         // where its text starts: its first non-empty word, or none yet
        int lastEnd = segStart;     // the end of the last word that fit

        int i = segStart;
        while (i <= segEnd)
        {
            int wordStart = i;
            while (i < segEnd && text[i] != ' ') i++;
            int wordEnd = i;

            var candidate = textStart < 0 ? text[wordStart..wordEnd] : text[textStart..wordEnd];
            float w = MeasureRuns(candidate, style) / scale;
            if (!Fits(w, maxWidth) && textStart >= 0)
            {
                result.Add(new TextLine(lineStart, lastEnd));
                lineStart = wordStart;
                textStart = wordEnd > wordStart ? wordStart : -1;
            }
            else if (textStart < 0 && wordEnd > wordStart)
            {
                textStart = wordStart;
            }
            lastEnd = wordEnd;

            if (i >= segEnd) break;
            i++; // skip the space separator
        }
        if (textStart >= 0)
            result.Add(new TextLine(lineStart, lastEnd));
    }

    // One logical line, [segStart, segEnd), cut into the longest runs of
    // characters that fit, each at least one character long.
    private void WrapCharacters(ReadOnlySpan<char> text, int segStart, int segEnd, float maxWidth, FontStyle style,
                                float scale, ref Lines result)
    {
        int start = segStart;
        while (start < segEnd)
        {
            // Invariant: [start, lo) fits, or is the first character; nothing past hi does.
            int lo = NextCharacter(text, start, segEnd), hi = segEnd;
            while (lo < hi)
            {
                int mid = CharacterStart(text, (lo + hi + 1) / 2, lo);
                if (mid <= lo) mid = NextCharacter(text, lo, segEnd);
                float w = MeasureRuns(text[start..mid], style) / scale;
                if (Fits(w, maxWidth)) lo = mid;
                else hi = CharacterStart(text, mid - 1, lo);
            }
            result.Add(new TextLine(start, lo));
            start = lo;
        }
    }

    // Where the character after the one at `at` starts, no further than `end`.
    private static int NextCharacter(ReadOnlySpan<char> text, int at, int end)
    {
        at++;
        while (at < end && char.IsLowSurrogate(text[at])) at++;
        return at;
    }

    // The nearest character start at or before `at`, no earlier than `floor`.
    private static int CharacterStart(ReadOnlySpan<char> text, int at, int floor)
    {
        while (at > floor && at < text.Length && char.IsLowSurrogate(text[at])) at--;
        return at;
    }
}

/// <summary>
/// Where a text too wide for its width is broken: between words, or anywhere,
/// for text where words do not matter — a log, a path, a hash.
/// </summary>
public enum TextWrap { Words, Characters }

/// <summary>
/// A resolved font: its size in logical px, the face it is set in, and the
/// surface scale it is measured at. Everything <see cref="Fonts"/> needs to
/// measure in it, so nothing else has to be passed alongside.
///
/// <para>A font comes from the theme, never from a family's name:
/// <c>runtime.FontInScope()</c> gives the one a text built there would get. So
/// text is set only in faces the theme provides.</para>
/// </summary>
public readonly record struct Font
{
    internal Font(float size, FontFace face, float scale) => (Size, Face, Scale) = (size, face, scale);

    /// <summary>Its size in logical px.</summary>
    public float Size { get; }

    /// <summary>The face it is set in: the theme's family at the weight asked for, or the built-in Inter.</summary>
    public FontFace Face { get; }

    /// <summary>The surface scale it is measured at: logical px times this is physical px.</summary>
    public float Scale { get; }
}

/// <summary>
/// One face at one physical size, and what text asks of it: the strike that
/// measures and shapes, and its metrics. <see cref="Fonts.StyleOf(FontFace, float)"/>
/// keeps one per (face, size), under <see cref="Key"/>.
/// </summary>
internal sealed class FontStyle(ulong key, FontFace face, FontStrike strike, TextMetrics metrics)
{
    /// <summary>The face's id and the size's bits: what a cache key under this style holds of it.</summary>
    public ulong Key { get; } = key;
    public FontFace Face { get; } = face;
    public FontStrike Strike { get; } = strike;
    public TextMetrics Metrics { get; } = metrics;
}

/// <summary>
/// One line of a text broken at a width: the char range [Start, End) of the
/// text it was broken from, which holds the characters themselves. Linked to the
/// lines before and after it. A cached chain: read it, never relink it.
/// </summary>
public sealed class TextLine
{
    internal TextLine(int start, int end) { Start = start; End = end; }

    /// <summary>Where the line starts in its text.</summary>
    public int Start { get; }

    /// <summary>Where it ends: the offset after its last character.</summary>
    public int End { get; }

    public TextLine? Next { get; private set; }
    public TextLine? Prev { get; private set; }

    internal TextLine Append(TextLine line)
    {
        Next = line;
        line.Prev = this;
        return line;
    }
}

/// <summary>
/// A stretch of text set in one typeface: the char range [Start, End) of the
/// text it was split from, linked to the runs before and after it. A cached
/// chain: read it, never relink it.
/// </summary>
/// <summary>A run shaped for drawing: glyph ids, their positions from the run's origin, and its advance.</summary>
internal sealed class ShapedText(ushort[] glyphs, System.Numerics.Vector2[] positions, float width)
{
    public ushort[] Glyphs { get; } = glyphs;
    public System.Numerics.Vector2[] Positions { get; } = positions;
    public float Width { get; } = width;
}

internal sealed class TextRun(FontFace face, int start, int end)
{
    public FontFace Face { get; } = face;
    public int Start { get; } = start;
    public int End { get; } = end;
    public TextRun? Next { get; private set; }
    public TextRun? Prev { get; private set; }

    /// <summary>Links <paramref name="run"/> after this one and returns it.</summary>
    internal TextRun Append(TextRun run)
    {
        Next = run;
        run.Prev = this;
        return run;
    }
}
