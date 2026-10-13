using System.Numerics;

namespace Bibui;

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

        if (!AllowsOverflow(node, a))
        {
            if ((int)node.ChildLayoutAxis == a)
            {
                // Along the layout axis the children share the room: each gives
                // up the same fraction of what it may give up.
                float total = 0f, yieldable = 0f;
                for (var child = node.FirstChild; child is not null; child = NextOf(child))
                {
                    if (Floats(child, a)) continue;
                    float s = Get(child.FixedSize, a);
                    total += s;
                    yieldable += s * (1f - Sem(child, a).Strictness);
                }
                float over = total - inner;
                if (over > 0f && yieldable > 0f)
                {
                    float fraction = Math.Clamp(over / yieldable, 0f, 1f);
                    for (var child = node.FirstChild; child is not null; child = NextOf(child))
                    {
                        if (Floats(child, a)) continue;
                        float s = Get(child.FixedSize, a);
                        child.FixedSize = With(child.FixedSize, a, s - s * (1f - Sem(child, a).Strictness) * fraction);
                    }
                }
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

    // ── 5. Positions ───────────────────────────────────────────────────────

    private static void Position(UiNode node, int a)
    {
        float start = Get(node.Rect, a) + PadStart(node, a) - Get(node.ViewOffset, a);
        float cursor = 0f;
        bool along = (int)node.ChildLayoutAxis == a;
        for (var child = node.FirstChild; child is not null; child = NextOf(child))
        {
            float s = Get(child.FixedSize, a);
            float at = Floats(child, a)
                ? Get(node.Rect, a) + Get(child.FixedPosition, a) - Get(node.ViewOffset, a)
                : start + (along ? cursor : 0f);
            if (along && !Floats(child, a)) cursor += s;
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

    /// <summary>The children's extent along <paramref name="a"/>: summed if it is the layout axis, the largest if not.</summary>
    private static float Extent(UiNode node, int a)
    {
        bool along = (int)node.ChildLayoutAxis == a;
        float extent = 0f;
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
