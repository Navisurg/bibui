using System.Numerics;
using Bibui.Core;

namespace Bibui.Core.Runtime;

/// <summary>
/// A vertical list of rows that all have the same height, of which only the
/// rows in sight are built. A list of a hundred thousand rows costs what its
/// screenful does.
///
/// Every row is <c>pitch</c> apart, top to top, so where a row and a pixel are,
/// which rows are in sight and where the list ends all follow from the pitch:
/// nothing is walked and nothing is estimated. Something several rows tall is
/// the caller's to map onto whole rows: it covers rows <c>i</c> to <c>i + n</c>,
/// and is built at the first of them in sight, shifted up by the rows of it
/// above. Rows of their own heights take <see cref="ScrollList"/>.
///
/// It eases: the wheel, and a target moved with <see cref="ScrollPt.Target"/>,
/// glide there, the list keeping what is shown where it was and easing
/// <see cref="ScrollPt.Off"/> to 0.
///
/// <code>
/// var (first, last) = runtime.BeginRowScrollList("log", ref scroll, pitch: 24f, lines.Count, ScrollBy.Pixels);
/// for (int i = first; i &lt; last; i++) Line(lines[i]);
/// runtime.EndRowScrollList(ref scroll, pitch: 24f, lines.Count, ScrollBy.Pixels);
/// runtime.Scrollbar("log-bar", ref scroll, lines.Count, first, last);
/// </code>
/// </summary>
public static class RowScrollList
{
    // A view not laid out yet has no height. Build as if for a tall screen, so
    // its first frame is not a single row.
    private const float Unmeasured = 4096f;

    /// <summary>
    /// Builds the view named <paramref name="id"/>, filling what the parent
    /// leaves, eases <paramref name="pt"/> a frame, and pushes the view for the
    /// rows. Returns the rows in sight: build [First, Last), each
    /// <paramref name="pitch"/> tall, then call <see cref="EndRowScrollList"/>
    /// with the same pitch, count and <paramref name="by"/>.
    /// </summary>
    /// <param name="pitch">The distance from the top of one row to the top of the next.</param>
    /// <param name="count">How many rows there are.</param>
    /// <param name="by">Whether the list rests on whole rows or may rest part way into one.</param>
    public static (int First, int Last) BeginRowScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        if (!(pitch > 0f)) throw new ArgumentOutOfRangeException(nameof(pitch), pitch, "Rows are a pitch apart, above zero.");

        Box view = runtime.BoxFromId(id, InteractionKinds.Scroll | InteractionKinds.Clip);
        view.ChildLayoutAxis = LayoutAxis.Y;
        (view.SizeX, view.SizeY) = (Size.Fill, Size.Fill);
        view.AllowOverflowY = true;

        // Ease, once a frame however often the build runs, and to half a pixel.
        if (pt.Stepped != runtime.FrameCount)
        {
            pt.Stepped = runtime.FrameCount;
            pt.Off = Ease.Exponential(pt.Off, 0f, runtime.Rates.Follow);
            if (MathF.Abs(pt.Off) * pitch <= 0.5f) pt.Off = 0f;
        }
        if (pt.Off != 0f) runtime.RequestFrame();

        // The top shown, in rows: the target, what is still to ease, and by
        // pixels the part of a row. Kept between the first row and the last
        // screenful, so the rows in sight and the view's offset follow from it.
        float height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured;   // last frame's
        float top = Math.Clamp(pt.Idx + pt.Off + (float)by * pt.Px / pitch, 0f, MaxTop(height, pitch, count, by));
        int first = (int)top;
        int last = Math.Min(count, first + (int)MathF.Ceiling(height / pitch) + 1);
        view.ViewOffset = view.TargetViewOffset = new Vector2(0f, (top - first) * pitch);

        runtime.BeginParent(view);
        return (first, last);
    }

    /// <summary>
    /// Pops the view <see cref="BeginRowScrollList"/> pushed, the parent in
    /// scope, then spends the wheel over it. Its pixels move the target, kept
    /// between the first row and the last screenful: whole rows into
    /// <see cref="ScrollPt.Idx"/>, the rest into <see cref="ScrollPt.Px"/>.
    /// What is shown stays put and eases to the new target, so by rows a row
    /// moved eases, and by pixels the glide does.
    /// </summary>
    public static void EndRowScrollList(this BibuiRuntime runtime, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ParentInScope is not Box { Claims: var claims } view || (claims & InteractionKinds.Scroll) == 0)
            throw new InvalidOperationException("EndRowScrollList pops the list's view, and the parent in scope is not one: a parent pushed inside the list was not popped.");
        runtime.EndParent(view);
        float scale = (float)by, height = view.Extent.Y > 0f ? view.Extent.Y : Unmeasured;
        float before = pt.Idx + scale * pt.Px / pitch;   // where the list comes to rest, in rows

        // The target in pixels, with the wheel, kept in range; whole rows of
        // it from row Idx, towards zero, are the new Idx, and the rest Px. By
        // rows the wheel held past either end is clamped away with it.
        double total = Math.Clamp((double)pt.Idx * pitch + pt.Px + runtime.InteractionsFor(view).ScrollY, 0, MaxTop(height, pitch, count, by) * pitch);
        pt.Idx += (int)Math.Truncate((total - (double)pt.Idx * pitch) / pitch);
        pt.Px = (float)(total - (double)pt.Idx * pitch);

        pt.Off += before - (pt.Idx + scale * pt.Px / pitch);   // what is shown stays put, and eases to the new target
    }

    // The highest the top may be, in rows: the top of the last screenful, so
    // the last row is on the bottom. By rows, the whole row at or above it, so
    // the target is a row and the last row is clipped rather than a gap left.
    private static float MaxTop(float height, float pitch, int count, ScrollBy by)
    {
        float top = MathF.Max(0f, count - height / pitch);
        return by == ScrollBy.Rows ? MathF.Floor(top) : top;
    }
}
