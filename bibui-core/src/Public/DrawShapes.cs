using System.Numerics;
using Bibui.Core.Draw;

namespace Bibui.Core;

/// <summary>
/// Shapes built from <see cref="DrawContext"/>'s primitives: usage code, not a
/// privilege of the core. Each is written against the context's public API
/// alone (rects, paths, lines, the path cache and paints), so a component can
/// write its own the same way. The painter decorates boxes with these, and the
/// spinner turns with <see cref="DrawArc"/>.
///
/// <para>Positions and sizes are logical px, as a component is laid out in;
/// each converts to the physical px the primitives take.</para>
/// </summary>
public static class DrawShapes
{
    // Discriminates cached geometry whose keys would otherwise be the same few
    // floats: the path cache is one key space, so unrelated shapes must not alias.
    private enum PathKind { CheckMark, Arc }

    /// <summary>A rounded rect, filled and/or stroked; a plain rect is radius 0.</summary>
    public static void DrawRRect(this DrawContext ctx, float x, float y, float w, float h, float radius,
        Rgba? fillColor, Rgba? strokeColor, float? strokeWidth)
    {
        ctx.DrawRect(ctx.Physical(x, y, w, h), radius * ctx.Scale, new Paint
        {
            Fill = fillColor,
            Stroke = strokeColor,
            StrokeWidth = (strokeWidth ?? 1f) * ctx.Scale,
        });
    }

    /// <summary>
    /// A soft shadow: the rect offset, grown by <paramref name="spread"/>, and
    /// blurred by <paramref name="blur"/> (a radius, about two sigmas).
    /// </summary>
    public static void DrawShadow(this DrawContext ctx, float x, float y, float w, float h, float radius,
        float offsetX, float offsetY, float blur, float spread, Rgba color)
    {
        float s = ctx.Scale;
        float px = (x + offsetX - spread) * s;
        float py = (y + offsetY - spread) * s;
        float pw = (w + 2f * spread) * s;
        float ph = (h + 2f * spread) * s;
        ctx.DrawRect(new RectF(px, py, px + pw, py + ph), MathF.Max(0f, radius + spread) * s,
            new Paint { Fill = color, Blur = MathF.Max(0.01f, blur * s * 0.5f) });
    }

    /// <summary>A straight edge of <paramref name="width"/>, dashed in proportion to its width when asked.</summary>
    public static void DrawEdge(this DrawContext ctx, float x1, float y1, float x2, float y2, float width, Rgba color, bool dashed = false)
    {
        float s = ctx.Scale;
        float dash = MathF.Max(2f, width * 2.5f) * s;
        ctx.DrawLine(x1 * s, y1 * s, x2 * s, y2 * s, new Paint
        {
            Stroke = color,
            StrokeWidth = width * s,
            Dash = dashed ? new DashPattern(dash, dash) : null,
        });
    }

    /// <summary>A check mark in the rect, stroked with round ends.</summary>
    public static void DrawCheckMark(this DrawContext ctx, float x, float y, float width, float height, Rgba strokeColor, float strokeWidth)
    {
        // Geometry at the origin and sized in physical px; a translation places
        // it, so every check mark of a size is one cached path, not one per row.
        float s = ctx.Scale, pw = width * s, ph = height * s;
        var path = ctx.Path(
            CacheKey.Of([CacheKey.Bits(pw), CacheKey.Bits(ph), (int)PathKind.CheckMark]),
            (pw, ph),
            static (p, path) =>
            {
                path.MoveTo(p.pw * 0.22f, p.ph * 0.55f);
                path.LineTo(p.pw * 0.42f, p.ph * 0.74f);
                path.LineTo(p.pw * 0.78f, p.ph * 0.28f);
            });
        ctx.PushTransform(Matrix3x2.CreateTranslation(x * s, y * s));
        ctx.DrawPath(path, new Paint { Stroke = strokeColor, StrokeWidth = strokeWidth * s, Cap = StrokeCap.Round, Join = StrokeJoin.Round });
        ctx.PopTransform();
    }

    /// <summary>An arc of the circle at (<paramref name="cx"/>, <paramref name="cy"/>), stroked with round ends; angles in degrees, clockwise from 3 o'clock.</summary>
    public static void DrawArc(this DrawContext ctx, float cx, float cy, float radius, float strokeWidth, Rgba color,
        float startAngle = -90f, float sweepAngle = 270f)
    {
        float s = ctx.Scale, r = radius * s;
        var path = ctx.Path(
            CacheKey.Of([CacheKey.Bits(r), CacheKey.Bits(startAngle), CacheKey.Bits(sweepAngle), (int)PathKind.Arc]),
            (r, startAngle, sweepAngle),
            static (p, path) => path.AddArc(new RectF(0f, 0f, p.r * 2f, p.r * 2f), p.startAngle, p.sweepAngle));
        ctx.PushTransform(Matrix3x2.CreateTranslation((cx - radius) * s, (cy - radius) * s));
        ctx.DrawPath(path, new Paint { Stroke = color, StrokeWidth = strokeWidth * s, Cap = StrokeCap.Round, Join = StrokeJoin.Miter });
        ctx.PopTransform();
    }

    // Chart geometry is built fresh into the scratch path: the points *are* the
    // content, so a cache key would have to hash all of them, and a series that
    // moves every frame would miss every frame.

    /// <summary>A line through <paramref name="points"/>, with round joins and ends.</summary>
    public static void DrawPolyline(this DrawContext ctx, ReadOnlySpan<(float X, float Y)> points, Rgba color, float strokeWidth)
    {
        if (points.Length < 2) return;
        float s = ctx.Scale;
        var path = ctx.ScratchPath();
        path.MoveTo(points[0].X * s, points[0].Y * s);
        for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X * s, points[i].Y * s);
        ctx.DrawPath(path, new Paint { Stroke = color, StrokeWidth = strokeWidth * s, Cap = StrokeCap.Round, Join = StrokeJoin.Round });
    }

    /// <summary>The area under <paramref name="points"/> down to <paramref name="baselineY"/>, filled.</summary>
    public static void DrawFilledPath(this DrawContext ctx, ReadOnlySpan<(float X, float Y)> points, float baselineY, Rgba fillColor)
    {
        if (points.Length < 2) return;
        ctx.DrawPath(ClosedArea(ctx, points, baselineY), Paint.Filled(fillColor));
    }

    /// <summary>The area under <paramref name="points"/>, filled with <paramref name="color"/> fading out from <paramref name="plotTopY"/> to the baseline.</summary>
    public static void DrawFilledPathGradient(this DrawContext ctx, ReadOnlySpan<(float X, float Y)> points, float plotTopY, float baselineY, Rgba color)
    {
        if (points.Length < 2) return;
        var path = ClosedArea(ctx, points, baselineY);
        var shader = RasterShader.Linear(
            new Vector2(0f, plotTopY * ctx.Scale),
            new Vector2(0f, baselineY * ctx.Scale),
            [color.WithAlpha(0x55), color.WithAlpha(0x00)],
            []);
        ctx.DrawPath(path, new Paint { Shader = shader });
    }

    // The curve, then a straight baseline back to the start, in the scratch path.
    private static VectorPath ClosedArea(DrawContext ctx, ReadOnlySpan<(float X, float Y)> points, float baselineY)
    {
        float s = ctx.Scale;
        var path = ctx.ScratchPath();
        path.MoveTo(points[0].X * s, points[0].Y * s);
        for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X * s, points[i].Y * s);
        path.LineTo(points[^1].X * s, baselineY * s);
        path.LineTo(points[0].X * s, baselineY * s);
        path.Close();
        return path;
    }

    /// <summary>
    /// A rect with a radius for each corner (<see cref="CornerRadii"/>), filled and stroked: a
    /// uniform one is <see cref="DrawRRect(DrawContext, float, float, float, float, float, Rgba?, Rgba?, float?)"/>,
    /// any other one closed path.
    /// </summary>
    public static void DrawRRect(this DrawContext ctx, float x, float y, float w, float h, CornerRadii radii,
        Rgba? fillColor, Rgba? strokeColor, float? strokeWidth)
    {
        if (radii.IsUniform) { ctx.DrawRRect(x, y, w, h, radii.TopLeft, fillColor, strokeColor, strokeWidth); return; }
        var path = new VectorPath();
        radii.Scaled(ctx.Scale).AddTo(path, ctx.Physical(x, y, w, h));
        ctx.DrawPath(path, new Paint { Fill = fillColor, Stroke = strokeColor, StrokeWidth = (strokeWidth ?? 1f) * ctx.Scale });
    }

    /// <summary>
    /// A rounded rect filled with a gradient: <paramref name="stops"/> of theme
    /// hex colours, from <paramref name="start"/> to <paramref name="end"/> in the
    /// rect's own 0..1 space, and an optional stroke.
    /// </summary>
    public static void DrawRRectGradient(this DrawContext ctx, float x, float y, float w, float h, float radius,
        IReadOnlyList<(float Stop, string Color)> stops,
        (float X, float Y) start, (float X, float Y) end,
        Rgba? strokeColor = null, float? strokeWidth = null, GradientType type = GradientType.Linear, CornerRadii? radii = null)
    {
        if (stops.Count == 0) return;
        float s = ctx.Scale;
        var rect = ctx.Physical(x, y, w, h);
        float pw = w * s, ph = h * s;
        var from = new Vector2(rect.Left + start.X * pw, rect.Top + start.Y * ph);
        var to = new Vector2(rect.Left + end.X * pw, rect.Top + end.Y * ph);

        var colors = new Rgba[stops.Count];
        var positions = new float[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            colors[i] = DrawContext.Color(stops[i].Color);
            positions[i] = stops[i].Stop;
        }

        var shader = type == GradientType.Radial
            ? RasterShader.Radial(from, MathF.Max(0.01f, MathF.Sqrt(MathF.Pow(to.X - from.X, 2) + MathF.Pow(to.Y - from.Y, 2))), colors, positions)
            : RasterShader.Linear(from, to, colors, positions);

        var paint = new Paint { Shader = shader, Stroke = strokeColor, StrokeWidth = (strokeWidth ?? 1f) * s };
        if (radii is { IsUniform: false } corners)
        {
            var path = new VectorPath();
            corners.Scaled(s).AddTo(path, rect);
            ctx.DrawPath(path, paint);
            return;
        }
        ctx.DrawRect(rect, (radii?.TopLeft ?? radius) * s, paint);
    }
}
