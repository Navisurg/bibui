using System.Numerics;
using Bibui.Renderer;
using Bibui.TextEdit;

namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    // A dropdown's root, and the build it was last begun in.
    private sealed class DropDown
    {
        public required Box Root;
        public ulong Build;
        public DropDown? Next;      // _dropDowns: the order drawn last frame
        public DropDown? NextBegun; // _begun: the order begun this build
    }

    // The dropdowns drawn last frame, bottom first: while a build runs, their
    // rects are what they occupy and their order is which is over which. One
    // begun for the first time goes on the end, with no rect yet. When the
    // build returns, the list is _begun: a dropdown not begun is pruned.
    private DropDown? _dropDowns;

    // The dropdowns begun this build, in the order begun: the order they are
    // linked in, so drawn in.
    private DropDown? _begun, _begunLast;
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
    /// neither opens nor begins it closes it. Opening one that was not open,
    /// or closing one that was, asks for a frame: the change may come after
    /// the dropdown was begun this frame.
    /// </summary>
    public void OpenDropDown(string id, UiNode? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool opened;
        lock (_syncRoot)
        {
            opened = _openDropDowns.TryAdd(id, anchor);
            if (!opened) _openDropDowns[id] = anchor;
            _openedThisFrame.Add(id);
        }
        if (opened) RequestFrame();
    }

    /// <summary>Closes the dropdown named <paramref name="id"/>: an item in it was chosen, say.</summary>
    public void CloseDropDown(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool closed;
        lock (_syncRoot) closed = _openDropDowns.Remove(id);
        if (closed) RequestFrame();
    }

    /// <summary>Whether the dropdown named <paramref name="id"/> is open.</summary>
    public bool IsDropDownOpen(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_syncRoot) return _openDropDowns.ContainsKey(id);
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
        lock (_syncRoot)
        {
            if (!_openDropDowns.ContainsKey(id)) return null;
            var dropDown = DropDownFromId(id);
            bool again = dropDown?.Build == _dropDownBuild;
            if (dropDown is null)
            {
                dropDown = new DropDown { Root = new Box { Id = id } };
                if (_dropDowns is null) _dropDowns = dropDown;
                else
                {
                    var last = _dropDowns;
                    while (last.Next is not null) last = last.Next;
                    last.Next = dropDown;
                }
            }
            if (!again)
            {
                dropDown.Build = _dropDownBuild;
                dropDown.NextBegun = null;
                if (_begunLast is null) _begun = dropDown; else _begunLast.NextBegun = dropDown;
                _begunLast = dropDown;
            }
            var root = dropDown.Root;
            ResetForBuild(root);
            root.Claims = InteractionKinds.None;
            root.ChildLayoutAxis = LayoutAxis.Y;
            root.FloatingX = root.FloatingY = true;
            return root;
        }
    }

    // A box took a press: every open dropdown it is not under, by the
    // dropdown's root or its anchor, is dismissed.
    private void PressTaken(Box box)
    {
        lock (_syncRoot)
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
        }
        RequestFrame();
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
        lock (_syncRoot)
        {
            EmptyToolTip(_toolTip);
            _openedThisFrame.Clear();
            _dropDownBuild++;
            _begun = _begunLast = null;
        }
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

    // After the build. A press nobody took dismisses every open dropdown but
    // those opened since it came, and one neither begun nor opened this frame
    // closes: its caller stopped building it. Then every dropdown begun this
    // frame goes last, in the order begun, so it is drawn over everything
    // built before it; then the tooltip, over them. Last, the order begun is
    // the list, and a dropdown not begun is pruned from it.
    private void EndDropDowns(UiNode tree)
    {
        lock (_syncRoot)
        {
            bool untaken = false;
            foreach (var handle in _frameEvents)
                untaken |= !handle.Consumed && handle.Event.Kind == BibUiEventKind.MouseDown;
            List<string>? closing = null;
            foreach (var id in _openDropDowns.Keys)
            {
                bool begun = DropDownFromId(id)?.Build == _dropDownBuild;
                if (!_openedThisFrame.Contains(id) && (untaken || !begun)) (closing ??= new()).Add(id);
            }
            if (closing is not null)
            {
                foreach (var id in closing) _openDropDowns.Remove(id);
                RequestFrame();
            }

            for (var d = _begun; d is not null; d = d.NextBegun)
                if (d.Root.Parent is null && d.Root != tree) UiNode.PushIntoParent(tree, d.Root);
            if (_toolTip.FirstChild is not null && _toolTip.Parent is null) UiNode.PushIntoParent(tree, _toolTip);

            for (var d = _begun; d is not null; d = d.NextBegun) d.Next = d.NextBegun;
            _dropDowns = _begun;
            _begun = _begunLast = null;
        }
    }

    /// <summary>
    /// Whether <paramref name="pos"/> lies in a rect a dropdown drawn over
    /// <paramref name="box"/> was drawn at last frame. A box is drawn with the
    /// dropdown it is built in, or under them all; boxes ask while the build
    /// runs, when a dropdown's root has no parent, so the top of a box's tree
    /// is the dropdown it is in.
    /// </summary>
    private bool IsUnderDropDown(Box box, Vector2 pos)
    {
        lock (_syncRoot)
        {
            // Bottom first: a rect under the box's own dropdown stops counting
            // there. Which dropdown that is matters only once a rect holds pos,
            // so the walk to the top waits until then.
            UiNode? top = null;
            bool covered = false;
            for (var d = _dropDowns; d is not null; d = d.Next)
            {
                var root = d.Root;
                bool holds = pos.X >= root.Rect.X && pos.Y >= root.Rect.Y
                    && pos.X < root.Rect.X + root.Extent.X && pos.Y < root.Rect.Y + root.Extent.Y;
                if (!holds && !covered) continue;
                if (top is null)
                {
                    top = box;
                    while (top.Parent is not null) top = top.Parent;
                }
                if (root == top) covered = false;
                else covered |= holds;
            }
            return covered;
        }
    }

    // Called under _syncRoot.
    private DropDown? DropDownFromId(string id)
    {
        for (var d = _dropDowns; d is not null; d = d.Next)
            if (d.Root.Id == id) return d;
        return null;
    }
}
