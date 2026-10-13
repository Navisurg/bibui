using System.Numerics;
using Bibui.Renderer;

namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    // The dropdowns open this frame and the last. Kept for no frames past the
    // one they were opened in: a dropdown not opened in a frame is gone when
    // that frame ends, so while a build runs this holds the ones open last
    // frame — whose rects are what they occupy — and any opened so far.
    private readonly ResourceCache<string, Box> _dropDowns = new(keepFrames: 0);

    /// <summary>
    /// The root of the dropdown named <paramref name="id"/>, open this frame:
    /// a floating box to build the dropdown into, placed and sized by the
    /// caller. It is linked nowhere while the build runs, whatever parent is
    /// pushed; when the build returns, the runtime links it as the last child
    /// of the tree, so it is drawn over everything.
    ///
    /// <para>While it is open, the rect it was drawn at last frame is its own:
    /// a box not under it gets no pointer event and no hover there, whenever
    /// it asks. A dropdown not opened in a frame is gone when the frame ends.</para>
    ///
    /// <para>The caller positions it, absolutely: <c>.At(x, y)</c> in window
    /// coordinates, typically from the trigger's rect as drawn last frame. So a
    /// trigger that moves while the dropdown is open is followed a frame late,
    /// and nothing keeps the dropdown inside the window.</para>
    /// </summary>
    // TODO: consider an anchor instead — OpenDropDown(id, anchor, offset): after
    // layout, move the root to the anchor's freshly solved rect plus the offset,
    // shift it back inside the window, and lay its subtree out again. No lag,
    // and contained.
    public Box OpenDropDown(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_syncRoot)
        {
            var root = _dropDowns.FromKey(id, id, static key => new Box { Id = key });
            ResetForBuild(root);
            root.Claims = InteractionKinds.None;
            root.ChildLayoutAxis = LayoutAxis.Y;
            root.FloatingX = root.FloatingY = true;
            return root;
        }
    }

    // The one tooltip root. Cleared as each build begins, so a build starts
    // with an empty one and the frame it built stays as it was; linked only
    // if something was built into it.
    private readonly Box _toolTip = EmptyToolTip(new Box { Id = "bibui-tooltip" });

    /// <summary>
    /// The tooltip: one floating column, sized by what is built into it. Every
    /// call in a frame returns the same box, so two tooltips in a frame stack
    /// in it. It claims nothing, so it never takes the pointer. The caller
    /// places it — <see cref="DefaultToolTipOffset"/> by the mouse, or
    /// <c>.At(x, y)</c>. Like a dropdown it is linked nowhere while the build
    /// runs and last when the build returns, after the dropdowns, so it is
    /// drawn over them too; with nothing built into it, it is not linked.
    /// </summary>
    public Box OpenToolTip()
    {
        lock (_syncRoot) return _toolTip;
    }

    /// <summary>Places <paramref name="box"/> where a tooltip goes by default: 15 px right of and below the mouse.</summary>
    public void DefaultToolTipOffset(Box box)
    {
        ArgumentNullException.ThrowIfNull(box);
        float x, y;
        lock (_syncRoot) (x, y) = (_mouseX, _mouseY);
        // Before any pointer event the mouse is nowhere; the corner, then.
        box.At((float.IsNaN(x) ? 0f : x) + 15f, (float.IsNaN(y) ? 0f : y) + 15f);
    }

    // The frame's opening marker, before build() runs.
    private void BeginBuild()
    {
        lock (_syncRoot) EmptyToolTip(_toolTip);
    }

    private static Box EmptyToolTip(Box box)
    {
        ResetForBuild(box);
        box.Claims = InteractionKinds.None;
        box.ChildLayoutAxis = LayoutAxis.Y;
        box.SizeX = box.SizeY = SemanticSize.ChildrenSum();
        box.FloatingX = box.FloatingY = true;
        return box;
    }

    // After the build: every dropdown opened this frame goes last, in the
    // order opened, so it is drawn over everything built before it; then the
    // tooltip, over them.
    private void LinkDropDowns(UiNode tree)
    {
        lock (_syncRoot)
        {
            foreach (var entry in _dropDowns.Entries.Values)
                if (entry.Touched == _dropDowns.Frame && entry.Value.Parent is null && entry.Value != tree)
                    UiNode.PushIntoParent(tree, entry.Value);
            if (_toolTip.FirstChild is not null && _toolTip.Parent is null) UiNode.PushIntoParent(tree, _toolTip);
        }
    }

    /// <summary>
    /// Whether <paramref name="pos"/> lies in a rect an open dropdown was drawn
    /// at last frame, and <paramref name="box"/> is not under that dropdown.
    /// </summary>
    private bool IsUnderDropDown(Box box, Vector2 pos)
    {
        foreach (var entry in _dropDowns.Entries.Values)
        {
            var root = entry.Value;
            if (pos.X < root.Rect.X || pos.Y < root.Rect.Y
                || pos.X >= root.Rect.X + root.Extent.X || pos.Y >= root.Rect.Y + root.Extent.Y) continue;
            bool inside = false;
            for (UiNode? n = box; n is not null; n = n.Parent == n ? null : n.Parent)
                if (n == root) { inside = true; break; }
            if (!inside) return true;
        }
        return false;
    }
}
