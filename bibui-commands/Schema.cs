namespace Bibui.Commands;

// The model's nouns, beside the commands' verbs (OpInfo): each kind of node, by its index (what a tree
// node's Kind holds, and its table's place in ModelStore.Tables), what it is called, its row's shape (its
// fields, each its own EvalType), the kinds that sit under it, its row commands, and how its table is made.
// What code that holds only an id reads a node through: the evaluator, node.set, the port, an agent
// (model.schema). Code that knows the kind (a command) uses its typed table and never needs it.
//
// The typing of a node's values is its row's: a record with real fields, checked by the compiler. So a
// kind does not type values; it says where a node's row is kept and what may sit under it. A kind is an
// index rather than a name, because the tree is built by code, not parsed from text; the name is kept for
// the edges (the port, expressions). cmdgen writes an app's kinds ([NodeSet]); the library adds its own
// (the root's place, the view rules).

/// <summary>
/// A node set: a static class (or a record) whose nested records are the model's kinds of node, as
/// a command set's are its commands. Each record is a kind (<c>Panel</c> is <c>panel</c>); its
/// parameters are the keys it holds (<c>long Submits</c> is <c>submits</c>, an Int), a parameter
/// whose type is another kind of the set is a child node of that kind, and [Description] says what
/// each is. A kind with fields is a row kind: its record is its row, kept in its table by the node's id.
/// cmdgen writes from them the set's schema, kind names and tables (<c>&lt;Set&gt;Kinds</c>,
/// <c>&lt;Set&gt;Tables</c>), so a key is spelt in one place, the declaration.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NodeSetAttribute : Attribute;

/// <summary>A child kind that may repeat under its parent: a list (<c>[Many] Panel Panel</c>).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ManyAttribute : Attribute;

/// <summary>
/// Where a kind sits: under <paramref name="parent"/>, one, or many (<see cref="Many"/>), as a parameter
/// <c>[Many] Case Case</c> of the parent would say, said on the kind instead, so a kind whose record is its
/// data (a <c>record struct</c>: its fields are a table's row) holds only its fields. cmdgen adds it to the
/// parent's children.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class InAttribute(Type parent) : Attribute
{
    public Type Parent { get; } = parent;
    public bool Many { get; init; }
}

/// <summary>
/// A kind's row: what a node of the kind shows and offers wherever it is shown, in order. A key (<c>nameof(Note)</c>) is a field, its value; a command
/// (<c>typeof(Cmd.Panel_Close)</c>) is one the row can take, made from a context holding the node,
/// its ancestors and its children, and there while that context fills it. Neither says what it
/// looks like: that is the app's, which builds its UI from the row; an agent reads the same row.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class RowAttribute(params object[] entries) : Attribute
{
    public object[] Entries { get; } = entries;
}

/// <summary>
/// A field a person or an agent may change directly, by its address, not through a command of its
/// own: set by a generic node.set (<c>ModelEval.Set</c>, checked against its type), or, a text
/// buffer, typed into in place. Only such a field is offered for editing.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class EditableAttribute : Attribute;

/// <summary>One entry of a kind's row: a field, by its key (Op null), or a command, by its entry in its set's table (Name its wire name).</summary>
public readonly record struct RowEntry(string Name, OpInfo? Op);

/// <summary>
/// One entry of a node's row as it is now (<see cref="Schemas.RowOf"/>): a field, by its key and its type
/// (whether it is editable), or a command its context can make. What the app maps to what it shows, and
/// the outline writes; no widget.
/// </summary>
public readonly record struct RowItem(string? Field, EvalType? Type, OpInfo? Command);

/// <summary>
/// The kind indices the library gives: none (a free slot), the root, the view rules. An app's kinds
/// follow, from <see cref="FirstApp"/> (cmdgen numbers them; an app's <c>Root</c> record is the root).
/// </summary>
public static class Kinds
{
    public const ushort None = 0;
    public const ushort Root = 1;
    public const ushort ViewRules = 2;
    public const ushort ViewRule = 3;
    public const ushort FirstApp = 4;
}

/// <summary>A kind that sits under another: its index, and whether many may (a list) or one.</summary>
public readonly record struct ChildKind(ushort Kind, bool Many);

/// <summary>
/// One kind of node, the same for every tree: its index, its name, its row's shape (null: no fields), the
/// kinds under it, its row commands (<see cref="RowAttribute"/>), and how its table is made. Its rows in a
/// tree are <c>t.Tables[Index]</c>.
/// </summary>
public sealed class KindInfo
{
    public required ushort Index;                 // its tree nodes' Kind, and its table's place in ModelStore.Tables
    public required string Name;                  // case: the port's, an expression's, a file's name for it
    public string Label = "";                     // Case: a person's
    public string Description = "";
    public EvalType? Shape;                       // its row's shape: its fields, each its own type (cmdgen's {Record}Eval.Type); null: none
    public ChildKind[] Children = [];             // the kinds that sit under it
    public RowEntry[]? Row;                       // what a node of it shows and offers
    public string? Icon;
    public Func<ModelClock, object>? NewTable;    // a row kind's: its table (a Table<T, TId>), on a model's clock

    // A row kind's rows, for code that holds only a node's id (cmdgen's, per kind; the library's for its view
    // rules). The per-kind code lives in this table, written once per kind, so generic code (the evaluator,
    // node.set, the port) indexes it by kind and never switches over the app's kinds.
    public RowRead? ReadRow;                      // the model Place's reader: a leaf of the node's row
    public RowWrite? WriteRow;                    // node.set: a field by key; false for a key or a value it refuses
    public Action<ModelStore, NodeId>? AddRow;    // a node of the kind made: its row, the declared defaults (Nodes.New)
    public Action<ModelStore, NodeId>? RemoveRow; // a node released: its row (Nodes.Release)
    public RowDiffSince? DiffRow;                 // the change output: its fields that differ between a generation and now
}

/// <summary>A leaf of node's row at path.</summary>
public delegate Value RowRead(ModelStore t, NodeId node, ReadOnlySpan<Step> path);

/// <summary>Node's row with its field key set to value; false for a key or a value the field refuses.</summary>
public delegate bool RowWrite(ModelStore t, NodeId node, string key, Value value);

/// <summary>Node's row's fields that differ between generation since (as it saw it) and now, into.</summary>
public delegate void RowDiffSince(ModelStore t, NodeId node, long since, List<(string Field, Value Before, Value After)> into);

/// <summary>
/// A table whose rows are the values of an enum named <paramref name="enumName"/>: cmdgen writes the enum
/// from the table's first column (a name per row: "card" is Body.Card), in its order, and <c>BodyNames</c>
/// (each value's name, and its label: the table's column named Label when it has one, else the name). The
/// table is indexed by the enum (Bodies[(int)body]), so it has a row for every value by construction and
/// the two cannot drift apart. A
/// field of the enum is kept as its value, may hold only its names (its type's <see cref="EvalType.Choices"/>),
/// and is picked from them, each shown by its label.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class EnumTableAttribute(string enumName) : Attribute
{
    public string EnumName { get; } = enumName;
}

/// <summary>
/// A field picked from a set: its view rule is <c>pick(it, choices)</c>, <paramref name="choices"/>
/// the name of a symbol holding the set (<c>[Pick("board_bodies")]</c>), so it is drawn as a dropdown
/// of them. The attribute is the field's view rule written at its declaration, so every reader of the
/// field sees it through the pick, not only the drawer.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PickAttribute(string choices) : Attribute
{
    public string Choices { get; } = choices;
}

/// <summary>
/// A number field between <paramref name="min"/> and <paramref name="max"/>, moving by
/// <see cref="Step"/> (0: continuously): its view rule is <c>range(it, min, max, step)</c>, so it is
/// drawn as a slider.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RangeAttribute(double min, double max) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Step { get; init; }
}

/// <summary>
/// The view rule a model starts with for a kind's nodes, or a field's values; one the app sets later replaces it:
/// an expression, as written (<c>[ViewRule("rows(it, it.title, it.picked)")]</c>, <c>it</c> the value); or
/// a lens called over the node (<c>[ViewRule(typeof(BoardLenses.Board))]</c>: board(it)), or over a member
/// of it, a dotted path (<c>[ViewRule(typeof(BoardLenses.Board), "case")]</c>: board(it.case)). cmdgen
/// writes it into the tree the model starts with (NodeKinds.NewTree), as a view_rule node, where a command
/// may change it like any rule.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Parameter)]
public sealed class ViewRuleAttribute : Attribute
{
    public ViewRuleAttribute(Type lens, string? of = null) => (Lens, Of) = (lens, of);
    public ViewRuleAttribute(string rule) => Rule = rule;
    public Type? Lens { get; }
    public string? Of { get; }
    public string? Rule { get; }
}

/// <summary>An app's kinds, by index (a node's Kind), and by name (the port's, an expression's). Made by <see cref="Schemas.Make"/>.</summary>
public sealed class Schema
{
    public KindInfo?[] Kinds = [];                               // by index; null for an index no kind has (none)
    public readonly Dictionary<string, ushort> ByName = new();
}

public static class Schemas
{
    /// <summary>The built-in kinds' names and keys: the view rules under the root, each a type's name and its lens (<see cref="ModelEval.SetViewRuleForType"/>).</summary>
    public const string ViewRulesKey = "view_rules", ViewRuleKey = "view_rule", TypeKey = "type", LensKey = "lens";

    /// <summary>
    /// A schema of an app's kinds and the library's: the view rules, a list under the root of the rules the
    /// model holds for a type. They are the model's nodes, so they are logged and read like any other
    /// (an app declares no kind for them; it sets them with <c>Model.SetViewRuleForType</c>). An app without a
    /// root kind gets a bare one. Two kinds at one
    /// index, or one name twice, is a mistake: it throws.
    /// </summary>
    public static Schema Make(params KindInfo[] kinds)
    {
        var s = new Schema();
        var root = Array.Find(kinds, k => k.Index == Kinds.Root) ?? new KindInfo { Index = Kinds.Root, Name = Nodes.RootKey, Label = "Root" };
        if (!Array.Exists(root.Children, c => c.Kind == Kinds.ViewRules)) root.Children = [.. root.Children, new(Kinds.ViewRules, false)];
        Add(s, root);
        Add(s, new KindInfo
        {
            Index = Kinds.ViewRules, Name = ViewRulesKey, Label = "View rules",
            Description = "The rules for how values of a type are seen: those the tree started with (the schema's), and any set since.",
            Children = [new(Kinds.ViewRule, true)],
        });
        Add(s, ViewRules.Info);
        foreach (var k in kinds) if (k != root) Add(s, k);
        return s;
    }

    static void Add(Schema s, KindInfo kind)
    {
        if (kind.Index >= s.Kinds.Length) Array.Resize(ref s.Kinds, kind.Index + 1);
        if (s.Kinds[kind.Index] is not null) throw new InvalidOperationException($"Two kinds at index {kind.Index}: {s.Kinds[kind.Index]!.Name} and {kind.Name}.");
        s.Kinds[kind.Index] = kind;
        s.ByName.Add(kind.Name, kind.Index);
    }

    /// <summary>The kind at <paramref name="kind"/>, or null when the schema has none there.</summary>
    public static KindInfo? Of(Schema s, ushort kind) => kind < s.Kinds.Length ? s.Kinds[kind] : null;

    /// <summary>
    /// The row of a node of <paramref name="kind"/>, added to <paramref name="into"/>: its kind's [Row], each
    /// field as declared, each command <paramref name="offered"/> (the model's Offers: the context where it is
    /// drawn holds what it reads; null: every one). A row's commands take no arguments (<see cref="OpInfo.Cmd"/>):
    /// what they need is the context's. Allocates nothing.
    /// </summary>
    public static void RowOf(KindInfo kind, Func<OpInfo, bool>? offered, List<RowItem> into)
    {
        foreach (var entry in kind.Row ?? [])
        {
            if (entry.Op is { } op)
            {
                if (op.Cmd is not null && (offered is null || offered(op))) into.Add(new(null, null, op));
                continue;
            }
            foreach (var (key, type) in kind.Shape?.Members ?? [])
                if (key == entry.Name) into.Add(new(key, type, null));
        }
    }

    /// <summary>The kind called <paramref name="name"/>, or null.</summary>
    public static KindInfo? Named(Schema s, string name) => s.ByName.TryGetValue(name, out var at) ? s.Kinds[at] : null;

    /// <summary>
    /// Whether <paramref name="type"/> names a type of <paramref name="s"/>'s, as a view rule names one: a kind
    /// (case), a list of a kind that sits under another as many (case[]), or a kind's field, plain, not a list
    /// or a record (case.title). What ModelEval.MakeEvalTypes names, said from the schema alone.
    /// </summary>
    public static bool NamesType(Schema s, ViewRuleType rule)
    {
        if (rule == ViewRuleType.None) return false;
        var type = rule.Name!;
        if (s.ByName.ContainsKey(type)) return true;
        if (type.EndsWith("[]") && s.ByName.TryGetValue(type[..^2], out var of))
        {
            foreach (var k in s.Kinds)
                foreach (var child in k?.Children ?? [])
                    if (child.Many && child.Kind == of) return true;
            return false;
        }
        foreach (var k in s.Kinds)
            foreach (var (_, field) in k?.Shape?.Members ?? [])
                if (field.Members is null && field.PartType is null && field.Name == type) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="t"/>'s tables, one per row kind, at its index: made the first time a tree is asked for
    /// them (a tree made without them, a test's), and shared by its generations from then on.
    /// </summary>
    public static object?[] TablesOf(Schema s, ModelStore t)
    {
        if (t.Schema == s) return t.Tables;
        var all = new object?[Math.Max(s.Kinds.Length, t.Tables.Length)];
        t.Tables.CopyTo(all, 0);
        foreach (var k in s.Kinds)
            if (k?.NewTable is { } make && all[k.Index] is null) all[k.Index] = make(t.Clock);
        (t.Tables, t.Schema) = (all, s);
        // The nodes made before (a test's plain tree): each its kind's row, as making it would have added.
        for (int slot = Nodes.Root; slot != 0; slot = Nodes.NextInTree(t, slot))
            Of(s, Nodes.At(t, slot).Kind)?.AddRow?.Invoke(t, Nodes.IdOf(t, slot));
        return all;
    }
}

/// <summary>A view rule's row: the type's name (a kind, case; a list of one, case[]; a field, frame.zoom) and the lens its values are seen through (none: no rule).</summary>
public readonly record struct ViewRuleRow(ViewRuleType Type, Expr? Lens);

/// <summary>
/// The type a view rule is for, by its name: a kind (case), a list of one (case[]), a field (case.picked).
/// <see cref="None"/> (default): a node's own rule, for no type. An app's are cmdgen's ({Set}Types.CasePicked),
/// so code names a type no other way; a client's are read from its name, and checked against the schema.
/// In JSON, its name (none: null).
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ViewRuleTypeJson))]
public readonly record struct ViewRuleType(string? Name)
{
    public static readonly ViewRuleType None = default;
    public override string ToString() => Name ?? "";
}

/// <summary>A <see cref="ViewRuleType"/> in JSON: its name, a string; none, null.</summary>
public sealed class ViewRuleTypeJson : System.Text.Json.Serialization.JsonConverter<ViewRuleType>
{
    public override bool HandleNull => true;
    public override ViewRuleType Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String && reader.GetString() is { Length: > 0 } name ? new(name) : ViewRuleType.None;
    public override void Write(System.Text.Json.Utf8JsonWriter writer, ViewRuleType value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value == ViewRuleType.None) writer.WriteNullValue();
        else writer.WriteStringValue(value.Name);
    }
}

/// <summary>
/// A view_rule node's id (the library's kind; as cmdgen writes {Kind}Id for an app's): made only by a check
/// (From, At, New), so one is a view rule's for good (an id is given once, its node's kind never changes).
/// default is none.
/// </summary>
public readonly record struct ViewRuleId
{
    public readonly NodeId Id;
    ViewRuleId(NodeId id) => Id = id;

    /// <summary>None: no view rule (default).</summary>
    public bool IsNone => Id.IsNone;

    /// <summary>The id, when <paramref name="t"/> holds a view rule by it; none for another kind's, or one it does not hold.</summary>
    public static ViewRuleId From(ModelStore t, NodeId id) => Nodes.HasKind(t, id, Kinds.ViewRule) ? new(id) : default;

    /// <summary>The id of the node in <paramref name="slot"/>, when it is a view rule; none for any other.</summary>
    public static ViewRuleId At(ModelStore t, int slot) => slot != 0 && Nodes.At(t, slot) is { Kind: Kinds.ViewRule } n ? new(n.Id) : default;

    /// <summary>A new view rule, the last child of <paramref name="parent"/>: its id.</summary>
    public static ViewRuleId New(ModelStore t, int parent) => new(Nodes.IdOf(t, Nodes.New(t, parent, Kinds.ViewRule)));

    /// <summary>Its row: its newest; the default for none, or once it is released.</summary>
    public ViewRuleRow RowFromId(ModelStore t) => ViewRules.Table(t).FromId(Id);

    /// <summary>Writes <paramref name="row"/> as its newest.</summary>
    public void Modify(ModelStore t, in ViewRuleRow row) => ViewRules.Table(t).SetRow(Id, row);

    public static implicit operator NodeId(ViewRuleId id) => id.Id;
    public override string ToString() => Id.ToString();
}

/// <summary>
/// The library's row kind: the view rules, in the tree's table at <see cref="Kinds.ViewRule"/> (every tree
/// has it: Nodes.NewTree). A type's rule is a <c>view_rule</c> node under the root's <c>view_rules</c>, its
/// row by that node's id (ModelEval.SetViewRuleForType). A node's own rule is a <c>view_rule</c>
/// child of that node (its type ""): the node is seen through it instead of its kind's rule, so one node
/// can be seen otherwise than the rest of its kind; view_rule.set with a node makes, changes or releases it
/// (ModelEval.SetViewRuleForNode), its type none. Both read each frame (FillViewRulesFromModel).
/// </summary>
public static class ViewRules
{
    /// <summary>The view rules' rows in <paramref name="t"/>'s model, by their view_rule nodes' ids.</summary>
    public static Table<ViewRuleRow, NodeId> Table(ModelStore t) => (Table<ViewRuleRow, NodeId>)t.Tables[Kinds.ViewRule]!;

    static readonly EvalType Shape = new()
    {
        Name = ViewRuleKey, Kind = EvalKind.Parts,
        Members = [(Schemas.TypeKey, new EvalType { Name = $"{ViewRuleKey}.{Schemas.TypeKey}", Kind = EvalKind.Text, Description = "The type's name: a kind (case), a list of one (case[]), a field (frame.zoom)." }),
                   (Schemas.LensKey, new EvalType { Name = $"{ViewRuleKey}.{Schemas.LensKey}", Kind = EvalKind.Opaque, Description = "The lens call over it: card(it).", Text = EvalTypes.Expression.Text })],
        Fields = [Schemas.TypeKey, Schemas.LensKey],
    };
    const string ViewRuleKey = Schemas.ViewRuleKey;

    /// <summary>The kind.</summary>
    public static readonly KindInfo Info = new()
    {
        Index = Kinds.ViewRule, Name = ViewRuleKey, Label = "View rule",
        Description = "How every value of a type is seen: the lens called over it (it).", Shape = Shape, NewTable = NewTable,
        ReadRow = ValueFromPath, WriteRow = SetValueForKey, RemoveRow = (t, node) => Table(t).RemoveRow(node), DiffRow = RowChanges,
    };

    static ViewRules() => Shape.PartName = i => Shape.Members![i].Name;

    /// <summary>A new, empty table of them, on the model's clock.</summary>
    public static Table<ViewRuleRow, NodeId> NewTable(ModelClock clock) => new() { Clock = clock, Default = new(ViewRuleType.None, null) };

    //- its row functions (Info): by path, set by key, what changed

    public static Value ValueFromPath(ModelStore t, NodeId node, ReadOnlySpan<Step> path) => path switch
    {
        [{ Kind: StepKind.Member, At: 0 }] => TypeValue(Table(t).FromId(node).Type),
        [{ Kind: StepKind.Member, At: 1 }] => Value.Of(Table(t).FromId(node).Lens),
        _ => Value.None,
    };

    public static bool SetValueForKey(ModelStore t, NodeId node, string key, Value v)
    {
        var r = Table(t).FromId(node);
        if (key == Schemas.TypeKey && v.Kind == EvalKind.Text) r = r with { Type = new(v.AsText) };
        else if (key == Schemas.LensKey && v.Type == EvalTypes.Expression) r = r with { Lens = v.Holder as Expr };
        else return false;
        Table(t).SetRow(node, r);
        return true;
    }

    public static void RowChanges(ModelStore t, NodeId node, long gen, List<(string Field, Value Before, Value After)> into)
    {
        Table(t).FromIdAt(node, gen, out var before);
        Diff(before, Table(t).FromId(node), into);
    }

    static void Diff(in ViewRuleRow a, in ViewRuleRow b, List<(string Field, Value Before, Value After)> into)
    {
        if (a.Type != b.Type) into.Add((Schemas.TypeKey, TypeValue(a.Type), TypeValue(b.Type)));
        if (!Equals(a.Lens, b.Lens)) into.Add((Schemas.LensKey, Value.Of(a.Lens), Value.Of(b.Lens)));
    }

    // A rule's type as a value: its name; none for a node's own rule.
    static Value TypeValue(ViewRuleType type) => type == ViewRuleType.None ? Value.None : Value.Of(type.Name!);
}
