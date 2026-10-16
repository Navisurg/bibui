using Bibui.Theming;
using Bibui.Renderer;
using Bibui.Renderer.Components;
using Bibui.FreeType;
using Bibui.TextEdit;

namespace Bibui;

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
    private readonly ResourceCache<MetricsKey, TextMetrics> _metrics;
    private readonly ResourceCache<MeasureKey, float> _measures;
    private readonly ResourceCache<WrapKey, TextLine> _lines;

    /// <summary>Fonts on a text engine of their own, which the renderer owns with them.</summary>
    internal Fonts()
    {
        _provider = new FontProvider(_engine, Create<RunsKey, TextRun>());
        _metrics = Create<MetricsKey, TextMetrics>();
        _measures = Create<MeasureKey, float>();
        _lines = Create<WrapKey, TextLine>();
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
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        _engine.Dispose();
    }

    // ── Typefaces and runs ───────────────────────────────────────────────────

    /// <summary>The face for a family and weight; see <see cref="FontProvider.Resolve"/>.</summary>
    internal FontFace Resolve(string? family, int weight) => _provider.Resolve(family, weight);

    /// <summary>
    /// The face bibui draws <paramref name="family"/> at <paramref name="weight"/>
    /// with: the registered font, else the built-in Inter. For a draw that hands
    /// a raster canvas text directly.
    /// </summary>
    public FontFace Face(string? family = null, int weight = 400) => _provider.Resolve(family, weight);

    /// <summary>The default family at weight 400.</summary>
    internal FontFace Regular => _provider.Regular;

    /// <summary>
    /// <paramref name="text"/> split into runs of one face each. With no fallback
    /// per character yet, that is one run in the base face. The first run, the
    /// rest linked from it; null for empty text.
    /// </summary>
    internal TextRun? SplitRuns(ReadOnlySpan<char> text, string? family, int weight)
        => _provider.SplitRuns(text, family, weight);

    // ── Metrics ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Ascent and descent for the *base* font at this size. Every run of a draw
    /// shares one baseline, so this must never be asked per run — a fallback
    /// font's own ascent would sit its glyphs off the line the rest sit on.
    /// </summary>
    internal TextMetrics Metrics(float logicalFontSize, string? family, int weight, float scale)
        => _metrics.FromKey(
            MetricsKey.For(logicalFontSize * scale, family, weight),
            (self: this, logicalFontSize, family, weight, scale),
            static p =>
            {
                var m = p.self.Resolve(p.family, p.weight).Strike(p.logicalFontSize * p.scale).Metrics;
                return new TextMetrics(m.Ascent, m.Descent, m.Leading);
            });

    // ── A text node's font ───────────────────────────────────────────────────

    /// <summary>The font <paramref name="text"/> is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontOf(Text text, ThemeTokens theme, float scale)
        => FontOf(text.Size, text.Family, text.Weight, theme, scale);

    /// <summary>The font a text with these settings is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontOf(FontSize size, FontSlot family, FontWeight weight, ThemeTokens theme, float scale)
        => new(size.In(theme), FontResolution.ResolveFamily(family, theme), FontResolution.ResolveWeight(weight, theme), scale);

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
    internal float LineBox(float fontSize, string? family, int weight, float scale)
    {
        var m = Metrics(fontSize, family, weight, scale);
        return (m.Descent - m.Ascent) / scale;
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
    internal float BlockHeight(int lines, float fontSize, string? family, int weight, float scale)
        => LineBox(fontSize, family, weight, scale) + MathF.Max(0, lines - 1) * LineAdvance(fontSize);

    /// <inheritdoc cref="LineBox(float, string?, int, float)"/>
    internal float LineBox(in Font font) => LineBox(font.Size, font.Family, font.Weight, font.Scale);

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

    /// <inheritdoc cref="BlockHeight(int, float, string?, int, float)"/>
    internal float BlockHeight(int lines, in Font font) => BlockHeight(lines, font.Size, font.Family, font.Weight, font.Scale);

    // ── A text node's size ───────────────────────────────────────────────────

    /// <summary>
    /// How large <paramref name="text"/> is: as wide as its chars on one line,
    /// and one line tall. A text is one line whatever room it is given; what
    /// breaks into lines is composed from texts, as a multi-line field is.
    /// </summary>
    internal Size Measure(Text text, in Font font)
        => new(Measure(text.Content, font), LineHeight(font));

    // ── Public: measuring in a Font ──────────────────────────────────────────

    /// <summary>
    /// The width of <paramref name="text"/> on one line in
    /// <paramref name="font"/>, in logical px. The same number layout gives a
    /// text with that content, from the same cache.
    /// </summary>
    public float Measure(ReadOnlySpan<char> text, in Font font)
        => MeasureText(text, font.Size, font.Scale, font.Family, font.Weight);

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
    /// Width in physical px, summed over the run split. Measuring the whole
    /// text against one typeface would be wrong wherever fallback applies.
    /// </summary>
    internal float MeasureRuns(ReadOnlySpan<char> text, float logicalFontSize, string? family, int weight, float scale)
    {
        float total = 0f;
        for (var run = SplitRuns(text, family, weight); run is not null; run = run.Next)
            total += run.Face.Strike(logicalFontSize * scale).Measure(text[run.Start..run.End]);
        return total;
    }

    /// <summary>The width of <paramref name="text"/> in logical px.</summary>
    private float MeasureText(ReadOnlySpan<char> text, float logicalFontSize, float scale, string? family, int weight)
    {
        if (text.IsEmpty) return 0f;
        var key = MeasureKey.For(text, logicalFontSize * scale, family, weight);
        return _measures.TryGet(key, out float width)
            ? width
            : _measures.Add(key, MeasureRuns(text, logicalFontSize, family, weight, scale) / scale);
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
        var key = WrapKey.For(text, width, font.Size * font.Scale, font.Scale, font.Family, font.Weight, wrap);
        return _lines.TryGet(key, out var lines) ? lines : _lines.Add(key, WrapUncached(text, width, font, wrap));
    }

    private TextLine WrapUncached(ReadOnlySpan<char> text, float maxWidth, in Font font, TextWrap wrap)
    {
        var result = new Lines();

        int idx = 0;
        while (idx <= text.Length)
        {
            int segStart = idx;
            while (idx < text.Length && text[idx] != '\n' && text[idx] != '\r') idx++;
            int segEnd = idx;

            var before = result.Last;
            if (wrap == TextWrap.Characters) WrapCharacters(text, segStart, segEnd, maxWidth, font, ref result);
            else WrapSegment(text, segStart, segEnd, maxWidth, font, ref result);
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
    private void WrapSegment(ReadOnlySpan<char> text, int segStart, int segEnd, float maxWidth, in Font font,
                             ref Lines result)
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
            float w = MeasureRuns(candidate, font.Size, font.Family, font.Weight, font.Scale) / font.Scale;
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
    private void WrapCharacters(ReadOnlySpan<char> text, int segStart, int segEnd, float maxWidth, in Font font,
                                ref Lines result)
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
                float w = MeasureRuns(text[start..mid], font.Size, font.Family, font.Weight, font.Scale) / font.Scale;
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
/// A resolved font: the size step it came from, its size in logical px, the
/// family and weight it is set in, the letter-spacing in px, and the surface
/// scale it is measured at. Everything <see cref="Fonts"/> needs to measure in
/// it, so nothing else has to be passed alongside.
/// </summary>
public readonly record struct Font(float Size, string? Family, int Weight, float Scale);

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
