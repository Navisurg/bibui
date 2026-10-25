using System.Numerics;
using Bibui.Core;

namespace Bibui.Testing;

/// <summary>
/// Reading a laid-out tree from outside the build: a test, a self-test, a
/// harness that drives an app with the events a person would send. The tree is
/// the app's own — the root it built into — and is read between frames, after
/// <c>EndBuild</c> and before the next <c>BeginBuild</c>, which resets and
/// reuses its nodes. Nothing here builds a frame or keeps a node.
/// </summary>
public static class Nodes
{
    /// <summary>The first node under <paramref name="root"/>, itself included, named <paramref name="id"/>, depth first; null when none is.</summary>
    public static UiNode? FindById(UiNode root, string id)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(id);
        if (root.Id == id) return root;
        for (var child = root.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
            if (FindById(child, id) is { } found) return found;
        return null;
    }

    /// <summary>The centre of <paramref name="node"/>'s rect as last laid out, in logical px: where a person would click it.</summary>
    public static Vector2 Centre(UiNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Rect + node.Extent / 2f;
    }
}
