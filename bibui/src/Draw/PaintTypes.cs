using System.Numerics;

namespace Bibui.Draw;

/// <summary>
/// Everything a shape draw needs to know about how to paint it: the
/// <see cref="PaintSpec"/> (colour, style, stroke) plus the effects bibui uses —
/// a gradient, a Gaussian blur of the shape's coverage (shadows), a Gaussian
/// blur of what is drawn (backdrops) and a dash pattern. What a
/// <see cref="Paint"/> becomes on its way into a <see cref="DrawOp"/>, which
/// carries the same fields as values.
/// </summary>
internal readonly record struct RasterPaint
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

/// <summary>How a <see cref="DrawOpKind.Points"/> op joins its points.</summary>
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
/// Pixel memory the caller owns, for a renderer to draw a frame into: BGRA8888
/// premultiplied — the layout of a <c>wl_shm</c> ARGB8888 buffer on a
/// little-endian machine.
/// </summary>
/// <param name="Address">The first byte of the top row. Must stay valid and pinned while a renderer draws into it.</param>
/// <param name="RowBytes">Bytes from one row to the next; at least <c>Width * 4</c>.</param>
public readonly record struct RasterPixels(nint Address, int Width, int Height, int RowBytes);

/// <summary>
/// A linear or radial gradient, clamped at its ends. Immutable: a
/// <see cref="Paint"/>'s shader, recorded into a <see cref="DrawList"/>'s
/// payload as its colours and stops.
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

    private RasterShader(GradientType type, Vector2 start, Vector2 end, float radius, Rgba[] colors, float[]? stops)
    {
        Type = type;
        Start = start;
        End = end;
        Radius = radius;
        Colors = colors;
        Stops = stops;
    }

    /// <summary>
    /// A linear gradient from <paramref name="start"/> to <paramref name="end"/>.
    /// Empty <paramref name="stops"/> spreads the colours evenly.
    /// </summary>
    public static RasterShader Linear(Vector2 start, Vector2 end, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
        => new(GradientType.Linear, start, end, 0f, colors.ToArray(),
            // No stops means evenly distributed — null, not an empty array, which
            // is a different (and malformed) request.
            stops.IsEmpty ? null : stops.ToArray());

    /// <summary>A radial gradient around <paramref name="centre"/>.</summary>
    public static RasterShader Radial(Vector2 centre, float radius, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
        => new(GradientType.Radial, centre, centre, radius, colors.ToArray(),
            // Radial passes its stops through as given, empty included — the
            // Skia call before the raster backend seam did, and the pixels are pinned to it.
            stops.ToArray());
}
