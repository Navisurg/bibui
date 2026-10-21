namespace Bibui.Core;

/// <summary>
/// Spacing, centring and grouping, said with nodes rather than rules: a gap is
/// a spacer of a fixed size, a spring is a spacer that takes the room left, and
/// a row or column is a node with an axis that its children are pushed into.
///
/// <para>The expression forms (<see cref="Row"/>, <see cref="Column"/>,
/// <see cref="Space"/>, <see cref="Spring()"/>) do exactly what the statement
/// forms do: they push into the parent's linked list, and allocate only the
/// nodes — their arguments are a span on the stack.</para>
/// </summary>
public static class Arrange
{
    /// <summary>
    /// One thing to push into a <see cref="Row"/> or <see cref="Column"/>: a
    /// node, a gap of fixed pixels, or a spring. A node converts to one.
    /// </summary>
    public readonly struct Item
    {
        internal readonly UiNode? Node;
        internal readonly SemanticSize Spacer;

        private Item(UiNode? node, SemanticSize spacer)
        {
            Node = node;
            Spacer = spacer;
        }

        public static implicit operator Item(UiNode node) => new(node, default);

        /// <summary>
        /// A built component goes into a row or column as its node, so
        /// <c>Row((...).Button(), ...)</c> needs no <c>.Node</c>.
        /// </summary>
        public static implicit operator Item(BibuiWidget widget) => new(widget.Node, default);
        internal static Item Of(SemanticSize spacer) => new(null, spacer);
    }

    /// <summary>A node laying <paramref name="items"/> out along X, in order, sized by them.</summary>
    [Obsolete("Push a runtime row instead: var row = runtime.RowFit(); using (runtime.PushParent(row)) { ... } builds into a pooled row.")]
    public static Box Row(params ReadOnlySpan<Item> items) => Group(null, LayoutAxis.X, items);

    /// <summary>A node laying <paramref name="items"/> out along Y, in order, sized by them.</summary>
    [Obsolete("Push a runtime column instead: var column = runtime.ColumnFit(); using (runtime.PushParent(column)) { ... } builds into a pooled column.")]
    public static Box Column(params ReadOnlySpan<Item> items) => Group(null, LayoutAxis.Y, items);

    /// <summary>The same as <see cref="Row(ReadOnlySpan{Item})"/>, named <paramref name="id"/>.</summary>
    [Obsolete("Push a runtime row instead: var row = runtime.RowFit(); using (runtime.PushParent(row)) { ... } builds into a pooled row.")]
    public static Box Row(string? id, params ReadOnlySpan<Item> items) => Group(id, LayoutAxis.X, items);

    /// <summary>The same as <see cref="Column(ReadOnlySpan{Item})"/>, named <paramref name="id"/>.</summary>
    [Obsolete("Push a runtime column instead: var column = runtime.ColumnFit(); using (runtime.PushParent(column)) { ... } builds into a pooled column.")]
    public static Box Column(string? id, params ReadOnlySpan<Item> items) => Group(id, LayoutAxis.Y, items);

    /// <summary>A gap of <paramref name="pixels"/> along the axis of the row or column it is in.</summary>
    public static Item Space(float pixels) => Item.Of(SemanticSize.Pixels(pixels));

    /// <summary>A spring, in a row or column: see <see cref="Spring(UiNode)"/>.</summary>
    public static Item Spring() => Item.Of(SemanticSize.ParentPct(1f, 0f));

    /// <summary>Pushes a spacer of <paramref name="pixels"/> along <paramref name="parent"/>'s axis.</summary>
    public static Box Gap(UiNode parent, float pixels) => Spacer(parent, SemanticSize.Pixels(pixels));

    /// <summary>
    /// Pushes a spacer that takes whatever room its siblings leave along
    /// <paramref name="parent"/>'s axis, and gives all of it up first when they
    /// do not fit: what pushes the nodes after it to the far end.
    /// </summary>
    public static Box Spring(UiNode parent) => Spacer(parent, SemanticSize.ParentPct(1f, 0f));

    /// <summary>
    /// Pushes <paramref name="node"/> into <paramref name="parent"/> centred
    /// along the parent's axis: the room it does not take is split between a
    /// spacer before and after it.
    /// </summary>
    [Obsolete("Build into runtime.Centred() instead: using (runtime.Centred()) { ... } centres what is built inside along the parent in scope.")]
    public static void Centred(UiNode parent, UiNode node)
    {
        Spacer(parent, SemanticSize.ParentPct(1f, 0f));
        UiNode.PushIntoParent(parent, node);
        Spacer(parent, SemanticSize.ParentPct(1f, 0f));
    }

    /// <summary><paramref name="node"/> centred across a column: a row of a spring, it, and a spring, all the column's width.</summary>
    [Obsolete("Push a full-width runtime row and centre in it: using (runtime.PushParent(runtime.RowFillX())) using (runtime.Centred()) { ... }.")]
    public static Box CentredX(UiNode node) => Group(null, LayoutAxis.X, [Spring(), node, Spring()]).FillX();

    /// <summary><paramref name="node"/> centred across a row: a column of a spring, it, and a spring, all the row's height.</summary>
    [Obsolete("Push a full-height runtime column and centre in it: using (runtime.PushParent(runtime.ColumnFillY())) using (runtime.Centred()) { ... }.")]
    public static Box CentredY(UiNode node) => Group(null, LayoutAxis.Y, [Spring(), node, Spring()]).FillY();

    // ── Sizes, set on the node and handed back ─────────────────────────────

    /// <summary>A share of the parent along X: all of it across a column, a share of what is left along a row.</summary>
    public static T FillX<T>(this T node, float share = 1f) where T : UiNode
    {
        node.SizeX = SemanticSize.ParentPct(share);
        return node;
    }

    /// <summary>The same as <see cref="FillX{T}"/>, along Y.</summary>
    public static T FillY<T>(this T node, float share = 1f) where T : UiNode
    {
        node.SizeY = SemanticSize.ParentPct(share);
        return node;
    }

    /// <summary>Fixed pixel sizes, on the axes given.</summary>
    public static T Px<T>(this T node, float? width = null, float? height = null) where T : UiNode
    {
        if (width is { } w) node.SizeX = SemanticSize.Pixels(w);
        if (height is { } h) node.SizeY = SemanticSize.Pixels(h);
        return node;
    }

    /// <summary>
    /// Out of the flow on both axes, at (<paramref name="x"/>, <paramref name="y"/>)
    /// from the parent's top-left corner, and not counted in the parent's size.
    /// </summary>
    public static T At<T>(this T node, float x, float y) where T : UiNode
    {
        node.FloatingX = node.FloatingY = true;
        node.FixedPosition = new System.Numerics.Vector2(x, y);
        return node;
    }

    /// <summary>A style for the node: its colours, border, radius and padding.</summary>
    public static T Styled<T>(this T node, Style style) where T : UiNode
    {
        node.Style = style;
        return node;
    }

    /// <summary>
    /// The box claims <see cref="InteractionKinds.Clip"/>: what is under it is
    /// drawn and pressed only inside its rect.
    /// </summary>
    public static Box Clipped(this Box box)
    {
        box.Claims |= InteractionKinds.Clip;
        return box;
    }

    /// <summary>Space between its children along its axis (<see cref="UiNode.ChildGap"/>).</summary>
    public static T Spaced<T>(this T node, float gap) where T : UiNode
    {
        node.ChildGap = gap;
        return node;
    }

    /// <summary>Where its children sit across its axis (<see cref="UiNode.ChildAlign"/>).</summary>
    public static T Aligned<T>(this T node, ChildAlignment align) where T : UiNode
    {
        node.ChildAlign = align;
        return node;
    }

    /// <summary>Padding inside the node.</summary>
    public static T Padded<T>(this T node, EdgeInsets padding) where T : UiNode
    {
        node.Style.Padding = padding;
        return node;
    }

    private static Box Group(string? id, LayoutAxis axis, ReadOnlySpan<Item> items)
    {
        var box = new Box
        {
            Id = id,
            ChildLayoutAxis = axis,
            SizeX = SemanticSize.ChildrenSum(),
            SizeY = SemanticSize.ChildrenSum(),
        };
        foreach (var item in items)
        {
            if (item.Node is { } node) UiNode.PushIntoParent(box, node);
            else Spacer(box, item.Spacer);
        }
        return box;
    }

    private static Box Spacer(UiNode parent, SemanticSize along)
    {
        var spacer = new Box();
        if (parent.ChildLayoutAxis == LayoutAxis.X) { spacer.SizeX = along; spacer.SizeY = SemanticSize.Pixels(0f); }
        else { spacer.SizeY = along; spacer.SizeX = SemanticSize.Pixels(0f); }
        UiNode.PushIntoParent(parent, spacer);
        return spacer;
    }
}
