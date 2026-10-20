using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bibui.Core.FreeType;

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

/// <summary>
/// Composites coverage masks into premultiplied 8888 pixels through the mask
/// gamma (<see cref="MaskGamma"/>) and the chosen <see cref="MaskBlend"/>, to the
/// bit (see the parity tests). Stateless; safe from any thread, provided no two
/// threads write the same pixels.
///
/// <para>A rasterizer that keeps its own clip stack and layers can use
/// <see cref="CompositeMask"/> for each <see cref="PlacedGlyph"/> with its device
/// clip rectangle, against whichever layer is current; for a non-rectangular clip
/// it multiplies its clip coverage into <c>table[coverage]</c> itself.</para>
/// </summary>
internal static class GlyphCompositor
{
    /// <summary>
    /// Blends one placed mask in <paramref name="color"/>, inside <paramref name="clip"/>
    /// (which is intersected with the target's bounds).
    /// </summary>
    public static void CompositeMask(in PixelTarget dst, in PlacedGlyph glyph, TextColor color,
        PixelClip clip, MaskBlend blend = MaskBlend.LegacyN32)
        => CompositeMask(dst, glyph, color, MaskGamma.Table(color), clip, blend);

    /// <summary>As <see cref="CompositeMask(in PixelTarget, in PlacedGlyph, TextColor, PixelClip, MaskBlend)"/>, with the gamma table already looked up.</summary>
    public static void CompositeMask(in PixelTarget dst, in PlacedGlyph glyph, TextColor color,
        ReadOnlySpan<byte> gammaTable, PixelClip clip, MaskBlend blend = MaskBlend.LegacyN32)
    {
        var m = glyph.Mask;
        if (m.IsEmpty || color.A == 0) return;
        clip = clip.Intersect(PixelClip.Of(dst.Width, dst.Height));
        int x0 = Math.Max(clip.Left, glyph.X), x1 = Math.Min(clip.Right, glyph.X + m.Width);
        int y0 = Math.Max(clip.Top, glyph.Y), y1 = Math.Min(clip.Bottom, glyph.Y + m.Height);
        if (x0 >= x1 || y0 >= y1) return;

        // The premultiplied source colour in memory order.
        int a = color.A;
        int r = MulDiv255Round(color.R, a), g = MulDiv255Round(color.G, a), b = MulDiv255Round(color.B, a);
        int c0, c2;
        if (dst.Order == PixelOrder.Rgba) { c0 = r; c2 = b; } else { c0 = b; c2 = r; }
        int c1 = g;

        ref byte lut = ref MemoryMarshal.GetReference(gammaTable);
        ReadOnlySpan<byte> cov = m.Coverage;

        if (blend == MaskBlend.LegacyN32)
        {
            // Sk4px: left = s ~* k; d = left + d ~* (255 - left.a), where a ~* b is
            // (a * (b + 1)) >> 8 per channel: SkAlphaMulQ on a packed pixel, two
            // channels per 32-bit multiply.
            uint pmc = (uint)(c0 | c1 << 8 | c2 << 16 | a << 24);
            ref byte covRef = ref MemoryMarshal.GetReference(cov);
            ref byte pxRef = ref MemoryMarshal.GetReference(dst.Pixels);
            for (int y = y0; y < y1; y++)
            {
                ref byte mrow = ref Unsafe.Add(ref covRef, (y - glyph.Y) * m.Width - glyph.X);
                ref uint row = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref pxRef, y * dst.Stride));
                for (int x = x0; x < x1; x++)
                {
                    int cv = Unsafe.Add(ref mrow, x);
                    if (cv == 0) continue;
                    int k = Unsafe.Add(ref lut, cv);
                    if (k == 0) continue;
                    uint left = MulQ(pmc, (uint)k + 1);
                    ref uint d = ref Unsafe.Add(ref row, x);
                    d = left + MulQ(d, 256 - (left >> 24));
                }
            }
            return;
        }

        for (int y = y0; y < y1; y++)
        {
            int mrow = (y - glyph.Y) * m.Width - glyph.X;
            Span<byte> row = dst.Pixels.Slice(y * dst.Stride);
            for (int x = x0; x < x1; x++)
            {
                int cv = cov[mrow + x];
                if (cv == 0) continue;
                int k = Unsafe.Add(ref lut, cv);
                if (k == 0) continue;
                ref byte px = ref row[x * 4];
                if (a == 255)
                {
                    // An opaque src-over is Src, and a masked Src is lerp_u8:
                    // d = div255(d * (255 - c) + s * c).
                    int inv = 255 - k;
                    px = (byte)Div255(px * inv + c0 * k);
                    Unsafe.Add(ref px, 1) = (byte)Div255(Unsafe.Add(ref px, 1) * inv + c1 * k);
                    Unsafe.Add(ref px, 2) = (byte)Div255(Unsafe.Add(ref px, 2) * inv + c2 * k);
                    Unsafe.Add(ref px, 3) = (byte)Div255(Unsafe.Add(ref px, 3) * inv + 255 * k);
                }
                else
                {
                    // lowp: scale_u8 then srcover.
                    int l0 = Div255(c0 * k), l1 = Div255(c1 * k), l2 = Div255(c2 * k), la = Div255(a * k);
                    int inv = 255 - la;
                    px = (byte)(l0 + Div255(px * inv));
                    Unsafe.Add(ref px, 1) = (byte)(l1 + Div255(Unsafe.Add(ref px, 1) * inv));
                    Unsafe.Add(ref px, 2) = (byte)(l2 + Div255(Unsafe.Add(ref px, 2) * inv));
                    Unsafe.Add(ref px, 3) = (byte)(la + Div255(Unsafe.Add(ref px, 3) * inv));
                }
            }
        }
    }

    /// <summary>Composites every placed glyph, in order.</summary>
    public static void Composite(in PixelTarget dst, ReadOnlySpan<PlacedGlyph> glyphs, TextColor color,
        PixelClip clip, MaskBlend blend = MaskBlend.LegacyN32)
    {
        var table = MaskGamma.Table(color);
        foreach (ref readonly var g in glyphs) CompositeMask(dst, g, color, table, clip, blend);
    }

    // SkAlphaMulQ: each byte of c times scale (0..256), >> 8.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MulQ(uint c, uint scale)
        => ((c & 0x00FF00FFu) * scale >> 8 & 0x00FF00FFu) | ((c >> 8 & 0x00FF00FFu) * scale & 0xFF00FF00u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Div255(int v) => (v + 255) >> 8;

    // SkMulDiv255Round: exact rounding of a*b/255.
    private static int MulDiv255Round(int a, int b)
    {
        int prod = a * b + 128;
        return (prod + (prod >> 8)) >> 8;
    }
}
