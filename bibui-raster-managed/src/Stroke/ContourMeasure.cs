using System.Numerics;
using static Bibui.Raster.Managed.CurveMath;

namespace Bibui.Raster.Managed;

/// <summary>
/// Lengths along a path's contours, and the pieces between two distances,
/// which the dasher walks. Curves are measured by chords, subdivided until a
/// chord is within ½ / <c>resScale</c> of the curve.
///
/// <para>A port of Skia m88's <c>SkContourMeasure</c> / <c>SkPathMeasure</c>
/// (the Sk names on members map it), so a dash breaks exactly where the Skia
/// backend's does and dashed borders match the fixture baselines it drew.</para>
/// </summary>
internal sealed class ContourMeasure
{
    private enum SegType : byte { Line, Quad, Cubic, Conic }

    private struct Segment
    {
        public float Distance;
        public int PtIndex;
        public int TValue;
        public SegType Type;
    }

    private const int MaxTValue = 0x3FFFFFFF;

    private static float TValueToScalar(int t) => t * (1.0f / MaxTValue);

    private Segment[] _segs = new Segment[16];
    private int _segCount;
    private Vector2[] _pts = new Vector2[16];
    private int _ptCount;

    private Path _path = null!;
    private int _vi, _pi, _wi;
    private float _tolerance;
    private Vector2 _lastPoint;

    public float Length { get; private set; }
    public bool IsClosed { get; private set; }

    /// <summary>Starts measuring <paramref name="path"/> and moves to its first contour of non-zero length (<c>SkPathMeasure</c>'s constructor).</summary>
    public void Reset(Path path, float resScale)
    {
        _path = path;
        _vi = _pi = _wi = 0;
        _tolerance = 0.5f * (1f / resScale);
        Length = 0;
        IsClosed = false;
        _segCount = 0;
        NextContour();
    }

    /// <summary>Moves to the next contour of non-zero length; false when there is none (<c>SkPathMeasure::nextContour</c>).</summary>
    public bool NextContour()
    {
        while (_vi < _path.VerbCount)
        {
            if (BuildSegments()) return true;
        }
        Length = 0;
        IsClosed = false;
        _segCount = 0;
        return false;
    }

    private void AddSeg(float distance, int ptIndex, SegType type, int tValue)
    {
        if (_segCount == _segs.Length) Array.Resize(ref _segs, _segs.Length * 2);
        _segs[_segCount++] = new Segment { Distance = distance, PtIndex = ptIndex, Type = type, TValue = tValue };
    }

    private void AddPt(Vector2 p)
    {
        if (_ptCount == _pts.Length) Array.Resize(ref _pts, _pts.Length * 2);
        _pts[_ptCount++] = p;
    }

    private static bool CheapDistExceedsLimit(Vector2 pt, float x, float y, float tolerance)
        => MathF.Max(MathF.Abs(x - pt.X), MathF.Abs(y - pt.Y)) > tolerance;

    private float ComputeLineSeg(Vector2 p0, Vector2 p1, float distance, int ptIndex)
    {
        float d = Distance(p0, p1);
        float prevD = distance;
        distance += d;
        if (distance > prevD) AddSeg(distance, ptIndex, SegType.Line, MaxTValue);
        return distance;
    }

    private float ComputeQuadSegs(ReadOnlySpan<Vector2> pts, float distance, int mint, int maxt, int ptIndex)
    {
        float dx = pts[1].X * 0.5f - (pts[0].X + pts[2].X) * 0.5f * 0.5f;
        float dy = pts[1].Y * 0.5f - (pts[0].Y + pts[2].Y) * 0.5f * 0.5f;
        bool tooCurvy = MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) > _tolerance;
        if (((maxt - mint) >> 10) != 0 && tooCurvy)
        {
            Span<Vector2> tmp = stackalloc Vector2[5];
            int halft = (mint + maxt) >> 1;
            ChopQuadAt(pts, tmp, 0.5f);
            distance = ComputeQuadSegs(tmp[..3], distance, mint, halft, ptIndex);
            distance = ComputeQuadSegs(tmp[2..5], distance, halft, maxt, ptIndex);
        }
        else
        {
            float d = Distance(pts[0], pts[2]);
            float prevD = distance;
            distance += d;
            if (distance > prevD) AddSeg(distance, ptIndex, SegType.Quad, maxt);
        }
        return distance;
    }

    private float ComputeConicSegs(in ConicCurve conic, float distance, int mint, Vector2 minPt, int maxt, Vector2 maxPt, int ptIndex)
    {
        int halft = (mint + maxt) >> 1;
        var halfPt = conic.EvalAt(TValueToScalar(halft));
        if (!float.IsFinite(halfPt.X) || !float.IsFinite(halfPt.Y)) return distance;
        var midEnds = (minPt + maxPt) * 0.5f;
        var dxy = halfPt - midEnds;
        bool tooCurvy = MathF.Max(MathF.Abs(dxy.X), MathF.Abs(dxy.Y)) > _tolerance;
        if (((maxt - mint) >> 10) != 0 && tooCurvy)
        {
            distance = ComputeConicSegs(conic, distance, mint, minPt, halft, halfPt, ptIndex);
            distance = ComputeConicSegs(conic, distance, halft, halfPt, maxt, maxPt, ptIndex);
        }
        else
        {
            float d = Distance(minPt, maxPt);
            float prevD = distance;
            distance += d;
            if (distance > prevD) AddSeg(distance, ptIndex, SegType.Conic, maxt);
        }
        return distance;
    }

    private float ComputeCubicSegs(ReadOnlySpan<Vector2> pts, float distance, int mint, int maxt, int ptIndex)
    {
        bool tooCurvy =
            CheapDistExceedsLimit(pts[1], pts[0].X + (pts[3].X - pts[0].X) * (1f / 3), pts[0].Y + (pts[3].Y - pts[0].Y) * (1f / 3), _tolerance)
            || CheapDistExceedsLimit(pts[2], pts[0].X + (pts[3].X - pts[0].X) * (2f / 3), pts[0].Y + (pts[3].Y - pts[0].Y) * (2f / 3), _tolerance);
        if (((maxt - mint) >> 10) != 0 && tooCurvy)
        {
            Span<Vector2> tmp = stackalloc Vector2[7];
            int halft = (mint + maxt) >> 1;
            ChopCubicAt(pts, tmp, 0.5f);
            distance = ComputeCubicSegs(tmp[..4], distance, mint, halft, ptIndex);
            distance = ComputeCubicSegs(tmp[3..7], distance, halft, maxt, ptIndex);
        }
        else
        {
            float d = Distance(pts[0], pts[3]);
            float prevD = distance;
            distance += d;
            if (distance > prevD) AddSeg(distance, ptIndex, SegType.Cubic, maxt);
        }
        return distance;
    }

    /// <summary><c>SkContourMeasureIter::Impl::buildSegments</c>: one contour; false if it has no length.</summary>
    private bool BuildSegments()
    {
        int ptIndex = -1;
        float distance = 0;
        bool haveSeenClose = false, haveSeenMoveTo = false;
        _segCount = 0;
        _ptCount = 0;
        var verbs = _path.Verbs;
        var src = _path.Points;
        var ws = _path.Weights;
        Span<Vector2> p = stackalloc Vector2[4];

        while (_vi < verbs.Length)
        {
            var verb = verbs[_vi];
            if (haveSeenMoveTo && verb == PathVerb.Move) break;
            _vi++;
            switch (verb)
            {
                case PathVerb.Move:
                    ptIndex += 1;
                    _lastPoint = src[_pi++];
                    AddPt(_lastPoint);
                    haveSeenMoveTo = true;
                    break;
                case PathVerb.Line:
                {
                    var p1 = src[_pi++];
                    float prevD = distance;
                    distance = ComputeLineSeg(_lastPoint, p1, distance, ptIndex);
                    if (distance > prevD)
                    {
                        AddPt(p1);
                        ptIndex++;
                    }
                    _lastPoint = p1;
                    break;
                }
                case PathVerb.Quad:
                {
                    p[0] = _lastPoint; p[1] = src[_pi]; p[2] = src[_pi + 1];
                    _pi += 2;
                    float prevD = distance;
                    distance = ComputeQuadSegs(p[..3], distance, 0, MaxTValue, ptIndex);
                    if (distance > prevD)
                    {
                        AddPt(p[1]);
                        AddPt(p[2]);
                        ptIndex += 2;
                    }
                    _lastPoint = p[2];
                    break;
                }
                case PathVerb.Conic:
                {
                    var conic = new ConicCurve(_lastPoint, src[_pi], src[_pi + 1], ws[_wi++]);
                    _pi += 2;
                    float prevD = distance;
                    distance = ComputeConicSegs(conic, distance, 0, conic.P0, MaxTValue, conic.P2, ptIndex);
                    if (distance > prevD)
                    {
                        // The weight rides in the next point, then the two conic points.
                        AddPt(new Vector2(conic.W, 0));
                        AddPt(conic.P1);
                        AddPt(conic.P2);
                        ptIndex += 3;
                    }
                    _lastPoint = conic.P2;
                    break;
                }
                case PathVerb.Cubic:
                {
                    p[0] = _lastPoint; p[1] = src[_pi]; p[2] = src[_pi + 1]; p[3] = src[_pi + 2];
                    _pi += 3;
                    float prevD = distance;
                    distance = ComputeCubicSegs(p, distance, 0, MaxTValue, ptIndex);
                    if (distance > prevD)
                    {
                        AddPt(p[1]);
                        AddPt(p[2]);
                        AddPt(p[3]);
                        ptIndex += 3;
                    }
                    _lastPoint = p[3];
                    break;
                }
                case PathVerb.Close:
                    haveSeenClose = true;
                    break;
            }
        }

        if (!float.IsFinite(distance) || _segCount == 0) return false;
        if (haveSeenClose)
        {
            float prevD = distance;
            var firstPt = _pts[0];
            distance = ComputeLineSeg(_pts[ptIndex], firstPt, distance, ptIndex);
            if (distance > prevD) AddPt(firstPt);
        }
        Length = distance;
        IsClosed = haveSeenClose;
        return true;
    }

    private int DistanceToSegment(float distance, out float t)
    {
        // SkTKSearch: the first segment whose distance is not below the key.
        int lo = 0, hi = _segCount - 1;
        while (lo < hi)
        {
            int mid = (hi + lo) >> 1;
            if (_segs[mid].Distance < distance) lo = mid + 1;
            else hi = mid;
        }
        int index = hi;
        if (_segs[hi].Distance < distance) index = hi + 1;
        ref var seg = ref _segs[index];
        float startT = 0, startD = 0;
        if (index > 0)
        {
            startD = _segs[index - 1].Distance;
            if (_segs[index - 1].PtIndex == seg.PtIndex) startT = TValueToScalar(_segs[index - 1].TValue);
        }
        t = startT + (TValueToScalar(seg.TValue) - startT) * (distance - startD) / (seg.Distance - startD);
        return index;
    }

    private Vector2 PosAt(int ptIndex, SegType type, float t)
    {
        ReadOnlySpan<Vector2> pts = _pts.AsSpan(ptIndex, _ptCount - ptIndex);
        switch (type)
        {
            case SegType.Line:
                return new Vector2(pts[0].X + (pts[1].X - pts[0].X) * t, pts[0].Y + (pts[1].Y - pts[0].Y) * t);
            case SegType.Quad:
                return EvalQuadAt(pts, t);
            case SegType.Conic:
                return new ConicCurve(pts[0], pts[2], pts[3], pts[1].X).EvalAt(t);
            default:
                return EvalCubicAt(pts, t);
        }
    }

    /// <summary>
    /// Appends the piece between distances <paramref name="startD"/> and
    /// <paramref name="stopD"/> to <paramref name="dst"/>, starting with a move
    /// if asked (<c>SkContourMeasure::getSegment</c>).
    /// </summary>
    public bool GetSegment(float startD, float stopD, Path dst, bool startWithMoveTo)
    {
        float length = Length;
        if (startD < 0) startD = 0;
        if (stopD > length) stopD = length;
        if (!(startD <= stopD)) return false;
        if (_segCount == 0) return false;

        int seg = DistanceToSegment(startD, out float startT);
        if (!float.IsFinite(startT)) return false;
        int stopSeg = DistanceToSegment(stopD, out float stopT);
        if (!float.IsFinite(stopT)) return false;
        if (startWithMoveTo) dst.MoveTo(PosAt(_segs[seg].PtIndex, _segs[seg].Type, startT));

        if (_segs[seg].PtIndex == _segs[stopSeg].PtIndex)
        {
            SegTo(_segs[seg].PtIndex, _segs[seg].Type, startT, stopT, dst);
        }
        else
        {
            do
            {
                SegTo(_segs[seg].PtIndex, _segs[seg].Type, startT, 1, dst);
                // Segment::Next: the next segment with a different point index.
                int ptIndex = _segs[seg].PtIndex;
                do { seg++; } while (_segs[seg].PtIndex == ptIndex);
                startT = 0;
            } while (_segs[seg].PtIndex < _segs[stopSeg].PtIndex);
            SegTo(_segs[seg].PtIndex, _segs[seg].Type, 0, stopT, dst);
        }
        return true;
    }

    /// <summary>Appends the piece of one segment between two parameters to <paramref name="dst"/> (<c>SkContourMeasure_segTo</c>).</summary>
    private void SegTo(int ptIndex, SegType type, float startT, float stopT, Path dst)
    {
        if (startT == stopT)
        {
            // A zero-length dash: a zero-length line, which the stroker caps.
            if (dst.TryGetLastPoint(out var last)) dst.LineTo(last);
            return;
        }
        ReadOnlySpan<Vector2> pts = _pts.AsSpan(ptIndex, _ptCount - ptIndex);
        Span<Vector2> tmp0 = stackalloc Vector2[7];
        Span<Vector2> tmp1 = stackalloc Vector2[7];
        switch (type)
        {
            case SegType.Line:
                if (stopT == 1) dst.LineTo(pts[1]);
                else dst.LineTo(new Vector2(pts[0].X + (pts[1].X - pts[0].X) * stopT, pts[0].Y + (pts[1].Y - pts[0].Y) * stopT));
                break;
            case SegType.Quad:
                if (0 == startT)
                {
                    if (stopT == 1) dst.QuadTo(pts[1], pts[2]);
                    else
                    {
                        ChopQuadAt(pts, tmp0, stopT);
                        dst.QuadTo(tmp0[1], tmp0[2]);
                    }
                }
                else
                {
                    ChopQuadAt(pts, tmp0, startT);
                    if (stopT == 1) dst.QuadTo(tmp0[3], tmp0[4]);
                    else
                    {
                        ChopQuadAt(tmp0[2..5], tmp1, (stopT - startT) / (1 - startT));
                        dst.QuadTo(tmp1[1], tmp1[2]);
                    }
                }
                break;
            case SegType.Conic:
            {
                var conic = new ConicCurve(pts[0], pts[2], pts[3], pts[1].X);
                if (0 == startT)
                {
                    if (stopT == 1) dst.ConicTo(conic.P1, conic.P2, conic.W);
                    else if (conic.ChopAt(stopT, out var a, out _)) dst.ConicTo(a.P1, a.P2, a.W);
                }
                else
                {
                    if (stopT == 1)
                    {
                        if (conic.ChopAt(startT, out _, out var b)) dst.ConicTo(b.P1, b.P2, b.W);
                    }
                    else
                    {
                        var c = conic.ChopAt(startT, stopT);
                        dst.ConicTo(c.P1, c.P2, c.W);
                    }
                }
                break;
            }
            default:
                if (0 == startT)
                {
                    if (stopT == 1) dst.CubicTo(pts[1], pts[2], pts[3]);
                    else
                    {
                        ChopCubicAt(pts, tmp0, stopT);
                        dst.CubicTo(tmp0[1], tmp0[2], tmp0[3]);
                    }
                }
                else
                {
                    ChopCubicAt(pts, tmp0, startT);
                    if (stopT == 1) dst.CubicTo(tmp0[4], tmp0[5], tmp0[6]);
                    else
                    {
                        ChopCubicAt(tmp0[3..7], tmp1, (stopT - startT) / (1 - startT));
                        dst.CubicTo(tmp1[1], tmp1[2], tmp1[3]);
                    }
                }
                break;
        }
    }
}
