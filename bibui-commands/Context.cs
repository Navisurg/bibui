namespace Bibui.Commands;

// The context: which node of each kind the code running now is about (the panel, the case, the view).
// It is a reader monad: code reads its context and never writes it; a scope runs the code inside it in a
// changed context (Push: the monad's `local`), and the context outside is as it was when the scope ends,
// with nothing to remember or restore. The UI pushes as it descends (a panel, then a card in it, then the
// card's row); what it draws and the commands it makes read the top (Top): a command's arguments filled
// from its slots (cmdgen's FromContext, Missing). Never the truth, which is the model: made each frame.
//
// Kept in memory cleared with the frame: one array of entries, each a slot set (a kind and its node) and
// a link to the entry it was set over. A push adds an entry; a scope's end moves the top back, the entries
// staying until the frame's Clear; so a Context (the index of an entry) stays good for the whole frame,
// after its scope too. Reading a slot walks the links to the nearest entry of its kind: a few steps (a
// panel, a card, a row). Nothing is allocated once the array has grown.

/// <summary>
/// A context: the nodes code is about, by kind, and the view (the node a view is drawn for, whose view
/// state its lens keeps). What <see cref="ContextStack.Top"/> gives; good until the stack's next <see cref="ContextStack.Clear"/>.
/// </summary>
public readonly struct Context
{
    readonly ContextStack? stack;
    readonly int at;   // its newest entry; 0, the empty one
    internal Context(ContextStack stack, int at) => (this.stack, this.at) = (stack, at);

    /// <summary>The slot of the view (<see cref="View"/>): kind 0, None, never a node's kind.</summary>
    public const ushort ViewSlot = Kinds.None;

    /// <summary>The node of kind <paramref name="kind"/> (an index: NodeKinds.Case) the code is about: the nearest set; none for none.</summary>
    public NodeId Of(ushort kind)
    {
        if (stack is null) return default;
        var entries = stack.Entries;
        for (int e = at; e != 0; e = entries[e].Parent)
            if (entries[e].Kind == kind) return entries[e].Node;
        return default;
    }

    /// <summary>The node the view being drawn is for (a panel; a case, for its card's parts): whose view state its lens keeps. None for a value drawn on its own.</summary>
    public NodeId View => Of(ViewSlot);
}

/// <summary>
/// Contexts as a stack: <see cref="Top"/> what code reads now; <see cref="Push()"/> a scope over it, whose
/// slots are set with Set and View (<c>using (ctx.Push().Set(NodeKinds.Panel, p).View(p)) …</c>), on top
/// until the scope is disposed. Cleared with the frame (<see cref="Clear"/>). One thread's.
/// </summary>
public sealed class ContextStack
{
    /// <summary>One slot set: its kind, its node, and the entry it was set over.</summary>
    internal struct Entry
    {
        public int Parent;
        public ushort Kind;
        public NodeId Node;
    }

    internal Entry[] Entries = new Entry[64];
    int count = 1;   // entries made this frame; 0 is the empty context's
    int top;         // the entry code reads from now

    /// <summary>The context the code running now is in.</summary>
    public Context Top => new(this, top);

    /// <summary>A scope over the top, its slots as the top's until set; on top until disposed.</summary>
    public Scope Push() => new(this, top);

    /// <summary>A scope over the top with <paramref name="node"/> in slot <paramref name="kind"/>.</summary>
    public Scope Push(ushort kind, NodeId node) => Push().Set(kind, node);

    /// <summary>A scope over the top with <paramref name="node"/> as its view (none: a part drawn on its own, not as the view).</summary>
    public Scope PushView(NodeId node) => Push().View(node);

    /// <summary>The start of a frame: every context made in the last is gone, and the top is empty.</summary>
    public void Clear() => (count, top) = (1, 0);

    void Add(ushort kind, NodeId node)
    {
        if (count == Entries.Length) Array.Resize(ref Entries, count * 2);
        Entries[count] = new Entry { Parent = top, Kind = kind, Node = node };
        top = count++;
    }

    /// <summary>
    /// A pushed scope: Set and View set slots for the code inside it, and Dispose ends it, the top back to
    /// what it was over. Only in a <c>using</c>, so scopes end in the order they began.
    /// </summary>
    public readonly ref struct Scope
    {
        readonly ContextStack stack;
        readonly int over;   // the top it was pushed over: what Dispose makes the top again
        internal Scope(ContextStack stack, int over) => (this.stack, this.over) = (stack, over);

        /// <summary>The node in slot <paramref name="kind"/>, for the code inside this scope.</summary>
        public Scope Set(ushort kind, NodeId node)
        {
            stack.Add(kind, node);
            return this;
        }

        /// <summary>The view, for the code inside this scope.</summary>
        public Scope View(NodeId node) => Set(Context.ViewSlot, node);

        public void Dispose() => stack.top = over;
    }
}
