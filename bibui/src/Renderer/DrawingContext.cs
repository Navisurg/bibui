using Bibui;
using Bibui.Raster;
using Bibui.Theming;

namespace Bibui.Renderer;

internal sealed class DrawingContext
{
    private readonly IRasterCanvas _canvas;
    private readonly IRasterBackend _backend;
    private readonly float _scale;
    private readonly Fonts _fonts;
    private readonly bool _backdrop;

    /// <summary>
    /// Logical surface dimensions, set by the painter at frame start.
    /// </summary>
    internal float SurfaceWidth { get; set; }
    internal float SurfaceHeight { get; set; }

    /// <summary>The theme this frame is painted in — the one the painter resolves every token against.</summary>
    internal ThemeTokens Theme { get; }

    /// <param name="backdrop">
    /// Whether <see cref="DrawContext.BlurBackdrop"/> may read back what the canvas
    /// has painted. Without it the blur is a no-op.
    /// </param>
    internal DrawingContext(IRasterCanvas canvas, float scale, Fonts fonts, RenderCaches caches, FrameInput input, ThemeTokens theme, bool backdrop = false)
    {
        _canvas = canvas;
        Theme = theme;
        _backend = caches.Backend;
        _scale = scale;
        _fonts = fonts;
        _backdrop = backdrop;
        _caches = caches;
        _input = input;
    }

    // Owned by the renderer, shared across frames.
    private readonly RenderCaches _caches;

    // This frame's input record. Held, not unpacked: Dt reads through it so the
    // value exists once on the render path rather than in a field here too.
    private readonly FrameInput _input;

    /// <inheritdoc cref="FrameInput.Dt"/>
    public float Dt => _input.Dt;

    /// <inheritdoc cref="FrameInput.ReducedMotion"/>
    public bool ReducedMotion => _input.ReducedMotion;

    /// <summary>The font one run of text is measured and drawn in: a typeface at this size in physical px.</summary>
    internal RasterFont Font(RasterTypeface typeface, float logicalFontSize)
        => new(typeface, logicalFontSize * _scale);

    /// <inheritdoc cref="Fonts.Metrics"/>
    internal TextMetrics Metrics(float logicalFontSize, string? family, int weight)
        => _fonts.Metrics(logicalFontSize, family, weight, _scale);

    /// <inheritdoc cref="Fonts.MeasureRuns"/>
    internal float MeasureRuns(ReadOnlySpan<byte> utf8, float logicalFontSize, string? family, int weight)
        => _fonts.MeasureRuns(utf8, logicalFontSize, family, weight, _scale);

    internal IRasterCanvas Canvas => _canvas;

    internal IRasterBackend Backend => _backend;
    internal float Scale => _scale;
    internal RenderCaches Caches => _caches;
    internal Fonts Fonts => _fonts;
    internal bool CanBlurBackdrop => _backdrop;

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
