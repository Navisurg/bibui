namespace Bibui;

/// <summary>The axis a node lays its children out along.</summary>
public enum LayoutAxis
{
    X,
    Y,
}

/// <summary>What a node's size along one axis depends on.</summary>
public enum SizeKind
{
    /// <summary>Not said: <see cref="ChildrenSum"/> for a node with children, <see cref="Text"/> otherwise.</summary>
    Null,

    /// <summary>Exactly <see cref="SemanticSize.Value"/> pixels.</summary>
    Pixels,

    /// <summary>What the content measures, plus <see cref="SemanticSize.Value"/> of padding.</summary>
    Text,

    /// <summary><see cref="SemanticSize.Value"/> of the parent's size, inside its padding.</summary>
    ParentPct,

    /// <summary>The children: summed along the layout axis, the largest across it, plus padding.</summary>
    ChildrenSum,
}

/// <summary>
/// The size a node asks for along one axis, stated by what it depends on: a
/// semantic size. The solver turns it into
/// <see cref="UiNode.FixedSize"/>.
///
/// <para><see cref="Strictness"/> is the fraction of the size that may not be
/// taken away when siblings do not fit: 1 never shrinks, 0 gives up all of it.</para>
/// </summary>
public readonly record struct SemanticSize(SizeKind Kind, float Value, float Strictness)
{
    public static SemanticSize Pixels(float value, float strictness = 1f) => new(SizeKind.Pixels, value, strictness);
    public static SemanticSize Text(float padding = 0f, float strictness = 1f) => new(SizeKind.Text, padding, strictness);
    public static SemanticSize ParentPct(float fraction, float strictness = 0f) => new(SizeKind.ParentPct, fraction, strictness);
    public static SemanticSize ChildrenSum(float strictness = 1f) => new(SizeKind.ChildrenSum, 0f, strictness);
}
