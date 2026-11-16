using System.Numerics;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

public sealed partial class BibuiRuntime
{
    // A dropdown's root, and the build it was last built in.
    private sealed class DropDown
    {
        public required Box Root;
        public ulong Build;
        public DropDown? NextDrawn; // _drawnDropDowns
        public DropDown? NextBuilt; // _builtDropDowns
    }

    // A list of dropdowns, by NextDrawn or by NextBuilt: whichever the list is.
    private struct DropDownList
    {
        public DropDown? First, Last;
    }

    // The dropdowns on screen: drawn last frame, bottom first. While a build
    // runs their rects are what they occupy, and their order is which is over
    // which. One built for the first time goes on the end, with no rect yet.
    private DropDownList _drawnDropDowns;

    // The dropdowns built this frame, in the order built: the order they are
    // linked in, so drawn in. When the build returns they are the drawn ones,
    // and a dropdown not built is pruned.
    private DropDownList _builtDropDowns;
    private ulong _dropDownBuild;

    // The dropdowns open, each with its anchor or none, and those opened
    // during this frame's build. Opening and closing change it at once, so
    // what is said last in a frame stands.
    private readonly Dictionary<string, UiNode?> _openDropDowns = new();
    private readonly HashSet<string> _openedThisFrame = new();

    /// <summary>
    /// Opens the dropdown named <paramref name="id"/> until
    /// <see cref="CloseDropDown"/> closes it or a press dismisses it: a press
    /// that a box not under its root or under <paramref name="anchor"/> (its
    /// trigger, typically) takes, or one that nothing takes. A box dismisses as
    /// it takes the press, before its caller reads it, so a caller that opens
    /// the dropdown on that press opens it again; and one opened every frame it
    /// is built, as <see cref="Dialog"/> is, is never dismissed. A frame that
    /// neither opens nor begins it closes it. Opening one that was not open
    /// gives it focus, so Escape reaches it (<see cref="InteractionKinds.Escape"/>);
    /// opening it again, as <see cref="Dialog"/> does every frame, does not.
    /// Opening one that was not open, or closing one that was, asks for a
    /// frame: the change may come after the dropdown was begun this frame.
    /// </summary>
    public void OpenDropDown(string id, UiNode? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool opened;
        opened = _openDropDowns.TryAdd(id, anchor);
        if (!opened) _openDropDowns[id] = anchor;
        _openedThisFrame.Add(id);
        if (opened)
        {
            RequestFrame();
            Focus(id);
        }
    }


    /// <summary>Whether the dropdown named <paramref name="id"/> is open.</summary>
    public bool IsDropDownOpen(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _openDropDowns.ContainsKey(id);
    }

    /// <summary>
    /// The root of the open dropdown named <paramref name="id"/> to build into
    /// this frame, or null when it is not open: a floating box, placed and
    /// sized by the caller. It is linked nowhere while the build runs, whatever
    /// parent is pushed; when the build returns, the runtime links it as the
    /// last child of the tree, so it is drawn over everything.
    ///
    /// <para>The rect it was drawn at last frame is its own over what is drawn
    /// under it: a box not built in it or in a dropdown drawn over it gets no
    /// pointer event and no hover there, whenever it asks. Its rows ask
    /// as usual; the root asks after them, with <see cref="InteractionKinds.Press"/>,
    /// so a press on its padding is the dropdown's.</para>
    ///
    /// <para>The caller positions it, absolutely: <c>.At(x, y)</c> in window
    /// coordinates, typically from the trigger's rect as drawn last frame. So a
    /// trigger that moves while the dropdown is open is followed a frame late,
    /// and nothing keeps the dropdown inside the window.</para>
    /// </summary>
    // TODO: place it by its anchor — after layout, move the root to the
    // anchor's freshly solved rect plus an offset, shift it back inside the
    // window, and lay its subtree out again. No lag, and contained.
    public Box? BeginDropDown(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_openDropDowns.ContainsKey(id)) return null;
        var dropDown = DropDownFromId(id);
        if (dropDown is null)
        {
            dropDown = new DropDown { Root = new Box { Id = id, PopupRoot = true } };
            if (_drawnDropDowns.Last is null) _drawnDropDowns.First = dropDown; else _drawnDropDowns.Last.NextDrawn = dropDown;
            _drawnDropDowns.Last = dropDown;
        }
        // A build counts from 1, so a new dropdown is not yet built in it.
        if (dropDown.Build != _dropDownBuild)
        {
            dropDown.Build = _dropDownBuild;
            dropDown.NextBuilt = null;
            if (_builtDropDowns.Last is null) _builtDropDowns.First = dropDown; else _builtDropDowns.Last.NextBuilt = dropDown;
            _builtDropDowns.Last = dropDown;
        }
        var root = dropDown.Root;
        ResetForBuild(root);
        root.Claims = InteractionKinds.None;
        root.ChildLayoutAxis = LayoutAxis.Y;
        root.FloatingX = root.FloatingY = true;
        return root;
    }

    /// <summary>
    /// The root of the open dropdown named <paramref name="id"/> as a float, or
    /// null when it is not open: what a select's list or a menu opened from its
    /// trigger is. Unlike <see cref="BeginDropDown"/> it is built where it is
    /// called, a child of the parent in scope, so what is built in it is under
    /// the boxes around it: a press on a row of a select inside a dropdown is
    /// inside that dropdown, and does not close it. It is drawn as a dropdown is,
    /// lifted to the end of the tree when the build returns, so it is over
    /// everything and clipped by nothing it was built in; and while open it
    /// owns the rect it was drawn at. Placed <paramref name="gap"/> px under
    /// <paramref name="anchor"/>, left edges aligned, where the anchor was
    /// drawn last frame, so it follows a moving anchor a frame late. The caller
    /// sizes it and builds into it, as into a dropdown.
    /// </summary>
    public Box? BeginFloat(string id, UiNode anchor, float gap)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (BeginDropDown(id) is not { } root) return null;
        UiNode.PushIntoParent(_parentStack[^1], root);
        root.At(anchor.Rect.X, anchor.Rect.Y + anchor.Extent.Y + gap);
        return root;
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
        return _toolTip;
    }

    /// <summary>Places <paramref name="box"/> where a tooltip goes by default: 15 px right of and below the mouse.</summary>
    public void DefaultToolTipOffset(Box box)
    {
        ArgumentNullException.ThrowIfNull(box);
        float x, y;
        (x, y) = (_mouseX, _mouseY);
        // Before any pointer event the mouse is nowhere; the corner, then.
        box.At((float.IsNaN(x) ? 0f : x) + 15f, (float.IsNaN(y) ? 0f : y) + 15f);
    }

    // The frame's opening marker, before build() runs.
    private void BeginDropDowns()
    {
        EmptyToolTip(_toolTip);
        _openedThisFrame.Clear();
        _dropDownBuild++;
        _builtDropDowns = default;
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

    // ── What closes a dropdown ───────────────────────────────────────────────
    //
    // A dropdown is open until its caller closes it (CloseDropDown) or one of
    // these does, which is all that ever closes one:
    //   - a press a box takes, when the box is not under it or its anchor
    //     (PressTaken, as the press is taken);
    //   - at the end of the frame (EndDropDowns), in this order:
    //       1. an Escape nothing ate: the dropdown that has focus, if it claims
    //          Escape, and focus moves to the next one out;
    //       2. a press nothing took: every dropdown not opened since it came;
    //       3. a dropdown neither begun nor opened this frame: its caller
    //          stopped building it.
    // A box is under a float it was built in, and so under every dropdown the
    // float was built in: a select's list does not close the dropdown it is in.

    /// <summary>Closes the dropdown named <paramref name="id"/>: an item in it was chosen, say.</summary>
    public void CloseDropDown(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool closed;
        closed = _openDropDowns.Remove(id);
        if (closed) RequestFrame();
    }

    // A box took a press: every open dropdown it is not under, by the
    // dropdown's root or its anchor, is dismissed.
    private void PressTaken(Box box)
    {
        List<string>? dismissed = null;
        foreach (var (id, anchor) in _openDropDowns)
        {
            Box? root = DropDownFromId(id)?.Root;
            bool under = false;
            for (UiNode? n = box; n is not null && !under; n = n.Parent == n ? null : n.Parent)
                under = n == root || n == anchor;
            if (!under) (dismissed ??= new()).Add(id);
        }
        if (dismissed is null) return;
        foreach (var id in dismissed) _openDropDowns.Remove(id);
        RequestFrame();
    }

    // After the build: the closing rules above, then focus, then every
    // dropdown built this frame goes last in the tree, in the order built, so
    // it is drawn over everything built before it; floats are lifted out of
    // where they were built. Then the tooltip, over them. The built dropdowns
    // are the drawn ones from here on.
    private void EndDropDowns(UiNode tree)
    {
        // 1. Escape. Floats are still where they were built, so the walk up
        // from the focused box passes every dropdown it is in.
        for (var ev = _firstEvent; ev is not null; ev = ev.Next)
        {
            if (ev.Consumed || ev is not { IsKeyDown: true, Key: Key.Escape }) continue;
            if (FocusedDropDown(_focused) is not { } escaped) break;
            ev.Eat();
            _openDropDowns.Remove(escaped.Id!);
            RequestFrame();
            Focus(FocusedDropDown(escaped.Parent)?.Id ?? "");
            break;
        }

        // 2 and 3.
        bool untaken = false;
        for (var ev = _firstEvent; ev is not null; ev = ev.Next)
            untaken |= !ev.Consumed && ev.Kind == BibUiEventKind.MouseDown;
        List<string>? closing = null;
        foreach (var id in _openDropDowns.Keys)
            if (!_openedThisFrame.Contains(id) && (untaken || DropDownFromId(id)?.Build != _dropDownBuild))
                (closing ??= new()).Add(id);
        if (closing is not null)
        {
            foreach (var id in closing) _openDropDowns.Remove(id);
            RequestFrame();
        }

        // Focus nothing holds for next frame stays in the open dropdown it was
        // in, so the next Escape still reaches it: a press inside one, or a
        // field in it giving focus up, does not leave it without focus.
        if (_focusedNext.Length == 0 && FocusedDropDown(_focused) is { } holding)
            _focusedNext = holding.Id!;

        for (var d = _builtDropDowns.First; d is not null; d = d.NextBuilt)
        {
            if (d.Root != tree) UiNode.PushIntoParent(tree, d.Root);
            d.NextDrawn = d.NextBuilt;
        }
        _drawnDropDowns = _builtDropDowns;
        if (_toolTip.FirstChild is not null && _toolTip.Parent is null) UiNode.PushIntoParent(tree, _toolTip);
    }

    // The open dropdown built this frame that claims Escape, nearest at or
    // above the box named `id`; null when there is none.
    private Box? FocusedDropDown(string id) =>
        id.Length == 0 ? null : FocusedDropDown(PeekBox(id) ?? BuiltDropDownRoot(id));

    private Box? FocusedDropDown(UiNode? node)
    {
        for (; node is not null; node = node.Parent)
            if (node is Box { PopupRoot: true, Id: { } id } root && (root.Claims & InteractionKinds.Escape) != 0
                && _openDropDowns.ContainsKey(id) && DropDownFromId(id) is { } d && d.Build == _dropDownBuild)
                return root;
        return null;
    }

    private Box? BuiltDropDownRoot(string id)
    {
        for (var d = _builtDropDowns.First; d is not null; d = d.NextBuilt)
            if (d.Root.Id == id) return d.Root;
        return null;
    }

    /// <summary>
    /// Whether <paramref name="pos"/> lies in a rect a dropdown drawn over
    /// <paramref name="box"/> was drawn at last frame. A box is drawn with the
    /// dropdown it is built in, or under them all; boxes ask while the build
    /// runs, so the first dropdown root above a box, or the top of its tree,
    /// is the dropdown it is in.
    /// </summary>
    private bool IsUnderDropDown(Box box, Vector2 pos)
    {
        // Bottom first: a rect that holds pos covers the box, until the
        // box's own dropdown comes, which is over what came before. Which
        // dropdown that is matters only once something covers, so the walk
        // to the top waits until then.
        UiNode? top = null;
        bool covered = false;
        for (var d = _drawnDropDowns.First; d is not null; d = d.NextDrawn)
        {
            var root = d.Root;
            if (!covered && !(pos.X >= root.Rect.X && pos.Y >= root.Rect.Y
                && pos.X < root.Rect.X + root.Extent.X && pos.Y < root.Rect.Y + root.Extent.Y)) continue;
            if (top is null)
            {
                top = box;
                while (top.Parent is not null && top is not Box { PopupRoot: true }) top = top.Parent;
            }
            covered = root != top;
        }
        return covered;
    }

    private DropDown? DropDownFromId(string id)
    {
        for (var d = _drawnDropDowns.First; d is not null; d = d.NextDrawn)
            if (d.Root.Id == id) return d;
        return null;
    }
}
