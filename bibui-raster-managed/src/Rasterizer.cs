using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>Receives coverage: <paramref name="len"/> pixels from (x, y) at 8-bit coverage <paramref name="alpha"/> (1..255).</summary>
internal interface ICoverageSink
{
    void Blit(int y, int x, int len, int alpha);
}

/// <summary>
/// The scanline core: device-space line segments in, 8-bit coverage spans out.
///
/// <para>Coverage is the exact area of each pixel that lies inside the path.
/// Each pixel row is processed on its own with a one-row float accumulator:
/// a line's signed area goes into the cells it crosses (font-rs's
/// <c>draw_line</c>), and a prefix sum along the row turns those into
/// coverage. Runs of cells no line touched have the coverage of the cell
/// before them, and go to the sink as one span.</para>
///
/// <para>Two modes:</para>
/// <list type="bullet">
/// <item><b>Simple</b> accumulates every edge and takes |sum|, clamped. That is
/// exact only where the winding number is 0 or one sign of 1 — true of the
/// rects, rrects and rings the canvas builds itself, which use it.</item>
/// <item><b>Exact</b> splits the row into bands at every edge end point
/// and every crossing, so that within a band the edges are straight, disjoint
/// and in a fixed left-to-right order. Walking them with a running winding
/// number gives the intervals the fill rule puts inside, and only the edges
/// that bound an inside interval are accumulated — each as a trapezoid side.
/// Overlaps (winding 2) and cancellations (+1 over −1) come out exact, under
/// either fill rule.</item>
/// </list>
///
/// <para>Segments are clipped in x on the way in: the part left of the clip
/// becomes a vertical edge on its left side (it still carries winding), and
/// the part right of it is dropped. So the accumulator is never wider than
/// the clip, however large the path.</para>
///
/// <para>For <see cref="Antialiasing.BaselineCompatible"/>, two pieces reproduce
/// Skia 2.88's CPU scan converters exactly: <see cref="FillSupersampled"/> is
/// its supersampler (<c>SkScan_AntiPath.cpp</c>, SHIFT 2, with
/// <c>SkFDot6Round</c>/<c>SkScalarRoundToFDot6</c> rounding), and the
/// <c>snapY</c> of <see cref="Reset"/> is its analytic AA's quarter-pixel
/// <c>SnapY</c> (<c>SkAnalyticEdge</c>). The fixture baselines are drawn by the
/// Skia backend, so the managed backend rounds where it rounds to match them
/// byte for byte.</para>
/// </summary>
internal sealed unsafe class Rasterizer
{
    private struct Edge
    {
        public float X0, Y0, X1, Y1, Dxdy;
        public int Dir;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly float XAt(float y) => X0 + (y - Y0) * Dxdy;
    }

    private Edge[] _edges = new Edge[256];
    private int _count;
    private float[] _acc = GC.AllocateArray<float>(1024, pinned: true);
    private float[] _keys = new float[256];
    private int[] _order = new int[256];
    private int[] _active = new int[64];
    private float[] _events = new float[64];
    private int[] _band = new int[64];
    private float[] _bandTop = new float[64], _bandBot = new float[64], _bandKey = new float[64];
    private float[] _cross = new float[64];

    private IRect _clip;
    private float _minX, _maxX, _minY, _maxY;
    private bool _reachesRight;

    // Per row: the accumulator's origin and the cells touched.
    private int _ox, _minCell, _maxCell;

    public int EdgeCount => _count;

    private bool _snapY;

    /// <summary>
    /// Empties the edge list. <paramref name="snapY"/> rounds every end point's
    /// y to the nearest quarter pixel — for parity with the baselines (see the
    /// class), not accuracy.
    /// </summary>
    public void Reset(IRect clip, bool snapY = false)
    {
        _clip = clip;
        _count = 0;
        _minX = _minY = float.MaxValue;
        _maxX = _maxY = float.MinValue;
        _reachesRight = false;
        _snapY = snapY;
    }

    public bool IsEmpty => _count == 0;

    /// <summary>The pixels the edges can cover, within the clip.</summary>
    public IRect Bounds => _count == 0 ? default : new IRect(
        (int)MathF.Floor(_minX), (int)MathF.Floor(_minY),
        _reachesRight ? _clip.Right : (int)MathF.Ceiling(_maxX) + 1, (int)MathF.Ceiling(_maxY)).Intersect(_clip);

    // ── Building ─────────────────────────────────────────────────────────

    /// <summary>A line segment in device space, clipped to the clip's columns.</summary>
    public void AddLine(float x0, float y0, float x1, float y1)
    {
        if (_snapY)
        {
            y0 = MathF.Floor(y0 * 4f + 0.5f) * 0.25f;
            y1 = MathF.Floor(y1 * 4f + 0.5f) * 0.25f;
        }
        if (y0 == y1 || !(float.IsFinite(x0) && float.IsFinite(y0) && float.IsFinite(x1) && float.IsFinite(y1))) return;
        if (MathF.Max(y0, y1) <= _clip.Top || MathF.Min(y0, y1) >= _clip.Bottom) return;
        ClipX(x0, y0, x1, y1);
    }

    private void ClipX(float x0, float y0, float x1, float y1)
    {
        float l = _clip.Left, r = _clip.Right;
        if (x0 <= l && x1 <= l) { Push(l, y0, l, y1); return; }
        if (x0 >= r && x1 >= r) { _reachesRight = true; return; }
        if ((x0 < l) != (x1 < l))
        {
            float ym = y0 + (l - x0) * (y1 - y0) / (x1 - x0);
            if (x0 < l) { Push(l, y0, l, ym); ClipX(l, ym, x1, y1); }
            else { ClipX(x0, y0, l, ym); Push(l, ym, l, y1); }
            return;
        }
        if ((x0 > r) != (x1 > r))
        {
            float ym = y0 + (r - x0) * (y1 - y0) / (x1 - x0);
            _reachesRight = true;
            if (x0 > r) Push(r, ym, x1 < r ? x1 : r, y1);
            else Push(x0, y0, r, ym);
            return;
        }
        Push(x0, y0, x1, y1);
    }

    private void Push(float x0, float y0, float x1, float y1)
    {
        if (y0 == y1) return;
        if (_count == _edges.Length) Array.Resize(ref _edges, _edges.Length * 2);
        ref var e = ref _edges[_count++];
        if (y0 < y1) { e.X0 = x0; e.Y0 = y0; e.X1 = x1; e.Y1 = y1; e.Dir = 1; }
        else { e.X0 = x1; e.Y0 = y1; e.X1 = x0; e.Y1 = y0; e.Dir = -1; }
        e.Dxdy = (e.X1 - e.X0) / (e.Y1 - e.Y0);
        if (x0 < _minX) _minX = x0;
        if (x1 < _minX) _minX = x1;
        if (x0 > _maxX) _maxX = x0;
        if (x1 > _maxX) _maxX = x1;
        if (e.Y0 < _minY) _minY = e.Y0;
        if (e.Y1 > _maxY) _maxY = e.Y1;
    }

    /// <summary>A closed polygon.</summary>
    public void AddPolygon(ReadOnlySpan<float> xy, bool reverse = false)
    {
        int n = xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            if (reverse) AddLine(xy[2 * j], xy[2 * j + 1], xy[2 * i], xy[2 * i + 1]);
            else AddLine(xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
        }
    }

    // ── Filling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rasterizes the segments into rows [rowStart, rowEnd) ∩ clip and hands
    /// the coverage to <paramref name="sink"/>.
    /// </summary>
    public void Fill<TSink>(ref TSink sink, FillRule rule, bool exact, int rowStart = int.MinValue, int rowEnd = int.MaxValue)
        where TSink : struct, ICoverageSink
    {
        if (_count == 0) return;
        int ry0 = Math.Max(Math.Max(rowStart, _clip.Top), (int)MathF.Floor(_minY));
        int ry1 = Math.Min(Math.Min(rowEnd, _clip.Bottom), (int)MathF.Ceiling(_maxY));
        if (ry1 <= ry0) return;
        int ox = Math.Max(_clip.Left, (int)MathF.Floor(_minX));
        int xEnd = _reachesRight ? _clip.Right : Math.Min(_clip.Right, (int)MathF.Ceiling(_maxX) + 1);
        if (xEnd <= ox) return;
        int bw = xEnd - ox + 2;
        if (!exact)
        {
            FillSimple(ref sink, ry0, ry1, ox, xEnd - ox);
            return;
        }
        if (_acc.Length < bw) _acc = GC.AllocateArray<float>(Math.Max(bw, _acc.Length * 2), pinned: true);
        _ox = ox;

        // Edges in order of their top.
        if (_keys.Length < _count) { _keys = new float[_edges.Length]; _order = new int[_edges.Length]; }
        for (int i = 0; i < _count; i++) { _keys[i] = _edges[i].Y0; _order[i] = i; }
        _keys.AsSpan(0, _count).Sort(_order.AsSpan(0, _count));

        int next = 0, nActive = 0;
        int visible = xEnd - ox;
        fixed (float* acc = _acc)
        {
            for (int y = ry0; y < ry1; y++)
            {
                float yTop = y, yBot = y + 1;
                while (next < _count && _edges[_order[next]].Y0 < yBot)
                {
                    if (nActive == _active.Length) Array.Resize(ref _active, nActive * 2);
                    _active[nActive++] = _order[next++];
                }
                int keep = 0;
                for (int i = 0; i < nActive; i++)
                    if (_edges[_active[i]].Y1 > yTop) _active[keep++] = _active[i];
                nActive = keep;
                if (nActive == 0)
                {
                    if (next >= _count) break;
                    // Jump to the row the next edge starts in.
                    int ny = (int)MathF.Floor(_edges[_order[next]].Y0);
                    if (ny > y) y = Math.Min(ny, ry1) - 1;
                    continue;
                }

                _minCell = int.MaxValue;
                _maxCell = -1;
                if (exact) ExactRow(acc, y, nActive, rule);
                else
                {
                    for (int i = 0; i < nActive; i++)
                    {
                        ref var e = ref _edges[_active[i]];
                        float ya = MathF.Max(e.Y0, yTop), yb = MathF.Min(e.Y1, yBot);
                        if (yb <= ya) continue;
                        Accumulate(acc, e.XAt(ya) - ox, ya - yTop, e.XAt(yb) - ox, yb - yTop, e.Dir);
                    }
                }
                if (_maxCell >= 0) EmitRow(ref sink, acc, y, visible, !exact);
            }
        }
    }

    /// <summary>
    /// Simple mode: no sorting and no active list. Every edge adds its area
    /// straight into a strip of row accumulators (the first prototype's layout), and
    /// each row is then prefix-summed, |sum| clamped, with untouched runs found
    /// by a vectorised scan.
    /// </summary>
    private void FillSimple<TSink>(ref TSink sink, int ry0, int ry1, int ox, int visible) where TSink : struct, ICoverageSink
    {
        int bw = visible + 2;
        int stripRows = Math.Clamp(32768 / bw, 1, ry1 - ry0);
        int need = bw * stripRows;
        if (_acc.Length < need) _acc = GC.AllocateArray<float>(Math.Max(need, _acc.Length * 2), pinned: true);
        if (_rowMin.Length < stripRows) { _rowMin = new int[stripRows]; _rowMax = new int[stripRows]; }
        fixed (float* acc = _acc)
        {
            for (int s0 = ry0; s0 < ry1; s0 += stripRows)
            {
                int s1 = Math.Min(s0 + stripRows, ry1);
                _rowMin.AsSpan(0, s1 - s0).Fill(int.MaxValue);
                _rowMax.AsSpan(0, s1 - s0).Fill(-1);
                for (int i = 0; i < _count; i++)
                {
                    ref var e = ref _edges[i];
                    if (e.Y1 <= s0 || e.Y0 >= s1) continue;
                    int ya0 = Math.Max(s0, (int)MathF.Floor(e.Y0));
                    int ya1 = Math.Min(s1, (int)MathF.Ceiling(e.Y1));
                    float yPrev = MathF.Max(e.Y0, ya0);
                    float xPrev = e.XAt(yPrev) - ox;
                    for (int y = ya0; y < ya1; y++)
                    {
                        float yb = MathF.Min(e.Y1, y + 1);
                        float xb = e.XAt(yb) - ox;
                        AccumulateRaw(acc + (y - s0) * bw, xPrev, yPrev - y, xb, yb - y, e.Dir);
                        int r = y - s0;
                        int lo = (int)MathF.Floor(MathF.Max(0, MathF.Min(xPrev, xb)));
                        int hi = (int)MathF.Ceiling(MathF.Max(0, MathF.Max(xPrev, xb))) + 1;
                        if (lo < _rowMin[r]) _rowMin[r] = lo;
                        if (hi > _rowMax[r]) _rowMax[r] = hi;
                        yPrev = yb;
                        xPrev = xb;
                    }
                }
                for (int y = s0; y < s1; y++)
                    EmitRowFull(ref sink, acc + (y - s0) * bw, y, ox, visible, _rowMin[y - s0], Math.Min(_rowMax[y - s0], bw - 1));
            }
        }
    }

    private int[] _rowMin = new int[64], _rowMax = new int[64];

    /// <summary>
    /// One accumulator row to coverage spans, reading (and zeroing) only the
    /// touched cells [from, last]: before them coverage is 0, after them it is
    /// whatever the sum reached, to the visible edge.
    /// </summary>
    private static void EmitRowFull<TSink>(ref TSink sink, float* a, int y, int ox, int visible, int from, int last) where TSink : struct, ICoverageSink
    {
        if (last < 0) return;
        float sum = 0;
        int x = from;
        while (x < visible)
        {
            sum += a[x];
            a[x] = 0;
            int len;
            int n = last - x;
            if (n <= 0) len = visible - x;
            else
            {
                // Bit pattern scan: vectorised, and a −0 only ends a run early.
                int run = new ReadOnlySpan<int>(a + x + 1, n).IndexOfAnyExcept(0);
                len = run < 0 ? visible - x : 1 + run;
            }
            if (x + len > visible) len = visible - x;
            int alpha = PixelMath.CoverageToAlpha(MathF.Abs(sum));
            if (alpha != 0) sink.Blit(y, ox + x, len, alpha);
            x += len;
        }
        for (int i = Math.Max(x, from); i <= last; i++) a[i] = 0;
    }

    /// <summary>As <see cref="Accumulate"/>, without tracking the touched cells.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateRaw(float* a, float x0, float y0, float x1, float y1, float sign)
    {
        float d = (y1 - y0) * sign;
        if (d == 0) return;
        float xa, xb;
        if (x0 < x1) { xa = x0; xb = x1; } else { xa = x1; xb = x0; }
        if (xa < 0) xa = 0;
        if (xb < 0) xb = 0;
        float xaf = MathF.Floor(xa);
        int xai = (int)xaf;
        float xbc = MathF.Ceiling(xb);
        int xbi = (int)xbc;
        if (xbi <= xai + 1)
        {
            float xmf = 0.5f * (xa + xb) - xaf;
            a[xai] += d - d * xmf;
            a[xai + 1] += d * xmf;
            return;
        }
        float s = 1f / (xb - xa);
        float x0f = xa - xaf;
        float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
        float x1f = xb - xbc + 1;
        float am = 0.5f * s * x1f * x1f;
        a[xai] += d * a0;
        if (xbi == xai + 2) a[xai + 1] += d * (1 - a0 - am);
        else
        {
            float a1 = s * (1.5f - x0f);
            a[xai + 1] += d * (a1 - a0);
            float ds = d * s;
            for (int xi = xai + 2; xi < xbi - 1; xi++) a[xi] += ds;
            float a2 = a1 + (xbi - xai - 3) * s;
            a[xbi - 1] += d * (1 - a2 - am);
        }
        a[xbi] += d * am;
    }

    /// <summary>
    /// Adds the signed area to the right of one line segment within the row
    /// (font-rs). x is relative to the accumulator; y is 0..1 within the row,
    /// y0 &lt; y1. A positive sign covers to the right.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Accumulate(float* a, float x0, float y0, float x1, float y1, float sign)
    {
        float d = (y1 - y0) * sign;
        float xa, xb;
        if (x0 < x1) { xa = x0; xb = x1; } else { xa = x1; xb = x0; }
        if (xa < 0) xa = 0;
        if (xb < 0) xb = 0;
        float xaf = MathF.Floor(xa);
        int xai = (int)xaf;
        float xbc = MathF.Ceiling(xb);
        int xbi = (int)xbc;
        if (xai < _minCell) _minCell = xai;
        if (xbi <= xai + 1)
        {
            float xmf = 0.5f * (xa + xb) - xaf;
            a[xai] += d - d * xmf;
            a[xai + 1] += d * xmf;
            if (xai + 1 > _maxCell) _maxCell = xai + 1;
        }
        else
        {
            float s = 1f / (xb - xa);
            float x0f = xa - xaf;
            float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
            float x1f = xb - xbc + 1;
            float am = 0.5f * s * x1f * x1f;
            a[xai] += d * a0;
            if (xbi == xai + 2) a[xai + 1] += d * (1 - a0 - am);
            else
            {
                float a1 = s * (1.5f - x0f);
                a[xai + 1] += d * (a1 - a0);
                float ds = d * s;
                for (int xi = xai + 2; xi < xbi - 1; xi++) a[xi] += ds;
                float a2 = a1 + (xbi - xai - 3) * s;
                a[xbi - 1] += d * (1 - a2 - am);
            }
            a[xbi] += d * am;
            if (xbi > _maxCell) _maxCell = xbi;
        }
    }

    /// <summary>Prefix-sums the touched cells of one row, zeroing them, and emits coverage spans.</summary>
    private void EmitRow<TSink>(ref TSink sink, float* a, int y, int visible, bool absolute) where TSink : struct, ICoverageSink
    {
        int x = _minCell, last = _maxCell;
        float sum = 0;
        while (x < visible)
        {
            sum += a[x];
            a[x] = 0;
            float cov = absolute ? MathF.Abs(sum) : sum;
            int alpha = PixelMath.CoverageToAlpha(cov);
            int len = 1;
            int x2 = x + 1;
            if (x2 > last) len = visible - x;
            else
            {
                int run = new ReadOnlySpan<int>(a + x2, last + 1 - x2).IndexOfAnyExcept(0);
                if (run < 0) len = visible - x; // nothing else touched: constant to the end
                else len = 1 + run;
                if (x + len > visible) len = visible - x;
            }
            if (alpha != 0) sink.Blit(y, _ox + x, len, alpha);
            x += len;
            if (x > last) break;
        }
        // Touched cells past the visible columns were never read.
        for (int i = Math.Max(x, _minCell); i <= last; i++) a[i] = 0;
    }

    // ── Exact mode ────────────────────────────────────────────────

    private void ExactRow(float* acc, int y, int nActive, FillRule rule)
    {
        float yTop = y, yBot = y + 1;
        if (nActive == 2)
        {
            // The common row: two edges of opposite direction cross it top to
            // bottom without crossing each other — the inside is between them.
            ref var e0 = ref _edges[_active[0]];
            ref var e1 = ref _edges[_active[1]];
            if (e0.Dir != e1.Dir && e0.Y0 <= yTop && e1.Y0 <= yTop && e0.Y1 >= yBot && e1.Y1 >= yBot)
            {
                float t0 = e0.XAt(yTop), b0 = e0.XAt(yBot), t1 = e1.XAt(yTop), b1 = e1.XAt(yBot);
                if ((t0 - t1) * (b0 - b1) > 0 || (t0 == t1 && b0 != b1) || (b0 == b1 && t0 != t1))
                {
                    bool firstLeft = t0 + b0 < t1 + b1;
                    Accumulate(acc, t0 - _ox, 0, b0 - _ox, 1, firstLeft ? 1f : -1f);
                    Accumulate(acc, t1 - _ox, 0, b1 - _ox, 1, firstLeft ? -1f : 1f);
                    return;
                }
            }
        }
        int ne = 0;
        EnsureEvents(2 * nActive + 2);
        _events[ne++] = yTop;
        _events[ne++] = yBot;
        for (int i = 0; i < nActive; i++)
        {
            ref var e = ref _edges[_active[i]];
            if (e.Y0 > yTop && e.Y0 < yBot) _events[ne++] = e.Y0;
            if (e.Y1 > yTop && e.Y1 < yBot) _events[ne++] = e.Y1;
        }
        ne = SortUnique(_events, ne);
        for (int k = 0; k + 1 < ne; k++) Band(acc, y, _events[k], _events[k + 1], nActive, rule, 0);
    }

    private void Band(float* acc, int y, float ya, float yb, int nActive, FillRule rule, int depth)
    {
        if (!(yb - ya > 1e-7f)) return;
        float ym = 0.5f * (ya + yb);
        if (_band.Length < nActive) { int n2 = nActive * 2; _band = new int[n2]; _bandTop = new float[n2]; _bandBot = new float[n2]; _bandKey = new float[n2]; }

        int n = 0;
        for (int i = 0; i < nActive; i++)
        {
            int ei = _active[i];
            ref var e = ref _edges[ei];
            // Band limits are edge end points, so an edge spans the band or misses it.
            if (e.Y0 > ym || e.Y1 < ym) continue;
            _band[n] = ei;
            _bandTop[n] = e.XAt(ya);
            _bandBot[n] = e.XAt(yb);
            n++;
        }
        if (n == 0) return;

        // Order by x at the top, ties by x at the bottom.
        for (int i = 1; i < n; i++)
        {
            int bi = _band[i];
            float t = _bandTop[i], b = _bandBot[i];
            int j = i - 1;
            while (j >= 0 && (_bandTop[j] > t || (_bandTop[j] == t && _bandBot[j] > b)))
            {
                _band[j + 1] = _band[j]; _bandTop[j + 1] = _bandTop[j]; _bandBot[j + 1] = _bandBot[j];
                j--;
            }
            _band[j + 1] = bi; _bandTop[j + 1] = t; _bandBot[j + 1] = b;
        }

        // Any pair out of order at the bottom crosses inside the band: split there.
        const float eps = 1e-5f;
        bool crossed = false;
        float maxBot = _bandBot[0];
        for (int i = 1; i < n; i++)
        {
            if (_bandBot[i] < maxBot - eps) { crossed = true; break; }
            if (_bandBot[i] > maxBot) maxBot = _bandBot[i];
        }
        if (crossed && depth == 0)
        {
            int nc = 0;
            EnsureCross(2);
            _cross[nc++] = ya;
            _cross[nc++] = yb;
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                float dTop = _bandTop[j] - _bandTop[i];   // ≥ 0
                float dBot = _bandBot[j] - _bandBot[i];
                if (dBot < -eps)
                {
                    float t = dTop / (dTop - dBot);
                    float yc = ya + t * (yb - ya);
                    if (yc > ya && yc < yb)
                    {
                        EnsureCross(nc + 1);
                        _cross[nc++] = yc;
                    }
                }
            }
            // Copy out: the recursive bands reuse the scratch arrays.
            Span<float> cuts = nc <= 128 ? stackalloc float[nc] : new float[nc];
            _cross.AsSpan(0, nc).CopyTo(cuts);
            cuts.Sort();
            for (int k = 0; k + 1 < cuts.Length; k++)
                if (cuts[k + 1] > cuts[k]) Band(acc, y, cuts[k], cuts[k + 1], nActive, rule, 1);
            return;
        }
        if (crossed)
        {
            // Inside a band already split at every crossing, a residual
            // inversion is rounding: order by the middle.
            for (int i = 0; i < n; i++) _bandKey[i] = _bandTop[i] + _bandBot[i];
            for (int i = 1; i < n; i++)
            {
                int bi = _band[i];
                float t = _bandTop[i], b = _bandBot[i], k = _bandKey[i];
                int j = i - 1;
                while (j >= 0 && _bandKey[j] > k)
                {
                    _band[j + 1] = _band[j]; _bandTop[j + 1] = _bandTop[j]; _bandBot[j + 1] = _bandBot[j]; _bandKey[j + 1] = _bandKey[j];
                    j--;
                }
                _band[j + 1] = bi; _bandTop[j + 1] = t; _bandBot[j + 1] = b; _bandKey[j + 1] = k;
            }
        }

        // Walk left to right: accumulate only where the fill rule's answer changes.
        int ox = _ox;
        float yt = ya - y, ybr = yb - y;
        int w = 0;
        bool inside = false;
        for (int i = 0; i < n; i++)
        {
            w += _edges[_band[i]].Dir;
            bool now = rule == FillRule.NonZero ? w != 0 : (w & 1) != 0;
            if (now == inside) continue;
            Accumulate(acc, _bandTop[i] - ox, yt, _bandBot[i] - ox, ybr, now ? 1f : -1f);
            inside = now;
        }
    }

    // ── 4×4 supersampling ───────────────────────────────────────────────────

    private int[] _iacc = GC.AllocateArray<int>(1024, pinned: true);
    private int[] _ssTop = new int[256], _ssBot = new int[256];
    private int[] _xs = new int[64], _ws = new int[64];

    /// <summary>
    /// The supersampling scan converter, used for small curved shapes in
    /// <see cref="Antialiasing.BaselineCompatible"/> mode: four sub-scanlines per row; on each, every edge's x is rounded to a quarter
    /// pixel, the fill rule is applied to the winding at those crossings, and
    /// each inside interval adds 16 per covered quarter (64 per full pixel, 63
    /// on the last sub-scanline, so a covered pixel sums to 255). Winding is
    /// tracked per sample, so overlaps are right here too — only quantized.
    /// </summary>
    public void FillSupersampled<TSink>(ref TSink sink, FillRule rule, int rowStart = int.MinValue, int rowEnd = int.MaxValue)
        where TSink : struct, ICoverageSink
    {
        if (_count == 0) return;
        int ry0 = Math.Max(Math.Max(rowStart, _clip.Top), (int)MathF.Floor(_minY));
        int ry1 = Math.Min(Math.Min(rowEnd, _clip.Bottom), (int)MathF.Ceiling(_maxY));
        if (ry1 <= ry0) return;
        int ox = Math.Max(_clip.Left, (int)MathF.Floor(_minX));
        int xEnd = _reachesRight ? _clip.Right : Math.Min(_clip.Right, (int)MathF.Ceiling(_maxX) + 1);
        if (xEnd <= ox) return;
        int visible = xEnd - ox;
        int bw = visible + 2;
        if (_iacc.Length < bw) _iacc = GC.AllocateArray<int>(Math.Max(bw, _iacc.Length * 2), pinned: true);
        if (_ssTop.Length < _count) { _ssTop = new int[_edges.Length]; _ssBot = new int[_edges.Length]; }
        if (_keys.Length < _count) { _keys = new float[_edges.Length]; _order = new int[_edges.Length]; }

        // An edge's ends round to sub-scanlines: y·4 rounded in 26.6 fixed point.
        for (int i = 0; i < _count; i++)
        {
            ref var e = ref _edges[i];
            _ssTop[i] = (RoundFDot6(e.Y0 * 256f) + 32) >> 6;
            _ssBot[i] = (RoundFDot6(e.Y1 * 256f) + 32) >> 6;
            _keys[i] = e.Y0;
            _order[i] = i;
        }
        _keys.AsSpan(0, _count).Sort(_order.AsSpan(0, _count));
        int mask = rule == FillRule.NonZero ? -1 : 1;
        int limitX = visible * 4;

        int next = 0, nActive = 0;
        fixed (int* acc = _iacc)
        {
            for (int y = ry0; y < ry1; y++)
            {
                while (next < _count && _edges[_order[next]].Y0 < y + 1)
                {
                    if (nActive == _active.Length) Array.Resize(ref _active, nActive * 2);
                    _active[nActive++] = _order[next++];
                }
                int keep = 0;
                for (int i = 0; i < nActive; i++)
                    if (_edges[_active[i]].Y1 > y) _active[keep++] = _active[i];
                nActive = keep;
                if (nActive == 0)
                {
                    if (next >= _count) break;
                    int ny = (int)MathF.Floor(_edges[_order[next]].Y0);
                    if (ny > y) y = Math.Min(ny, ry1) - 1;
                    continue;
                }
                if (_xs.Length < nActive) { _xs = new int[nActive * 2]; _ws = new int[nActive * 2]; }

                int minC = int.MaxValue, maxC = -1;
                for (int sub = 0; sub < 4; sub++)
                {
                    int sy = y * 4 + sub;
                    float ys = (sy + 0.5f) * 0.25f;
                    int n = 0;
                    for (int i = 0; i < nActive; i++)
                    {
                        int ei = _active[i];
                        if (_ssTop[ei] > sy || _ssBot[ei] <= sy) continue;
                        ref var e = ref _edges[ei];
                        int x = (int)MathF.Floor((e.X0 + (ys - e.Y0) * e.Dxdy - ox) * 4f + 0.5f);
                        int w = e.Dir;
                        int j = n - 1;
                        while (j >= 0 && _xs[j] > x) { _xs[j + 1] = _xs[j]; _ws[j + 1] = _ws[j]; j--; }
                        _xs[j + 1] = x;
                        _ws[j + 1] = w;
                        n++;
                    }
                    int maxValue = sub == 3 ? 63 : 64;
                    int wind = 0, left = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int x = _xs[i];
                        if ((wind & mask) == 0) left = x;
                        wind += _ws[i];
                        if ((wind & mask) == 0)
                        {
                            int start = Math.Max(left, 0), stop = Math.Min(x, limitX);
                            if (stop > start) AddSpan(acc, start, stop, maxValue, ref minC, ref maxC);
                        }
                    }
                    // Edges right of the clip were dropped: an interval still open runs to its edge.
                    if ((wind & mask) != 0 && limitX > Math.Max(left, 0))
                        AddSpan(acc, Math.Max(left, 0), limitX, maxValue, ref minC, ref maxC);
                }
                if (maxC < 0) continue;

                // Prefix-sum the deltas, zeroing them, and emit runs.
                int sum = 0, xi = minC;
                while (xi < visible && xi <= maxC)
                {
                    sum += acc[xi];
                    acc[xi] = 0;
                    int len = 1;
                    int x2 = xi + 1;
                    if (x2 <= maxC)
                    {
                        int run = new ReadOnlySpan<int>(acc + x2, maxC + 1 - x2).IndexOfAnyExcept(0);
                        len = run < 0 ? maxC + 1 - xi : 1 + run;
                    }
                    else len = 1;
                    if (xi + len > visible) len = visible - xi;
                    int a = sum > 255 ? 255 : sum;
                    if (a > 0) sink.Blit(y, ox + xi, len, a);
                    xi += len;
                }
                for (int i = Math.Max(xi, minC); i <= maxC + 1 && i < bw; i++) acc[i] = 0;
            }
        }
    }

    /// <summary>
    /// <see cref="FillSupersampled"/> for shapes whose winding is only 0 and
    /// one sign of 1 (rrects and rings the canvas builds): there the sample
    /// coverage is linear in the edges, so each edge crossing adds its step
    /// straight into a strip of row accumulators — no sorting, no active list.
    /// Edges of direction <paramref name="leftDir"/> open the inside.
    /// </summary>
    public void FillSupersampledSimple<TSink>(ref TSink sink, int leftDir, int rowStart = int.MinValue, int rowEnd = int.MaxValue)
        where TSink : struct, ICoverageSink
    {
        if (_count == 0) return;
        int ry0 = Math.Max(Math.Max(rowStart, _clip.Top), (int)MathF.Floor(_minY));
        int ry1 = Math.Min(Math.Min(rowEnd, _clip.Bottom), (int)MathF.Ceiling(_maxY));
        if (ry1 <= ry0) return;
        int ox = Math.Max(_clip.Left, (int)MathF.Floor(_minX));
        int xEnd = _reachesRight ? _clip.Right : Math.Min(_clip.Right, (int)MathF.Ceiling(_maxX) + 1);
        if (xEnd <= ox) return;
        int visible = xEnd - ox;
        int bw = visible + 2;
        int limitX = visible * 4;
        int stripRows = Math.Clamp(32768 / bw, 1, ry1 - ry0);
        if (_rowMin.Length < stripRows) { _rowMin = new int[stripRows]; _rowMax = new int[stripRows]; }
        int need = bw * stripRows;
        if (_iacc.Length < need) _iacc = GC.AllocateArray<int>(Math.Max(need, _iacc.Length * 2), pinned: true);
        fixed (int* acc = _iacc)
        {
            for (int s0 = ry0; s0 < ry1; s0 += stripRows)
            {
                int s1 = Math.Min(s0 + stripRows, ry1);
                _rowMin.AsSpan(0, s1 - s0).Fill(int.MaxValue);
                _rowMax.AsSpan(0, s1 - s0).Fill(-1);
                for (int i = 0; i < _count; i++)
                {
                    ref var e = ref _edges[i];
                    if (e.Y1 <= s0 || e.Y0 >= s1) continue;
                    int top = Math.Max((RoundFDot6(e.Y0 * 256f) + 32) >> 6, s0 * 4);
                    int bot = Math.Min((RoundFDot6(e.Y1 * 256f) + 32) >> 6, s1 * 4);
                    bool isLeft = e.Dir == leftDir;
                    for (int sy = top; sy < bot; sy++)
                    {
                        float ys = (sy + 0.5f) * 0.25f;
                        int x = (int)MathF.Floor((e.XAt(ys) - ox) * 4f + 0.5f);
                        x = Math.Clamp(x, 0, limitX);
                        int r = (sy >> 2) - s0;
                        int* row = acc + r * bw;
                        int maxV = (sy & 3) == 3 ? 63 : 64;
                        int p = x >> 2, f = x & 3;
                        if (p < _rowMin[r]) _rowMin[r] = p;
                        if (p + 1 > _rowMax[r]) _rowMax[r] = p + 1;
                        if (isLeft)
                        {
                            if (f == 0) row[p] += maxV;
                            else { int v = (4 - f) * 16; row[p] += v; row[p + 1] += maxV - v; }
                        }
                        else
                        {
                            if (f == 0) row[p] -= maxV;
                            else { int v = f * 16; row[p] -= maxV - v; row[p + 1] -= v; }
                        }
                    }
                }
                for (int y = s0; y < s1; y++)
                {
                    int from = _rowMin[y - s0], last = Math.Min(_rowMax[y - s0], bw - 1);
                    if (last < 0) continue;
                    int* a = acc + (y - s0) * bw;
                    int sum = 0, x = from;
                    while (x < visible)
                    {
                        sum += a[x];
                        a[x] = 0;
                        int len;
                        int n = last - x;
                        if (n <= 0) len = visible - x;
                        else
                        {
                            int run = new ReadOnlySpan<int>(a + x + 1, n).IndexOfAnyExcept(0);
                            len = run < 0 ? visible - x : 1 + run;
                        }
                        if (x + len > visible) len = visible - x;
                        int alpha = sum <= 0 ? 0 : sum > 255 ? 255 : sum;
                        if (alpha != 0) sink.Blit(y, ox + x, len, alpha);
                        x += len;
                    }
                    for (int i = Math.Max(x, from); i <= last; i++) a[i] = 0;
                }
            }
        }
    }

    /// <summary>SkSuperBlitter::blitH for one sub-scanline span [start, stop) in quarter pixels, as deltas.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddSpan(int* acc, int start, int stop, int maxValue, ref int minC, ref int maxC)
    {
        int fb = start & 3, fe = stop & 3;
        int px = start >> 2;
        int n = (stop >> 2) - px - 1;
        if (px < minC) minC = px;
        if (n < 0)
        {
            int v = (fe - fb) * 16;
            acc[px] += v;
            acc[px + 1] -= v;
            if (px + 1 > maxC) maxC = px + 1;
            return;
        }
        if (fb == 0) n += 1;
        else
        {
            int v = (4 - fb) * 16;
            acc[px] += v;
            acc[px + 1] -= v;
            px++;
        }
        if (n > 0)
        {
            acc[px] += maxValue;
            acc[px + n] -= maxValue;
            px += n;
        }
        if (fe != 0)
        {
            int v = fe * 16;
            acc[px] += v;
            acc[px + 1] -= v;
            px++;
        }
        if (px > maxC) maxC = px;
    }

    /// <summary>Round half to even (what a magic-number float-to-26.6 conversion gives).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundFDot6(float v) => (int)Math.Round((double)v, MidpointRounding.ToEven);

    private static int SortUnique(float[] v, int n)
    {
        for (int i = 1; i < n; i++)
        {
            float t = v[i];
            int j = i - 1;
            while (j >= 0 && v[j] > t) { v[j + 1] = v[j]; j--; }
            v[j + 1] = t;
        }
        int m = n == 0 ? 0 : 1;
        for (int i = 1; i < n; i++) if (v[i] != v[m - 1]) v[m++] = v[i];
        return m;
    }

    private void EnsureEvents(int n)
    {
        if (_events.Length < n) _events = new float[Math.Max(n, _events.Length * 2)];
    }

    private void EnsureCross(int n)
    {
        if (_cross.Length < n) Array.Resize(ref _cross, Math.Max(n, _cross.Length * 2));
    }
}
