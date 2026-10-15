using System.Numerics;

namespace Bibui.Raster;

// Everything bibui needs from a rasterizer, and nothing it does not.
//
// The renderer lays out, resolves theme colours and logical->physical scale,
// and decides what to draw; a backend only turns resolved, physical-pixel
// draw calls into pixels. So the vocabulary here is bibui's own value types
// (Rgba, RectF, Vector2, PaintSpec) plus a handful of handles a backend
// subclasses (RasterPath, RasterTypeface, RasterImage). Nothing in a
// signature is a Skia type, and nothing assumes a native object behind a
// handle: a managed rasterizer implements every one of these in C#.
//
// Selection is explicit: a renderer is handed an instance (BibuiRenderer,
// BibuiRuntime constructors) and the default is the Skia backend, named in
// code — no discovery, no reflection, so it survives trimming and AOT.

/// <summary>
/// A raster backend: surfaces, paths, images, typefaces and text measurement,
/// plus the frame clock its own caches run on.
///
/// <para>One instance per renderer. A backend may keep per-frame caches (Skia
/// keeps its paints there), and <see cref="EndFrame"/> is the renderer telling
/// it a frame is done — two renderers sharing one would age its caches twice
/// as fast. The renderer that is handed a backend owns it.</para>
///
/// <para>Not thread-safe, like the renderer: frames are single-flighted by the
/// runtime's render lock. The one exception is <see cref="EncodePng"/>.</para>
/// </summary>
public interface IRasterBackend : IDisposable
{
    /// <summary>A short name for logs and benches, e.g. <c>"skia"</c>.</summary>
    string Name { get; }

    // ── Surfaces ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A CPU surface the backend allocates: <paramref name="width"/> by
    /// <paramref name="height"/> physical pixels, BGRA8888 premultiplied,
    /// cleared to transparent.
    /// </summary>
    IRasterSurface CreateSurface(int width, int height);

    /// <summary>
    /// A surface that draws straight into memory the caller owns — a
    /// <c>wl_shm</c> buffer, a mapped texture. The memory must stay valid and
    /// pinned for the surface's lifetime.
    /// </summary>
    IRasterSurface CreateSurface(in RasterPixels pixels);

    // ── Geometry ─────────────────────────────────────────────────────────────

    /// <summary>An empty path. The caller owns it and disposes it.</summary>
    RasterPath CreatePath();

    // ── Images and encoded bitmaps ───────────────────────────────────────────

    /// <summary>An immutable image copied from straight-alpha RGBA8888 pixels.</summary>
    RasterImage CreateImage(ReadOnlySpan<byte> rgba, int width, int height);

    /// <summary>A decoded PNG (or any format the backend reads), or null when it cannot be decoded.</summary>
    RasterImage? DecodeImage(ReadOnlySpan<byte> encoded);

    /// <summary>
    /// PNG bytes for a tightly packed BGRA8888 premultiplied frame. Must be safe
    /// to call from any thread: a render output encodes lazily, on whichever
    /// thread first asks for its PNG.
    /// </summary>
    byte[] EncodePng(ReadOnlySpan<byte> bgraPremul, int width, int height);

    // ── Typefaces ────────────────────────────────────────────────────────────

    /// <summary>A typeface from font file bytes (TTF/OTF), or null when they cannot be parsed.</summary>
    RasterTypeface? LoadTypeface(ReadOnlySpan<byte> data);

    /// <summary>An installed typeface by family name and weight, or null when the system has none.</summary>
    RasterTypeface? MatchFamily(string family, int weight);

    /// <summary>An installed typeface that has a glyph for <paramref name="codepoint"/>, or null.</summary>
    RasterTypeface? MatchCharacter(int codepoint, int weight);

    /// <summary>The backend's last-resort typeface. Never null.</summary>
    RasterTypeface DefaultTypeface { get; }

    // ── Text ─────────────────────────────────────────────────────────────────
    // A run is one typeface: bibui splits a string by glyph coverage before it
    // gets here (FontProvider), so a backend never does font fallback itself.

    /// <summary>The advance width of <paramref name="text"/> in <paramref name="font"/>, in physical px.</summary>
    float MeasureText(in RasterFont font, ReadOnlySpan<char> text);

    /// <summary>Ascent, descent and leading of <paramref name="font"/>, in physical px.</summary>
    TextMetrics GetMetrics(in RasterFont font);

    /// <summary>
    /// The glyphs and advances <paramref name="text"/> maps to in
    /// <paramref name="font"/> — a shaped run that
    /// <see cref="IRasterCanvas.DrawGlyphRun"/> draws. A backend with a shaper
    /// (HarfBuzz) may substitute and kern here; the advances it returns must be
    /// the ones <see cref="MeasureText"/> sums.
    /// </summary>
    ShapedRun ShapeText(in RasterFont font, ReadOnlySpan<char> text);

    // ── Frame clock ──────────────────────────────────────────────────────────

    /// <summary>One frame finished: evict whatever the backend caches by touch.</summary>
    void EndFrame();
}

/// <summary>A target a frame is painted into: a canvas over pixels.</summary>
public interface IRasterSurface : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>The canvas that draws into this surface. The same instance for the surface's life.</summary>
    IRasterCanvas Canvas { get; }

    /// <summary>
    /// Copies the top-left <paramref name="width"/> by <paramref name="height"/>
    /// pixels into <paramref name="bgraPremul"/>, tightly packed BGRA8888
    /// premultiplied (<c>width * height * 4</c> bytes). Works for a GPU-backed
    /// surface too, on the thread that owns its context. False when the pixels
    /// cannot be read.
    /// </summary>
    bool ReadPixels(Span<byte> bgraPremul, int width, int height);
}

/// <summary>
/// The drawing calls. Coordinates are physical pixels under the current
/// transform; paints are fully resolved. Save/Restore bracket the transform,
/// the clip and layers, as a stack.
/// </summary>
public interface IRasterCanvas
{
    /// <summary>Fills the whole surface, ignoring the clip and transform.</summary>
    void Clear(Rgba color);

    // ── State ────────────────────────────────────────────────────────────────

    void Save();
    void Restore();
    void Translate(float dx, float dy);
    void Scale(float sx, float sy);

    /// <summary>The current transform, device from local. Affine only.</summary>
    Matrix3x2 TotalMatrix { get; }

    /// <summary>Replaces the current transform (restored by the enclosing <see cref="Restore"/>).</summary>
    void SetMatrix(in Matrix3x2 matrix);

    /// <summary>Intersects the clip with <paramref name="rect"/>, aliased (pixel-aligned edges stay crisp).</summary>
    void ClipRect(RectF rect);

    /// <summary>
    /// Intersects the clip with <paramref name="rect"/> rounded by
    /// <paramref name="radii"/> (physical px, already <see cref="CornerRadii.Fit"/>
    /// to the rect), antialiased. A backend that cannot clip to a curve may
    /// leave this to the default, which clips to the rect.
    /// </summary>
    void ClipRoundRect(RectF rect, CornerRadii radii) => ClipRect(rect);

    /// <summary>
    /// Starts an offscreen layer; the matching <see cref="Restore"/> composites
    /// it back at <paramref name="alpha"/>/255 opacity.
    /// </summary>
    void SaveLayerAlpha(byte alpha);

    // ── Shapes ───────────────────────────────────────────────────────────────

    /// <summary>A rounded rect; radii 0 is a plain rect.</summary>
    void DrawRoundRect(RectF rect, float rx, float ry, in RasterPaint paint);

    void DrawLine(float x0, float y0, float x1, float y1, in RasterPaint paint);

    void DrawPath(RasterPath path, in RasterPaint paint);

    void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in RasterPaint paint);

    // ── Images ───────────────────────────────────────────────────────────────

    /// <summary>Draws <paramref name="image"/> scaled into <paramref name="dest"/>, sampled per the paint's FilterQuality and modulated by its alpha.</summary>
    void DrawImage(RasterImage image, RectF dest, in RasterPaint paint);

    /// <summary>
    /// <see cref="DrawImage"/> over pixels the caller holds — straight-alpha
    /// RGBA8888, <c>width * 4</c> bytes a row — without making an image that
    /// outlives the call. For video frames and other per-frame bitmaps.
    /// </summary>
    void DrawPixels(ReadOnlySpan<byte> rgba, int width, int height, RectF dest, in RasterPaint paint);

    /// <summary>
    /// Blurs what is already painted: snapshots <paramref name="region"/> of the
    /// surface, blurs it by <paramref name="sigma"/> and draws it back clipped to
    /// <paramref name="clip"/> — frosted glass under a translucent fill. Device
    /// pixels; the current transform is not applied to the snapshot.
    /// </summary>
    void DrawBackdropBlur(PixelRect region, RectF clip, float sigma);

    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One run of text in one typeface, its baseline at (<paramref name="x"/>,
    /// <paramref name="baselineY"/>). Returns its advance — what
    /// <see cref="IRasterBackend.MeasureText"/> would — so a caller laying runs
    /// end to end does not ask twice.
    /// </summary>
    float DrawText(ReadOnlySpan<char> text, float x, float baselineY, in RasterFont font, Rgba color);

    /// <summary>
    /// A shaped run: <paramref name="glyphs"/> at <paramref name="positions"/>,
    /// which are relative to the origin (<paramref name="x"/>, <paramref name="baselineY"/>).
    /// </summary>
    void DrawGlyphRun(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, in RasterFont font, Rgba color);
}
