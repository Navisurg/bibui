using System.Numerics;
using Bibui.Raster;
using Bibui.Renderer;
using Bibui.Theming;

namespace Bibui;

/// <summary>
/// A complete description of a paint's own fields. This *is* the cache key — every
/// field that changes the object is here, so a new requirement is a new field
/// rather than a new key type.
///
/// Effects (a gradient, a blur, a dash) are not part of the spec: they ride
/// beside it in a <see cref="RasterPaint"/>, which <see cref="DrawContext.Paint"/>
/// builds, and the raster backend keys its own objects on both.
/// </summary>
public readonly record struct PaintSpec
{
    public Rgba Color { get; init; }
    public PaintStyle Style { get; init; }
    public float StrokeWidth { get; init; }
    public StrokeCap StrokeCap { get; init; }
    public StrokeJoin StrokeJoin { get; init; }
    public bool IsAntialias { get; init; }
    public FilterQuality FilterQuality { get; init; }

    public static PaintSpec Fill(Rgba color) =>
        new() { Color = color, Style = PaintStyle.Fill, IsAntialias = true };

    public static PaintSpec Stroke(Rgba color, float width) =>
        new() { Color = color, Style = PaintStyle.Stroke, StrokeWidth = width, IsAntialias = true };
}

/// <summary>
/// Discriminates cached geometry whose keys would otherwise be the same few
/// floats. The path table is one key space, so unrelated shapes must not alias.
/// </summary>
internal enum PathKind { CheckMark, Arc, Polyline, FilledPath, Edge }

/// <summary>
/// What a <c>Box.Draw</c> callback is handed: the canvas, where it may paint,
/// and cached constructors for the paths and paints it might need.
///
/// Nothing here is constructed twice. A path lives in the renderer's caches, and
/// a paint is a value the raster backend resolves against its own — both evicted
/// by the same frame clock as the renderer's — so a custom component costs what
/// a built-in one costs, instead of allocating and destroying native objects on
/// every frame.
///
/// One vocabulary, the raster backend's (<see cref="IRasterBackend"/>): bibui's
/// value types (<see cref="Rgba"/>, <see cref="RectF"/>, Vector2,
/// <see cref="PaintSpec"/>, <see cref="RasterPaint"/>) and the backend's handles
/// (<see cref="RasterPath"/>, <see cref="RasterImage"/>). Nothing here is
/// SkiaSharp, so a component written against it draws on any backend. Code that
/// wants Skia itself can still reach it through the Skia backend's own types
/// (<c>(ctx.Canvas as SkiaRasterCanvas)?.Native</c>), and is then tied to it.
///
/// A ref struct so it cannot outlive the callback. The objects it hands back
/// are ordinary references, so do not store them past the call: they are valid
/// for this frame and are recycled, not owned by you.
/// </summary>
public readonly ref partial struct DrawContext
{
    private readonly DrawingContext _ctx;
    private RenderCaches _caches => _ctx.Caches;
    private Fonts Fonts => _ctx.Fonts;

    /// <summary>What this wraps, for the renderer to keep where a ref struct cannot go.</summary>
    internal DrawingContext Drawing => _ctx;

    internal DrawContext(DrawingContext ctx, RectF rect)
    {
        _ctx = ctx;
        Rect = rect;
    }

    /// <summary>
    /// The raw canvas, in physical pixels. The Draw* methods below resolve theme
    /// colours, scale and cached geometry for you; what is drawn here is drawn
    /// as given, and the raster backend sees it like any other draw.
    /// </summary>
    public IRasterCanvas Canvas => _ctx.Canvas;

    /// <summary>The raster backend painting this frame — for paths and images a primitive takes.</summary>
    public IRasterBackend Backend => _ctx.Backend;

    /// <summary>This node's paint rect, in physical pixels.</summary>
    public RectF Rect { get; }

    /// <summary>
    /// This node's paint rect in logical pixels — the units the composed blocks
    /// below take. Offered because a client that never touches a primitive still
    /// needs to know where it may paint, and dividing <see cref="Rect"/> back
    /// down is the first thing it would otherwise write.
    /// </summary>
    public RectF Logical => new(
        Rect.Left / Scale, Rect.Top / Scale, Rect.Right / Scale, Rect.Bottom / Scale);

    /// <summary>Logical-to-physical pixel factor. Multiply logical sizes by this.</summary>
    public float Scale => _ctx.Scale;

    // ── Primitives ───────────────────────────────────────────────────────────
    // Physical coordinates and resolved paints — the vocabulary everything else
    // here is written in. A caller with its own transform and its own colours
    // can reach these directly; a caller that wants theme colours and logical
    // coordinates uses the composed blocks below, which are built from these.

    /// <summary>
    /// Fills and/or strokes a rounded rect. A plain rect is this with radius 0 —
    /// the backend degenerates the round rect to a rect, so there is no second
    /// operation to add.
    /// </summary>
    public void DrawRoundRect(RectF rect, float radius, RasterPaint? fill = null, RasterPaint? stroke = null)
    {
        if (fill is { } f)
        {
            Canvas.DrawRoundRect(rect, radius, radius, f);
        }
        if (stroke is { } s)
        {
            Canvas.DrawRoundRect(rect, radius, radius, s);
        }
    }

    public void DrawLine(float x1, float y1, float x2, float y2, in RasterPaint paint)
        => Canvas.DrawLine(x1, y1, x2, y2, paint);

    public void DrawPath(RasterPath path, in RasterPaint paint) => Canvas.DrawPath(path, paint);

    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in RasterPaint paint)
        => Canvas.DrawPoints(mode, points, paint);

    /// <summary>Draws <paramref name="image"/> into <paramref name="dest"/> (physical px).</summary>
    public void DrawImage(RasterImage image, RectF dest, in RasterPaint paint)
        => Canvas.DrawImage(image, dest, paint);

    /// <summary>Clips to a physical rect until the matching <see cref="PopClip"/>.</summary>
    public void PushClip(RectF rect)
    {
        Canvas.Save();
        Canvas.ClipRect(rect);
    }

    public void PopClip() => Canvas.Restore();

    /// <summary>A logical rect in physical pixels, matching <see cref="Rect"/>.</summary>
    public RectF Physical(float x, float y, float w, float h) => _ctx.ToPhysical(x, y, w, h);

    /// <summary>Parses <c>#rrggbb</c>/<c>#rrggbbaa</c> hex, as the theme spells colours.</summary>
    public static Rgba Color(ReadOnlySpan<char> hex) => Rgba.Parse(hex);

    /// <summary>
    /// The theme this frame is painted in: the one every built-in node resolves
    /// its tokens against, read at paint rather than at build.
    /// </summary>
    public ThemeTokens Theme => _ctx.Theme;

    /// <summary>
    /// <paramref name="color"/> against <see cref="Theme"/>, the way the painter
    /// resolves a <see cref="Style.BackgroundColor"/>: a token follows a theme
    /// changed between the build and this paint, and <see cref="UiColor.WithAlpha"/>
    /// applies. A callback that resolves here instead of capturing an
    /// <see cref="Rgba"/> while building needs no theme passed to it.
    /// </summary>
    public Rgba Resolve(UiColor color)
    {
        var (r, g, b, a) = color.ResolveColor(_ctx.Theme.Colors);
        return new Rgba(r, g, b, a);
    }

    // ── Animation ────────────────────────────────────────────────────────────
    // State that outlives a frame, addressed by key and evicted by the same
    // clock as everything else here. Say where a value should be; get back
    // where it is. Nothing to register and nothing to release — a value stops
    // animating the frame nobody asks for it again.
    //
    // Two mechanisms over one store: ease toward a target, or ramp and wrap.
    // They live here rather than on the cache because a caller reaches them the
    // way it reaches a draw call, and a third mechanism should be a new method
    // rather than a new field on a shared cell.

    /// <inheritdoc cref="DrawingContext.Dt"/>
    public float Dt => _ctx.Dt;

    /// <summary>
    /// Where <paramref name="target"/> has got to. <paramref name="speed"/> is
    /// halvings per second, so the shape of the curve does not depend on the
    /// frame rate.
    ///
    /// A value asked for the first time arrives *at* its target rather than
    /// travelling to it — otherwise everything would fade in from nothing on the
    /// frame it first appears, including the app's first frame. That also makes
    /// a one-shot offscreen render deterministic: every key is a miss, so every
    /// value is settled.
    ///
    /// <paramref name="epsilon"/> is a perception threshold in the value's own
    /// units — 0.01 for a 0..1 fade, 0.5 for a pixel offset. It is what makes
    /// the animation *stop*: exponential approach never actually arrives.
    /// </summary>
    public float Animate(AnimKey key, float target, float speed = 12f, float epsilon = 0.01f)
    {
        var cell = _caches.Anims.FromKey(key, target,
            static t => new AnimCell { Current = t, Next = t, Target = t });

        cell.Target  = target;
        cell.Epsilon = epsilon;
        // Computed from Current, which nothing writes until the frame ends, so
        // asking twice writes the same answer twice and returns the same value
        // to both callers.
        cell.Next = _ctx.ReducedMotion
            ? target
            : cell.Current + (target - cell.Current) * (1f - MathF.Pow(2f, -speed * _ctx.Dt));
        return cell.Current;
    }

    /// <summary>
    /// A 0..1 ramp that wraps — a spinner's rotation, a marching-ants offset —
    /// completing one cycle every <paramref name="periodSeconds"/>.
    ///
    /// Linear in time, unlike <see cref="Animate"/>. Easing is what you want
    /// when travelling *to* somewhere; a repeating ramp has no destination, and
    /// an exponential one would visibly race then dawdle within every turn. It
    /// also makes the period a number you can state rather than one that falls
    /// out of a rate and a threshold — seconds at the runtime's TargetHz, since
    /// the clock is nominal.
    ///
    /// Never settles by construction, so it keeps a draw-on-demand host running
    /// for as long as it is on screen — which is what a spinner should do.
    /// </summary>
    public float Sawtooth(AnimKey key, float periodSeconds = 1f)
    {
        // No destination, so the sweep's "has it arrived" question is answered
        // once, here, by putting the answer out of reach.
        var cell = _caches.Anims.FromKey(key, 0f,
            static _ => new AnimCell { Target = float.PositiveInfinity });

        float next = cell.Current + _ctx.Dt / MathF.Max(0.0001f, periodSeconds);
        // Floor, not -1: after a long pause this may have wrapped many times.
        cell.Next = next - MathF.Floor(next);
        return cell.Current;
    }

    // ── Composed blocks ──────────────────────────────────────────────────────
    // Logical coordinates and theme hex, built from the primitives above. These
    // are conveniences, not privileges — a client can write any of them itself.

    /// <summary>
    /// Hex, as the theme spells colours. Parses and forwards to the
    /// <see cref="Rgba"/> overload, which is where the drawing happens — so
    /// a caller that already holds colours, or is blending two, can skip the
    /// round trip through text entirely.
    /// </summary>
    public void DrawRRect(float x, float y, float w, float h, float radius,
        ReadOnlySpan<char> fillColor = default, ReadOnlySpan<char> strokeColor = default, float? strokeWidth = null)
        => DrawRRect(x, y, w, h, radius,
            fillColor.IsEmpty ? null : Color(fillColor),
            strokeColor.IsEmpty ? null : Color(strokeColor),
            strokeWidth);

    public void DrawRRect(float x, float y, float w, float h, float radius,
        Rgba? fillColor, Rgba? strokeColor, float? strokeWidth)
    {
        DrawRoundRect(
            ToPhysical(x, y, w, h), radius * Scale,
            fill: fillColor is null ? null : Paint(PaintSpec.Fill(fillColor.Value)),
            stroke: strokeColor is null
                ? null
                : Paint(PaintSpec.Stroke(strokeColor.Value, (strokeWidth ?? 1f) * Scale)));
    }

    public void DrawShadow(float x, float y, float w, float h, float radius,
        float offsetX, float offsetY, float blur, float spread, ReadOnlySpan<char> color)
        => DrawShadow(x, y, w, h, radius, offsetX, offsetY, blur, spread, Color(color));

    public void DrawShadow(float x, float y, float w, float h, float radius, float offsetX, float offsetY, float blur, float spread, Rgba color)
    {
        float px = (x + offsetX - spread) * Scale;
        float py = (y + offsetY - spread) * Scale;
        float pw = (w + 2f * spread) * Scale;
        float ph = (h + 2f * spread) * Scale;
        float pr = MathF.Max(0f, radius + spread) * Scale;

        // A blur radius is about two Gaussian sigmas: sigma ≈ radius/2.
        float sigma = MathF.Max(0.01f, blur * Scale * 0.5f);

        DrawRoundRect(new RectF(px, py, px + pw, py + ph), pr,
            fill: Paint(PaintSpec.Fill(color), blurSigma: sigma));
    }


    public void DrawEdge(float x1, float y1, float x2, float y2, float width, ReadOnlySpan<char> color, bool dashed = false)
        => DrawEdge(x1, y1, x2, y2, width, Color(color), dashed);

    public void DrawEdge(float x1, float y1, float x2, float y2, float width, Rgba color, bool dashed = false)
    {
        // A two-point path is just a line: a dash applies to DrawLine as readily
        // as to DrawPath, so there is no geometry to build.
        var spec = PaintSpec.Stroke(color, width * Scale);
        var paint = dashed
            ? Paint(spec, dash: new DashPattern(DashLength(width), DashLength(width)))
            : Paint(spec);

        DrawLine(x1 * Scale, y1 * Scale, x2 * Scale, y2 * Scale, paint);
    }

    private float DashLength(float width) => MathF.Max(2f, width * 2.5f) * Scale;

    public void DrawVerticalLine(float x, float y1, float y2, ReadOnlySpan<char> color, float strokeWidth = 1f)
        => DrawVerticalLine(x, y1, y2, Color(color), strokeWidth);

    public void DrawVerticalLine(float x, float y1, float y2, Rgba color, float strokeWidth = 1f)
    {
        var paint = Paint(PaintSpec.Stroke(color, strokeWidth * Scale));
        DrawLine(x * Scale, y1 * Scale, x * Scale, y2 * Scale, paint);
    }

    public void DrawCheckMark(float x, float y, float width, float height, ReadOnlySpan<char> strokeColor, float strokeWidth)
        => DrawCheckMark(x, y, width, height, Color(strokeColor), strokeWidth);

    public void DrawCheckMark(float x, float y, float width, float height, Rgba strokeColor, float strokeWidth)
    {
        // Geometry at the origin and sized in physical px; the canvas applies the
        // position. Baking x/y into the path would make every on-screen location
        // its own cache entry, which is the difference between one check mark and
        // one per row of a list.
        float pw = width * Scale, ph = height * Scale;
        var path = Path(
            CacheKey.Of([CacheKey.Bits(pw), CacheKey.Bits(ph), (int)PathKind.CheckMark]),
            (pw, ph),
            static (p, path) =>
            {
                path.MoveTo(p.pw * 0.22f, p.ph * 0.55f);
                path.LineTo(p.pw * 0.42f, p.ph * 0.74f);
                path.LineTo(p.pw * 0.78f, p.ph * 0.28f);
            });

        var paint = Paint(PaintSpec.Stroke(strokeColor, strokeWidth * Scale) with
        {
            StrokeCap = StrokeCap.Round,
            StrokeJoin = StrokeJoin.Round,
        });

        PushTranslation(x, y);
        DrawPath(path, paint);
        PopTranslation();
    }

    public void DrawArc(float cx, float cy, float radius, float strokeWidth, ReadOnlySpan<char> color,
        float startAngle = -90f, float sweepAngle = 270f)
        => DrawArc(cx, cy, radius, strokeWidth, Color(color), startAngle, sweepAngle);

    public void DrawArc(float cx, float cy, float radius, float strokeWidth, Rgba color, float startAngle = -90f, float sweepAngle = 270f)
    {
        float r = radius * Scale;
        var path = Path(
            CacheKey.Of([CacheKey.Bits(r), CacheKey.Bits(startAngle), CacheKey.Bits(sweepAngle), (int)PathKind.Arc]),
            (r, startAngle, sweepAngle),
            static (p, path) => path.AddArc(new RectF(0f, 0f, p.r * 2f, p.r * 2f), p.startAngle, p.sweepAngle));

        var paint = Paint(PaintSpec.Stroke(color, strokeWidth * Scale) with
        {
            StrokeCap = StrokeCap.Round,
            StrokeJoin = StrokeJoin.Miter,
        });

        PushTranslation(cx - radius, cy - radius);
        DrawPath(path, paint);
        PopTranslation();
    }

    // Chart geometry is built fresh: the points *are* the content, so a cache key
    // would have to hash all of them, and a series that moves every frame would
    // miss every frame and leave a dead entry behind. It is built into the one
    // scratch path the renderer keeps for this, rewound per use.
    public void DrawPolyline(ReadOnlySpan<(float X, float Y)> points, ReadOnlySpan<char> color, float strokeWidth)
        => DrawPolyline(points, Color(color), strokeWidth);

    public void DrawPolyline(ReadOnlySpan<(float X, float Y)> points, Rgba color, float strokeWidth)
    {
        if (points.Length < 2)
        {
            return;
        }

        var path = _caches.ScratchPath();
        path.MoveTo(points[0].X * Scale, points[0].Y * Scale);
        for (int i = 1; i < points.Length; i++)
        {
            path.LineTo(points[i].X * Scale, points[i].Y * Scale);
        }

        DrawPath(path, Paint(
            PaintSpec.Stroke(color, strokeWidth * Scale) with
            {
                StrokeCap = StrokeCap.Round,
                StrokeJoin = StrokeJoin.Round,
            }));
    }

    public void DrawFilledPath(ReadOnlySpan<(float X, float Y)> points, float baselineY, ReadOnlySpan<char> fillColor)
        => DrawFilledPath(points, baselineY, Color(fillColor));

    public void DrawFilledPath(ReadOnlySpan<(float X, float Y)> points, float baselineY, Rgba fillColor)
    {
        if (points.Length < 2)
        {
            return;
        }

        var path = _caches.ScratchPath();
        BuildClosedArea(path, points, baselineY);
        DrawPath(path, Paint(PaintSpec.Fill(fillColor)));
    }

    public void DrawFilledPathGradient(ReadOnlySpan<(float X, float Y)> points,
        float plotTopY, float baselineY, ReadOnlySpan<char> color)
        => DrawFilledPathGradient(points, plotTopY, baselineY, Color(color));

    public void DrawFilledPathGradient(ReadOnlySpan<(float X, float Y)> points, float plotTopY, float baselineY, Rgba color)
{
        if (points.Length < 2)
        {
            return;
        }

        var path = _caches.ScratchPath();
        BuildClosedArea(path, points, baselineY);

        var baseColor = color;
        var shader = LinearGradient(
            new Vector2(0f, plotTopY * Scale),
            new Vector2(0f, baselineY * Scale),
            [baseColor.WithAlpha(0x55), baseColor.WithAlpha(0x00)],
            []);

        DrawPath(path, Paint(PaintSpec.Fill(Rgba.Black), shader: shader));
    }

    /// <summary>The curve, then a straight baseline back to the start.</summary>
    private void BuildClosedArea(RasterPath path, ReadOnlySpan<(float X, float Y)> points, float baselineY)
    {
        path.MoveTo(points[0].X * Scale, points[0].Y * Scale);
        for (int i = 1; i < points.Length; i++)
        {
            path.LineTo(points[i].X * Scale, points[i].Y * Scale);
        }
        path.LineTo(points[^1].X * Scale, baselineY * Scale);
        path.LineTo(points[0].X * Scale, baselineY * Scale);
        path.Close();
    }

    public void DrawRRectGradient(float x, float y, float w, float h, float radius,
        IReadOnlyList<(float Stop, string Color)> stops,
        (float X, float Y) start, (float X, float Y) end,
        ReadOnlySpan<char> strokeColor = default, float? strokeWidth = null,
        GradientType type = GradientType.Linear)
        => DrawRRectGradient(x, y, w, h, radius, stops, start, end,
            strokeColor.IsEmpty ? null : Color(strokeColor), strokeWidth, type);

    public void DrawRRectGradient(float x, float y, float w, float h, float radius,
        IReadOnlyList<(float Stop, string Color)> stops,
        (float X, float Y) start, (float X, float Y) end,
        Rgba? strokeColor, float? strokeWidth, GradientType type)
    {
        if (stops.Count == 0)
        {
            return;
        }

        var rect = ToPhysical(x, y, w, h);
        float pw = w * Scale, ph = h * Scale;
        float pr = radius * Scale;

        // Endpoints are given in normalized rect coordinates (0..1).
        var skStart = new Vector2(rect.Left + start.X * pw, rect.Top + start.Y * ph);
        var skEnd = new Vector2(rect.Left + end.X * pw, rect.Top + end.Y * ph);

        var colors = new Rgba[stops.Count];
        var positions = new float[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            colors[i] = Color(stops[i].Color);
            positions[i] = stops[i].Stop;
        }

        var shader = type == GradientType.Radial
            ? RadialGradient(skStart, MathF.Max(0.01f, Distance(skStart, skEnd)), colors, positions)
            : LinearGradient(skStart, skEnd, colors, positions);

        DrawRoundRect(rect, pr,
            fill: Paint(PaintSpec.Fill(Rgba.Black), shader: shader),
            stroke: strokeColor is null
                ? null
                : Paint(PaintSpec.Stroke(strokeColor.Value, (strokeWidth ?? 1f) * Scale)));
    }

    private static float Distance(Vector2 a, Vector2 b)
        => MathF.Sqrt(MathF.Pow(b.X - a.X, 2) + MathF.Pow(b.Y - a.Y, 2));

    /// <summary>Blits RGBA8888 pixels into the given logical rect.</summary>
    public void DrawBitmap(byte[] rgbaBytes, int srcWidth, int srcHeight,
        float destX, float destY, float destW, float destH)
    {
        var destRect = new RectF(
            destX * Scale, destY * Scale,
            (destX + destW) * Scale, (destY + destH) * Scale);
        // FilterQuality.Low (linear) instead of Medium (mipmaps). Mipmap
        // generation runs every render and dominates the per-frame cost for
        // full-screen video; linear filtering looks fine at typical video
        // display sizes. AA off — no rotation, axis-aligned blit.
        // Opaque black, not default: DrawImage modulates the image by the
        // paint's alpha, and default(Rgba) is transparent.
        Canvas.DrawPixels(rgbaBytes, srcWidth, srcHeight, destRect, Paint(new PaintSpec
        {
            Color = Rgba.Black,
            FilterQuality = FilterQuality.Low,
            IsAntialias = false,
        }));
    }

    /// <summary>Shifts the origin until the matching <see cref="PopTranslation"/>.</summary>
    public void PushTranslation(float dx, float dy)
    {
        Canvas.Save();
        Canvas.Translate(dx * Scale, dy * Scale);
    }

    public void PopTranslation() => Canvas.Restore();

    /// <summary>
    /// Maps a zoomed subtree's local logical space onto the surface: local point
    /// <c>p</c> lands at <c>origin + scale * p</c>. Every draw here already
    /// multiplies its logical input by the device scale, so the translation is
    /// pre-multiplied by it and the zoom composes on top — which is what lets a
    /// subtree be laid out at 1:1 and simply appear zoomed.
    /// </summary>
    public void PushScale(float originX, float originY, float scale)
    {
        Canvas.Save();
        Canvas.Translate(originX * Scale, originY * Scale);
        Canvas.Scale(scale, scale);
    }

    public void PopScale() => Canvas.Restore();

    /// <summary>
    /// Draws everything until <see cref="PopTransform"/> through
    /// <paramref name="logical"/>, a transform over logical px (what
    /// <c>Style.Translate</c>/<c>Scale</c>/<c>Rotate</c> build), applied on top
    /// of whatever transform is already in force.
    /// </summary>
    public void PushTransform(Matrix3x2 logical)
    {
        // The same map over physical px: down to logical, through it, back up.
        float s = Scale;
        var physical = Matrix3x2.CreateScale(1f / s) * logical * Matrix3x2.CreateScale(s);
        Canvas.Save();
        Canvas.SetMatrix(physical * Canvas.TotalMatrix);
    }

    public void PopTransform() => Canvas.Restore();


    /// <summary>Clips to this logical rect until the matching <see cref="PopClip"/>.</summary>
    public void PushClip(float x, float y, float w, float h) => PushClip(Physical(x, y, w, h));

    /// <summary>Composites everything until <see cref="PopOpacityLayer"/> at the given opacity.</summary>
    public void PushOpacityLayer(float opacity)
    {
        byte alpha = (byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255);
        Canvas.SaveLayerAlpha(alpha);
    }

    public void PopOpacityLayer()
    {
        Canvas.Restore();
    }

    /// <summary>
    /// The paint for <paramref name="spec"/> with the effects bibui draws with: a
    /// gradient, a blur of the shape's coverage (a soft shadow, sigma in physical
    /// px), a blur of what is drawn (a backdrop), a dash. A value — nothing is
    /// built here; the raster backend resolves it against its own cache when it
    /// draws, keyed on all of it.
    /// </summary>
    public RasterPaint Paint(
        in PaintSpec spec,
        RasterShader? shader = null,
        float blurSigma = 0f,
        float imageBlurSigma = 0f,
        DashPattern? dash = null)
        => new(spec)
        {
            Shader = shader,
            MaskBlurSigma = blurSigma,
            ImageBlurSigma = imageBlurSigma,
            Dash = dash,
        };

    /// <summary>Convenience for <c>Paint(PaintSpec.Fill(color))</c>.</summary>
    public RasterPaint Fill(Rgba color) => Paint(PaintSpec.Fill(color));

    /// <summary>Convenience for <c>Paint(PaintSpec.Stroke(color, width))</c>.</summary>
    public RasterPaint Stroke(Rgba color, float width) => Paint(PaintSpec.Stroke(color, width));

    /// <summary>A linear gradient in physical px. Empty <paramref name="stops"/> spreads the colours evenly.</summary>
    public RasterShader LinearGradient(
        Vector2 start, Vector2 end, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
        => RasterShader.Linear(start, end, colors, stops);

    /// <summary>A radial gradient in physical px, one stop per colour.</summary>
    public RasterShader RadialGradient(
        Vector2 centre, float radius, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
        => RasterShader.Radial(centre, radius, colors, stops);

    /// <summary>
    /// Geometry that repeats across frames, built once for <paramref name="key"/>.
    /// The key must cover everything <paramref name="build"/> reads — including
    /// position, unless the caller translates the canvas instead.
    /// </summary>
    public RasterPath Path(CacheKey key, Action<RasterPath> build) =>
        _caches.Paths.FromKey(key, (backend: _ctx.Backend, build),
            static p => Built(p.backend, p.build));

    /// <summary>
    /// Geometry built from <paramref name="arg"/> by a <c>static</c> lambda, so a
    /// hit allocates nothing — no closure is captured. Prefer this over the
    /// <see cref="Action{T}"/> overload on any path a frame draws repeatedly.
    /// </summary>
    public RasterPath Path<TArg>(CacheKey key, TArg arg, Action<TArg, RasterPath> build)
        => _caches.Paths.FromKey(key, (backend: _ctx.Backend, arg, build),
            static p => Built(p.backend, p.arg, p.build));

    private static RasterPath Built(IRasterBackend backend, Action<RasterPath> build)
    {
        var path = backend.CreatePath();
        build(path);
        return path;
    }

    private static RasterPath Built<TArg>(IRasterBackend backend, TArg arg, Action<TArg, RasterPath> build)
    {
        var path = backend.CreatePath();
        build(arg, path);
        return path;
    }


    // ── Renderer plumbing ────────────────────────────────────────────────────
    // Internal, so the painter can take a DrawContext like any other draw does
    // without widening what a client component can reach. Frame lifecycle
    // stays off this type entirely: it belongs to whoever owns the frame, and
    // that code holds the DrawingContext.

    internal float SurfaceWidth => _ctx.SurfaceWidth;
    internal float SurfaceHeight => _ctx.SurfaceHeight;

    internal RectF ToPhysical(float x, float y, float w, float h) => _ctx.ToPhysical(x, y, w, h);

    internal DrawContext For(float x, float y, float w, float h) => _ctx.DrawContextFor(x, y, w, h);
}
