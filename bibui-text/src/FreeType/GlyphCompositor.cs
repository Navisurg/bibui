using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bibui.Text.FreeType;

/// <summary>Byte order of a 32-bit pixel in memory.</summary>
public enum PixelOrder
{
    /// <summary>R, G, B, A.</summary>
    Rgba,
    /// <summary>B, G, R, A: what bibui renders into on little-endian machines.</summary>
    Bgra,
}

/// <summary>
/// Which of two A8-mask blends to composite with. They differ by at most one
/// level per channel. Both are Skia 2.88's, reproduced exactly so text matches
/// the Skia-drawn fixture baselines; Skia picks one by the surface's pixel
/// format, so matching a given Skia surface means picking its blend.
/// </summary>
public enum MaskBlend
{
    /// <summary>
    /// The raster-pipeline blend (lowp): <c>s' = div255(s * c)</c>, then src-over
    /// <c>d = s' + div255(d * (255 - s'a))</c>, with <c>div255(v) = (v + 255) &gt;&gt; 8</c>.
    /// The blend for a surface that is not BGRA on little-endian, e.g. RGBA.
    /// </summary>
    RasterPipeline,

    /// <summary>
    /// The legacy N32 mask blend (SkBlitMask, Sk4px): <c>l = s * c ~/ 255</c>,
    /// <c>d = l + d * (255 - l.a) ~/ 255</c>, where <c>a ~/ 255 = (a * b + a) &gt;&gt; 8</c>.
    /// The blend for BGRA on little-endian: bibui's frames.
    /// </summary>
    LegacyN32,
}

/// <summary>An integer device rectangle, half-open: [Left, Right) x [Top, Bottom).</summary>
public readonly record struct PixelClip(int Left, int Top, int Right, int Bottom)
{
    public static PixelClip Of(int width, int height) => new(0, 0, width, height);

    public PixelClip Intersect(PixelClip o) => new(
        Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
}

/// <summary>
/// Premultiplied 8888 pixels the caller owns: <see cref="Width"/> by <see cref="Height"/>,
/// <see cref="Stride"/> bytes a row, in <see cref="Order"/>.
/// </summary>
public readonly ref struct PixelTarget
{
    public readonly Span<byte> Pixels;
    public readonly int Width, Height, Stride;
    public readonly PixelOrder Order;

    public PixelTarget(Span<byte> pixels, int width, int height, int stride, PixelOrder order = PixelOrder.Rgba)
    {
        if (width < 0 || height < 0 || stride < width * 4)
            throw new ArgumentException("Width, height and stride do not describe a 32-bit image.");
        if (height > 0 && pixels.Length < (long)stride * (height - 1) + width * 4)
            throw new ArgumentException("The pixel span is smaller than width, height and stride say.", nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Order = order;
    }
}
