// The model, read by a client: model.get answers a node and what is under it, at the current
// generation or one the log still holds; model.log answers the commands that made the generations;
// model.schema answers what each kind of node is, and which commands act on it; a command's answer
// says what it changed (Diff).
// Nothing here knows what the nodes mean: it walks keys, ids and handles, so any app's tree reads
// the same.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bibui.Commands;

public static class ModelJson
{
    static readonly List<RowItem> Row = [];   // one node's row, while it is written

    // How the outline's nodes are found on screen: the app's convention, said with every answer.
    const string Ui = "A field in [brackets] ([index] 2) is editable: a person changes it where it is shown, at <handle>/<key> (click it, then type), and a client may set it (node.set Node=<handle> Key=<key> Value=<expression>: 2, \"text\", $7.title). A node's row (row:) is what it shows on screen, left to right, each at <handle>/<key or command>: [key] an editable field, key a field shown, text → command a button running that command.";

    // A tree for writing a value that names no node of its own (a default): a handle in it resolves to nothing.
    static readonly ModelStore NoTree = Nodes.NewTree(0);

    /// <summary>
    /// Writes the node at <paramref name="path"/> and its children <paramref name="depth"/> levels
    /// down; returns why not, or null. A path is segments split by '/', each a kind's name (the first
    /// child of it), an id (the child with it) or a handle (<c>#7</c>, the node it names), and may end in
    /// <c>@gen</c>: <c>panel/#7@40</c>; it may start with <c>root</c>. Its last segment may name a field of
    /// the node's kind: answered as its value, from the node's row (its default before one is written).
    /// </summary>
    public static string? Get<TCtx>(Utf8JsonWriter json, Model<TCtx> model, Schema schema, string path, int depth) where TCtx : struct
    {
        long gen = model.Gen;
        ModelStore? t = model.Tree;
        int at = path.LastIndexOf('@');
        if (at >= 0)
        {
            // The tree is one, written in place: only the generation it holds now is read whole.
            if (!long.TryParse(path.AsSpan(at + 1), out var asked)) return $"'{path[(at + 1)..]}' is not a generation.";
            if (asked != gen) return $"Only the generation the model holds now, {gen}, is read whole: what changed since an earlier one is the change output.";
            path = path[..at];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int slot = Nodes.Root;
        for (int i = segments.Length > 0 && segments[0] == "root" ? 1 : 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var kind = Schemas.Of(schema, Nodes.At(t, slot).Kind);
            int next = NodeId.TryParse(segment, out var h) ? Nodes.SlotOf(t, h)
                : long.TryParse(segment, out var id) ? ChildWithId(t, slot, id)
                : Schemas.Named(schema, segment) is { } of ? Nodes.Child(t, slot, of.Index)
                : 0;
            if (next == 0)
            {
                // A field of its kind: its value, the last step of a path only.
                if (i == segments.Length - 1 && kind is not null && FieldOf(kind, segment) is { } field)
                {
                    json.WriteStartObject();
                    json.WriteNumber("gen", gen);
                    json.WriteString("ui", Ui);
                    json.WritePropertyName("node");
                    Field(json, t, segment, ModelEval.FieldOf(schema, t, kind, Nodes.IdOf(t, slot), segment), field.Editable);
                    json.WriteEndObject();
                    return null;
                }
                return $"No node '{segment}' under '{NameOf(schema, Nodes.At(t, slot).Kind)}' at generation {gen}"
                    + (kind is null ? "." : $": a {kind.Name} holds {string.Join(", ", Keys(schema, kind))}.");
            }
            slot = next;
        }

        json.WriteStartObject();
        json.WriteNumber("gen", gen);
        json.WriteString("ui", Ui);
        json.WritePropertyName("node");
        Node(json, t, schema, slot, depth);
        json.WriteEndObject();
        return null;
    }

    // A kind's field by key: a leaf of its row's shape; null for none.
    static EvalType? FieldOf(KindInfo kind, string key)
    {
        foreach (var (name, type) in kind.Shape?.Members ?? [])
            if (name == key) return type;
        return null;
    }

    // What a node of a kind holds, by name: its fields, then the kinds under it.
    static IEnumerable<string> Keys(Schema schema, KindInfo kind) =>
        (kind.Shape?.Members ?? []).Select(m => m.Name).Concat(kind.Children.Select(c => NameOf(schema, c.Kind)));

    // A kind's name, by its index: the schema's; its index as text without one.
    static string NameOf(Schema schema, ushort kind) => Schemas.Of(schema, kind) is { } k ? k.Name : $"kind {kind}";

    /// <summary>
    /// The generations after <paramref name="since"/> the log still holds, oldest first: each one's command,
    /// the context it ran in (the fields its entry in <paramref name="ops"/> declares, beside its arguments,
    /// as a client sends them), and what pushed it.
    /// </summary>
    public static void Log<TCtx>(Utf8JsonWriter json, Model<TCtx> model, JsonTypeInfo<Cmd> commands, IReadOnlyList<OpInfo> ops, long since) where TCtx : struct
    {
        json.WriteStartObject();
        json.WriteNumber("gen", model.Gen);
        json.WriteNumber("oldest", model.OldestGen);
        json.WriteStartArray("entries");
        for (long g = Math.Max(since + 1, model.OldestGen); g <= model.Gen; g++)
        {
            if (!model.TryEntry(g, out var entry)) continue;
            json.WriteStartObject();
            json.WriteNumber("gen", entry.Gen);
            if (entry.CauseGen != 0) json.WriteNumber("cause", entry.CauseGen);
            json.WritePropertyName("cmd");
            Cmd(json, entry.Cmd, entry.Ctx, commands, ops, model.Context);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// A command as a client sends it: its JSON (the app's or the library's: one JSON for both), and beside
    /// its arguments the fields of <paramref name="ctx"/> its entry in <paramref name="ops"/> declares.
    /// </summary>
    public static void Cmd<TCtx>(Utf8JsonWriter json, Cmd cmd, in TCtx ctx, JsonTypeInfo<Cmd> commands, IReadOnlyList<OpInfo> ops, ContextOps<TCtx>? context) where TCtx : struct
    {
        OpInfo? op = null;
        foreach (var o in ops) if (o.Type == cmd.GetType()) { op = o; break; }
        if (op is null || op.Context.Length == 0 || context is null)
        {
            JsonSerializer.Serialize(json, cmd, commands);
            return;
        }
        // Its arguments as its JSON writes them, then its context beside them, in the one object.
        using var written = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(cmd, commands));
        json.WriteStartObject();
        foreach (var property in written.RootElement.EnumerateObject()) property.WriteTo(json);
        context.Write(json, op, ctx);
        json.WriteEndObject();
    }

    /// <summary>
    /// Writes what changed from generation <paramref name="from"/> to the model's latest:
    /// <c>{"from":40,"gen":42,"diff":[...]}</c>, each entry an add, remove, change or move with
    /// the node's key and handle and its parent's; an added node with what it holds. At most
    /// <paramref name="most"/> entries, then how many more. An empty diff: nothing changed. Its
    /// fields, into the object the caller has open: what else an answer says is the caller's.
    /// </summary>
    public static void Diff<TCtx>(Utf8JsonWriter json, Model<TCtx> model, Schema schema, long from, int most = 200) where TCtx : struct
    {
        json.WriteNumber("from", from);
        json.WriteNumber("gen", model.Gen);
        // What the log no longer holds, the tree and tables no longer keep: from its oldest on only.
        if (from < model.OldestGen - 1)
        {
            json.WriteString("error", $"Generation {from} is no longer in the log: it holds {model.OldestGen} to {model.Gen}.");
            return;
        }
        var t = model.Tree;
        int written = 0, more = 0;
        bool Room()   // another entry fits; past most, counted instead
        {
            if (written < most) { written++; return true; }
            more++;
            return false;
        }

        //- the shape: each node added, removed or moved since `from`, by the edits each generation kept; a
        // node added and removed since is neither, and what was added or removed under one that was is
        // said with it
        Added.Clear();
        Removed.Clear();
        for (int e = t.EditsRetired; e < t.Edits.Count; e++)
            if (t.Edits[e] is { Gen: var g } edit && g > from)
            {
                if (edit.Op == EditOp.Add) Added.Add(edit.Node);
                if (edit.Op == EditOp.Remove) Removed.Add(edit.Node);
            }
        json.WriteStartArray("diff");
        for (int e = t.EditsRetired; e < t.Edits.Count; e++)
        {
            var edit = t.Edits[e];
            if (edit.Gen <= from) continue;
            bool born = Added.Contains(edit.Node), gone = Removed.Contains(edit.Node);
            if (born && gone) continue;                                                          // made and released since: nothing
            if (edit.Op == EditOp.Add && (gone || Added.Contains(edit.Parent))) continue;        // said with its parent
            if (edit.Op == EditOp.Remove && (born || Removed.Contains(edit.Parent))) continue;   // said with its parent
            if (edit.Op == EditOp.Move && (born || gone)) continue;
            if (!Room()) continue;
            json.WriteStartObject();
            json.WriteString("op", edit.Op switch { EditOp.Add => "add", EditOp.Remove => "remove", _ => "move" });
            json.WriteString("key", NameOf(schema, edit.Kind));
            json.WriteString("handle", edit.Node.ToString());
            json.WriteString("parent", edit.Parent.ToString());
            json.WriteString("parentKey", NameOf(schema, edit.ParentKind));
            if (edit.Op == EditOp.Add)
            {
                RowFields(schema, t, edit.Node, 0);   // its rows' fields: those its rows set
                int slot = Nodes.SlotOf(t, edit.Node);
                if (Nodes.At(t, slot).First != 0 || Fields.Count > 0)
                {
                    json.WriteStartArray("children");
                    foreach (var (key, _, value) in Fields) Field(json, t, key, value);
                    for (int c = Nodes.At(t, slot).First; c != 0; c = Nodes.At(t, c).Next) Node(json, t, schema, c, 2);
                    json.WriteEndArray();
                }
            }
            json.WriteEndObject();
        }

        //- the rows: each node's, their fields that differ between then and now,
        // as each kind's DiffRow says (a node added or removed since is said above)
        for (int slot = Nodes.Root; slot != 0; slot = Nodes.NextInTree(t, slot))
        {
            var node = Nodes.IdOf(t, slot);
            if (Added.Contains(node)) continue;
            Fields.Clear();
            Schemas.Of(schema, Nodes.At(t, slot).Kind)?.DiffRow?.Invoke(t, node, from, Fields);
            foreach (var (key, old, now) in Fields)
            {
                if (!Room()) continue;
                json.WriteStartObject();
                json.WriteString("op", "change");
                json.WriteString("key", key);
                json.WriteString("handle", Nodes.IdOf(t, slot).ToString());
                json.WriteString("parent", Nodes.IdOf(t, slot).ToString());
                json.WriteString("parentKey", NameOf(schema, Nodes.At(t, slot).Kind));   // the node's kind
                WriteValue(json, t, old, "old");
                WriteValue(json, t, now, "new");
                if (old.Type == EvalTypes.Node || now.Type == EvalTypes.Node) json.WriteString("kind", "handle");
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();
        if (more > 0) json.WriteNumber("more", more);
    }

    static readonly HashSet<NodeId> Added = new(), Removed = new();   // reused: one answer at a time

    /// <summary>
    /// Writes the schema: each kind, what it is, the keys it may hold, and its row (what a node of it
    /// presents and offers, as [Row] declares it), each command by its table entry's wire name and text.
    /// </summary>
    public static void Schema(Utf8JsonWriter json, Schema schema)
    {
        json.WriteStartArray();
        foreach (var kind in schema.Kinds)
        {
            if (kind is null) continue;
            json.WriteStartObject();
            json.WriteString("kind", kind.Name);
            json.WriteString("label", kind.Label);
            json.WriteString("description", kind.Description);
            json.WriteStartArray("children");
            foreach (var (key, type) in kind.Shape?.Members ?? [])
            {
                json.WriteStartObject();
                json.WriteString("key", key);
                json.WriteString("value", ValueName(type));
                if (ModelEval.KindOf(type.Choices) is var refers and not Kinds.None) json.WriteString("ref", NameOf(schema, refers));
                if (type.Editable) json.WriteBoolean("editable", true);
                json.WriteString("description", type.Description);
                json.WriteEndObject();
            }
            foreach (var child in kind.Children)
            {
                json.WriteStartObject();
                json.WriteString("key", NameOf(schema, child.Kind));
                json.WriteString("value", "node");
                if (child.Many) json.WriteBoolean("many", true);
                json.WriteString("description", Schemas.Of(schema, child.Kind)?.Description ?? "");
                json.WriteEndObject();
            }
            json.WriteEndArray();
            // Its row, as declared: what a node of the kind presents and offers, in order; the
            // commands outside every row are the command table's (test.ops).
            if (kind.Row is { } row)
            {
                json.WriteStartArray("row");
                foreach (var entry in row)
                {
                    json.WriteStartObject();
                    if (entry.Op is not { } op) json.WriteString("field", entry.Name);
                    else
                    {
                        json.WriteString("command", op.Wire);
                        json.WriteString("text", op.Short ?? op.Label);
                    }
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // What kind of value a field holds, by its type, as the schema says it: int, float, bool, string, handle, expr, object.
    static string ValueName(EvalType type) => type.Kind switch
    {
        EvalKind.Int => "int", EvalKind.Real => "float", EvalKind.Bool => "bool", EvalKind.Text => "string", EvalKind.Address => "handle",
        EvalKind.Opaque when type.Text == EvalTypes.Expression.Text => "expr",
        EvalKind.Parts when type.PartType is not null => "list",
        _ => "object",
    };

    // A row kind's fields of the node `id`: those that differ between its row as generation `gen` saw it
    // and its newest (gen 0: from its default row: the fields its row sets), into Fields.
    static void RowFields(Schema schema, ModelStore t, NodeId id, long gen)
    {
        Fields.Clear();
        Schemas.Of(schema, Nodes.KindOf(t, id))?.DiffRow?.Invoke(t, id, gen, Fields);
    }

    static readonly List<(string Field, Value Before, Value After)> Fields = new();   // reused: one answer at a time

    // A field of a row, written as a child holding a value is: its key, its value.
    static void Field(Utf8JsonWriter json, ModelStore t, string key, in Value value, bool editable = false)
    {
        json.WriteStartObject();
        json.WriteString("key", key);
        WriteValue(json, t, value);
        if (editable) json.WriteBoolean("editable", true);
        json.WriteEndObject();
    }

    // A node: its kind's name, its id, its row (what it shows on screen), its fields (its row's: each its
    // value, its default before one is written), and its children while depth
    // lasts; past depth, how many children there are.
    static void Node(Utf8JsonWriter json, ModelStore t, Schema schema, int slot, int depth)
    {
        var n = Nodes.At(t, slot);
        var id = Nodes.IdOf(t, slot);
        json.WriteStartObject();
        json.WriteString("key", NameOf(schema, n.Kind));
        json.WriteString("handle", id.ToString());
        // Its row, as its kind declares it (Schemas.RowOf): a field by its key and whether a person types
        // into it; a command by its text there and its wire name. Each is on screen at <handle>/<key or
        // command>. Every command of it: which are offered is the context's where the row is drawn, the
        // app's, and a walk of the tree is in none.
        Row.Clear();
        if (Schemas.Of(schema, n.Kind) is { } rowKind) Schemas.RowOf(rowKind, null, Row);
        if (Row.Count > 0)
        {
            json.WriteStartArray("row");
            foreach (var entry in Row)
            {
                json.WriteStartObject();
                if (entry.Command is { } op) { json.WriteString("command", op.Wire); json.WriteString("text", op.Short ?? op.Label); }
                else if (entry.Field is { } field)
                {
                    json.WriteString("field", field);
                    json.WriteString("label", field);
                    if (entry.Type?.Editable == true) json.WriteBoolean("editable", true);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        var kind = Schemas.Of(schema, n.Kind);
        if (depth <= 0)
        {
            int count = 0;
            for (int c = n.First; c != 0; c = Nodes.At(t, c).Next) count++;
            if (count > 0) json.WriteNumber("children", count);
        }
        else
        {
            bool any = n.First != 0 || kind?.Shape?.Members?.Length > 0;
            if (any) json.WriteStartArray("children");
            if (kind is not null)
                foreach (var (key, type) in kind.Shape?.Members ?? [])
                    Field(json, t, key, ModelEval.FieldOf(schema, t, kind, id, key), type.Editable);
            for (int c = n.First; c != 0; c = Nodes.At(t, c).Next)
                Node(json, t, schema, c, depth - 1);
            if (any) json.WriteEndArray();
        }
        json.WriteEndObject();
    }

    // A value as JSON's own kind where it has one, under name; a handle as its text, said to be one,
    // and said to be gone when it no longer resolves; an app's own kind as its fields.
    static void WriteValue(Utf8JsonWriter json, ModelStore t, in Value v, string name = "value")
    {
        if (v.Type == EvalTypes.Node)
        {
            // Said to be a node once per object: a change's old and new share one "kind".
            json.WriteString(name, new NodeId(v.Id).ToString());
            if (name == "value") json.WriteString("kind", "handle");
            if (!Nodes.Holds(t, new NodeId(v.Id))) json.WriteBoolean(name == "value" ? "gone" : name + "Gone", true);
            return;
        }
        switch (v.Kind)
        {
            case EvalKind.None: break;
            case EvalKind.Int: json.WriteNumber(name, v.AsInt); break;
            case EvalKind.Real: json.WriteNumber(name, v.AsReal); break;
            case EvalKind.Bool: json.WriteBoolean(name, v.AsBool); break;
            case EvalKind.Text: json.WriteString(name, v.AsText); break;
            default:
                // As the evaluator prints it: an expression as written (the model keeps it, never evaluates
                // it), a list as how many it holds; said once per object, as old and new share it.
                json.WriteString(name, Exprs.ValueText(v));
                if (name == "value") json.WriteString("kind", v.Type == EvalTypes.Expression ? "expr" : "object");
                break;
        }
    }

    static int ChildWithId(ModelStore t, int parent, long id)
    {
        for (int c = Nodes.At(t, parent).First; c != 0; c = Nodes.At(t, c).Next)
            if (Nodes.At(t, c).Id.Value == id) return c;
        return 0;
    }
}
