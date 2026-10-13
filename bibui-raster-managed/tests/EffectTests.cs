// Gradients, mask blur (shadows), the shadow cache, image and backdrop
// blur. The references are the definitions: Skia's stop rules and
// interpolation, its kernel sizes per sigma (measured against SkiaSharp 2.88.8
// with the bench's probes), and conservation of what a blur spreads.
using System.Numerics;
using Bibui.Raster.Managed;

static class EffectTests
{
    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static int Ch(uint argb, int shift) => (int)((argb >> shift) & 0xFF);

    // ── Gradients ────────────────────────────────────────────────────────

    public static void GradientTwoStopsInterpolateAndClamp()
    {
        var g = GradientShader.Linear(new Vector2(10, 0), new Vector2(265, 0), [0xFF000000, 0xFFFFFFFF]);
        g.Begin(Matrix3x2.Identity);
        var row = new uint[300];
        g.ShadeSpan(0, 0, row);
        // Pixel centre x + ½: t = (x + ½ − 10)/255, one level per pixel, rounded.
        for (int x = 0; x < 300; x++)
        {
            float t = Math.Clamp((x + 0.5f - 10) / 255f, 0f, 1f);
            int want = (int)(t * 255 + 0.5f);
            Assert(Math.Abs(Ch(row[x], 16) - want) <= 1 && (row[x] >> 24) == 255, $"x={x}: got {Ch(row[x], 16)}, want {want}");
        }
        Assert(row[0] == 0xFF000000 && row[299] == 0xFFFFFFFF, "clamped ends");
    }

    public static void GradientStopsAreNormalisedAsSkiaDoes()
    {
        // Stops not at 0 and 1: the end colours extend to them.
        var g = GradientShader.Linear(Vector2.Zero, new Vector2(1, 0), [0xFFFF0000, 0xFF0000FF], [0.25f, 0.75f]);
        Assert(g.Colour(0.1f) == 0xFFFF0000 && g.Colour(-5f) == 0xFFFF0000, "before the first stop: its colour");
        Assert(g.Colour(0.9f) == 0xFF0000FF && g.Colour(7f) == 0xFF0000FF, "after the last stop: its colour");
        uint mid = g.Colour(0.5f);
        Assert(Math.Abs(Ch(mid, 16) - 128) <= 1 && Math.Abs(Ch(mid, 0) - 128) <= 1, $"halfway: {mid:X8}");

        // A position below its predecessor is pinned to it: a hard stop at 0.5.
        var h = GradientShader.Linear(Vector2.Zero, new Vector2(1, 0), [0xFF000000, 0xFFFFFFFF, 0xFF00FF00], [0.5f, 0.2f, 1f]);
        Assert(h.Colour(0.49f) == 0xFF000000, $"left of the hard stop: {h.Colour(0.49f):X8}");
        Assert(Ch(h.Colour(0.5f), 16) == 255, $"at the hard stop: {h.Colour(0.5f):X8}");
    }

    public static void GradientInterpolatesUnpremultiplied()
    {
        // Transparent black to opaque white: Skia interpolates straight colours,
        // then premultiplies — grey 0.5 at alpha 0.5 is 0.25, not 0.5.
        var g = GradientShader.Linear(Vector2.Zero, new Vector2(1, 0), [0x00000000, 0xFFFFFFFF]);
        uint c = g.Colour(0.5f);
        Assert(Ch(c, 24) == 128 && Ch(c, 16) == 64 && Ch(c, 8) == 64 && Ch(c, 0) == 64, $"got {c:X8}, want 80404040");
    }

    public static void GradientEvenlySpacedStops()
    {
        var g = GradientShader.Linear(Vector2.Zero, new Vector2(1, 0), [0xFFFF0000, 0xFF00FF00, 0xFF0000FF]);
        Assert(g.Colour(0.5f) == 0xFF00FF00, "the middle colour sits at 0.5");
        Assert(g.Boundaries.Length == 4, $"two pieces plus the two constants, got {g.Boundaries.Length - 1} boundaries");
    }

    public static void DegenerateGradientIsTheLastColour()
    {
        var g = GradientShader.Linear(new Vector2(5, 5), new Vector2(5, 5), [0xFF112233, 0xFF445566]);
        g.Begin(Matrix3x2.Identity);
        var row = new uint[4];
        g.ShadeSpan(0, 0, row);
        Assert(row.All(p => p == 0xFF445566), "clamped degenerate gradient: last colour");
        var r = GradientShader.Radial(Vector2.Zero, 0f, [0xFF112233, 0x80445566]);
        Assert(r.Colour(0) == PixelMath.Premul(0x80445566), "zero radius: last colour");
    }

    public static void RadialGradientRunsOutwardAndClamps()
    {
        var g = GradientShader.Radial(new Vector2(50, 50), 40, [0xFFFFFFFF, 0xFF000000]);
        g.Begin(Matrix3x2.Identity);
        var row = new uint[100];
        g.ShadeSpan(0, 49, row);
        Assert(Ch(row[49], 16) > 245 && Ch(row[50], 16) > 245, "white at the centre");
        Assert(row[5] == 0xFF000000 && row[95] == 0xFF000000, "black past the radius");
        Assert(Ch(row[30], 16) > Ch(row[20], 16), "falls off outward");
    }

    public static void GradientFollowsTheTransform()
    {
        // The same gradient drawn under a translation lands translated.
        var g = GradientShader.Linear(Vector2.Zero, new Vector2(100, 0), [0xFF000000, 0xFFFFFFFF]);
        var a = new Surface(120, 2);
        var ca = new Canvas(a);
        ca.DrawRect(new Rect(0, 0, 100, 2), new Paint { Color = 0xFF000000, Shader = g });
        var b = new Surface(120, 2);
        var cb = new Canvas(b);
        cb.Translate(20, 0);
        cb.DrawRect(new Rect(0, 0, 100, 2), new Paint { Color = 0xFF000000, Shader = g });
        for (int x = 0; x < 100; x++) Assert(a.GetPixel(x, 1) == b.GetPixel(x + 20, 1), $"x={x}");
    }

    public static void VectorShaderBlendEqualsThePixelFormula()
    {
        // ShaderBlend is a vectorisation: it must give the bytes the scalar sink gave.
        var rnd = new Random(18);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = rnd.Next(1, 70);
            var src = new uint[n];
            var dst = new uint[n];
            var mask = new byte[n];
            for (int i = 0; i < n; i++)
            {
                uint a = (uint)rnd.Next(256);
                src[i] = PixelMath.Premul((a << 24) | (uint)rnd.Next(0x1000000));
                dst[i] = PixelMath.Premul(0xFF000000 | (uint)rnd.Next(0x1000000)) & (rnd.Next(4) == 0 ? 0x80FFFFFFu : 0xFFFFFFFFu);
                mask[i] = (byte)(rnd.Next(3) == 0 ? 255 : rnd.Next(3) == 0 ? 0 : rnd.Next(256));
            }
            uint cover = (uint)rnd.Next(1, 256);
            bool useMask = trial % 2 == 0;
            var want = (uint[])dst.Clone();
            for (int i = 0; i < n; i++)
            {
                uint c = cover;
                if (useMask)
                {
                    uint m = mask[i];
                    c = m == 255 ? c : PixelMath.MulDiv255Round(c, m);
                    if (c == 0) continue;
                }
                uint p = PixelMath.ScaleByCoverage(src[i], c);
                want[i] = (p >> 24) == 255 ? p : PixelMath.SrcOverColor(p, want[i]);
            }
            unsafe
            {
                fixed (uint* d = dst)
                fixed (byte* m = mask)
                    ShaderBlend.Blend(d, src, cover, useMask ? m : null);
            }
            Assert(dst.AsSpan().SequenceEqual(want), $"trial {trial}: vector and scalar blends differ");
        }
    }

    // ── Mask blur ────────────────────────────────────────────────────────

    public static void BlurKernelSizesPerSigma()
    {
        // σ < 2: the Bessel-function Gaussian, cut where a weight drops to 0.01.
        // The transitions are where SkiaSharp 2.88.8's impulse response widens.
        (double Sigma, int Radius)[] small = [(0.34, 1), (0.57, 1), (0.58, 2), (1.05, 2), (1.06, 3), (1.53, 3), (1.54, 4), (1.99, 4)];
        foreach (var (s, r) in small) Assert(GaussianMaskBlur.GaussRadius(s) == r, $"σ={s}: radius {GaussianMaskBlur.GaussRadius(s)}, want {r}");
        Assert(GaussianMaskBlur.HasNoBlur(0.33f) && !GaussianMaskBlur.HasNoBlur(0.34f), "no blur while 3σ ≤ 1");
        // σ ≥ 2: three boxes of w = ⌊σ·3√(2π)/4 + ½⌋.
        (double Sigma, int Window, int Border)[] box = [(2, 4, 5), (2.4, 5, 6), (3, 6, 8), (8, 15, 21), (15, 28, 41)];
        foreach (var (s, w, b) in box)
        {
            Assert(GaussianMaskBlur.BoxWindow(s) == w, $"σ={s}: window {GaussianMaskBlur.BoxWindow(s)}, want {w}");
            Assert(GaussianMaskBlur.BoxBorder(w) == b, $"w={w}: border {GaussianMaskBlur.BoxBorder(w)}, want {b}");
        }
        Assert(GaussianMaskBlur.BoxDivisor(5) == 125 && GaussianMaskBlur.BoxDivisor(4) == 80, "w³ for odd w, w²(w + 1) for even");
        var mb = new GaussianMaskBlur();
        Assert(mb.Margin(0.3f) == 0 && mb.Margin(1.5f) == 3 && mb.Margin(15f) == 41, "margin = the kernel's reach");
        foreach (var (s, _) in small)
        {
            var g = GaussianMaskBlur.GaussWeights(s);
            double sum = g[0] + 2 * g.Skip(1).Sum();
            Assert(Math.Abs(sum - 1) < 1e-12, $"σ={s}: weights sum to {sum}");
        }
    }

    public static void BlurredMaskConservesEnergy()
    {
        var mb = new GaussianMaskBlur();
        foreach (float sigma in new[] { 0.5f, 1f, 1.5f, 1.99f, 2f, 3f, 5f, 8f, 15f })
        {
            int m = mb.Margin(sigma);
            int w = 20 + 2 * m + 4, h = 12 + 2 * m + 4;
            var data = new byte[w * h];
            long before = 0;
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 20; x++) { data[(y + m + 2) * w + x + m + 2] = 255; before += 255; }
            mb.BlurA8(data, w, h, sigma);
            long after = data.Sum(b => (long)b);
            // Each pass rounds every pixel to 8 bits: at most ½ per pixel per pass.
            Assert(Math.Abs(after - before) <= w * h, $"σ={sigma}: {before} → {after}");
            Assert(after > 0.97 * before, $"σ={sigma}: lost {before - after} of {before}");
            // Symmetric about the block's centre.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Assert(data[y * w + x] == data[(h - 1 - y) * w + (w - 1 - x)], $"σ={sigma}: asymmetric at ({x},{y})");
        }
    }

    public static void BoxScaleIsExactInThirtyTwoBits()
    {
        // The vector path's (c·w + add) >> 32 in 16-bit halves equals the 64-bit product.
        var rnd = new Random(7);
        for (int window = 2; window < 140; window++)
        {
            long d = GaussianMaskBlur.BoxDivisor(window);
            ulong w = GaussianMaskBlur.BoxWeight(window);
            foreach (ulong add in new[] { 1UL << 31, (ulong)((d + 1) / 2) * w })
            {
                uint wh = (uint)(w >> 16), wl = (uint)(w & 0xFFFF), ah = (uint)(add >> 16), al = (uint)(add & 0xFFFF);
                for (int k = 0; k < 400; k++)
                {
                    uint c = k < 2 ? (uint)(k * 255 * d) : (uint)rnd.NextInt64(0, 255 * d + 1);
                    uint want = (uint)((c * w + add) >> 32);
                    Assert(BoxBlur.Scale(c, wh, wl, ah, al) == want, $"w={window} c={c}");
                }
            }
        }
    }

    // ── Shadows and the cache ────────────────────────────────────────────

    static (Surface, Canvas) Card(GaussianMaskBlur blur, float dx = 0)
    {
        var s = new Surface(200, 160);
        var c = new Canvas(s) { Antialiasing = Antialiasing.BaselineCompatible, MaskBlur = blur };
        c.Clear(0xFFFFFFFF);
        c.Translate(dx, 0);
        c.DrawRoundRect(new Rect(20, 20, 170, 140), 12, 12, new Paint { Color = 0x40000000, MaskBlurSigma = 6 });
        return (s, c);
    }

    static uint[] Pixels(Surface s)
    {
        var px = new uint[s.Width * s.Height];
        s.CopyTo(px);
        return px;
    }

    public static void ShadowCacheHitsAndChangesNothing()
    {
        var blur = new GaussianMaskBlur();
        var first = Pixels(Card(blur).Item1);
        Assert(blur.Cache.Misses == 1 && blur.Cache.Hits == 0 && blur.Cache.Count == 1, $"first draw blurs: {blur.Cache.Misses}/{blur.Cache.Hits}");
        var second = Pixels(Card(blur).Item1);
        Assert(blur.Cache.Hits == 1, "second draw of the same shadow hits");
        Assert(first.AsSpan().SequenceEqual(second), "a cached shadow draws the same bytes");
        // Moved by whole pixels: the same patch, moved.
        var moved = Pixels(Card(blur, 10).Item1);
        Assert(blur.Cache.Hits == 2, "a shadow moved by whole pixels reuses its patch");
        for (int y = 0; y < 160; y++)
            for (int x = 0; x < 180; x++)
                Assert(first[y * 200 + x] == moved[y * 200 + x + 10], $"moved shadow differs at ({x},{y})");
        // A fresh cache (cold) gives the same bytes again.
        var cold = Pixels(Card(new GaussianMaskBlur()).Item1);
        Assert(first.AsSpan().SequenceEqual(cold), "cold and warm shadows differ");
        blur.Cache.Clear();
        Assert(blur.Cache.Count == 0, "clear empties");
    }

    public static void ShadowCacheEvictsAtCapacity()
    {
        var blur = new GaussianMaskBlur { };
        var cache = blur.Cache;
        var s = new Surface(400, 300);
        var c = new Canvas(s) { Antialiasing = Antialiasing.BaselineCompatible, MaskBlur = blur };
        for (int i = 0; i < cache.Capacity + 5; i++)
            c.DrawRoundRect(new Rect(20, 20, 300, 200), 8 + i * 0.01f, 8 + i * 0.01f, new Paint { Color = 0x20000000, MaskBlurSigma = 3 });
        Assert(cache.Count <= cache.Capacity && cache.Count > 0, $"bounded: {cache.Count}");
    }

    public static void ShadowSpreadsAndFadesOutsideTheShape()
    {
        var (s, _) = Card(new GaussianMaskBlur());
        uint inside = s.GetPixel(100, 80), edge = s.GetPixel(20, 80), outside = s.GetPixel(8, 80), far = s.GetPixel(199, 2);
        Assert(Ch(inside, 16) < Ch(edge, 16) && Ch(edge, 16) < Ch(outside, 16) && far == 0xFFFFFFFF,
            $"inside {inside:X8} < edge {edge:X8} < outside {outside:X8} < far {far:X8}");
    }

    public static void TinySigmaDrawsTheShapeUnblurred()
    {
        var a = new Surface(80, 60);
        var ca = new Canvas(a) { Antialiasing = Antialiasing.BaselineCompatible, MaskBlur = new GaussianMaskBlur() };
        ca.DrawRoundRect(new Rect(10.5f, 10.25f, 70, 50), 6, 6, new Paint { Color = 0xFF336699, MaskBlurSigma = 0.3f });
        var b = new Surface(80, 60);
        var cb = new Canvas(b) { Antialiasing = Antialiasing.BaselineCompatible };
        cb.DrawRoundRect(new Rect(10.5f, 10.25f, 70, 50), 6, 6, Paint.Fill(0xFF336699));
        Assert(Pixels(a).AsSpan().SequenceEqual(Pixels(b)), "3σ ≤ 1 is no blur, as in Skia");
    }

    // ── Image and backdrop blur ─────────────────────────────────────────

    public static void ImageBlurSpreadsAPixelAndKeepsItsEnergy()
    {
        var src = new uint[] { 0xFFFFFFFF };
        foreach (float sigma in new[] { 1f, 2f, 5f })
        {
            int spread = ImageBlur.Spread(sigma);
            var dstRect = new IRect(-spread, -spread, spread + 1, spread + 1);
            var dst = new uint[dstRect.Width * dstRect.Height];
            ImageBlur.Blur(src, 1, new IRect(0, 0, 1, 1), dst, dstRect, sigma);
            long sum = dst.Sum(p => (long)(p >> 24));
            Assert(Math.Abs(sum - 255) <= dst.Length / 2 + 1, $"σ={sigma}: alpha sum {sum}");
            Assert(dst.All(p => Ch(p, 24) >= Ch(p, 16)), $"σ={sigma}: premultiplied stays premultiplied");
            int c = dst.Length / 2;
            Assert(dst.Max(p => p >> 24) == dst[c] >> 24, $"σ={sigma}: peak at the centre");
        }
        var copy = new uint[1];
        ImageBlur.Blur(src, 1, new IRect(0, 0, 1, 1), copy, new IRect(0, 0, 1, 1), 0.2f);
        Assert(copy[0] == 0xFFFFFFFF, "a window of 1 copies");
    }

    public static void BackdropBlurStaysInsideItsClip()
    {
        var s = new Surface(120, 80);
        var c = new Canvas(s);
        c.Clear(0xFF000000);
        for (int x = 0; x < 120; x += 8) c.DrawRect(new Rect(x, 0, x + 4, 80), Paint.Fill(0xFFFFFFFF));
        var before = Pixels(s);
        c.DrawBackdropBlur(new IRect(10, 10, 110, 70), new Rect(30, 20, 90, 60), 4f);
        var after = Pixels(s);
        int changedInside = 0;
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 120; x++)
            {
                bool inside = x >= 30 && x < 90 && y >= 20 && y < 60;
                if (!inside) Assert(before[y * 120 + x] == after[y * 120 + x], $"changed outside the clip at ({x},{y})");
                else if (before[y * 120 + x] != after[y * 120 + x]) changedInside++;
            }
        Assert(changedInside > 60 * 40 / 2, $"blurred inside: {changedInside} px changed");
        // Stripes average to grey.
        uint mid = after[40 * 120 + 60];
        Assert(Ch(mid, 16) > 90 && Ch(mid, 16) < 170, $"stripes blur towards grey: {mid:X8}");
    }

    public static void EffectsAreDeterministic()
    {
        byte[] Frame()
        {
            var s = new Surface(160, 120);
            var c = new Canvas(s) { Antialiasing = Antialiasing.BaselineCompatible, MaskBlur = new GaussianMaskBlur() };
            c.DrawRect(new Rect(0, 0, 160, 120), new Paint { Color = 0xFF000000, Shader = GradientShader.Linear(Vector2.Zero, new Vector2(0, 120), [0xFF101830, 0xFF402060], [0, 1]) });
            c.DrawRoundRect(new Rect(20.3f, 20.6f, 140, 90), 10, 10, new Paint { Color = 0x60000000, MaskBlurSigma = 5 });
            c.DrawRoundRect(new Rect(20.3f, 18.6f, 140, 88), 10, 10, new Paint { Color = 0xFF000000, Shader = GradientShader.Radial(new Vector2(60, 40), 90, [0xFFF59E0B, 0x007C2D12]) });
            c.DrawBackdropBlur(new IRect(30, 30, 130, 100), new Rect(40, 40, 120, 90), 3f);
            var px = Pixels(s);
            return System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()));
        }
        Assert(Frame().AsSpan().SequenceEqual(Frame()), "same calls, same bytes");
    }
}
