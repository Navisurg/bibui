using System.Numerics;
using Bibui.Core;

namespace Bibui.Core.Runtime;

/// <summary>What a <see cref="RowScrollList"/> scrolls by. Its value, 0 or 1, is a scale in the list's arithmetic.</summary>
public enum ScrollBy
{
    /// <summary>Whole rows: the wheel moves the list a row at a time, and it rests on a row's top.</summary>
    Rows = 0,

    /// <summary>Pixels: the wheel slides the rows as it turns, and the list may rest part way into a row.</summary>
    Pixels = 1,
}

/// <summary>
/// A vertical list of rows of one height, <c>pitch</c> apart, of which only
/// those in sight are built; everything follows from the pitch. Something
/// several rows tall is the caller's to map onto whole rows. Rows of their own
/// heights take <see cref="ScrollList"/>.
/// <code>
/// var (first, last) = runtime.BeginRowScrollList("log", ref scroll, pitch: 24f, lines.Count, ScrollBy.Pixels);
/// for (int i = first; i &lt; last; i++) Line(lines[i]);
/// runtime.EndRowScrollList(ref scroll, pitch: 24f, lines.Count, ScrollBy.Pixels);
/// runtime.Scrollbar("log-bar", ref scroll, lines.Count, first, last);
/// </code>
/// </summary>
public static class RowScrollList
{
    /// <summary>
    /// Builds and pushes the view named <paramref name="id"/>, filling the
    /// parent, and returns the rows in sight: build [First, Last), each
    /// <paramref name="pitch"/> tall.
    /// </summary>
    public static (int First, int Last) BeginRowScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        if (!(pitch > 0f)) throw new ArgumentOutOfRangeException(nameof(pitch), pitch, "Rows are a pitch apart, above zero.");

        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (Size.Fill, Size.Fill);
        view.AllowOverflowY = true;

        pt.Ease(runtime);

        // The top shown, in rows, kept between the first row and the last screenful. Unmeasured, only to the
        // last row: a view built again (shown again after being hidden) keeps its point, not one clamped to a
        // height it has not been laid out at.
        float height = ScrollList.BuildHeight(runtime, view);
        float top = Math.Clamp(pt.Idx + ((float)by * pt.Px + pt.Off) / pitch, 0f, view.Extent.Y > 0f ? MaxTop(height, pitch, count, by) : MathF.Max(0f, count - 1));
        int first = (int)top;
        int last = Math.Min(count, first + (int)MathF.Ceiling(height / pitch) + 1);
        view.ViewOffset = view.TargetViewOffset = new Vector2(0f, (top - first) * pitch);

        runtime.BeginParent(view);
        return (first, last);
    }

    /// <summary>Pops the view and moves the target by the wheel over it, kept between the first row and the last screenful.</summary>
    public static void EndRowScrollList(this BibuiRuntime runtime, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ParentInScope is not Box { Claims: var claims } view || (claims & InteractionKinds.Scroll) == 0)
            throw new InvalidOperationException("EndRowScrollList pops the list's view, and the parent in scope is not one: a parent pushed inside the list was not popped.");
        runtime.EndParent(view);
        if (!(view.Extent.Y > 0f)) return;   // unmeasured: no wheel has reached it, and its range is not known to keep to
        float scale = (float)by, height = view.Extent.Y;
        double before = (double)pt.Idx * pitch + scale * pt.Px;   // where the list comes to rest, in pixels

        // The target in pixels, with the wheel, kept in range: whole rows of it
        // are Idx, the rest Px.
        double total = Math.Clamp((double)pt.Idx * pitch + pt.Px + runtime.InteractionsFor(view).ScrollY, 0, MaxTop(height, pitch, count, by) * pitch);
        pt.Idx += (int)Math.Truncate((total - (double)pt.Idx * pitch) / pitch);
        pt.Px = (float)(total - (double)pt.Idx * pitch);

        pt.Off += (float)(before - ((double)pt.Idx * pitch + scale * pt.Px));   // what is shown stays put, and eases to the new target
    }

    // The highest the top may be, in rows: by pixels the last row on the
    // bottom, by rows the whole row above that, the last row clipped.
    private static float MaxTop(float height, float pitch, int count, ScrollBy by)
    {
        float top = MathF.Max(0f, count - height / pitch);
        return by == ScrollBy.Rows ? MathF.Floor(top) : top;
    }
}
