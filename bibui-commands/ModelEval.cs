namespace Bibui.Commands;

// The model, to the evaluator: a type per kind of node, made from the schema, so every read of a node
// goes through what its kind declares. The model is one Place, `model` (Places.cs), entered by its own
// symbols (root, $15), not by name. Each node is a root address in it, keyed by its id rather than by
// the path it was reached by, so a node reached two ways (as a child, and through a handle field) is one
// address, and a view state or cache keyed on it finds the same entry; its slot and id are also in At
// and Id. No value holds the tree: a hook reads the tree of the context it is evaluated in
// (Eval.Current's Model: the published generation). A node's parts, in order (node[i] is its i-th):
//
//   its fields      by its row's shape (its kind's Shape, cmdgen's): a leaf, read through the Place from
//                   the node's newest row (its declared default before one is written), typed as the
//                   field and kept at its address (a write finds the node and key from it: AddressOf); a
//                   list, its address, stepped on by shape; a handle field reads as the node it names,
//                   none once that is gone
//   its child kinds a kind under it one: the child node; many: the set of those children, in order
//
// Its fields are steps of its row's shape (0 up); its child kinds are hooks, at steps no row
// member has (below 0), so an address never names two things. Shown, a set of a kind is a table of that
// kind's fields (its type's Fields).
//
// Other layers are not reached through it, nor it through them: each is a Place of its own, and an
// expression builds the key into one from the model's facts (vessels(it.title, it.modes[it.picked].index)).
// A kind's row of commands is the schema's ([Row]), read where the node is drawn (Schemas.RowOf), not a
// member of its nodes: an expression over a node sees its facts, never the buttons shown beside it.
// Writing is a command's: Set is what a generic node.set applies, checked by the field's type and its
// table.
//
// An address literal, $15, is the node with id 15 in the tree being evaluated: a node's id is its
// name in any expression, stable across generations, so a lens or query written with it keeps working.
//
// Nothing here is particular to an app: any node set's schema makes its types.

/// <summary>
/// The model, as an evaluation context reads it (EvalContext.Model): its types, the tree it reads (the
/// published generation), and its Place: a node is a root in it, keyed by its id. Made by
/// ModelEval.AddSymbols.
/// </summary>
public sealed class ModelRoot
{
    public required ModelTypes Types;
    public required Func<ModelStore> Tree;
    public required Place<NodeId> Place;
}

/// <summary>The types of one schema's kinds: made once, for every tree of that schema.</summary>
public sealed class ModelTypes
{
    public readonly Schema Schema;
    public readonly EvalType?[] Kinds;                            // a node of the kind, by its index
    public readonly EvalType?[] Sets;                             // the children of a kind under one node (a kind that sits under another as many), by its index
    public readonly Dictionary<EvalType, string> Fields = new();  // a field's type → its key
    public readonly Dictionary<(string Kind, string Key), EvalType> ByField = new();   // the same, by kind and key: to give one a lens
    public readonly Dictionary<string, EvalType> Named = new();   // a type by the name a view rule gives it: a kind (case), a list of one (case[]), a field (frame.zoom)

    public ModelTypes(Schema schema)
    {
        Schema = schema;
        Kinds = new EvalType?[schema.Kinds.Length];
        Sets = new EvalType?[schema.Kinds.Length];
    }
}

public static class ModelEval
{
    /// <summary>The types of <paramref name="schema"/>'s kinds.</summary>
    public static ModelTypes MakeEvalTypes(Schema schema)
    {
        var types = new ModelTypes(schema);
        foreach (var kind in schema.Kinds)
        {
            if (kind is null) continue;
            var k = kind;
            // Its fields: its row's leaves, each its own type (cmdgen's: its view rule's name, whether it is
            // editable, its choices), registered by key and by name.
            foreach (var (key, field) in k.Shape?.Members ?? [])
                if (field.Members is null && field.PartType is null)
                {
                    types.ByField[(k.Name, key)] = field;
                    types.Named[field.Name] = field;   // what a view rule names it by: its [Pick], [Range] (NodeKinds.NewTree)
                    types.Fields[field] = key;
                }
            types.Kinds[k.Index] = new EvalType
            {
                Name = k.Name, Description = k.Description, Kind = EvalKind.Parts,
                Member = (in Value of, string name) => Member(types, k, of, name),
                Count = (in Value of) => Slot(of) == 0 ? 0 : PartCount(k),
                Index = (in Value of, in Value at) =>
                    Slot(of) != 0 && at is { Kind: EvalKind.Int, At: >= 0 } && at.At < PartCount(k) ? Part(types, k, of, at.At) : Value.None,
                PartName = i => PartName(schema, k, i),
                Members = k.Shape?.Members,
                Fields = k.Shape?.Fields ?? [],
                Text = (in Value v) => $"{k.Name} {new NodeId(v.Id)}",
                Label = (in Value v) => $"{k.Name} {new NodeId(v.Id)}",
            };
            types.Named[k.Name] = types.Kinds[k.Index]!;
        }
        // A set per kind that sits under some kind as many: the children of that kind under one node.
        foreach (var kind in schema.Kinds)
            foreach (var child in kind?.Children ?? [])
                if (child.Many && types.Sets[child.Kind] is null && Schemas.Of(schema, child.Kind) is { } of)
                {
                    var under = child.Kind;
                    types.Sets[under] = new EvalType
                    {
                        Name = $"{of.Name}[]", Description = of.Description, Kind = EvalKind.Parts, PartType = types.Kinds[under],
                        Count = (in Value set) => Count(set, under),
                        Index = (in Value set, in Value at) => at is { Kind: EvalKind.Int, At: >= 0 } ? Child(types, set, under, at.At) : Value.None,
                    };
                    types.Named[$"{of.Name}[]"] = types.Sets[under]!;
                }
        return types;
    }

    //- view rules: the model's, for a type of model node, each replacing the type's default

    /// <summary>
    /// Sets the model's view rule for <paramref name="type"/> (a kind, case; a list of one, case[]; a field,
    /// case.picked): every value of it seen through <paramref name="lens"/>, a lens called over it (it); none
    /// removes it. A rule is a view_rule node under the root's view_rules (Schemas.Make), one per type,
    /// whether the tree started with it (cmdgen's NewTree) or a command set it since. Nothing for a type the
    /// tree's schema does not name (Schemas.NamesType), or a lens that is not a call of a lens
    /// <paramref name="lenses"/> has (any call, without a table): refused as it is set, not shown later as nothing.
    /// </summary>
    public static void SetViewRuleForType(ModelStore t, LensTable? lenses, ViewRuleType type, Expr? lens)
    {
        if (lens is not null && !Lenses.IsCall(lenses, lens)) return;
        if (t.Schema is not { } s || !Schemas.NamesType(s, type)) return;
        int rules = Nodes.Child(t, Nodes.Root, Kinds.ViewRules);
        if (rules == 0 && lens is null) return;
        if (rules == 0) rules = Nodes.New(t, Nodes.Root, Kinds.ViewRules);
        for (int r = Nodes.Child(t, rules, Kinds.ViewRule); r != 0; r = Nodes.NextOfKind(t, r))
        {
            var rule = ViewRuleId.At(t, r);
            var row = rule.RowFromId(t);
            if (row.Type != type) continue;
            if (lens is null) Nodes.Release(t, rule);
            else rule.Modify(t, row with { Lens = lens });
            return;
        }
        if (lens is null) return;
        ViewRuleId.New(t, rules).Modify(t, new(type, lens));
    }

    /// <summary>
    /// Sets <paramref name="node"/>'s own view rule: a view_rule child of it, its type none, made, changed or
    /// released (<paramref name="lens"/> none); seen through before its type's. Nothing for a node gone, or a
    /// lens that is not a call of a lens <paramref name="lenses"/> has.
    /// </summary>
    public static void SetViewRuleForNode(ModelStore t, LensTable? lenses, NodeId node, Expr? lens)
    {
        if (lens is not null && !Lenses.IsCall(lenses, lens)) return;
        int slot = Nodes.SlotOf(t, node);
        if (slot == 0) return;
        var own = ViewRuleFromNode(t, node);
        if (lens is null) { Nodes.Release(t, own); return; }   // nothing for none
        (own.IsNone ? ViewRuleId.New(t, slot) : own).Modify(t, new(ViewRuleType.None, lens));
    }

    /// <summary>The node's own view rule: its view_rule child; none (default) for none, or a node gone.</summary>
    public static ViewRuleId ViewRuleFromNode(ModelStore t, NodeId node) =>
        Nodes.SlotOf(t, node) is var slot and not 0 ? ViewRuleId.At(t, Nodes.Child(t, slot, Kinds.ViewRule)) : default;

    /// <summary>
    /// Makes <paramref name="ctx"/>'s view rules again from <paramref name="t"/>: the rules the tree
    /// holds, and only those (the schema's are in the tree it started with), and each node's own (its
    /// view_rule child), by its kind's type and its id. Rebuilt from the tree each frame rather than
    /// updated by events, so a rule set, undone or replayed from the log is in effect the frame after,
    /// with nothing to keep in step. Called once a frame, after Model.Run. Allocates nothing once warm: the
    /// dictionaries are cleared, not made, and each lens is the expression the tree holds.
    /// </summary>
    public static void FillViewRulesFromModel(EvalContext ctx, ModelTypes types, ModelStore t)
    {
        ctx.ViewRules.Clear();
        ctx.NodeViewRules.Clear();
        foreach (var (id, row, _) in ViewRules.Table(t))
        {
            if (row.Type != ViewRuleType.None) { if (types.Named.TryGetValue(row.Type.Name!, out var type)) ctx.ViewRules[type] = row.Lens!; }   // a type's (one the schema names)
            else if (Nodes.At(t, Nodes.SlotOf(t, id)).Parent is var owner and not 0)                                            // a node's own: its parent's, by its kind's type
                ctx.NodeViewRules[(types.Kinds[Nodes.At(t, owner).Kind]!, Nodes.IdOf(t, owner).Value)] = row.Lens!;
        }
    }

    /// <summary>The model's function owner(x): the node a field's address is on.</summary>
    public const string OwnerName = "owner";

    /// <summary>
    /// Names the model: <c>root</c>, the root node of the tree <paramref name="tree"/> gives when
    /// evaluated; <c>owner(x)</c>, the node the field <c>x</c> is on (<c>owner(it).source</c> in a lens's
    /// argument: the field's node's source), none for anything not a field; and its address literals,
    /// <c>$15</c>, each the node it names in that tree.
    /// </summary>
    public static void AddSymbols(EvalContext ctx, ModelTypes types, Func<ModelStore> tree)
    {
        // The model's Place: a node, by its id. Its child kinds are stepped by hooks (Member, Part); its
        // fields by its row's shape, read here: the node's newest row in its kind's table, along the path.
        // Entered by root and its address literals, so it has no key packer.
        var place = Places.Add(ctx, "model", "The model's nodes, each by its id: entered by root and $15, stepped by its kinds.",
            EvalTypes.Node, (in NodeId id, ReadOnlySpan<Step> path) =>
            {
                var t = tree();
                int slot = Nodes.SlotOf(t, id);
                if (slot == 0 || Schemas.Of(types.Schema, Nodes.At(t, slot).Kind) is not { } kind) return Value.None;
                return kind.ReadRow is { } read ? Rows(types.Schema, t, read, id, path) : Value.None;
            },
            // its generation: the model's, which every command that changes the tree or a row moves on
            gen: () => tree().Clock.Gen);
        ctx.Model = new ModelRoot { Types = types, Tree = tree, Place = place };
        Eval.Select(ctx);
        ctx.Add(Nodes.RootKey, () => Node(types, tree(), Nodes.Root));
        ctx.Add(OwnerName, EvalTypes.Function(OwnerName, "The node a field is on.",
            (in Value _, ReadOnlySpan<Value> args, Expr.Call _) =>
                args.Length == 1 && EvalTypes.Bare(args[0].Type) is { } field && types.Fields.ContainsKey(field)
                    ? Node(types, tree(), Nodes.SlotOf(tree(), Places.KeyOf(ctx, place, args[0].Address))) : Value.None));
        ctx.AddressLiterals[EvalTypes.Node] = at => tree() is var t && Node(types, t, Nodes.SlotOf(t, new NodeId(at.Id))) is { IsNone: false } node
            ? node : Value.None with { Status = Status.NodeGone };
    }

    /// <summary>The node with id <paramref name="id"/> in <paramref name="t"/>, typed by its kind; None for one gone.</summary>
    public static Value Node(ModelTypes types, ModelStore t, NodeId id) => Node(types, t, Nodes.SlotOf(t, id));

    /// <summary>The node in <paramref name="slot"/> of <paramref name="t"/>, typed by its kind; None for 0 or a kind the schema has not.</summary>
    public static Value Node(ModelTypes types, ModelStore t, int slot)
    {
        if (slot == 0) return Value.None;
        var n = Nodes.At(t, slot);
        if (n.Kind >= types.Kinds.Length || types.Kinds[n.Kind] is not { } type) return Value.None;
        int address = Eval.Current is { Model: { } model } ctx ? Places.Start(ctx, model.Place, n.Id) : 0;
        return new Value(type, slot, n.Id.Value, 0, null) { Address = address };
    }

    /// <summary>The address literal <c>$15</c>: evaluated, the node with that id in the tree being evaluated.</summary>
    public static Expr Address(ModelTypes types, NodeId id) => new Expr.Lit(Value.Of(id));

    /// <summary>
    /// Whether <paramref name="v"/> is a model node, as it is (an address is not: see IdOf): its
    /// type is its node's kind's, and its slot still holds it. Nothing else is: a set of a kind and every
    /// node of a kind hold a node too, but are not typed as its kind.
    /// </summary>
    public static bool IsNode(in Value v) =>
        Eval.Current?.Model is { } model && model.Tree() is var t && EvalTypes.Bare(v.Type) is { } type
        && Nodes.At(t, (int)v.At) is var n && n.Id.Value == v.Id && n.Kind < model.Types.Kinds.Length && model.Types.Kinds[n.Kind] == type;

    /// <summary>The id of a node found by an evaluation: what its row and its view state are kept by; none for anything else.</summary>
    public static NodeId IdOf(in Value v) => Eval.Load(v) is var n && IsNode(n) ? new(n.Id) : default;

    /// <summary>
    /// The node and key of a field found by an evaluation, to pass to a command that writes there: the node
    /// its address starts at, its key its type's; (default, null)
    /// for anything else.
    /// </summary>
    public static (NodeId Node, string? Key) AddressOf(ModelTypes types, in Value v) =>
        EvalTypes.Bare(v.Type) is { } t && types.Fields.TryGetValue(t, out var key) && Eval.Current is { Model: { } model } ctx
            ? (Places.KeyOf(ctx, model.Place, v.Address), key) : (default, null);

    // The node a value names in its tree, or 0 when it is gone (its slot released, maybe reused).
    static int Slot(in Value of) => TreeNow() is { } t && Nodes.At(t, (int)of.At).Id.Value == of.Id ? (int)of.At : 0;

    /// <summary>The tree and slot of the node <paramref name="v"/> is, in the tree its context reads: for code that works on the tree (a node's row, its context for a command); (an empty tree, 0) for anything else.</summary>
    public static (ModelStore Tree, int Slot) NodeOf(in Value v)
    {
        var n = Eval.Load(v);
        return IsNode(n) && TreeNow() is { } t ? (t, (int)n.At) : (NoTree, 0);
    }

    static readonly ModelStore NoTree = Nodes.NewTree(0);   // what NodeOf gives for no node: empty, never written

    // The tree the selected context reads (its Model's: the published generation); none outside a context that reads a model.
    static ModelStore? TreeNow() => Eval.Current?.Model?.Tree();

    // A leaf of node's row read by its kind's reader, t's tables made first when it has none yet (a test's plain tree).
    static Value Rows(Schema schema, ModelStore t, RowRead read, NodeId node, ReadOnlySpan<Step> path)
    {
        Schemas.TablesOf(schema, t);
        return read(t, node, path);
    }

    /// <summary>
    /// The field <paramref name="key"/> of <paramref name="node"/>'s row, a node of <paramref name="kind"/>: read by path, the member of its shape the key names; a list, how
    /// many it holds. None for a key the kind has not. For code that reads a row by key: the port, a view's lens field.
    /// </summary>
    public static Value FieldOf(Schema schema, ModelStore t, KindInfo kind, NodeId node, string key)
    {
        var members = kind.Shape?.Members ?? [];
        for (int m = 0; m < members.Length; m++)
            if (members[m].Name == key && kind.ReadRow is { } read)
            {
                ReadOnlySpan<Step> path = members[m].Type.PartType is null
                    ? [new Step(StepKind.Member, m)]
                    : [new Step(StepKind.Member, m), new Step(StepKind.Count, 0)];
                return Rows(schema, t, read, node, path);
            }
        return Value.None;
    }

    // The address one member from of's: a row member's at i (0 up); a child kind's below 0.
    static int MemberAddress(in Value of, long i) =>
        Eval.Current is { } ctx && of.Address != 0 ? Addresses.Child(ctx.Addresses, of.Address, new Step(StepKind.Member, i)) : 0;

    // The step from a node to its j-th child kind (ChildStep - j): no member of its row's shape.
    const long ChildStep = -2;

    /// <summary>
    /// A field's choices when they are names (an enum field's): a list of texts, read from the array it is
    /// given, never copied, each shown by its label (the field type's ChoiceLabels). cmdgen's, per enum field.
    /// NOTE(k): held as the array, the last such value with a Holder; a fixed set of names, it is the next
    /// to become a Place of its own.
    /// </summary>
    public static Value Names(string name, string description, string[] names) => new(new EvalType
    {
        Name = name, Description = description, Kind = EvalKind.Parts, PartType = EvalTypes.Text,
        Count = (in Value of) => of.Holder is string[] all ? all.Length : 0,
        Index = (in Value of, in Value at) =>
            of.Holder is string[] all && at is { Kind: EvalKind.Int, At: >= 0 } && at.At < all.Length ? Value.Of(all[at.At]) : Value.None,
    }, 0, 0, 0, names);

    /// <summary>
    /// A handle field's choices: every node of <paramref name="kind"/> in the tree the context reads (its
    /// Model's), in tree order, counted and indexed by walking it. Made once per kind, with no tree; cmdgen's,
    /// per handle field marked [Kind].
    /// </summary>
    public static Value Every(ushort kind)
    {
        lock (EveryOf)
        {
            if (!EveryOf.TryGetValue(kind, out var type))
                EveryOf[kind] = type = new EvalType
                {
                    Name = "every", Description = "Every node of a kind in the model.", Kind = EvalKind.Parts,
                    Count = (in Value _) => Eval.Current?.Model is { } m ? Walk(m.Types, m.Tree(), kind, 0, []) : 0,
                    Index = (in Value _, in Value at) =>
                    {
                        Span<Value> one = [Value.None];
                        return Eval.Current?.Model is { } m && at is { Kind: EvalKind.Int, At: >= 0 } && Walk(m.Types, m.Tree(), kind, at.At, one) == 1 ? one[0] : Value.None;
                    },
                };
            return new Value(type, 0, 0, 0, null);
        }
    }

    static readonly Dictionary<ushort, EvalType> EveryOf = new();   // filled by generated types' static constructors: locked

    /// <summary>The kind a handle field's choices are every node of (its type's Choices, made by <see cref="Every"/>): what it may name; <see cref="Kinds.None"/> for any other choices.</summary>
    public static ushort KindOf(in Value choices)
    {
        lock (EveryOf)
            foreach (var (kind, type) in EveryOf)
                if (type == choices.Type) return kind;
        return Kinds.None;
    }

    /// <summary>A handle field's read (its type's Read hook, cmdgen's): the node its id names, read through the model's Place; none once that is gone, or its own node is.</summary>
    public static Value ReadNode(in Value at) =>
        Eval.Current is { Model: { } model } ctx && Addresses.Read(ctx, at.Address) is { Type: var type } id && type == EvalTypes.Node
            && model.Tree() is var t ? Node(model.Types, t, Nodes.SlotOf(t, new NodeId(id.Id))) : Value.None;

    /// <summary>
    /// The context node.set evaluates its value in (<see cref="Set"/>): the model's symbols over
    /// <paramref name="t"/> as it is (root, owner, $15) and the language's own functions; no other Place,
    /// no view rules, no lenses. So a write depends on the tree alone: what the log's command did is
    /// what it does again on the same tree. Made once per model (Model.Run). Leaves the thread's context as it was.
    /// </summary>
    public static EvalContext WriteContext(ModelStore t, Schema schema)
    {
        var outer = Eval.Current;
        var ctx = new EvalContext();
        Eval.AddBuiltinFunctionSymbols(ctx);
        AddSymbols(ctx, MakeEvalTypes(schema), () => t);
        Eval.Current = outer;
        return ctx;
    }

    /// <summary>
    /// What node.set does: the field <paramref name="key"/> of <paramref name="node"/> set to
    /// <paramref name="value"/>, if it is an editable field of its kind.
    /// An expression field (a lens's argument) keeps the expression as it is; any other gets its value, evaluated in <paramref name="ctx"/> (<see cref="WriteContext"/>) with
    /// `it` the node: 2, "text", $7, $7.title, count(root.case) > 2. False, and nothing written, when the node is
    /// gone, the key is not such an editable field, or the value is none or not one its field may hold
    /// (its table says: its kind, a node of the kind it names, a name of its enum).
    /// </summary>
    public static bool Set(ModelStore t, EvalContext ctx, NodeId node, string key, Expr? value)
    {
        if (ctx.Model is not { } model) return false;
        var schema = model.Types.Schema;
        int slot = Nodes.SlotOf(t, node);
        if (slot == 0 || Schemas.Of(schema, Nodes.At(t, slot).Kind) is not { } kind) return false;
        var field = FieldType(kind, key);
        if (field is not { Editable: true }) return false;
        Value v;
        if (field.Kind == EvalKind.Opaque) v = Value.Of(value);   // an expression field (cmdgen's only opaque leaf): the expression itself
        else
        {
            if (value is null) return false;
            var (outer, outerIt) = (Eval.Current, ctx.DynamicVar);
            Eval.Select(ctx);
            Addresses.Begin(ctx.Addresses, t.Clock.Gen);
            ctx.DynamicVar = Node(model.Types, t, slot);
            var got = Eval.Load(Eval.Run(value, ctx));
            v = IsNode(got) ? Value.Of(new NodeId(got.Id)) : got;   // a node, as a handle field holds it: its id
            (Eval.Current, ctx.DynamicVar) = (outer, outerIt);
            if (v.IsNone) return false;
        }
        Schemas.TablesOf(schema, t);
        return kind.WriteRow?.Invoke(t, node, key, v) ?? false;
    }

    // A kind's field's type, by key: a leaf of its row's shape; null for none.
    static EvalType? FieldType(KindInfo kind, string key)
    {
        foreach (var (name, type) in kind.Shape?.Members ?? [])
            if (name == key && type.Members is null && type.PartType is null) return type;
        return null;
    }

    // The nodes of a kind in tree order, from the from-th, into into: how many it wrote, or, into
    // empty, how many there are past from.
    static int Walk(ModelTypes types, ModelStore t, ushort kind, long from, Span<Value> into)
    {
        int seen = 0, wrote = 0;
        int at = Nodes.At(t, Nodes.Root).First;
        while (at != 0)
        {
            var n = Nodes.At(t, at);
            if (n.Kind == kind && seen++ >= from)
            {
                if (into.Length == 0) wrote++;
                else { into[wrote++] = Node(types, t, at); if (wrote == into.Length) return wrote; }
            }
            if (n.First != 0) { at = n.First; continue; }
            while (at != 0 && Nodes.At(t, at).Next == 0) at = Nodes.At(t, at).Parent;
            if (at != 0) at = Nodes.At(t, at).Next;
        }
        return wrote;
    }

    // A node's parts: its fields, then its child kinds.
    static int PartCount(KindInfo k) => (k.Shape?.Members?.Length ?? 0) + k.Children.Length;

    // A node's i-th part, as PartCount counts them.
    static Value Part(ModelTypes types, KindInfo k, in Value of, long i)
    {
        int fields = k.Shape?.Members?.Length ?? 0;
        if (i < fields) return Eval.MemberStep(of, k.Shape!.Members![i].Name);
        return ChildKindOf(types, k, (int)(i - fields), of);
    }

    // What a node's i-th part is called on screen: a field's (its shape's PartName: its [Label], else its key), a child kind's name.
    static string PartName(Schema schema, KindInfo k, long i)
    {
        int fields = k.Shape?.Members?.Length ?? 0;
        if (i < fields) return k.Shape!.PartName?.Invoke(i) ?? k.Shape.Members![i].Name;
        return Schemas.Of(schema, k.Children[i - fields].Kind)?.Name ?? "";
    }

    // of.name on a node: a field, by its shape; a child kind, by its name.
    static Value Member(ModelTypes types, KindInfo k, in Value of, string name)
    {
        if (Slot(of) == 0) return Value.None with { Status = Status.NodeGone };
        foreach (var (field, _) in k.Shape?.Members ?? [])
            if (field == name) return Eval.MemberStep(of, name);
        for (int j = 0; j < k.Children.Length; j++)
            if (Schemas.Of(types.Schema, k.Children[j].Kind)?.Name == name) return ChildKindOf(types, k, j, of);
        return Value.None with { Status = Status.NoMember };   // a key its kind does not have; one it has and nothing wrote is Produced
    }

    // A node's j-th child kind: many, the set of those children; one, the child node.
    static Value ChildKindOf(ModelTypes types, KindInfo k, int j, in Value of)
    {
        if (TreeNow() is not { } t) return Value.None;
        var child = k.Children[j];
        if (child.Many) return types.Sets[child.Kind] is { } set ? new Value(set, of.At, of.Id, 0, null) { Address = MemberAddress(of, ChildStep - j) } : Value.None;
        return Node(types, t, Nodes.Child(t, (int)of.At, child.Kind));
    }

    static long Count(in Value of, ushort kind)
    {
        int node = Slot(of);
        if (node == 0) return 0;
        var t = TreeNow()!;   // Slot found the node in it
        long n = 0;
        for (int c = Nodes.Child(t, node, kind); c != 0; c = Nodes.NextOfKind(t, c)) n++;
        return n;
    }

    // A node's at-th child of a kind, by walking its children of that kind from the first: reading
    // every child so is quadratic in how many there are (a set of model nodes is a list, not an array).
    // NOTE(k): when a set grows large, gather its children into an array once per generation (keyed on
    // the node and the model's generation) and index that.
    static Value Child(ModelTypes types, in Value of, ushort kind, long at)
    {
        int node = Slot(of);
        if (node == 0) return Value.None;
        var t = TreeNow()!;   // Slot found the node in it
        long i = 0;
        for (int c = Nodes.Child(t, node, kind); c != 0; c = Nodes.NextOfKind(t, c), i++)
            if (i == at) return Node(types, t, c);
        return Value.None;
    }
}
