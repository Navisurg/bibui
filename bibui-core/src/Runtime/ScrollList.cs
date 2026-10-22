using System.Numerics;
using Bibui.Core;

namespace Bibui.Core.Runtime;

/// <summary>
/// Where a scroll list is scrolled to, in pixels from the top of a row. The
/// caller keeps it between frames, beside the rows it scrolls, and hands it by
/// reference to a list's Begin and End and to its <see cref="ScrollList.Scrollbar"/>:
/// it is all a list keeps from one frame to the next.
///
/// <see cref="Idx"/> and <see cref="Px"/> are the target, where the list
/// comes to rest: <see cref="Px"/> pixels below the top of row <see cref="Idx"/>.
/// <see cref="Off"/> is how far what is shown still is from it, in pixels,
/// eased to 0 once a frame: the top shown is <c>Px + Off</c> below the top of
/// row Idx. A list moving the target keeps what is shown where it was, by
/// putting the difference into Off, so it eases there. Setting
/// <see cref="Idx"/> (with Px and Off 0), or <see cref="Target"/>, shows a row
/// at once.
/// </summary>
public struct ScrollPt
{
    /// <summary>The row the position is measured from: at rest, the row at the top.</summary>
    public int Idx;

    /// <summary>
    /// The target: how far below row <see cref="Idx"/>'s top the top of the
    /// view comes to rest, in pixels. At rest, inside row Idx. A
    /// <see cref="RowScrollList"/> by rows rests on a row's top and holds the
    /// wheel not yet a row here, unseen.
    /// </summary>
    public float Px;

    /// <summary>How far, in pixels, what is shown still is from the target. Eased to 0 once a frame.</summary>
    public float Off;

    internal long Stepped;    // the frame Off was last eased in, so a build run twice eases once

    /// <summary>Shows row <paramref name="idx"/> at the top, at once.</summary>
    public void Target(int idx) => (Idx, Px, Off) = (idx, 0f, 0f);

    // Eases Off a frame, once a frame however often the build runs, and to
    // half a pixel; asks for another frame while it moves.
    internal void Ease(BibuiRuntime runtime)
    {
        if (Stepped != runtime.FrameCount)
        {
            Stepped = runtime.FrameCount;
            Off = Bibui.Core.Ease.Exponential(Off, 0f, runtime.Rates.Follow);
            if (MathF.Abs(Off) <= 0.5f) Off = 0f;
        }
        if (Off != 0f) runtime.RequestFrame();
    }
}

/// <summary>
/// One frame's loop over a <see cref="ScrollList"/>: which row's height it
/// wants next, whether it wants another, and how far the building has got.
/// <see cref="ScrollList.BeginScrollList"/> returns it.
/// <code>
/// var rows = runtime.BeginScrollList("inbox", ref scroll, messages.Count);
/// for (int i = rows.Row; rows.More; i = rows.Row)
///     if (rows.Next(ref scroll, HeightOf(i))) Message(i);
/// runtime.EndScrollList(ref scroll, rows);
/// </code>
/// </summary>
public struct ScrollRows
{
    /// <summary>The row whose height <see cref="Next"/> wants next.</summary>
    public int Row;

    /// <summary>Another row is wanted: <see cref="Row"/> is in the list, and the view is not full.</summary>
    public bool More;

    /// <summary>The rows built so far: <see cref="ScrollPt.Idx"/> up to Idx + Built.</summary>
    public int Built;

    internal float Y;          // where the next row built starts, from the top of the view
    internal float Height;     // the view's, as last laid out
    internal int Count;
    internal Box View;         // the list's, for its offset

    /// <summary>
    /// Row <see cref="Row"/> is <paramref name="height"/> tall: moves
    /// <paramref name="pt"/> onto it or past it as the top shown needs, and
    /// says whether to build it now, exactly <paramref name="height"/> tall.
    /// Rows are asked for in the order the top needs: up while it is above row
    /// Idx, then down from the row at the top.
    /// </summary>
    public bool Next(ref ScrollPt pt, float height)
    {
        // Up: the top shown is above row Idx, and this is the row above. Onto
        // it, the target measured from it too; the row above that next if the
        // top is above it as well, else the way down from it, where it is built.
        if (pt.Px + pt.Off < 0f)
        {
            pt.Idx = Row;
            pt.Px += height;
            if (pt.Idx == 0) Top(ref pt);
            Row = pt.Px + pt.Off < 0f ? pt.Idx - 1 : pt.Idx;
            return false;
        }

        // Down: nothing built yet, and the top shown is past this row: past it,
        // so the next is the top. Never past the last row.
        if (Built == 0 && pt.Px + pt.Off >= height && pt.Idx < Count - 1)
        {
            pt.Idx++;
            pt.Px -= height;
            Row = pt.Idx;
            return false;
        }

        // In view: built. The first sets the view's offset, how far past its
        // top the view shows.
        if (Built == 0)
        {
            View.ViewOffset = View.TargetViewOffset = new Vector2(0f, pt.Px + pt.Off);
            Y = -(pt.Px + pt.Off);
        }
        Y += height;
        Built++;
        Row++;
        More = Row < Count && Y < Height;
        return true;
    }

    // At row 0 nothing is above: neither the target nor what is shown.
    internal static void Top(ref ScrollPt pt)
    {
        pt.Px = MathF.Max(pt.Px, 0f);
        pt.Off = MathF.Max(pt.Px + pt.Off, 0f) - pt.Px;
    }
}

/// <summary>
/// A vertical list of rows of any height, scrolled by pixels, of which only
/// the rows in sight are built. It holds no heights: one loop hands them in as
/// it needs them (<see cref="ScrollRows.Next"/>), and the top shown says which
/// way to go: up while it is above row Idx, past the rows scrolled past, then
/// down through the view, building what is in it. Each height is asked of the
/// caller as the list reaches the row, so it must be known before the row is
/// built (from what the row holds: a line height per line, and padding).
///
/// The wheel moves the target exactly through the heights of the rows it
/// passes, however tall: a row taller than the view scrolls through like any
/// other. What is shown eases there. At the end the last row comes to rest on
/// the bottom. Rows all of one height, or a list to step through by rows,
/// take <see cref="RowScrollList"/>.
/// </summary>
public static class ScrollList
{
    // A view not laid out yet has no height. Build as if for a tall screen, so
    // its first frame is not a single row.
    private const float Unmeasured = 4096f;

    /// <summary>The width of the lane <see cref="Scrollbar"/> takes beside a list.</summary>
    public const float BarLane = 12f;

    // The thumb's thickness in its lane, and the least it is along it.
    private const float BarThickness = 6f, BarMinThumb = 24f;

    /// <summary>
    /// Builds the view named <paramref name="id"/>, filling what the parent
    /// leaves, eases <paramref name="pt"/> a frame, and pushes the view for
    /// the rows. Returns the loop: while <see cref="ScrollRows.More"/>, hand
    /// <see cref="ScrollRows.Next"/> the height of <see cref="ScrollRows.Row"/>,
    /// and build that row when it says so; then call <see cref="EndScrollList"/>.
    /// Ask the count first: no height is asked past it.
    /// </summary>
    public static ScrollRows BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (Size.Fill, Size.Fill);
        view.AllowOverflowY = true;
        view.ViewOffset = view.TargetViewOffset = Vector2.Zero;   // until the first row built sets it

        pt.Ease(runtime);
        pt.Idx = Math.Clamp(pt.Idx, 0, Math.Max(0, count - 1));
        if (pt.Idx == 0) ScrollRows.Top(ref pt);

        runtime.BeginParent(view);
        return new ScrollRows
        {
            Row = pt.Px + pt.Off < 0f ? pt.Idx - 1 : pt.Idx,
            More = count > 0,
            Height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured,   // last frame's
            Count = count,
            View = view,
        };
    }

    /// <summary>
    /// Pops the view <see cref="BeginScrollList"/> pushed, the parent in scope,
    /// then moves the target by the wheel over it, keeping what is shown where
    /// it is, so it eases there. Once the last row was built the room left
    /// under the view is exact: the target goes no further than to rest the
    /// last row on the bottom, and a gap shown there closes at once.
    /// </summary>
    public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, in ScrollRows rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ParentInScope is not Box { Claims: var claims } view || (claims & InteractionKinds.Scroll) == 0)
            throw new InvalidOperationException("EndScrollList pops the list's view, and the parent in scope is not one: a parent pushed inside the list was not popped.");
        runtime.EndParent(view);
        float dy = runtime.InteractionsFor(view).ScrollY;

        // The room left under the view as shown, exact once the last row was
        // built: below 0 a gap. The target's is that and what is still to ease.
        bool sawEnd = rows.Built > 0 && pt.Idx + rows.Built == rows.Count;
        float room = sawEnd ? rows.Y - rows.Height : float.PositiveInfinity;

        // The wheel moves the target, no further than the end; what is shown
        // stays put and eases there. A gap shown closes at once.
        dy = MathF.Min(dy, room + pt.Off);
        pt.Px += dy;
        pt.Off -= dy;
        if (room < 0f) pt.Off += room;

        pt.Idx = Math.Min(pt.Idx, Math.Max(0, rows.Count - 1));
        if (pt.Idx == 0) ScrollRows.Top(ref pt);
    }

    /// <summary>
    /// Builds the bar named <paramref name="id"/> in the parent in scope, beside
    /// a list of <paramref name="count"/> rows that showed rows
    /// <paramref name="first"/> up to <paramref name="last"/> this frame: a
    /// <see cref="ScrollList"/> or a <see cref="RowScrollList"/>. Lay the list
    /// and the bar out in a row; the bar takes <see cref="BarLane"/>.
    ///
    /// It knows rows, not their heights: the thumb is the share of the rows
    /// shown, placed by the rows above the first shown against those after the
    /// last, so it is at the top at the first row and at the bottom at the end.
    /// A press on the lane, or a drag from the lane or the thumb, shows the row
    /// that puts the thumb's centre under the pointer, at once; a press on the
    /// thumb alone moves nothing.
    /// </summary>
    public static void Scrollbar(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        Box lane = runtime.BoxFromId(id, InteractionKinds.LeftPress);

        // ── Track: where the thumb is, in rows ───────────────────────────────
        // Above: the rows above the first shown. Below: the rows after the last
        // shown. So each is 0 at its end of the list. The thumb is the share of
        // the rows shown, along the lane as it was last laid out.
        // NOTE(k): rows, not heights: the thumb moves a row at a time, and a
        // row of any height is one row of the bar.
        float above = first, below = count - last;
        float range = above + below;   // 0: all of it in view
        float t = range > 0f ? Math.Clamp(above / range, 0f, 1f) : 0f;
        float height = lane.Extent.Y;
        float size = Math.Clamp(height * (last - first) / Math.Max(1, count), MathF.Min(BarMinThumb, height), height);

        // ── UI: the lane, and the thumb floating in it ───────────────────────
        float inset = (BarLane - BarThickness) / 2f;
        (lane.SizeX, lane.SizeY) = (Size.Px(BarLane), Size.Fill);   // its lane even with nothing to scroll
        lane.Style.Padding = new EdgeInsets(0f, inset, 0f, inset);
        lane.Style.BackgroundColor = UiColor.Surface;   // a step below the thumb's SurfaceRaised
        lane.Style.HoverBackgroundColor = UiColor.SurfaceRaised;   // the bar comes forward under the pointer, eased
        lane.Style.CornerRadius = BarLane / 2f;
        if (range <= 0f) return;

        // The lane is the one box that takes the pointer, so it stays hot over
        // the thumb too. The thumb is drawn only: a step brighter than the lane
        // under the pointer, and again while held.
        Interaction onLane = runtime.InteractionsFor(lane);
        bool held = onLane.Pressed || lane.ActiveT >= 1f;   // pressed, and held since: the lane is the active box
        Box thumb = runtime.BoxFromId(id + "-thumb", parent: lane);
        (thumb.SizeX, thumb.SizeY) = (Size.Fill, Size.Px(size));
        thumb.FloatingY = true;
        thumb.FixedPosition = new Vector2(0f, t * (height - size));
        thumb.Style.CornerRadius = BarThickness / 2f;
        thumb.Style.BackgroundColor = held ? UiColor.SurfaceRaisedActive : onLane.Hovering ? UiColor.SurfaceRaisedHover : UiColor.SurfaceRaised;

        // A press on the lane, or a drag: the thumb's centre under the pointer,
        // as a share of its travel, and the row that share of the range is. A
        // press on the thumb, as it was drawn, moves nothing until the pointer
        // does (Dragging is the button held, moved or not).
        bool onThumb = onLane.Y >= thumb.Rect.Y && onLane.Y <= thumb.Rect.Y + thumb.Extent.Y;
        bool moved = onLane.Dragging && onLane.DragDeltaY != 0f;
        if (!(onLane.Pressed && !onThumb || moved)) return;
        float at = Math.Clamp((onLane.Y - lane.Rect.Y - size / 2f) / MathF.Max(1f, height - size), 0f, 1f);
        pt.Target((int)MathF.Round(at * range));
    }
}
