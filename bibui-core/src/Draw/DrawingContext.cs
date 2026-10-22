using System.Numerics;
using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.Theming;

namespace Bibui.Core.Draw;

internal sealed class DrawingContext
{
    private DrawList _list = null!;
    private float _scale;
    private readonly Fonts _fonts;

    /// <summary>
    /// Logical surface dimensions, set by the painter at frame start.
    /// </summary>
    internal float SurfaceWidth { get; set; }
    internal float SurfaceHeight { get; set; }

    /// <summary>The theme this frame is painted in — the one the painter resolves every token against.</summary>
    internal ThemeTokens Theme { get; private set; } = null!;

    // Made once per painter; Begin sets what changes from frame to frame, so a
    // frame allocates no context.
    internal DrawingContext(Fonts fonts, RenderCaches caches)
    {
        _fonts = fonts;
        _caches = caches;
    }

    /// <summary>Starts a frame's recording into <paramref name="list"/>.</summary>
    internal void Begin(DrawList list, float scale, FrameInput input, ThemeTokens theme, float width, float height)
    {
        _list = list;
        _scale = scale;
        _input = input;
        Theme = theme;
        SurfaceWidth = width;
        SurfaceHeight = height;
        PaintDepth = 0;
        (StateDepth, StateFloor) = (0, 0);
        _transforms[0] = Matrix3x2.Identity;
    }

    // The transform in force at each depth of the state stack, so a draw can know
    // where it lands (DrawContext.Transform). A pop needs nothing: the depth is the index.
    private Matrix3x2[] _transforms = new Matrix3x2[16];

    /// <summary>The transform the recorded pushes leave in force: local physical px to device px.</summary>
    internal Matrix3x2 Transform => _transforms[StateDepth];

    /// <summary>A clip, transform or layer was pushed: one deeper, through <paramref name="by"/> when it is a transform.</summary>
    internal void Pushed(in Matrix3x2? by = null)
    {
        if (StateDepth + 1 == _transforms.Length) Array.Resize(ref _transforms, _transforms.Length * 2);
        var m = _transforms[StateDepth];
        _transforms[StateDepth + 1] = by is { } t ? t * m : m;
        StateDepth++;
    }

    // The clip/transform/opacity stack's depth, and the depth a pop may not go
    // below: the painter raises the floor around a callback, so the callback
    // cannot pop what the painter pushed, and pops what it left pushed itself.
    internal int StateDepth;
    internal int StateFloor;

    // The current paint: what a draw without its own is drawn in. A stack reused
    // from frame to frame; the painter restores its depth after every callback,
    // so one that forgets a pop does not leak its paint into the next.
    private Paint[] _paints = new Paint[8];

    internal int PaintDepth { get; set; }

    internal Paint CurrentPaint => PaintDepth > 0 ? _paints[PaintDepth - 1] : DefaultPaint;

    private static readonly Paint DefaultPaint = Paint.Filled(Rgba.Black);

    internal void PushPaint(in Paint paint)
    {
        if (PaintDepth == _paints.Length) Array.Resize(ref _paints, PaintDepth * 2);
        _paints[PaintDepth++] = paint;
    }

    internal void PopPaint()
    {
        if (PaintDepth > 0) PaintDepth--;
    }

    // Owned by the renderer, shared across frames.
    private readonly RenderCaches _caches;

    // This frame's input record. Held, not unpacked: Dt reads through it so the
    // value exists once on the render path rather than in a field here too.
    private FrameInput _input = FrameInput.None;

    /// <inheritdoc cref="FrameInput.Dt"/>
    public float Dt => _input.Dt;

    /// <inheritdoc cref="FrameInput.ReducedMotion"/>
    public bool ReducedMotion => _input.ReducedMotion;

    /// <inheritdoc cref="FrameInput.Rates"/>
    public MotionRates Rates => _input.Rates;

    /// <inheritdoc cref="Fonts.Metrics"/>
    internal TextMetrics Metrics(float logicalFontSize, string? family, int weight)
        => _fonts.Metrics(logicalFontSize, family, weight, _scale);

    /// <inheritdoc cref="Fonts.MeasureRuns"/>
    internal float MeasureRuns(ReadOnlySpan<char> text, float logicalFontSize, string? family, int weight)
        => _fonts.MeasureRuns(text, logicalFontSize, family, weight, _scale);

    internal DrawList List => _list;

    internal float Scale => _scale;
    internal RenderCaches Caches => _caches;
    internal Fonts Fonts => _fonts;

    /// <summary>The context handed to a Box.Draw callback, over this frame's caches.</summary>
    internal DrawContext DrawContextFor(float x, float y, float w, float h) =>
        new(this, ToPhysical(x, y, w, h));

    /// <summary>
    /// The whole surface as a draw context. This is the boundary between owning a
    /// frame and drawing into it: the renderer holds the DrawingContext for the
    /// lifecycle and descends through this, so its own
    /// components reach exactly the API a client component does.
    /// </summary>
    internal DrawContext Root => DrawContextFor(0f, 0f, SurfaceWidth, SurfaceHeight);
    internal RectF ToPhysical(float x, float y, float w, float h) =>
        new(x * _scale, y * _scale, (x + w) * _scale, (y + h) * _scale);

    /// <summary>
    /// Parses <c>#rrggbb</c> or <c>#rrggbbaa</c>, as the theme spells colours.
    ///
    /// Over a span rather than a string: the old form allocated on every call —
    /// one for the TrimStart and one per channel for the substring — and a
    /// colour is parsed for every fill, stroke and glyph run of every frame. A
    /// string converts implicitly, so callers are unchanged.
    ///
    /// A malformed digit gives black, the same answer a wrong length already
    /// gave, rather than the exception Convert.ToByte used to throw.
    /// </summary>
    internal static Rgba ParseColor(ReadOnlySpan<char> hex) => Rgba.Parse(hex);
}
