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
    internal RowHeights? Rows; // an uneven list's heights; null for rows of one pitch
    internal float Height;    // an uneven list's view height, for End
    internal string? ListId;  // an uneven list's id, for its rows

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
/// </summary>
public static partial class ScrollList
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

        // The area lays the view and its bar out side by side. The view claims
        // the wheel without moving on it: End turns the wheel into rows.
        Box area = runtime.BoxFromId(id + "-area");
        area.ChildLayoutAxis = LayoutAxis.X;
        (area.SizeX, area.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip, area);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        view.AllowOverflowY = true;

        // Measured from last frame's layout.
        float height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured;
        float maxPx = MathF.Max(0f, count * pitch - height);   // the last row flush with the bottom
        pt.ViewRows = (int)(height / pitch);
        (pt.Pitch, pt.MaxPx) = (pitch, maxPx);

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
        if (pt.Rows is not null) { EndUneven(runtime, ref pt, dy); return; }
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
