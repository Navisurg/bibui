using Bibui.Core;
using Bibui.Core.Runtime;

/// <summary>
/// Expression forms the tests build trees with, which the library leaves to
/// statements: a node styled, padded or clipped where it is made, and rows and
/// columns of nodes built outside any parent in scope, as the solver tests and
/// the tests that move runtime nodes need them.
/// </summary>
static class TestNodes
{
    public static T Styled<T>(this T node, Style style) where T : UiNode
    {
        node.Style = style;
        return node;
    }

    public static T Padded<T>(this T node, EdgeInsets padding) where T : UiNode
    {
        node.Style.Padding = padding;
        return node;
    }

    public static Box Clipped(this Box box)
    {
        box.Claims |= InteractionKinds.Clip;
        return box;
    }

    /// <summary>
    /// A box sized by its children laying <paramref name="items"/> out along
    /// <paramref name="axis"/>: a string first is its id, a node goes in as it
    /// is, a float is a gap of that many px and a size a spacer of it.
    /// </summary>
    public static Box Group(LayoutAxis axis, params object[] items)
    {
        var box = new Box { Id = items is [string id, ..] ? id : null, ChildLayoutAxis = axis, SizeX = Size.Fit, SizeY = Size.Fit };
        foreach (var item in items)
            switch (item)
            {
                case string: break;
                case UiNode node: UiNode.PushIntoParent(box, node); break;
                case BibuiWidget widget: UiNode.PushIntoParent(box, widget.Node); break;
                case SliderWidget slider: UiNode.PushIntoParent(box, slider.Node); break;
                case float px: Spacer(box, Size.Px(px)); break;
                case SemanticSize size: Spacer(box, size); break;
                default: throw new ArgumentException($"{item.GetType().Name} is not a group item");
            }
        return box;
    }

    /// <summary>A spacer <paramref name="along"/> <paramref name="parent"/>'s axis, pushed into it.</summary>
    public static Box Spacer(UiNode parent, SemanticSize along)
    {
        var spacer = new Box();
        bool alongX = parent.ChildLayoutAxis == LayoutAxis.X;
        spacer.SizeX = alongX ? along : Size.Px(0f);
        spacer.SizeY = alongX ? Size.Px(0f) : along;
        UiNode.PushIntoParent(parent, spacer);
        return spacer;
    }

    /// <summary>A spring in <paramref name="parent"/>: a fill spacer along its axis.</summary>
    public static Box Spring(UiNode parent) => Spacer(parent, Size.Fill);
}
