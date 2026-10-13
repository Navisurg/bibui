using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>
/// Antialiased hairlines — one device pixel wide whatever the
/// transform.
///
/// <para>This reproduces Skia 2.88's antialiased hairlines exactly
/// (<c>SkScan_Antihair.cpp</c>: the 26.6 snap, the walk, and its
/// <c>blitAntiH</c>, <c>blitAntiH2</c>/<c>blitAntiV2</c> and <c>blitV</c>
/// blends): the fixture baselines are drawn by the Skia backend and nearly
/// every bibui border is a hairline, so the managed backend matches its pixels
/// bit for bit to be verified against them.</para>
///
/// <para>Each segment is snapped to 26.6 fixed point and walked along its major
/// axis one pixel at a time (Wu-style): the line's 16.16 position across the
/// minor axis splits 255 of coverage between the two pixels it falls between,
/// and the partial first and last pixels are scaled by how much of them the
/// segment covers. Segments are blended one after another, so where two meet
/// the pixel is blended twice.</para>
/// </summary>
internal unsafe struct HairlineBlitter : IHairlineSink
{
    public uint* Origin;
    public int Stride;
    public IRect Clip;
    public byte* Mask;
    public int MaskStride;
    public uint Pm;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint Coverage(int x, int y, uint a)
    {
        if (Mask == null) return a;
        return PixelMath.MulDiv255Round(a, Mask[(long)y * MaskStride + x]);
    }

    /// <summary>A two-pixel step's pixel: <see cref="PixelMath.BlendARGB32"/>, even at coverage 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void BlendAA(int x, int y, uint a)
    {
        if (!Clip.Contains(x, y)) return;
        ref uint d = ref Origin[(long)y * Stride + x];
        d = PixelMath.BlendARGB32(Pm, d, Coverage(x, y, a));
    }

    /// <summary>A horizontal run: scale by a + 1, then <see cref="PixelMath.SrcOverColor"/> (a fill when opaque at full coverage).</summary>
    private readonly void AntiH(int x, int y, int count, uint a)
    {
        if (a == 0 || y < Clip.Top || y >= Clip.Bottom) return;
        int x0 = Math.Max(x, Clip.Left), x1 = Math.Min(x + count, Clip.Right);
        for (int i = x0; i < x1; i++)
        {
            uint c = Coverage(i, y, a);
            if (c == 0) continue;
            ref uint d = ref Origin[(long)y * Stride + i];
            if (c == 255 && (Pm >> 24) == 255) d = Pm;
            else
            {
                uint sc = PixelMath.MulQ(Pm, c + 1);
                if ((sc >> 24) == 255) d = sc;
                else if (sc != 0) d = PixelMath.SrcOverColor(sc, d);
            }
        }
    }

    /// <summary>A vertical run: scale by a + 1, then dst·(256 − a′)/256 truncated, plus src.</summary>
    private readonly void V(int x, int y, int height, uint a)
    {
        if (a == 0 || (Pm >> 24) == 0 || x < Clip.Left || x >= Clip.Right) return;
        int y0 = Math.Max(y, Clip.Top), y1 = Math.Min(y + height, Clip.Bottom);
        for (int j = y0; j < y1; j++)
        {
            uint c = Coverage(x, j, a);
            if (c == 0) continue;
            uint color = c != 255 ? PixelMath.MulQ(Pm, c + 1) : Pm;
            uint dstScale = 256 - (color >> 24);
            ref uint d = ref Origin[(long)j * Stride + x];
            d = color + PixelMath.MulQ(d, dstScale);
        }
    }

    public void Line(Vector2 a, Vector2 b)
    {
        // SkScalarToFDot6 truncates toward zero.
        DoLine((int)(a.X * 64), (int)(a.Y * 64), (int)(b.X * 64), (int)(b.Y * 64));
    }

    private enum Kind { HLine, Horish, VLine, Vertish }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FDot6Div(int a, int b) => (int)Math.Clamp(((long)a << 16) / b, int.MinValue, int.MaxValue);

    /// <summary><c>do_anti_hairline</c>, without its clip (the pixel writes clip).</summary>
    private void DoLine(int x0, int y0, int x1, int y1)
    {
        if (Math.Abs(x1 - x0) > 511 * 64 || Math.Abs(y1 - y0) > 511 * 64)
        {
            int hx = (x0 >> 1) + (x1 >> 1), hy = (y0 >> 1) + (y1 >> 1);
            DoLine(x0, y0, hx, hy);
            DoLine(hx, hy, x1, y1);
            return;
        }

        int scaleStart, scaleStop, istart, istop, fstart, slope;
        Kind kind;
        if (Math.Abs(x1 - x0) > Math.Abs(y1 - y0))
        {
            if (x0 > x1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
            istart = x0 >> 6;
            istop = (x1 + 63) >> 6;
            fstart = y0 << 10;
            if (y0 == y1) { slope = 0; kind = Kind.HLine; }
            else
            {
                slope = FDot6Div(y1 - y0, x1 - x0);
                fstart += (slope * (32 - (x0 & 63)) + 32) >> 6;
                kind = Kind.Horish;
            }
            if (istop - istart == 1) { scaleStart = x1 - x0; scaleStop = 0; }
            else { scaleStart = 64 - (x0 & 63); scaleStop = x1 & 63; }
        }
        else
        {
            if (y0 > y1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
            istart = y0 >> 6;
            istop = (y1 + 63) >> 6;
            fstart = x0 << 10;
            if (x0 == x1)
            {
                if (y0 == y1) return;
                slope = 0;
                kind = Kind.VLine;
            }
            else
            {
                slope = FDot6Div(x1 - x0, y1 - y0);
                fstart += (slope * (32 - (y0 & 63)) + 32) >> 6;
                kind = Kind.Vertish;
            }
            if (istop - istart == 1) { scaleStart = y1 - y0; scaleStop = 0; }
            else { scaleStart = 64 - (y0 & 63); scaleStop = y1 & 63; }
        }

        fstart = DrawCap(kind, istart, fstart, slope, scaleStart);
        istart += 1;
        int fullSpans = istop - istart - (scaleStop > 0 ? 1 : 0);
        if (fullSpans > 0) fstart = DrawLine(kind, istart, istart + fullSpans, fstart, slope);
        if (scaleStop > 0) DrawCap(kind, istop - 1, fstart, slope, scaleStop);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Scale6(int value, int dot6) => (uint)((value * dot6) >> 6);

    private const int Half = 1 << 15;

    private int DrawCap(Kind kind, int i, int f, int slope, int mod64)
    {
        f += Half;
        int whole = f >> 16;
        int a = (f >> 8) & 0xFF;
        switch (kind)
        {
            case Kind.HLine:
            {
                uint ma = Scale6(a, mod64);
                if (ma != 0) AntiH(i, whole, 1, ma);
                ma = Scale6(255 - a, mod64);
                if (ma != 0) AntiH(i, whole - 1, 1, ma);
                return f - Half;
            }
            case Kind.Horish:
                BlendAA(i, whole - 1, Scale6(255 - a, mod64));
                BlendAA(i, whole, Scale6(a, mod64));
                return f + slope - Half;
            case Kind.VLine:
            {
                uint ma = Scale6(a, mod64);
                if (ma != 0) V(whole, i, 1, ma);
                ma = Scale6(255 - a, mod64);
                if (ma != 0) V(whole - 1, i, 1, ma);
                return f - Half;
            }
            default: // Vertish
                BlendAA(whole - 1, i, Scale6(255 - a, mod64));
                BlendAA(whole, i, Scale6(a, mod64));
                return f + slope - Half;
        }
    }

    private int DrawLine(Kind kind, int i, int stop, int f, int slope)
    {
        f += Half;
        switch (kind)
        {
            case Kind.HLine:
            {
                int y = f >> 16;
                uint a = (uint)((f >> 8) & 0xFF);
                if (a != 0) AntiH(i, y, stop - i, a);
                a = 255 - a;
                if (a != 0) AntiH(i, y - 1, stop - i, a);
                return f - Half;
            }
            case Kind.Horish:
                do
                {
                    int lower = f >> 16;
                    uint a = (uint)((f >> 8) & 0xFF);
                    BlendAA(i, lower - 1, 255 - a);
                    BlendAA(i, lower, a);
                    f += slope;
                } while (++i < stop);
                return f - Half;
            case Kind.VLine:
            {
                int x = f >> 16;
                uint a = (uint)((f >> 8) & 0xFF);
                if (a != 0) V(x, i, stop - i, a);
                a = 255 - a;
                if (a != 0) V(x - 1, i, stop - i, a);
                return f - Half;
            }
            default: // Vertish
                do
                {
                    int x = f >> 16;
                    uint a = (uint)((f >> 8) & 0xFF);
                    BlendAA(x - 1, i, 255 - a);
                    BlendAA(x, i, a);
                    f += slope;
                } while (++i < stop);
                return f - Half;
        }
    }
}
