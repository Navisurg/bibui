using Bibui.Theming;
using Bibui.Draw;
using Bibui.Renderer;

namespace Bibui;

/// <summary>
/// One frame, in two passes that never mix: <see cref="Layout"/> solves every
/// node's rect, then <see cref="Record"/> records the tree as laid out into
/// <see cref="DrawList"/>. Recording lays nothing out, and draws nothing: a
/// renderer the caller chose executes the list.
/// </summary>
public sealed class BibuiRenderer
{
    // One RenderCaches and one Fonts per renderer: typefaces load once, and
    // frames share the path caches and the measure and line caches.
    private readonly RenderCaches _caches;
    private readonly Fonts _fonts;
    private readonly Painter _painter;

    public BibuiRenderer()
    {
        _caches = new RenderCaches();
        _fonts = new Fonts();
        _painter = new Painter(_caches, _fonts);
        _measure = MeasureLeaf;
    }

    /// <summary>
    /// Whether the frame just painted left anything mid-transition — a fade
    /// still travelling, a spinner turning. A host that draws on demand reads
    /// this after each render to decide whether to schedule another; one that
    /// draws continuously can ignore it.
    /// </summary>
    public bool IsAnimating => _painter.IsAnimating;

    /// <summary>This renderer's fonts: what it measures and draws text with, and its caches.</summary>
    internal Fonts Fonts => _fonts;

    // ── Layout ───────────────────────────────────────────────────────────────

    // What a text is measured in, for the frame being laid out; a field so the
    // delegate the solver calls is made once.
    private readonly MeasureLeaf _measure;
    private ThemeTokens? _measureTheme;
    private float _measureScale = 1f;

    /// <summary>
    /// Lays <paramref name="root"/> out over the whole surface: every node's
    /// rect, extent and content size, for the paint and for the next frame's
    /// asks. A text measures itself in this renderer's fonts.
    /// </summary>
    public void Layout(UiNode root, SurfaceOptions surface, ThemeTokens theme)
    {
        (_measureTheme, _measureScale) = (theme, surface.ScaleFactor);
        LayoutSolver.Layout(root, System.Numerics.Vector2.Zero,
            new System.Numerics.Vector2(surface.Width, surface.Height), _measure);
    }

    // What a leaf sized by what it holds measures: a text its lines, an icon
    // its side.
    private System.Numerics.Vector2 MeasureLeaf(UiNode node, float width)
    {
        switch (node)
        {
            case Text text:
                var measured = _fonts.Measure(text, Fonts.FontOf(text, _measureTheme!, _measureScale));
                return new System.Numerics.Vector2(measured.Width, measured.Height);
            case Icon icon:
                float side = icon.Size.In(_measureTheme!);
                return new System.Numerics.Vector2(side, side);
            default:
                throw new InvalidOperationException($"{node.GetType().Name} '{node.Id}' is sized by its content, and has none.");
        }
    }

    // ── Record ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The last frame's draws: what <see cref="Record"/> wrote. Valid until the
    /// next frame records.
    /// </summary>
    public DrawList DrawList { get; } = new();

    /// <summary>
    /// Records the laid-out <paramref name="root"/> into <see cref="DrawList"/>,
    /// then ends the frame for the caches recording reads: what nobody asked for
    /// lately is evicted.
    /// </summary>
    public void Record(UiNode root, SurfaceOptions surface, ThemeTokens theme, FrameInput? input = null)
    {
        _painter.Record(DrawList, root, surface, theme, input ?? FrameInput.None);
        _caches.EndFrame();
        _fonts.EndFrame();
    }
}
