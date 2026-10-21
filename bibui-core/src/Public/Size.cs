namespace Bibui.Core;

/// <summary>
/// The sizes a node asks for, by short name: what <see cref="SemanticSize"/>
/// says at length. A size relative to the font in scope is the runtime's
/// (<see cref="Runtime.BibuiRuntime.Em"/>), since only it knows the font.
/// </summary>
public static class Size
{
    /// <summary>As large as its children: summed along its axis, the largest across it.</summary>
    public static SemanticSize Fit => SemanticSize.ChildrenSum();

    /// <summary>
    /// All of the parent's room on this axis: across a row or column, its size;
    /// along one, what its other children leave, shared among the fills. Gives
    /// all of it up first when they do not fit, so a fill spacer is a spring.
    /// </summary>
    public static SemanticSize Fill => SemanticSize.ParentPct(1f);

    /// <summary><paramref name="share"/> of the parent's room, as <see cref="Fill"/> is all of it.</summary>
    public static SemanticSize Pct(float share) => SemanticSize.ParentPct(share);

    /// <summary>Exactly <paramref name="pixels"/> logical px.</summary>
    public static SemanticSize Px(float pixels) => SemanticSize.Pixels(pixels);

    /// <summary>As large as the text or content it holds.</summary>
    public static SemanticSize Text => SemanticSize.Text();
}

/// <summary>Sets a node's place and size, and hands it back, so it is said where the node is made.</summary>
public static class NodeLayout
{
    /// <summary>
    /// The node's size on the axes given; an axis left out keeps what it had
    /// (for <see cref="Runtime.BibuiRuntime.Row"/> and
    /// <see cref="Runtime.BibuiRuntime.Column"/>, <see cref="Size.Fit"/>).
    /// <c>runtime.Row().Sized(Size.Fill, runtime.Em(2.5f))</c> is a toolbar.
    /// </summary>
    public static T Sized<T>(this T node, SemanticSize? x = null, SemanticSize? y = null) where T : UiNode
    {
        if (x is { } sx) node.SizeX = sx;
        if (y is { } sy) node.SizeY = sy;
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
}
