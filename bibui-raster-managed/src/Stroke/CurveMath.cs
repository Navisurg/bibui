using System.Numerics;

namespace Bibui.Raster.Managed;

/// <summary>
/// The curve geometry the stroker and the dasher stand on: evaluation,
/// chopping, roots, curvature and inflections of quads, cubics and conics.
///
/// <para>Ported from Skia m88's <c>SkGeometry.cpp</c> (the Skia under
/// SkiaSharp 2.88) operation for operation, in single precision where it uses
/// single precision — the Sk names on members map it — so offsets and chops
/// land exactly where the Skia backend's do and strokes match the fixture
/// baselines it drew.</para>
/// </summary>
internal static class CurveMath
{
    /// <summary><c>SK_ScalarNearlyZero</c>: 1/4096.</summary>
    public const float NearlyZero = 1f / (1 << 12);

    public static bool IsNearlyZero(float x, float tol = NearlyZero) => MathF.Abs(x) <= tol;

    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

    public static float LengthSqd(Vector2 v) => v.X * v.X + v.Y * v.Y;

    public static float DistanceToSqd(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>The length of (dx, dy) (<c>SkPoint::Length</c>).</summary>
    public static float Length(float dx, float dy)
    {
        float mag2 = dx * dx + dy * dy;
        if (float.IsFinite(mag2)) return MathF.Sqrt(mag2);
        double xx = dx, yy = dy;
        return (float)Math.Sqrt(xx * xx + yy * yy);
    }

    public static float Distance(Vector2 a, Vector2 b) => Length(a.X - b.X, a.Y - b.Y);

    /// <summary><c>SkPointPriv::CanNormalize</c>: finite and not zero.</summary>
    public static bool CanNormalize(float dx, float dy) => float.IsFinite(dx) && float.IsFinite(dy) && (dx != 0 || dy != 0);

    /// <summary><c>SkPoint::setLength(x, y, length)</c>: scales through a double magnitude; false (and zero) when degenerate.</summary>
    public static bool SetLength(ref Vector2 v, float x, float y, float length)
    {
        double xx = x, yy = y;
        double dmag = Math.Sqrt(xx * xx + yy * yy);
        double dscale = length / dmag;
        x = (float)(x * dscale);
        y = (float)(y * dscale);
        if (!float.IsFinite(x) || !float.IsFinite(y) || (x == 0 && y == 0))
        {
            v = Vector2.Zero;
            return false;
        }
        v = new Vector2(x, y);
        return true;
    }

    public static bool SetLength(ref Vector2 v, float length) => SetLength(ref v, v.X, v.Y, length);

    public static bool Normalize(ref Vector2 v) => SetLength(ref v, v.X, v.Y, 1f);

    /// <summary><c>SkPointPriv::RotateCCW</c>: (x, y) → (y, −x).</summary>
    public static Vector2 RotateCCW(Vector2 v) => new(v.Y, -v.X);

    /// <summary><c>SkPointPriv::RotateCW</c>: (x, y) → (−y, x).</summary>
    public static Vector2 RotateCW(Vector2 v) => new(-v.Y, v.X);

    // ── Roots ──────────────────────────────────────────────────────────────

    private static int ValidUnitDivide(float numer, float denom, out float ratio)
    {
        ratio = 0;
        if (numer < 0)
        {
            numer = -numer;
            denom = -denom;
        }
        if (denom == 0 || numer == 0 || numer >= denom) return 0;
        float r = numer / denom;
        if (float.IsNaN(r)) return 0;
        if (r == 0) return 0;
        ratio = r;
        return 1;
    }

    /// <summary><c>SkFindUnitQuadRoots</c>: roots of At²+Bt+C in (0, 1), ascending, duplicates dropped.</summary>
    public static int FindUnitQuadRoots(float a, float b, float c, Span<float> roots)
    {
        if (a == 0)
        {
            int n0 = ValidUnitDivide(-c, b, out float r0);
            roots[0] = r0;
            return n0;
        }
        double dr = (double)b * b - 4 * (double)a * c;
        if (dr < 0) return 0;
        dr = Math.Sqrt(dr);
        float rr = (float)dr;
        if (!float.IsFinite(rr)) return 0;
        float q = (b < 0) ? -(b - rr) / 2 : -(b + rr) / 2;
        int n = 0;
        if (ValidUnitDivide(q, a, out float x0) == 1) roots[n++] = x0;
        if (ValidUnitDivide(c, q, out float x1) == 1) roots[n++] = x1;
        if (n == 2)
        {
            if (roots[0] > roots[1]) (roots[0], roots[1]) = (roots[1], roots[0]);
            else if (roots[0] == roots[1]) n = 1;
        }
        return n;
    }

    // ── Quads ──────────────────────────────────────────────────────────────

    /// <summary><c>SkEvalQuadAt</c>: (A·t + B)·t + C in the power basis.</summary>
    public static Vector2 EvalQuadAt(ReadOnlySpan<Vector2> src, float t)
    {
        var c = src[0];
        var p1 = src[1];
        var p2 = src[2];
        var b = (p1 - c) * 2f;
        var a = p2 - p1 * 2f + c;
        return (a * t + b) * t + c;
    }

    /// <summary>The quad's tangent at <paramref name="t"/>, falling back to the chord where a control point sits on an end (<c>SkEvalQuadTangentAt</c>).</summary>
    public static Vector2 EvalQuadTangentAt(ReadOnlySpan<Vector2> src, float t)
    {
        if ((t == 0 && src[0] == src[1]) || (t == 1 && src[1] == src[2])) return src[2] - src[0];
        var b = src[1] - src[0];
        var a = src[2] - src[1] - b;
        var tt = a * t + b;
        return tt + tt;
    }

    /// <summary><c>SkChopQuadAt</c>: five points, the two halves sharing dst[2].</summary>
    public static void ChopQuadAt(ReadOnlySpan<Vector2> src, Span<Vector2> dst, float t)
    {
        var p0 = src[0];
        var p1 = src[1];
        var p2 = src[2];
        var p01 = p0 + (p1 - p0) * t;
        var p12 = p1 + (p2 - p1) * t;
        dst[0] = p0;
        dst[1] = p01;
        dst[2] = p01 + (p12 - p01) * t;
        dst[3] = p12;
        dst[4] = p2;
    }

    /// <summary>The t of the quad's greatest curvature, in [0, 1] (<c>SkFindQuadMaxCurvature</c>).</summary>
    public static float FindQuadMaxCurvature(ReadOnlySpan<Vector2> src)
    {
        float ax = src[1].X - src[0].X;
        float ay = src[1].Y - src[0].Y;
        float bx = src[0].X - src[1].X - src[1].X + src[2].X;
        float by = src[0].Y - src[1].Y - src[1].Y + src[2].Y;
        float numer = -(ax * bx + ay * by);
        float denom = bx * bx + by * by;
        if (denom < 0)
        {
            numer = -numer;
            denom = -denom;
        }
        if (numer <= 0) return 0;
        if (numer >= denom) return 1;
        return numer / denom;
    }

    // ── Cubics ─────────────────────────────────────────────────────────────

    /// <summary><c>SkEvalCubicAt</c> for the position.</summary>
    public static Vector2 EvalCubicAt(ReadOnlySpan<Vector2> src, float t)
    {
        var p0 = src[0];
        var p1 = src[1];
        var p2 = src[2];
        var p3 = src[3];
        var a = p3 + 3f * (p1 - p2) - p0;
        var b = 3f * (p2 - p1 * 2f + p0);
        var c = 3f * (p1 - p0);
        return ((a * t + b) * t + c) * t + p0;
    }

    private static Vector2 EvalCubicDerivative(ReadOnlySpan<Vector2> src, float t)
    {
        var p0 = src[0];
        var p1 = src[1];
        var p2 = src[2];
        var p3 = src[3];
        var a = p3 + 3f * (p1 - p2) - p0;
        var b = (p2 - p1 * 2f + p0) * 2f;
        var c = p1 - p0;
        return (a * t + b) * t + c;
    }

    /// <summary>The cubic's tangent at <paramref name="t"/>, with fallbacks where a control point sits on an end (<c>SkEvalCubicAt</c>).</summary>
    public static Vector2 EvalCubicTangentAt(ReadOnlySpan<Vector2> src, float t)
    {
        if ((t == 0 && src[0] == src[1]) || (t == 1 && src[2] == src[3]))
        {
            var tan = t == 0 ? src[2] - src[0] : src[3] - src[1];
            if (tan.X == 0 && tan.Y == 0) tan = src[3] - src[0];
            return tan;
        }
        return EvalCubicDerivative(src, t);
    }

    /// <summary><c>SkChopCubicAt</c>: seven points, the halves sharing dst[3].</summary>
    public static void ChopCubicAt(ReadOnlySpan<Vector2> src, Span<Vector2> dst, float t)
    {
        if (t == 1)
        {
            src[..4].CopyTo(dst);
            dst[4] = dst[5] = dst[6] = src[3];
            return;
        }
        var p0 = src[0];
        var p1 = src[1];
        var p2 = src[2];
        var p3 = src[3];
        var ab = (p1 - p0) * t + p0;
        var bc = (p2 - p1) * t + p1;
        var cd = (p3 - p2) * t + p2;
        var abc = (bc - ab) * t + ab;
        var bcd = (cd - bc) * t + bc;
        var abcd = (bcd - abc) * t + abc;
        dst[0] = p0;
        dst[1] = ab;
        dst[2] = abc;
        dst[3] = abcd;
        dst[4] = bcd;
        dst[5] = cd;
        dst[6] = p3;
    }

    /// <summary>The cubic's inflection points in (0, 1), written to <paramref name="tValues"/>; returns how many (<c>SkFindCubicInflections</c>).</summary>
    public static int FindCubicInflections(ReadOnlySpan<Vector2> src, Span<float> tValues)
    {
        float ax = src[1].X - src[0].X;
        float ay = src[1].Y - src[0].Y;
        float bx = src[2].X - 2 * src[1].X + src[0].X;
        float by = src[2].Y - 2 * src[1].Y + src[0].Y;
        float cx = src[3].X + 3 * (src[1].X - src[2].X) - src[0].X;
        float cy = src[3].Y + 3 * (src[1].Y - src[2].Y) - src[0].Y;
        return FindUnitQuadRoots(bx * cy - by * cx, ax * cy - ay * cx, ax * by - ay * bx, tValues);
    }

    private static void F1DotF2(float s0, float s1, float s2, float s3, Span<float> coeff)
    {
        float a = s1 - s0;
        float b = s2 - 2 * s1 + s0;
        float c = s3 + 3 * (s1 - s2) - s0;
        coeff[0] = c * c;
        coeff[1] = 3 * b * c;
        coeff[2] = 2 * b * b + c * a;
        coeff[3] = a * b;
    }

    /// <summary><c>SkFindCubicMaxCurvature</c>: where F′·F″ = 0, pinned to [0, 1].</summary>
    public static int FindCubicMaxCurvature(ReadOnlySpan<Vector2> src, Span<float> tValues)
    {
        Span<float> cx = stackalloc float[4];
        Span<float> cy = stackalloc float[4];
        F1DotF2(src[0].X, src[1].X, src[2].X, src[3].X, cx);
        F1DotF2(src[0].Y, src[1].Y, src[2].Y, src[3].Y, cy);
        for (int i = 0; i < 4; i++) cx[i] += cy[i];
        return SolveCubicPoly(cx, tValues);
    }

    private static int SolveCubicPoly(ReadOnlySpan<float> coeff, Span<float> tValues)
    {
        if (IsNearlyZero(coeff[0])) return FindUnitQuadRoots(coeff[1], coeff[2], coeff[3], tValues);

        float inva = 1f / coeff[0];
        float a = coeff[1] * inva;
        float b = coeff[2] * inva;
        float c = coeff[3] * inva;
        float q = (a * a - b * 3) / 9;
        float r = (2 * a * a * a - 9 * a * b + 27 * c) / 54;
        float q3 = q * q * q;
        float r2MinusQ3 = r * r - q3;
        float adiv3 = a / 3;

        if (r2MinusQ3 < 0)
        {
            float theta = MathF.Acos(Math.Clamp(r / MathF.Sqrt(q3), -1f, 1f));
            float neg2RootQ = -2 * MathF.Sqrt(q);
            tValues[0] = Math.Clamp(neg2RootQ * MathF.Cos(theta / 3) - adiv3, 0f, 1f);
            tValues[1] = Math.Clamp(neg2RootQ * MathF.Cos((theta + 2 * MathF.PI) / 3) - adiv3, 0f, 1f);
            tValues[2] = Math.Clamp(neg2RootQ * MathF.Cos((theta - 2 * MathF.PI) / 3) - adiv3, 0f, 1f);
            // Bubble sort, then collapse duplicates.
            for (int i = 2; i > 0; --i)
                for (int j = i; j > 0; --j)
                    if (tValues[j] < tValues[j - 1]) (tValues[j], tValues[j - 1]) = (tValues[j - 1], tValues[j]);
            int count = 3;
            int k = 0;
            for (int n = 3; n > 1; --n)
            {
                if (tValues[k] == tValues[k + 1])
                {
                    for (int i = k + 1; i < k + n; ++i) tValues[i - 1] = tValues[i];
                    count -= 1;
                }
                else k++;
            }
            return count;
        }
        float aa = MathF.Abs(r) + MathF.Sqrt(r2MinusQ3);
        aa = MathF.Pow(aa, 0.3333333f);
        if (r > 0) aa = -aa;
        if (aa != 0) aa += q / aa;
        tValues[0] = Math.Clamp(aa - adiv3, 0f, 1f);
        return 1;
    }

    private static bool OnSameSide(ReadOnlySpan<Vector2> src, int testIndex, int lineIndex)
    {
        var origin = src[lineIndex];
        var line = src[lineIndex + 1] - origin;
        float c0 = Cross(line, src[testIndex] - origin);
        float c1 = Cross(line, src[testIndex + 1] - origin);
        return c0 * c1 >= 0;
    }

    /// <summary><c>SkFindCubicCusp</c>: the t of a cusp in (0, 1), or −1.</summary>
    public static float FindCubicCusp(ReadOnlySpan<Vector2> src)
    {
        if (src[0] == src[1]) return -1;
        if (src[2] == src[3]) return -1;
        if (OnSameSide(src, 0, 2) || OnSameSide(src, 2, 0)) return -1;
        Span<float> maxCurvature = stackalloc float[3];
        int roots = FindCubicMaxCurvature(src, maxCurvature);
        for (int i = 0; i < roots; ++i)
        {
            float testT = maxCurvature[i];
            if (0 >= testT || testT >= 1) continue;
            var dPt = EvalCubicDerivative(src, testT);
            float mag = LengthSqd(dPt);
            float precision = (DistanceToSqd(src[1], src[0]) + DistanceToSqd(src[2], src[1]) + DistanceToSqd(src[3], src[2])) * 1e-8f;
            if (mag < precision) return testT;
        }
        return -1;
    }
}

/// <summary>A conic (rational quadratic), with the operations the stroker and dasher need (<c>SkConic</c>'s).</summary>
internal struct ConicCurve
{
    public Vector2 P0, P1, P2;
    public float W;

    public ConicCurve(Vector2 p0, Vector2 p1, Vector2 p2, float w)
    {
        P0 = p0;
        P1 = p1;
        P2 = p2;
        W = w;
    }

    /// <summary><c>SkConic::evalAt</c>: the numerator and denominator quads, divided.</summary>
    public readonly Vector2 EvalAt(float t)
    {
        var p1w = P1 * W;
        var nC = P0;
        var nA = P2 - p1w * 2f + P0;
        var nB = (p1w - P0) * 2f;
        float dC = 1f;
        float dB = 2f * (W - dC);
        float dA = 0f - dB;
        var numer = (nA * t + nB) * t + nC;
        float denom = (dA * t + dB) * t + dC;
        return numer / denom;
    }

    /// <summary><c>SkConic::evalTangentAt</c> (unnormalised).</summary>
    public readonly Vector2 EvalTangentAt(float t)
    {
        if ((t == 0 && P0 == P1) || (t == 1 && P1 == P2)) return P2 - P0;
        var p20 = P2 - P0;
        var p10 = P1 - P0;
        var c = W * p10;
        var a = W * p20 - p20;
        var b = p20 - c - c;
        return (a * t + b) * t + c;
    }

    /// <summary><c>SkConic::chopAt(t, dst[2])</c>: false if the halves are not finite.</summary>
    public readonly bool ChopAt(float t, out ConicCurve a, out ConicCurve b)
    {
        // Map to homogeneous 3D, interpolate each coordinate, project back.
        float x0 = P0.X, y0 = P0.Y, z0 = 1;
        float x1 = P1.X * W, y1 = P1.Y * W, z1 = W;
        float x2 = P2.X, y2 = P2.Y, z2 = 1;
        static void Interp(float s0, float s1, float s2, float t, out float d0, out float d1, out float d2)
        {
            float ab = s0 + (s1 - s0) * t;
            float bc = s1 + (s2 - s1) * t;
            d0 = ab;
            d1 = ab + (bc - ab) * t;
            d2 = bc;
        }
        Interp(x0, x1, x2, t, out float tx0, out float tx1, out float tx2);
        Interp(y0, y1, y2, t, out float ty0, out float ty1, out float ty2);
        Interp(z0, z1, z2, t, out float tz0, out float tz1, out float tz2);
        var m = new Vector2(tx1 / tz1, ty1 / tz1);
        float root = MathF.Sqrt(tz1);
        a = new ConicCurve(P0, new Vector2(tx0 / tz0, ty0 / tz0), m, tz0 / root);
        b = new ConicCurve(m, new Vector2(tx2 / tz2, ty2 / tz2), P2, tz2 / root);
        return float.IsFinite(a.P1.X) && float.IsFinite(a.P1.Y) && float.IsFinite(m.X) && float.IsFinite(m.Y)
               && float.IsFinite(b.P1.X) && float.IsFinite(b.P1.Y) && float.IsFinite(a.W) && float.IsFinite(b.W);
    }

    /// <summary><c>SkConic::chopAt(t1, t2, dst)</c>: the piece between two parameters.</summary>
    public readonly ConicCurve ChopAt(float t1, float t2)
    {
        if (0 == t1 || 1 == t2)
        {
            if (0 == t1 && 1 == t2) return this;
            if (ChopAt(t1 != 0 ? t1 : t2, out var a, out var b)) return t1 != 0 ? b : a;
        }
        var p1w = P1 * W;
        var nC = P0;
        var nA = P2 - p1w * 2f + P0;
        var nB = (p1w - P0) * 2f;
        float dC = 1f, dB = 2f * (W - dC), dA = 0f - dB;
        Vector2 N(float t) => (nA * t + nB) * t + nC;
        float D(float t) => (dA * t + dB) * t + dC;
        var aXY = N(t1);
        float aZZ = D(t1);
        float mid = (t1 + t2) / 2;
        var dXY = N(mid);
        float dZZ = D(mid);
        var cXY = N(t2);
        float cZZ = D(t2);
        var bXY = dXY * 2f - (aXY + cXY) * 0.5f;
        float bZZ = dZZ * 2f - (aZZ + cZZ) * 0.5f;
        return new ConicCurve(aXY / aZZ, bXY / bZZ, cXY / cZZ, bZZ / MathF.Sqrt(aZZ * cZZ));
    }

    public const int MaxConicsForArc = 5;

    /// <summary>
    /// <c>SkConic::BuildUnitArc</c>: the arc of the unit circle from
    /// <paramref name="uStart"/> to <paramref name="uStop"/> as one conic per
    /// quadrant plus a remainder, mapped by scale <paramref name="scale"/> and
    /// translation <paramref name="center"/>. Returns how many conics; 0 when
    /// the vectors coincide.
    /// </summary>
    public static int BuildUnitArc(Vector2 uStart, Vector2 uStop, bool clockwise, Vector2 scale, Vector2 center, Span<ConicCurve> dst)
    {
        float x = CurveMath.Dot(uStart, uStop);
        float y = CurveMath.Cross(uStart, uStop);
        float absY = MathF.Abs(y);
        if (absY <= CurveMath.NearlyZero && x > 0 && ((y >= 0 && clockwise) || (y <= 0 && !clockwise))) return 0;
        if (!clockwise) y = -y;

        int quadrant = 0;
        if (0 == y) quadrant = 2;
        else if (0 == x) quadrant = y > 0 ? 1 : 3;
        else
        {
            if (y < 0) quadrant += 2;
            if ((x < 0) != (y < 0)) quadrant += 1;
        }

        ReadOnlySpan<Vector2> q =
        [
            new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1),
            new(1, 0),
        ];
        const float quadrantWeight = 0.707106781f;
        int conicCount = quadrant;
        for (int i = 0; i < conicCount; ++i) dst[i] = new ConicCurve(q[i * 2], q[i * 2 + 1], q[i * 2 + 2], quadrantWeight);

        var finalP = new Vector2(x, y);
        var lastQ = q[quadrant * 2];
        float dot = CurveMath.Dot(lastQ, finalP);
        if (dot < 1)
        {
            var offCurve = new Vector2(lastQ.X + x, lastQ.Y + y);
            float cosThetaOver2 = MathF.Sqrt((1 + dot) / 2);
            CurveMath.SetLength(ref offCurve, 1f / cosThetaOver2);
            if (CurveMath.CanNormalize(lastQ.X - offCurve.X, lastQ.Y - offCurve.Y))
            {
                dst[conicCount] = new ConicCurve(lastQ, offCurve, finalP, cosThetaOver2);
                conicCount += 1;
            }
        }

        // Rotate to uStart (SkMatrix::setSinCos), flip for CCW, then scale and translate.
        float sin = uStart.Y, cos = uStart.X;
        for (int i = 0; i < conicCount; ++i)
        {
            ref var c = ref dst[i];
            c.P0 = Map(c.P0);
            c.P1 = Map(c.P1);
            c.P2 = Map(c.P2);
        }
        return conicCount;

        Vector2 Map(Vector2 p)
        {
            if (!clockwise) p.Y = -p.Y;
            // setSinCos(sin, cos): [cos −sin; sin cos], then postConcat(scale, translate).
            float rx = cos * p.X + -sin * p.Y;
            float ry = sin * p.X + cos * p.Y;
            return new Vector2(rx * scale.X + center.X, ry * scale.Y + center.Y);
        }
    }
}
