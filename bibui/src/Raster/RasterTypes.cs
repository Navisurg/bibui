using System.Numerics;
using Bibui.Renderer;

namespace Bibui.Raster;

/// <summary>
/// Everything a shape draw needs to know about how to paint it: the
/// <see cref="PaintSpec"/> (colour, style, stroke) plus the effects bibui uses —
/// a gradient, a Gaussian blur of the shape's coverage (shadows), a Gaussian
/// blur of what is drawn (backdrops) and a dash pattern.
///
/// <para>A description, not an object: a backend that keeps native paint
/// objects (Skia) caches them by this value's content, and one that does not
/// (a managed rasterizer) reads the fields directly. Either way, building one
/// allocates nothing.</para>
/// </summary>
public readonly record struct RasterPaint
{
    public PaintSpec Spec { get; init; }

    /// <summary>Replaces the colour with a gradient; the paint's alpha still modulates it. Null for a solid colour.</summary>
    public RasterShader? Shader { get; init; }

    /// <summary>
    /// Gaussian sigma, in physical px, applied to the shape's coverage before it
    /// is filled — a soft shadow. 0 for none.
    /// </summary>
    public float MaskBlurSigma { get; init; }

    /// <summary>
    /// Gaussian sigma, in physical px, applied to what this paint draws — used
    /// on an image to blur a backdrop. 0 for none.
    /// </summary>
    public float ImageBlurSigma { get; init; }

    /// <summary>A dash pattern for strokes. Null for a solid stroke.</summary>
    public DashPattern? Dash { get; init; }

    public RasterPaint(in PaintSpec spec) => Spec = spec;

    public static implicit operator RasterPaint(in PaintSpec spec) => new(spec);
}

/// <summary>On/off dash lengths and a phase, in physical px.</summary>
public readonly record struct DashPattern(float On, float Off, float Phase = 0f);

/// <summary>How <see cref="IRasterCanvas.DrawPoints"/> joins its points.</summary>
public enum PointMode
{
    /// <summary>Each point is a dot (a square or round cap per the paint).</summary>
    Points = 0,
    /// <summary>Each pair of points is a separate line.</summary>
    Lines = 1,
    /// <summary>The points are one open polyline.</summary>
    Polygon = 2,
}

/// <summary>A rect on the pixel grid, by its edges.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Pixel memory the caller owns, for <see cref="IRasterBackend.CreateSurface(in RasterPixels)"/>:
/// BGRA8888 premultiplied — the layout of a <c>wl_shm</c> ARGB8888 buffer on a
/// little-endian machine.
/// </summary>
/// <param name="Address">The first byte of the top row. Must stay valid and pinned while a surface uses it.</param>
/// <param name="RowBytes">Bytes from one row to the next; at least <c>Width * 4</c>.</param>
public readonly record struct RasterPixels(nint Address, int Width, int Height, int RowBytes);

/// <summary>
/// A linear or radial gradient, clamped at its ends. Immutable, and carries
/// its content key so a backend that caches native shaders can find one
/// without hashing the stops again.
/// </summary>
public sealed class RasterShader
{
    public GradientType Type { get; }

    /// <summary>The start point (linear) or the centre (radial), in physical px.</summary>
    public Vector2 Start { get; }

    /// <summary>The end point (linear). Equal to <see cref="Start"/> for a radial gradient.</summary>
    public Vector2 End { get; }

    /// <summary>The radius (radial), in physical px. 0 for linear.</summary>
    public float Radius { get; }

    public Rgba[] Colors { get; }

    /// <summary>Stop positions 0..1, one per colour; null means evenly spread.</summary>
    public float[]? Stops { get; }

    /// <summary>A hash of every field above. Equal keys mean an equal gradient.</summary>
    public CacheKey Key { get; }

    private RasterShader(GradientType type, Vector2 start, Vector2 end, float radius, Rgba[] colors, float[]? stops, CacheKey key)
    {
        Type = type;
        Start = start;
        End = end;
        Radius = radius;
        Colors = colors;
        Stops = stops;
        Key = key;
    }

    /// <summary>
    /// A linear gradient from <paramref name="start"/> to <paramref name="end"/>.
    /// Empty <paramref name="stops"/> spreads the colours evenly.
    /// </summary>
    public static RasterShader Linear(Vector2 start, Vector2 end, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
        => new(GradientType.Linear, start, end, 0f, colors.ToArray(),
            // No stops means evenly distributed — null, not an empty array, which
            // is a different (and malformed) request.
            stops.IsEmpty ? null : stops.ToArray(),
            CacheKey.OfGradient(0, start, end, 0f, colors, stops));

    /// <summary>A radial gradient around <paramref name="centre"/>.</summary>
    public static RasterShader Radial(Vector2 centre, float radius, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
        => new(GradientType.Radial, centre, centre, radius, colors.ToArray(),
            // Radial passes its stops through as given, empty included — the
            // Skia call before the raster backend seam did, and the pixels are pinned to it.
            stops.ToArray(),
            CacheKey.OfGradient(1, centre, centre, radius, colors, stops));
}

/// <summary>
/// The verbs a path is built from. <see cref="RasterPath"/> implements it, and
/// so can anything that wants to record a path before a backend exists to
/// hold it (the SVG icon parser does).
/// </summary>
public interface IPathBuilder
{
    void MoveTo(float x, float y);
    void LineTo(float x, float y);
    void QuadTo(float x1, float y1, float x2, float y2);
    void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3);

    /// <summary>
    /// An SVG elliptical arc from the current point to (<paramref name="x"/>,
    /// <paramref name="y"/>). <paramref name="sweep"/> true goes the way of
    /// increasing angle — clockwise on a y-down screen.
    /// </summary>
    void ArcTo(float rx, float ry, float xAxisRotate, bool largeArc, bool sweep, float x, float y);

    /// <summary>
    /// An arc of the ellipse inscribed in <paramref name="oval"/>, as a new
    /// contour. Angles in degrees, 0 along +x, positive clockwise on screen.
    /// </summary>
    void AddArc(RectF oval, float startAngle, float sweepAngle);

    void Close();
}

/// <summary>
/// A path a backend holds in its own form. Build it with the
/// <see cref="IPathBuilder"/> verbs, draw it with
/// <see cref="IRasterCanvas.DrawPath"/>.
/// </summary>
public abstract class RasterPath : IPathBuilder, IDisposable
{
    public abstract void MoveTo(float x, float y);
    public abstract void LineTo(float x, float y);
    public abstract void QuadTo(float x1, float y1, float x2, float y2);
    public abstract void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3);
    public abstract void ArcTo(float rx, float ry, float xAxisRotate, bool largeArc, bool sweep, float x, float y);
    public abstract void AddArc(RectF oval, float startAngle, float sweepAngle);
    public abstract void Close();

    /// <summary>Appends <paramref name="source"/>'s contours mapped through <paramref name="transform"/>.</summary>
    public abstract void AddPath(RasterPath source, in Matrix3x2 transform);

    /// <summary>Empties the path, keeping its storage for the next build.</summary>
    public abstract void Rewind();

    public abstract bool IsEmpty { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }
}
