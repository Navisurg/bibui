using System.Numerics;
using Bibui.Core;
using static Bibui.Core.Runtime.Scrollbar;

namespace Bibui.Core.Runtime;

/// <summary>
/// A scrollbar for a box that scrolls, described as boxes.
///
/// A track laid out along the axis holds three boxes: the space before the
/// thumb, the thumb, and the space after it, each a share of the track, read off the view as it was last frame: the thumb's share is how
/// much of the content is in view, and the spaces split the rest by how far the
/// view has scrolled. Dragging the thumb puts its centre under the pointer;
/// pressing either space pages by one view.
///
/// It sits beside the view, not over it: acquire both under one parent laid out
/// across the bar's axis. The view should claim the wheel and the clamp along
/// the same axis — the bar moves <see cref="Box.TargetViewOffset"/> and leaves
/// keeping it in range to the clamp.
/// </summary>
public static class Scrollbar
{
    /// <summary>How wide a lane the bar takes, and so how wide a strip catches its presses.</summary>
    public const float Lane = 12f;

    internal const float Thickness = 6f;
    internal const float MinThumb = 24f;


    // The lane across the axis, all of the parent along it.
    internal static void DescribeTrack(Box track, bool y)
    {
        float inset = (Lane - Thickness) / 2f;
        var lane = SemanticSize.Pixels(Lane);
        var along = SemanticSize.ParentPct(1f);
        (track.SizeX, track.SizeY) = y ? (lane, along) : (along, lane);
        track.Style.Padding = y ? new EdgeInsets(0f, inset, 0f, inset) : new EdgeInsets(inset, 0f, inset, 0f);
    }

    // A share of the track along the axis, and all of it across.
    internal static void Share(Box box, bool y, float share)
    {
        var along = SemanticSize.ParentPct(share);
        var across = SemanticSize.ParentPct(1f);
        (box.SizeX, box.SizeY) = y ? (across, along) : (along, across);
    }

    internal static float Along(Vector2 v, bool y) => y ? v.Y : v.X;

    internal static Vector2 With(Vector2 v, bool y, float along) => y ? v with { Y = along } : v with { X = along };
}

/// <summary>The Scrollbar component, called on the runtime that builds it.</summary>
public static class ScrollbarExtensions
{
    /// <summary>
    /// Builds the bar named <paramref name="id"/> for <paramref name="view"/>,
    /// along <paramref name="axis"/>, and returns its track with what happened
    /// to the thumb this frame.
    /// </summary>
    public static BibuiWidget Scrollbar(this BibuiRuntime runtime, string id, Box view, LayoutAxis axis = LayoutAxis.Y)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(view);

        bool y = axis == LayoutAxis.Y;
        float viewport = Along(view.Extent, y);
        float range = Along(view.ContentSize, y) - viewport;

        Box track = runtime.BoxFromId(id);
        track.ChildLayoutAxis = axis;

        // The track keeps its lane when there is nothing to scroll, so the
        // view does not change width as its content crosses the edge.
        DescribeTrack(track, y);
        if (range <= 0f) return new BibuiWidget(track, default);

        Box before = runtime.BoxFromId(id + "-before", InteractionKinds.LeftPress, track);
        Box thumb = runtime.BoxFromId(id + "-thumb", InteractionKinds.LeftPress, track);
        Box after = runtime.BoxFromId(id + "-after", InteractionKinds.LeftPress, track);

        Interaction drag = runtime.InteractionsFor(thumb);
        if (drag.Pressed || drag.Dragging)
        {
            float free = MathF.Max(1f, Along(track.Extent, y) - Along(thumb.Extent, y));
            float u = ((y ? drag.Y : drag.X) - Along(track.Rect, y) - Along(thumb.Extent, y) / 2f) / free;
            var to = With(view.ViewOffset, y, Math.Clamp(u, 0f, 1f) * range);
            view.TargetViewOffset = to;
            view.ViewOffset = to;   // under the pointer, not easing after it
        }
        if (runtime.InteractionsFor(before).Pressed)
            view.TargetViewOffset = With(view.TargetViewOffset, y, Along(view.TargetViewOffset, y) - viewport);
        if (runtime.InteractionsFor(after).Pressed)
            view.TargetViewOffset = With(view.TargetViewOffset, y, Along(view.TargetViewOffset, y) + viewport);

        float content = viewport + range;
        float share = MathF.Min(1f, MathF.Max(viewport / content, MinThumb / MathF.Max(1f, Along(track.Extent, y))));
        float t = Math.Clamp(Along(view.ViewOffset, y) / range, 0f, 1f);

        Share(before, y, t * (1f - share));
        Share(thumb, y, share);
        Share(after, y, (1f - t) * (1f - share));

        thumb.Style.CornerRadius = Thickness / 2f;
        thumb.Style.Draw = DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects;

        return new BibuiWidget(track, drag);
    }
}
