using System.Numerics;
using static Bibui.Raster.Managed.CurveMath;

namespace Bibui.Raster.Managed;

/// <summary>
/// The canvas's stroker: turns a path and a stroke into the outline
/// that fills it — butt, round and square caps; miter (with its limit), round
/// and bevel joins; closed and open contours; zero-length segments (a dot
/// under a round or square cap) — and curves offset to within a quarter of a
/// device pixel.
///
/// <para>Curves are offset by approximating each side with quads that pass an
/// error test; <c>resScale</c> (the transform's scale) tightens the tolerance
/// as the path is magnified.</para>
///
/// <para>A port of Skia m88's <c>SkStroke.cpp</c> and <c>SkStrokerPriv.cpp</c>
/// (the Skia under SkiaSharp 2.88): the same decisions, in the same order, in
/// single precision, so the outline has the Skia backend's points and takes
/// the same scan converter, and strokes match the fixture baselines it drew.
/// The Sk names on members map the port.</para>
///
/// <para>Dashes are not applied here: the canvas dashes the path first
/// (<see cref="Dasher"/>).
/// An instance keeps scratch paths, so it is used by one canvas at a time.</para>
/// </summary>
public sealed class PathStroker : IPathStroker
{
    private readonly StrokerCore _core = new();

    public void Stroke(Path source, in Paint paint, float resScale, Path destination)
    {
        float width = paint.StrokeWidth;
        float radius = width * 0.5f;
        if (!(radius > 0)) return;
        float miter = paint.MiterLimit > 0 ? paint.MiterLimit : 4f;
        bool doFill = paint.Style == PaintStyle.StrokeAndFill;
        var cap = paint.Cap;
        var join = paint.Join;

        // A closed rect is stroked as whole rects.
        if (source.IsRect(out var rect, out bool closed, out bool cw) && closed)
        {
            StrokeRect(rect, cw, radius, width, join, miter, doFill, destination);
            return;
        }

        _core.Run(source, radius, miter, cap, join, resScale, destination);

        if (doFill)
        {
            if (source.FirstContourSignedArea() < 0) destination.ReverseAddPath(source);
            else destination.AppendRaw(source);
        }
    }

    /// <summary>Strokes a closed rect as whole rects (<c>SkStroke::strokeRect</c>).</summary>
    private static void StrokeRect(Rect rect, bool clockwise, float radius, float width, LineJoin join, float miter, bool doFill, Path dst)
    {
        float rw = rect.Width, rh = rect.Height;
        var r = rect.Outset(radius, radius);
        if (join == LineJoin.Miter && miter < 1.41421356f) join = LineJoin.Bevel;
        switch (join)
        {
            case LineJoin.Miter:
                dst.AddRect(r, clockwise);
                break;
            case LineJoin.Bevel:
            {
                Span<Vector2> pts = stackalloc Vector2[8];
                if (clockwise)
                {
                    pts[0] = new(rect.Left, r.Top);
                    pts[1] = new(rect.Right, r.Top);
                    pts[2] = new(r.Right, rect.Top);
                    pts[3] = new(r.Right, rect.Bottom);
                    pts[4] = new(rect.Right, r.Bottom);
                    pts[5] = new(rect.Left, r.Bottom);
                    pts[6] = new(r.Left, rect.Bottom);
                    pts[7] = new(r.Left, rect.Top);
                }
                else
                {
                    pts[7] = new(rect.Left, r.Top);
                    pts[6] = new(rect.Right, r.Top);
                    pts[5] = new(r.Right, rect.Top);
                    pts[4] = new(r.Right, rect.Bottom);
                    pts[3] = new(rect.Right, r.Bottom);
                    pts[2] = new(rect.Left, r.Bottom);
                    pts[1] = new(r.Left, rect.Bottom);
                    pts[0] = new(r.Left, rect.Top);
                }
                dst.AddPoly(pts, close: true);
                break;
            }
            case LineJoin.Round:
                dst.AddRRect(new RRect(r, radius, radius), clockwise);
                break;
        }
        if (width < MathF.Min(rw, rh) && !doFill)
        {
            var inner = rect.Outset(-radius, -radius);
            // addRect(dir) starts at the top left whichever way it turns.
            dst.AddRect(inner, !clockwise, 0);
        }
    }
}

/// <summary>One stroke of one path, outer side and inner side (<c>SkPathStroker</c>).</summary>
internal sealed class StrokerCore
{
    private const int TangentRecursiveLimit = 0, CubicRecursiveLimit = 1, ConicRecursiveLimit = 2, QuadRecursiveLimit = 3;
    private static readonly int[] RecursiveLimits = [5 * 3, 24, 11 * 3, 11 * 3];

    private enum ResultType { Split, Degenerate, Quad }

    private enum ReductionType { Point, Line, Quad, Degenerate, Degenerate2, Degenerate3 }

    private enum RayType { CtrlPt, ResultType }

    /// <summary>The quad under construction parallel to a piece of the curve (<c>SkQuadConstruct</c>).</summary>
    private struct QuadConstruct
    {
        public Vector2 Q0, Q1, Q2;
        public Vector2 TangentStart, TangentEnd;
        public float StartT, MidT, EndT;
        public bool StartSet, EndSet, OppositeTangents;

        public bool Init(float start, float end)
        {
            StartT = start;
            MidT = (start + end) * 0.5f;
            EndT = end;
            StartSet = EndSet = false;
            return StartT < MidT && MidT < EndT;
        }

        public bool InitWithStart(in QuadConstruct parent)
        {
            if (!Init(parent.StartT, parent.MidT)) return false;
            Q0 = parent.Q0;
            TangentStart = parent.TangentStart;
            StartSet = true;
            return true;
        }

        public bool InitWithEnd(in QuadConstruct parent)
        {
            if (!Init(parent.MidT, parent.EndT)) return false;
            Q2 = parent.Q2;
            TangentEnd = parent.TangentEnd;
            EndSet = true;
            return true;
        }
    }

    private float _radius, _invMiterLimit, _resScale, _invResScale, _invResScaleSquared;
    private Vector2 _firstNormal, _prevNormal, _firstUnitNormal, _prevUnitNormal;
    private Vector2 _firstPt, _prevPt, _firstOuterPt;
    private int _firstOuterPtIndexInContour, _segmentCount;
    private bool _prevIsLine;
    private LineCap _cap;
    private LineJoin _join;
    private Path _outer = new(), _inner = new();
    private readonly Path _cusper = new();
    private int _strokeType; // +1 outer, −1 inner
    private int _recursionDepth;
    private bool _foundTangents, _joinCompleted;

    private Path StrokePath => _strokeType == 1 ? _outer : _inner;

    /// <summary>Strokes <paramref name="src"/> into <paramref name="dst"/>, for every path but a closed rect (<c>SkStroke::strokePath</c>).</summary>
    public void Run(Path src, float radius, float miterLimit, LineCap cap, LineJoin join, float resScale, Path dst)
    {
        _radius = radius;
        _resScale = resScale;
        _invMiterLimit = 0;
        if (join == LineJoin.Miter)
        {
            if (miterLimit <= 1) join = LineJoin.Bevel;
            else _invMiterLimit = 1f / miterLimit;
        }
        _cap = cap;
        _join = join;
        _segmentCount = -1;
        _firstOuterPtIndexInContour = 0;
        _prevIsLine = false;
        _invResScale = 1f / (resScale * 4);
        _invResScaleSquared = _invResScale * _invResScale;
        _recursionDepth = 0;
        _outer.Rewind();
        _inner.Rewind();
        _cusper.Rewind();

        var iter = new PathIter(src);
        Span<Vector2> pts = stackalloc Vector2[4];
        var lastSegment = PathVerb.Move;
        while (iter.Next(pts, out var verb))
        {
            switch (verb)
            {
                case PathVerb.Move:
                    MoveTo(pts[0]);
                    break;
                case PathVerb.Line:
                    LineTo(pts[1], iter);
                    lastSegment = PathVerb.Line;
                    break;
                case PathVerb.Quad:
                    QuadTo(pts[1], pts[2]);
                    lastSegment = PathVerb.Quad;
                    break;
                case PathVerb.Conic:
                    ConicTo(pts[1], pts[2], iter.ConicWeight);
                    lastSegment = PathVerb.Conic;
                    break;
                case PathVerb.Cubic:
                    CubicTo(pts[1], pts[2], pts[3]);
                    lastSegment = PathVerb.Cubic;
                    break;
                case PathVerb.Close:
                    if (cap != LineCap.Butt)
                    {
                        // A move then a close, or a contour of zero-length
                        // verbs: a zero-length line, which caps as a dot.
                        if (_segmentCount == 0)
                        {
                            LineTo(_firstPt, null);
                            lastSegment = PathVerb.Line;
                            break;
                        }
                        if (IsCurrentContourEmpty)
                        {
                            lastSegment = PathVerb.Line;
                            break;
                        }
                    }
                    FinishContour(true, lastSegment == PathVerb.Line);
                    break;
            }
        }
        FinishContour(false, lastSegment == PathVerb.Line);
        dst.AppendRaw(_outer);
    }

    private bool IsCurrentContourEmpty
        => _inner.IsZeroLengthSincePoint(0) && _outer.IsZeroLengthSincePoint(_firstOuterPtIndexInContour);

    // ── Joins and caps (SkStrokerPriv.cpp) ─────────────────────────────────

    private static bool IsClockwise(Vector2 before, Vector2 after) => before.X * after.Y > before.Y * after.X;

    private enum AngleType { Nearly180, Sharp, Shallow, NearlyLine }

    private static AngleType Dot2AngleType(float dot)
    {
        if (dot >= 0) return IsNearlyZero(1 - dot) ? AngleType.NearlyLine : AngleType.Shallow;
        return IsNearlyZero(1 + dot) ? AngleType.Nearly180 : AngleType.Sharp;
    }

    private static void HandleInnerJoin(Path inner, Vector2 pivot, Vector2 after)
    {
        // Through the pivot, so a radius larger than the segments does not show a diagonal.
        inner.LineTo(pivot);
        inner.LineTo(pivot - after);
    }

    private void Join(LineJoin join, Vector2 beforeUnitNormal, Vector2 pivot, Vector2 afterUnitNormal, bool prevIsLine, bool currIsLine)
    {
        var outer = _outer;
        var inner = _inner;
        switch (join)
        {
            case LineJoin.Bevel:
            {
                var after = afterUnitNormal * _radius;
                if (!IsClockwise(beforeUnitNormal, afterUnitNormal))
                {
                    (outer, inner) = (inner, outer);
                    after = -after;
                }
                outer.LineTo(pivot + after);
                HandleInnerJoin(inner, pivot, after);
                return;
            }
            case LineJoin.Round:
            {
                float dotProd = Dot(beforeUnitNormal, afterUnitNormal);
                if (Dot2AngleType(dotProd) == AngleType.NearlyLine) return;
                var before = beforeUnitNormal;
                var after = afterUnitNormal;
                bool cw = true;
                if (!IsClockwise(before, after))
                {
                    (outer, inner) = (inner, outer);
                    before = -before;
                    after = -after;
                    cw = false;
                }
                Span<ConicCurve> conics = stackalloc ConicCurve[ConicCurve.MaxConicsForArc];
                int count = ConicCurve.BuildUnitArc(before, after, cw, new Vector2(_radius, _radius), pivot, conics);
                if (count > 0)
                {
                    for (int i = 0; i < count; i++) outer.ConicTo(conics[i].P1, conics[i].P2, conics[i].W);
                    HandleInnerJoin(inner, pivot, after * _radius);
                }
                return;
            }
            default:
                MiterJoin(outer, inner, beforeUnitNormal, pivot, afterUnitNormal, prevIsLine, currIsLine);
                return;
        }
    }

    private void MiterJoin(Path outer, Path inner, Vector2 beforeUnitNormal, Vector2 pivot, Vector2 afterUnitNormal, bool prevIsLine, bool currIsLine)
    {
        const float OneOverSqrt2 = 0.707106781f;
        float dotProd = Dot(beforeUnitNormal, afterUnitNormal);
        var angleType = Dot2AngleType(dotProd);
        var before = beforeUnitNormal;
        var after = afterUnitNormal;
        Vector2 mid;

        if (angleType == AngleType.NearlyLine) return;
        if (angleType == AngleType.Nearly180)
        {
            currIsLine = false;
            goto DoBlunt;
        }

        bool ccw = !IsClockwise(before, after);
        if (ccw)
        {
            (outer, inner) = (inner, outer);
            before = -before;
            after = -after;
        }

        // An upright right angle (stroked rects): exact and cheap.
        if (0 == dotProd && _invMiterLimit <= OneOverSqrt2)
        {
            mid = (before + after) * _radius;
            goto DoMiter;
        }

        float sinHalfAngle = MathF.Sqrt(0.5f * (1 + dotProd));
        if (sinHalfAngle < _invMiterLimit)
        {
            currIsLine = false;
            goto DoBlunt;
        }

        if (angleType == AngleType.Sharp)
        {
            mid = new Vector2(after.Y - before.Y, before.X - after.X);
            if (ccw) mid = -mid;
        }
        else mid = new Vector2(before.X + after.X, before.Y + after.Y);
        SetLength(ref mid, _radius / sinHalfAngle);

    DoMiter:
        if (prevIsLine) outer.SetLastPoint(pivot + mid);
        else outer.LineTo(pivot + mid);

    DoBlunt:
        after *= _radius;
        if (!currIsLine) outer.LineTo(pivot + after);
        HandleInnerJoin(inner, pivot, after);
    }

    private void Cap(Path path, Vector2 pivot, Vector2 normal, Vector2 stop, Path? otherPath)
    {
        switch (_cap)
        {
            case LineCap.Butt:
                path.LineTo(stop);
                break;
            case LineCap.Round:
            {
                var parallel = RotateCW(normal);
                var projectedCenter = pivot + parallel;
                path.ConicTo(projectedCenter + normal, projectedCenter, Path.Root2Over2);
                path.ConicTo(projectedCenter - normal, stop, Path.Root2Over2);
                break;
            }
            default:
            {
                var parallel = RotateCW(normal);
                if (otherPath is not null)
                {
                    path.SetLastPoint(new Vector2(pivot.X + normal.X + parallel.X, pivot.Y + normal.Y + parallel.Y));
                    path.LineTo(new Vector2(pivot.X - normal.X + parallel.X, pivot.Y - normal.Y + parallel.Y));
                }
                else
                {
                    path.LineTo(new Vector2(pivot.X + normal.X + parallel.X, pivot.Y + normal.Y + parallel.Y));
                    path.LineTo(new Vector2(pivot.X - normal.X + parallel.X, pivot.Y - normal.Y + parallel.Y));
                    path.LineTo(stop);
                }
                break;
            }
        }
    }

    // ── Contours ───────────────────────────────────────────────────────────

    private bool SetNormalUnitNormal(Vector2 before, Vector2 after, float scale, out Vector2 normal, out Vector2 unitNormal)
    {
        unitNormal = default;
        if (!SetLength(ref unitNormal, (after.X - before.X) * scale, (after.Y - before.Y) * scale, 1f))
        {
            normal = default;
            return false;
        }
        unitNormal = RotateCCW(unitNormal);
        normal = unitNormal * _radius;
        return true;
    }

    private bool SetNormalUnitNormal(Vector2 vec, out Vector2 normal, out Vector2 unitNormal)
    {
        unitNormal = default;
        if (!SetLength(ref unitNormal, vec.X, vec.Y, 1f))
        {
            normal = default;
            return false;
        }
        unitNormal = RotateCCW(unitNormal);
        normal = unitNormal * _radius;
        return true;
    }

    private bool PreJoinTo(Vector2 currPt, out Vector2 normal, out Vector2 unitNormal, bool currIsLine)
    {
        float prevX = _prevPt.X, prevY = _prevPt.Y;
        if (!SetNormalUnitNormal(_prevPt, currPt, _resScale, out normal, out unitNormal))
        {
            if (_cap == LineCap.Butt) return false;
            // A zero-length segment has no direction: cap it upright.
            normal = new Vector2(_radius, 0);
            unitNormal = new Vector2(1, 0);
        }
        if (_segmentCount == 0)
        {
            _firstNormal = normal;
            _firstUnitNormal = unitNormal;
            _firstOuterPt = new Vector2(prevX + normal.X, prevY + normal.Y);
            _outer.MoveTo(_firstOuterPt);
            _inner.MoveTo(new Vector2(prevX - normal.X, prevY - normal.Y));
        }
        else
        {
            Join(_join, _prevUnitNormal, _prevPt, unitNormal, _prevIsLine, currIsLine);
        }
        _prevIsLine = currIsLine;
        return true;
    }

    private void PostJoinTo(Vector2 currPt, Vector2 normal, Vector2 unitNormal)
    {
        _joinCompleted = true;
        _prevPt = currPt;
        _prevUnitNormal = unitNormal;
        _prevNormal = normal;
        _segmentCount += 1;
    }

    private void FinishContour(bool close, bool currIsLine)
    {
        if (_segmentCount > 0)
        {
            if (close)
            {
                Join(_join, _prevUnitNormal, _prevPt, _firstUnitNormal, _prevIsLine, currIsLine);
                _outer.Close();
                // The inner side as its own contour.
                _inner.TryGetLastPoint(out var pt);
                _outer.MoveTo(pt);
                _outer.ReversePathTo(_inner);
                _outer.Close();
            }
            else
            {
                _inner.TryGetLastPoint(out var pt);
                Cap(_outer, _prevPt, _prevNormal, pt, currIsLine ? _inner : null);
                _outer.ReversePathTo(_inner);
                Cap(_outer, _firstPt, -_firstNormal, _firstOuterPt, _prevIsLine ? _inner : null);
                _outer.Close();
            }
            if (!_cusper.IsEmpty)
            {
                _outer.AppendRaw(_cusper);
                _cusper.Rewind();
            }
        }
        _inner.Rewind();
        _segmentCount = -1;
        _firstOuterPtIndexInContour = _outer.PointCount;
    }

    private void MoveTo(Vector2 pt)
    {
        if (_segmentCount > 0) FinishContour(false, false);
        _segmentCount = 0;
        _firstPt = _prevPt = pt;
        _joinCompleted = false;
    }

    private void LineTo(Vector2 currPt, PathIter? iter)
    {
        var d = _prevPt - currPt;
        float tol = NearlyZero * _invResScale;
        bool teenyLine = IsNearlyZero(d.X, tol) && IsNearlyZero(d.Y, tol);
        if (_cap == LineCap.Butt && teenyLine) return;
        if (teenyLine && (_joinCompleted || (iter is { } it && HasValidTangent(it)))) return;
        if (!PreJoinTo(currPt, out var normal, out var unitNormal, true)) return;
        _outer.LineTo(currPt + normal);
        _inner.LineTo(currPt - normal);
        PostJoinTo(currPt, normal, unitNormal);
    }

    /// <summary>Whether a later segment of the contour has a direction (<c>has_valid_tangent</c>).</summary>
    private static bool HasValidTangent(PathIter copy)
    {
        Span<Vector2> pts = stackalloc Vector2[4];
        while (copy.Next(pts, out var verb))
        {
            switch (verb)
            {
                case PathVerb.Move: return false;
                case PathVerb.Line:
                    if (pts[0] == pts[1]) continue;
                    return true;
                case PathVerb.Quad:
                case PathVerb.Conic:
                    if (pts[0] == pts[1] && pts[0] == pts[2]) continue;
                    return true;
                case PathVerb.Cubic:
                    if (pts[0] == pts[1] && pts[0] == pts[2] && pts[0] == pts[3]) continue;
                    return true;
                case PathVerb.Close: return false;
            }
        }
        return false;
    }

    // ── Linearity checks ───────────────────────────────────────────────────

    private static bool DegenerateVector(Vector2 v) => !CanNormalize(v.X, v.Y);

    /// <summary>Squared distance from a point to a segment (<c>pt_to_line</c>).</summary>
    private static float PtToLine(Vector2 pt, Vector2 lineStart, Vector2 lineEnd)
    {
        var dxy = lineEnd - lineStart;
        var ab0 = pt - lineStart;
        float numer = Dot(dxy, ab0);
        float denom = Dot(dxy, dxy);
        float t = numer / denom;
        if (t >= 0 && t <= 1)
        {
            var hit = new Vector2(lineStart.X * (1 - t) + lineEnd.X * t, lineStart.Y * (1 - t) + lineEnd.Y * t);
            return DistanceToSqd(hit, pt);
        }
        return DistanceToSqd(pt, lineStart);
    }

    private static bool CubicInLine(ReadOnlySpan<Vector2> cubic)
    {
        float ptMax = -1;
        int outer1 = 0, outer2 = 0;
        for (int index = 0; index < 3; ++index)
            for (int inner = index + 1; inner < 4; ++inner)
            {
                var d = cubic[inner] - cubic[index];
                float testMax = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
                if (ptMax < testMax)
                {
                    outer1 = index;
                    outer2 = inner;
                    ptMax = testMax;
                }
            }
        int mid1 = (1 + (2 >> outer2)) >> outer1;
        int mid2 = outer1 ^ outer2 ^ mid1;
        float lineSlop = ptMax * ptMax * 0.00001f;
        return PtToLine(cubic[mid1], cubic[outer1], cubic[outer2]) <= lineSlop
               && PtToLine(cubic[mid2], cubic[outer1], cubic[outer2]) <= lineSlop;
    }

    private static bool QuadInLine(ReadOnlySpan<Vector2> quad)
    {
        float ptMax = -1;
        int outer1 = 0, outer2 = 0;
        for (int index = 0; index < 2; ++index)
            for (int inner = index + 1; inner < 3; ++inner)
            {
                var d = quad[inner] - quad[index];
                float testMax = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
                if (ptMax < testMax)
                {
                    outer1 = index;
                    outer2 = inner;
                    ptMax = testMax;
                }
            }
        int mid = outer1 ^ outer2 ^ 3;
        const float CurvatureSlop = 0.000005f;
        float lineSlop = ptMax * ptMax * CurvatureSlop;
        return PtToLine(quad[mid], quad[outer1], quad[outer2]) <= lineSlop;
    }

    private static ReductionType CheckCubicLinear(ReadOnlySpan<Vector2> cubic, Span<Vector2> reduction, out int tangentPt)
    {
        tangentPt = 1;
        bool degenerateAB = DegenerateVector(cubic[1] - cubic[0]);
        bool degenerateBC = DegenerateVector(cubic[2] - cubic[1]);
        bool degenerateCD = DegenerateVector(cubic[3] - cubic[2]);
        if (degenerateAB & degenerateBC & degenerateCD) return ReductionType.Point;
        if ((degenerateAB ? 1 : 0) + (degenerateBC ? 1 : 0) + (degenerateCD ? 1 : 0) == 2) return ReductionType.Line;
        if (!CubicInLine(cubic))
        {
            tangentPt = degenerateAB ? 2 : 1;
            return ReductionType.Quad;
        }
        Span<float> tValues = stackalloc float[3];
        int count = FindCubicMaxCurvature(cubic, tValues);
        int rCount = 0;
        for (int i = 0; i < count; ++i)
        {
            float t = tValues[i];
            if (0 >= t || t >= 1) continue;
            reduction[rCount] = EvalCubicAt(cubic, t);
            if (reduction[rCount] != cubic[0] && reduction[rCount] != cubic[3]) ++rCount;
        }
        if (rCount == 0) return ReductionType.Line;
        return (ReductionType)((int)ReductionType.Quad + rCount);
    }

    private static ReductionType CheckConicLinear(in ConicCurve conic, out Vector2 reduction)
    {
        reduction = default;
        bool degenerateAB = DegenerateVector(conic.P1 - conic.P0);
        bool degenerateBC = DegenerateVector(conic.P2 - conic.P1);
        if (degenerateAB & degenerateBC) return ReductionType.Point;
        if (degenerateAB | degenerateBC) return ReductionType.Line;
        ReadOnlySpan<Vector2> q = [conic.P0, conic.P1, conic.P2];
        if (!QuadInLine(q)) return ReductionType.Quad;
        float t = FindQuadMaxCurvature(q);
        if (0 == t) return ReductionType.Line;
        reduction = conic.EvalAt(t);
        return ReductionType.Degenerate;
    }

    private static ReductionType CheckQuadLinear(ReadOnlySpan<Vector2> quad, out Vector2 reduction)
    {
        reduction = default;
        bool degenerateAB = DegenerateVector(quad[1] - quad[0]);
        bool degenerateBC = DegenerateVector(quad[2] - quad[1]);
        if (degenerateAB & degenerateBC) return ReductionType.Point;
        if (degenerateAB | degenerateBC) return ReductionType.Line;
        if (!QuadInLine(quad)) return ReductionType.Quad;
        float t = FindQuadMaxCurvature(quad);
        if (0 == t || 1 == t) return ReductionType.Line;
        reduction = EvalQuadAt(quad, t);
        return ReductionType.Degenerate;
    }

    // ── Curves ─────────────────────────────────────────────────────────────

    private void LineToWithRoundJoin(Vector2 pt)
    {
        var save = _join;
        _join = LineJoin.Round;
        LineTo(pt, null);
        _join = save;
    }

    private void ConicTo(Vector2 pt1, Vector2 pt2, float weight)
    {
        var conic = new ConicCurve(_prevPt, pt1, pt2, weight);
        var reductionType = CheckConicLinear(conic, out var reduction);
        if (reductionType is ReductionType.Point or ReductionType.Line)
        {
            LineTo(pt2, null);
            return;
        }
        if (reductionType == ReductionType.Degenerate)
        {
            LineTo(reduction, null);
            LineToWithRoundJoin(pt2);
            return;
        }
        if (!PreJoinTo(pt1, out var normalAB, out var unitAB, false))
        {
            LineTo(pt2, null);
            return;
        }
        var quadPts = default(QuadConstruct);
        Init(1, ref quadPts, 0, 1);
        ConicStroke(conic, ref quadPts);
        Init(-1, ref quadPts, 0, 1);
        ConicStroke(conic, ref quadPts);
        SetQuadEndNormal(conic.P1, conic.P2, normalAB, unitAB, out var normalBC, out var unitBC);
        PostJoinTo(pt2, normalBC, unitBC);
    }

    private void QuadTo(Vector2 pt1, Vector2 pt2)
    {
        ReadOnlySpan<Vector2> quad = [_prevPt, pt1, pt2];
        var reductionType = CheckQuadLinear(quad, out var reduction);
        if (reductionType is ReductionType.Point or ReductionType.Line)
        {
            LineTo(pt2, null);
            return;
        }
        if (reductionType == ReductionType.Degenerate)
        {
            LineTo(reduction, null);
            LineToWithRoundJoin(pt2);
            return;
        }
        if (!PreJoinTo(pt1, out var normalAB, out var unitAB, false))
        {
            LineTo(pt2, null);
            return;
        }
        var quadPts = default(QuadConstruct);
        Init(1, ref quadPts, 0, 1);
        QuadStroke(quad, ref quadPts);
        Init(-1, ref quadPts, 0, 1);
        QuadStroke(quad, ref quadPts);
        SetQuadEndNormal(quad[1], quad[2], normalAB, unitAB, out var normalBC, out var unitBC);
        PostJoinTo(pt2, normalBC, unitBC);
    }

    private void CubicTo(Vector2 pt1, Vector2 pt2, Vector2 pt3)
    {
        ReadOnlySpan<Vector2> cubic = [_prevPt, pt1, pt2, pt3];
        Span<Vector2> reduction = stackalloc Vector2[3];
        var reductionType = CheckCubicLinear(cubic, reduction, out int tangentPt);
        if (reductionType is ReductionType.Point or ReductionType.Line)
        {
            LineTo(pt3, null);
            return;
        }
        if (reductionType >= ReductionType.Degenerate)
        {
            LineTo(reduction[0], null);
            var save = _join;
            _join = LineJoin.Round;
            if (reductionType >= ReductionType.Degenerate2) LineTo(reduction[1], null);
            if (reductionType == ReductionType.Degenerate3) LineTo(reduction[2], null);
            LineTo(pt3, null);
            _join = save;
            return;
        }
        if (!PreJoinTo(cubic[tangentPt], out var normalAB, out var unitAB, false))
        {
            LineTo(pt3, null);
            return;
        }
        Span<float> tValues = stackalloc float[2];
        int count = FindCubicInflections(cubic, tValues);
        float lastT = 0;
        for (int i = 0; i <= count; ++i)
        {
            float nextT = i < count ? tValues[i] : 1;
            var quadPts = default(QuadConstruct);
            Init(1, ref quadPts, lastT, nextT);
            CubicStroke(cubic, ref quadPts);
            Init(-1, ref quadPts, lastT, nextT);
            CubicStroke(cubic, ref quadPts);
            lastT = nextT;
        }
        float cusp = FindCubicCusp(cubic);
        if (cusp > 0)
        {
            var cuspLoc = EvalCubicAt(cubic, cusp);
            _cusper.AddCircle(cuspLoc.X, cuspLoc.Y, _radius);
        }
        SetCubicEndNormal(cubic, normalAB, unitAB, out var normalCD, out var unitCD);
        PostJoinTo(pt3, normalCD, unitCD);
    }

    private void SetQuadEndNormal(Vector2 q1, Vector2 q2, Vector2 normalAB, Vector2 unitNormalAB, out Vector2 normalBC, out Vector2 unitNormalBC)
    {
        if (!SetNormalUnitNormal(q1, q2, _resScale, out normalBC, out unitNormalBC))
        {
            normalBC = normalAB;
            unitNormalBC = unitNormalAB;
        }
    }

    private void SetCubicEndNormal(ReadOnlySpan<Vector2> cubic, Vector2 normalAB, Vector2 unitNormalAB, out Vector2 normalCD, out Vector2 unitNormalCD)
    {
        var ab = cubic[1] - cubic[0];
        var cd = cubic[3] - cubic[2];
        bool degenerateAB = DegenerateVector(ab);
        bool degenerateCD = DegenerateVector(cd);
        if (!(degenerateAB && degenerateCD))
        {
            if (degenerateAB)
            {
                ab = cubic[2] - cubic[0];
                degenerateAB = DegenerateVector(ab);
            }
            if (degenerateCD)
            {
                cd = cubic[3] - cubic[1];
                degenerateCD = DegenerateVector(cd);
            }
            if (!(degenerateAB || degenerateCD))
            {
                SetNormalUnitNormal(cd, out normalCD, out unitNormalCD);
                return;
            }
        }
        normalCD = normalAB;
        unitNormalCD = unitNormalAB;
    }

    private void Init(int strokeType, ref QuadConstruct quadPts, float tStart, float tEnd)
    {
        _strokeType = strokeType;
        _foundTangents = false;
        quadPts.Init(tStart, tEnd);
    }

    // ── Perpendicular rays ─────────────────────────────────────────────────

    private void SetRayPts(Vector2 tPt, Vector2 dxy, out Vector2 onPt, out Vector2 tangent)
    {
        if (!SetLength(ref dxy, _radius)) dxy = new Vector2(_radius, 0);
        float axisFlip = _strokeType;
        onPt = new Vector2(tPt.X + axisFlip * dxy.Y, tPt.Y - axisFlip * dxy.X);
        tangent = new Vector2(onPt.X + dxy.X, onPt.Y + dxy.Y);
    }

    private void ConicPerpRay(in ConicCurve conic, float t, out Vector2 tPt, out Vector2 onPt, out Vector2 tangent)
    {
        tPt = conic.EvalAt(t);
        var dxy = conic.EvalTangentAt(t);
        if (dxy.X == 0 && dxy.Y == 0) dxy = conic.P2 - conic.P0;
        SetRayPts(tPt, dxy, out onPt, out tangent);
    }

    private void ConicQuadEnds(in ConicCurve conic, ref QuadConstruct q)
    {
        if (!q.StartSet)
        {
            ConicPerpRay(conic, q.StartT, out _, out q.Q0, out q.TangentStart);
            q.StartSet = true;
        }
        if (!q.EndSet)
        {
            ConicPerpRay(conic, q.EndT, out _, out q.Q2, out q.TangentEnd);
            q.EndSet = true;
        }
    }

    private void CubicPerpRay(ReadOnlySpan<Vector2> cubic, float t, out Vector2 tPt, out Vector2 onPt, out Vector2 tangent)
    {
        tPt = EvalCubicAt(cubic, t);
        var dxy = EvalCubicTangentAt(cubic, t);
        if (dxy.X == 0 && dxy.Y == 0)
        {
            Span<Vector2> chopped = stackalloc Vector2[7];
            scoped ReadOnlySpan<Vector2> cPts = cubic;
            if (IsNearlyZero(t)) dxy = cubic[2] - cubic[0];
            else if (IsNearlyZero(1 - t)) dxy = cubic[3] - cubic[1];
            else
            {
                // A cusp at the inflection: the tangent from the chopped halves.
                ChopCubicAt(cubic, chopped, t);
                dxy = chopped[3] - chopped[2];
                if (dxy.X == 0 && dxy.Y == 0)
                {
                    dxy = chopped[3] - chopped[1];
                    cPts = chopped;
                }
            }
            if (dxy.X == 0 && dxy.Y == 0) dxy = cPts[3] - cPts[0];
        }
        SetRayPts(tPt, dxy, out onPt, out tangent);
    }

    private void CubicQuadEnds(ReadOnlySpan<Vector2> cubic, ref QuadConstruct q)
    {
        if (!q.StartSet)
        {
            CubicPerpRay(cubic, q.StartT, out _, out q.Q0, out q.TangentStart);
            q.StartSet = true;
        }
        if (!q.EndSet)
        {
            CubicPerpRay(cubic, q.EndT, out _, out q.Q2, out q.TangentEnd);
            q.EndSet = true;
        }
    }

    private void QuadPerpRay(ReadOnlySpan<Vector2> quad, float t, out Vector2 tPt, out Vector2 onPt, out Vector2 tangent)
    {
        tPt = EvalQuadAt(quad, t);
        var dxy = EvalQuadTangentAt(quad, t);
        if (dxy.X == 0 && dxy.Y == 0) dxy = quad[2] - quad[0];
        SetRayPts(tPt, dxy, out onPt, out tangent);
    }

    // ── Fitting a quad to the offset ───────────────────────────────────────

    private ResultType IntersectRay(ref QuadConstruct q, RayType rayType)
    {
        var start = q.Q0;
        var end = q.Q2;
        var aLen = q.TangentStart - start;
        var bLen = q.TangentEnd - end;
        float denom = Cross(aLen, bLen);
        if (denom == 0 || !float.IsFinite(denom))
        {
            q.OppositeTangents = Dot(aLen, bLen) < 0;
            return ResultType.Degenerate;
        }
        q.OppositeTangents = false;
        var ab0 = start - end;
        float numerA = Cross(bLen, ab0);
        float numerB = Cross(aLen, ab0);
        if ((numerA >= 0) == (numerB >= 0))
        {
            // The control point would be outside the ends: a line if the ends are near the tangents.
            float dist1 = PtToLine(start, end, q.TangentEnd);
            float dist2 = PtToLine(end, start, q.TangentStart);
            if (MathF.Max(dist1, dist2) <= _invResScaleSquared) return ResultType.Degenerate;
            return ResultType.Split;
        }
        numerA /= denom;
        bool validDivide = numerA > numerA - 1;
        if (validDivide)
        {
            if (rayType == RayType.CtrlPt)
                q.Q1 = new Vector2(start.X * (1 - numerA) + q.TangentStart.X * numerA,
                                   start.Y * (1 - numerA) + q.TangentStart.Y * numerA);
            return ResultType.Quad;
        }
        q.OppositeTangents = Dot(aLen, bLen) < 0;
        return ResultType.Degenerate;
    }

    private ResultType TangentsMeet(ReadOnlySpan<Vector2> cubic, ref QuadConstruct q)
    {
        CubicQuadEnds(cubic, ref q);
        return IntersectRay(ref q, RayType.ResultType);
    }

    private static int IntersectQuadRay(Vector2 line0, Vector2 line1, ReadOnlySpan<Vector2> quad, Span<float> roots)
    {
        var vec = line1 - line0;
        float r0 = (quad[0].Y - line0.Y) * vec.X - (quad[0].X - line0.X) * vec.Y;
        float r1 = (quad[1].Y - line0.Y) * vec.X - (quad[1].X - line0.X) * vec.Y;
        float r2 = (quad[2].Y - line0.Y) * vec.X - (quad[2].X - line0.X) * vec.Y;
        float a = r2, b = r1, c = r0;
        a += c - 2 * b;
        b -= c;
        return FindUnitQuadRoots(a, 2 * b, c, roots);
    }

    private bool PtInQuadBounds(ReadOnlySpan<Vector2> quad, Vector2 pt)
    {
        float xMin = MathF.Min(MathF.Min(quad[0].X, quad[1].X), quad[2].X);
        if (pt.X + _invResScale < xMin) return false;
        float xMax = MathF.Max(MathF.Max(quad[0].X, quad[1].X), quad[2].X);
        if (pt.X - _invResScale > xMax) return false;
        float yMin = MathF.Min(MathF.Min(quad[0].Y, quad[1].Y), quad[2].Y);
        if (pt.Y + _invResScale < yMin) return false;
        float yMax = MathF.Max(MathF.Max(quad[0].Y, quad[1].Y), quad[2].Y);
        if (pt.Y - _invResScale > yMax) return false;
        return true;
    }

    private static bool PointsWithinDist(Vector2 nearPt, Vector2 farPt, float limit) => DistanceToSqd(nearPt, farPt) <= limit * limit;

    private static bool SharpAngle(ReadOnlySpan<Vector2> quad)
    {
        var smaller = quad[1] - quad[0];
        var larger = quad[1] - quad[2];
        float smallerLen = LengthSqd(smaller);
        float largerLen = LengthSqd(larger);
        if (smallerLen > largerLen)
        {
            (smaller, larger) = (larger, smaller);
            largerLen = smallerLen;
        }
        if (!SetLength(ref smaller, largerLen)) return false;
        return Dot(smaller, larger) > 0;
    }

    private ResultType StrokeCloseEnough(ReadOnlySpan<Vector2> stroke, Vector2 ray0, Vector2 ray1, in QuadConstruct q)
    {
        ReadOnlySpan<Vector2> quad = [q.Q0, q.Q1, q.Q2];
        var strokeMid = EvalQuadAt(stroke, 0.5f);
        if (PointsWithinDist(ray0, strokeMid, _invResScale))
            return SharpAngle(quad) ? ResultType.Split : ResultType.Quad;
        if (!PtInQuadBounds(stroke, ray0)) return ResultType.Split;
        Span<float> roots = stackalloc float[2];
        int rootCount = IntersectQuadRay(ray0, ray1, stroke, roots);
        if (rootCount != 1) return ResultType.Split;
        var quadPt = EvalQuadAt(stroke, roots[0]);
        float error = _invResScale * (1 - MathF.Abs(roots[0] - 0.5f) * 2);
        if (PointsWithinDist(ray0, quadPt, error))
            return SharpAngle(quad) ? ResultType.Split : ResultType.Quad;
        return ResultType.Split;
    }

    private ResultType CompareQuadCubic(ReadOnlySpan<Vector2> cubic, ref QuadConstruct q)
    {
        CubicQuadEnds(cubic, ref q);
        var resultType = IntersectRay(ref q, RayType.CtrlPt);
        if (resultType != ResultType.Quad) return resultType;
        CubicPerpRay(cubic, q.MidT, out var ray1, out var ray0, out _);
        ReadOnlySpan<Vector2> stroke = [q.Q0, q.Q1, q.Q2];
        return StrokeCloseEnough(stroke, ray0, ray1, q);
    }

    private ResultType CompareQuadConic(in ConicCurve conic, ref QuadConstruct q)
    {
        ConicQuadEnds(conic, ref q);
        var resultType = IntersectRay(ref q, RayType.CtrlPt);
        if (resultType != ResultType.Quad) return resultType;
        ConicPerpRay(conic, q.MidT, out var ray1, out var ray0, out _);
        ReadOnlySpan<Vector2> stroke = [q.Q0, q.Q1, q.Q2];
        return StrokeCloseEnough(stroke, ray0, ray1, q);
    }

    private ResultType CompareQuadQuad(ReadOnlySpan<Vector2> quad, ref QuadConstruct q)
    {
        if (!q.StartSet)
        {
            QuadPerpRay(quad, q.StartT, out _, out q.Q0, out q.TangentStart);
            q.StartSet = true;
        }
        if (!q.EndSet)
        {
            QuadPerpRay(quad, q.EndT, out _, out q.Q2, out q.TangentEnd);
            q.EndSet = true;
        }
        var resultType = IntersectRay(ref q, RayType.CtrlPt);
        if (resultType != ResultType.Quad) return resultType;
        QuadPerpRay(quad, q.MidT, out var ray1, out var ray0, out _);
        ReadOnlySpan<Vector2> stroke = [q.Q0, q.Q1, q.Q2];
        return StrokeCloseEnough(stroke, ray0, ray1, q);
    }

    private void AddDegenerateLine(in QuadConstruct q) => StrokePath.LineTo(q.Q2);

    private bool CubicMidOnLine(ReadOnlySpan<Vector2> cubic, in QuadConstruct q)
    {
        CubicPerpRay(cubic, q.MidT, out _, out var strokeMid, out _);
        float dist = PtToLine(strokeMid, q.Q0, q.Q2);
        return dist < _invResScaleSquared;
    }

    private bool CubicStroke(ReadOnlySpan<Vector2> cubic, ref QuadConstruct q)
    {
        if (!_foundTangents)
        {
            var resultType = TangentsMeet(cubic, ref q);
            if (resultType != ResultType.Quad)
            {
                if ((resultType == ResultType.Degenerate || PointsWithinDist(q.Q0, q.Q2, _invResScale))
                    && CubicMidOnLine(cubic, q))
                {
                    AddDegenerateLine(q);
                    return true;
                }
            }
            else _foundTangents = true;
        }
        if (_foundTangents)
        {
            var resultType = CompareQuadCubic(cubic, ref q);
            if (resultType == ResultType.Quad)
            {
                StrokePath.QuadTo(q.Q1, q.Q2);
                return true;
            }
            if (resultType == ResultType.Degenerate && !q.OppositeTangents)
            {
                AddDegenerateLine(q);
                return true;
            }
        }
        if (!float.IsFinite(q.Q2.X) || !float.IsFinite(q.Q2.Y)) return false;
        if (++_recursionDepth > RecursiveLimits[_foundTangents ? CubicRecursiveLimit : TangentRecursiveLimit]) return false;
        var half = default(QuadConstruct);
        if (!half.InitWithStart(q))
        {
            AddDegenerateLine(q);
            --_recursionDepth;
            return true;
        }
        if (!CubicStroke(cubic, ref half)) return false;
        if (!half.InitWithEnd(q))
        {
            AddDegenerateLine(q);
            --_recursionDepth;
            return true;
        }
        if (!CubicStroke(cubic, ref half)) return false;
        --_recursionDepth;
        return true;
    }

    private bool ConicStroke(in ConicCurve conic, ref QuadConstruct q)
    {
        var resultType = CompareQuadConic(conic, ref q);
        if (resultType == ResultType.Quad)
        {
            StrokePath.QuadTo(q.Q1, q.Q2);
            return true;
        }
        if (resultType == ResultType.Degenerate)
        {
            AddDegenerateLine(q);
            return true;
        }
        if (++_recursionDepth > RecursiveLimits[ConicRecursiveLimit]) return false;
        var half = default(QuadConstruct);
        half.InitWithStart(q);
        if (!ConicStroke(conic, ref half)) return false;
        half.InitWithEnd(q);
        if (!ConicStroke(conic, ref half)) return false;
        --_recursionDepth;
        return true;
    }

    private bool QuadStroke(ReadOnlySpan<Vector2> quad, ref QuadConstruct q)
    {
        var resultType = CompareQuadQuad(quad, ref q);
        if (resultType == ResultType.Quad)
        {
            StrokePath.QuadTo(q.Q1, q.Q2);
            return true;
        }
        if (resultType == ResultType.Degenerate)
        {
            AddDegenerateLine(q);
            return true;
        }
        if (++_recursionDepth > RecursiveLimits[QuadRecursiveLimit]) return false;
        var half = default(QuadConstruct);
        half.InitWithStart(q);
        if (!QuadStroke(quad, ref half)) return false;
        half.InitWithEnd(q);
        if (!QuadStroke(quad, ref half)) return false;
        --_recursionDepth;
        return true;
    }
}
