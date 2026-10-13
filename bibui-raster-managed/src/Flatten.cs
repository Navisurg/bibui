using System.Numerics;

namespace Bibui.Raster.Managed;

/// <summary>
/// Path → device-space line segments. Two flatteners: one for fills, which
/// subdivides to a fixed tolerance, and one for hairlines, which subdivides
/// each curve into a power-of-two number of chords chosen from how far it
/// bulges — a hairline's pixels are its segments, so its subdivision decides
/// them.
///
/// <para>The hairline flattener and <see cref="Conic"/> reproduce Skia 2.88's
/// (<c>SkScan_Hairline.cpp</c>'s <c>hair_path</c>, <c>SkConic</c>) exactly:
/// the fixture baselines are drawn by the Skia backend, and nearly every bibui
/// border is a hairline, so the managed backend must cut curves into the same
/// chords to match those baselines pixel for pixel.</para>
/// </summary>
internal static class Flatten
{
    /// <summary>Maximum distance, in device px, between a curve and its flattened chords for fills.</summary>
    public const float FillTolerance = 0.02f;

    /// <summary>How closely the quads a conic becomes must follow it, before they are flattened.</summary>
    public const float ConicTolerance = 0.01f;

    /// <summary>Flattens every contour of <paramref name="path"/> (each implicitly closed) into <paramref name="r"/>.</summary>
    public static void Fill(Path path, in Matrix3x2 m, Rasterizer r)
    {
        var verbs = path.Verbs;
        var pts = path.Points;
        var ws = path.Weights;
        int pi = 0, wi = 0;
        Vector2 start = default, cur = default;
        bool open = false;
        Span<Vector2> quads = stackalloc Vector2[1 + 2 * 32];
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
                    FillQuad(r, cur, p1, p2);
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
                    for (int q = 0; q < nq; q++) FillQuad(r, quads[2 * q], quads[2 * q + 1], quads[2 * q + 2]);
                    cur = p2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    var p3 = Vector2.Transform(pts[pi + 2], m);
                    pi += 3;
                    FillCubic(r, cur, p1, p2, p3);
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

    private static void FillQuad(Rasterizer r, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        // Uniform steps: the chord error of a quad is |p0 − 2p1 + p2| / (4n²).
        float dd = (p0 - 2 * p1 + p2).Length();
        int n = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(dd / (4 * FillTolerance))), 1, 256);
        var prev = p0;
        for (int i = 1; i < n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            var p = u * u * p0 + 2 * u * t * p1 + t * t * p2;
            r.AddLine(prev.X, prev.Y, p.X, p.Y);
            prev = p;
        }
        r.AddLine(prev.X, prev.Y, p2.X, p2.Y);
    }

    private static void FillCubic(Rasterizer r, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        // |B''| ≤ 6·max(|p0−2p1+p2|, |p1−2p2+p3|), so the chord error is ≤ 3M/(4n²).
        float m = MathF.Max((p0 - 2 * p1 + p2).Length(), (p1 - 2 * p2 + p3).Length());
        int n = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(3 * m / (4 * FillTolerance))), 1, 512);
        var prev = p0;
        for (int i = 1; i < n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            var p = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
            r.AddLine(prev.X, prev.Y, p.X, p.Y);
            prev = p;
        }
        r.AddLine(prev.X, prev.Y, p3.X, p3.Y);
    }

    // ── Hairlines: power-of-two subdivision ──────────────────────────────────

    private const int MaxQuadSubdivideLevel = 5;
    private const int MaxCubicSubdivideLevel = 9;

    /// <summary>
    /// Feeds <paramref name="path"/>'s segments to <paramref name="line"/>:
    /// open contours stay open, conics become
    /// quads at ¼ px, quads and cubics become 2^level chords.
    /// </summary>
    public static void Hairline<TLine>(Path path, in Matrix3x2 m, ref TLine line) where TLine : struct, IHairlineSink
    {
        var verbs = path.Verbs;
        var pts = path.Points;
        var ws = path.Weights;
        int pi = 0, wi = 0;
        Vector2 first = default, last = default;
        Span<Vector2> quads = stackalloc Vector2[1 + 2 * 32];
        Span<Vector2> tmp = stackalloc Vector2[(1 << MaxCubicSubdivideLevel) + 1];
        foreach (var v in verbs)
        {
            switch (v)
            {
                case PathVerb.Move:
                    first = last = Vector2.Transform(pts[pi++], m);
                    break;
                case PathVerb.Line:
                {
                    var p = Vector2.Transform(pts[pi++], m);
                    line.Line(last, p);
                    last = p;
                    break;
                }
                case PathVerb.Quad:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    pi += 2;
                    HairQuad(ref line, last, p1, p2, tmp);
                    last = p2;
                    break;
                }
                case PathVerb.Conic:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    pi += 2;
                    float w = ws[wi++];
                    int pow2 = Conic.QuadPow2(last, p1, p2, w, 0.25f);
                    int nq = Conic.ToQuads(last, p1, p2, w, pow2, quads);
                    for (int q = 0; q < nq; q++) HairQuad(ref line, quads[2 * q], quads[2 * q + 1], quads[2 * q + 2], tmp);
                    last = p2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    var p1 = Vector2.Transform(pts[pi], m);
                    var p2 = Vector2.Transform(pts[pi + 1], m);
                    var p3 = Vector2.Transform(pts[pi + 2], m);
                    pi += 3;
                    HairCubic(ref line, last, p1, p2, p3, tmp);
                    last = p3;
                    break;
                }
                case PathVerb.Close:
                    line.Line(last, first);
                    last = first;
                    break;
            }
        }
    }

    private static int QuadLevel(Vector2 p0, Vector2 p1, Vector2 p2)
    {
        float dx = MathF.Abs((p0.X + p2.X) * 0.5f - p1.X);
        float dy = MathF.Abs((p0.Y + p2.Y) * 0.5f - p1.Y);
        uint idx = (uint)(int)MathF.Ceiling(dx), idy = (uint)(int)MathF.Ceiling(dy);
        uint d = idx > idy ? idx + (idy >> 1) : idy + (idx >> 1);
        int level = (33 - System.Numerics.BitOperations.LeadingZeroCount(d)) >> 1;
        return Math.Min(level, MaxQuadSubdivideLevel);
    }

    private static void HairQuad<TLine>(ref TLine line, Vector2 p0, Vector2 p1, Vector2 p2, Span<Vector2> tmp) where TLine : struct, IHairlineSink
    {
        int lines = 1 << QuadLevel(p0, p1, p2);
        // SkQuadCoeff: A = p2 − 2p1 + p0, B = 2(p1 − p0), C = p0; t accumulates dt.
        var a = p2 - (p1 + p1) + p0;
        var b = (p1 - p0) * 2f;
        var c = p0;
        float dt = 1f / lines, t = 0;
        tmp[0] = p0;
        for (int i = 1; i < lines; i++)
        {
            t += dt;
            tmp[i] = new Vector2((a.X * t + b.X) * t + c.X, (a.Y * t + b.Y) * t + c.Y);
        }
        tmp[lines] = p2;
        for (int i = 0; i < lines; i++) line.Line(tmp[i], tmp[i + 1]);
    }

    private static void HairCubic<TLine>(ref TLine line, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Span<Vector2> tmp) where TLine : struct, IHairlineSink
    {
        var p13 = (1f / 3f) * p3 + (2f / 3f) * p0;
        var p23 = (1f / 3f) * p0 + (2f / 3f) * p3;
        var d1 = Vector2.Abs(p1 - p13);
        var d2 = Vector2.Abs(p2 - p23);
        float diff = MathF.Max(MathF.Max(d1.X, d1.Y), MathF.Max(d2.X, d2.Y));
        float tol = 1f / 8;
        int lines = 1 << MaxCubicSubdivideLevel;
        for (int i = 0; i < MaxCubicSubdivideLevel; i++)
        {
            if (diff < tol) { lines = 1 << i; break; }
            tol *= 4;
        }
        if (lines == 1) { line.Line(p0, p3); return; }
        // SkCubicCoeff: A = p3 + 3(p1 − p2) − p0, B = 3(p2 − 2p1 + p0), C = 3(p1 − p0), D = p0.
        var a = p3 + 3f * (p1 - p2) - p0;
        var b = 3f * (p2 - (p1 + p1) + p0);
        var c = 3f * (p1 - p0);
        float dt = 1f / lines, t = 0;
        tmp[0] = p0;
        for (int i = 1; i < lines; i++)
        {
            t += dt;
            tmp[i] = new Vector2(((a.X * t + b.X) * t + c.X) * t + p0.X, ((a.Y * t + b.Y) * t + c.Y) * t + p0.Y);
        }
        tmp[lines] = p3;
        for (int i = 0; i < lines; i++) line.Line(tmp[i], tmp[i + 1]);
    }
}

/// <summary>Receives hairline segments in device space.</summary>
internal interface IHairlineSink
{
    void Line(Vector2 a, Vector2 b);
}

/// <summary>Conic subdivision into quads, so a hairline's chords land on the same points as the hairline flattener's (see <see cref="Flatten"/>).</summary>
internal static class Conic
{
    public const int MaxPow2 = 5;

    /// <summary>log2 of how many quads approximate the conic within <paramref name="tol"/> (<c>SkConic::computeQuadPOW2</c>).</summary>
    public static int QuadPow2(Vector2 p0, Vector2 p1, Vector2 p2, float w, float tol)
    {
        float a = w - 1;
        float k = a / (4 * (2 + a));
        float x = k * (p0.X - 2 * p1.X + p2.X);
        float y = k * (p0.Y - 2 * p1.Y + p2.Y);
        float error = MathF.Sqrt(x * x + y * y);
        int pow2;
        for (pow2 = 0; pow2 < MaxPow2; ++pow2)
        {
            if (error <= tol) break;
            error *= 0.25f;
        }
        return pow2;
    }

    /// <summary>Writes 1 + 2·2^pow2 points (shared end points) into <paramref name="dst"/>; returns the quad count.</summary>
    public static int ToQuads(Vector2 p0, Vector2 p1, Vector2 p2, float w, int pow2, Span<Vector2> dst)
    {
        dst[0] = p0;
        int k = 1;
        Subdivide(p0, p1, p2, w, dst, ref k, pow2);
        return 1 << pow2;
    }

    private static void Subdivide(Vector2 p0, Vector2 p1, Vector2 p2, float w, Span<Vector2> dst, ref int k, int level)
    {
        if (level == 0)
        {
            dst[k++] = p1;
            dst[k++] = p2;
            return;
        }
        // SkConic::chop
        float scale = 1f / (1f + w);
        float newW = MathF.Sqrt(0.5f + w * 0.5f);
        var wp1 = w * p1;
        var m = (p0 + (wp1 + wp1) + p2) * scale * 0.5f;
        var a1 = (p0 + wp1) * scale;
        var b1 = (wp1 + p2) * scale;
        float startY = p0.Y, endY = p2.Y;
        if (Between(startY, p1.Y, endY))
        {
            // Keep the halves monotonic in y if the whole was.
            float midY = m.Y;
            if (!Between(startY, midY, endY))
                m.Y = MathF.Abs(midY - startY) < MathF.Abs(midY - endY) ? startY : endY;
            if (!Between(startY, a1.Y, m.Y)) a1.Y = startY;
            if (!Between(m.Y, b1.Y, endY)) b1.Y = endY;
        }
        level--;
        Subdivide(p0, a1, m, newW, dst, ref k, level);
        Subdivide(m, b1, p2, newW, dst, ref k, level);
    }

    private static bool Between(float a, float b, float c) => (a - b) * (c - b) <= 0;
}
