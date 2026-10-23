using System.Numerics;
using Bibui.Core.Draw;

namespace Bibui.Core;

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
    /// Draws one line of <paramref name="text"/> in <paramref name="style"/> with
    /// its baseline at <paramref name="baselineY"/>, in physical px: its glyphs
    /// walked straight into the list, so the backend draws exactly what was
    /// measured.
    /// </summary>
    private void DrawRuns(ReadOnlySpan<char> text, float x, float baselineY, FontStyle style, Rgba color)
    {
        var glyphs = List.ReserveGlyphs(text.Length, out int glyphStart);
        var points = List.ReservePoints(text.Length, out int pointStart);
        int count = style.Strike.Glyphs(text, glyphs, points, out _);
        List.ReturnGlyphs(text.Length - count);
        List.ReturnPoints(text.Length - count);
        PushGlyphs(style, x, baselineY, color, glyphStart, count, pointStart);
    }

    // The op for a run whose glyphs and positions are in the list already.
    private void PushGlyphs(FontStyle style, float x, float baselineY, Rgba color, int glyphStart, int count, int pointStart)
    {
        ref var op = ref List.Push(DrawOpKind.Glyphs);
        op.Resource = List.AddFace(style.Face);
        (op.A, op.B, op.C, op.Color) = (x, baselineY, style.Strike.Size, color);
        (op.Start, op.Count, op.Start2) = (glyphStart, count, pointStart);
    }

    /// <summary>
    /// Ascent and descent of <paramref name="font"/> at this canvas's scale, in
    /// physical px: one baseline for every run of a text.
    /// </summary>
    public TextMetrics FontMetrics(in Font font) => Fonts.StyleOf(font, Scale).Metrics;

    /// <summary>
    /// Baseline-to-baseline distance between wrapped lines, in logical px — the
    /// leading that <see cref="Fonts.LineHeight"/> does not bake into a single line.
    /// </summary>
    public static float LineAdvance(float fontSize) => Bibui.Core.Fonts.LineAdvance(fontSize);

    /// <summary>
    /// <paramref name="text"/> in <paramref name="font"/>, its ascent at
    /// <paramref name="top"/>: drawn from the run layout walked it into this
    /// frame, copied into the list, or walked again when it has none, or one
    /// walked in another style.
    /// </summary>
    internal void DrawTextTop(Text text, float x, float top, in Font font, ReadOnlySpan<char> color)
    {
        if (text.Content.IsEmpty) return;
        var style = Fonts.StyleOf(font, Scale);
        var rgba = Color(color);
        float baselineY = top * Scale - style.Metrics.Ascent;
        if (text.RunFrame != Fonts.RunFrame || text.RunStyle != style)
        {
            DrawRuns(text.Content, x * Scale, baselineY, style, rgba);
            return;
        }
        PushGlyphs(style, x * Scale, baselineY, rgba,
            List.AddGlyphs(Fonts.RunGlyphs(text)), text.RunCount, List.AddPoints(Fonts.RunPoints(text)));
    }

    /// <summary>
    /// One line of <paramref name="text"/> in <paramref name="font"/>, drawn as a
    /// <see cref="Text"/> is: at (<paramref name="x"/>, <paramref name="top"/>) in
    /// physical px, its ascent at the top. Measure it with <see cref="Fonts"/> and
    /// the same font.
    /// </summary>
    public void DrawText(ReadOnlySpan<char> text, float x, float top, in Font font, Rgba color)
    {
        if (text.IsEmpty) return;
        var style = Fonts.StyleOf(font, Scale);
        DrawRuns(text, x, top - style.Metrics.Ascent, style, color);
    }

    // An icon with no path: one glyph in the built-in Inter's SemiBold, centred
    // in the logical rect.
    private void DrawGlyphCentered(ReadOnlySpan<char> text, float rectX, float rectY, float rectW, float rectH,
        float fontSize, Rgba color)
    {
        var style = Fonts.StyleOf(Fonts.BuiltIn(600), fontSize * Scale);
        var metrics = style.Metrics;
        float textWidth = style.Strike.Measure(text);
        float textHeight = metrics.Descent - metrics.Ascent;
        float textX = (rectX * Scale) + ((rectW * Scale) - textWidth) / 2f;
        float baselineY = (rectY * Scale) + ((rectH * Scale) - textHeight) / 2f - metrics.Ascent;

        DrawRuns(text, textX, baselineY, style, color);
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
    internal void DrawIcon(string? iconName, float cx, float cy, float size, Rgba color)
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

        // The parse is process-wide and backend-free. The icon is added at its own
        // 24-unit scale and mapped to the target size, so a renderer builds the
        // parsed path and then scales it, in that order: the scaled geometry is
        // exactly what scaling the parsed path gives.
        var scaledPath = Path(
            CacheKey.Of([CacheKey.Bits(s)], CacheKey.OfString(iconName)),
            (srcPath, s),
            static (p, path) => path.AddPath(p.srcPath, Matrix3x2.CreateScale(p.s, p.s)));

        float strokeW = MathF.Max(1f, 1.5f * (size / 18f) * Scale);
        var paint = new Paint { Stroke = color, StrokeWidth = strokeW, Cap = StrokeCap.Round, Join = StrokeJoin.Round };

        PushTransform(Matrix3x2.CreateTranslation((cx - size / 2f) * Scale, (cy - size / 2f) * Scale));
        DrawPath(scaledPath, paint);
        PopTransform();
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
    internal void BlurBackdrop(float x, float y, float w, float h, float sigma)
    {
        if (sigma <= 0f)
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
        ref var op = ref List.Push(DrawOpKind.BackdropBlur);
        (op.Region, op.Rect, op.A) = (physSubset, clipRect, sigma * Scale);
    }
}
