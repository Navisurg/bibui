// The stroker and the dasher. Coverage tests draw with exact coverage
// and compare with the stroke's own geometry, point-sampled; structure tests
// read the outline or the dashes directly.
using System.Numerics;
using Bibui.Raster.Managed;
using Path = Bibui.Raster.Managed.Path;

static class StrokeTests
{
    const uint White = 0xFFFFFFFF, Black = 0xFF000000;

    // ── Helpers ──────────────────────────────────────────────────────────

    static (Surface s, Canvas c) Black8(int w = 80, int h = 64)
    {
        var s = new Surface(w, h);
        var c = new Canvas(s);
        c.Clear(Black);
        return (s, c);
    }

    static int Cov(Surface s, int x, int y) => (int)((s.GetPixel(x, y) >> 16) & 0xFF);

    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static double Reference(int x, int y, Func<float, float, bool> inside, int n = 32)
    {
        int hit = 0;
        for (int sy = 0; sy < n; sy++)
        for (int sx = 0; sx < n; sx++)
            if (inside(x + (sx + 0.5f) / n, y + (sy + 0.5f) / n)) hit++;
        return 255.0 * hit / (n * n);
    }

    /// <summary>Every pixel within <paramref name="tol"/> of the reference, and the mean error under <paramref name="meanTol"/>.</summary>
    static void AssertCoverage(Surface s, Func<float, float, bool> inside, double tol, double meanTol, string what)
    {
        double worst = 0, sum = 0;
        int n = 0;
        (int x, int y, int got, double want) at = default;
        for (int y = 0; y < s.Height; y++)
        for (int x = 0; x < s.Width; x++)
        {
            double r = Reference(x, y, inside);
            int got = Cov(s, x, y);
            double e = Math.Abs(got - r);
            sum += e;
            n++;
            if (e > worst) { worst = e; at = (x, y, got, r); }
        }
        if (worst > tol)
            throw new Exception($"{what}: pixel ({at.x},{at.y}) is {at.got}, reference {at.want:F1} (|err| {worst:F1} > {tol})");
        if (sum / n > meanTol) throw new Exception($"{what}: mean |err| {sum / n:F3} > {meanTol}");
    }

    static float DistToSegment(float px, float py, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 == 0 ? 0 : Math.Clamp(Vector2.Dot(new Vector2(px, py) - a, ab) / len2, 0, 1);
        return Vector2.Distance(new Vector2(px, py), a + ab * t);
    }

    /// <summary>Inside a butt-capped segment, extended by <paramref name="ext"/> at both ends (a square cap is ext = half width).</summary>
    static bool InBand(float px, float py, Vector2 a, Vector2 b, float h, float ext)
    {
        var d = b - a;
        float len = d.Length();
        var u = d / len;
        var p = new Vector2(px, py) - a;
        float along = Vector2.Dot(p, u);
        float across = MathF.Abs(u.X * p.Y - u.Y * p.X);
        return along >= -ext && along <= len + ext && across <= h;
    }

    static bool InConvex(float px, float py, ReadOnlySpan<Vector2> poly)
    {
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            float c = (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
            if (c == 0) continue;
            int sc = c > 0 ? 1 : -1;
            if (sign == 0) sign = sc;
            else if (sc != sign) return false;
        }
        return true;
    }

    static Path Line(float x0, float y0, float x1, float y1)
    {
        var p = new Path();
        p.MoveTo(x0, y0);
        p.LineTo(x1, y1);
        return p;
    }

    static Paint Stroke(float width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter, float miter = 0)
        => new() { Color = White, Style = PaintStyle.Stroke, StrokeWidth = width, Cap = cap, Join = join, MiterLimit = miter };

    // ── Caps ─────────────────────────────────────────────────────────────

    public static void ButtCapIsTheSegment()
    {
        var (s, c) = Black8();
        var a = new Vector2(12.3f, 20.6f);
        var b = new Vector2(61.7f, 41.2f);
        c.DrawPath(Line(a.X, a.Y, b.X, b.Y), Stroke(8));
        AssertCoverage(s, (x, y) => InBand(x, y, a, b, 4, 0), 3, 0.2, "butt");
    }

    public static void SquareCapExtendsByHalfTheWidth()
    {
        var (s, c) = Black8();
        var a = new Vector2(12.3f, 20.6f);
        var b = new Vector2(61.7f, 41.2f);
        c.DrawPath(Line(a.X, a.Y, b.X, b.Y), Stroke(8, LineCap.Square));
        AssertCoverage(s, (x, y) => InBand(x, y, a, b, 4, 4), 3, 0.2, "square");
    }

    public static void RoundCapIsAHalfDisc()
    {
        var (s, c) = Black8();
        var a = new Vector2(14.3f, 20.6f);
        var b = new Vector2(61.7f, 41.2f);
        c.DrawPath(Line(a.X, a.Y, b.X, b.Y), Stroke(12, LineCap.Round));
        // The cap is two quarter conics, followed by quads to a quarter pixel.
        AssertCoverage(s, (x, y) => DistToSegment(x, y, a, b) <= 6, 24, 0.5, "round");
    }

    public static void RoundCapsOnDrawLine()
    {
        // DrawLine's round cap used to be drawn square.
        var (s, c) = Black8();
        c.DrawLine(20, 30, 60, 30, Stroke(10, LineCap.Round));
        AssertCoverage(s, (x, y) => DistToSegment(x, y, new(20, 30), new(60, 30)) <= 5, 24, 0.5, "drawLine round");
        // (15, 25) is inside a square end and outside a round one.
        Assert(Cov(s, 15, 25) < 40, $"a round end is not a square one ({Cov(s, 15, 25)})");
    }

    // ── Joins ────────────────────────────────────────────────────────────

    static readonly Vector2 J0 = new(10.5f, 52.25f), JP = new(40.25f, 12.5f), J1 = new(70.5f, 52.25f);

    static Path Chevron()
    {
        var p = new Path();
        p.MoveTo(J0);
        p.LineTo(JP);
        p.LineTo(J1);
        return p;
    }

    /// <summary>The outer corner of the chevron's join: the two offset points and the miter tip.</summary>
    static (Vector2 n0, Vector2 n1, Vector2 tip) Corner(float h)
    {
        var u0 = Vector2.Normalize(JP - J0);
        var u1 = Vector2.Normalize(J1 - JP);
        // Outer side of a left-then-right turn on screen: the normals pointing up.
        var n0 = new Vector2(u0.Y, -u0.X);
        var n1 = new Vector2(u1.Y, -u1.X);
        if (n0.Y > 0) n0 = -n0;
        if (n1.Y > 0) n1 = -n1;
        var bis = Vector2.Normalize(n0 + n1);
        float cosHalf = Vector2.Dot(bis, n0);
        return (JP + n0 * h, JP + n1 * h, JP + bis * (h / cosHalf));
    }

    static bool InChevron(float x, float y, float h) => InBand(x, y, J0, JP, h, 0) || InBand(x, y, JP, J1, h, 0);

    public static void MiterJoinReachesTheTip()
    {
        var (s, c) = Black8();
        float h = 4;
        var (n0, n1, tip) = Corner(h);
        c.DrawPath(Chevron(), Stroke(2 * h, join: LineJoin.Miter, miter: 10));
        Span<Vector2> wedge = [JP, n0, tip, n1];
        var w = wedge.ToArray();
        AssertCoverage(s, (x, y) => InChevron(x, y, h) || InConvex(x, y, w), 3, 0.2, "miter");
    }

    public static void MiterLimitFallsBackToBevel()
    {
        float h = 4;
        var (n0, n1, tip) = Corner(h);
        // The miter here is 1/sin(θ/2) ≈ 1.67 widths: a limit of 1.5 cuts it to a bevel.
        var (s, c) = Black8();
        c.DrawPath(Chevron(), Stroke(2 * h, join: LineJoin.Miter, miter: 1.5f));
        var tri = new[] { JP, n0, n1 };
        AssertCoverage(s, (x, y) => InChevron(x, y, h) || InConvex(x, y, tri), 3, 0.2, "miter limit → bevel");
        var (s2, c2) = Black8();
        c2.DrawPath(Chevron(), Stroke(2 * h, join: LineJoin.Miter, miter: 2f));
        var probe = Vector2.Lerp(JP, tip, 0.9f);
        Assert(Cov(s2, (int)probe.X, (int)probe.Y) > 0 && Cov(s, (int)probe.X, (int)probe.Y) == 0,
            "a limit of 2 keeps the miter, 1.5 cuts it");
    }

    public static void BevelJoinIsATriangle()
    {
        var (s, c) = Black8();
        float h = 4;
        var (n0, n1, _) = Corner(h);
        c.DrawPath(Chevron(), Stroke(2 * h, join: LineJoin.Bevel));
        var tri = new[] { JP, n0, n1 };
        AssertCoverage(s, (x, y) => InChevron(x, y, h) || InConvex(x, y, tri), 3, 0.2, "bevel");
    }

    public static void RoundJoinIsADisc()
    {
        var (s, c) = Black8();
        float h = 5;
        c.DrawPath(Chevron(), Stroke(2 * h, join: LineJoin.Round));
        AssertCoverage(s, (x, y) => InChevron(x, y, h) || Vector2.Distance(new(x, y), JP) <= h, 24, 0.3, "round join");
    }

    // ── Closed and open ──────────────────────────────────────────────────

    static Path Triangle(bool close)
    {
        var p = new Path();
        p.MoveTo(15.5f, 50.5f);
        p.LineTo(40.5f, 10.5f);
        p.LineTo(65.5f, 50.5f);
        if (close) p.Close();
        else p.LineTo(15.5f, 50.5f);
        return p;
    }

    public static void ClosedContourJoinsWhereItStarted()
    {
        // Closed, the start is a miter corner; open (back to the start), it is two butt ends.
        var (sc, cc) = Black8();
        cc.DrawPath(Triangle(close: true), Stroke(6, join: LineJoin.Miter, miter: 10));
        var (so, co) = Black8();
        co.DrawPath(Triangle(close: false), Stroke(6, join: LineJoin.Miter, miter: 10));
        // Just outside the start vertex, down-left along the bisector.
        Assert(Cov(sc, 12, 52) > 128, $"closed: miter at the start ({Cov(sc, 12, 52)})");
        Assert(Cov(so, 12, 52) < Cov(sc, 12, 52), $"open: no join at the start ({Cov(so, 12, 52)} vs {Cov(sc, 12, 52)})");
        // The closed outline is two contours (outer and inner), the open one is one.
        var outline = new Path();
        new PathStroker().Stroke(Triangle(true), Stroke(6), 1, outline);
        int moves = 0;
        foreach (var v in outline.Verbs) if (v == PathVerb.Move) moves++;
        Assert(moves == 2, $"closed: {moves} contours");
        outline.Rewind();
        new PathStroker().Stroke(Triangle(false), Stroke(6), 1, outline);
        moves = 0;
        foreach (var v in outline.Verbs) if (v == PathVerb.Move) moves++;
        Assert(moves == 1, $"open: {moves} contours");
    }

    // ── Degenerate segments ──────────────────────────────────────────────

    public static void ZeroLengthWithRoundCapIsADot()
    {
        var (s, c) = Black8();
        c.DrawPath(Line(40.3f, 30.6f, 40.3f, 30.6f), Stroke(10, LineCap.Round));
        AssertCoverage(s, (x, y) => Vector2.Distance(new(x, y), new(40.3f, 30.6f)) <= 5, 24, 0.5, "round dot");
    }

    public static void ZeroLengthWithSquareCapIsASquare()
    {
        var (s, c) = Black8();
        c.DrawPath(Line(40.3f, 30.6f, 40.3f, 30.6f), Stroke(10, LineCap.Square));
        AssertCoverage(s, (x, y) => MathF.Abs(x - 40.3f) <= 5 && MathF.Abs(y - 30.6f) <= 5, 4, 0.2, "square dot");
    }

    public static void ZeroLengthWithButtCapIsNothing()
    {
        var (s, c) = Black8();
        c.DrawPath(Line(40.3f, 30.6f, 40.3f, 30.6f), Stroke(10));
        AssertCoverage(s, (_, _) => false, 0, 0, "butt dot");
    }

    public static void MoveThenCloseWithRoundCapIsADot()
    {
        var (s, c) = Black8();
        var p = new Path();
        p.MoveTo(40.3f, 30.6f);
        p.Close();
        c.DrawPath(p, Stroke(10, LineCap.Round));
        AssertCoverage(s, (x, y) => Vector2.Distance(new(x, y), new(40.3f, 30.6f)) <= 5, 24, 0.5, "move+close dot");
    }

    public static void DegenerateQuadAndCubicAreLines()
    {
        // Control points on the chord: stroked as the line they are.
        var (s, c) = Black8();
        var p = new Path();
        p.MoveTo(10, 20);
        p.QuadTo(30, 20, 50, 20);
        p.MoveTo(10, 40);
        p.CubicTo(20, 40, 50, 40, 70, 40);
        c.DrawPath(p, Stroke(6));
        AssertCoverage(s, (x, y) => InBand(x, y, new(10, 20), new(50, 20), 3, 0) || InBand(x, y, new(10, 40), new(70, 40), 3, 0), 3, 0.2, "flat curves");
    }

    // ── Curves ───────────────────────────────────────────────────────────

    public static void CircleStrokeIsARing()
    {
        var (s, c) = Black8();
        var p = new Path();
        p.AddCircle(40.3f, 31.7f, 20);
        c.DrawPath(p, Stroke(6));
        AssertCoverage(s, (x, y) => MathF.Abs(Vector2.Distance(new(x, y), new(40.3f, 31.7f)) - 20) <= 3, 24, 0.5, "ring");
    }

    public static void CubicOffsetStaysWithinAQuarterPixel()
    {
        // Every outline point of a stroked S-curve is half the width from the curve, to ¼ px.
        var src = new Path();
        src.MoveTo(10, 50);
        src.CubicTo(20, 0, 70, 100, 90, 40);
        var outline = new Path();
        new PathStroker().Stroke(src, Stroke(8), 1, outline);
        var curve = new Vector2[2001];
        for (int i = 0; i <= 2000; i++)
        {
            float t = i / 2000f, u = 1 - t;
            curve[i] = u * u * u * new Vector2(10, 50) + 3 * u * u * t * new Vector2(20, 0) + 3 * u * t * t * new Vector2(70, 100) + t * t * t * new Vector2(90, 40);
        }
        // Sample the outline's segments (quads evaluated) against the curve; skip the butt caps.
        var verbs = outline.Verbs;
        var pts = outline.Points;
        int pi = 0;
        Vector2 cur = default;
        double worst = 0;
        foreach (var v in verbs)
        {
            switch (v)
            {
                case PathVerb.Move: cur = pts[pi++]; break;
                case PathVerb.Line: cur = pts[pi++]; break;
                case PathVerb.Quad:
                    for (int k = 0; k <= 8; k++)
                    {
                        float t = k / 8f, u = 1 - t;
                        var q = u * u * cur + 2 * u * t * pts[pi] + t * t * pts[pi + 1];
                        float d = float.MaxValue;
                        for (int i = 0; i < curve.Length - 1; i++) d = MathF.Min(d, DistToSegment(q.X, q.Y, curve[i], curve[i + 1]));
                        worst = Math.Max(worst, Math.Abs(d - 4));
                    }
                    cur = pts[pi + 1];
                    pi += 2;
                    break;
                default: pi += Path.PointsIn(v); break;
            }
        }
        Assert(worst <= 0.25, $"offset error {worst:F3} px");
    }

    public static void StrokeAndFillIsOneOutline()
    {
        // Stroke-and-fill in a translucent colour: the fill and the stroke must not double up.
        var (s, c) = Black8();
        var p = new Path();
        p.MoveTo(20, 20);
        p.LineTo(60, 20);
        p.LineTo(40, 50);
        p.Close();
        c.DrawPath(p, new Paint { Color = 0x80FFFFFF, Style = PaintStyle.StrokeAndFill, StrokeWidth = 4 });
        int inside = Cov(s, 40, 28), edge = Cov(s, 40, 20);
        Assert(inside == edge && inside > 100 && inside < 160, $"one coat: inside {inside}, on the edge {edge}");
    }

    // ── Dashes ───────────────────────────────────────────────────────────

    /// <summary>The x extents of each contour of a dashed horizontal line.</summary>
    static List<(float, float)> Extents(Path p)
    {
        var list = new List<(float, float)>();
        var verbs = p.Verbs;
        var pts = p.Points;
        int pi = 0;
        float lo = 0, hi = 0;
        bool open = false;
        foreach (var v in verbs)
        {
            int n = Path.PointsIn(v);
            if (v == PathVerb.Move)
            {
                if (open) list.Add((lo, hi));
                lo = hi = pts[pi].X;
                open = true;
            }
            for (int k = 0; k < n; k++) { lo = MathF.Min(lo, pts[pi + k].X); hi = MathF.Max(hi, pts[pi + k].X); }
            pi += n;
        }
        if (open) list.Add((lo, hi));
        return list;
    }

    static void AssertExtents(List<(float, float)> got, (float, float)[] want, string what)
    {
        string Show(IEnumerable<(float, float)> e) => string.Join(" ", e.Select(x => $"[{x.Item1:G4},{x.Item2:G4}]"));
        Assert(got.Count == want.Length, $"{what}: {Show(got)}, want {Show(want)}");
        for (int i = 0; i < want.Length; i++)
            Assert(MathF.Abs(got[i].Item1 - want[i].Item1) < 1e-3f && MathF.Abs(got[i].Item2 - want[i].Item2) < 1e-3f,
                $"{what}: {Show(got)}, want {Show(want)}");
    }

    public static void DashIntervals()
    {
        var dst = new Path();
        var r = new Dasher().Apply(Line(0, 10, 50, 10), new DashPattern(10, 5), Stroke(2, LineCap.Round), 1, null, dst);
        Assert(r == DashResult.Path, $"{r}");
        AssertExtents(Extents(dst), [(0, 10), (15, 25), (30, 40), (45, 50)], "10/5");
    }

    public static void DashPhase()
    {
        var dst = new Path();
        new Dasher().Apply(Line(0, 10, 50, 10), new DashPattern(10, 5, 7), Stroke(2, LineCap.Round), 1, null, dst);
        AssertExtents(Extents(dst), [(0, 3), (8, 18), (23, 33), (38, 48)], "phase 7");
        dst.Rewind();
        // A negative phase counts back from the end of the pattern: −3 is 12.
        new Dasher().Apply(Line(0, 10, 50, 10), new DashPattern(10, 5, -3), Stroke(2, LineCap.Round), 1, null, dst);
        AssertExtents(Extents(dst), [(3, 13), (18, 28), (33, 43), (48, 50)], "phase −3");
        dst.Rewind();
        // A phase past the pattern wraps.
        new Dasher().Apply(Line(0, 10, 50, 10), new DashPattern(10, 5, 22), Stroke(2, LineCap.Round), 1, null, dst);
        AssertExtents(Extents(dst), [(0, 3), (8, 18), (23, 33), (38, 48)], "phase 22");
    }

    public static void ButtDashedLineIsQuadsToFill()
    {
        var dst = new Path();
        var r = new Dasher().Apply(Line(0, 10, 50, 10), new DashPattern(10, 5), Stroke(4), 1, null, dst);
        Assert(r == DashResult.Filled, $"{r}");
        AssertExtents(Extents(dst), [(0, 10), (15, 25), (30, 40), (45, 50)], "quads");
        Assert(dst.Bounds.Top == 8 && dst.Bounds.Bottom == 12, $"{dst.Bounds}");
    }

    static Path Square40(bool close = true)
    {
        var p = new Path();
        p.MoveTo(0, 0);
        p.LineTo(40, 0);
        p.LineTo(40, 40);
        p.LineTo(0, 40);
        if (close) p.Close();
        return p;
    }

    static int Contours(Path p)
    {
        int n = 0;
        foreach (var v in p.Verbs) if (v == PathVerb.Move) n++;
        return n;
    }

    public static void DashAcrossAContourStart()
    {
        // Perimeter 160. Phase 20 starts mid-dash: the first 10 is held back
        // and joined to the dash that runs over the start, so 140→160→0→10 is
        // one dash through the corner, not two.
        var dst = new Path();
        new Dasher().Apply(Square40(), new DashPattern(30, 10, 20), Stroke(2, LineCap.Round), 1, null, dst);
        Assert(Contours(dst) == 4, $"{Contours(dst)} contours");
        dst.TryGetLastPoint(out var last);
        Assert(last == new Vector2(10, 0), $"the last dash ends at {last}");
        bool throughCorner = false;
        foreach (var q in dst.Points) throughCorner |= q == Vector2.Zero;
        Assert(throughCorner, "the joined dash passes the start corner");

        // Ending in a gap, the held-back first dash is its own contour.
        dst.Rewind();
        new Dasher().Apply(Square40(), new DashPattern(30, 10), Stroke(2, LineCap.Round), 1, null, dst);
        Assert(Contours(dst) == 4, $"{Contours(dst)} contours");
        dst.TryGetLastPoint(out last);
        Assert(last == new Vector2(30, 0), $"the start dash ends at {last}");

        // Open, nothing is held back: 0..30 is the first dash.
        dst.Rewind();
        new Dasher().Apply(Square40(close: false), new DashPattern(30, 10), Stroke(2, LineCap.Round), 1, null, dst);
        Assert(Contours(dst) == 3 && dst.PointAt(0) == Vector2.Zero, $"open: {Contours(dst)} contours from {dst.PointAt(0)}");
    }

    public static void DashedStrokeDraws()
    {
        // A dashed 3 px rrect border: ons drawn, offs left alone.
        var (s, c) = Black8();
        c.DrawRoundRect(new Rect(10, 10, 70, 50), 6, 6, new Paint { Color = White, Style = PaintStyle.Stroke, StrokeWidth = 3, Dash = new DashPattern(6, 6) });
        // Along the top edge, past the corners: some columns lit, some dark.
        int lit = 0, dark = 0;
        for (int x = 20; x < 60; x++) { if (Cov(s, x, 10) > 200) lit++; else if (Cov(s, x, 10) < 30) dark++; }
        Assert(lit > 10 && dark > 10, $"lit {lit}, dark {dark}");
    }

    public static void DashedHairline()
    {
        // A dashed stroke a pixel wide is dashed, then drawn as a hairline.
        var (s, c) = Black8();
        c.DrawLine(0, 10.5f, 80, 10.5f, new Paint { Color = White, Style = PaintStyle.Stroke, StrokeWidth = 1, Dash = new DashPattern(4, 4) });
        Assert(Cov(s, 1, 10) > 200 && Cov(s, 5, 10) == 0 && Cov(s, 9, 10) > 200, $"{Cov(s, 1, 10)} {Cov(s, 5, 10)} {Cov(s, 9, 10)}");
    }

    public static void StrokesAreDeterministic()
    {
        string Frame()
        {
            var (s, c) = Black8();
            c.Antialiasing = Antialiasing.BaselineCompatible;
            var p = new Path();
            p.MoveTo(10, 50);
            p.CubicTo(20, 0, 70, 100, 75, 40);
            p.ArcTo(10, 10, 0, false, true, 60, 20);
            c.DrawPath(p, Stroke(3.3f, LineCap.Round, LineJoin.Round));
            c.DrawRoundRect(new Rect(5, 5, 70, 55), 8, 8, new Paint { Color = 0x80FFFFFF, Style = PaintStyle.Stroke, StrokeWidth = 2, Dash = new DashPattern(5, 3, 1) });
            var px = new uint[s.Width * s.Height];
            s.CopyTo(px);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan())));
        }
        Assert(Frame() == Frame(), "two runs, two hashes");
    }
}
