namespace Bibui.Core;

/// <summary>
/// A layout the solver cannot solve: a node that says no size, or a ring — a
/// node that takes a share of its parent (<see cref="SizeKind.ParentPct"/>)
/// while the parent's size is not known yet, because it is sized by its
/// children, so the child depends on the parent and the parent on the child.
/// </summary>
public sealed class LayoutException : Exception
{
    public LayoutException(string message) : base(message) { }

    internal static LayoutException Ring(UiNode child, int axis)
    {
        string along = axis == 0 ? "X" : "Y";
        return new LayoutException(
            $"Unsolvable layout ring along {along}: {Path(child)} takes a share of its parent, " +
            $"and the parent is sized by its children. Give the parent a size along {along} " +
            "(pixels, or a share of a sized ancestor), or size this node by its content.");
    }

    internal static LayoutException NoSize(UiNode node, int axis)
    {
        string along = axis == 0 ? "X" : "Y";
        return new LayoutException(
            $"No size along {along} for {Path(node)}. Every node says its size: " +
            $"Px, FillX/FillY, sized by its children, or pushed with PushSize{along}.");
    }

    // The node and its ancestors up to the first one with an id, so the
    // message points at code that can be found.
    private static string Path(UiNode node)
    {
        var path = new System.Text.StringBuilder();
        for (var n = node; n is not null; n = n.Parent)
        {
            path.Insert(0, n.Id is { } id ? $" > {n.GetType().Name} '{id}'" : $" > {n.GetType().Name}");
            if (n.Id is not null && n != node) break;
        }
        var text = FirstText(node);
        var beside = node.Parent is { } parent && text is null ? FirstText(parent) : null;
        return path.ToString(3, path.Length - 3)
            + (text is not null ? $" (holding \"{text}\")" : beside is not null ? $" (beside \"{beside}\")" : "");
    }

    private static string? FirstText(UiNode node)
    {
        if (node is Text t) return new string(t.Content);
        for (var c = node.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
            if (FirstText(c) is { } found) return found;
        return null;
    }
}
