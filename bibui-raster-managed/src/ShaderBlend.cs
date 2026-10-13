using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>
/// Shaded pixels onto the destination, a vector of pixels at a time —
/// the same integer arithmetic <see cref="ShaderSink"/> did one pixel at a
/// time, so the bytes are unchanged: each pixel is scaled by its coverage
/// (<see cref="PixelMath.MulQ"/> by c + 1) and blended src-over
/// (<see cref="PixelMath.SrcOverColor"/>). Written branch-free: coverage
/// 255 scales by 256, which is the identity, and coverage 0 leaves dst alone.
/// </summary>
internal static unsafe class ShaderBlend
{
    private const uint MaskRB = 0x00FF00FF;

    /// <param name="row">The first destination pixel.</param>
    /// <param name="src">Premultiplied shaded pixels, in the destination's order.</param>
    /// <param name="cover">Coverage × paint alpha, 1..255.</param>
    /// <param name="mask">The clip mask under the first pixel, or null.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Blend(uint* row, ReadOnlySpan<uint> src, uint cover, byte* mask)
    {
        int len = src.Length;
        int i = 0;
        fixed (uint* s = src)
        {
            if (Vector.IsHardwareAccelerated)
            {
                int n = Vector<uint>.Count;
                var rb = new Vector<uint>(MaskRB);
                var ag = new Vector<uint>(~MaskRB);
                var round = new Vector<uint>(0x00800080u);
                var k255 = new Vector<uint>(255u);
                var vc = new Vector<uint>(cover);
                Span<uint> mbuf = stackalloc uint[Vector<uint>.Count];
                for (; i <= len - n; i += n)
                {
                    var sv = Vector.Load(s + i);
                    var c = vc;
                    if (mask != null)
                    {
                        for (int k = 0; k < n; k++) mbuf[k] = mask[i + k];
                        // MulDiv255Round(cover, m), exact for every m (255 included).
                        var t = vc * new Vector<uint>(mbuf) + new Vector<uint>(128u);
                        c = Vector.ShiftRightLogical(t + Vector.ShiftRightLogical(t, 8), 8);
                    }
                    var scale = c + Vector<uint>.One;
                    sv = (Vector.ShiftRightLogical((sv & rb) * scale, 8) & rb) | ((Vector.ShiftRightLogical(sv, 8) & rb) * scale & ag);
                    var inv = k255 - Vector.ShiftRightLogical(sv, 24);
                    inv += Vector.ShiftRightLogical(inv, 7);
                    var d = Vector.Load(row + i);
                    var drb = Vector.ShiftRightLogical((d & rb) * inv + round, 8) & rb;
                    var dag = ((Vector.ShiftRightLogical(d, 8) & rb) * inv + round) & ag;
                    (sv + (drb | dag)).Store(row + i);
                }
            }
            for (; i < len; i++)
            {
                uint c = cover;
                if (mask != null)
                {
                    uint m = mask[i];
                    c = m == 255 ? c : PixelMath.MulDiv255Round(c, m);
                    if (c == 0) continue;
                }
                uint p = PixelMath.ScaleByCoverage(s[i], c);
                row[i] = (p >> 24) == 255 ? p : PixelMath.SrcOverColor(p, row[i]);
            }
        }
    }
}
