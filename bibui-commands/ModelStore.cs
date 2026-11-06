using System.Diagnostics;

namespace Bibui.Commands;

// The model's state: its shape, a tree of ids (Tree.cs), and beside it, by those ids, its data (its kinds'
// tables: Tables.cs), its clock, and what each generation did to the shape (its edit log, for the change
// output). Nodes is the model's tree editing: the tree's, plus what the model keeps by an id. A node is
// named from outside the tree by its id (NodeId): a command, a field, a view state, an answer to a client.

/// <summary>What an edit did to the shape: a node added (under its parent), removed (with everything under it), moved (to another parent or place).</summary>
public enum EditOp : byte { Add, Remove, Move }

/// <summary>
/// One edit of the shape, in the generation it was made in: the node, its kind and slot, and its parent's,
/// kept here because a removed node is gone from the tree. What the change output says (+, -, moved).
/// </summary>
public readonly record struct TreeEdit(long Gen, EditOp Op, NodeId Node, ushort Kind, int Slot, NodeId Parent, ushort ParentKind, int ParentSlot);

/// <summary>
/// The model's store: everything it holds, behind one handle (Salsa's db): its shape (<see cref="Tree"/>, a
/// tree of ids: Tree.cs), its data (<see cref="Tables"/>, rows keyed by those ids, read and written through
/// a node's typed id: caseId.Modes(store), caseId.Modify(store, row)), its clock (the generation being written), and its edit log (what each generation
/// the log holds did to the shape, for the change output). Written only through <see cref="Nodes"/> and the
/// tables, by a command; read by anyone on the model's thread.
/// </summary>
public sealed class ModelStore
{
    public readonly Tree Tree = Trees.New(Kinds.Root);
    public object?[] Tables = [];    // by kind index (a node's Kind): a kind's rows, a Table<T, TId>; null for a kind with no fields
    public Schema? Schema;           // whose tables they are: its kinds add and remove a node's rows (Nodes.New, Release); set with them (Schemas.TablesOf)
    public ModelClock Clock = new(); // the generation being written: what a row and an edit are stamped with
    public readonly List<TreeEdit> Edits = new();        // what each generation the log holds did to the shape, in order
    public int EditsRetired;                             // how many of Edits are of generations the log no longer holds
    public readonly List<NodeId> Released = new();       // a release's ids: reused
}

/// <summary>
/// The model's shape, as a command edits it and the model reads it: the tree's own (Trees), and, beside
/// it, what the model keeps by a node's id. An add, a release or a move is kept in the edit log; a release
/// tells every table, so the node's rows read as none from then on (and are freed once no kept generation
/// reads them).
/// </summary>
public static class Nodes
{
    /// <summary>The root's slot: every tree has one, made with it, never released.</summary>
    public const int Root = Trees.Root;

    /// <summary>The root's name, to the evaluator and the port (<c>root</c>); its kind is <see cref="Kinds.Root"/>.</summary>
    public const string RootKey = "root";

    /// <summary>A model's state with its root, generation <paramref name="gen"/> being written, its tables the library's (the view rules); an app's are added by its schema (Schemas.TablesOf).</summary>
    public static ModelStore NewTree(long gen = 1)
    {
        var t = new ModelStore();
        (t.Clock.Gen, t.Clock.Live) = (gen, t);
        t.Tables = new object?[Kinds.FirstApp];
        t.Tables[Kinds.ViewRule] = ViewRules.NewTable(t.Clock);
        return t;
    }

    //- reading

    /// <summary>The node in <paramref name="slot"/>; the nil node for 0 or a slot past the end.</summary>
    public static TreeNode At(ModelStore t, int slot) => Trees.At(t.Tree, slot);

    /// <summary>The slot of the node with id <paramref name="id"/>, or 0 when the tree holds none (gone, or not yet made).</summary>
    public static int SlotOf(ModelStore t, NodeId id) => Trees.SlotOf(t.Tree, id);

    /// <summary>Whether the tree holds the node with id <paramref name="id"/>.</summary>
    public static bool Holds(ModelStore t, NodeId id) => Trees.SlotOf(t.Tree, id) != 0;

    /// <summary>The id of the node in <paramref name="slot"/>; none for 0.</summary>
    public static NodeId IdOf(ModelStore t, int slot) => At(t, slot).Id;

    /// <summary>The root's id: what code about things (a typed id's First) names the top of the tree by.</summary>
    public static NodeId RootOf(ModelStore t) => At(t, Root).Id;

    /// <summary>The kind of the node with id <paramref name="id"/>; <see cref="Kinds.None"/> when the tree holds none.</summary>
    public static ushort KindOf(ModelStore t, NodeId id) => At(t, SlotOf(t, id)).Kind;

    /// <summary>Whether the tree holds the node <paramref name="id"/>, of kind <paramref name="kind"/>: an id may be gone, or name another kind.</summary>
    public static bool HasKind(ModelStore t, NodeId id, ushort kind) => SlotOf(t, id) is var slot and not 0 && At(t, slot).Kind == kind;

    /// <summary>The first child of <paramref name="parent"/> of kind <paramref name="kind"/>, or 0.</summary>
    public static int Child(ModelStore t, int parent, ushort kind) => Trees.Child(t.Tree, parent, kind);

    /// <summary>The next sibling of <paramref name="slot"/> of its kind, or 0: the next of a list's items.</summary>
    public static int NextOfKind(ModelStore t, int slot) => Trees.NextOfKind(t.Tree, slot);

    //- editing

    /// <summary>A new node of kind <paramref name="kind"/>, the last child of <paramref name="parent"/>, or unlinked with parent 0 (placed with <see cref="InsertChild"/>).</summary>
    public static int New(ModelStore t, int parent, ushort kind)
    {
        int slot = Trees.New(t.Tree, parent, kind);
        if (parent != 0) Edit(t, EditOp.Add, slot);
        if (t.Schema is { } s) Schemas.Of(s, kind)?.AddRow?.Invoke(t, IdOf(t, slot));   // its kind's row: what a write to it finds
        return slot;
    }

    /// <summary>Links the unlinked node <paramref name="child"/> under <paramref name="parent"/>, after its child <paramref name="after"/>, or first when 0: a move.</summary>
    public static void InsertChild(ModelStore t, int parent, int after, int child)
    {
        Trees.Link(t.Tree, parent, after, child);
        Edit(t, EditOp.Move, child);
    }

    /// <summary>Takes <paramref name="slot"/>'s node out of its parent's children, its own children with it, to be inserted elsewhere.</summary>
    public static void Unlink(ModelStore t, int slot) => Trees.Unlink(t.Tree, slot);

    /// <summary>Releases the node with id <paramref name="id"/> and everything under it, as the slot form does; nothing for a node gone.</summary>
    public static void Release(ModelStore t, NodeId id)
    {
        if (SlotOf(t, id) is var slot and not 0) Release(t, slot);
    }

    /// <summary>
    /// Releases <paramref name="slot"/>'s node and everything under it: their rows removed (each while the tree
    /// still holds it: its kind's, and a view rule's), then from the tree (their ids
    /// find nothing from here on). Kept in the edit log once, for its top.
    /// </summary>
    public static void Release(ModelStore t, int slot)
    {
        Edit(t, EditOp.Remove, slot);
        for (int at = slot; at != 0; at = NextInTree(t, at, slot))
        {
            var id = IdOf(t, at);
            ViewRules.Table(t).RemoveRow(id);   // the library's
            if (t.Schema is not { } s) continue;
            Schemas.Of(s, At(t, at).Kind)?.RemoveRow?.Invoke(t, id);
        }
        t.Released.Clear();
        Trees.Release(t.Tree, slot, t.Released);
    }

    /// <summary>
    /// The slot after <paramref name="at"/> in depth-first order under <paramref name="top"/> (the whole tree,
    /// for the root): its first child, else its next sibling, else its nearest ancestor's below top; 0 past the last.
    /// </summary>
    public static int NextInTree(ModelStore t, int at, int top = Root)
    {
        if (At(t, at).First != 0) return At(t, at).First;
        while (at != top && At(t, at).Next == 0) at = At(t, at).Parent;
        return at == top ? 0 : At(t, at).Next;
    }

    /// <summary>Drops the edits of generations before <paramref name="oldest"/>: the log no longer holds them, so no change output reads them.</summary>
    public static void Retire(ModelStore t, long oldest)
    {
        while (t.EditsRetired < t.Edits.Count && t.Edits[t.EditsRetired].Gen < oldest) t.EditsRetired++;
        if (t.EditsRetired > 1024 && t.EditsRetired * 2 > t.Edits.Count)
        {
            t.Edits.RemoveRange(0, t.EditsRetired);
            t.EditsRetired = 0;
        }
    }

    // Keeps what an edit did to slot's node, in the generation being written.
    static void Edit(ModelStore t, EditOp op, int slot)
    {
        var n = At(t, slot);
        var p = At(t, n.Parent);
        t.Edits.Add(new(t.Clock.Gen, op, n.Id, n.Kind, slot, p.Id, p.Kind, n.Parent));
    }
}
