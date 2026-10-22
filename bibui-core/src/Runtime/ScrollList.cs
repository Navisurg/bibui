using System.Numerics;
using Bibui.Core;
using static Bibui.Core.Runtime.Scrollbar;

namespace Bibui.Core.Runtime;

/// <summary>
/// Where a scroll list is scrolled to, counted in rows, and which rows are in
/// sight. The caller keeps it between frames, beside the rows it scrolls, and
/// hands it to <see cref="ScrollList.BeginScrollList"/> and
/// <see cref="ScrollList.EndScrollList"/> by reference: it is all the
/// list needs from one frame to the next.
///
/// <see cref="Idx"/> and <see cref="Px"/> are the target: where the list comes
/// to rest. <see cref="Off"/> is how far, in rows, the list is still shown from
/// it, and eases to 0, so the top row shown is <c>Idx + Px / pitch + Off</c>.
/// Move the target with <see cref="Target"/>, which keeps what is shown where
/// it is and lets the list ease there; setting <see cref="Off"/> to 0 as well
/// jumps instead.
///
/// In a list whose rows have heights of their own (the overload of
/// <see cref="ScrollList.BeginScrollList(BibuiRuntime, string, ref ScrollPt, double[], int)"/>
/// that takes their tops), <see cref="Off"/> is in pixels, not rows, so the top
/// shown is <c>tops[Idx] + Px + Off</c>.
/// </summary>
public struct ScrollPt
{
    /// <summary>The target: the row at the top once the list settles.</summary>
    public int Idx;

    /// <summary>The target: how far into that row, in pixels, 0 ≤ Px &lt; pitch.</summary>
    public float Px;

    /// <summary>Rows still to travel to the target. Eased to 0 once a frame by the list.</summary>
    public float Off;

    /// <summary>The first row in sight: build rows <see cref="First"/> up to, not including, <see cref="Last"/>. Set by Begin.</summary>
    public int First { readonly get; internal set; }

    /// <summary>One past the last row in sight. Set by Begin.</summary>
    public int Last { readonly get; internal set; }

    /// <summary>How many whole rows the view holds, as of last frame. Set by Begin.</summary>
    public int ViewRows { readonly get; internal set; }

    internal float Grab;      // the scrollbar's: the top row shown when its thumb was pressed
    internal long Stepped;    // the frame Off was last eased in, so a build run twice eases once
    internal float Pitch;     // Begin's, for End
    internal float MaxPx;     // Begin's, for End: the furthest the target goes
    internal double[]? Tops;  // Begin's, for End: the caller's tops of rows of their own heights; null for rows of one pitch
    internal int Count;       // Begin's, for End: how many rows Tops holds
    internal double MaxTop;   // Begin's, for End: the furthest the top goes, in pixels, for rows of their own heights

    /// <summary>
    /// Moves the target to row <paramref name="idx"/>, <paramref name="px"/>
    /// pixels into it, rows being <paramref name="pitch"/> apart, and keeps the
    /// rows shown where they are: <see cref="Off"/> takes up the difference, and
    /// the list eases to the new target from there.
    /// </summary>
    public void Target(int idx, float px, float pitch)
    {
        Off += (Idx - idx) + (Px - px) / pitch;
        Idx = idx;
        Px = px;
    }

    /// <summary>
    /// The same, in a list whose rows have heights of their own, starting at
    /// <paramref name="tops"/>: moves the target to row <paramref name="idx"/>,
    /// <paramref name="px"/> pixels into it, and <see cref="Off"/> takes up the
    /// pixels between, so the list eases there.
    /// </summary>
    public void Target(int idx, float px, double[] tops)
    {
        Off += (float)(tops[Math.Clamp(Idx, 0, tops.Length - 1)] + Px - (tops[idx] + px));
        Idx = idx;
        Px = px;
    }
}

/// <summary>
/// A vertical list of rows that all have the same height, of which only the
/// rows in sight are built. A list of a hundred thousand rows costs what its
/// screenful does.
///
/// It scrolls by rows, not by pixels. Every row is <c>pitch</c> apart, top to
/// top, so which rows are in sight follows from the <see cref="ScrollPt"/>
/// alone: the whole part of the top row picks them, and the view is offset by
/// the fraction. The view holds only those rows, and the bar beside it is sized
/// from the row count, not from the view's content.
///
/// <code>
/// runtime.BeginScrollList("log", ref scroll, pitch: 24f, count: lines.Count);
/// for (int i = scroll.First; i &lt; scroll.Last; i++) Row(lines[i]);
/// runtime.EndScrollList(ref scroll);
/// </code>
///
/// Rows of different heights take the other overload, with where each row
/// starts in place of the pitch: the caller works the heights out from what
/// the rows hold, before building them, and the list scrolls by pixels over
/// them, the same rows in sight and the same bar.
///
/// <code>
/// runtime.BeginScrollList("inbox", ref scroll, tops, messages.Count);
/// for (int i = scroll.First; i &lt; scroll.Last; i++) Message(messages[i], height: tops[i + 1] - tops[i]);
/// runtime.EndScrollList(ref scroll);
/// </code>
/// </summary>
public static class ScrollList
{
    // A view not laid out yet has no height. Build as if for a tall screen, so
    // its first frame is not a single row.
    private const float Unmeasured = 4096f;

    /// <summary>
    /// Builds the list named <paramref name="id"/>, filling the parent in scope
    /// with its view and the bar beside it, and pushes the view as the parent for
    /// the rows. Build rows <see cref="ScrollPt.First"/> up to
    /// <see cref="ScrollPt.Last"/>, each exactly <paramref name="pitch"/> tall,
    /// then call <see cref="EndScrollList"/>.
    ///
    /// Once a frame it eases <paramref name="pt"/> towards its target at
    /// <see cref="MotionRates.Follow"/>, lets the bar move it, and keeps the
    /// target between the first row at the top and the last row at the bottom.
    /// </summary>
    /// <param name="pitch">
    /// The distance from the top of one row to the top of the next: a row and
    /// anything between rows. The list assumes every row has this same pitch.
    /// </param>
    /// <param name="count">How many rows there are.</param>
    public static void BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        if (pitch <= 0f) throw new ArgumentOutOfRangeException(nameof(pitch), pitch, "Rows are a pitch apart, above zero.");

        var (area, view) = AreaAndView(runtime, id);

        // Measured from last frame's layout.
        float height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured;
        float maxPx = MathF.Max(0f, count * pitch - height);   // the last row flush with the bottom
        pt.ViewRows = (int)(height / pitch);
        (pt.Pitch, pt.MaxPx, pt.Tops) = (pitch, maxPx, null);

        // Ease, once a frame however often the build runs, and to half a pixel.
        if (pt.Stepped != runtime.FrameCount)
        {
            pt.Stepped = runtime.FrameCount;
            pt.Off = Ease.Exponential(pt.Off, 0f, runtime.Rates.Follow);
            if (MathF.Abs(pt.Off) * pitch <= 0.5f) pt.Off = 0f;
        }
        if (pt.Off != 0f) runtime.RequestFrame();

        // The bar, before the rows: what it does moves the rows chosen below.
        using (runtime.Parent(area))
            pt = runtime.Scrollbar(id + "-bar", pt, count, height / pitch);
        ClampTarget(ref pt, pitch, maxPx);

        // The rows in sight. Shown within range, so a list that shrank shows its end at once.
        float top = Math.Clamp(pt.Idx + pt.Px / pitch + pt.Off, 0f, maxPx / pitch);
        pt.First = Math.Clamp((int)top, 0, count);
        pt.Last = Math.Clamp(pt.First + (int)MathF.Ceiling(height / pitch) + 1, pt.First, count);
        view.ViewOffset = view.TargetViewOffset = new Vector2(0f, (top - pt.First) * pitch);

        runtime.Parent(view);   // EndScrollList pops it
    }

    /// <summary>
    /// Builds the list named <paramref name="id"/> whose rows have heights of
    /// their own, as the pitch overload builds one whose rows share one: fills
    /// the parent in scope with the view and the bar beside it, and pushes the
    /// view for the rows. Build rows <see cref="ScrollPt.First"/> up to
    /// <see cref="ScrollPt.Last"/>, row <c>i</c> exactly
    /// <c>tops[i + 1] - tops[i]</c> tall, then call <see cref="EndScrollList"/>.
    ///
    /// <para>The heights are the caller's, known before the rows are built: what
    /// the rows hold decides them (a line height per line, and padding), so the
    /// caller works them out once and again only when the rows or the fonts
    /// change. The list reads them and measures nothing.</para>
    ///
    /// <para>It scrolls by pixels and keeps its place by a row: the target is row
    /// <see cref="ScrollPt.Idx"/>, <see cref="ScrollPt.Px"/> into it, so when the
    /// rows above it change height that row stays at the top.
    /// <see cref="ScrollPt.Off"/> is in pixels here.</para>
    /// </summary>
    /// <param name="tops">
    /// Where each row starts, from the top of row 0, and at <c>tops[count]</c>
    /// where the last one ends: <c>tops[0]</c> is 0 and no top is above the one
    /// before. The caller's, read by the list until End; it may be longer than
    /// <c>count + 1</c>.
    /// </param>
    /// <param name="count">How many rows there are.</param>
    public static void BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, double[] tops, int count)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(tops);
        if (count < 0 || tops.Length <= count) throw new ArgumentOutOfRangeException(nameof(tops), tops.Length, "A list of count rows has count + 1 tops: where each starts, and where the last ends.");

        var (area, view) = AreaAndView(runtime, id);

        // Measured from last frame's layout.
        float height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured;
        (pt.Tops, pt.Count, pt.MaxTop) = (tops, count, Math.Max(0, tops[count] - height));   // the last row flush with the bottom
        ClampTarget(ref pt);   // for the bar: the list may have shrunk under the target

        // Ease, once a frame however often the build runs, and to half a pixel.
        if (pt.Stepped != runtime.FrameCount)
        {
            pt.Stepped = runtime.FrameCount;
            pt.Off = Ease.Exponential(pt.Off, 0f, runtime.Rates.Follow);
            if (MathF.Abs(pt.Off) <= 0.5f) pt.Off = 0f;
        }
        if (pt.Off != 0f) runtime.RequestFrame();

        // The bar, before the rows: what it does moves the rows chosen below.
        using (runtime.Parent(area))
            TopsScrollbar(runtime, id + "-bar", ref pt, height);
        ClampTarget(ref pt);

        // The rows in sight: from the row holding the top pixel to the last that
        // starts above the bottom one.
        double top = Math.Clamp(tops[pt.Idx] + pt.Px + pt.Off, 0, pt.MaxTop);
        int first = RowAt(tops, count, top), last = first;
        while (last < count && tops[last] < top + height) last++;
        (pt.First, pt.Last, pt.ViewRows) = (first, last, last - first);
        view.ViewOffset = view.TargetViewOffset = new Vector2(0f, (float)(top - tops[first]));

        runtime.Parent(view);   // EndScrollList pops it
    }

    // The area lays the view and its bar out side by side. The view claims
    // the wheel without moving on it: End turns the wheel into rows.
    private static (Box Area, Box View) AreaAndView(BibuiRuntime runtime, string id)
    {
        Box area = runtime.BoxFromId(id + "-area");
        area.ChildLayoutAxis = LayoutAxis.X;
        (area.SizeX, area.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip, area);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        view.AllowOverflowY = true;
        return (area, view);
    }

    /// <summary>
    /// Pops the view <see cref="BeginScrollList"/> pushed, the parent in scope,
    /// then takes the wheel over it, after the rows so they have it first, and
    /// moves the target by its pixels.
    /// </summary>
    public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ParentInScope is not Box { Claims: var claims } view || (claims & InteractionKinds.Scroll) == 0)
            throw new InvalidOperationException("EndScrollList pops the list's view, and the parent in scope is not one: a parent pushed inside the list was not popped.");
        runtime.EndParent(view);

        float dy = runtime.InteractionsFor(view).ScrollY;
        if (dy == 0f) return;
        if (pt.Tops is not null) { Move(ref pt, dy); return; }
        float px = pt.Px + dy;
        int carry = (int)MathF.Floor(px / pt.Pitch);
        pt.Target(pt.Idx + carry, px - carry * pt.Pitch, pt.Pitch);
        ClampTarget(ref pt, pt.Pitch, pt.MaxPx);
    }

    // The target kept between the first row at the top and the last row at the
    // bottom, holding what is shown.
    private static void ClampTarget(ref ScrollPt pt, float pitch, float maxPx)
    {
        if (pt.Idx < 0 || (pt.Idx == 0 && pt.Px < 0f)) pt.Target(0, 0f, pitch);
        else if (pt.Idx * pitch + pt.Px > maxPx)
        {
            int idx = (int)(maxPx / pitch);
            pt.Target(idx, maxPx - idx * pitch, pitch);
        }
    }

    // ── Rows of their own heights ────────────────────────────────────────────

    // The target, in a list of tops, kept between the first row at the top and
    // the last row at the bottom, and on the row its pixel is in, holding what
    // is shown. A target past the rows (the list shrank) goes to the end at once.
    private static void ClampTarget(ref ScrollPt pt)
    {
        double[] tops = pt.Tops!;
        int count = pt.Count;
        if (count == 0) { (pt.Idx, pt.Px, pt.Off) = (0, 0f, 0f); return; }
        bool onRow = pt.Idx >= 0 && pt.Idx < count;
        double at = onRow ? tops[pt.Idx] + pt.Px : pt.Idx < 0 ? 0 : tops[count];
        double to = Math.Clamp(at, 0, pt.MaxTop);
        if (onRow && to == at && pt.Px >= 0f && (pt.Px == 0f || pt.Px < tops[pt.Idx + 1] - tops[pt.Idx])) return;
        int row = RowAt(tops, count, to);
        pt.Off = onRow ? pt.Off + (float)(at - to) : 0f;
        (pt.Idx, pt.Px) = (row, (float)(to - tops[row]));
    }

    // Moves the target by pixels, within the rows, and Off takes them up so
    // what is shown eases there: the wheel and the bar's pages.
    private static void Move(ref ScrollPt pt, double by)
    {
        double[] tops = pt.Tops!;
        double from = tops[pt.Idx] + pt.Px;
        double to = Math.Clamp(from + by, 0, pt.MaxTop);
        int row = RowAt(tops, pt.Count, to);
        pt.Off += (float)(from - to);
        (pt.Idx, pt.Px) = (row, (float)(to - tops[row]));
    }

    // The row whose span holds pixel y: the last row starting at or above it,
    // so a row of no height is passed over.
    private static int RowAt(double[] tops, int count, double y)
    {
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (tops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return Math.Max(0, lo);
    }

    // The bar for a list of tops: the pitch bar's, in pixels. The thumb is the
    // view's share of the whole height, placed by the top pixel shown; dragging
    // moves that pixel by the thumb's travel, at once; the spaces page by a view.
    private static void TopsScrollbar(BibuiRuntime runtime, string id, ref ScrollPt pt, float height)
    {
        Box track = runtime.BoxFromId(id);
        track.ChildLayoutAxis = LayoutAxis.Y;
        DescribeTrack(track, y: true);   // its lane even with nothing to scroll
        double[] tops = pt.Tops!;
        double total = tops[pt.Count];
        double range = total - height;   // pixels the top can travel
        if (range <= 0) return;

        Box before = runtime.BoxFromId(id + "-before", InteractionKinds.LeftPress, track);
        Box thumb = runtime.BoxFromId(id + "-thumb", InteractionKinds.LeftPress, track);
        Box after = runtime.BoxFromId(id + "-after", InteractionKinds.LeftPress, track);

        double shown = tops[pt.Idx] + pt.Px + pt.Off;
        Interaction drag = runtime.InteractionsFor(thumb);
        if (drag.Pressed) pt.Grab = (float)shown;
        if (drag.Pressed || drag.Dragging)
        {
            float free = MathF.Max(1f, track.Extent.Y - thumb.Extent.Y);
            shown = Math.Clamp(pt.Grab + drag.DragDeltaY / free * range, 0, range);
            int row = RowAt(tops, pt.Count, shown);
            (pt.Idx, pt.Px, pt.Off) = (row, (float)(shown - tops[row]), 0f);
        }
        if (runtime.InteractionsFor(before).Pressed) Move(ref pt, -height);
        if (runtime.InteractionsFor(after).Pressed) Move(ref pt, height);

        float share = MathF.Min(1f, MathF.Max((float)(height / total), MinThumb / MathF.Max(1f, track.Extent.Y)));
        float t = (float)Math.Clamp(shown / range, 0, 1);
        Share(before, y: true, t * (1f - share));
        Share(thumb, y: true, share);
        Share(after, y: true, (1f - t) * (1f - share));
        thumb.Style.CornerRadius = Thickness / 2f;
        thumb.Style.Draw = DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects;
    }

    /// <summary>
    /// Builds the bar named <paramref name="id"/> for a list of
    /// <paramref name="count"/> rows with <paramref name="viewRows"/> in view,
    /// scrolled to <paramref name="pt"/>, and returns <paramref name="pt"/> as
    /// the bar moved it.
    ///
    /// The thumb is <paramref name="viewRows"/> of <paramref name="count"/> of
    /// the track, placed by the top row shown. Dragging it moves the top row by
    /// as many rows as the thumb travels from where it was pressed, at once and
    /// on a whole row. Pressing either space pages by the rows in view, eased.
    /// The caller keeps the result in range; <see cref="BeginScrollList"/> does.
    /// </summary>
    public static ScrollPt Scrollbar(this BibuiRuntime runtime, string id, ScrollPt pt, int count, float viewRows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        Box track = runtime.BoxFromId(id);
        track.ChildLayoutAxis = LayoutAxis.Y;
        DescribeTrack(track, y: true);   // its lane even with nothing to scroll
        float range = count - viewRows;  // rows the top can travel
        if (range <= 0f) return pt;

        Box before = runtime.BoxFromId(id + "-before", InteractionKinds.LeftPress, track);
        Box thumb = runtime.BoxFromId(id + "-thumb", InteractionKinds.LeftPress, track);
        Box after = runtime.BoxFromId(id + "-after", InteractionKinds.LeftPress, track);

        Interaction drag = runtime.InteractionsFor(thumb);
        if (drag.Pressed) pt.Grab = pt.Idx + pt.Off;
        if (drag.Pressed || drag.Dragging)
        {
            float free = MathF.Max(1f, track.Extent.Y - thumb.Extent.Y);
            float top = pt.Grab + drag.DragDeltaY / free * range;
            (pt.Idx, pt.Px, pt.Off) = ((int)MathF.Round(Math.Clamp(top, 0f, range)), 0f, 0f);
        }
        int page = Math.Max(1, (int)viewRows);
        if (runtime.InteractionsFor(before).Pressed) (pt.Idx, pt.Off) = (pt.Idx - page, pt.Off + page);
        if (runtime.InteractionsFor(after).Pressed) (pt.Idx, pt.Off) = (pt.Idx + page, pt.Off - page);

        float share = MathF.Min(1f, MathF.Max(viewRows / count, MinThumb / MathF.Max(1f, track.Extent.Y)));
        float t = Math.Clamp((pt.Idx + pt.Off) / range, 0f, 1f);
        Share(before, y: true, t * (1f - share));
        Share(thumb, y: true, share);
        Share(after, y: true, (1f - t) * (1f - share));
        thumb.Style.CornerRadius = Thickness / 2f;
        thumb.Style.Draw = DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects;

        return pt;
    }
}
