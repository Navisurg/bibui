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

    // The op for a run whose glyphs and positions are in the list already.
    //
    // NOTE(positions): a run's positions are where each glyph sits from the
    // run's start, and here they follow from its face, size and glyphs alone.
    // The walk (TextLayer.RunsFromChars) puts the first glyph at 0 and moves the pen by
    // each glyph's advance, which is the strike's for that glyph: one number per
    // glyph of a font at a pixel size. "Hi!" at 16 px:
    //
    //     glyphs:     H         i          !
    //     positions:  (0, 0)    (11.6, 0)  (16.0, 0)
    //                 └─ +11.6 ─┘└─ +4.4 ──┘   each step the previous glyph's advance
    //
    // So the same face, size and glyphs always have the same positions, and the
    // Skia renderers rely on that: they key the text blob they keep for a run by
    // face, size and glyphs, without the positions (SkiaOps.RunKey), which reads
    // a fifth of the bytes a key with them would. Shaping breaks it: kerning
    // pulls "AV" together, and HarfBuzz places marks and the parts of an emoji
    // sequence, so the positions depend on more than the advances. A run made
    // that way (fallback runs shaped by HarfBuzz would be) must not reach this op with only
    // its glyphs to tell it apart: its positions go back into the renderers' key.
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
    public TextMetrics FontMetrics(in Font font) => TextLayer.StyleFromFont(Fonts, font, Scale).Metrics;

    /// <summary>
    /// Baseline-to-baseline distance between wrapped lines, in logical px — the
    /// leading that <see cref="Fonts.LineHeight"/> does not bake into a single line.
    /// </summary>
    public static float LineAdvance(float fontSize) => TextLayer.LineAdvanceFromSize(fontSize);

    /// <summary>
    /// One line of <paramref name="text"/> in <paramref name="font"/>, drawn as a
    /// <see cref="Text"/> is: walked as layout walks a text (TextLayer.RunsFromChars)
    /// and drawn from its runs, at (<paramref name="x"/>, <paramref name="top"/>) in
    /// physical px, its ascent at the top. For a draw callback's own text.
    /// </summary>
    public void DrawText(ReadOnlySpan<char> text, float x, float top, in Font font, Rgba color)
    {
        if (text.IsEmpty) return;
        var style = TextLayer.StyleFromFont(Fonts, font, Scale);
        DrawRuns(TextLayer.RunsFromChars(Fonts, text, style), x, top - style.Metrics.Ascent, color);
    }

    /// <summary>
    /// <paramref name="text"/> in <paramref name="font"/>, its ascent at
    /// <paramref name="top"/>: drawn from the runs layout walked it into this
    /// frame, or walked again when it has none, or ones walked in another style.
    /// </summary>
    internal void DrawTextTop(Text text, float x, float top, in Font font, ReadOnlySpan<char> color)
    {
        if (text.Content.IsEmpty) return;
        var style = TextLayer.StyleFromFont(Fonts, font, Scale);
        var runs = TextLayer.RunsAreCurrent(Fonts, text.Runs, style) ? text.Runs : TextLayer.RunsFromChars(Fonts, text.Content, style);
        DrawRuns(runs, x * Scale, top * Scale - style.Metrics.Ascent, Color(color));
    }

    // The runs a text was walked into, its baseline at (x, baselineY) in physical
    // px. A text's glyphs are one slice across its runs: they go into the list in
    // one copy, and each run is an op over its part of them, in its own style,
    // from its own pen. A run with no glyphs (the text's own style, when it starts
    // in another face) draws nothing.
    private void DrawRuns(RunHandle handle, float x, float baselineY, Rgba color)
    {
        var fonts = Fonts;
        var runs = TextLayer.RunsOf(fonts, handle);
        if (runs.IsEmpty) return;
        int first = runs[0].GlyphStart;
        ref readonly var last = ref runs[^1];
        int total = last.GlyphStart + last.GlyphCount - first;
        int glyphShift = List.AddGlyphs(fonts.RunGlyphs.AsSpan(first, total)) - first;
        int pointShift = List.AddPoints(fonts.RunPoints.AsSpan(first, total)) - first;
        foreach (ref readonly var run in runs)
        {
            if (run.GlyphCount == 0) continue;
            PushGlyphs(run.Style, x + run.X, baselineY, color, run.GlyphStart + glyphShift, run.GlyphCount, run.GlyphStart + pointShift);
        }
    }

    // An icon with no path: one glyph in the built-in Inter's SemiBold, centred
    // in the logical rect.
    private void DrawGlyphCentered(ReadOnlySpan<char> text, float rectX, float rectY, float rectW, float rectH,
        float fontSize, Rgba color)
    {
        var style = TextLayer.StyleFromFaceSize(Fonts, TextLayer.FaceBuiltIn(Fonts, 600), fontSize * Scale);
        var metrics = style.Metrics;
        float textWidth = TextLayer.WidthFromChars(Fonts, text, style, 1f);
        float textHeight = metrics.Descent - metrics.Ascent;
        float textX = (rectX * Scale) + ((rectW * Scale) - textWidth) / 2f;
        float baselineY = (rectY * Scale) + ((rectH * Scale) - textHeight) / 2f - metrics.Ascent;

        DrawRuns(TextLayer.RunsFromChars(Fonts, text, style), textX, baselineY, color);
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
