using System.Numerics;
using System.Runtime.InteropServices;
using Bibui.Draw;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// The Skia boundary for bibui's graphics value types. Internal to the Skia
/// backend: a consumer of the public API never needs SkiaSharp to name a
/// colour, a rect or a point. Every conversion is a field copy, so nothing
/// here can move a pixel.
/// </summary>
internal static class SkiaInterop
{
    public static SKColor ToSk(this Rgba c) => new(c.Argb);
    public static Rgba ToRgba(this SKColor c) => new((uint)c);

    public static SKRect ToSk(this RectF r) => new(r.Left, r.Top, r.Right, r.Bottom);
    public static RectF ToRectF(this SKRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static SKRectI ToSk(this PixelRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static SKPoint ToSk(this Vector2 p) => new(p.X, p.Y);

    // The bibui enums are declared with Skia's values, so these are casts.
    public static SKPaintStyle ToSk(this PaintStyle s) => (SKPaintStyle)(int)s;
    public static SKStrokeCap ToSk(this StrokeCap c) => (SKStrokeCap)(int)c;
    public static SKStrokeJoin ToSk(this StrokeJoin j) => (SKStrokeJoin)(int)j;
    /// <summary>
    /// How an image is sampled at a filter quality: Skia's own mapping of its old
    /// filter qualities (<c>SkSamplingPriv::FromFQ</c>), so pixels drawn at a
    /// quality are the ones that quality drew before sampling options replaced it.
    /// </summary>
    public static SKSamplingOptions ToSk(this FilterQuality q) => q switch
    {
        FilterQuality.None => new(SKFilterMode.Nearest, SKMipmapMode.None),
        FilterQuality.Low => new(SKFilterMode.Linear, SKMipmapMode.None),
        FilterQuality.Medium => new(SKFilterMode.Linear, SKMipmapMode.Nearest),
        _ => new(SKCubicResampler.Mitchell),
    };
    public static SKPointMode ToSk(this PointMode m) => (SKPointMode)(int)m;

    /// <summary>Same packing (0xAARRGGBB), so a span reinterprets rather than copies.</summary>
    public static ReadOnlySpan<SKColor> AsSk(this ReadOnlySpan<Rgba> colors)
        => MemoryMarshal.Cast<Rgba, SKColor>(colors);

    /// <summary>Same layout (two floats), so a span reinterprets rather than copies.</summary>
    public static ReadOnlySpan<SKPoint> AsSk(this ReadOnlySpan<Vector2> points)
        => MemoryMarshal.Cast<Vector2, SKPoint>(points);

    // Affine only: bibui never sets perspective, so the bottom row is (0, 0, 1)
    // both ways and the six coefficients copy across unchanged.
    public static Matrix3x2 ToMatrix3x2(this in SKMatrix m)
        => new(m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY);

    public static SKMatrix ToSk(this in Matrix3x2 m)
        => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0f, 0f, 1f);
}
