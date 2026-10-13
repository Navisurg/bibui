using System.Numerics;

namespace Bibui.Raster.Managed;

/// <summary>
/// Path → device lines for the 4×4 supersampler: conics become quads
/// at ¼ px, quads and cubics are chopped at their y extrema, and each
/// monotonic piece becomes 2^shift chords stepped in fixed point, with the
/// number of chords from an estimate of the curve's bulge at 4× scale.
///
/// <para>These chords are far coarser than <see cref="Flatten.Fill"/>'s
/// 0.02 px: on a 2 px icon stroke they sit visibly inside the curve. They are
/// Skia 2.88's edge builder's (<c>SkEdgeBuilder</c>, <c>SkQuadraticEdge</c>,
/// <c>SkCubicEdge</c>), cut exactly where it cuts them, because the fixture
/// baselines are drawn by the Skia backend and a stroked curve's edge pixels
/// only agree with them when filled from the same chords. Only used in
/// <see cref="Antialiasing.BaselineCompatible"/> mode, for paths the supersampler
/// takes.</para>
/// </summary>
internal static class SupersampleEdges
{
    private const int SuperShift = 2;      // SkScan_AntiPath SHIFT
    private const int MaxCoeffShift = 6;   // SkEdge.cpp MAX_COEFF_SHIFT
    private const float ConicTolerance = 0.25f;
    private const float FixedToDevice = 1f / (65536 << SuperShift);

    public static void Fill(Path path, in Matrix3x2 m, Rasterizer r)
    {
        var verbs = path.Verbs;
        var pts = path.Points;
        var ws = path.Weights;
        int pi = 0, wi = 0;
        Vector2 start = default, cur = default;
        bool open = false;
        Span<Vector2> quads = stackalloc Vector2[1 + 2 * 32];
        Span<Vector2> mono = stackalloc Vector2[10];
        foreach (var v in verbs)
        {
            switch (v)
            {
                case PathVerb.Move:
                    if (open) r.AddLine(cur.X, cur.Y, start.X, start.Y);
                    start = cur = Vector2.Transform(pts[pi++], m);
                    open = true;
                    break;
                case PathVerb.Line:
                {
                    var p = Vector2.Transform(pts[pi++], m);
                    r.AddLine(cur.X, cur.Y, p.X, p.Y);
                    cur = p;
                    break;
                }
                case PathVerb.Quad:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    pi += 2;
                    Quad(r, cur, p1, p2, mono);
                    cur = p2;
                    break;
                }
                case PathVerb.Conic:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    pi += 2;
                    float w = ws[wi++];
                    int pow2 = Conic.QuadPow2(cur, p1, p2, w, ConicTolerance);
                    int nq = Conic.ToQuads(cur, p1, p2, w, pow2, quads);
                    for (int q = 0; q < nq; q++) Quad(r, quads[2 * q], quads[2 * q + 1], quads[2 * q + 2], mono);
                    cur = p2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    var p3 = Vector2.Transform(pts[pi + 2], m);
                    pi += 3;
                    Cubic(r, cur, p1, p2, p3, mono);
                    cur = p3;
                    break;
                }
                case PathVerb.Close:
                    if (open) r.AddLine(cur.X, cur.Y, start.X, start.Y);
                    cur = start;
                    open = false;
                    break;
            }
        }
        if (open) r.AddLine(cur.X, cur.Y, start.X, start.Y);
    }

    /// <summary><c>SkScalarRoundToFDot6(x, shift)</c>: round half to even at 2^(6+shift).</summary>
    private static int ToFDot6(float v) => (int)Math.Round((double)v * (1 << (6 + SuperShift)), MidpointRounding.ToEven);

    private static int CheapDistance(int dx, int dy)
    {
        dx = Math.Abs(dx);
        dy = Math.Abs(dy);
        return dx > dy ? dx + (dy >> 1) : dy + (dx >> 1);
    }

    private static int DiffToShift(int dx, int dy, int shiftAA)
    {
        int dist = CheapDistance(dx, dy);
        dist = (dist + (1 << 4)) >> (3 + shiftAA);
        return (32 - BitOperations.LeadingZeroCount((uint)dist)) >> 1;
    }

    // ── Quads ──────────────────────────────────────────────────────────────

    private static bool IsNotMonotonic(float a, float b, float c)
    {
        float ab = a - b;
        float bc = b - c;
        if (ab < 0) bc = -bc;
        return ab == 0 || bc < 0;
    }

    private static bool ValidUnitDivide(float numer, float denom, out float ratio)
    {
        ratio = 0;
        if (numer < 0) { numer = -numer; denom = -denom; }
        if (denom == 0 || numer == 0 || numer >= denom) return false;
        float r = numer / denom;
        if (float.IsNaN(r) || r == 0) return false;
        ratio = r;
        return true;
    }

    /// <summary><c>SkChopQuadAtYExtrema</c>, then each monotonic piece as a quadratic edge.</summary>
    private static void Quad(Rasterizer r, Vector2 p0, Vector2 p1, Vector2 p2, Span<Vector2> dst)
    {
        float a = p0.Y, b = p1.Y, c = p2.Y;
        if (IsNotMonotonic(a, b, c))
        {
            if (ValidUnitDivide(a - b, a - b - b + c, out float t))
            {
                CurveMath.ChopQuadAt([p0, p1, p2], dst, t);
                dst[1].Y = dst[3].Y = dst[2].Y; // flatten the double extremum
                QuadEdge(r, dst[0], dst[1], dst[2]);
                QuadEdge(r, dst[2], dst[3], dst[4]);
                return;
            }
            b = MathF.Abs(a - b) < MathF.Abs(b - c) ? a : c;
            p1.Y = b;
        }
        QuadEdge(r, p0, p1, p2);
    }

    /// <summary><c>SkQuadraticEdge::setQuadratic</c> and its forward differencing, as device lines.</summary>
    private static void QuadEdge(Rasterizer r, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        int x0 = ToFDot6(p0.X), y0 = ToFDot6(p0.Y);
        int x1 = ToFDot6(p1.X), y1 = ToFDot6(p1.Y);
        int x2 = ToFDot6(p2.X), y2 = ToFDot6(p2.Y);
        bool swapped = false;
        if (y0 > y2)
        {
            (x0, x2) = (x2, x0);
            (y0, y2) = (y2, y0);
            swapped = true;
        }
        if (((y0 + 32) >> 6) == ((y2 + 32) >> 6)) return; // no sample row crossed

        int shift;
        {
            int dx = ((x1 << 1) - x0 - x2) >> 2;
            int dy = ((y1 << 1) - y0 - y2) >> 2;
            shift = DiffToShift(dx, dy, SuperShift);
        }
        if (shift == 0) shift = 1;
        else if (shift > MaxCoeffShift) shift = MaxCoeffShift;
        int count = 1 << shift;
        int curveShift = shift - 1;

        int ax = (x0 - x1 - x1 + x2) << 9, bx = (x1 - x0) << 10;
        int qx = x0 << 10, qdx = bx + (ax >> shift), qddx = ax >> (shift - 1);
        int ay = (y0 - y1 - y1 + y2) << 9, by = (y1 - y0) << 10;
        int qy = y0 << 10, qdy = by + (ay >> shift), qddy = ay >> (shift - 1);
        int lastX = x2 << 10, lastY = y2 << 10;

        int oldx = qx, oldy = qy;
        while (count > 0)
        {
            int newx, newy;
            if (--count > 0)
            {
                newx = oldx + (qdx >> curveShift);
                qdx += qddx;
                newy = oldy + (qdy >> curveShift);
                qdy += qddy;
            }
            else
            {
                newx = lastX;
                newy = lastY;
            }
            Emit(r, oldx, oldy, newx, newy, swapped);
            oldx = newx;
            oldy = newy;
        }
    }

    // ── Cubics ─────────────────────────────────────────────────────────────

    /// <summary><c>SkChopCubicAtYExtrema</c>, then each monotonic piece as a cubic edge.</summary>
    private static void Cubic(Rasterizer r, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Span<Vector2> dst)
    {
        Span<float> t = stackalloc float[2];
        // SkFindCubicExtrema in y.
        float a = p0.Y, b = p1.Y, c = p2.Y, d = p3.Y;
        int roots = CurveMath.FindUnitQuadRoots(d - a + 3 * (b - c), 2 * (a - b - b + c), b - a, t);
        ReadOnlySpan<Vector2> src = [p0, p1, p2, p3];
        if (roots == 0)
        {
            CubicEdge(r, p0, p1, p2, p3);
            return;
        }
        // SkChopCubicAt(src, dst, tValues, roots), one chop at a time.
        CurveMath.ChopCubicAt(src, dst, t[0]);
        if (roots == 2)
        {
            float t1 = Math.Clamp((t[1] - t[0]) / (1 - t[0]), 0f, 1f);
            Span<Vector2> tail = stackalloc Vector2[4];
            dst[3..7].CopyTo(tail);
            CurveMath.ChopCubicAt(tail, dst[3..], t1);
        }
        // Flatten the y extrema.
        dst[2].Y = dst[4].Y = dst[3].Y;
        if (roots == 2) dst[5].Y = dst[7].Y = dst[6].Y;
        for (int i = 0; i <= roots; i++) CubicEdge(r, dst[3 * i], dst[3 * i + 1], dst[3 * i + 2], dst[3 * i + 3]);
    }

    private static int CubicDeltaFromLine(int a, int b, int c, int d)
    {
        int oneThird = (a * 8 - b * 15 + 6 * c + d) * 19 >> 9;
        int twoThird = (a + 6 * b - c * 15 + d * 8) * 19 >> 9;
        return Math.Max(Math.Abs(oneThird), Math.Abs(twoThird));
    }

    /// <summary><c>SkCubicEdge::setCubic</c> and its forward differencing, as device lines.</summary>
    private static void CubicEdge(Rasterizer r, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        int x0 = ToFDot6(p0.X), y0 = ToFDot6(p0.Y);
        int x1 = ToFDot6(p1.X), y1 = ToFDot6(p1.Y);
        int x2 = ToFDot6(p2.X), y2 = ToFDot6(p2.Y);
        int x3 = ToFDot6(p3.X), y3 = ToFDot6(p3.Y);
        bool swapped = false;
        if (y0 > y3)
        {
            (x0, x3) = (x3, x0);
            (x1, x2) = (x2, x1);
            (y0, y3) = (y3, y0);
            (y1, y2) = (y2, y1);
            swapped = true;
        }
        if (((y0 + 32) >> 6) == ((y3 + 32) >> 6)) return;

        int shift = DiffToShift(CubicDeltaFromLine(x0, x1, x2, x3), CubicDeltaFromLine(y0, y1, y2, y3), 2) + 1;
        if (shift > MaxCoeffShift) shift = MaxCoeffShift;
        int upShift = 6;
        int downShift = shift + upShift - 10;
        if (downShift < 0)
        {
            downShift = 0;
            upShift = 10 - shift;
        }
        int count = -1 << shift;
        int ddshift = shift, dshift = downShift;

        int bx = (3 * (x1 - x0)) << upShift;
        int cx = (3 * (x0 - x1 - x1 + x2)) << upShift;
        int dxx = (x3 + 3 * (x1 - x2) - x0) << upShift;
        int fx = x0 << 10;
        int cdx = bx + (cx >> shift) + (dxx >> 2 * shift);
        int cddx = 2 * cx + (3 * dxx >> (shift - 1));
        int cdddx = 3 * dxx >> (shift - 1);

        int by = (3 * (y1 - y0)) << upShift;
        int cy = (3 * (y0 - y1 - y1 + y2)) << upShift;
        int dyy = (y3 + 3 * (y1 - y2) - y0) << upShift;
        int fy = y0 << 10;
        int cdy = by + (cy >> shift) + (dyy >> 2 * shift);
        int cddy = 2 * cy + (3 * dyy >> (shift - 1));
        int cdddy = 3 * dyy >> (shift - 1);

        int lastX = x3 << 10, lastY = y3 << 10;
        int oldx = fx, oldy = fy;
        while (count < 0)
        {
            int newx, newy;
            if (++count < 0)
            {
                newx = oldx + (cdx >> dshift);
                cdx += cddx >> ddshift;
                cddx += cdddx;
                newy = oldy + (cdy >> dshift);
                cdy += cddy >> ddshift;
                cddy += cdddy;
            }
            else
            {
                newx = lastX;
                newy = lastY;
            }
            if (newy < oldy) newy = oldy;
            Emit(r, oldx, oldy, newx, newy, swapped);
            oldx = newx;
            oldy = newy;
        }
    }

    /// <summary>A chord in 16.16 supersample space, as a device line in the curve's own direction.</summary>
    private static void Emit(Rasterizer r, int x0, int y0, int x1, int y1, bool reversed)
    {
        float fx0 = x0 * FixedToDevice, fy0 = y0 * FixedToDevice, fx1 = x1 * FixedToDevice, fy1 = y1 * FixedToDevice;
        if (reversed) r.AddLine(fx1, fy1, fx0, fy0);
        else r.AddLine(fx0, fy0, fx1, fy1);
    }
}
