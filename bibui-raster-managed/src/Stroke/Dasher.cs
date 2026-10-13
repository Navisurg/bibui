using System.Numerics;
using static Bibui.Raster.Managed.CurveMath;

namespace Bibui.Raster.Managed;

/// <summary>What <see cref="Dasher.Apply"/> produced.</summary>
internal enum DashResult
{
    /// <summary>The pattern is invalid or the path too long to dash; draw the path undashed.</summary>
    None,
    /// <summary>The dashes as open contours, to be stroked (or drawn as hairlines).</summary>
    Path,
    /// <summary>A straight butt-capped line's dashes, already their outlines: fill them, do not stroke.</summary>
    Filled,
}

/// <summary>
/// The dash path effect: a path cut into on/off pieces along its
/// length, before it is stroked. A dash that runs over a closed contour's
/// start joins the piece it started with, long straight lines are culled to
/// the clip, dashed rects get a closing join, and a straight butt-capped
/// line's dashes are emitted as quads to fill.
///
/// <para>A port of Skia m88's <c>SkDashPath</c> (the Sk names on members map
/// it), so dashes fall exactly where the Skia backend's do and dashed borders
/// match the fixture baselines it drew.</para>
/// </summary>
internal sealed class Dasher
{
    /// <summary>Past a million dashes the path is drawn undashed.</summary>
    public const float MaxDashCount = 1000000;

    private readonly ContourMeasure _measure = new();
    private readonly Path _cull = new();

    /// <summary>
    /// Dashes <paramref name="src"/> into <paramref name="dst"/> with
    /// <paramref name="dash"/> for a stroke of <paramref name="paint"/>.
    /// <paramref name="cullRect"/> is the clip in local coordinates (or null),
    /// <paramref name="resScale"/> the transform's scale.
    /// </summary>
    public DashResult Apply(Path src, DashPattern dash, in Paint paint, float resScale, Rect? cullRect, Path dst)
    {
        Span<float> intervals = [dash.On, dash.Off];
        if (!Valid(dash.Phase, intervals)) return DashResult.None;
        CalcDashParameters(dash.Phase, intervals, out float initialDashLength, out int initialDashIndex, out float intervalLength);
        bool hairline = paint.StrokeWidth <= 0;
        float width = paint.StrokeWidth;

        Path srcPtr = src;
        _cull.Rewind();
        if (CullPath(src, paint, hairline, cullRect, intervalLength, _cull))
        {
            // A closed rect that starts and ends in a dash gets its first join back (skbug.com/7445).
            if (src.IsRect(out _, out _, out _) && src.IsLastContourClosed && (initialDashIndex & 1) == 0)
            {
                _measure.Reset(src, resScale);
                float pathLength = _measure.Length;
                float endPhase = Mod(pathLength + initialDashLength, intervalLength);
                int index = 0;
                while (endPhase > intervals[index])
                {
                    endPhase -= intervals[index++];
                    if (index == intervals.Length)
                    {
                        endPhase = 0;
                        break;
                    }
                }
                if (((index & 1) == 0) == (endPhase > 0))
                {
                    var midPoint = src.PointAt(0);
                    int last = src.PointCount - 1;
                    while (midPoint == src.PointAt(last)) --last;
                    int next = 1;
                    while (midPoint == src.PointAt(next)) ++next;
                    var v = (midPoint - src.PointAt(last)) * NearlyZero;
                    _cull.MoveTo(midPoint - v);
                    _cull.LineTo(midPoint);
                    v = (midPoint - src.PointAt(next)) * NearlyZero;
                    _cull.LineTo(midPoint - v);
                }
            }
            srcPtr = _cull;
        }

        // A straight butt-capped line: its dashes are quads, already stroked.
        bool specialLine = false;
        Vector2 lineStart = default, tangent = default, normal = default;
        float lineLength = 0;
        if (!hairline && paint.Cap == LineCap.Butt && srcPtr.IsLine(out var a, out var b))
        {
            float len = Distance(a, b);
            var t = b - a;
            if (t.X != 0 || t.Y != 0)
            {
                specialLine = true;
                lineStart = a;
                lineLength = len;
                tangent = t * (1f / len);
                normal = RotateCCW(tangent) * (width * 0.5f);
            }
        }

        float dashCount = 0;
        int segCount = 0;
        _measure.Reset(srcPtr, resScale);
        Span<Vector2> quad = stackalloc Vector2[4];
        do
        {
            bool skipFirstSegment = _measure.IsClosed;
            bool addedSegment = false;
            float length = _measure.Length;
            int index = initialDashIndex;

            dashCount += length * (intervals.Length >> 1) / intervalLength;
            if (dashCount > MaxDashCount)
            {
                dst.Rewind();
                return DashResult.None;
            }

            // Doubles, so a long path does not stall on float rounding.
            double distance = 0;
            double dlen = initialDashLength;
            while (distance < length)
            {
                addedSegment = false;
                if ((index & 1) == 0 && !skipFirstSegment)
                {
                    addedSegment = true;
                    ++segCount;
                    if (specialLine)
                    {
                        float d0 = (float)distance, d1 = (float)(distance + dlen);
                        if (d1 > lineLength) d1 = lineLength;
                        float x0 = lineStart.X + tangent.X * d0;
                        float x1 = lineStart.X + tangent.X * d1;
                        float y0 = lineStart.Y + tangent.Y * d0;
                        float y1 = lineStart.Y + tangent.Y * d1;
                        quad[0] = new Vector2(x0 + normal.X, y0 + normal.Y);
                        quad[1] = new Vector2(x1 + normal.X, y1 + normal.Y);
                        quad[2] = new Vector2(x1 - normal.X, y1 - normal.Y);
                        quad[3] = new Vector2(x0 - normal.X, y0 - normal.Y);
                        dst.AddPoly(quad, close: false);
                    }
                    else
                    {
                        _measure.GetSegment((float)distance, (float)(distance + dlen), dst, true);
                    }
                }
                distance += dlen;
                skipFirstSegment = false;
                index += 1;
                if (index == intervals.Length) index = 0;
                dlen = intervals[index];
            }

            // A closed contour that ends in a dash continues into the dash it skipped at the start.
            if (_measure.IsClosed && (initialDashIndex & 1) == 0 && initialDashLength >= 0)
            {
                _measure.GetSegment(0, initialDashLength, dst, !addedSegment);
                ++segCount;
            }
        } while (_measure.NextContour());

        return specialLine ? DashResult.Filled : DashResult.Path;
    }

    private static float Mod(float x, float y) => x % y; // SkScalarMod is fmodf

    private static bool Valid(float phase, ReadOnlySpan<float> intervals)
    {
        float length = 0;
        foreach (var iv in intervals)
        {
            if (iv < 0) return false;
            length += iv;
        }
        return length > 0 && float.IsFinite(phase) && float.IsFinite(length);
    }

    /// <summary><c>SkDashPath::CalcDashParameters</c>: where in the pattern the path starts.</summary>
    internal static void CalcDashParameters(float phase, ReadOnlySpan<float> intervals, out float initialDashLength, out int initialDashIndex, out float intervalLength)
    {
        float len = 0;
        foreach (var iv in intervals) len += iv;
        intervalLength = len;
        if (phase < 0)
        {
            phase = -phase;
            if (phase > len) phase = Mod(phase, len);
            phase = len - phase;
            if (phase == len) phase = 0;
        }
        else if (phase >= len) phase = Mod(phase, len);

        for (int i = 0; i < intervals.Length; ++i)
        {
            float gap = intervals[i];
            if (phase > gap || (phase == gap && gap != 0)) phase -= gap;
            else
            {
                initialDashIndex = i;
                initialDashLength = gap - phase;
                return;
            }
        }
        initialDashIndex = 0;
        initialDashLength = intervals[0];
    }

    private static void OutsetForStroke(ref Rect r, in Paint paint, bool hairline)
    {
        float radius = hairline ? 1f : paint.StrokeWidth * 0.5f;
        if (paint.Join == LineJoin.Miter) radius *= paint.MiterLimit > 0 ? paint.MiterLimit : 4f;
        r = r.Outset(radius, radius);
    }

    private static void AdjustZeroLengthLine(ref Vector2 b) => b.X += MathF.Max(1.001f, b.X) * NearlyZero;

    private static bool ClipLine(ref Vector2 p0, ref Vector2 p1, Rect bounds, float intervalLength, float priorPhase)
    {
        var dxy = p1 - p0;
        if (dxy.X != 0 && dxy.Y != 0) return false;
        bool vertical = dxy.Y != 0;
        float minXY = vertical ? p0.Y : p0.X;
        float maxXY = vertical ? p1.Y : p1.X;
        bool swapped = maxXY < minXY;
        if (swapped) (minXY, maxXY) = (maxXY, minXY);
        float leftTop = vertical ? bounds.Top : bounds.Left;
        float rightBottom = vertical ? bounds.Bottom : bounds.Right;
        if (maxXY < leftTop || minXY > rightBottom) return false;
        if (minXY < leftTop)
        {
            minXY = leftTop - Mod(leftTop - minXY, intervalLength);
            if (!swapped) minXY -= priorPhase;
        }
        if (maxXY > rightBottom)
        {
            maxXY = rightBottom + Mod(maxXY - rightBottom, intervalLength);
            if (swapped) maxXY += priorPhase;
        }
        if (swapped) (minXY, maxXY) = (maxXY, minXY);
        if (vertical) { p0.Y = minXY; p1.Y = maxXY; }
        else { p0.X = minXY; p1.X = maxXY; }
        if (minXY == maxXY) AdjustZeroLengthLine(ref p1);
        return true;
    }

    /// <summary><c>cull_path</c>: a line or a rect, trimmed to the clip in phase with the pattern.</summary>
    private static bool CullPath(Path src, in Paint paint, bool hairline, Rect? cullRect, float intervalLength, Path dst)
    {
        if (cullRect is not { } cull)
        {
            if (src.IsLine(out var a, out var b) && a == b)
            {
                AdjustZeroLengthLine(ref b);
                dst.MoveTo(a);
                dst.LineTo(b);
                return true;
            }
            return false;
        }
        var bounds = cull;
        OutsetForStroke(ref bounds, paint, hairline);
        {
            if (src.IsLine(out var a, out var b))
            {
                if (ClipLine(ref a, ref b, bounds, intervalLength, 0))
                {
                    dst.MoveTo(a);
                    dst.LineTo(b);
                    return true;
                }
                return false;
            }
        }
        if (src.IsRect(out _, out _, out _))
        {
            // Four lines, each culled; the phase carried across the corners.
            var iter = new PathIter(src);
            Span<Vector2> pts = stackalloc Vector2[4];
            iter.Next(pts, out _);
            float accum = 0;
            while (iter.Next(pts, out var verb) && verb == PathVerb.Line)
            {
                var v = pts[1] - pts[0];
                var a = pts[0];
                var b = pts[1];
                if (ClipLine(ref a, ref b, bounds, intervalLength, Mod(accum, intervalLength)))
                {
                    if (!dst.TryGetLastPoint(out var last) || last != a) dst.MoveTo(a);
                    dst.LineTo(b);
                }
                accum += MathF.Abs(v.X + v.Y);
            }
            return !dst.IsEmpty;
        }
        return false;
    }

    /// <summary>
    /// The rect shortcut for a line (<c>SkDashImpl::asPoints</c>): when a
    /// straight, axis-aligned, butt-capped line has equal whole-number
    /// intervals, its dashes are drawn as filled rects rather than as a path.
    /// Writes the rects (local coordinates), in draw order — a partial first
    /// and last dash, which are filled as paths
    /// (<c>AsPath</c>), then the whole ones — and returns how many; −1 when
    /// the shortcut does not apply. <paramref name="cullRect"/> is the device
    /// clip outset by the stroke's reach and mapped to local coordinates.
    /// </summary>
    public static int AsRects(Vector2 p0, Vector2 p1, DashPattern dash, in Paint paint, bool rectStaysRect, Rect? cullRect, List<(Rect Rect, bool AsPath)> rects)
    {
        rects.Clear();
        if (!(paint.StrokeWidth > 0)) return -1;
        if (!IsNearlyEqual(dash.On, dash.Off) || !IsInt(dash.On) || !IsInt(dash.Off)) return -1;
        if (paint.Cap != LineCap.Butt) return -1;
        if (!rectStaysRect) return -1;
        Span<float> intervals = [dash.On, dash.Off];
        if (!Valid(dash.Phase, intervals)) return -1;
        CalcDashParameters(dash.Phase, intervals, out float initialDashLength, out int initialDashIndex, out float intervalLength);

        // cull_line (SkDashPathEffect.cpp): trim a horizontal or vertical line to the clip.
        if (cullRect is not { } bounds) return -1;
        float dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        if ((dx != 0 && dy != 0) || (dx == 0 && dy == 0)) return -1;
        if (dx != 0)
        {
            float minX = p0.X, maxX = p1.X;
            if (dx < 0) (minX, maxX) = (maxX, minX);
            if (maxX <= bounds.Left || minX >= bounds.Right) return -1;
            if (minX < bounds.Left) minX = bounds.Left - Mod(bounds.Left - minX, intervalLength);
            if (maxX > bounds.Right) maxX = bounds.Right + Mod(maxX - bounds.Right, intervalLength);
            if (dx < 0) (minX, maxX) = (maxX, minX);
            p0.X = minX;
            p1.X = maxX;
        }
        else
        {
            float minY = p0.Y, maxY = p1.Y;
            if (dy < 0) (minY, maxY) = (maxY, minY);
            if (maxY <= bounds.Top || minY >= bounds.Bottom) return -1;
            if (minY < bounds.Top) minY = bounds.Top - Mod(bounds.Top - minY, intervalLength);
            if (maxY > bounds.Bottom) maxY = bounds.Bottom + Mod(maxY - bounds.Bottom, intervalLength);
            if (dy < 0) (minY, maxY) = (maxY, minY);
            p0.Y = minY;
            p1.Y = maxY;
        }

        float length = Distance(p1, p0);
        var tangent = p1 - p0;
        if (tangent.X == 0 && tangent.Y == 0) return -1;
        tangent *= 1f / length;

        float halfW = paint.StrokeWidth * 0.5f;
        float on = intervals[0], off = intervals[1];
        bool isXAxis;
        Vector2 size;
        if (IsNearlyEqual(1, tangent.X) || IsNearlyEqual(-1, tangent.X)) { size = new(on * 0.5f, halfW); isXAxis = true; }
        else if (IsNearlyEqual(1, tangent.Y) || IsNearlyEqual(-1, tangent.Y)) { size = new(halfW, on * 0.5f); isXAxis = false; }
        else return -1;

        float clampedInitialDashLength = MathF.Min(length, initialDashLength);
        float len2 = length;
        if (clampedInitialDashLength > 0 || 0 == initialDashIndex)
        {
            if (0 == initialDashIndex)
            {
                if (clampedInitialDashLength > 0) len2 -= clampedInitialDashLength;
                len2 -= off;
                if (len2 < 0) len2 = 0;
            }
            else len2 -= clampedInitialDashLength;
        }
        float numIntervals = len2 / intervalLength;
        if (!float.IsFinite(numIntervals) || numIntervals > MaxDashCount) return -1;
        int numMidPoints = (int)MathF.Floor(numIntervals);
        len2 -= numMidPoints * intervalLength;
        bool partialLast = false;
        if (len2 > 0)
        {
            if (len2 < on) partialLast = true;
            else ++numMidPoints;
        }

        Rect Around(float x, float y, float hw, float hh) => new(x - hw, y - hh, x + hw, y + hh);

        Rect? first = null, last = null;
        int mark = rects.Count;
        float distance = 0;
        if (clampedInitialDashLength > 0 || 0 == initialDashIndex)
        {
            if (0 == initialDashIndex)
            {
                if (clampedInitialDashLength > 0)
                {
                    float x = p0.X + tangent.X * (clampedInitialDashLength * 0.5f);
                    float y = p0.Y + tangent.Y * (clampedInitialDashLength * 0.5f);
                    if (clampedInitialDashLength < on)
                    {
                        // A partial first dash: its own size.
                        float hw = isXAxis ? clampedInitialDashLength * 0.5f : halfW;
                        float hh = isXAxis ? halfW : clampedInitialDashLength * 0.5f;
                        first = Around(x, y, hw, hh);
                    }
                    else rects.Add((Around(x, y, size.X, size.Y), false));
                    distance += clampedInitialDashLength;
                }
                distance += off;
            }
            else distance += clampedInitialDashLength;
        }
        if (numMidPoints != 0)
        {
            distance += on * 0.5f;
            for (int i = 0; i < numMidPoints; ++i)
            {
                rects.Add((Around(p0.X + tangent.X * distance, p0.Y + tangent.Y * distance, size.X, size.Y), false));
                distance += intervalLength;
            }
            distance -= on * 0.5f;
        }
        if (partialLast)
        {
            float temp = length - distance;
            float x = p0.X + tangent.X * (distance + temp * 0.5f);
            float y = p0.Y + tangent.Y * (distance + temp * 0.5f);
            float hw = isXAxis ? temp * 0.5f : halfW;
            float hh = isXAxis ? halfW : temp * 0.5f;
            last = Around(x, y, hw, hh);
        }
        if (last is { } l) rects.Insert(mark, (l, true));
        if (first is { } f) rects.Insert(mark, (f, true));
        return rects.Count;
    }

    private static bool IsNearlyEqual(float a, float b) => MathF.Abs(a - b) <= NearlyZero;

    private static bool IsInt(float x) => x == MathF.Floor(x);
}
