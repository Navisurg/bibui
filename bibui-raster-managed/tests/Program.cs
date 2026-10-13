// Coverage-correctness tests for Bibui.Raster.Managed. The reference for every
// coverage test is the geometry itself, point-sampled on a 64×64 grid per pixel,
// so a pass means "the right area", not "agrees with another rasterizer".
//
//   dotnet run -c Release --project bibui-raster-managed/tests
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Bibui.Raster.Managed;
using Path = Bibui.Raster.Managed.Path;

int passed = 0, failed = 0;
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static).Concat(typeof(StrokeTests).GetMethods(BindingFlags.Public | BindingFlags.Static)).Concat(typeof(EffectTests).GetMethods(BindingFlags.Public | BindingFlags.Static))) // + StrokeTests.cs; + EffectTests.cs
{
    try
    {
        m.Invoke(null, null);
        passed++;
        Console.WriteLine($"  ok   {m.Name}");
    }
    catch (TargetInvocationException e)
    {
        failed++;
        Console.WriteLine($"  FAIL {m.Name}: {e.InnerException?.Message}");
    }
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static class Tests
{
    const uint White = 0xFFFFFFFF, Black = 0xFF000000;

    // ── Helpers ──────────────────────────────────────────────────────────

    static (Surface s, Canvas c) Black8(int w = 64, int h = 48)
    {
        var s = new Surface(w, h);
        var c = new Canvas(s);
        c.Clear(Black);
        return (s, c);
    }

    /// <summary>Red channel of a white-on-black draw is the coverage.</summary>
    static int Cov(Surface s, int x, int y) => (int)((s.GetPixel(x, y) >> 16) & 0xFF);

    static double Reference(int x, int y, Func<float, float, bool> inside, int n = 64)
    {
        int hit = 0;
        for (int sy = 0; sy < n; sy++)
        for (int sx = 0; sx < n; sx++)
            if (inside(x + (sx + 0.5f) / n, y + (sy + 0.5f) / n)) hit++;
        return 255.0 * hit / (n * n);
    }

    /// <summary>Every pixel within <paramref name="tol"/> of the supersampled reference.</summary>
    static void AssertCoverage(Surface s, Func<float, float, bool> inside, double tol, string what)
    {
        double worst = 0;
        (int x, int y, int got, double want) at = default;
        for (int y = 0; y < s.Height; y++)
        for (int x = 0; x < s.Width; x++)
        {
            double r = Reference(x, y, inside);
            int got = Cov(s, x, y);
            double e = Math.Abs(got - r);
            if (e > worst) { worst = e; at = (x, y, got, r); }
        }
        if (worst > tol)
            throw new Exception($"{what}: pixel ({at.x},{at.y}) is {at.got}, reference {at.want:F1} (|err| {worst:F1} > {tol})");
    }

    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static float[] Star(float cx, float cy, float r)
    {
        var xy = new float[10];
        for (int k = 0; k < 5; k++)
        {
            int v = (k * 2) % 5;
            double a = -Math.PI / 2 + v * 2 * Math.PI / 5;
            xy[2 * k] = cx + r * (float)Math.Cos(a);
            xy[2 * k + 1] = cy + r * (float)Math.Sin(a);
        }
        return xy;
    }

    static int Winding(float[] p, float x, float y)
    {
        int w = 0, n = p.Length / 2;
        for (int i = 0; i < n; i++)
        {
            float x0 = p[2 * i], y0 = p[2 * i + 1], x1 = p[2 * ((i + 1) % n)], y1 = p[2 * ((i + 1) % n) + 1];
            if (y0 <= y ? y1 > y : y1 <= y)
            {
                float cross = (x1 - x0) * (y - y0) - (x - x0) * (y1 - y0);
                if (y1 > y0 ? cross > 0 : cross < 0) w += y1 > y0 ? 1 : -1;
            }
        }
        return w;
    }

    static Path Poly(float[] xy, FillRule rule)
    {
        var p = new Path { FillRule = rule };
        p.MoveTo(xy[0], xy[1]);
        for (int k = 1; k < xy.Length / 2; k++) p.LineTo(xy[2 * k], xy[2 * k + 1]);
        p.Close();
        return p;
    }

    static bool InRect(float x, float y, float x0, float y0, float x1, float y1) => x >= x0 && x < x1 && y >= y0 && y < y1;

    static bool InRRect(float x, float y, float x0, float y0, float x1, float y1, float r)
    {
        if (x < x0 || x > x1 || y < y0 || y > y1) return false;
        float cx = Math.Clamp(x, x0 + r, x1 - r), cy = Math.Clamp(y, y0 + r, y1 - r);
        float dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    // ── Overlap ─────────────────────────────────────────────────────────

    public static void PentagramNonzeroIsExactWhereItOverlapsItself()
    {
        var (s, c) = Black8();
        var star = Star(20.3f, 12.6f, 11f);
        c.DrawPath(Poly(star, FillRule.NonZero), Paint.Fill(White));
        AssertCoverage(s, (x, y) => Winding(star, x, y) != 0, 2.0, "nonzero pentagram");
        // The pixel summed-and-clamped coverage put at 255 against a reference of ~210.
        Assert(Math.Abs(Cov(s, 17, 9) - Reference(17, 9, (x, y) => Winding(star, x, y) != 0)) <= 2, "overlap pixel (17,9)");
    }

    public static void PentagramEvenOddLeavesTheCentreOpen()
    {
        var (s, c) = Black8();
        var star = Star(20.3f, 12.6f, 11f);
        c.DrawPath(Poly(star, FillRule.EvenOdd), Paint.Fill(White));
        AssertCoverage(s, (x, y) => (Winding(star, x, y) & 1) != 0, 2.0, "even-odd pentagram");
        Assert(Cov(s, 20, 13) == 0, "centre of an even-odd pentagram is outside");
    }

    public static void SameDirectionOverlapCountsOnce()
    {
        // Winding 2 where the rects overlap: |sum| would give 2·area at their
        // shared fractional edges, clamped to 1 only where fully covered.
        var (s, c) = Black8();
        var p = new Path();
        p.AddRect(new Rect(4.3f, 4.6f, 30.7f, 20.2f));
        p.AddRect(new Rect(16.5f, 10.1f, 44.2f, 31.7f));
        p.AddRect(new Rect(20.25f, 12.5f, 22.75f, 30.5f)); // a third, inside the second's edge region
        c.DrawPath(p, Paint.Fill(White));
        AssertCoverage(s, (x, y) => InRect(x, y, 4.3f, 4.6f, 30.7f, 20.2f) || InRect(x, y, 16.5f, 10.1f, 44.2f, 31.7f) || InRect(x, y, 20.25f, 12.5f, 22.75f, 30.5f), 2.5, "union of same-direction rects");
    }

    public static void OppositeDirectionsCancel()
    {
        var (s, c) = Black8();
        var p = new Path();
        p.AddRect(new Rect(4.3f, 4.6f, 30.7f, 20.2f));
        p.AddRect(new Rect(10.2f, 8.4f, 22.9f, 27.3f), clockwise: false);
        c.DrawPath(p, Paint.Fill(White));
        // Winding +1, −1 and 0: nonzero fills both non-shared parts, nothing where they cancel.
        AssertCoverage(s, (x, y) =>
            (InRect(x, y, 4.3f, 4.6f, 30.7f, 20.2f) ? 1 : 0) - (InRect(x, y, 10.2f, 8.4f, 22.9f, 27.3f) ? 1 : 0) != 0, 2.5, "cancelling rects");
    }

    public static void TwiceTheSameRectIsOnce()
    {
        var (s, c) = Black8();
        var p = new Path();
        p.AddRect(new Rect(3.5f, 3.25f, 20.75f, 10.5f));
        p.AddRect(new Rect(3.5f, 3.25f, 20.75f, 10.5f));
        c.DrawPath(p, Paint.Fill(White));
        Assert(Cov(s, 3, 5) == 128, $"half-covered edge pixel is 128, got {Cov(s, 3, 5)}");
        p.FillRule = FillRule.EvenOdd;
        var (s2, c2) = Black8();
        c2.DrawPath(p, Paint.Fill(White));
        for (int y = 0; y < s2.Height; y++)
        for (int x = 0; x < s2.Width; x++)
            Assert(Cov(s2, x, y) == 0, "even-odd: a rect twice is winding 2, outside");
    }

    public static void BowTieCrossingIsExact()
    {
        // Two triangles meeting at a crossing inside a pixel: opposite windings.
        var (s, c) = Black8();
        float[] bow = [3.3f, 4.2f, 40.7f, 30.6f, 40.7f, 4.2f, 3.3f, 30.6f];
        c.DrawPath(Poly(bow, FillRule.NonZero), Paint.Fill(White));
        AssertCoverage(s, (x, y) => Winding(bow, x, y) != 0, 2.0, "bow tie");
    }

    public static void CurvesFillToTheirArea()
    {
        var (s, c) = Black8();
        var p = new Path();
        p.AddCircle(24.3f, 20.6f, 14.2f);
        c.DrawPath(p, Paint.Fill(White));
        AssertCoverage(s, (x, y) => (x - 24.3f) * (x - 24.3f) + (y - 20.6f) * (y - 20.6f) <= 14.2f * 14.2f, 5.5, "circle"); // flattened to 0.02 px, chords inside the arc
    }

    // ── Rects and rrects ─────────────────────────────────────────────────

    public static void RectCoverageIsExactPerEdge()
    {
        var (s, c) = Black8();
        c.DrawRect(new Rect(2.25f, 3.5f, 10.75f, 7.0f), Paint.Fill(White));
        Assert(Cov(s, 2, 3) == 96, $"corner 0.75·0.5 → 96, got {Cov(s, 2, 3)}");
        Assert(Cov(s, 5, 3) == 128, $"top edge 0.5 → 128, got {Cov(s, 5, 3)}");
        Assert(Cov(s, 5, 5) == 255, "interior");
        Assert(Cov(s, 10, 5) == 191, $"right edge 0.75 → 191, got {Cov(s, 10, 5)}");
        Assert(Cov(s, 11, 5) == 0 && Cov(s, 5, 7) == 0, "outside");
    }

    public static void RRectFillMatchesReference()
    {
        var (s, c) = Black8();
        c.DrawRoundRect(new Rect(3.3f, 2.7f, 43.3f, 26.7f), 6, 6, Paint.Fill(White));
        AssertCoverage(s, (x, y) => InRRect(x, y, 3.3f, 2.7f, 43.3f, 26.7f, 6), 3.0, "rrect");
    }

    public static void RRectStrokeIsTheRing()
    {
        var (s, c) = Black8();
        c.DrawRoundRect(new Rect(4.4f, 4.4f, 44.4f, 28.4f), 6, 6, Paint.Stroke(White, 2.5f));
        AssertCoverage(s, (x, y) => InRRect(x, y, 3.15f, 3.15f, 45.65f, 29.65f, 7.25f) && !InRRect(x, y, 5.65f, 5.65f, 43.15f, 27.15f, 4.75f), 3.0, "rrect ring");
    }

    public static void RectFrameIsExact()
    {
        var (s, c) = Black8();
        c.DrawRect(new Rect(4.3f, 5.6f, 30.2f, 20.9f), Paint.Stroke(White, 1.5f));
        AssertCoverage(s, (x, y) => InRect(x, y, 3.55f, 4.85f, 30.95f, 21.65f) && !InRect(x, y, 5.05f, 6.35f, 29.45f, 20.15f), 2.5, "rect frame");
    }

    // ── Clip ─────────────────────────────────────────────────────────────

    public static void AliasedClipRectSnapsToPixels()
    {
        var (s, c) = Black8();
        c.Save();
        c.ClipRect(new Rect(4.4f, 4.6f, 20.5f, 10.49f));
        Assert(c.DeviceClipBounds == new IRect(4, 5, 21, 10), $"clip bounds {c.DeviceClipBounds}");
        c.DrawRect(new Rect(0, 0, 64, 48), Paint.Fill(White));
        c.Restore();
        Assert(Cov(s, 4, 5) == 255 && Cov(s, 20, 9) == 255, "inside the snapped clip");
        Assert(Cov(s, 3, 5) == 0 && Cov(s, 4, 4) == 0 && Cov(s, 21, 9) == 0 && Cov(s, 20, 10) == 0, "outside the snapped clip");
        Assert(c.DeviceClipBounds == s.Bounds, "restore pops the clip");
    }

    public static void RRectClipIsCoverage()
    {
        var (s, c) = Black8();
        c.Save();
        c.ClipRRect(new RRect(new Rect(3.3f, 2.7f, 43.3f, 26.7f), 6));
        c.DrawRect(new Rect(0, 0, 64, 48), Paint.Fill(White));
        c.Restore();
        AssertCoverage(s, (x, y) => InRRect(x, y, 3.3f, 2.7f, 43.3f, 26.7f, 6), 3.0, "rrect clip");
    }

    public static void ClipsIntersectAndRestore()
    {
        var (s, c) = Black8();
        c.Save();
        c.ClipRect(new Rect(0, 0, 20, 48));
        c.Save();
        var p = new Path();
        p.AddCircle(20, 20, 10);
        c.ClipPath(p);
        c.DrawRect(new Rect(0, 0, 64, 48), Paint.Fill(White));
        c.Restore();
        // Back to the rect clip only.
        c.DrawRect(new Rect(0, 40, 64, 48), Paint.Fill(White));
        c.Restore();
        AssertCoverage(s, (x, y) => x < 20 && ((x - 20) * (x - 20) + (y - 20) * (y - 20) <= 100 || y >= 40), 3.0, "rect ∩ circle, then rect");
    }

    public static void EvenOddClipPath()
    {
        var (s, c) = Black8();
        var p = new Path { FillRule = FillRule.EvenOdd };
        p.AddRect(new Rect(4, 4, 40, 40));
        p.AddRect(new Rect(12, 12, 32, 32));
        c.ClipPath(p);
        c.DrawRect(new Rect(0, 0, 64, 48), Paint.Fill(White));
        Assert(Cov(s, 6, 6) == 255 && Cov(s, 20, 20) == 0 && Cov(s, 50, 20) == 0, "even-odd clip has a hole");
    }

    // ── Layers ───────────────────────────────────────────────────────────

    public static void LayerOpacityComposites()
    {
        var (s, c) = Black8();
        c.SaveLayer((byte)128);
        c.DrawRect(new Rect(0, 0, 32, 48), Paint.Fill(White));
        c.DrawRect(new Rect(16, 0, 48, 48), Paint.Fill(White)); // overlap stays one coat inside the layer
        c.Restore();
        int a = Cov(s, 8, 8), b = Cov(s, 20, 8), d = Cov(s, 56, 8);
        Assert(Math.Abs(a - 128) <= 1 && a == b, $"one coat at half opacity: {a}, {b}");
        Assert(d == 0, "outside the layer's drawing");
    }

    public static void NestedLayersMultiply()
    {
        var (s, c) = Black8();
        c.SaveLayer((byte)128);
        c.SaveLayer((byte)128);
        c.DrawRect(new Rect(0, 0, 64, 48), Paint.Fill(White));
        c.Restore();
        c.Restore();
        int v = Cov(s, 10, 10);
        Assert(Math.Abs(v - 64) <= 2, $"½·½ of white is ~64, got {v}");
        Assert(c.SaveCount == 1, "balanced");
    }

    public static void LayerRespectsClipAndTransform()
    {
        var (s, c) = Black8();
        c.Save();
        c.ClipRect(new Rect(10, 10, 30, 30));
        c.SaveLayer(1f);
        c.Translate(5, 5);
        c.DrawRect(new Rect(0, 0, 64, 48), Paint.Fill(White));
        c.Restore();
        c.Restore();
        Assert(Cov(s, 10, 10) == 255 && Cov(s, 29, 29) == 255, "inside clip");
        Assert(Cov(s, 9, 15) == 0 && Cov(s, 30, 15) == 0, "outside clip");
    }

    public static void ReusedLayerDoesNotLeakItsLastFrame()
    {
        // Layers are pooled and only the drawn-into region is cleared: a second
        // layer must not composite what the first one left elsewhere.
        var (s, c) = Black8();
        c.SaveLayer((byte)255);
        c.DrawRect(new Rect(2, 2, 20, 20), Paint.Fill(White));
        c.Restore();
        c.Clear(Black);
        c.SaveLayer((byte)255);
        c.DrawRect(new Rect(40, 2, 60, 20), Paint.Fill(White));
        c.DrawLine(30, 30, 50, 40, Paint.Stroke(White, 1));
        c.Restore();
        Assert(Cov(s, 10, 10) == 0, $"first layer's rect leaked: {Cov(s, 10, 10)}");
        Assert(Cov(s, 50, 10) == 255, "second layer's rect");
        Assert(Cov(s, 40, 35) > 0, "second layer's hairline");
    }

    // ── Hairlines ───────────────────────────────────────────────────────

    public static void HairlineOnAPixelCentreIsOnePixel()
    {
        var (s, c) = Black8();
        c.DrawLine(4, 10.5f, 40, 10.5f, Paint.Stroke(White, 1));
        Assert(Cov(s, 20, 10) == 255 && Cov(s, 20, 9) == 0 && Cov(s, 20, 11) == 0, $"one row: {Cov(s, 20, 9)} {Cov(s, 20, 10)} {Cov(s, 20, 11)}");
    }

    public static void HairlineBetweenRowsSplits()
    {
        var (s, c) = Black8();
        c.DrawLine(4, 10.25f, 40, 10.25f, Paint.Stroke(White, 0));
        int up = Cov(s, 20, 9), down = Cov(s, 20, 10);
        Assert(Math.Abs(up - 64) <= 2 && Math.Abs(down - 191) <= 2, $"¼ / ¾ split, got {up} / {down}");
    }

    public static void ThinStrokeIsAHairlineWithAlphaScaled()
    {
        Assert(Canvas.TreatAsHairline(Paint.Stroke(White, 1), Matrix3x2.Identity, out float c1) && c1 == 1, "1 px at 1x");
        Assert(Canvas.TreatAsHairline(Paint.Stroke(White, 0.5f), Matrix3x2.Identity, out float c2) && c2 == 0.5f, "½ px");
        Assert(!Canvas.TreatAsHairline(Paint.Stroke(White, 1), Matrix3x2.CreateScale(1.25f), out _), "1.25 px is geometric");
        Assert(!Canvas.TreatAsHairline(Paint.Fill(White), Matrix3x2.Identity, out _), "a fill is not");
    }

    public static void RRectHairlineFollowsTheOutline()
    {
        var (s, c) = Black8();
        c.DrawRoundRect(new Rect(4.5f, 4.5f, 40.5f, 28.5f), 6, 6, Paint.Stroke(White, 1));
        // Straight sides on pixel centres: exactly one pixel wide.
        Assert(Cov(s, 20, 4) == 255 && Cov(s, 20, 3) == 0 && Cov(s, 20, 5) == 0, "top side");
        Assert(Cov(s, 4, 16) == 255 && Cov(s, 3, 16) == 0 && Cov(s, 5, 16) == 0, "left side");
        Assert(Cov(s, 20, 16) == 0, "inside untouched");
    }

    // ── Pixels, transforms, shaders, determinism ─────────────────────────

    public static void RgbaOrderSwapsRedAndBlue()
    {
        var s = new Surface(4, 4, PixelOrder.Rgba8888);
        var c = new Canvas(s);
        c.Clear(0xFFFF0000);
        uint px = s.GetPixel(1, 1);
        Assert((px & 0xFF) == 0xFF && ((px >> 16) & 0xFF) == 0, $"RGBA red in memory is FF 00 00 FF, got {px:X8}");
        Assert(s.GetPixelArgb(1, 1) == 0xFFFF0000, "as ARGB");
    }

    public static unsafe void WrapsCallerMemoryWithStride()
    {
        var mem = new uint[10 * 6];
        fixed (uint* p = mem)
        {
            var s = Surface.Wrap((nint)p, 8, 6, 40);
            var c = new Canvas(s);
            c.Clear(0xFF102030);
            c.DrawRect(new Rect(0, 0, 8, 6), Paint.Fill(0xFFFFFFFF));
        }
        Assert(mem[0] == 0xFFFFFFFF && mem[7] == 0xFFFFFFFF && mem[8] == 0 && mem[9] == 0 && mem[10] == 0xFFFFFFFF, "stride padding untouched");
    }

    public static void TransformedPathMatchesReference()
    {
        var (s, c) = Black8();
        c.Translate(24, 22);
        c.Concat(Matrix3x2.CreateRotation(MathF.PI / 6));
        c.DrawRect(new Rect(-12, -8, 12, 8), Paint.Fill(White));
        var inv = Matrix3x2.CreateRotation(-MathF.PI / 6);
        AssertCoverage(s, (x, y) =>
        {
            var q = Vector2.Transform(new Vector2(x - 24, y - 22), inv);
            return q.X >= -12 && q.X < 12 && q.Y >= -8 && q.Y < 8;
        }, 2.0, "rotated rect");
    }

    public static void ShaderPathEqualsSolidPaint()
    {
        var (s1, c1) = Black8();
        var (s2, c2) = Black8();
        var star = Star(20.3f, 12.6f, 11f);
        c1.DrawPath(Poly(star, FillRule.NonZero), Paint.Fill(0xC0E4E4E7));
        c2.DrawPath(Poly(star, FillRule.NonZero), new Paint { Color = 0xC0FFFFFF, Shader = new ColorShader(0xFFE4E4E7) });
        for (int y = 0; y < s1.Height; y++)
        for (int x = 0; x < s1.Width; x++)
        {
            int d = 0;
            uint a = s1.GetPixel(x, y), b = s2.GetPixel(x, y);
            for (int sh = 0; sh < 32; sh += 8) d = Math.Max(d, Math.Abs((int)((a >> sh) & 0xFF) - (int)((b >> sh) & 0xFF)));
            Assert(d <= 1, $"({x},{y}): {a:X8} vs {b:X8}");
        }
    }

    public static void OutputIsDeterministic()
    {
        string Frame()
        {
            var s = new Surface(200, 120);
            var c = new Canvas(s);
            c.Clear(0xFF09090B);
            for (int i = 0; i < 40; i++)
            {
                float x = 3.3f + (i % 8) * 24.4f, y = 2.7f + (i / 8) * 23.1f;
                c.Save();
                if (i % 3 == 0) c.ClipRRect(new RRect(new Rect(x, y, x + 20, y + 18), 5));
                if (i % 4 == 0) c.SaveLayer((byte)(100 + i));
                c.DrawRoundRect(new Rect(x, y, x + 20, y + 18), 4, 4, Paint.Fill(0xFF27272A + (uint)i));
                c.DrawRoundRect(new Rect(x, y, x + 20, y + 18), 4, 4, Paint.Stroke(0x17FFFFFF, 1));
                c.DrawPath(Poly(Star(x + 10, y + 9, 8), i % 2 == 0 ? FillRule.NonZero : FillRule.EvenOdd), Paint.Fill(0xCCE4E4E7));
                c.RestoreToCount(1);
            }
            var px = new uint[s.Width * s.Height];
            s.CopyTo(px);
            return Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan())));
        }
        string a = Frame(), b = Frame();
        Assert(a == b, "two runs, two hashes");
    }

    public static void WideStrokeWithoutStrokerSaysWhichItem()
    {
        var (_, c) = Black8();
        c.Stroker = null; // a canvas has Skia's stroker unless it is taken away
        var p = new Path();
        p.MoveTo(2, 2);
        p.LineTo(30, 20);
        try
        {
            c.DrawPath(p, Paint.Stroke(White, 3));
        }
        catch (NotSupportedException e)
        {
            Assert(e.Message.Contains("IPathStroker"), "names IPathStroker");
            return;
        }
        throw new Exception("expected NotSupportedException");
    }

    public static void StrokerPlugsIn()
    {
        var (s, c) = Black8();
        c.Stroker = new SquareStroker();
        var p = new Path();
        p.MoveTo(10, 20);
        p.LineTo(40, 20);
        c.DrawPath(p, Paint.Stroke(White, 4));
        Assert(Cov(s, 20, 18) == 255 && Cov(s, 20, 21) == 255 && Cov(s, 20, 17) == 0 && Cov(s, 20, 22) == 0, "a 4 px band");
    }

    /// <summary>A toy stroker for horizontal lines: what the real stroker replaces.</summary>
    sealed class SquareStroker : IPathStroker
    {
        public void Stroke(Path source, in Paint paint, float resScale, Path destination)
        {
            float h = paint.StrokeWidth / 2;
            destination.AddRect(new Rect(10, 20 - h, 40, 20 + h));
        }
    }

    // ── BaselineCompatible ───────────────────────────────────────────────────

    static (Surface, Canvas) Compat()
    {
        var s = new Surface(64, 48);
        var c = new Canvas(s) { Antialiasing = Antialiasing.BaselineCompatible };
        c.Clear(Black);
        return (s, c);
    }

    public static void CompatOverlapIsNotOverestimated()
    {
        var (s, c) = Compat();
        var star = Star(20.3f, 12.6f, 11f);
        c.DrawPath(Poly(star, FillRule.NonZero), Paint.Fill(White));
        double want = Reference(17, 9, (x, y) => Winding(star, x, y) != 0);
        // The overlap pixel: summed-and-clamped coverage gave 255 here.
        Assert(Math.Abs(Cov(s, 17, 9) - want) <= 24, $"(17,9) is {Cov(s, 17, 9)}, reference {want:F0}");
    }

    public static void CompatSupersampledShapeRunsToTheRightClip()
    {
        // The shape leaves the clip on the right, so its right edges are
        // dropped: the interval left open must still run to the clip.
        var (s, c) = Compat();
        c.ClipRect(new Rect(0, 0, 30, 48));
        c.DrawRoundRect(new Rect(5.3f, 5.3f, 50.3f, 30.3f), 6, 6, Paint.Fill(White));
        Assert(Cov(s, 29, 15) == 255 && Cov(s, 20, 15) == 255 && Cov(s, 30, 15) == 0, $"{Cov(s, 20, 15)} {Cov(s, 29, 15)} {Cov(s, 30, 15)}");
    }

    public static void CompatEdgesRoundToQuarters()
    {
        // Skia supersamples a 40×24 rrect: its side at x = 3.3 rounds to 3.25,
        // three quarters, 192; the top at y = 2.3 to 2.25, 191 (the last
        // sub-scanline counts 63, not 64). Measured from SkiaSharp 2.88.8.
        var (s, c) = Compat();
        c.DrawRoundRect(new Rect(3.3f, 2.3f, 43.3f, 26.3f), 6, 6, Paint.Fill(White));
        Assert(Cov(s, 3, 12) == 192 && Cov(s, 14, 2) == 191, $"{Cov(s, 3, 12)} {Cov(s, 14, 2)}");
    }
}
