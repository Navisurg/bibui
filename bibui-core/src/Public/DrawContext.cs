using System.Numerics;
using Bibui.Core.Draw;
using Bibui.Core.Theming;

namespace Bibui.Core;

/// <summary>
/// A complete description of a paint's own fields, as a <see cref="DrawOp"/>
/// carries them: every field that changes what is drawn is here, so a renderer
/// that caches native paints keys them on it, and a new requirement is a new
/// field rather than a new key type.
///
/// Effects (a gradient, a blur, a dash) are not part of the spec: a
/// <see cref="DrawOp"/> carries them beside it, and a renderer that keeps
/// native paints keys them on both.
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
/// What a <c>Box.Draw</c> callback is handed: the draws it may make, recorded
/// into the frame's <see cref="DrawList"/> in tree order, and cached
/// constructors for the paths and paints it might need.
///
/// Nothing here is constructed twice. A path lives in the renderer's caches, and
/// a paint is a value the raster backend resolves against its own — both evicted
/// by the same frame clock as the renderer's — so a custom component costs what
/// a built-in one costs, instead of allocating and destroying native objects on
/// every frame.
///
/// One vocabulary, bibui's: value types (<see cref="Rgba"/>, <see cref="RectF"/>,
/// Vector2, <see cref="Paint"/>), its own <see cref="VectorPath"/>, and texture
/// handles. Nothing here is a backend's,
/// so a component written against it draws on any backend, and nothing is
/// drawn while it records: a renderer executes the list after.
///
/// A ref struct so it cannot outlive the callback. The objects it hands back
/// are ordinary references, so do not store them past the call: they are valid
/// for this frame and are recycled, not owned by you.
/// </summary>
public readonly ref partial struct DrawContext
{
    private readonly DrawingContext _ctx;
    private RenderCaches _caches => _ctx.Caches;
    /// <summary>The renderer's fonts: what this frame's text was measured with.</summary>
    public Fonts Fonts => _ctx.Fonts;

    /// <summary>What this wraps, for the renderer to keep where a ref struct cannot go.</summary>
    internal DrawingContext Drawing => _ctx;

    internal DrawContext(DrawingContext ctx, RectF rect)
    {
        _ctx = ctx;
        Rect = rect;
    }

    // The frame's draws: every method below records into it.
    private DrawList List => _ctx.List;

    /// <summary>
    /// The box's rect in physical pixels, in its own space: (0,0) is its
    /// top-left, and draws outside it are clipped.
    /// </summary>
    public RectF Rect { get; }

    /// <summary>
    /// The box's rect in logical pixels, in its own space — the units
    /// <see cref="DrawShapes"/> take.
    /// </summary>
    public RectF Logical => new(
        Rect.Left / Scale, Rect.Top / Scale, Rect.Right / Scale, Rect.Bottom / Scale);

    /// <summary>Logical-to-physical pixel factor. Multiply logical sizes by this.</summary>
    public float Scale => _ctx.Scale;

    // ── Primitives ───────────────────────────────────────────────────────────
    // Physical px, the units the renderer draws in. Each draw takes a Paint, or
    // uses the current one (PushPaint). What a draw becomes in the frame's
    // draw list is the core's business: callers name the shape and the look.

    /// <summary>A rect, rounded by <paramref name="radius"/>, in the current paint.</summary>
    public void DrawRect(RectF rect, float radius = 0f) => DrawRect(rect, radius, _ctx.CurrentPaint);

    /// <summary>A rect, rounded by <paramref name="radius"/>: filled, then stroked, as <paramref name="paint"/> says.</summary>
    public void DrawRect(RectF rect, float radius, in Paint paint)
    {
        if (paint.Fill is not null || paint.Shader is not null) RectOp(rect, radius, paint.FillPaint());
        if (paint.Stroke is not null) RectOp(rect, radius, paint.StrokePaint());
    }

    private void RectOp(RectF rect, float radius, in RasterPaint raster)
    {
        ref var op = ref List.Push(DrawOpKind.Rect);
        (op.Rect, op.A, op.B) = (rect, radius, radius);
        List.SetPaint(ref op, raster);
    }

    /// <summary>A line, stroked in the current paint.</summary>
    public void DrawLine(float x1, float y1, float x2, float y2) => DrawLine(x1, y1, x2, y2, _ctx.CurrentPaint);

    /// <summary>A line, stroked as <paramref name="paint"/> says.</summary>
    public void DrawLine(float x1, float y1, float x2, float y2, in Paint paint)
    {
        ref var op = ref List.Push(DrawOpKind.Line);
        (op.A, op.B, op.C, op.D) = (x1, y1, x2, y2);
        List.SetPaint(ref op, paint.StrokePaint());
    }

    /// <summary>A path, in the current paint.</summary>
    public void DrawPath(VectorPath path) => DrawPath(path, _ctx.CurrentPaint);

    /// <summary>
    /// A path: filled, then stroked, as <paramref name="paint"/> says. A path from
    /// <see cref="Path(CacheKey, Action{VectorPath})"/> is kept, so the list names it
    /// and the renderer reuses what it built from it; any other is copied into the
    /// list as it is now, and may be rewound and rebuilt as soon as this returns.
    /// </summary>
    public void DrawPath(VectorPath path, in Paint paint)
    {
        if (paint.Fill is not null || paint.Shader is not null) PathOp(path, paint.FillPaint());
        if (paint.Stroke is not null) PathOp(path, paint.StrokePaint());
    }

    private void PathOp(VectorPath path, in RasterPaint raster)
    {
        ref var op = ref List.Push(DrawOpKind.Path);
        if (path.Kept) op.Resource = List.AddPath(path);
        else
        {
            if (path.Verbs.Contains(PathVerb.AddPath))
                throw new NotSupportedException("A path that adds another is drawn from ctx.Path, where it is kept for the frame.");
            (op.Start, op.Count) = (List.AddVerbs(path.Verbs), path.Verbs.Length);
            (op.Start2, op.Count2) = (List.AddFloats(path.Args), path.Args.Length);
        }
        List.SetPaint(ref op, raster);
    }

    /// <summary>Points, stroked in the current paint.</summary>
    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points) => DrawPoints(mode, points, _ctx.CurrentPaint);

    /// <summary>Points, stroked as <paramref name="paint"/> says: dots, segments or a polygon.</summary>
    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in Paint paint)
    {
        ref var op = ref List.Push(DrawOpKind.Points);
        op.Mode = mode;
        (op.Start, op.Count) = (List.AddPoints(points), points.Length);
        List.SetPaint(ref op, paint.StrokePaint());
    }

    /// <summary>A texture into <paramref name="dest"/>, in the current paint.</summary>
    public void DrawTexture(TextureHandle texture, RectF dest) => DrawTexture(texture, dest, _ctx.CurrentPaint);

    /// <summary>
    /// <paramref name="texture"/> scaled into <paramref name="dest"/>. The texture
    /// comes from the backend the app chose (its <c>CreateTexture</c>); the app
    /// owns and releases it. The fill colour's alpha modulates it, so an opaque
    /// paint draws it as it is; for a full-screen video frame,
    /// <see cref="FilterQuality.Low"/> and no antialiasing avoid building mipmaps
    /// every frame.
    /// </summary>
    public void DrawTexture(TextureHandle texture, RectF dest, in Paint paint)
    {
        ref var op = ref List.Push(DrawOpKind.Texture);
        (op.Texture, op.Rect) = (texture, dest);
        List.SetPaint(ref op, paint.FillPaint());
    }

    /// <summary>
    /// <paramref name="texture"/> scaled into <paramref name="dest"/> through
    /// <paramref name="lookup"/>: each pixel's raw channel (or two) picks the colour
    /// drawn from the lookup's table (<see cref="TextureLookup"/>). The fill
    /// colour's alpha modulates it, as for any texture. A released table draws
    /// nothing; <see cref="TextureLookup.None"/> draws the texture plainly.
    /// </summary>
    public void DrawTexture(TextureHandle texture, RectF dest, in Paint paint, TextureLookup lookup)
    {
        ref var op = ref List.Push(DrawOpKind.Texture);
        (op.Texture, op.Rect) = (texture, dest);
        if (!lookup.IsNone) op.Resource = List.AddLookup(lookup);
        List.SetPaint(ref op, paint.FillPaint());
    }

    // ── State ────────────────────────────────────────────────────────────────
    // Each push is undone by its pop, in order. Clips, transforms and opacity
    // share one stack, counted so a callback's are its own (see StateFloor).

    /// <summary>Clips to <paramref name="rect"/> until the matching <see cref="PopClip"/>.</summary>
    public void PushClip(RectF rect)
    {
        List.Push(DrawOpKind.PushClip).Rect = rect;
        _ctx.Pushed();
    }

    public void PopClip() => Pop();

    /// <summary>
    /// The transform in force, from this context's space (physical px, (0,0) the box's
    /// top-left) to the frame's device pixels: the painter's translation to the box, the
    /// transforms of the nodes it lies in, and this callback's own pushes.
    /// </summary>
    public Matrix3x2 Transform => _ctx.Transform;

    /// <summary>
    /// <paramref name="rect"/> moved by under half a pixel each way so that its top-left
    /// lands on a whole device pixel, its size kept; as it is when the transform is more
    /// than a translation. A box is laid out in logical px, so at a fractional scale its
    /// callback's (0,0) is rarely on the grid: an image drawn at its own size into a
    /// snapped rect is drawn 1:1, copied rather than sampled, with sharp edges.
    /// </summary>
    public RectF Snap(RectF rect)
    {
        var m = _ctx.Transform;
        if (m.M11 != 1f || m.M22 != 1f || m.M12 != 0f || m.M21 != 0f) return rect;
        float x = rect.Left + m.M31, y = rect.Top + m.M32;
        float dx = MathF.Round(x) - x, dy = MathF.Round(y) - y;
        return new RectF(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
    }

    // Undoes the last clip, transform or opacity; nothing below the floor.
    internal void Pop()
    {
        if (_ctx.StateDepth == _ctx.StateFloor) return;
        List.Push(DrawOpKind.Pop);
        _ctx.StateDepth--;
    }

    /// <summary>
    /// Draws through <paramref name="transform"/>, over physical px, applied
    /// before the transform already in force, until <see cref="PopTransform"/>.
    /// A translation and a scale are transforms like any other.
    /// </summary>
    public void PushTransform(Matrix3x2 transform)
    {
        List.Push(DrawOpKind.PushTransform).Matrix = transform;
        _ctx.Pushed(transform);
    }

    public void PopTransform() => Pop();

    /// <summary>
    /// Composites everything until <see cref="PopOpacity"/> at <paramref name="opacity"/>
    /// (0..1): a colour matrix that only scales alpha (<see cref="PushColorMatrix"/>).
    /// </summary>
    public void PushOpacity(float opacity)
    {
        // In 1/255 steps, as a layer's alpha is kept.
        float a = Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255) / 255f;
        PushColorMatrix([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, a, 0]);
    }

    public void PopOpacity() => Pop();

    /// <summary>
    /// Draws everything until <see cref="PopBlendMode"/> into a layer, blending
    /// with each other normally, and composites that layer onto what is under it
    /// with <paramref name="mode"/>: one Multiply over a network of strokes
    /// darkens once where they cross. SrcOver composites as if there were no group.
    /// </summary>
    public void PushBlendMode(BlendMode mode)
    {
        List.Push(DrawOpKind.PushBlendMode).Blend = mode;
        _ctx.Pushed();
    }

    public void PopBlendMode() => Pop();

    /// <summary>
    /// Draws everything until <see cref="PopColorMatrix"/> into a layer and
    /// composites it through <paramref name="matrix"/>: 20 floats, a 4×5 matrix
    /// row by row, each output channel (r, g, b, a) = the row's four weights times
    /// the straight colour (0..1) plus its fifth, the translation. Saturation,
    /// brightness and opacity are matrices.
    /// </summary>
    public void PushColorMatrix(ReadOnlySpan<float> matrix)
    {
        if (matrix.Length != 20) throw new ArgumentException($"A colour matrix is 20 floats (4×5), not {matrix.Length}.", nameof(matrix));
        ref var op = ref List.Push(DrawOpKind.PushColorMatrix);
        (op.Start, op.Count) = (List.AddFloats(matrix), 20);
        _ctx.Pushed();
    }

    public void PopColorMatrix() => Pop();

    /// <summary>Makes <paramref name="paint"/> the current paint, until <see cref="PopPaint"/>.</summary>
    public void PushPaint(in Paint paint) => _ctx.PushPaint(paint);

    public void PopPaint() => _ctx.PopPaint();

    /// <summary>
    /// A path for geometry drawn once and dropped (a chart series whose points
    /// are the content, so no key could name it), rewound. Valid until the next
    /// call: drawing it copies it into the frame.
    /// </summary>
    public VectorPath ScratchPath() => _caches.ScratchPath();

    /// <summary>A logical rect in physical pixels, in the same space as <see cref="Rect"/>.</summary>
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

    /// <inheritdoc cref="DrawingContext.Rates"/>
    public MotionRates Rates => _ctx.Rates;

    /// <summary>
    /// Where <paramref name="target"/> has got to. <paramref name="rate"/> is
    /// the share of the gap closed this frame, from <see cref="Rates"/> for the
    /// kind of motion it is; with none, <see cref="MotionRates.Default"/>.
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
    public float Animate(AnimKey key, float target, float? rate = null, float epsilon = 0.01f)
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
            : Ease.Exponential(cell.Current, target, rate ?? _ctx.Rates.Default);
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
    /// out of a rate and a threshold — seconds of the runtime's Dt.
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

    /// <summary>
    /// Geometry that repeats across frames, built once for <paramref name="key"/>.
    /// The key must cover everything <paramref name="build"/> reads — including
    /// position, unless the caller translates the canvas instead.
    /// </summary>
    public VectorPath Path(CacheKey key, Action<VectorPath> build) =>
        _caches.Paths.FromKey(key, build, static build => Kept(build));

    /// <summary>
    /// Geometry built from <paramref name="arg"/> by a <c>static</c> lambda, so a
    /// hit allocates nothing — no closure is captured. Prefer this over the
    /// <see cref="Action{T}"/> overload on any path a frame draws repeatedly.
    /// </summary>
    public VectorPath Path<TArg>(CacheKey key, TArg arg, Action<TArg, VectorPath> build)
        => _caches.Paths.FromKey(key, (arg, build), static p => Kept(p.arg, p.build));

    private static VectorPath Kept(Action<VectorPath> build)
    {
        var path = new VectorPath { Kept = true };
        build(path);
        return path;
    }

    private static VectorPath Kept<TArg>(TArg arg, Action<TArg, VectorPath> build)
    {
        var path = new VectorPath { Kept = true };
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

    /// <summary>A box's context: its rect at the origin, <paramref name="w"/>×<paramref name="h"/> logical px.</summary>
    internal DrawContext For(float w, float h) => _ctx.DrawContextFor(0f, 0f, w, h);
}
