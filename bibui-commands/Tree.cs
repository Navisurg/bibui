using System.Diagnostics;

namespace Bibui.Commands;

// A tree of ids: shape only. A node is who it is (its id), what it is (its kind, an index: the model's
// schema says which, and it is also the index of the kind's table), and where it sits (its links); nothing
// else. The tree is built by code, not parsed from text, so a kind is a number; its name is the schema's,
// used only at the edges (the port, expressions, disk).
// What a node says lives elsewhere, keyed by its id (the model's tables, a view's state), so the shape can
// be walked and changed without touching any row. Written in place; it keeps no history and knows nothing
// of what is kept by its ids.
//
// Links are slots, indices into Nodes; slot 0 is nil, slot 1 the root. A released slot goes on a free list
// and is reused; an id is never given twice, so a stale id finds nothing.

/// <summary>One node: its id (given once, never reused, so a stale id finds nothing), its kind (0: none, a free slot), and its links, as slots (0: none).</summary>
public readonly record struct TreeNode(NodeId Id, ushort Kind, int Parent, int First, int Last, int Prev, int Next);

/// <summary>The nodes, in place. Written only through <see cref="Trees"/>.</summary>
public sealed class Tree
{
    public TreeNode[] Nodes = new TreeNode[64];
    public int Count = 2;                                    // slots handed out, nil and root included: the next fresh slot
    public int FreeFirst;                                    // released slots, linked through Next; 0 when none
    public long NextId = 2;                                  // the id the next node gets (the root's is 1)
    public long Changes;                                     // counts edits: what changed the shape since a reader last looked
    public readonly Dictionary<NodeId, int> Slots = new();   // a live node's id → its slot
}

public static class Trees
{
    /// <summary>The root's slot: every tree has one, made with it, never released.</summary>
    public const int Root = 1;

    /// <summary>A tree with its nil node (slot 0) and its root (slot 1, id 1) of kind <paramref name="rootKind"/>.</summary>
    public static Tree New(ushort rootKind)
    {
        var t = new Tree();
        t.Nodes[Root] = new TreeNode(new(1), rootKind, 0, 0, 0, 0, 0);
        t.Slots[new(1)] = Root;
        return t;
    }

    //- reading

    /// <summary>The node in <paramref name="slot"/>; the nil node for 0 or a slot past the end.</summary>
    public static TreeNode At(Tree t, int slot) => (uint)slot < (uint)t.Count ? t.Nodes[slot] : t.Nodes[0];

    /// <summary>The slot of the node <paramref name="id"/>, or 0 when the tree holds none (released, or never made).</summary>
    public static int SlotOf(Tree t, NodeId id) => t.Slots.GetValueOrDefault(id);

    /// <summary>The first child of <paramref name="parent"/> of kind <paramref name="kind"/>, or 0.</summary>
    public static int Child(Tree t, int parent, ushort kind)
    {
        for (int c = At(t, parent).First; c != 0; c = At(t, c).Next)
            if (At(t, c).Kind == kind) return c;
        return 0;
    }

    /// <summary>The next sibling of <paramref name="slot"/> of its kind, or 0: the next of a list's items.</summary>
    public static int NextOfKind(Tree t, int slot)
    {
        ushort kind = At(t, slot).Kind;
        for (int c = At(t, slot).Next; c != 0; c = At(t, c).Next)
            if (At(t, c).Kind == kind) return c;
        return 0;
    }

    //- editing

    /// <summary>
    /// A new node of kind <paramref name="kind"/>, the last child of <paramref name="parent"/>, or unlinked
    /// with parent 0 (placed later with <see cref="Link"/>). A released slot is reused before a fresh one
    /// is taken; either way the node gets an id never given before.
    /// </summary>
    public static int New(Tree t, int parent, ushort kind)
    {
        int slot;
        if (t.FreeFirst != 0)
        {
            slot = t.FreeFirst;
            t.FreeFirst = t.Nodes[slot].Next;
        }
        else
        {
            slot = t.Count++;
            if (slot == t.Nodes.Length) Array.Resize(ref t.Nodes, slot * 2);
        }
        Write(t, slot) = new TreeNode(new(t.NextId++), kind, 0, 0, 0, 0, 0);
        t.Slots[t.Nodes[slot].Id] = slot;
        if (parent != 0) Link(t, parent, At(t, parent).Last, slot);
        return slot;
    }

    /// <summary>Links the unlinked node <paramref name="child"/> under <paramref name="parent"/>, after its child <paramref name="after"/> (first when 0).</summary>
    public static void Link(Tree t, int parent, int after, int child)
    {
        Debug.Assert(At(t, child).Parent == 0, "a node is unlinked before it is linked");
        int next = after != 0 ? At(t, after).Next : At(t, parent).First;
        Write(t, child) = t.Nodes[child] with { Parent = parent, Prev = after, Next = next };
        if (after != 0) Write(t, after) = t.Nodes[after] with { Next = child };
        else Write(t, parent) = t.Nodes[parent] with { First = child };
        if (next != 0) Write(t, next) = t.Nodes[next] with { Prev = child };
        else Write(t, parent) = t.Nodes[parent] with { Last = child };
    }

    /// <summary>Takes <paramref name="slot"/>'s node out of its parent's children, its own children with it.</summary>
    public static void Unlink(Tree t, int slot)
    {
        var n = At(t, slot);
        if (n.Parent == 0) return;
        if (n.Prev != 0) Write(t, n.Prev) = t.Nodes[n.Prev] with { Next = n.Next };
        else Write(t, n.Parent) = t.Nodes[n.Parent] with { First = n.Next };
        if (n.Next != 0) Write(t, n.Next) = t.Nodes[n.Next] with { Prev = n.Prev };
        else Write(t, n.Parent) = t.Nodes[n.Parent] with { Last = n.Prev };
        Write(t, slot) = t.Nodes[slot] with { Parent = 0, Prev = 0, Next = 0 };
    }

    /// <summary>
    /// Releases <paramref name="slot"/>'s node and everything under it: their slots go on the free list and
    /// their ids find nothing from here on. Each id released is added to <paramref name="released"/>, for
    /// whoever keeps something by it (the model's tables).
    /// </summary>
    public static void Release(Tree t, int slot, List<NodeId> released)
    {
        Debug.Assert(slot != 0 && slot != Root, "nil and the root are never released");
        Unlink(t, slot);
        // Leaves first, without a stack: go down first children to a leaf, release it (it is its parent's
        // first child, so the parent's First moves to its next), then go on from its next sibling, or from
        // its parent once the parent has no children left.
        int at = slot;
        while (true)
        {
            while (At(t, at).First != 0) at = At(t, at).First;
            var leaf = At(t, at);
            released.Add(leaf.Id);
            t.Slots.Remove(leaf.Id);
            Write(t, at) = new TreeNode(default, 0, 0, 0, 0, 0, t.FreeFirst);
            t.FreeFirst = at;
            if (at == slot) break;
            Write(t, leaf.Parent) = t.Nodes[leaf.Parent] with { First = leaf.Next, Last = leaf.Next == 0 ? 0 : t.Nodes[leaf.Parent].Last };
            if (leaf.Next != 0) Write(t, leaf.Next) = t.Nodes[leaf.Next] with { Prev = 0 };
            at = leaf.Next != 0 ? leaf.Next : leaf.Parent;
        }
    }

    // The slot, writable; counted as an edit.
    static ref TreeNode Write(Tree t, int slot)
    {
        t.Changes++;
        return ref t.Nodes[slot];
    }
}
