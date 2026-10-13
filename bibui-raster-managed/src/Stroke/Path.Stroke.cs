using System.Numerics;

namespace Bibui.Raster.Managed;

// The path operations the stroker and the dasher use, kept out of
// Path.cs so the core file stays the core's. Each reproduces the SkPath (m88)
// operation named on it, exactly — see the class's summary in Path.cs.
public sealed partial class Path
{
    /// <summary>The last point, if there is one (<c>SkPath::getLastPt</c>).</summary>
    internal bool TryGetLastPoint(out Vector2 p)
    {
        if (_ptCount == 0) { p = default; return false; }
        p = _pts[_ptCount - 1];
        return true;
    }

    /// <summary>Moves the last point, or starts the path there if it is empty (<c>SkPath::setLastPt</c>).</summary>
    internal void SetLastPoint(Vector2 p)
    {
        if (_ptCount == 0) MoveTo(p);
        else _pts[_ptCount - 1] = p;
    }

    internal Vector2 PointAt(int index) => _pts[index];

    /// <summary>True when every point from <paramref name="startPtIndex"/> on is the same (<c>SkPath::isZeroLengthSincePoint</c>).</summary>
    internal bool IsZeroLengthSincePoint(int startPtIndex)
    {
        int count = _ptCount - startPtIndex;
        if (count < 2) return true;
        var first = _pts[startPtIndex];
        for (int i = 1; i < count; i++)
            if (first != _pts[startPtIndex + i]) return false;
        return true;
    }

    /// <summary>
    /// Appends the last contour of <paramref name="src"/>, reversed, continuing
    /// from the current point (<c>SkPath::reversePathTo</c>): no move, no close.
    /// </summary>
    internal void ReversePathTo(Path src)
    {
        if (src._verbCount == 0) return;
        int pi = src._ptCount - 1;
        int wi = src._weightCount;
        for (int vi = src._verbCount - 1; vi >= 0; vi--)
        {
            var v = src._verbs[vi];
            pi -= PointsIn(v);
            switch (v)
            {
                case PathVerb.Move: return;
                case PathVerb.Line: LineTo(src._pts[pi]); break;
                case PathVerb.Quad: QuadTo(src._pts[pi + 1], src._pts[pi]); break;
                case PathVerb.Conic: ConicTo(src._pts[pi + 1], src._pts[pi], src._weights[--wi]); break;
                case PathVerb.Cubic: CubicTo(src._pts[pi + 2], src._pts[pi + 1], src._pts[pi]); break;
            }
        }
    }

    /// <summary>Appends every contour of <paramref name="src"/> reversed (<c>SkPath::reverseAddPath</c>).</summary>
    internal void ReverseAddPath(Path src)
    {
        int pi = src._ptCount;
        int wi = src._weightCount;
        bool needMove = true, needClose = false;
        for (int vi = src._verbCount - 1; vi >= 0; vi--)
        {
            var v = src._verbs[vi];
            int n = PointsIn(v);
            if (needMove)
            {
                --pi;
                MoveTo(src._pts[pi]);
                needMove = false;
            }
            pi -= n;
            switch (v)
            {
                case PathVerb.Move:
                    if (needClose) { Close(); needClose = false; }
                    needMove = true;
                    pi += 1;
                    break;
                case PathVerb.Line: LineTo(src._pts[pi]); break;
                case PathVerb.Quad: QuadTo(src._pts[pi + 1], src._pts[pi]); break;
                case PathVerb.Conic: ConicTo(src._pts[pi + 1], src._pts[pi], src._weights[--wi]); break;
                case PathVerb.Cubic: CubicTo(src._pts[pi + 2], src._pts[pi + 1], src._pts[pi]); break;
                case PathVerb.Close: needClose = true; break;
            }
        }
    }

    /// <summary>Appends <paramref name="src"/>'s verbs, points and weights as they are (<c>SkPath::addPath</c>, append mode).</summary>
    internal void AppendRaw(Path src)
    {
        for (int i = 0; i < src._verbCount; i++) AddVerb(src._verbs[i]);
        for (int i = 0; i < src._ptCount; i++) AddPoint(src._pts[i]);
        for (int i = 0; i < src._weightCount; i++)
        {
            if (_weightCount == _weights.Length) Array.Resize(ref _weights, _weights.Length * 2);
            _weights[_weightCount++] = src._weights[i];
        }
        // Where a following verb starts: after src's last contour.
        if (src._verbCount > 0)
        {
            int lastMove = -1, pc = 0;
            for (int i = 0; i < src._verbCount; i++)
            {
                if (src._verbs[i] == PathVerb.Move) lastMove = pc;
                pc += PointsIn(src._verbs[i]);
            }
            _lastMoveIndex = _ptCount - src._ptCount + lastMove;
            _needsMove = src._verbs[src._verbCount - 1] == PathVerb.Close;
        }
    }

    /// <summary>A polygon contour, moved to without collapsing into a previous move (<c>SkPath::addPoly</c>).</summary>
    internal void AddPoly(ReadOnlySpan<Vector2> pts, bool close)
    {
        if (pts.IsEmpty) return;
        _lastMoveIndex = _ptCount;
        AddVerb(PathVerb.Move);
        AddPoint(pts[0]);
        _needsMove = false;
        for (int i = 1; i < pts.Length; i++)
        {
            AddVerb(PathVerb.Line);
            AddPoint(pts[i]);
        }
        if (close) Close();
    }

    /// <summary>The path is one move and one line (<c>SkPath::isLine</c>).</summary>
    internal bool IsLine(out Vector2 a, out Vector2 b)
    {
        if (_verbCount == 2 && _verbs[0] == PathVerb.Move && _verbs[1] == PathVerb.Line)
        {
            a = _pts[0];
            b = _pts[1];
            return true;
        }
        a = b = default;
        return false;
    }

    /// <summary>Whether the last contour ends with a close.</summary>
    internal bool IsLastContourClosed => _verbCount > 0 && _verbs[_verbCount - 1] == PathVerb.Close;

    /// <summary>Only lines (and moves and closes): <c>getSegmentMasks() == kLine_SegmentMask</c>.</summary>
    internal bool HasOnlyLines
    {
        get
        {
            bool any = false;
            for (int i = 0; i < _verbCount; i++)
            {
                var v = _verbs[i];
                if (v is PathVerb.Quad or PathVerb.Conic or PathVerb.Cubic) return false;
                any |= v == PathVerb.Line;
            }
            return any;
        }
    }

    private static int RectMakeDir(float dx, float dy) => (dx != 0 ? 1 : 0) | ((dx > 0 || dy > 0) ? 2 : 0);

    /// <summary>
    /// Whether the path is one axis-aligned rectangle, its rect, whether it is
    /// closed and whether it turns clockwise (on a y-down screen)
    /// (<c>SkPathPriv::IsRectContour</c>, no partial).
    /// </summary>
    internal bool IsRect(out Rect rect, out bool isClosed, out bool clockwise)
    {
        rect = default;
        isClosed = clockwise = false;
        int corners = 0;
        Vector2 lineStart = default, firstCorner = default, thirdCorner = default;
        int firstPt = -1, lastPt = -1;
        Span<int> directions = [-1, -1, -1, -1, -1];
        bool closedOrMoved = false, autoClose = false;
        int pi = 0;
        for (int vi = 0; vi < _verbCount; vi++)
        {
            var verb = _verbs[vi];
            switch (verb)
            {
                case PathVerb.Close:
                case PathVerb.Line:
                {
                    if (verb == PathVerb.Close) autoClose = true;
                    else lastPt = pi;
                    var lineEnd = verb == PathVerb.Close ? _pts[firstPt] : _pts[pi++];
                    var d = lineEnd - lineStart;
                    if (d.X != 0 && d.Y != 0) return false;
                    if (!float.IsFinite(d.X) || !float.IsFinite(d.Y)) return false;
                    if (lineStart == lineEnd) break;
                    int next = RectMakeDir(d.X, d.Y);
                    if (corners == 0)
                    {
                        directions[0] = next;
                        corners = 1;
                        closedOrMoved = false;
                        lineStart = lineEnd;
                        break;
                    }
                    if (closedOrMoved) return false;
                    if (autoClose && next == directions[0]) break;
                    closedOrMoved = autoClose;
                    if (directions[corners - 1] == next)
                    {
                        if (corners == 3 && verb == PathVerb.Line) thirdCorner = lineEnd;
                        lineStart = lineEnd;
                        break;
                    }
                    directions[corners++] = next;
                    switch (corners)
                    {
                        case 2: firstCorner = lineStart; break;
                        case 3:
                            if ((directions[0] ^ directions[2]) != 2) return false;
                            thirdCorner = lineEnd;
                            break;
                        case 4:
                            if ((directions[1] ^ directions[3]) != 2) return false;
                            break;
                        default: return false;
                    }
                    lineStart = lineEnd;
                    break;
                }
                case PathVerb.Quad:
                case PathVerb.Conic:
                case PathVerb.Cubic:
                    return false;
                case PathVerb.Move:
                    if (corners == 0) firstPt = pi;
                    else
                    {
                        if (lastPt < 0) return false;
                        var c = _pts[firstPt] - _pts[lastPt];
                        if (c.X != 0 && c.Y != 0) return false;
                    }
                    lineStart = _pts[pi++];
                    closedOrMoved = true;
                    break;
            }
        }
        if (corners < 3 || corners > 4 || lastPt < 0) return false;
        var closeXY = _pts[firstPt] - _pts[lastPt];
        if (closeXY.X != 0 && closeXY.Y != 0) return false;
        rect = new Rect(
            MathF.Min(firstCorner.X, thirdCorner.X), MathF.Min(firstCorner.Y, thirdCorner.Y),
            MathF.Max(firstCorner.X, thirdCorner.X), MathF.Max(firstCorner.Y, thirdCorner.Y));
        isClosed = autoClose;
        clockwise = directions[0] == ((directions[1] + 1) & 3);
        return true;
    }

    /// <summary>The signed area of the first contour's polygon of points: positive turns clockwise on a y-down screen.</summary>
    internal float FirstContourSignedArea()
    {
        if (_ptCount == 0) return 0;
        int end = _ptCount, pc = 0;
        for (int vi = 0; vi < _verbCount; vi++)
        {
            if (vi > 0 && _verbs[vi] == PathVerb.Move) { end = pc; break; }
            pc += PointsIn(_verbs[vi]);
        }
        double sum = 0;
        for (int i = 0; i < end; i++)
        {
            var p = _pts[i];
            var q = _pts[(i + 1) % end];
            sum += (double)p.X * q.Y - (double)p.Y * q.X;
        }
        return (float)sum;
    }

    private static float SinSnap(float rad) { float v = MathF.Sin(rad); return MathF.Abs(v) <= CurveMath.NearlyZero ? 0 : v; }

    private static float CosSnap(float rad) { float v = MathF.Cos(rad); return MathF.Abs(v) <= CurveMath.NearlyZero ? 0 : v; }

    private const float DegToRad = MathF.PI / 180f;

    /// <summary>
    /// <c>SkPath::addArc</c> (m88): a sweep of a full turn starting on a
    /// quarter is an oval; otherwise conics from <see cref="ConicCurve.BuildUnitArc"/>,
    /// one per quadrant plus the remainder, after a move to the start.
    /// </summary>
    private void AddArcConics(Rect oval, float startAngle, float sweepAngle)
    {
        if (oval.IsEmpty || 0 == sweepAngle) return;
        if (sweepAngle >= 360f || sweepAngle <= -360f)
        {
            float startOver90 = startAngle / 90f;
            float startOver90I = MathF.Floor(startOver90 + 0.5f);
            if (MathF.Abs(startOver90 - startOver90I) <= CurveMath.NearlyZero)
            {
                float startIndex = (startOver90I + 1f) % 4f;
                if (startIndex < 0) startIndex += 4f;
                AddOvalAt(oval, sweepAngle > 0, (int)startIndex);
                return;
            }
        }
        ArcToConics(oval, startAngle, sweepAngle, forceMoveTo: true);
    }

    /// <summary><c>SkPath::addOval(oval, dir, startIndex)</c>: four quarter conics from the given quarter point (0 top, 1 right, 2 bottom, 3 left).</summary>
    private void AddOvalAt(Rect r, bool clockwise, int startIndex)
    {
        r = r.Sorted();
        float cx = (r.Left + r.Right) * 0.5f, cy = (r.Top + r.Bottom) * 0.5f;
        Span<Vector2> ov = [new(cx, r.Top), new(r.Right, cy), new(cx, r.Bottom), new(r.Left, cy)];
        Span<Vector2> rc = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
        int oi = startIndex & 3, ri = (startIndex + (clockwise ? 1 : 0)) & 3;
        int step = clockwise ? 1 : 3;
        MoveTo(ov[oi]);
        for (int k = 0; k < 4; k++)
        {
            oi = (oi + step) & 3;
            ConicTo(rc[ri], ov[oi], Root2Over2);
            ri = (ri + step) & 3;
        }
        Close();
    }

    /// <summary>An arc of the ellipse in <paramref name="oval"/> as conics, joined to the current contour by a line unless <paramref name="forceMoveTo"/> (<c>SkPath::arcTo(oval, start, sweep, forceMoveTo)</c>).</summary>
    private void ArcToConics(Rect oval, float startAngle, float sweepAngle, bool forceMoveTo)
    {
        if (oval.Width < 0 || oval.Height < 0) return;
        if (_verbCount == 0) forceMoveTo = true;
        float ocx = (oval.Left + oval.Right) * 0.5f, ocy = (oval.Top + oval.Bottom) * 0.5f;

        // arc_is_lone_point
        if (0 == sweepAngle && (0 == startAngle || 360f == startAngle)) { ArcAddPt(new Vector2(oval.Right, ocy), forceMoveTo); return; }
        if (0 == oval.Width && 0 == oval.Height) { ArcAddPt(new Vector2(oval.Right, oval.Top), forceMoveTo); return; }

        // angles_to_unit_vectors
        float startRad = startAngle * DegToRad, stopRad = (startAngle + sweepAngle) * DegToRad;
        var startV = new Vector2(CosSnap(startRad), SinSnap(startRad));
        var stopV = new Vector2(CosSnap(stopRad), SinSnap(stopRad));
        if (startV == stopV)
        {
            float sw = MathF.Abs(sweepAngle);
            if (sw < 360f && sw > 359f)
            {
                float deltaRad = MathF.CopySign(1f / 512, sweepAngle);
                do
                {
                    stopRad -= deltaRad;
                    stopV = new Vector2(CosSnap(stopRad), SinSnap(stopRad));
                } while (startV == stopV);
            }
        }
        bool cw = sweepAngle > 0;

        if (startV == stopV)
        {
            float endAngle = (startAngle + sweepAngle) * DegToRad;
            ArcAddPt(new Vector2(ocx + oval.Width / 2 * MathF.Cos(endAngle), ocy + oval.Height / 2 * MathF.Sin(endAngle)), forceMoveTo);
            return;
        }
        Span<ConicCurve> conics = stackalloc ConicCurve[ConicCurve.MaxConicsForArc];
        var scale = new Vector2(oval.Width * 0.5f, oval.Height * 0.5f);
        int count = ConicCurve.BuildUnitArc(startV, stopV, cw, scale, new Vector2(ocx, ocy), conics);
        if (count > 0)
        {
            ArcAddPt(conics[0].P0, forceMoveTo);
            for (int i = 0; i < count; i++) ConicTo(conics[i].P1, conics[i].P2, conics[i].W);
        }
        else ArcAddPt(new Vector2(stopV.X * scale.X + ocx, stopV.Y * scale.Y + ocy), forceMoveTo);
    }

    /// <summary>A move to the arc's start, or a line to it unless the path is already there.</summary>
    private void ArcAddPt(Vector2 pt, bool forceMoveTo)
    {
        if (forceMoveTo) MoveTo(pt);
        else if (!TryGetLastPoint(out var last) || MathF.Abs(last.X - pt.X) > CurveMath.NearlyZero || MathF.Abs(last.Y - pt.Y) > CurveMath.NearlyZero)
            LineTo(pt);
    }

    /// <summary>
    /// <c>SkPath::arcTo(rx, ry, angle, largeArc, sweep, x, y)</c> (m88): the
    /// SVG arc as conics of at most 120° each, ending exactly on (x, y).
    /// </summary>
    private void SvgArcToConics(float rx, float ry, float angle, bool largeArc, bool sweep, float x, float y)
    {
        InjectMoveIfNeeded();
        TryGetLastPoint(out var src0);
        if (rx == 0 || ry == 0) { LineTo(x, y); return; }
        var src1 = new Vector2(x, y);
        if (src0 == src1) { LineTo(x, y); return; }
        rx = MathF.Abs(rx);
        ry = MathF.Abs(ry);
        var midPointDistance = (src0 - src1) * 0.5f;

        // setRotate(-angle)
        float rad = -angle * DegToRad;
        float sn = SinSnap(rad), cs = CosSnap(rad);
        var tm = new Vector2(cs * midPointDistance.X - sn * midPointDistance.Y, sn * midPointDistance.X + cs * midPointDistance.Y);
        float squareRx = rx * rx, squareRy = ry * ry;
        float squareX = tm.X * tm.X, squareY = tm.Y * tm.Y;
        float radiiScale = squareX / squareRx + squareY / squareRy;
        if (radiiScale > 1)
        {
            radiiScale = MathF.Sqrt(radiiScale);
            rx *= radiiScale;
            ry *= radiiScale;
        }

        // setScale(1/rx, 1/ry).preRotate(-angle): rotate, then scale.
        float isx = 1 / rx, isy = 1 / ry;
        var u0 = new Vector2((cs * src0.X - sn * src0.Y) * isx, (sn * src0.X + cs * src0.Y) * isy);
        var u1 = new Vector2((cs * src1.X - sn * src1.Y) * isx, (sn * src1.X + cs * src1.Y) * isy);
        var delta = u1 - u0;
        float d = delta.X * delta.X + delta.Y * delta.Y;
        float scaleFactorSquared = MathF.Max(1 / d - 0.25f, 0f);
        float scaleFactor = MathF.Sqrt(scaleFactorSquared);
        if (!sweep != largeArc) scaleFactor = -scaleFactor;
        delta *= scaleFactor;
        var centerPoint = (u0 + u1) * 0.5f;
        centerPoint += new Vector2(-delta.Y, delta.X);
        u0 -= centerPoint;
        u1 -= centerPoint;
        float theta1 = MathF.Atan2(u0.Y, u0.X);
        float theta2 = MathF.Atan2(u1.Y, u1.X);
        float thetaArc = theta2 - theta1;
        if (thetaArc < 0 && sweep) thetaArc += MathF.PI * 2;
        else if (thetaArc > 0 && !sweep) thetaArc -= MathF.PI * 2;
        if (MathF.Abs(thetaArc) < MathF.PI / (1000 * 1000)) { LineTo(x, y); return; }

        // setRotate(angle).preScale(rx, ry): scale, then rotate.
        float rad2 = angle * DegToRad;
        float sn2 = SinSnap(rad2), cs2 = CosSnap(rad2);

        int segments = (int)MathF.Ceiling(MathF.Abs(thetaArc / (2 * MathF.PI / 3)));
        float thetaWidth = thetaArc / segments;
        float t = MathF.Tan(0.5f * thetaWidth);
        if (!float.IsFinite(t)) return;
        float startTheta = theta1;
        float w = MathF.Sqrt(0.5f + MathF.Cos(thetaWidth) * 0.5f);
        bool expectIntegers = MathF.Abs(MathF.PI / 2 - MathF.Abs(thetaWidth)) <= CurveMath.NearlyZero
                              && IsWhole(rx) && IsWhole(ry) && IsWhole(x) && IsWhole(y);
        for (int i = 0; i < segments; ++i)
        {
            float endTheta = startTheta + thetaWidth;
            float sinEnd = SinSnap(endTheta), cosEnd = CosSnap(endTheta);
            var e = new Vector2(cosEnd, sinEnd) + centerPoint;
            var c = e + new Vector2(t * sinEnd, -t * cosEnd);
            var m0 = new Vector2(cs2 * (c.X * rx) - sn2 * (c.Y * ry), sn2 * (c.X * rx) + cs2 * (c.Y * ry));
            var m1 = new Vector2(cs2 * (e.X * rx) - sn2 * (e.Y * ry), sn2 * (e.X * rx) + cs2 * (e.Y * ry));
            if (expectIntegers)
            {
                m0 = new Vector2(MathF.Floor(m0.X + 0.5f), MathF.Floor(m0.Y + 0.5f));
                m1 = new Vector2(MathF.Floor(m1.X + 0.5f), MathF.Floor(m1.Y + 0.5f));
            }
            ConicTo(m0, m1, w);
            startTheta = endTheta;
        }
        SetLastPoint(new Vector2(x, y));
    }

    private static bool IsWhole(float v) => v == MathF.Floor(v);

    internal static int PointsIn(PathVerb v) => v switch
    {
        PathVerb.Move or PathVerb.Line => 1,
        PathVerb.Quad or PathVerb.Conic => 2,
        PathVerb.Cubic => 3,
        _ => 0,
    };
}

/// <summary>
/// Walks a path (as <c>SkPath::Iter</c>, not force-closed): each
/// segment comes with its start point, a close that does not end where its
/// contour began is preceded by the line that closes it, and a trailing move
/// is dropped.
/// </summary>
internal struct PathIter
{
    private readonly Path _path;
    private int _vi, _pi, _wi;
    private Vector2 _moveTo, _lastPt;
    private bool _afterMove;

    public PathIter(Path path)
    {
        _path = path;
        _vi = _pi = _wi = 0;
        _moveTo = _lastPt = default;
        _afterMove = false;
    }

    public float ConicWeight { get; private set; }

    /// <summary>The next verb with its points in <paramref name="pts"/> (start point first); false when done.</summary>
    public bool Next(Span<Vector2> pts, out PathVerb verb)
    {
        var verbs = _path.Verbs;
        var src = _path.Points;
        verb = PathVerb.Close;
        if (_vi >= verbs.Length) return false;
        var v = verbs[_vi++];
        switch (v)
        {
            case PathVerb.Move:
                if (_vi == verbs.Length) return false; // trailing move
                _moveTo = src[_pi++];
                pts[0] = _moveTo;
                _lastPt = _moveTo;
                _afterMove = true;
                break;
            case PathVerb.Line:
                pts[0] = ConsMoveTo();
                pts[1] = src[_pi++];
                _lastPt = pts[1];
                break;
            case PathVerb.Quad:
            case PathVerb.Conic:
                pts[0] = ConsMoveTo();
                pts[1] = src[_pi];
                pts[2] = src[_pi + 1];
                _pi += 2;
                _lastPt = pts[2];
                if (v == PathVerb.Conic) ConicWeight = _path.Weights[_wi++];
                break;
            case PathVerb.Cubic:
                pts[0] = ConsMoveTo();
                pts[1] = src[_pi];
                pts[2] = src[_pi + 1];
                pts[3] = src[_pi + 2];
                _pi += 3;
                _lastPt = pts[3];
                break;
            case PathVerb.Close:
                if (_lastPt != _moveTo && !(float.IsNaN(_lastPt.X) || float.IsNaN(_lastPt.Y) || float.IsNaN(_moveTo.X) || float.IsNaN(_moveTo.Y)))
                {
                    pts[0] = _lastPt;
                    pts[1] = _moveTo;
                    _lastPt = _moveTo;
                    _vi--; // the close comes next
                    verb = PathVerb.Line;
                    return true;
                }
                pts[0] = _moveTo;
                _lastPt = _moveTo;
                break;
        }
        verb = v;
        return true;
    }

    private Vector2 ConsMoveTo()
    {
        if (_afterMove)
        {
            _afterMove = false;
            return _moveTo;
        }
        return _lastPt;
    }
}
