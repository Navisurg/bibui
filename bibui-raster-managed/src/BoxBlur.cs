using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Bibui.Raster.Managed;

/// <summary>
/// The three-box Gaussian approximation both blurs use (<see cref="GaussianMaskBlur"/>
/// for σ ≥ 2 and <see cref="ImageBlur"/>): three running sums stacked, so the output is the exact convolution with
/// box(w)·box(w)·box(w or w + 1), never rounded between boxes. The result is
/// (sum·weight + add) >> 32, where weight = round(2³²/divisor) and add is the
/// rounding each caller uses.
///
/// <para>Coordinates: input element j sits at output position j + inStart;
/// input outside [0, inLen) is zero (a decal edge), and output o is
/// centred on position o.</para>
/// </summary>
internal static class BoxBlur
{
    /// <summary>
    /// Every column of a block of rows at once (vectorised across the row):
    /// input rows [0, inLen) of <paramref name="width"/> lanes to output rows
    /// [0, outLen). Values are 0..255 in 32-bit lanes.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static unsafe void Columns(ReadOnlySpan<uint> input, int width, int inStart, int inLen,
        Span<uint> output, int outLen, int window, ulong weight, ulong add, ref uint[] scratch)
    {
        if (input.Length < inLen * width || output.Length < outLen * width) throw new ArgumentException("Too small.");
        var (wa, wb, wc) = GaussianMaskBlur.BoxWidths(window);
        int border = GaussianMaskBlur.BoxBorder(window);
        int ringRows = (wa - 1) + (wb - 1) + (wc - 1);
        // Strips of lanes, so the sums and their rings stay in cache for the
        // whole height instead of streaming through it once per row.
        const int Strip = 128;
        int sw = Math.Min(Strip, width);
        int need = (4 + ringRows) * sw;
        if (scratch.Length < need) scratch = new uint[need];
        int start = Math.Max(-border, inStart - 3 * window);
        for (int o = 0; o < Math.Min(outLen, start - border); o++) output.Slice(o * width, width).Clear();
        int end = outLen + border;
        uint wh = (uint)(weight >> 16), wl = (uint)(weight & 0xFFFF);
        uint ah = (uint)(add >> 16), al = (uint)(add & 0xFFFF);
        fixed (uint* pin = input, pout = output, pbuf = scratch)
        {
            for (int x0 = 0; x0 < width; x0 += sw)
            {
                int n = Math.Min(sw, width - x0);
                new Span<uint>(pbuf, need).Clear();
                uint* s0 = pbuf, s1 = pbuf + sw, s2 = pbuf + 2 * sw, zero = pbuf + 3 * sw;
                uint* r0 = pbuf + 4 * sw, r1 = r0 + (wa - 1) * sw, r2 = r1 + (wb - 1) * sw;
                int c0 = 0, c1 = 0, c2 = 0;
                for (int v = start; v < end; v++)
                {
                    int j = v - inStart;
                    uint* lead = (uint)j < (uint)inLen ? pin + (long)j * width + x0 : zero;
                    int o = v - border;
                    uint* outRow = o >= 0 ? pout + (long)o * width + x0 : null;
                    Step(lead, s0, s1, s2, r0 + c0 * sw, r1 + c1 * sw, r2 + c2 * sw, outRow, n, wh, wl, ah, al);
                    if (++c0 == wa - 1) c0 = 0;
                    if (++c1 == wb - 1) c1 = 0;
                    if (++c2 == wc - 1) c2 = 0;
                }
            }
        }
    }

    /// <summary>
    /// One row of the stacked running sums, every lane at once. The final
    /// (c·weight + add) >> 32 is exact in 32-bit lanes: c·weight is at most
    /// 255·2³², so splitting weight and c into 16-bit halves keeps every partial
    /// product under 2³² (see <see cref="Scale"/>).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static unsafe void Step(uint* pl, uint* p0, uint* p1, uint* p2, uint* q0, uint* q1, uint* q2, uint* po,
        int len, uint wh, uint wl, uint ah, uint al)
    {
        int x = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            // Sixteen lanes where the hardware has them (AVX-512); the same integers.
            var vwh = Vector512.Create(wh);
            var vwl = Vector512.Create(wl);
            var vah = Vector512.Create(ah);
            var val = Vector512.Create(al);
            var m16 = Vector512.Create(0xFFFFu);
            for (; x <= len - 16; x += 16)
            {
                var l = Vector512.Load(pl + x);
                var a = Vector512.Load(p0 + x) + l;
                var b = Vector512.Load(p1 + x) + a;
                var c = Vector512.Load(p2 + x) + b;
                if (po != null)
                {
                    var hiPart = c * vwh + Vector512.ShiftRightLogical(c, 16) * vwl + vah;
                    var low = (c & m16) * vwl + val;
                    Vector512.ShiftRightLogical(hiPart + Vector512.ShiftRightLogical(low, 16), 16).Store(po + x);
                }
                (c - Vector512.Load(q2 + x)).Store(p2 + x);
                b.Store(q2 + x);
                (b - Vector512.Load(q1 + x)).Store(p1 + x);
                a.Store(q1 + x);
                (a - Vector512.Load(q0 + x)).Store(p0 + x);
                l.Store(q0 + x);
            }
        }
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<uint>.Count;
            var vwh = new Vector<uint>(wh);
            var vwl = new Vector<uint>(wl);
            var vah = new Vector<uint>(ah);
            var val = new Vector<uint>(al);
            var m16 = new Vector<uint>(0xFFFF);
            for (; x <= len - n; x += n)
            {
                var l = Vector.Load(pl + x);
                var a = Vector.Load(p0 + x) + l;
                var b = Vector.Load(p1 + x) + a;
                var c = Vector.Load(p2 + x) + b;
                if (po != null)
                {
                    var hiPart = c * vwh + Vector.ShiftRightLogical(c, 16) * vwl + vah;
                    var low = (c & m16) * vwl + val;
                    Vector.ShiftRightLogical(hiPart + Vector.ShiftRightLogical(low, 16), 16).Store(po + x);
                }
                (c - Vector.Load(q2 + x)).Store(p2 + x);
                b.Store(q2 + x);
                (b - Vector.Load(q1 + x)).Store(p1 + x);
                a.Store(q1 + x);
                (a - Vector.Load(q0 + x)).Store(p0 + x);
                l.Store(q0 + x);
            }
        }
        for (; x < len; x++)
        {
            uint l = pl[x];
            uint a = p0[x] + l;
            uint b = p1[x] + a;
            uint c = p2[x] + b;
            if (po != null) po[x] = Scale(c, wh, wl, ah, al);
            p2[x] = c - q2[x]; q2[x] = b;
            p1[x] = b - q1[x]; q1[x] = a;
            p0[x] = a - q0[x]; q0[x] = l;
        }
    }

    /// <summary>
    /// (c·w + add) >> 32 in 32-bit arithmetic, w = wh·2¹⁶ + wl and add = ah·2¹⁶ + al:
    /// c·wh + (c >> 16)·wl + ah carries the high half, (c &amp; 0xFFFF)·wl + al the low
    /// one (under 2³² by construction), whose carry is added before the last shift.
    /// Exact while c·w ≤ 255·2³², which a box sum scaled by round(2³²/divisor) is.
    /// </summary>
    internal static uint Scale(uint c, uint wh, uint wl, uint ah, uint al)
    {
        uint hi = c * wh + (c >> 16) * wl + ah;
        uint low = (c & 0xFFFF) * wl + al;
        return (hi + (low >> 16)) >> 16;
    }

    /// <summary>
    /// <paramref name="src"/> (rows × cols, rows <paramref name="srcStride"/>
    /// apart; 0 means cols) into <paramref name="dst"/> (cols × rows, packed).
    /// 8×8 blocks go through AVX2 registers where there is AVX2; pure data
    /// movement, so the result is the same either way.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static unsafe void Transpose(ReadOnlySpan<uint> src, int rows, int cols, Span<uint> dst, int srcStride = 0)
    {
        int ss = srcStride == 0 ? cols : srcStride;
        if (rows <= 0 || cols <= 0) return;
        if (src.Length < (rows - 1) * ss + cols || dst.Length < rows * cols) throw new ArgumentException("Too small.");
        fixed (uint* ps = src, pd = dst)
        {
            int R8 = rows & ~7, C8 = cols & ~7;
            if (Avx2.IsSupported)
            {
                for (int c0 = 0; c0 < C8; c0 += 8)
                    for (int r0 = 0; r0 < R8; r0 += 8)
                        Block8(ps + (long)r0 * ss + c0, ss, pd + (long)c0 * rows + r0, rows);
            }
            else
            {
                for (int r = 0; r < R8; r++)
                    for (int c = 0; c < C8; c++) pd[(long)c * rows + r] = ps[(long)r * ss + c];
            }
            // The ragged edges.
            for (int r = 0; r < rows; r++)
                for (int c = r < R8 ? C8 : 0; c < cols; c++) pd[(long)c * rows + r] = ps[(long)r * ss + c];
        }
    }

    private static unsafe void Block8(uint* s, int ss, uint* d, int ds)
    {
        var r0 = Vector256.Load(s);
        var r1 = Vector256.Load(s + ss);
        var r2 = Vector256.Load(s + 2 * ss);
        var r3 = Vector256.Load(s + 3 * ss);
        var r4 = Vector256.Load(s + 4 * ss);
        var r5 = Vector256.Load(s + 5 * ss);
        var r6 = Vector256.Load(s + 6 * ss);
        var r7 = Vector256.Load(s + 7 * ss);
        var t0 = Avx2.UnpackLow(r0, r1);
        var t1 = Avx2.UnpackHigh(r0, r1);
        var t2 = Avx2.UnpackLow(r2, r3);
        var t3 = Avx2.UnpackHigh(r2, r3);
        var t4 = Avx2.UnpackLow(r4, r5);
        var t5 = Avx2.UnpackHigh(r4, r5);
        var t6 = Avx2.UnpackLow(r6, r7);
        var t7 = Avx2.UnpackHigh(r6, r7);
        var u0 = Avx2.UnpackLow(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        var u1 = Avx2.UnpackHigh(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        var u2 = Avx2.UnpackLow(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
        var u3 = Avx2.UnpackHigh(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
        var u4 = Avx2.UnpackLow(t4.AsUInt64(), t6.AsUInt64()).AsUInt32();
        var u5 = Avx2.UnpackHigh(t4.AsUInt64(), t6.AsUInt64()).AsUInt32();
        var u6 = Avx2.UnpackLow(t5.AsUInt64(), t7.AsUInt64()).AsUInt32();
        var u7 = Avx2.UnpackHigh(t5.AsUInt64(), t7.AsUInt64()).AsUInt32();
        Avx2.Permute2x128(u0, u4, 0x20).Store(d);
        Avx2.Permute2x128(u1, u5, 0x20).Store(d + ds);
        Avx2.Permute2x128(u2, u6, 0x20).Store(d + 2 * ds);
        Avx2.Permute2x128(u3, u7, 0x20).Store(d + 3 * ds);
        Avx2.Permute2x128(u0, u4, 0x31).Store(d + 4 * ds);
        Avx2.Permute2x128(u1, u5, 0x31).Store(d + 5 * ds);
        Avx2.Permute2x128(u2, u6, 0x31).Store(d + 6 * ds);
        Avx2.Permute2x128(u3, u7, 0x31).Store(d + 7 * ds);
    }

    /// <summary>
    /// The small-sigma Gaussian down every column at once: out = (½ +
    /// Σ mulhi(in·256, g)) >> 8 over taps that fall inside [0, rows), each
    /// tap's 0.16 weight <paramref name="g"/>[|k|].
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void SmallColumns(ReadOnlySpan<uint> input, int width, int rows, Span<uint> output, ReadOnlySpan<ushort> g)
    {
        int r = g.Length - 1;
        int n = Vector<uint>.Count;
        Span<uint> acc = width <= 4096 ? stackalloc uint[width] : new uint[width];
        for (int y = 0; y < rows; y++)
        {
            acc.Fill(0x80);
            int lo = Math.Max(0, y - r), hi = Math.Min(rows - 1, y + r);
            for (int k = lo; k <= hi; k++)
            {
                uint f = g[Math.Abs(k - y)];
                var row = input.Slice(k * width, width);
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var vf = new Vector<uint>(f);
                    for (; x <= width - n; x += n)
                        (new Vector<uint>(acc[x..]) + Vector.ShiftRightLogical(Vector.ShiftLeft(new Vector<uint>(row[x..]), 8) * vf, 16)).CopyTo(acc[x..]);
                }
                for (; x < width; x++) acc[x] += ((row[x] << 8) * f) >> 16;
            }
            var o = output.Slice(y * width, width);
            int i = 0;
            if (Vector.IsHardwareAccelerated)
                for (; i <= width - n; i += n) Vector.ShiftRightLogical(new Vector<uint>(acc[i..]), 8).CopyTo(o[i..]);
            for (; i < width; i++) o[i] = acc[i] >> 8;
        }
    }

    /// <summary>Bytes to 32-bit lanes.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void Widen(ReadOnlySpan<byte> src, Span<uint> dst)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<byte>.Count;
            for (; i <= src.Length - n; i += n)
            {
                Vector.Widen(new Vector<byte>(src[i..]), out var lo, out var hi);
                Vector.Widen(lo, out var a, out var b);
                Vector.Widen(hi, out var c, out var d);
                int q = Vector<uint>.Count;
                a.CopyTo(dst[i..]); b.CopyTo(dst[(i + q)..]); c.CopyTo(dst[(i + 2 * q)..]); d.CopyTo(dst[(i + 3 * q)..]);
            }
        }
        for (; i < src.Length; i++) dst[i] = src[i];
    }

    /// <summary>32-bit lanes holding 0..255 back to bytes.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void Narrow(ReadOnlySpan<uint> src, Span<byte> dst)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int n = Vector<byte>.Count, q = Vector<uint>.Count;
            for (; i <= src.Length - n; i += n)
            {
                var lo = Vector.Narrow(new Vector<uint>(src[i..]), new Vector<uint>(src[(i + q)..]));
                var hi = Vector.Narrow(new Vector<uint>(src[(i + 2 * q)..]), new Vector<uint>(src[(i + 3 * q)..]));
                Vector.Narrow(lo, hi).CopyTo(dst[i..]);
            }
        }
        for (; i < src.Length; i++) dst[i] = (byte)src[i];
    }
}

/// <summary>
/// The Gaussian image blur, for backdrop blur (frosted glass) and
/// <c>ImageBlurSigma</c>: the three-box approximation over each channel of
/// premultiplied 32-bit pixels, horizontal then vertical, transparent outside
/// the source (decal), rounded as ((sum + ⌈divisor/2⌉)·round(2³²/divisor)) >> 32.
///
/// <para>This reproduces Skia 2.88's CPU image blur exactly
/// (<c>SkBlurImageFilter</c>'s <c>cpu_blur</c> and <c>blur_one_direction</c>):
/// the fixture baselines are drawn by the Skia backend, so the managed backend
/// matches its pixels byte for byte to be verified against them.</para>
/// </summary>
public static class ImageBlur
{
    [ThreadStatic] private static uint[]? t_lines;
    [ThreadStatic] private static uint[]? t_scratch;

    /// <summary>The box width for a sigma, as the image filter computes it (σ pinned to 0..136).</summary>
    public static int Window(float sigma) => GaussianMaskBlur.BoxWindow(Math.Clamp((double)sigma, 0.0, 136.0));

    /// <summary>How far, in px, the blur of <paramref name="sigma"/> reaches: the output bounds are the input's outset by this.</summary>
    public static int Spread(float sigma) => (int)MathF.Ceiling(sigma * 3f);

    /// <summary>
    /// Blurs the pixels of <paramref name="src"/> (rows of <paramref name="srcStride"/>,
    /// covering <paramref name="srcRect"/>) into <paramref name="dst"/>, which covers
    /// <paramref name="dstRect"/> in the same coordinates (rows of dstRect.Width).
    /// Channels are blurred independently, so any 8888 order works.
    /// A window of 1 (σ under about 0.53) copies.
    /// </summary>
    public static void Blur(ReadOnlySpan<uint> src, int srcStride, IRect srcRect, Span<uint> dst, IRect dstRect, float sigma)
    {
        int dw = dstRect.Width, dh = dstRect.Height, sw = srcRect.Width, sh = srcRect.Height;
        if (dw <= 0 || dh <= 0) return;
        int window = Window(sigma);
        if (window <= 1 || sw <= 0 || sh <= 0)
        {
            Copy(src, srcStride, srcRect, dst, dstRect);
            return;
        }
        long divisor = GaussianMaskBlur.BoxDivisor(window);
        ulong weight = GaussianMaskBlur.BoxWeight(window) & 0xFFFFFFFF;
        ulong add = (ulong)((divisor + 1) / 2) * weight;

        // Horizontal first — run as a vertical pass over the
        // transposed source, so every channel of every row is a vector lane.
        // Horizontal first, run as a vertical pass over the
        // transposed source; each byte of a pixel is a 32-bit lane, in memory
        // order, so lanes narrow straight back into pixels.
        int tl = sh * 4, ol = dw * 4;
        int px = Math.Max(sw * sh, Math.Max(dw * sh, 0));
        int need = 2 * px + sw * tl + dw * tl + sh * ol + dh * ol;
        var lines = t_lines is { } l && l.Length >= need ? l : (t_lines = new uint[need]);
        var p1 = lines.AsSpan(0, px);
        var p2 = lines.AsSpan(px, px);
        int at = 2 * px;
        var t = lines.AsSpan(at, sw * tl); at += sw * tl;
        var h = lines.AsSpan(at, dw * tl); at += dw * tl;
        var v = lines.AsSpan(at, sh * ol); at += sh * ol;
        var o = lines.AsSpan(at, dh * ol);
        var scratch = t_scratch ??= new uint[64];
        BoxBlur.Transpose(src, sh, sw, p1, srcStride);                          // sw × sh pixels
        BoxBlur.Widen(System.Runtime.InteropServices.MemoryMarshal.AsBytes(p1[..(sw * sh)]), t);
        BoxBlur.Columns(t, tl, srcRect.Left - dstRect.Left, sw, h, dw, window, weight, add, ref scratch);
        BoxBlur.Narrow(h, System.Runtime.InteropServices.MemoryMarshal.AsBytes(p2[..(dw * sh)]));
        BoxBlur.Transpose(p2, dw, sh, p1);                                      // sh × dw pixels
        BoxBlur.Widen(System.Runtime.InteropServices.MemoryMarshal.AsBytes(p1[..(dw * sh)]), v);
        BoxBlur.Columns(v, ol, srcRect.Top - dstRect.Top, sh, o, dh, window, weight, add, ref scratch);
        t_scratch = scratch;
        BoxBlur.Narrow(o, System.Runtime.InteropServices.MemoryMarshal.AsBytes(dst[..(dw * dh)]));
    }

    private static void Copy(ReadOnlySpan<uint> src, int srcStride, IRect srcRect, Span<uint> dst, IRect dstRect)
    {
        dst[..(dstRect.Width * dstRect.Height)].Clear();
        var b = srcRect.Intersect(dstRect);
        for (int y = b.Top; y < b.Bottom; y++)
            src.Slice((y - srcRect.Top) * srcStride + (b.Left - srcRect.Left), b.Width)
               .CopyTo(dst.Slice((y - dstRect.Top) * dstRect.Width + (b.Left - dstRect.Left), b.Width));
    }
}
