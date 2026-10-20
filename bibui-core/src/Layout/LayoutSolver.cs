using System.Numerics;

namespace Bibui.Core;

/// <summary>
/// How large a text is, for a node sized by <see cref="SemanticSize.Text"/>.
/// Given the width the node will have — unbounded while widths are being
/// solved — it answers the width and height it needs.
/// </summary>
internal delegate Vector2 MeasureLeaf(UiNode node, float availableWidth);

/// <summary>
/// Lays out a tree of linked nodes from the sizes they ask for
/// (<see cref="UiNode.SizeX"/>, <see cref="UiNode.SizeY"/>).
///
/// <para>Each axis is solved whole before the next, X first, so a text's height
/// is measured at the width it will actually have. For each axis:</para>
/// <list type="number">
/// <item>Sizes that depend on nothing: pixels, and what a leaf measures.</item>
/// <item>Sizes that depend on the parent, top-down.</item>
/// <item>Sizes that depend on the children, bottom-up.</item>
/// <item>Children that overflow a parent that does not allow it are shrunk, by
/// as much as each may give up.</item>
/// <item>Positions, minus the parent's view offset.</item>
/// </list>
/// <para>Allocates nothing: it walks the links and writes into the nodes.</para>
/// </summary>
internal static class LayoutSolver
{
    /// <summary>
    /// How large <paramref name="root"/> is with <paramref name="available"/>
    /// to fill. Writes no geometry: only <see cref="UiNode.FixedSize"/>, the
    /// working state that <see cref="Layout"/> writes again.
    /// </summary>
    public static Vector2 Measure(UiNode root, Vector2 available, MeasureLeaf leaf)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(leaf);
        for (int a = 0; a < 2; a++)
        {
            Standalone(root, a, leaf);
            var size = Sem(root, a);
            bool known = size.Kind != SizeKind.ChildrenSum;
            if (size.Kind == SizeKind.ParentPct)
            {
                float room = Get(available, a);
                known = float.IsFinite(room);
                root.FixedSize = With(root.FixedSize, a, known ? room * size.Value : 0f);
            }
            Upward(root, a, known);
            Downward(root, a, includeSelf: true);
            Constrain(root, a);
        }
        return root.FixedSize;
    }

    /// <summary>
    /// Lays <paramref name="root"/> out at <paramref name="position"/> with
    /// <paramref name="size"/>, and writes <see cref="UiNode.Rect"/>,
    /// <see cref="UiNode.Extent"/> and <see cref="UiNode.ContentSize"/> for it
    /// and everything under it.
    /// </summary>
    public static void Layout(UiNode root, Vector2 position, Vector2 size, MeasureLeaf leaf)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(leaf);
        root.Rect = position;
        root.Extent = size;
        for (int a = 0; a < 2; a++)
        {
            Standalone(root, a, leaf);
            root.FixedSize = With(root.FixedSize, a, Get(size, a));
            Upward(root, a, parentKnown: true);
            Downward(root, a, includeSelf: false);
            Constrain(root, a);
            Position(root, a);
        }
    }

    // ── 1. Sizes that depend on nothing ─────────────────────────────────────

    private static void Standalone(UiNode node, int a, MeasureLeaf leaf)
    {
        var size = Sem(node, a);
        if (size.Kind == SizeKind.Pixels)
            node.FixedSize = With(node.FixedSize, a, size.Value);
        else if (size.Kind == SizeKind.Text)
        {
            // TODO: fragile. Y measures a leaf at the width X settled on, so a text
            // wraps to its final width; but X measured it unbounded, and nothing
            // ties the two measurements together. A leaf whose width depends on
            // its height, or a constraint that changes the width after it was
            // measured on Y, gets a height for a width it does not have.
            var measured = leaf(node, a == 0 ? float.PositiveInfinity : node.FixedSize.X);
            node.FixedSize = With(node.FixedSize, a, Get(measured, a) + size.Value);
        }

        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            Standalone(child, a, leaf);
    }

    // ── 2. Sizes that depend on the parent, top-down ─────────────────────────

    // A share of a parent whose size is not known yet is a loop: the parent is
    // sized by its children, and this child is sized by the parent.
    //
    // TODO: a ring could be given an answer instead — a share could take the
    // share of the nearest ancestor that has a size, skipping those sized by
    // their children; a share could count what it holds; or centring could be
    // a position rule. None has proven a good rule yet, so a ring throws and
    // the layout says its size.
    private static void Upward(UiNode node, int a, bool parentKnown)
    {
        float inner = Get(node.FixedSize, a) - PadStart(node, a) - PadEnd(node, a);
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            var size = Sem(child, a);
            bool known = size.Kind == SizeKind.ParentPct ? parentKnown : size.Kind != SizeKind.ChildrenSum;
            if (size.Kind == SizeKind.ParentPct)
            {
                if (!parentKnown) throw LayoutException.Ring(child, a);
                child.FixedSize = With(child.FixedSize, a, MathF.Max(0f, inner) * size.Value);
            }
            Upward(child, a, known);
        }
    }

    // ── 3. Sizes that depend on the children, bottom-up ──────────────────────

    private static void Downward(UiNode node, int a, bool includeSelf)
    {
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            Downward(child, a, includeSelf: true);

        if (!includeSelf || Sem(node, a).Kind != SizeKind.ChildrenSum) return;
        node.FixedSize = With(node.FixedSize, a, Extent(node, a) + PadStart(node, a) + PadEnd(node, a));
    }

    // ── 4. Constraints ─────────────────────────────────────────────────────

    private static void Constrain(UiNode node, int a)
    {
        float inner = MathF.Max(0f, Get(node.FixedSize, a) - PadStart(node, a) - PadEnd(node, a));

        if (Wraps(node) && a == 0)
        {
            // A wrapping row: one line while what is not a fill fits, else lines, and fills take nothing.
            node.Wrapped = !FitsOnOneLine(node, inner);
            if (node.Wrapped) BreakLines(node, inner);
            else for (var child = node.FirstChild; child is not null; child = NextOf(child)) child.WrapLine = 0;
        }
        if (Wraps(node) && node.Wrapped && a == 0)
        {
            // Its lines are broken: nothing more to share along them.
        }
        else if (!AllowsOverflow(node, a))
        {
            if ((int)node.ChildLayoutAxis == a)
            {
                // Along the layout axis the children share the room. Fills (shares of
                // the parent) give way first: what is left after the rest is theirs.
                // Then, if that was not enough, the rest give up the same fraction of
                // what each may give up.
                float total = Gaps(node, a);
                for (var child = node.FirstChild; child is not null; child = NextOf(child))
                    if (!Floats(child, a)) total += Get(child.FixedSize, a);
                float over = total - inner;
                if (over > 0f) over -= Yield(node, a, over, fills: true);
                if (over > 0f) Yield(node, a, over, fills: false);
            }
            else
            {
                // Across it, each child is cut to the parent.
                for (var child = node.FirstChild; child is not null; child = NextOf(child))
                    if (!Floats(child, a) && Get(child.FixedSize, a) > inner)
                        child.FixedSize = With(child.FixedSize, a, inner);
            }
        }
        else
        {
            // Children that run past keep their size, but a share of the parent
            // is a share of its size as it now is.
            for (var child = node.FirstChild; child is not null; child = NextOf(child))
                if (Sem(child, a) is { Kind: SizeKind.ParentPct } pct)
                    child.FixedSize = With(child.FixedSize, a, inner * pct.Value);
        }

        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            Constrain(child, a);
    }

    private static bool Wraps(UiNode node) => node.ChildWrap && node.ChildLayoutAxis == LayoutAxis.X;

    /// <summary>Whether a wrapping row's children, fills left out, fit on one line of <paramref name="inner"/>.</summary>
    private static bool FitsOnOneLine(UiNode node, float inner)
    {
        float total = Gaps(node, 0);
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            if (!Floats(child, 0) && Sem(child, 0).Kind != SizeKind.ParentPct) total += child.FixedSize.X;
        return total <= inner + 0.01f;
    }

    /// <summary>
    /// The lines of a wrapping row too narrow for its children: fills get nothing, a child wider than
    /// the row is cut to it, and each child that would run past the end starts the next line.
    /// </summary>
    private static void BreakLines(UiNode node, float inner)
    {
        float cursor = 0f;
        int line = 0;
        bool any = false;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            if (Floats(child, 0)) continue;
            float s = Sem(child, 0).Kind == SizeKind.ParentPct ? 0f : MathF.Min(child.FixedSize.X, inner);
            child.FixedSize = child.FixedSize with { X = s };
            if (s == 0f) { child.WrapLine = line; continue; }
            if (any && cursor + s > inner + 0.01f) { line++; cursor = 0f; }
            child.WrapLine = line;
            cursor += s + node.ChildGap;
            any = true;
        }
    }

    /// <summary>The height of each line of a wrapped row, in order.</summary>
    private static float LineHeight(UiNode node, int line)
    {
        float h = 0f;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            if (!Floats(child, 1) && child.WrapLine == line) h = MathF.Max(h, child.FixedSize.Y);
        return h;
    }

    private static int Lines(UiNode node)
    {
        int last = 0;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            if (!Floats(child, 1)) last = Math.Max(last, child.WrapLine);
        return last + 1;
    }

    /// <summary>
    /// Shrinks the fills (or the other children) along <paramref name="a"/> to give up
    /// <paramref name="over"/>, each by the same fraction of what it may give up; returns what they gave.
    /// </summary>
    private static float Yield(UiNode node, int a, float over, bool fills)
    {
        float yieldable = 0f;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            if (Floats(child, a) || (Sem(child, a).Kind == SizeKind.ParentPct) != fills) continue;
            yieldable += Get(child.FixedSize, a) * (1f - Sem(child, a).Strictness);
        }
        if (yieldable <= 0f) return 0f;
        float fraction = Math.Clamp(over / yieldable, 0f, 1f);
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            if (Floats(child, a) || (Sem(child, a).Kind == SizeKind.ParentPct) != fills) continue;
            float s = Get(child.FixedSize, a);
            child.FixedSize = With(child.FixedSize, a, s - s * (1f - Sem(child, a).Strictness) * fraction);
        }
        return yieldable * fraction;
    }

    // ── 5. Positions ───────────────────────────────────────────────────────

    private static void Position(UiNode node, int a)
    {
        if (Wraps(node) && node.Wrapped) { PositionWrapped(node, a); return; }

        float start = Get(node.Rect, a) + PadStart(node, a) - Get(node.ViewOffset, a);
        float cursor = 0f;
        bool along = (int)node.ChildLayoutAxis == a;
        // Across the axis, the share of the room left that goes before a child.
        float across = along ? 0f : node.ChildAlign switch { ChildAlignment.Center => 0.5f, ChildAlignment.End => 1f, _ => 0f };
        float room = across == 0f ? 0f : Get(node.Extent, a) - PadStart(node, a) - PadEnd(node, a);
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            float s = Get(child.FixedSize, a);
            float at = Floats(child, a)
                ? Get(node.Rect, a) + Get(child.FixedPosition, a) - Get(node.ViewOffset, a)
                : start + (along ? cursor : MathF.Max(0f, room - s) * across);
            if (along && !Floats(child, a)) cursor += s + node.ChildGap;
            child.Rect = With(child.Rect, a, at);
            child.Extent = With(child.Extent, a, s);
            Position(child, a);
        }

        node.ContentSize = With(node.ContentSize, a, Extent(node, a) + PadStart(node, a) + PadEnd(node, a));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The size a node asks for. There is no default: a size not said throws.</summary>
    private static SemanticSize Sem(UiNode node, int a)
    {
        var size = a == 0 ? node.SizeX : node.SizeY;
        if (size.Kind != SizeKind.Null) return size;
        throw LayoutException.NoSize(node, a);
    }

    /// <summary>A wrapped row's children: along X from each line's start, down Y line by line, placed in their line by <see cref="UiNode.ChildAlign"/>.</summary>
    private static void PositionWrapped(UiNode node, int a)
    {
        float start = Get(node.Rect, a) + PadStart(node, a) - Get(node.ViewOffset, a);
        float across = node.ChildAlign switch { ChildAlignment.Center => 0.5f, ChildAlignment.End => 1f, _ => 0f };
        int lines = a == 1 ? Lines(node) : 0;
        Span<float> tops = a == 1 ? stackalloc float[Math.Min(lines, 256)] : default;
        Span<float> heights = a == 1 ? stackalloc float[Math.Min(lines, 256)] : default;
        if (a == 1)
        {
            float y = 0f;
            for (int i = 0; i < tops.Length; i++) { heights[i] = LineHeight(node, i); tops[i] = y; y += heights[i] + node.LineGap; }
        }
        float cursor = 0f;
        int current = 0;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            float s = Get(child.FixedSize, a);
            float at;
            if (Floats(child, a)) at = Get(node.Rect, a) + Get(child.FixedPosition, a) - Get(node.ViewOffset, a);
            else if (a == 0)
            {
                if (child.WrapLine != current) { current = child.WrapLine; cursor = 0f; }
                at = start + cursor;
                if (s > 0f) cursor += s + node.ChildGap;
            }
            else
            {
                int line = Math.Min(child.WrapLine, tops.Length - 1);
                at = start + tops[line] + MathF.Max(0f, heights[line] - s) * across;
            }
            child.Rect = With(child.Rect, a, at);
            child.Extent = With(child.Extent, a, s);
            Position(child, a);
        }
        node.ContentSize = With(node.ContentSize, a, Extent(node, a) + PadStart(node, a) + PadEnd(node, a));
    }

    /// <summary>The space <see cref="UiNode.ChildGap"/> puts between the in-flow children along <paramref name="a"/>.</summary>
    private static float Gaps(UiNode node, int a)
    {
        if (node.ChildGap == 0f || (int)node.ChildLayoutAxis != a) return 0f;
        int n = 0;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
            if (!Floats(child, a)) n++;
        return n > 1 ? node.ChildGap * (n - 1) : 0f;
    }

    /// <summary>The children's extent along <paramref name="a"/>: summed with the gaps between them if it is the layout axis, the largest if not.</summary>
    private static float Extent(UiNode node, int a)
    {
        if (Wraps(node) && node.Wrapped && a == 1)
        {
            // A wrapped row is as tall as its lines and the gaps between them.
            int lines = Lines(node);
            float h = node.LineGap * (lines - 1);
            for (int i = 0; i < lines; i++) h += LineHeight(node, i);
            return h;
        }
        bool along = (int)node.ChildLayoutAxis == a;
        float extent = along ? Gaps(node, a) : 0f;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            if (Floats(child, a)) continue;
            float s = Get(child.FixedSize, a);
            extent = along ? extent + s : MathF.Max(extent, s);
        }
        return extent;
    }

    private static UiNode? NextOf(UiNode node) => node.Next == node ? null : node.Next;
    private static bool Floats(UiNode node, int a) => a == 0 ? node.FloatingX : node.FloatingY;
    private static bool AllowsOverflow(UiNode node, int a) => a == 0 ? node.AllowOverflowX : node.AllowOverflowY;
    private static float PadStart(UiNode node, int a) => a == 0 ? node.Style.Padding.Left : node.Style.Padding.Top;
    private static float PadEnd(UiNode node, int a) => a == 0 ? node.Style.Padding.Right : node.Style.Padding.Bottom;
    private static float Get(Vector2 v, int a) => a == 0 ? v.X : v.Y;
    private static Vector2 With(Vector2 v, int a, float value) => a == 0 ? v with { X = value } : v with { Y = value };
}
