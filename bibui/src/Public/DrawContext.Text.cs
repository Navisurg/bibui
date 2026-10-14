using System.Numerics;
using Bibui.Raster;
using Bibui.Renderer;

namespace Bibui;

/// <summary>
/// Text, icons and the backdrop blur. Split out because they are the draws that
/// need more than geometry and a paint: text resolves typefaces per run through
/// <see cref="Fonts"/>, icons parse SVG from the registry, and the blur
/// snapshots the surface. The paint machinery for those stays on DrawingContext
/// — this file owns the drawing.
/// </summary>
public readonly ref partial struct DrawContext
{
    /// <summary>
    /// Draws one line of <paramref name="text"/> with its baseline at
    /// <paramref name="baselineY"/>. Position is physical; <paramref name="size"/>
    /// is logical, as everywhere a font size is named.
    ///
    /// Takes the description rather than a paint, because one text is not one
    /// paint: a font without a glyph falls back, so the run split decides how
    /// many typefaces this draw needs.
    /// </summary>
    private void DrawRuns(ReadOnlySpan<char> text, float x, float baselineY,
        float size, Rgba color, string? family, int weight)
    {
        float cursor = x;
        for (var run = Fonts.SplitRuns(text, family, weight); run is not null; run = run.Next)
            cursor += Canvas.DrawText(text[run.Start..run.End], cursor, baselineY, _ctx.Font(run.Typeface, size), color);
    }

    /// <summary>Ascent and descent of the base font — one baseline for every run.</summary>
    public TextMetrics FontMetrics(float size, string? family = null, int weight = 400)
        => _ctx.Metrics(size, family, weight);

    /// <summary>
    /// The height one line of text occupies, in logical px: the font's own em box,
    /// ascent to descent, and nothing else. Successive lines of a paragraph are
    /// spaced <see cref="LineAdvance"/> apart.
    /// </summary>
    public float LineBox(float fontSize, string? family = null, int weight = 400)
        => Fonts.LineBox(fontSize, family, weight, Scale);

    /// <summary>
    /// Baseline-to-baseline distance between wrapped lines, in logical px — the
    /// leading that <see cref="LineBox"/> no longer bakes into a single line.
    /// </summary>
    public static float LineAdvance(float fontSize) => Bibui.Fonts.LineAdvance(fontSize);

    /// <summary>
    /// Total height of <paramref name="lines"/> stacked lines: the last line's em box
    /// plus one advance for every line above it. One line is exactly its em box.
    /// </summary>
    public float BlockHeight(int lines, float fontSize, string? family = null, int weight = 400)
        => Fonts.BlockHeight(lines, fontSize, family, weight, Scale);

    /// <summary>
    /// One line of <paramref name="text"/> in <paramref name="font"/>, its
    /// ascent at <paramref name="top"/>. What a <see cref="Text"/> is drawn with.
    /// </summary>
    internal void DrawTextTop(ReadOnlySpan<char> text, float x, float top, in Font font, ReadOnlySpan<char> color)
    {
        if (text.IsEmpty) return;
        var c = Color(color);
        float baselineY = (top * Scale) - FontMetrics(font.Size, font.Family, font.Weight).Ascent;
        DrawRuns(text, x * Scale, baselineY, font.Size, c, font.Family, font.Weight);
    }

    // An icon with no path: one glyph, centred in the logical rect.
    private void DrawGlyphCentered(ReadOnlySpan<char> text, float rectX, float rectY, float rectW, float rectH,
        float fontSize, Rgba color)
    {
        const int weight = 600;
        var metrics = FontMetrics(fontSize, null, weight);
        float textWidth = _ctx.MeasureRuns(text, fontSize, null, weight);
        float textHeight = metrics.Descent - metrics.Ascent;
        float textX = (rectX * Scale) + ((rectW * Scale) - textWidth) / 2f;
        float baselineY = (rectY * Scale) + ((rectH * Scale) - textHeight) / 2f - metrics.Ascent;

        DrawRuns(text, textX, baselineY, fontSize, color, null, weight);
    }

    /// <summary>
    /// Draws a Lucide-style icon by name (24×24 viewBox, stroke-based) centered in
    /// the given logical rect, scaled from 24px to <paramref name="size"/>.
    ///
    /// An unknown or null name draws a fallback glyph — the uppercase first letter,
    /// or "·" if empty — so the layout slot is never silently blank and a shipped
    /// app does not fall over. An unknown name is still a mistake: check one that
    /// comes from data with <see cref="Icon.IsKnown"/>.
    /// </summary>
    public void DrawIcon(string? iconName, float cx, float cy, float size, ReadOnlySpan<char> color)
        => DrawIcon(iconName, cx, cy, size, Color(color));

    public void DrawIcon(string? iconName, float cx, float cy, float size, Rgba color)
    {
        var srcPath = SvgPathParser.Parse(IconRegistry.Get(iconName));
        if (srcPath is null)
        {
            var glyph = string.IsNullOrEmpty(iconName)
                ? new System.Text.Rune('·')
                : System.Text.Rune.TryCreate(char.ToUpperInvariant(iconName[0]), out var r) ? r : System.Text.Rune.ReplacementChar;
            Span<char> chars = stackalloc char[2];
            int length = glyph.EncodeToUtf16(chars);
            DrawGlyphCentered(chars[..length], cx - size / 2f, cy - size / 2f, size, size, size * 0.75f, color);
            return;
        }

        // Scale from the 24×24 viewbox to the target size. Position is applied by
        // the canvas rather than baked into the path, so the cached geometry is the
        // same for every instance of an icon at a given size — bake the translation
        // in and every on-screen position would be its own entry.
        float s = size / 24f * Scale;

        // The parse is process-wide and backend-free, so the icon is replayed into
        // a path of this frame's backend at its own 24-unit scale and then mapped
        // to the target size — in that order, so the scaled geometry is exactly
        // what scaling the parsed path gives.
        var scaledPath = Path(
            CacheKey.Of([CacheKey.Bits(s)], CacheKey.OfString(iconName)),
            (srcPath, s, backend: _ctx.Backend),
            static (p, path) =>
            {
                using var unscaled = p.backend.CreatePath();
                p.srcPath.ReplayInto(unscaled);
                path.AddPath(unscaled, Matrix3x2.CreateScale(p.s, p.s));
            });

        float strokeW = MathF.Max(1f, 1.5f * (size / 18f) * Scale);
        var paint = Paint(PaintSpec.Stroke(color, strokeW) with
        {
            StrokeCap = StrokeCap.Round,
            StrokeJoin = StrokeJoin.Round,
        });

        PushTranslation(cx - size / 2f, cy - size / 2f);
        DrawPath(scaledPath, paint);
        PopTranslation();
    }

    /// <summary>
    /// Blurs what is already painted in this rect, so a translucent fill over it
    /// reads as frosted glass. Call before painting the node's own content.
    ///
    /// An operation rather than access to the surface: the snapshot it needs is a
    /// picture of the frame so far, so there is no key that could name it and no
    /// way to cache it. Exposing the surface would hand callers a factory for
    /// exactly the uncached native objects the rest of this API exists to avoid.
    /// </summary>
    public void BlurBackdrop(float x, float y, float w, float h, float sigma)
    {
        if (sigma <= 0f || !_ctx.CanBlurBackdrop)
        {
            return;
        }

        // Snapshot a region padded by ~2 sigma so the blur kernel has real neighbouring
        // pixels to sample at the edges; otherwise the snapshot's own edges read as
        // transparent and the border fades. The paint is then clipped back to the exact
        // unpadded rect so the extra margin never bleeds onto neighbouring content.
        float pad = sigma * 2f;
        float padX = MathF.Max(0f, x - pad);
        float padY = MathF.Max(0f, y - pad);
        float padRight = x + w + pad;
        float padBottom = y + h + pad;
        if (SurfaceWidth > 0f)
        {
            padRight = MathF.Min(padRight, SurfaceWidth);
        }
        if (SurfaceHeight > 0f)
        {
            padBottom = MathF.Min(padBottom, SurfaceHeight);
        }

        var physSubset = new PixelRect(
            (int)MathF.Round(padX * Scale), (int)MathF.Round(padY * Scale),
            (int)MathF.Round(padRight * Scale), (int)MathF.Round(padBottom * Scale));
        if (physSubset.Width <= 0 || physSubset.Height <= 0)
        {
            return;
        }

        var clipRect = new RectF(x * Scale, y * Scale, (x + w) * Scale, (y + h) * Scale);
        Canvas.DrawBackdropBlur(physSubset, clipRect, sigma * Scale);
    }
}
