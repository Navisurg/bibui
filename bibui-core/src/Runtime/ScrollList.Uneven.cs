using System.Numerics;
using Bibui.Core;
using static Bibui.Core.Runtime.Scrollbar;

namespace Bibui.Core.Runtime;

/// <summary>
/// The heights of a scroll list's rows when they are not all the same: what each
/// row measured when it was last built, and an estimate for the rows never built.
/// The caller keeps it between frames beside its <see cref="ScrollPt"/>, one per
/// list, and tells it when rows are inserted or removed so the heights stay with
/// their rows.
///
/// <para>A row's height is read back from the layout that drew it, a frame late,
/// as everything in bibui is. Until a row has been built its height is the mean of
/// the rows that have been, or <see cref="Estimate"/> before any have. Positions
/// are sums of those heights, kept in a Fenwick tree, so finding the row at a
/// pixel or the pixel of a row costs log² n of the row count, not n.</para>
/// </summary>
public sealed class RowHeights
{
    private float[] _height = [];     // per row; NaN = never measured
    private double[] _sumTree = [];   // Fenwick tree over the measured heights (0 for unmeasured)
    private int[] _knownTree = [];    // Fenwick tree over "is measured" (0 or 1)
    private string[] _ids = [];       // the row boxes' ids, made once per row
    private string _list = "";
    private int _count;
    private double _knownSum;
    private int _knownCount;

    /// <summary>A list whose rows are about <paramref name="estimate"/> px tall until they are measured.</summary>
    public RowHeights(float estimate)
    {
        if (!(estimate > 0f)) throw new ArgumentOutOfRangeException(nameof(estimate), estimate, "A row is taller than zero.");
        Estimate = estimate;
    }

    /// <summary>What a row is taken to be before any row has been measured.</summary>
    public float Estimate { get; }

    /// <summary>How many rows have a measured height.</summary>
    public int Measured => _knownCount;

    /// <summary>What an unmeasured row is taken to be: the mean of the measured rows, or <see cref="Estimate"/>.</summary>
    public float Average => _knownCount > 0 ? (float)(_knownSum / _knownCount) : Estimate;

    /// <summary>Row <paramref name="i"/>'s height: measured, or <see cref="Average"/>.</summary>
    public float Of(int i) => float.IsNaN(_height[i]) ? Average : _height[i];

    /// <summary>Whether row <paramref name="i"/> has been measured.</summary>
    public bool IsMeasured(int i) => !float.IsNaN(_height[i]);

    /// <summary>Where row <paramref name="i"/>'s top is, from the top of row 0. <c>Top(count)</c> is the whole list's height.</summary>
    public double Top(int i)
    {
        double sum = 0; int known = 0;
        for (int j = i; j > 0; j -= j & -j) { sum += _sumTree[j]; known += _knownTree[j]; }
        return sum + (i - known) * (double)Average;
    }

    /// <summary>The whole list's height: the measured rows and the estimate for the rest.</summary>
    public double Total => _knownSum + (_count - _knownCount) * (double)Average;

    /// <summary>
    /// Forgets every height, for when they no longer hold: the list's width
    /// changed and its rows wrap differently, or its content was replaced.
    /// </summary>
    public void Clear()
    {
        Array.Fill(_height, float.NaN, 0, _count);
        Array.Clear(_sumTree);
        Array.Clear(_knownTree);
        (_knownSum, _knownCount) = (0, 0);
    }

    /// <summary><paramref name="n"/> rows were inserted before row <paramref name="at"/>: the heights after it move with their rows.</summary>
    public void Insert(int at, int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if ((uint)at > (uint)_count) throw new ArgumentOutOfRangeException(nameof(at), at, $"Rows are inserted at 0 to {_count}.");
        Resize(_count + n);
        Array.Copy(_height, at, _height, at + n, _count - n - at);
        Array.Fill(_height, float.NaN, at, n);
        Rebuild();
    }

    /// <summary><paramref name="n"/> rows from row <paramref name="at"/> on were removed.</summary>
    public void Remove(int at, int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (at < 0 || at + n > _count) throw new ArgumentOutOfRangeException(nameof(at), at, $"Rows {at} to {at + n} are not all among the {_count}.");
        Array.Copy(_height, at + n, _height, at, _count - at - n);
        _count -= n;
        Rebuild();
    }

    // ── The list's side ──────────────────────────────────────────────────────

    /// <summary>The rows built last frame, [First, Last): the ones to read back.</summary>
    internal int BuiltFirst, BuiltLast;

    /// <summary>The list's count, which may change from frame to frame: rows past it are dropped, new ones unmeasured.</summary>
    internal void Resize(int count)
    {
        if (count > _height.Length)
        {
            int cap = Math.Max(count, _height.Length * 2);
            var h = new float[cap];
            Array.Fill(h, float.NaN);
            Array.Copy(_height, h, _count);
            _height = h;
            Array.Resize(ref _ids, cap);
            _sumTree = new double[cap + 1];
            _knownTree = new int[cap + 1];
            _count = count;
            Rebuild();
            return;
        }
        if (count == _count) return;
        if (count < _count) Array.Fill(_height, float.NaN, count, _count - count);
        _count = count;
        Rebuild();
    }

    internal void Measure(int i, float height)
    {
        if (i >= _count || height <= 0f) return;
        float old = _height[i];
        if (old == height) return;
        double dSum = height - (float.IsNaN(old) ? 0f : old);
        int dKnown = float.IsNaN(old) ? 1 : 0;
        _height[i] = height;
        _knownSum += dSum;
        _knownCount += dKnown;
        for (int j = i + 1; j <= _count; j += j & -j)
        {
            _sumTree[j] += dSum;
            _knownTree[j] += dKnown;
        }
    }

    /// <summary>
    /// The row whose span holds <paramref name="pixel"/> (from the top of row 0),
    /// and how far into it the pixel is. Past the end, the last row.
    /// </summary>
    internal (int Row, float Into) Locate(double pixel)
    {
        if (_count == 0 || pixel <= 0) return (0, 0f);
        // The largest row whose top is at or before the pixel. Top is monotonic.
        int lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (Top(mid) <= pixel) lo = mid; else hi = mid - 1;
        }
        return (lo, (float)Math.Min(pixel - Top(lo), Of(lo)));
    }

    /// <summary>Row <paramref name="i"/>'s box id in the list named <paramref name="list"/>, made once.</summary>
    internal string RowId(string list, int i)
    {
        if (!ReferenceEquals(list, _list) && list != _list)
        {
            _list = list;
            Array.Clear(_ids);
        }
        return _ids[i] ??= list + "-row-" + i;
    }

    private void Rebuild()
    {
        Array.Clear(_sumTree, 0, Math.Min(_sumTree.Length, _count + 1));
        Array.Clear(_knownTree, 0, Math.Min(_knownTree.Length, _count + 1));
        (_knownSum, _knownCount) = (0, 0);
        if (_sumTree.Length < _count + 1) { _sumTree = new double[_count + 1]; _knownTree = new int[_count + 1]; }
        for (int i = 0; i < _count; i++)
        {
            if (float.IsNaN(_height[i])) continue;
            _knownSum += _height[i];
            _knownCount++;
            for (int j = i + 1; j <= _count; j += j & -j)
            {
                _sumTree[j] += _height[i];
                _knownTree[j] += 1;
            }
        }
        Array.Clear(_ids);
    }
}

public static partial class ScrollList
{
    /// <summary>
    /// Builds the list named <paramref name="id"/> whose rows are not all one
    /// height, as <see cref="BeginScrollList(BibuiRuntime, string, ref ScrollPt, float, int)"/>
    /// builds one whose rows are: fills the parent in scope with the view and the
    /// bar beside it, and pushes the view for the rows. Build each row
    /// <see cref="ScrollPt.First"/> up to <see cref="ScrollPt.Last"/> inside
    /// <see cref="BeginScrollRow"/> and <see cref="EndScrollRow"/>, which measure
    /// it, then call <see cref="EndScrollList"/>.
    ///
    /// <para>The list scrolls by pixels but keeps its place by a row: the target
    /// is row <see cref="ScrollPt.Idx"/>, <see cref="ScrollPt.Px"/> into it, so a
    /// row above it that turns out taller than estimated moves nothing in view.
    /// <see cref="ScrollPt.Off"/> is in pixels here, not rows. The bar is placed by
    /// the heights measured so far and the estimate for the rest, so its thumb may
    /// move a little as unmeasured rows come into view.</para>
    ///
    /// <para>For rows of one height use the other overload: it needs no heights
    /// and builds nothing it does not show.</para>
    /// </summary>
    public static void BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, RowHeights rows, int count)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(rows);

        Box area = runtime.BoxFromId(id + "-area");
        area.ChildLayoutAxis = LayoutAxis.X;
        (area.SizeX, area.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip, area);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        view.AllowOverflowY = true;

        // What last frame's rows measured, before anything is placed from the heights.
        rows.Resize(count);
        for (int i = rows.BuiltFirst; i < rows.BuiltLast && i < count; i++)
            if (runtime.RetainedBox(rows.RowId(id, i)) is { } row) rows.Measure(i, row.Extent.Y);

        float height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured;
        double maxTop = Math.Max(0, rows.Total - height);   // the last row flush with the bottom
        pt.Rows = rows;
        pt.Height = height;
        pt.ListId = id;

        // Off is pixels here: eased once a frame, to half a pixel.
        if (pt.Stepped != runtime.FrameCount)
        {
            pt.Stepped = runtime.FrameCount;
            pt.Off = Ease.Exponential(pt.Off, 0f, runtime.Rates.Follow);
            if (MathF.Abs(pt.Off) <= 0.5f) pt.Off = 0f;
        }
        if (pt.Off != 0f) runtime.RequestFrame();

        using (runtime.Parent(area))
            UnevenScrollbar(runtime, id + "-bar", ref pt, rows, height);
        ClampTarget(ref pt, rows, maxTop, count);

        // The rows in sight, from the row at the top: what is drawn is laid out, so
        // the heights only decide how many rows to build. One spare row below.
        double top = Math.Clamp(rows.Top(pt.Idx) + pt.Px + pt.Off, 0, maxTop);
        var (first, into) = rows.Locate(top);
        int last = first;
        for (float y = -into; last < count && y < height; last++) y += rows.Of(last);
        pt.First = first;
        pt.Last = Math.Min(last + 1, count);
        pt.ViewRows = (int)(height / rows.Average);
        (rows.BuiltFirst, rows.BuiltLast) = (pt.First, pt.Last);
        view.ViewOffset = view.TargetViewOffset = new Vector2(0f, into);

        runtime.Parent(view);   // EndScrollList pops it
    }

    /// <summary>
    /// Row <paramref name="i"/> of the list <paramref name="pt"/> scrolls, which
    /// <see cref="BeginScrollList(BibuiRuntime, string, ref ScrollPt, RowHeights, int)"/>
    /// began: a box as wide as the list and as tall as what is built into it,
    /// retained so the list can read back its height. Pushed; end it with
    /// <see cref="EndScrollRow"/>.
    /// </summary>
    public static Box BeginScrollRow(this BibuiRuntime runtime, ref ScrollPt pt, int i)
    {
        var rows = pt.Rows ?? throw new InvalidOperationException("BeginScrollRow is for a list begun with RowHeights.");
        Box row = runtime.BoxFromId(rows.RowId(pt.ListId!, i));
        row.ChildLayoutAxis = LayoutAxis.Y;
        (row.SizeX, row.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ChildrenSum());
        return runtime.BeginParent(row);
    }

    /// <summary>Ends <see cref="BeginScrollRow"/>.</summary>
    public static void EndScrollRow(this BibuiRuntime runtime, Box row) => runtime.EndParent(row);

    // The wheel over an uneven list: pixels moved from the target, and Off takes
    // them up so what is shown eases there.
    private static void EndUneven(BibuiRuntime runtime, ref ScrollPt pt, float dy)
    {
        var rows = pt.Rows!;
        double maxTop = Math.Max(0, rows.Total - pt.Height);
        double from = rows.Top(pt.Idx) + pt.Px;
        double to = Math.Clamp(from + dy, 0, maxTop);
        (pt.Idx, pt.Px) = rows.Locate(to);
        pt.Off += (float)(from - to);
    }

    // The target kept between the top and the last row at the bottom, holding what is shown.
    private static void ClampTarget(ref ScrollPt pt, RowHeights rows, double maxTop, int count)
    {
        if (count == 0) { (pt.Idx, pt.Px) = (0, 0f); return; }
        pt.Idx = Math.Clamp(pt.Idx, 0, count - 1);
        double at = rows.Top(pt.Idx) + pt.Px;
        double to = Math.Clamp(at, 0, maxTop);
        if (to == at && pt.Px >= 0f && pt.Px < rows.Of(pt.Idx)) return;
        (pt.Idx, pt.Px) = rows.Locate(to);
        pt.Off += (float)(at - to);
    }

    /// <summary>The bar for an uneven list: its thumb by pixels, from the heights measured and estimated.</summary>
    private static void UnevenScrollbar(BibuiRuntime runtime, string id, ref ScrollPt pt, RowHeights rows, float height)
    {
        Box track = runtime.BoxFromId(id);
        track.ChildLayoutAxis = LayoutAxis.Y;
        DescribeTrack(track, y: true);
        double total = rows.Total;
        double range = total - height;   // pixels the top can travel
        if (range <= 0) return;

        Box before = runtime.BoxFromId(id + "-before", InteractionKinds.LeftPress, track);
        Box thumb = runtime.BoxFromId(id + "-thumb", InteractionKinds.LeftPress, track);
        Box after = runtime.BoxFromId(id + "-after", InteractionKinds.LeftPress, track);

        double shown = rows.Top(pt.Idx) + pt.Px + pt.Off;
        Interaction drag = runtime.InteractionsFor(thumb);
        if (drag.Pressed) pt.Grab = (float)shown;
        if (drag.Pressed || drag.Dragging)
        {
            float free = MathF.Max(1f, track.Extent.Y - thumb.Extent.Y);
            double to = Math.Clamp(pt.Grab + drag.DragDeltaY / free * range, 0, range);
            (pt.Idx, pt.Px) = rows.Locate(to);
            pt.Off = 0f;
            shown = to;
        }
        if (runtime.InteractionsFor(before).Pressed) Page(ref pt, rows, -height);
        if (runtime.InteractionsFor(after).Pressed) Page(ref pt, rows, height);

        float share = MathF.Min(1f, MathF.Max((float)(height / total), MinThumb / MathF.Max(1f, track.Extent.Y)));
        float t = (float)Math.Clamp(shown / range, 0, 1);
        Share(before, y: true, t * (1f - share));
        Share(thumb, y: true, share);
        Share(after, y: true, (1f - t) * (1f - share));
        thumb.Style.CornerRadius = Thickness / 2f;
        thumb.Style.Draw = DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects;
    }

    private static void Page(ref ScrollPt pt, RowHeights rows, float by)
    {
        double from = rows.Top(pt.Idx) + pt.Px;
        (pt.Idx, pt.Px) = rows.Locate(Math.Max(0, from + by));
        pt.Off -= by;
    }
}
