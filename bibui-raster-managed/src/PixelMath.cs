using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>
/// Integer colour arithmetic on packed premultiplied pixels. Every channel is
/// handled alike, so the same code serves BGRA and RGBA: only the source colour
/// is swizzled.
///
/// <para>Each operation rounds exactly as Skia 2.88's legacy N32 blitters do
/// (<c>SkPreMultiplyARGB</c>, <c>SkAlphaMulQ</c>, <c>SkAlpha255To256</c>,
/// <c>SkBlitRow::Color32</c>, <c>SkPMSrcOver</c>, <c>SkBlendARGB32</c>), so
/// that where the coverage agrees the pixels agree to the bit: the fixture
/// baselines are drawn by the Skia backend, and the managed backend is verified
/// against them.</para>
/// </summary>
public static class PixelMath
{
    private const uint MaskRB = 0x00FF00FF;

    /// <summary>(a·b)/255 rounded, for a, b in 0..255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MulDiv255Round(uint a, uint b)
    {
        uint t = a * b + 128;
        return (t + (t >> 8)) >> 8;
    }

    /// <summary>Straight-alpha <c>0xAARRGGBB</c> to premultiplied, each channel rounded.</summary>
    public static uint Premul(uint argb)
    {
        uint a = argb >> 24;
        if (a == 255) return argb;
        if (a == 0) return 0;
        uint r = MulDiv255Round((argb >> 16) & 0xFF, a);
        uint g = MulDiv255Round((argb >> 8) & 0xFF, a);
        uint b = MulDiv255Round(argb & 0xFF, a);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>Swaps the bytes at bits 0 and 16: <c>0xAARRGGBB</c> ↔ <c>0xAABBGGRR</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SwapRB(uint c) => (c & 0xFF00FF00) | ((c >> 16) & 0xFF) | ((c & 0xFF) << 16);

    /// <summary>All four channels times scale/256, truncating. scale is 0..256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MulQ(uint c, uint scale)
    {
        uint rb = ((c & MaskRB) * scale) >> 8;
        uint ag = ((c >> 8) & MaskRB) * scale;
        return (rb & MaskRB) | (ag & ~MaskRB);
    }

    /// <summary>A premultiplied colour scaled by an 8-bit coverage a, as a scale of a + 1 out of 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ScaleByCoverage(uint pm, uint alpha) => alpha >= 255 ? pm : MulQ(pm, alpha + 1);

    /// <summary>
    /// Src-over of a premultiplied colour onto dst: dst·(255−a + (255−a)/128)/256 with
    /// rounding, plus src.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SrcOverColor(uint src, uint dst)
    {
        uint inv = 255 - (src >> 24);
        inv += inv >> 7;
        return src + MulRound(dst, inv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MulRound(uint c, uint scale)
    {
        uint rb = (((c & MaskRB) * scale + 0x00800080) >> 8) & MaskRB;
        uint ag = (((c >> 8) & MaskRB) * scale + 0x00800080) & ~MaskRB;
        return rb | ag;
    }

    /// <summary>Src-over of a premultiplied pixel onto another: dst·(256 − a)/256 truncated, plus src.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SrcOver(uint src, uint dst) => src + MulQ(dst, 256 - (src >> 24));

    /// <summary>
    /// src blended over dst at coverage aa (0..255), as hairlines blend. Note
    /// that it rounds dst down slightly even at aa = 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BlendARGB32(uint src, uint dst, uint aa)
    {
        uint srcScale = aa + 1;
        uint prod = 0xFFFF - (src >> 24) * srcScale;
        uint dstScale = (prod + (prod >> 8)) >> 8;
        uint srcRB = (src & MaskRB) * srcScale;
        uint srcAG = ((src >> 8) & MaskRB) * srcScale;
        uint dstRB = (dst & MaskRB) * dstScale;
        uint dstAG = ((dst >> 8) & MaskRB) * dstScale;
        return (((srcRB + dstRB) >> 8) & MaskRB) | ((srcAG + dstAG) & ~MaskRB);
    }

    /// <summary>Src-over of one colour across a span (a fill when opaque), vectorised.</summary>
    internal static unsafe void BlendSpan(uint* dst, int count, uint src)
    {
        uint sa = src >> 24;
        if (sa == 255) { new Span<uint>(dst, count).Fill(src); return; }
        if (src == 0) return;
        uint inv = 255 - sa;
        inv += inv >> 7;
        int i = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<uint>.Count)
        {
            var vs = new Vector<uint>(src);
            var vinv = new Vector<uint>(inv);
            var mask = new Vector<uint>(MaskRB);
            var maskHi = new Vector<uint>(~MaskRB);
            var round = new Vector<uint>(0x00800080u);
            for (; i <= count - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                var d = Vector.Load(dst + i);
                var rb = Vector.ShiftRightLogical((d & mask) * vinv + round, 8) & mask;
                var ag = ((Vector.ShiftRightLogical(d, 8) & mask) * vinv + round) & maskHi;
                (vs + (rb | ag)).Store(dst + i);
            }
        }
        for (; i < count; i++) dst[i] = src + MulRound(dst[i], inv);
    }

    /// <summary>Coverage 0..1 to an 8-bit alpha, rounded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CoverageToAlpha(float cov)
    {
        if (cov >= 1f) return 255;
        if (!(cov > 0f)) return 0;
        return (int)(cov * 255f + 0.5f);
    }
}
