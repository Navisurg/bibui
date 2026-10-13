namespace Bibui.Raster.Managed;

/// <summary>An axis-aligned rectangle by its edges.</summary>
public readonly record struct Rect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;

    /// <summary>True unless the rect has a positive width and height (NaN counts as empty).</summary>
    public bool IsEmpty => !(Right > Left && Bottom > Top);

    public static Rect FromXywh(float x, float y, float width, float height) => new(x, y, x + width, y + height);

    /// <summary>The same rect with Left ≤ Right and Top ≤ Bottom.</summary>
    public Rect Sorted() => new(MathF.Min(Left, Right), MathF.Min(Top, Bottom), MathF.Max(Left, Right), MathF.Max(Top, Bottom));

    public Rect Outset(float dx, float dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    /// <summary>The smallest pixel rect containing this one.</summary>
    public IRect RoundOut() => new(
        (int)MathF.Floor(Left), (int)MathF.Floor(Top), (int)MathF.Ceiling(Right), (int)MathF.Ceiling(Bottom));

    /// <summary>Each edge to the nearest pixel boundary, halves up — how an aliased clip snaps.</summary>
    public IRect Round() => new(
        (int)MathF.Floor(Left + 0.5f), (int)MathF.Floor(Top + 0.5f),
        (int)MathF.Floor(Right + 0.5f), (int)MathF.Floor(Bottom + 0.5f));
}

/// <summary>A rect on the pixel grid, by its edges (right and bottom exclusive).</summary>
public readonly record struct IRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public IRect Intersect(IRect o)
    {
        var r = new IRect(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
        return r.IsEmpty ? default : r;
    }

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// A rounded rect with the same elliptical radii at all four corners — what
/// bibui draws. Normalised on construction: the rect is
/// sorted, radii that do not fit are scaled down together, and a radius of 0 in
/// either axis makes a plain rect.
/// </summary>
public readonly record struct RRect
{
    public Rect Rect { get; }
    public float Rx { get; }
    public float Ry { get; }

    public RRect(Rect rect, float rx, float ry)
    {
        rect = rect.Sorted();
        if (!(rx > 0) || !(ry > 0)) { rx = 0; ry = 0; }
        float w = rect.Width, h = rect.Height;
        if (w < rx + rx || h < ry + ry)
        {
            float scale = MathF.Min(w / (rx + rx), h / (ry + ry));
            rx *= scale;
            ry *= scale;
        }
        if (!(rx > 0) || !(ry > 0)) { rx = 0; ry = 0; }
        Rect = rect;
        Rx = rx;
        Ry = ry;
    }

    public RRect(Rect rect, float radius) : this(rect, radius, radius) { }

    public bool IsRect => Rx == 0 || Ry == 0;

    /// <summary>The radii reach the middle of both sides: an ellipse.</summary>
    public bool IsOval => !IsRect && Rx >= Rect.Width * 0.5f && Ry >= Rect.Height * 0.5f;
}

/// <summary>Which points a path's contours enclose.</summary>
public enum FillRule
{
    /// <summary>Inside where the winding number is not zero.</summary>
    NonZero = 0,
    /// <summary>Inside where the winding number is odd.</summary>
    EvenOdd = 1,
}

/// <summary>
/// The byte order of a 32-bit premultiplied pixel. <see cref="Bgra8888"/> is
/// the native 32-bit order on little-endian (what bibui's surfaces use, and a <c>wl_shm</c>
/// ARGB8888 buffer): read as a <see cref="uint"/> it is <c>0xAARRGGBB</c>.
/// </summary>
public enum PixelOrder
{
    Bgra8888 = 0,
    Rgba8888 = 1,
}

/// <summary>How a <see cref="Canvas"/> antialiases shape edges.</summary>
public enum Antialiasing
{
    /// <summary>
    /// Each pixel's coverage is the exact area of it inside the shape, under
    /// the fill rule, however the shape overlaps itself.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Coverage computed exactly as Skia 2.88's CPU backend computes it, so a
    /// frame matches, byte for byte, the fixture baselines the Skia backend
    /// drew: a shape Skia would supersample (small curved paths, strokes) is
    /// sampled on a 4×4 grid with Skia's rounding; one it would fill
    /// analytically has its edge ends snapped to quarter-pixel rows. Overlaps
    /// are still resolved per sample, not by clamping summed coverage.
    /// </summary>
    BaselineCompatible = 1,
}

/// <summary>How the scanline core turns a canvas draw's edges into coverage.</summary>
internal enum RasterMode
{
    /// <summary>|accumulated area|: exact when winding is only 0 or ±1 (rects, rrects, rings).</summary>
    Simple,
    /// <summary>Exact area under the fill rule, with crossings resolved.</summary>
    Exact,
    /// <summary>The 4×4 supersampler (<see cref="Antialiasing.BaselineCompatible"/>).</summary>
    Supersample,
    /// <summary>The 4×4 supersampler, for shapes whose winding is only 0 or one sign of 1 (no sorting needed).</summary>
    SupersampleSimple,
}

/// <summary>Whether a paint fills the shape, strokes its outline, or both.</summary>
public enum PaintStyle { Fill = 0, Stroke = 1, StrokeAndFill = 2 }

/// <summary>How an open stroke ends.</summary>
public enum LineCap { Butt = 0, Round = 1, Square = 2 }

/// <summary>How a stroke turns a corner.</summary>
public enum LineJoin { Miter = 0, Round = 1, Bevel = 2 }

/// <summary>On/off dash lengths and a phase, in local units: one on interval and one off, repeated along the path by <see cref="Dasher"/>.</summary>
public readonly record struct DashPattern(float On, float Off, float Phase = 0f);
