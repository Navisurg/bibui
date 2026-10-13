using System.Numerics;

namespace Bibui.Raster.Managed;

/// <summary>The verbs a <see cref="Path"/> stores. Points per verb: Move 1, Line 1, Quad 2, Conic 2 (+ a weight), Cubic 3, Close 0.</summary>
internal enum PathVerb : byte { Move, Line, Quad, Conic, Cubic, Close }

/// <summary>
/// A path: contours of lines, quadratics, conics and cubics, in local
/// coordinates. Curves are kept as curves and flattened only when drawn, under
/// the transform in force, so they stay smooth at any scale.
///
/// <para>Shapes and arcs are built point for point as Skia 2.88's
/// <c>SkPath</c> builds them (<see cref="AddRect"/>, <see cref="AddRRect"/>,
/// <see cref="AddOval"/>, <see cref="AddArc"/>, <see cref="ArcTo"/> start at
/// the same point, turn the same way and split into the same conics), because
/// a hairline's or a stroke's pixels depend on where its segments begin and
/// end, and the fixture baselines are drawn by the Skia backend. Reusing a
/// path with <see cref="Rewind"/> keeps its storage, so a per-frame path does
/// not allocate.</para>
/// </summary>
public sealed partial class Path // stroker helpers in Stroke/Path.Stroke.cs
{
    internal const float Root2Over2 = 0.707106781f;

    private PathVerb[] _verbs = new PathVerb[16];
    private Vector2[] _pts = new Vector2[32];
    private float[] _weights = new float[4];
    private int _verbCount, _ptCount, _weightCount;
    private int _lastMoveIndex = -1;
    private bool _needsMove = true;

    public FillRule FillRule { get; set; } = FillRule.NonZero;

    public bool IsEmpty => _verbCount == 0;

    internal ReadOnlySpan<PathVerb> Verbs => _verbs.AsSpan(0, _verbCount);
    internal ReadOnlySpan<Vector2> Points => _pts.AsSpan(0, _ptCount);
    internal ReadOnlySpan<float> Weights => _weights.AsSpan(0, _weightCount);

    public int VerbCount => _verbCount;
    public int PointCount => _ptCount;

    /// <summary>Empties the path and resets the fill rule, keeping its storage.</summary>
    public void Rewind()
    {
        _verbCount = _ptCount = _weightCount = 0;
        _lastMoveIndex = -1;
        _needsMove = true;
        FillRule = FillRule.NonZero;
    }

    /// <summary>The bounds of every point, control points included. Empty for an empty path.</summary>
    public Rect Bounds
    {
        get
        {
            if (_ptCount == 0) return default;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in Points)
            {
                if (p.X < x0) x0 = p.X;
                if (p.X > x1) x1 = p.X;
                if (p.Y < y0) y0 = p.Y;
                if (p.Y > y1) y1 = p.Y;
            }
            return new Rect(x0, y0, x1, y1);
        }
    }

    // ── Verbs ──────────────────────────────────────────────────────────────

    public void MoveTo(float x, float y) => MoveTo(new Vector2(x, y));

    public void MoveTo(Vector2 p)
    {
        // Consecutive moves collapse into the last.
        if (_verbCount > 0 && _verbs[_verbCount - 1] == PathVerb.Move)
        {
            _pts[_ptCount - 1] = p;
            return;
        }
        _lastMoveIndex = _ptCount;
        AddVerb(PathVerb.Move);
        AddPoint(p);
        _needsMove = false;
    }

    public void LineTo(float x, float y) => LineTo(new Vector2(x, y));

    public void LineTo(Vector2 p)
    {
        InjectMoveIfNeeded();
        AddVerb(PathVerb.Line);
        AddPoint(p);
    }

    public void QuadTo(float x1, float y1, float x2, float y2) => QuadTo(new Vector2(x1, y1), new Vector2(x2, y2));

    public void QuadTo(Vector2 p1, Vector2 p2)
    {
        InjectMoveIfNeeded();
        AddVerb(PathVerb.Quad);
        AddPoint(p1);
        AddPoint(p2);
    }

    /// <summary>A conic (rational quadratic) of weight <paramref name="w"/>: 1 is a quad, √2/2 over a square corner is a quarter circle.</summary>
    public void ConicTo(Vector2 p1, Vector2 p2, float w)
    {
        if (!(w > 0) || !float.IsFinite(w)) { LineTo(p2); return; }
        if (w == 1f) { QuadTo(p1, p2); return; }
        InjectMoveIfNeeded();
        AddVerb(PathVerb.Conic);
        AddPoint(p1);
        AddPoint(p2);
        if (_weightCount == _weights.Length) Array.Resize(ref _weights, _weights.Length * 2);
        _weights[_weightCount++] = w;
    }

    public void ConicTo(float x1, float y1, float x2, float y2, float w) => ConicTo(new Vector2(x1, y1), new Vector2(x2, y2), w);

    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
        => CubicTo(new Vector2(x1, y1), new Vector2(x2, y2), new Vector2(x3, y3));

    public void CubicTo(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        InjectMoveIfNeeded();
        AddVerb(PathVerb.Cubic);
        AddPoint(p1);
        AddPoint(p2);
        AddPoint(p3);
    }

    public void Close()
    {
        if (_verbCount == 0) return;
        var last = _verbs[_verbCount - 1];
        if (last != PathVerb.Close) AddVerb(PathVerb.Close);
        _needsMove = true;
    }

    // ── Shapes ─────────────────────────────────────────────────────────────

    /// <summary>A closed rect contour, clockwise on screen from the corner <paramref name="startIndex"/> (0 TL, 1 TR, 2 BR, 3 BL).</summary>
    public void AddRect(Rect r, bool clockwise = true, int startIndex = 0)
    {
        Span<Vector2> c = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
        int step = clockwise ? 1 : 3;
        int i = startIndex & 3;
        MoveTo(c[i]);
        for (int k = 0; k < 3; k++) { i = (i + step) & 3; LineTo(c[i]); }
        Close();
    }

    /// <summary>
    /// A closed ellipse inscribed in <paramref name="r"/>: four quarter conics
    /// starting at the right middle.
    /// </summary>
    public void AddOval(Rect r, bool clockwise = true)
    {
        r = r.Sorted();
        float cx = (r.Left + r.Right) * 0.5f, cy = (r.Top + r.Bottom) * 0.5f;
        Span<Vector2> ov = [new(cx, r.Top), new(r.Right, cy), new(cx, r.Bottom), new(r.Left, cy)];
        Span<Vector2> rc = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
        int oi = 1, ri = clockwise ? 1 : 2;
        int step = clockwise ? 1 : 3;
        MoveTo(ov[oi]);
        for (int k = 0; k < 4; k++)
        {
            oi = (oi + step) & 3;
            ri = (ri + step) & 3;
            ConicTo(rc[ri], ov[oi], Root2Over2);
        }
        Close();
    }

    public void AddCircle(float cx, float cy, float radius, bool clockwise = true)
        => AddOval(new Rect(cx - radius, cy - radius, cx + radius, cy + radius), clockwise);

    /// <summary>
    /// A closed rounded-rect contour, built clockwise from the bottom of the left side,
    /// up, then a line and a quarter conic per side. A rect or oval rrect is
    /// added as one.
    /// </summary>
    public void AddRRect(in RRect rr, bool clockwise = true)
    {
        var b = rr.Rect;
        if (rr.IsRect) { AddRect(b, clockwise, clockwise ? 3 : 0); return; }
        if (rr.IsOval) { AddOval(b, clockwise); return; }
        float rx = rr.Rx, ry = rr.Ry;
        // The eight rrect points, clockwise from the top-left's right end.
        Span<Vector2> p =
        [
            new(b.Left + rx, b.Top), new(b.Right - rx, b.Top),
            new(b.Right, b.Top + ry), new(b.Right, b.Bottom - ry),
            new(b.Right - rx, b.Bottom), new(b.Left + rx, b.Bottom),
            new(b.Left, b.Bottom - ry), new(b.Left, b.Top + ry),
        ];
        Span<Vector2> c = [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)];
        if (clockwise)
        {
            // Start index 6, CW: line to 7, conic over TL to 0, line to 1, ...
            MoveTo(p[6]);
            int pi = 6, ci = 3;
            for (int k = 0; k < 4; k++)
            {
                pi = (pi + 1) & 7;
                LineTo(p[pi]);
                ci = (ci + 1) & 3;
                pi = (pi + 1) & 7;
                ConicTo(c[ci], p[pi], Root2Over2);
            }
        }
        else
        {
            // Start index 7, CCW: line to 6, conic over BL to 5, ...
            MoveTo(p[7]);
            int pi = 7, ci = 0;
            for (int k = 0; k < 4; k++)
            {
                pi = (pi + 7) & 7;
                LineTo(p[pi]);
                ci = (ci + 3) & 3;
                pi = (pi + 7) & 7;
                ConicTo(c[ci], p[pi], Root2Over2);
            }
        }
        Close();
    }

    /// <summary>A polygon through <paramref name="points"/>, closed if <paramref name="close"/>.</summary>
    public void AddPolygon(ReadOnlySpan<Vector2> points, bool close = true)
    {
        if (points.IsEmpty) return;
        MoveTo(points[0]);
        for (int i = 1; i < points.Length; i++) LineTo(points[i]);
        if (close) Close();
    }

    /// <summary>
    /// An arc of the ellipse inscribed in <paramref name="oval"/> as a new
    /// contour. Angles in degrees, 0 along +x, positive clockwise on screen.
    /// A sweep of a full turn or more adds the whole oval. Built as one conic
    /// per quadrant, then the remainder.
    /// </summary>
    public void AddArc(Rect oval, float startDegrees, float sweepDegrees)
        => AddArcConics(oval, startDegrees, sweepDegrees); // the conic split a stroked arc's pixels depend on

    /// <summary>
    /// An SVG elliptical arc from the current point to (<paramref name="x"/>,
    /// <paramref name="y"/>). <paramref name="sweep"/> true goes the way of
    /// increasing angle — clockwise on a y-down screen. Built as conics of at
    /// most 120° each.
    /// </summary>
    public void ArcTo(float rx, float ry, float xAxisRotateDegrees, bool largeArc, bool sweep, float x, float y)
        => SvgArcToConics(rx, ry, xAxisRotateDegrees, largeArc, sweep, x, y); // the conics a stroked icon's pixels depend on

    /// <summary>Appends <paramref name="source"/>'s contours mapped through <paramref name="m"/>.</summary>
    public void AddPath(Path source, in Matrix3x2 m)
    {
        var verbs = source.Verbs;
        var pts = source.Points;
        var ws = source.Weights;
        int pi = 0, wi = 0;
        foreach (var v in verbs)
        {
            switch (v)
            {
                case PathVerb.Move: MoveTo(Vector2.Transform(pts[pi++], m)); break;
                case PathVerb.Line: LineTo(Vector2.Transform(pts[pi++], m)); break;
                case PathVerb.Quad: QuadTo(Vector2.Transform(pts[pi], m), Vector2.Transform(pts[pi + 1], m)); pi += 2; break;
                case PathVerb.Conic: ConicTo(Vector2.Transform(pts[pi], m), Vector2.Transform(pts[pi + 1], m), ws[wi++]); pi += 2; break;
                case PathVerb.Cubic:
                    CubicTo(Vector2.Transform(pts[pi], m), Vector2.Transform(pts[pi + 1], m), Vector2.Transform(pts[pi + 2], m));
                    pi += 3;
                    break;
                case PathVerb.Close: Close(); break;
            }
        }
    }

    /// <summary>Maps every point through <paramref name="m"/>, in place.</summary>
    public void Transform(in Matrix3x2 m)
    {
        for (int i = 0; i < _ptCount; i++) _pts[i] = Vector2.Transform(_pts[i], m);
    }

    // ── Storage ────────────────────────────────────────────────────────────

    private void InjectMoveIfNeeded()
    {
        if (!_needsMove) return;
        // After a close (or at the start), a drawing verb begins at the last
        // contour's start point.
        var p = _lastMoveIndex >= 0 ? _pts[_lastMoveIndex] : Vector2.Zero;
        MoveTo(p);
    }

    private void AddVerb(PathVerb v)
    {
        if (_verbCount == _verbs.Length) Array.Resize(ref _verbs, _verbs.Length * 2);
        _verbs[_verbCount++] = v;
    }

    private void AddPoint(Vector2 p)
    {
        if (_ptCount == _pts.Length) Array.Resize(ref _pts, _pts.Length * 2);
        _pts[_ptCount++] = p;
    }
}
