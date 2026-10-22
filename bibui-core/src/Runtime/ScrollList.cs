using System.Numerics;
using Bibui.Core;

namespace Bibui.Core.Runtime;

/// <summary>
/// Where a <see cref="ScrollList"/> or <see cref="RowScrollList"/> is
/// scrolled to: all it keeps between frames, kept by the caller. The target
/// is <see cref="Px"/> pixels below the top of row <see cref="Idx"/>; what is
/// shown is <see cref="Off"/> pixels from it, easing to 0.
/// </summary>
public struct ScrollPt
{
    /// <summary>The row the position is measured from: at rest, the row at the top.</summary>
    public int Idx;

    /// <summary>The target, in pixels below row <see cref="Idx"/>'s top. By rows, a <see cref="RowScrollList"/> holds the wheel not yet a row here, unseen.</summary>
    public float Px;

    /// <summary>How far, in pixels, what is shown still is from the target. Eased to 0 once a frame.</summary>
    public float Off;

    internal long Stepped;    // the frame Off was last eased in, so a build run twice eases once

    /// <summary>Shows row <paramref name="idx"/> at the top, at once.</summary>
    public void Target(int idx) => (Idx, Px, Off) = (idx, 0f, 0f);

    // Once a frame however often the build runs, to half a pixel.
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

/// <summary>One frame's loop over a <see cref="ScrollList"/>, from <see cref="ScrollList.BeginScrollList"/>.</summary>
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
    /// Row <see cref="Row"/> is <paramref name="height"/> tall: steps
    /// <paramref name="pt"/> onto it or past it, or says to build it, exactly
    /// that tall.
    /// </summary>
    public bool Next(ref ScrollPt pt, float height)
    {
        // Up: the top shown is above row Idx, and this is the row above.
        if (pt.Px + pt.Off < 0f)
        {
            pt.Idx = Row;
            pt.Px += height;
            if (pt.Idx == 0) Top(ref pt);
            Row = pt.Px + pt.Off < 0f ? pt.Idx - 1 : pt.Idx;
            return false;
        }

        // Down: nothing built yet, and the top shown is past this row.
        if (Built == 0 && pt.Px + pt.Off >= height && pt.Idx < Count - 1)
        {
            pt.Idx++;
            pt.Px -= height;
            Row = pt.Idx;
            return false;
        }

        // In view. The first row built sets the view's offset.
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
/// A vertical list of rows of their own heights, scrolled by pixels, of which
/// only those in sight are built. It holds no heights: the loop hands each in
/// as the list reaches the row, going up or down from the row at the top, so
/// the wheel moves exactly through rows of any height. Rows of one height take
/// <see cref="RowScrollList"/>.
/// <code>
/// var rows = runtime.BeginScrollList("inbox", ref scroll, messages.Count);
/// for (int i = rows.Row; rows.More; i = rows.Row)
///     if (rows.Next(ref scroll, HeightOf(i))) Message(i);
/// runtime.EndScrollList(ref scroll, rows);
/// </code>
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

    /// <summary>Builds and pushes the view named <paramref name="id"/>, filling the parent, and returns the loop.</summary>
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

    /// <summary>Pops the view and moves the target by the wheel over it, no further than to rest the last row on the bottom.</summary>
    public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, in ScrollRows rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ParentInScope is not Box { Claims: var claims } view || (claims & InteractionKinds.Scroll) == 0)
            throw new InvalidOperationException("EndScrollList pops the list's view, and the parent in scope is not one: a parent pushed inside the list was not popped.");
        runtime.EndParent(view);
        float dy = runtime.InteractionsFor(view).ScrollY;

        // The room under the view, exact once the last row was built; below 0 a gap.
        bool sawEnd = rows.Built > 0 && pt.Idx + rows.Built == rows.Count;
        float room = sawEnd ? rows.Y - rows.Height : float.PositiveInfinity;

        // What is shown stays put and eases to the target; a gap closes at once.
        dy = MathF.Min(dy, room + pt.Off);
        pt.Px += dy;
        pt.Off -= dy;
        if (room < 0f) pt.Off += room;

        pt.Idx = Math.Min(pt.Idx, Math.Max(0, rows.Count - 1));
        if (pt.Idx == 0) ScrollRows.Top(ref pt);
    }

    /// <summary>
    /// Builds the bar named <paramref name="id"/> beside a list that showed rows
    /// <paramref name="first"/> up to <paramref name="last"/> of <paramref name="count"/>
    /// this frame. It counts rows, not heights. A press or a drag on it shows
    /// the row that puts the thumb's centre under the pointer.
    /// </summary>
    public static void Scrollbar(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        Box lane = runtime.BoxFromId(id, InteractionKinds.LeftPress);

        // ── Track: where the thumb is, in rows ───────────────────────────────
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

        // The lane alone takes the pointer, so it stays hot over the thumb.
        Interaction onLane = runtime.InteractionsFor(lane);
        bool held = onLane.Pressed || lane.ActiveT >= 1f;   // pressed, and held since: the lane is the active box
        Box thumb = runtime.BoxFromId(id + "-thumb", parent: lane);
        (thumb.SizeX, thumb.SizeY) = (Size.Fill, Size.Px(size));
        thumb.FloatingY = true;
        thumb.FixedPosition = new Vector2(0f, t * (height - size));
        thumb.Style.CornerRadius = BarThickness / 2f;
        thumb.Style.BackgroundColor = held ? UiColor.SurfaceRaisedActive : onLane.Hovering ? UiColor.SurfaceRaisedHover : UiColor.SurfaceRaised;

        // A press on the thumb alone moves nothing until the pointer does.
        bool onThumb = onLane.Y >= thumb.Rect.Y && onLane.Y <= thumb.Rect.Y + thumb.Extent.Y;
        bool moved = onLane.Dragging && onLane.DragDeltaY != 0f;
        if (!(onLane.Pressed && !onThumb || moved)) return;
        float at = Math.Clamp((onLane.Y - lane.Rect.Y - size / 2f) / MathF.Max(1f, height - size), 0f, 1f);
        pt.Target((int)MathF.Round(at * range));
    }
}
