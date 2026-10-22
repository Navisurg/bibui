using System.Numerics;
using Bibui.Core;

namespace Bibui.Core.Runtime;

/// <summary>
/// Where a scroll list is scrolled to: the row at the top, and how far past
/// its top the view is. The caller keeps it between frames, beside the rows it
/// scrolls, and hands it by reference to a list's Begin and End and to its
/// <see cref="ScrollList.Scrollbar"/>; it is all a list keeps from one frame
/// to the next. Code of the caller's may set <see cref="Idx"/> to show a row.
///
/// <see cref="RowScrollList"/> eases: <see cref="Off"/> is the rows the top
/// shown is still from the target, eased to 0, so moving the target with
/// <see cref="Target"/> glides there. <see cref="ScrollList"/> does not ease:
/// a target moved is shown at once, and by rows <see cref="Off"/> holds the
/// wheel not yet a whole row step.
/// </summary>
public struct ScrollPt
{
    /// <summary>The row at the top once the list comes to rest.</summary>
    public int Idx;

    /// <summary>
    /// How far past row <see cref="Idx"/>'s top the view rests, in pixels. By
    /// rows it is 0, the view resting on a row's top; a <see cref="ScrollList"/>
    /// takes it below 0 for a gap under its last row, to go back over.
    /// </summary>
    public float Px;

    /// <summary>
    /// For a <see cref="RowScrollList"/>, the rows the top shown still is from
    /// the target, eased to 0 once a frame. For a <see cref="ScrollList"/> by
    /// rows, the wheel not yet a whole row step.
    /// </summary>
    public float Off;

    /// <summary>The first row in sight, as the deprecated pitch overload of <see cref="ScrollList.BeginScrollList(BibuiRuntime, string, ref ScrollPt, float, int)"/> sets it.</summary>
    [Obsolete("Set only by the deprecated BeginScrollList(id, ref pt, pitch, count). BeginRowScrollList returns the rows in sight. Removed in 2.0.")]
    public int First { readonly get; internal set; }

    /// <summary>One past the last row in sight, as the deprecated pitch overload sets it.</summary>
    [Obsolete("Set only by the deprecated BeginScrollList(id, ref pt, pitch, count). BeginRowScrollList returns the rows in sight. Removed in 2.0.")]
    public int Last { readonly get; internal set; }

    /// <summary>How many whole rows the view held, as the deprecated pitch overload sets it.</summary>
    [Obsolete("Set only by the deprecated BeginScrollList(id, ref pt, pitch, count). Removed in 2.0.")]
    public int ViewRows { readonly get; internal set; }

    internal long Stepped;    // RowScrollList's: the frame Off was last eased in, so a build run twice eases once
    internal float Grab;      // the deprecated list's bar: the top row shown when its thumb was pressed
    internal float Pitch;     // the deprecated list's, Begin's for End
    internal float MaxPx;     // the deprecated list's, Begin's for End: the furthest the target goes

    /// <summary>
    /// Moves the target to row <paramref name="idx"/>, at its top, keeping the
    /// row shown where it is, so a <see cref="RowScrollList"/> eases there (by
    /// pixels, from the top of the row it was part way into). A
    /// <see cref="ScrollList"/>, which does not ease, shows it at once.
    /// </summary>
    public void Target(int idx)
    {
        Off += Idx - idx;
        (Idx, Px) = (idx, 0f);
    }

    /// <summary>
    /// Moves the target to row <paramref name="idx"/>, <paramref name="px"/>
    /// pixels into it, rows being <paramref name="pitch"/> apart, keeping the
    /// rows shown where they are, so a <see cref="RowScrollList"/> eases there.
    /// </summary>
    public void Target(int idx, float px, float pitch)
    {
        Off += (Idx - idx) + (Px - px) / pitch;
        Idx = idx;
        Px = px;
    }
}

/// <summary>
/// What a scroll list scrolls by, and so where it may come to rest. Its value
/// is the share of the wheel's pixels shown as they come: 0 or 1, used as a
/// scale in the lists' arithmetic.
/// </summary>
public enum ScrollBy
{
    /// <summary>Whole rows: the wheel moves the list a row at a time, and it rests on a row's top.</summary>
    Rows = 0,

    /// <summary>Pixels: the wheel slides the rows as it turns, and the list may rest part way into a row.</summary>
    Pixels = 1,
}

/// <summary>
/// One frame's loop over a <see cref="ScrollList"/>: which row's height it
/// wants next, whether it wants another, and how far the building has got.
/// <see cref="ScrollList.BeginScrollList"/> returns it.
/// <code>
/// var rows = runtime.BeginScrollList("inbox", ref scroll, messages.Count, ScrollBy.Pixels, rowStep: 40f);
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
    internal float TopHeight;  // the height of the row at the top, the first built
    internal float Height;     // the view's, as last laid out
    internal int Count;
    internal Box View;         // the list's, for its offset
    internal float S;          // (float)ScrollBy: 0 by rows, 1 by pixels
    internal float RowStep;    // by rows, the wheel's pixels that move the list a row

    /// <summary>
    /// Row <see cref="Row"/> is <paramref name="height"/> tall: moves
    /// <paramref name="pt"/> onto it or past it as the top needs, and says
    /// whether to build it now, exactly <paramref name="height"/> tall. Rows
    /// are asked for in the order the top needs: up while it is above row
    /// Idx, then down from the row at the top.
    /// </summary>
    public bool Next(ref ScrollPt pt, float height)
    {
        // Up: the top is above row Idx, and this is the row above. Onto it;
        // the row above that next if the top is above it too, else the way
        // down from it, where it is built.
        if (pt.Px < 0f)
        {
            pt.Idx = Row;
            pt.Px += height;
            if (pt.Idx == 0) pt.Px = MathF.Max(pt.Px, 0f);   // nothing above the first row
            if (pt.Px >= 0f) pt.Px *= S;   // by rows the view rests on a row's top
            Row = pt.Px < 0f ? pt.Idx - 1 : pt.Idx;
            return false;
        }

        // Down: nothing built yet, and the top is past this row: past it, so
        // the next is the top. Never past the last row.
        if (Built == 0 && pt.Px >= height && pt.Idx < Count - 1)
        {
            pt.Idx++;
            pt.Px -= height;
            Row = pt.Idx;
            return false;
        }

        // In view: built. The first sets the view's offset, how far past its
        // top the view is.
        if (Built == 0)
        {
            View.ViewOffset = View.TargetViewOffset = new Vector2(0f, pt.Px);
            Y = -pt.Px;
            TopHeight = height;
        }
        Y += height;
        Built++;
        Row++;
        More = Row < Count && Y < Height;
        return true;
    }
}

/// <summary>
/// A vertical list of rows of any height, of which only the rows in sight are
/// built. It holds no heights: one loop hands them in as it needs them
/// (<see cref="ScrollRows.Next"/>), and the sign of <see cref="ScrollPt.Px"/>
/// says which way to go: up while the top is above row Idx, past the rows
/// scrolled past, then down through the view, building what is in it. Each
/// height is asked of the caller as the list reaches the row, so it must be
/// known before the row is built (from what the row holds: a line height per
/// line, and padding). Rows all of one height take <see cref="RowScrollList"/>,
/// which needs no heights.
///
/// By pixels the wheel's pixels move the view, exactly through the heights of
/// the rows it passes, however tall: a row taller than the view scrolls
/// through like any other. By rows they are counted in row steps, the caller's
/// pixels per row, and move the list a row a step: no height is needed to
/// scroll, and the view rests on a row's top.
///
/// It does not ease: the wheel and a target moved are shown on the next frame.
/// </summary>
public static partial class ScrollList
{
    // A view not laid out yet has no height. Build as if for a tall screen, so
    // its first frame is not a single row.
    private const float Unmeasured = 4096f;

    /// <summary>The width of the lane <see cref="Scrollbar"/> takes beside a list.</summary>
    public const float BarLane = 16f;

    // The thumb's thickness in its lane, and the least it is along it.
    private const float BarThickness = 10f, BarMinThumb = 24f;

    /// <summary>
    /// Builds the view named <paramref name="id"/>, filling what the parent
    /// leaves, and pushes it for the rows. Returns the loop: while
    /// <see cref="ScrollRows.More"/>, hand <see cref="ScrollRows.Next"/> the
    /// height of <see cref="ScrollRows.Row"/>, and build that row when it says
    /// so; then call <see cref="EndScrollList"/>. Ask the count first: no
    /// height is asked past it.
    /// </summary>
    /// <param name="by">Whether the wheel moves the list by pixels or by rows.</param>
    /// <param name="rowStep">
    /// By rows, the wheel's pixels that move the list a row. What suits depends
    /// on the rows and the device, so it is the caller's; a notch of a mouse
    /// wheel is 32.
    /// </param>
    public static ScrollRows BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, ScrollBy by, float rowStep)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        if (!(rowStep > 0f)) throw new ArgumentOutOfRangeException(nameof(rowStep), rowStep, "A row step is some pixels of wheel, above zero.");

        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (Size.Fill, Size.Fill);
        view.AllowOverflowY = true;
        view.ViewOffset = view.TargetViewOffset = Vector2.Zero;   // until the first row built sets it

        // On a row of the list, and nothing above the first. By rows the view
        // rests on a row's top (Px below 0 is a gap to go back over), and Off
        // holds only the wheel not yet a step: whole rows there are a target
        // moved, which the list does not ease to. By pixels there is no Off.
        float s = (float)by;
        pt.Idx = Math.Clamp(pt.Idx, 0, Math.Max(0, count - 1));
        if (pt.Idx == 0) pt.Px = MathF.Max(pt.Px, 0f);
        pt.Px -= (1f - s) * MathF.Max(pt.Px, 0f);
        pt.Off = (1f - s) * (pt.Off - MathF.Truncate(pt.Off));

        runtime.BeginParent(view);
        return new ScrollRows
        {
            Row = pt.Px < 0f ? pt.Idx - 1 : pt.Idx,
            More = count > 0,
            Height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured,   // last frame's
            Count = count,
            View = view,
            S = s,
            RowStep = rowStep,
        };
    }

    /// <summary>
    /// Pops the view <see cref="BeginScrollList"/> pushed, the parent in scope,
    /// then spends the wheel over it. By pixels it goes into
    /// <see cref="ScrollPt.Px"/>, for the next frame's loop to go up or down
    /// by. By rows it is counted in row steps in <see cref="ScrollPt.Off"/>,
    /// and whole steps move <see cref="ScrollPt.Idx"/> a row each. Once the last
    /// row was built the room left under the view is exact: the wheel goes no
    /// further than it allows, and a gap there brings Px back by it.
    /// </summary>
    public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, in ScrollRows rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ParentInScope is not Box { Claims: var claims } view || (claims & InteractionKinds.Scroll) == 0)
            throw new InvalidOperationException("EndScrollList pops the list's view, and the parent in scope is not one: a parent pushed inside the list was not popped.");
        runtime.EndParent(view);
        float dy = runtime.InteractionsFor(view).ScrollY;

        // The room left under the view, exact once the last row was built:
        // below 0 a gap, to go back over.
        bool sawEnd = rows.Built > 0 && pt.Idx + rows.Built == rows.Count;
        float room = sawEnd ? rows.Y - rows.Height : float.PositiveInfinity;

        // By pixels the wheel goes into Px, as far as the room allows; by rows
        // Px rests at 0, and takes only a gap. By rows the wheel goes into Off,
        // in steps, and each whole step moves Idx a row; by pixels Off stays 0.
        pt.Px += MathF.Min(rows.S * dy, room);
        pt.Off += (1f - rows.S) * dy / rows.RowStep;

        // At the end, a step down only if the row leaving the top fits in the
        // room, so the rows below still fill the view; the wheel past that is
        // dropped.
        if (sawEnd && room < rows.TopHeight) pt.Off = MathF.Min(pt.Off, 0f);
        int steps = (int)MathF.Truncate(pt.Off);
        if (sawEnd) steps = Math.Min(steps, 1);
        pt.Idx += steps;
        pt.Off -= steps;

        // The ends: on a row of the list, and nothing above the first.
        if (pt.Idx <= 0) (pt.Idx, pt.Off, pt.Px) = (0, MathF.Max(pt.Off, 0f), MathF.Max(pt.Px, 0f));
        pt.Idx = Math.Min(pt.Idx, Math.Max(0, rows.Count - 1));
    }

    /// <summary>
    /// Builds the bar named <paramref name="id"/> in the parent in scope, beside
    /// a list of <paramref name="count"/> rows at <paramref name="pt"/> that
    /// showed rows <paramref name="first"/> up to <paramref name="last"/> this
    /// frame: a <see cref="ScrollList"/> or a <see cref="RowScrollList"/>. Lay
    /// the list and the bar out in a row; the bar takes <see cref="BarLane"/>.
    ///
    /// It knows rows, not their heights: the thumb is the share of the rows
    /// shown, placed by the rows scrolled past against those still to come, so
    /// it is at the top at the first row and at the bottom at the end. A press
    /// on the lane, or a drag from the lane or the thumb, sets
    /// <see cref="ScrollPt.Idx"/> to the row that puts the thumb's centre under
    /// the pointer, at once; a press on the thumb alone moves nothing.
    /// </summary>
    public static void Scrollbar(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        Box lane = runtime.BoxFromId(id, InteractionKinds.LeftPress);

        // ── Track: where the thumb is, in rows ───────────────────────────────
        // Above: the rows scrolled past, as shown, easing. Below: the rows not
        // yet shown. So each is 0 at its end of the list. The thumb is the
        // share of the rows shown, along the lane as it was last laid out.
        // NOTE(k): rows, not heights: by pixels the thumb moves a row at a
        // time, and a row of any height is one row of the bar.
        float above = MathF.Max(0f, pt.Idx + pt.Off);
        float below = count - last;
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
        (pt.Idx, pt.Off, pt.Px) = ((int)MathF.Round(at * range), 0f, 0f);
    }
}
