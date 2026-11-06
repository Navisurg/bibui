// cmdgen: the code a command set implies, written before the app compiles (Bibui.Commands'
// build step, buildTransitive/Bibui.Commands.targets).
//
//     cmdgen <output.cs> <source.cs | @list>...
//
// A command set is a `partial record` marked [CommandSet], with its commands as records
// nested in it. For each set it writes:
//
//   - its wire names, as [JsonDerivedType] lines on a partial of the set, for
//     System.Text.Json's generator to read like hand-written ones: Counter_Set is "counter.set";
//   - a push method per command, reached through `model.Cmds` on a Model of that set, and
//     `push.Cmds` on the Pushes an apply is given: model.Cmds.CounterSet(42) pushes
//     new Cmd.Counter_Set(42);
//   - its table, <Set>Ops: each command's wire name, label ([Label]), description
//     ([Description], on the record and on its arguments), contracts
//     ([ReadsOnly]), type and arguments (with the node kind of a typed id argument: CaseId),
//     looked up by command, wire name, type or label.
//
// With a node set beside it, also FromContext and Missing: a command made from a Context (the library's:
// a frame of the ContextStack the UI pushes as it draws, a node per kind), each argument named as its kind
// and of its kind's id type (PanelId Panel) taken from that kind's slot, so a palette, a button or a menu
// can run any command without code of its own; any other argument is the context's to ask for, never to guess.
//
// It warns of a command no `case` or pattern names, which nothing would handle (CMDGEN4),
// and of one without a [Description], which a palette or a client can only show by name
// (CMDGEN5). Arguments need no description: their names usually say enough.
//
// A node set is a `partial record` (or static class) marked [NodeSet], with the model's kinds of node as
// records nested in it: a kind with fields is a row kind, its record its row. For each set it writes
// <Set>Kinds (each kind's index, NodeKinds.Panel, and name; a row kind's default row, table and fields by
// key; each kind's KindInfo; the schema; the first tree), <Set>Tables (a typed table per row kind, for code
// that works on a table whole), an id type per kind (PanelId: its row and each field read through it, from
// the model's store, and written with Modify) and its JSON. A node is named by its id everywhere, typed by its kind ({Kind}Id); tree work is the
// generic Nodes', by kind index (NodeKinds.Case), so no kind is spelt as a string.
//
// Syntax only: the attributes are read from the source, as written, and copied into the
// table, so nothing is reflected at run time. The file is only rewritten when it changes,
// so an unchanged command set does not make the app recompile.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: cmdgen <output.cs> <source.cs | @list>...");
    return 2;
}
var output = args[0];
var sources = args.Skip(1).SelectMany(a => a.StartsWith('@') ? File.ReadAllLines(a[1..]) : [a])
    .Where(p => p.Length > 0 && Path.GetFullPath(p) != Path.GetFullPath(output));

//- the sets, by their attribute; then a set's commands, the records nested in any of its
// partials (the attribute need only be on one)
var roots = sources.Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p).GetCompilationUnitRoot()).ToList();
var sets = new List<(string Name, string? Namespace, List<RecordDeclarationSyntax> Commands)>();
foreach (var type in roots.SelectMany(r => r.DescendantNodes().OfType<RecordDeclarationSyntax>()))
    if (type.Parent is not TypeDeclarationSyntax && Attribute(type.AttributeLists, "CommandSet") is not null
        && !sets.Any(s => s.Name == type.Identifier.Text && s.Namespace == NamespaceOf(type)))
        sets.Add((type.Identifier.Text, NamespaceOf(type), []));
foreach (var record in roots.SelectMany(r => r.DescendantNodes().OfType<RecordDeclarationSyntax>()))
{
    if (record.Parent is TypeDeclarationSyntax) continue;
    int set = sets.FindIndex(s => s.Name == record.Identifier.Text && s.Namespace == NamespaceOf(record));
    if (set >= 0) sets[set].Commands.AddRange(record.Members.OfType<RecordDeclarationSyntax>());
}

//- the node sets, by their attribute; then a set's kinds, the records nested in any of its partials
var nodeSets = new List<(string Name, string? Namespace, List<RecordDeclarationSyntax> Kinds)>();
// (a set is a static class or a record, whichever reads better; its kinds are always records)
foreach (var type in roots.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>()))
    if (type.Parent is not TypeDeclarationSyntax && Attribute(type.AttributeLists, "NodeSet") is not null
        && !nodeSets.Any(s => s.Name == type.Identifier.Text && s.Namespace == NamespaceOf(type)))
        nodeSets.Add((type.Identifier.Text, NamespaceOf(type), []));
foreach (var type in roots.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>()))
{
    if (type.Parent is TypeDeclarationSyntax) continue;
    int set = nodeSets.FindIndex(s => s.Name == type.Identifier.Text && s.Namespace == NamespaceOf(type));
    if (set >= 0) nodeSets[set].Kinds.AddRange(type.Members.OfType<RecordDeclarationSyntax>());
}
// [In(typeof(Parent), Many = true)] on a kind: where it sits, said on the kind, as the parameter
// `[Many] Case Case` on the parent would say it, so a kind whose record is its data (a record struct: a
// table's row) holds only its fields. Written here as that parameter of the parent, so what follows reads
// one way to say where a kind sits; placedBy keeps which parameters were made so (a row kind's children).
var placedBy = new Dictionary<string, HashSet<string>>();
foreach (var (_, _, kinds) in nodeSets)
    foreach (var k in kinds.ToList())
    {
        if (Attribute(k.AttributeLists, "In") is not { ArgumentList.Arguments: [{ Expression: TypeOfExpressionSyntax { Type: var parentType } }, ..] } inAttr) continue;
        var parentName = parentType.ToString()[(parentType.ToString().LastIndexOf('.') + 1)..];
        int at = kinds.FindIndex(q => q.Identifier.Text == parentName);
        if (at < 0)
        {
            Console.Error.WriteLine($"{Where(k)}: warning CMDGEN11: {k.Identifier.Text} is [In({parentName})], which its node set does not declare: it sits nowhere.");
            continue;
        }
        bool manyIn = inAttr.ArgumentList!.Arguments.Any(a => a.NameEquals?.Name.Identifier.Text == "Many" && a.Expression.ToString() == "true");
        var kindName = k.Identifier.Text;
        var desc = Text(Attribute(k.AttributeLists, "Description")) ?? "\"\"";
        var param = SyntaxFactory.ParseParameterList($"([global::System.ComponentModel.Description({desc})] {(manyIn ? "[Many] " : "")}{kindName}{(manyIn ? "" : "?")} {kindName})").Parameters[0];
        kinds[at] = kinds[at].AddParameterListParameters(param);
        (placedBy.TryGetValue(parentName, out var made) ? made : placedBy[parentName] = []).Add(kindName);
    }
// The enum tables: a field marked [EnumTable("Body")], whose rows are the values of the enum it names.
// The enum is written from the table, so the two cannot drift apart. The first column of each row is the
// value's name ("card": Body.Card); a column named Label, when the table's type names one, is how a person
// reads it. Written before the kinds are read, so a field of the enum is a kind of field (Field: kept by
// its name, picked from them, each shown by its label).
var enumTables = new List<(string Enum, string? Namespace, string Table, string[] Names, string[]? Labels)>();
foreach (var field in roots.SelectMany(r => r.DescendantNodes().OfType<FieldDeclarationSyntax>()))
{
    if (Attribute(field.AttributeLists, "EnumTable") is not { ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax enumLit }] }) continue;
    var variable = field.Declaration.Variables[0];
    IEnumerable<ExpressionSyntax> rows = variable.Initializer?.Value switch
    {
        CollectionExpressionSyntax c => c.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
        InitializerExpressionSyntax i => i.Expressions,
        ArrayCreationExpressionSyntax { Initializer: { } i } => i.Expressions,
        ImplicitArrayCreationExpressionSyntax { Initializer: var i } => i.Expressions,
        _ => [],
    };
    // The Label column, by its name in the table's type: (string Name, string Label, Expr Of)[].
    int labelAt = field.Declaration.Type is ArrayTypeSyntax { ElementType: TupleTypeSyntax tuple }
        ? tuple.Elements.ToList().FindIndex(e => e.Identifier.Text == "Label") : -1;
    var names = new List<string>();
    var labels = new List<string>();
    foreach (var row in rows)
        if (row is TupleExpressionSyntax { Arguments: [{ Expression: LiteralExpressionSyntax { Token.Value: string first } }, ..] } t)
        {
            names.Add(first);
            labels.Add(labelAt > 0 && labelAt < t.Arguments.Count && t.Arguments[labelAt].Expression is LiteralExpressionSyntax { Token.Value: string label } ? label : first);
        }
        else Console.Error.WriteLine($"{Where(row)}: warning CMDGEN21: a row of the [EnumTable] {variable.Identifier.Text} that is not a tuple whose first column is a name (\"card\"): no value of {enumLit.Token.ValueText} for it.");
    var enumName = enumLit.Token.ValueText;
    enumTables.Add((enumName, NamespaceOf(field), $"{(field.Parent as TypeDeclarationSyntax)?.Identifier.Text}.{variable.Identifier.Text}", [.. names], labelAt > 0 ? [.. labels] : null));
    if (labelAt > 0) Enums.Labelled.Add(enumName);
    Enums.ByName[enumName] = $"global::{(NamespaceOf(field) is { } ens ? ens + "." : "")}{enumName}";
}

// The lens sets: a type marked [LensSet], a lens per record nested in it (BoardLenses.Mesh: the mesh
// lens). Its first parameter is what it draws; the rest are its arguments, in order. They live in its
// call and nowhere else, so a view is fully said by its call.
var lensSets = new List<(string Name, string? Namespace, List<RecordDeclarationSyntax> Lenses)>();
foreach (var type in roots.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>()))
    if (type.Parent is not TypeDeclarationSyntax && Attribute(type.AttributeLists, "LensSet") is not null)
        lensSets.Add((type.Identifier.Text, NamespaceOf(type), [.. type.Members.OfType<RecordDeclarationSyntax>()]));
var kindNames = nodeSets.SelectMany(s => s.Kinds).Select(k => k.Identifier.Text).ToHashSet();
// Each kind has an id type of its own, {Kind}Id: a NodeId only a check makes. A parameter of one is a
// node of that kind, checked where the id comes in (the wire, a cast), never again.
foreach (var (set, ns, kinds) in nodeSets)
    foreach (var k in kinds)
        KindIds.Of[k.Identifier.Text + "Id"] = (k.Identifier.Text, $"global::{(ns is null ? "" : ns + ".")}{set}Kinds.{k.Identifier.Text}");
// Commands are made from the library's Context (a frame of a ContextStack: a node per kind), each
// argument from its kind's slot; with one node set, whose kinds the slots are. Two node sets would share
// kind indexes, and a slot could not say which set's, so neither is made from a context.
bool fromContexts = nodeSets.Count == 1;
string? kindsType = nodeSets.Count == 1 ? $"global::{(nodeSets[0].Namespace is { } kns ? kns + "." : "")}{nodeSets[0].Name}Kinds" : null;

if (nodeSets.Count > 1)
    Console.Error.WriteLine("warning CMDGEN8: More than one [NodeSet]: their kinds share a context's slots, so commands are not made from a context (no OpInfo.FromContext).");

//- the commands nothing handles: a command whose type no `case` or pattern names anywhere is
// dropped when pushed, and only the app's log would say so. A warning, not an error: one may be
// pushed before its handler is written.
var handled = new HashSet<string>();
foreach (var root in roots)
    foreach (var node in root.DescendantNodes())
    {
        SyntaxNode? label = node switch
        {
            CaseSwitchLabelSyntax c => c.Value,          // case Cmd.Panel_Spawn:
            PatternSyntax p => p,                        // case Cmd.Panel_Focus(var id):, x is Cmd.Focus_Left
            _ => null,
        };
        if (label is null) continue;
        foreach (var name in label.DescendantNodesAndSelf())
            if (name is QualifiedNameSyntax or MemberAccessExpressionSyntax) handled.Add(name.ToString().Replace(" ", ""));
    }
foreach (var (set, _, commands) in sets)
    foreach (var c in commands)
    {
        if (!handled.Contains($"{set}.{c.Identifier.Text}"))
            Console.Error.WriteLine($"{Where(c)}: warning CMDGEN4: Nothing handles {set}.{c.Identifier.Text}: no case names it, so it is dropped when pushed.");
        if (Attribute(c.AttributeLists, "Description") is null)
            Console.Error.WriteLine($"{Where(c)}: warning CMDGEN5: {set}.{c.Identifier.Text} has no [Description]: a palette or a client sees only its name.");
    }

//- the Places: a function marked [Place<K, D>("name", "description")] is a layer's query, its data (D)
// at a key (K; NoKey for one with no key). Checked against K and D here, so a Place's stated types are
// its function's: its parameters are the layer's state (or none), then `in K key` (none for NoKey); it
// gives D or D?. A key is a type of this project's (the layer's own) whose fields are numbers, bools or
// texts: the arguments the Place is entered with. A mismatch is an error, and nothing is written.
var typesHere = roots.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>())
    .GroupBy(t => t.Identifier.Text).ToDictionary(g => g.Key, g => g.First());
var places = new List<(MethodDeclarationSyntax Query, string Class, string Key, string Data, string Name, string Description, string? State)>();
bool failed = false;
void Fail(SyntaxNode at, string message)
{
    Console.Error.WriteLine($"{Where(at)}: error CMDGEN30: {message}");
    failed = true;
}
foreach (var m in roots.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>()))
{
    var a = m.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(x => IsNamed(x, "Place"));
    if (a is null) continue;
    var g = a.Name switch { GenericNameSyntax n => n, QualifiedNameSyntax { Right: GenericNameSyntax n } => n, _ => null };
    var owner = (m.Parent as TypeDeclarationSyntax)?.Identifier.Text ?? "";
    var where = $"{owner}.{m.Identifier.Text}";
    if (g is not { TypeArgumentList.Arguments.Count: 2 } || a.ArgumentList?.Arguments is not { Count: 2 } stated)
    {
        Fail(m, $"{where}: a Place is declared [Place<K, D>(\"name\", \"description\")]: its key's type, its data's type, its name and what it is.");
        continue;
    }
    string key = g.TypeArgumentList.Arguments[0].ToString(), data = g.TypeArgumentList.Arguments[1].ToString();
    bool keyless = LastName(key) == "NoKey";
    var ps = m.ParameterList.Parameters;
    int stateCount = ps.Count - (keyless ? 0 : 1);
    var gives = m.ReturnType.ToString();
    if (!m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)))
        Fail(m, $"{where} is a Place's query: a static function.");
    else if (stateCount is not (0 or 1))
        Fail(m, $"{where} is the query of a Place keyed by {key}: its parameters are the layer's state (or none), then {(keyless ? "nothing" : $"in {key} key")}.");
    else if (!keyless && ps[^1].Type?.ToString() != key)
        Fail(ps[^1], $"{where}'s last parameter is the Place's key: in {key}, not {ps[^1].Type}.");
    else if (gives.TrimEnd('?') != data)
        Fail(m.ReturnType, $"{where} gives the Place's data: {data} (or {data}?), not {gives}.");
    else if (!typesHere.ContainsKey(LastName(data)))
        Fail(m, $"{where}: the Place's data, {data}, is a type of this project's, whose fields cmdgen reads.");
    else if (!keyless && (!typesHere.TryGetValue(LastName(key), out var k) || FieldsOf(k).Any(f => EvalLeaf(f.Type) is null or "Node" or "Expression" or "Enum")))
        Fail(m, $"{where}: the Place's key, {key}, is a type of this project's whose fields are numbers, bools or texts: the arguments it is entered with.");
    else
        places.Add((m, owner, key, data, stated[0].Expression.ToString(), stated[1].Expression.ToString(), stateCount == 1 ? ps[0].Type!.ToString() : null));
}
foreach (var c in places.GroupBy(p => p.Class))   // one registration per class: its queries read one state
    if (c.Select(p => p.State).Distinct().Count() > 1) Fail(c.First().Query, $"{c.Key}'s Places read one state: their queries' first parameters differ.");
if (failed) return 1;
// What cmdgen writes a shape and a reader for: each Place's data and key, as declared (a type reached
// from two Places, once).
var placeTypes = places.SelectMany(p => LastName(p.Key) == "NoKey" ? [p.Data] : new[] { p.Data, p.Key })
    .Select(LastName).Distinct().Select(n => typesHere[n]).ToList();
var keyTypes = places.Select(p => LastName(p.Key)).Where(k => k != "NoKey").ToHashSet();
// And each row kind's record: the model's Place reads a
// node's row by its shape (a list in a row stepped into as any layer's is), which is its kind's shape, each
// field a type of its own (its name, kind.field; whether it is editable; its choices). A field that is a
// child kind is the tree's, not the row's: not in the shape.
var rowRecordKind = new Dictionary<TypeDeclarationSyntax, string>();   // a row kind's record → its kind's name
foreach (var k in nodeSets.SelectMany(n => n.Kinds))
{
    if (!IsRowKind(k)) continue;
    TypeDeclarationSyntax? rowType = null;
    if (typesHere.TryGetValue(k.Identifier.Text, out var declared)) rowType = declared;
    if (rowType is null) continue;
    rowRecordKind[rowType] = Snake(k.Identifier.Text);
    if (!placeTypes.Contains(rowType)) placeTypes.Add(rowType);
}

//- the file
var code = new StringBuilder();
code.AppendLine("""
    // <auto-generated/> by cmdgen (Bibui.Commands), from the [CommandSet]s. Do not edit.
    #nullable enable
    using System.Text.Json.Serialization;
    using Bibui.Commands;
    """);
// The usings of the files that declare node sets: a key's type is written as it is there (BibuiStr).
foreach (var u in roots.Where(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(t => Attribute(t.AttributeLists, "NodeSet") is not null || placeTypes.Contains(t))
                                 || r.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => places.Any(p => p.Query == m)))
             .SelectMany(r => r.Usings).Select(u => u.ToString()).Distinct())
    if (u != "using System.Text.Json.Serialization;" && u != "using Bibui.Commands;") code.AppendLine(u);
foreach (var (set, ns, commands) in sets)
{
    const string model = "global::Bibui.Commands.Model";
    code.AppendLine();
    if (ns is not null) code.AppendLine($"namespace {ns}\n{{");

    // The wire names, on a partial of the set: System.Text.Json makes each command's metadata from
    // there. The set is a library Cmd: the model, its log and the wire take the app's and the library's
    // alike (Bibui.Commands.CmdJson names both).
    foreach (var c in commands)
        code.AppendLine($"[JsonDerivedType(typeof({set}.{c.Identifier.Text}), \"{WireName(c.Identifier.Text)}\")]");
    code.AppendLine($"public abstract partial record {set} : global::Bibui.Commands.Cmd;");
    code.AppendLine();

    // The push methods, one per command, on a struct a model of this set hands out as `Cmds`.
    code.AppendLine($$"""
        /// <summary>
        /// The commands of <see cref="{{set}}"/>, as methods that push them onto a model:
        /// <c>model.Cmds.CounterSet(42)</c>. A push only queues the command: it runs when the
        /// model runs its commands.
        /// </summary>
        public readonly struct {{set}}Queue(global::Bibui.Commands.Pushes push)
        {
        """);
    foreach (var c in commands)
    {
        var name = c.Identifier.Text;
        var parameters = c.ParameterList?.Parameters ?? default;
        var declared = string.Join(", ", parameters.Select(p => $"{p.Type} {Argument(p)}{(p.Default is null ? "" : " " + p.Default)}"));
        var passed = string.Join(", ", parameters.Select(Argument));
        code.AppendLine($"    /// <summary>Queues <c>{WireName(name)}</c>.</summary>");
        code.AppendLine($"    public void {MethodName(name)}({declared}) => push.Push(new {set}.{name}({passed}));");
    }
    code.AppendLine($$"""
        }

        public static class {{set}}QueueExtensions
        {
            extension({{model}} model)
            {
                /// <summary>This model's commands, as methods that push them.</summary>
                public {{set}}Queue Cmds => new(new global::Bibui.Commands.Pushes(model));
            }

            extension(global::Bibui.Commands.Pushes push)
            {
                /// <summary>The commands, as methods that push them where these pushes go: an apply's follow-ups.</summary>
                public {{set}}Queue Cmds => new(push);
            }
        }

        /// <summary>
        /// The commands of <see cref="{{set}}"/> as a table: each one's wire name, label,
        /// description, contracts and arguments, as their attributes say. Looked up by
        /// command, wire name, type or label; nothing is reflected.
        /// </summary>
        public static class {{set}}Ops
        {
            public static readonly OpInfo[] All =
            [
        """);
    foreach (var c in commands)
    {
        var name = c.Identifier.Text;
        var label = Text(Attribute(c.AttributeLists, "Label")) ?? Quote(Label(name));
        var description = Text(Attribute(c.AttributeLists, "Description")) ?? "\"\"";
        var flags = string.Join(" | ", new[] { ("ReadsOnly", "OpFlags.ReadOnly") }
            .Where(f => Attribute(c.AttributeLists, f.Item1) is not null).Select(f => f.Item2).DefaultIfEmpty("OpFlags.None"));
        var argInfos = string.Join(", ", (c.ParameterList?.Parameters ?? default).Select(p =>
            $"new(\"{p.Identifier.Text}\", \"{p.Type}\", {Text(Attribute(p.AttributeLists, "Description")) ?? "\"\""}, {ParamKind(p) ?? "null"})"));
        var shortText = Text(Attribute(c.AttributeLists, "Short"));
        var icon = Text(Attribute(c.AttributeLists, "Icon"));
        // How it is made from a context: each argument named as its kind (CaseId Case) from that kind's
        // slot; any other (a seed, panel.resize's First and Second) never the context's, so neither made
        // nor offered from one. Missing: its first argument the context does not hold.
        var builders = "";
        if (fromContexts)
        {
            int at = commands.IndexOf(c);
            var parameters = c.ParameterList?.Parameters ?? default;
            var slots = parameters.Select(p => ContextSlot(p, kindNames, kindsType!)).ToList();
            var missing = new StringBuilder("c => ");
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] is not { } slot) { missing.Append($"All![{at}].Args[{i}]"); break; }
                missing.Append($"c.Of({slot.Slot}).IsNone ? All![{at}].Args[{i}] : ");
            }
            if (slots.All(x => x is not null)) missing.Append("null");
            var made = slots.All(x => x is not null)
                ? $"c => {string.Concat(slots.Select(x => $"c.Of({x!.Value.Slot}).IsNone ? null : "))}new {set}.{name}({string.Join(", ", slots.Select(x => x!.Value.Value))})"
                : "null";
            builders = $" {{ FromContext = {made}, Missing = {missing} }}";
        }
        code.AppendLine($"        new(\"{WireName(name)}\", {label}, {description}, OpKind.Command, {flags}, typeof({set}.{name}), [{argInfos}]{(shortText is null ? "" : $", Short: {shortText}")}{(icon is null ? "" : $", Icon: {icon}")}){builders},");
    }
    code.AppendLine($$"""
            ];

            /// <summary>The command's entry: a type switch, no lookup.</summary>
            public static OpInfo Of({{set}} cmd) => cmd switch
            {
        """);
    for (int i = 0; i < commands.Count; i++)
        code.AppendLine($"        {set}.{commands[i].Identifier.Text} => All[{i}],");
    code.AppendLine($$"""
                _ => throw new System.ArgumentException($"Not a command cmdgen saw: {cmd}"),
            };

            /// <summary>The entry with this wire name (<c>panel.close</c>), or null.</summary>
            public static OpInfo? ByWire(string wire) => wire switch
            {
        """);
    for (int i = 0; i < commands.Count; i++)
        code.AppendLine($"        \"{WireName(commands[i].Identifier.Text)}\" => All[{i}],");
    code.AppendLine($$"""
                _ => null,
            };

            /// <summary>The entry of the command type <paramref name="type"/>, or null.</summary>
            public static OpInfo? ByType(System.Type type)
            {
                foreach (var op in All) if (op.Type == type) return op;
                return null;
            }

            /// <summary>The entry with this label, or null.</summary>
            public static OpInfo? ByLabel(string label)
            {
                foreach (var op in All) if (op.Label == label) return op;
                return null;
            }
        """);
    code.AppendLine("}");
    if (ns is not null) code.AppendLine("}");
}

foreach (var (set, ns, kinds) in nodeSets)
{
    code.AppendLine();
    if (ns is not null) code.AppendLine($"namespace {ns}\n{{");

    // The kinds: each one's index (what a tree node's Kind holds, and its table's place in the tree's
    // tables: the root's is the library's, Kinds.Root; the rest follow the library's own), a row kind's
    // table, then each kind's KindInfo and the schema. The KindInfos come before the schema: a static field
    // is set in the order written.
    var rowKinds = kinds.Where(IsRowKind).ToList();
    var indexOf = new Dictionary<string, int>();
    int nextIndex = 4;   // Kinds.FirstApp
    foreach (var k in kinds) indexOf[k.Identifier.Text] = k.Identifier.Text == "Root" ? 1 : nextIndex++;
    code.AppendLine($$"""
        /// <summary>The kinds of <see cref="{{set}}"/>: each one's index (a tree node's Kind; its table's place), a row kind's table, and the schema (model.schema). Written from their records.</summary>
        public static class {{set}}Kinds
        {
        """);
    foreach (var k in kinds) code.AppendLine($"    public const ushort {k.Identifier.Text} = {indexOf[k.Identifier.Text]};");
    code.AppendLine("\n    /// <summary>Each kind's name: what an expression steps to it by (it.case), and the port's.</summary>\n    public static class Names\n    {");
    foreach (var k in kinds) code.AppendLine($"        public const string {k.Identifier.Text} = \"{Snake(k.Identifier.Text)}\";");
    code.AppendLine("    }");
    foreach (var k in rowKinds)
    {
        var kn = k.Identifier.Text;
        RowModule(code, $"{set}.{kn}", IdType(kn), k,
            [.. (k.ParameterList?.Parameters ?? default).Where(p => IsRowField(k, p))], [.. (k.ParameterList?.Parameters ?? default).Where(p => !IsPlaced(placedBy, kn, p))], kindNames);
    }
    var startRules = new List<(string Type, string Lens)>();   // the view rules the tree starts with: NewTree, below
    var viewTypes = new List<(string Const, string Name)>();   // the types a view rule may be for: a kind, a list of one, a plain field (Types, below)
    foreach (var k in kinds)
    {
        var name = k.Identifier.Text;
        viewTypes.Add((name, Snake(name)));
        var label = Text(Attribute(k.AttributeLists, "Label")) ?? Quote(Label(name));
        var description = Text(Attribute(k.AttributeLists, "Description")) ?? "\"\"";
        var children = new List<string>();
        foreach (var p in k.ParameterList?.Parameters ?? default)
        {
            var (value, _, _, _) = Field(p, kindNames);
            bool many = Attribute(p.AttributeLists, "Many") is not null;
            if (value == "None" && many) viewTypes.Add((TypeName(p) + "List", Snake(TypeName(p)) + "[]"));
            else if (IsRowField(k, p) && EvalLeaf(p.Type!.ToString().TrimEnd('?')) is not null) viewTypes.Add((name + p.Identifier.Text, $"{Snake(name)}.{Snake(p.Identifier.Text)}"));
            bool editable = Attribute(p.AttributeLists, "Editable") is not null;
            // [Pick(choices)], [Range(min, max)]: the lens the field's type is seen through, written at its
            // declaration and kept as a view rule the tree starts with (NewTree), so an app can replace it.
            string? view = null;
            if (Attribute(p.AttributeLists, "ViewRule") is { ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax fieldRule }] })
                view = $"global::Bibui.Commands.ExpParser.Required({fieldRule})";
            else if (Attribute(p.AttributeLists, "Pick") is { ArgumentList.Arguments: [var choices] })
                view = $"global::Bibui.Commands.Exprs.Call(global::Bibui.Commands.Lenses.Pick.Name, global::Bibui.Commands.Exprs.It, global::Bibui.Commands.Exprs.Name({choices.Expression}))";
            else if (Attribute(p.AttributeLists, "Range") is { ArgumentList.Arguments: [var min, var max, ..] rangeArgs })
            {
                var step = rangeArgs.FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Step")?.Expression;
                view = $"global::Bibui.Commands.Exprs.Call(global::Bibui.Commands.Lenses.Range.Name, global::Bibui.Commands.Exprs.It, global::Bibui.Commands.Exprs.Lit((double)({min.Expression})), global::Bibui.Commands.Exprs.Lit((double)({max.Expression}))"
                    + (step is null ? ")" : $", global::Bibui.Commands.Exprs.Lit((double)({step})))");
            }
            if (editable && (value == "None" || many))
                Console.Error.WriteLine($"{Where(p)}: warning CMDGEN10: {name}.{p.Identifier.Text} is [Editable], but it is a child node, not a field: only a field's value is set (node.set), or typed into in place.");
            if (view is not null) startRules.Add(($"{Snake(name)}.{Snake(p.Identifier.Text)}", view));
            if (value == "None")
            {
                children.Add($"new({set}Kinds.{TypeName(p)}, {(many ? "true" : "false")})");
                if (Snake(p.Identifier.Text) != Snake(TypeName(p)))
                    Console.Error.WriteLine($"{Where(p)}: warning CMDGEN7: {name}.{p.Identifier.Text} is a {TypeName(p)} under the key \"{Snake(p.Identifier.Text)}\": a child's key is its kind, \"{Snake(TypeName(p))}\". Name the parameter {TypeName(p)}.");
            }
        }
        // Its row: [Row(nameof(Note), typeof(Cmd.Picture_Clear), …)], a field by its key, a command by its
        // entry in its set's table (what library code makes it from: OpInfo.FromContext); a command no set
        // declares, or a key the kind does not hold, is said.
        var row = new List<string>();
        var keys = (k.ParameterList?.Parameters ?? default).Select(q => q.Identifier.Text).ToHashSet();
        foreach (var arg in Attribute(k.AttributeLists, "Row")?.ArgumentList?.Arguments ?? default)
        {
            if (arg.Expression is TypeOfExpressionSyntax { Type: var rowType })
            {
                var command = rowType.ToString()[(rowType.ToString().LastIndexOf('.') + 1)..];
                var declaring = sets.FirstOrDefault(s => s.Commands.Any(c => c.Identifier.Text == command));
                if (declaring.Commands is null)
                {
                    Console.Error.WriteLine($"{Where(arg)}: warning CMDGEN9: {name}'s row names {rowType}, which no [CommandSet] declares.");
                    continue;
                }
                int index = declaring.Commands.FindIndex(c => c.Identifier.Text == command);
                row.Add($"new({Quote(WireName(command))}, global::{(declaring.Namespace is { } dns ? dns + "." : "")}{declaring.Name}Ops.All[{index}])");
            }
            else if (arg.Expression is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } nameOf)
            {
                var key = nameOf.ArgumentList.Arguments[0].Expression.ToString();
                key = key[(key.LastIndexOf('.') + 1)..];
                if (!keys.Contains(key))
                    Console.Error.WriteLine($"{Where(arg)}: warning CMDGEN9: {name}'s row names the key {key}, which {name} does not hold.");
                row.Add($"new({Quote(Snake(key))}, null)");
            }
        }
        var kindIcon = Text(Attribute(k.AttributeLists, "Icon"));
        // [ViewRule("rows(it, it.title, it.picked)")]: the view rule the tree starts with for it (NewTree), as
        // written; [ViewRule(typeof(BoardLenses.Board))]: the lens (its generated part, BoardLens) called over
        // the node, board(it); with a dotted path, over that member of it: [ViewRule(typeof(BoardLenses.Board), "case")], board(it.case).
        var ruleArgs = Attribute(k.AttributeLists, "ViewRule")?.ArgumentList?.Arguments;
        if (ruleArgs is [{ Expression: LiteralExpressionSyntax kindRule }])   // an expression, as written
            startRules.Add((Snake(name), $"global::Bibui.Commands.ExpParser.Required({kindRule})"));
        else if (ruleArgs is { Count: > 0 } && ruleArgs.Value[0].Expression is TypeOfExpressionSyntax { Type: var lensType })
        {
            var lensRecord = lensType.ToString()[(lensType.ToString().LastIndexOf('.') + 1)..];
            var over = "global::Bibui.Commands.Exprs.It";
            if (ruleArgs.Value.Count > 1 && ruleArgs.Value[1].Expression is LiteralExpressionSyntax path)
                foreach (var member in path.Token.ValueText.Split('.')) over += $".Get({Quote(member)})";
            startRules.Add((Snake(name), $"global::{lensRecord}Lens.Call({over})"));
        }
        var info = new List<string> { $"Index = {name}", $"Name = {Quote(Snake(name))}", $"Label = {label}", $"Description = {description}" };
        if (IsRowKind(k))
        {
            // Its rows, for code that holds only a node's id: by its id type, found by the node's kind (From).
            bool anyNode = !KindIds.Of.ContainsKey(name + "Id");
            // Its id from the node's: the caller asked this kind because the node is of it.
            var id = anyNode ? "node" : $"{name}Id.Known(node)";
            var shape = name + "Eval";
            info.Add($"Shape = {shape}.Type, NewTable = New{name}Table");
            info.Add($"ReadRow = (t, node, path) => {shape}.Read({name}Table(t).FromId({id}), path)");
            info.Add($"WriteRow = (t, node, key, value) => {{ if (!{name}With(t, {name}Table(t).FromId({id}), key, value, out var row)) return false; {name}Table(t).SetRow({id}, row); return true; }}");
            if (!anyNode) info.Add($"AddRow = (t, node) => {name}Table(t).AddRow({id}, {name}Default)");
            info.Add($"RemoveRow = (t, node) => {name}Table(t).RemoveRow({id})");
            info.Add($"DiffRow = (t, node, since, into) => {{ {name}Table(t).FromIdAt({id}, since, out var before); {name}Diff(before, {name}Table(t).FromId({id}), into); }}");
        }
        if (children.Count > 0) info.Add($"Children = [{string.Join(", ", children)}]");
        if (row.Count > 0) info.Add($"Row = [{string.Join(", ", row)}]");
        if (kindIcon is not null) info.Add($"Icon = {kindIcon}");
        code.AppendLine($"\n    static readonly global::Bibui.Commands.KindInfo {name}Info = new()\n    {{\n        {string.Join(",\n        ", info)},\n    }};");
    }
    code.AppendLine($$"""

            /// <summary>The kinds, with the library's (the root's place, the view rules): what code that holds only an id reads a node through.</summary>
            public static readonly global::Bibui.Commands.Schema Schema = global::Bibui.Commands.Schemas.Make({{string.Join(", ", kinds.Select(k => k.Identifier.Text + "Info"))}});

            /// <summary>The types a view rule may be for, by name: each kind (case), a list of one (case[]), each plain field (case.picked). What code names a type's rule by.</summary>
            public static class Types
            {
            {{string.Join("\n    ", viewTypes.DistinctBy(v => v.Const).Select(v => $"    public static readonly global::Bibui.Commands.ViewRuleType {v.Const} = new({Quote(v.Name)});"))}}
            }

            /// <summary>
            /// A tree to start a model with (Model's tree): its root, its tables, and the view rules its kinds
            /// and fields declare ([ViewRule], [Pick], [Range]), each a view_rule node, as a command would later set one.
            /// </summary>
            public static global::Bibui.Commands.ModelStore NewTree()
            {
                var t = global::Bibui.Commands.Nodes.NewTree(gen: 1);
                TablesOf(t);
        """);
    foreach (var (type, lens) in startRules)
        code.AppendLine($"        global::Bibui.Commands.ModelEval.SetViewRuleForType(t, null, new({Quote(type)}), {lens});");
    code.AppendLine($$"""
                return t;
            }

            /// <summary><paramref name="t"/>'s tables, by kind index: made the first time a tree is asked for them (a tree made without them, a test's), and shared by its generations from then on.</summary>
            public static object?[] TablesOf(global::Bibui.Commands.ModelStore t) => global::Bibui.Commands.Schemas.TablesOf(Schema, t);
        }
        """);

    // The model's tables, typed: a field per row kind, its table in the tree (what a command reads and writes
    // rows through: tables.Cases.FromId(id)).
    // Each kind's id type, and its JSON (written as its NodeId, #4; read checked against the model's tree).
    foreach (var k in kinds)
    {
        var kn = k.Identifier.Text;
        if (!KindIds.Of.ContainsKey(kn + "Id")) continue;
        var rowType = IsRowKind(k) ? $"{set}.{kn}" : null;
        code.AppendLine($$"""

            /// <summary>
            /// A {{Snake(kn)}}'s id: a NodeId only a check makes (From, At; New makes the node), so it names a
            /// {{Snake(kn)}}, or one since released (its row then reads as the default, and a write is refused).
            /// An id is given once and its node's kind never changes, so the check holds for good. default is none.
            /// </summary>
            public readonly record struct {{kn}}Id
            {
                public readonly global::Bibui.Commands.NodeId Id;
                {{kn}}Id(global::Bibui.Commands.NodeId id) => Id = id;

                /// <summary>The id of a node already known to be a {{Snake(kn)}}: for its kind's row functions (KindInfo), which are only asked about a node of their kind.</summary>
                internal static {{kn}}Id Known(global::Bibui.Commands.NodeId node) => new(node);

                /// <summary>The id, when <paramref name="t"/> holds a {{Snake(kn)}} by it; none for another kind's, or one it does not hold.</summary>
                public static {{kn}}Id From(global::Bibui.Commands.ModelStore t, global::Bibui.Commands.NodeId id) =>
                    global::Bibui.Commands.Nodes.HasKind(t, id, {{set}}Kinds.{{kn}}) ? new(id) : default;

                /// <summary>The id of the node in <paramref name="slot"/>, when it is a {{Snake(kn)}}; none for any other.</summary>
                public static {{kn}}Id At(global::Bibui.Commands.ModelStore t, int slot) =>
                    slot != 0 && global::Bibui.Commands.Nodes.At(t, slot) is { Kind: {{set}}Kinds.{{kn}} } n ? new(n.Id) : default;

                /// <summary>A new {{Snake(kn)}}, the last child of <paramref name="parent"/>: its id.</summary>
                public static {{kn}}Id New(global::Bibui.Commands.ModelStore t, int parent) =>
                    new(global::Bibui.Commands.Nodes.IdOf(t, global::Bibui.Commands.Nodes.New(t, parent, {{set}}Kinds.{{kn}})));

                /// <summary>A new {{Snake(kn)}}, the last child of the node <paramref name="parent"/>: its id; none, and nothing made, for a parent gone.</summary>
                public static {{kn}}Id New(global::Bibui.Commands.ModelStore t, global::Bibui.Commands.NodeId parent) =>
                    global::Bibui.Commands.Nodes.SlotOf(t, parent) is var p and not 0 ? New(t, p) : default;

                /// <summary>Its slot in <paramref name="t"/>; 0 once it is released.</summary>
                public int SlotIn(global::Bibui.Commands.ModelStore t) => global::Bibui.Commands.Nodes.SlotOf(t, Id);

                /// <summary>Its node's id: every {{Snake(kn)}}'s id is a node's, so code about nodes takes one as it is.</summary>
                public static implicit operator global::Bibui.Commands.NodeId({{kn}}Id id) => id.Id;

                /// <summary>None: no {{Snake(kn)}} (default): what First and Next give past the last.</summary>
                public bool IsNone => Id.IsNone;

                /// <summary>The first {{Snake(kn)}} under <paramref name="parent"/>; none for none, or a parent gone. With Next: <c>for (var x = {{kn}}Id.First(t, parent); !x.IsNone; x = x.Next(t))</c>.</summary>
                public static {{kn}}Id First(global::Bibui.Commands.ModelStore t, global::Bibui.Commands.NodeId parent) =>
                    global::Bibui.Commands.Nodes.SlotOf(t, parent) is var p and not 0 ? At(t, global::Bibui.Commands.Nodes.Child(t, p, {{set}}Kinds.{{kn}})) : default;

                /// <summary>The next {{Snake(kn)}} under its parent; none past the last, or once it is released.</summary>
                public {{kn}}Id Next(global::Bibui.Commands.ModelStore t) =>
                    SlotIn(t) is var s and not 0 ? At(t, global::Bibui.Commands.Nodes.NextOfKind(t, s)) : default;
            """);
        if (rowType is not null)
            code.AppendLine($$"""

                    /// <summary>Its row in <paramref name="store"/>: its newest; the default row for none, or once it is released.</summary>
                    public {{rowType}} RowFromId(global::Bibui.Commands.ModelStore store) => {{set}}Kinds.{{kn}}Table(store).FromId(this);

                    /// <summary>Writes <paramref name="row"/> as its newest in <paramref name="store"/> (nothing once it is released).</summary>
                    public void Modify(global::Bibui.Commands.ModelStore store, in {{rowType}} row) => {{set}}Kinds.{{kn}}Table(store).SetRow(this, row);
                """);
        // A getter per field of its row, as Salsa's are (case_id.modes(db)): its newest row's field, the
        // record's default once it is released. No setters: a write goes through Modify, the row whole.
        // A field named as a member the id has already (Next, First) gets none: read it from RowFromId.
        string[] idMembers = ["Id", "Known", "From", "At", "New", "SlotIn", "IsNone", "First", "Next", "RowFromId", "Modify", "ToString", "Equals", "GetHashCode"];
        if (rowType is not null)
            foreach (var p in (k.ParameterList?.Parameters ?? default).Where(p => IsRowField(k, p)))
            {
                var field = p.Identifier.Text;
                if (idMembers.Contains(field))
                {
                    Console.Error.WriteLine($"{Where(p)}: warning CMDGEN32: {kn}.{field} has no getter on {kn}Id: the id has a member of that name. Read it from RowFromId.");
                    continue;
                }
                code.AppendLine($"\n        /// <summary>Its {Snake(field)}: its newest row's; the default once it is released.</summary>");
                code.AppendLine($"        public {p.Type} {field}(global::Bibui.Commands.ModelStore store) => {set}Kinds.{kn}Table(store).FromId(this).{field};");
            }
        code.AppendLine($$"""

                public override string ToString() => Id.ToString();
            }

            /// <summary>A {{kn}}Id in JSON: its NodeId's text (#4), read only as a {{Snake(kn)}} of the tree <c>tree</c> gives.</summary>
            public sealed class {{kn}}IdJson(global::System.Func<global::Bibui.Commands.ModelStore> tree) : global::System.Text.Json.Serialization.JsonConverter<{{kn}}Id>
            {
                public override {{kn}}Id Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type type, global::System.Text.Json.JsonSerializerOptions options) =>
                    reader.TokenType == global::System.Text.Json.JsonTokenType.String && global::Bibui.Commands.NodeId.TryParse(reader.GetString(), out var id)
                        ? {{kn}}Id.From(tree(), id) is { IsNone: false } known ? known : throw new global::System.Text.Json.JsonException($"{id} names no {{Snake(kn)}} (another kind's node, or none).")
                        : throw new global::System.Text.Json.JsonException("A {{Snake(kn)}} is named by its id, a string like \"#4\".");

                public override void Write(global::System.Text.Json.Utf8JsonWriter writer, {{kn}}Id value, global::System.Text.Json.JsonSerializerOptions options) =>
                    writer.WriteStringValue(value.Id.ToString());
            }
            """);
    }
    code.AppendLine($"\n/// <summary>The id types' JSON, read against the tree <paramref name=\"tree\"/> gives: what a command's JSON context is given, so a command naming a node of another kind is refused as it is read.</summary>");
    code.AppendLine($"public static class {set}IdsJson\n{{");
    code.AppendLine($"    public static global::System.Text.Json.Serialization.JsonConverter[] For(global::System.Func<global::Bibui.Commands.ModelStore> tree) => [{string.Join(", ", kinds.Where(k => KindIds.Of.ContainsKey(k.Identifier.Text + "Id")).Select(k => $"new {k.Identifier.Text}IdJson(tree)"))}];");
    code.AppendLine("}");
    code.AppendLine($"\n/// <summary>The model's tables of <see cref=\"{set}\"/>'s row kinds, typed: a command reads and writes rows through them.</summary>");
    code.AppendLine($"public readonly struct {set}Tables\n{{");
    foreach (var r in rowKinds) code.AppendLine($"    public readonly global::Bibui.Commands.Table<{RowType(set, r.Identifier.Text)}, {IdType(r.Identifier.Text)}> {Plural(r.Identifier.Text)};");
    code.AppendLine($"\n    /// <summary>The tables of <paramref name=\"t\"/>'s model.</summary>\n    public {set}Tables(global::Bibui.Commands.ModelStore t)\n    {{");
    foreach (var r in rowKinds) code.AppendLine($"        {Plural(r.Identifier.Text)} = (global::Bibui.Commands.Table<{RowType(set, r.Identifier.Text)}, {IdType(r.Identifier.Text)}>){set}Kinds.TablesOf(t)[{set}Kinds.{r.Identifier.Text}]!;");
    code.AppendLine("    }\n}");

    if (ns is not null) code.AppendLine("}");
}

const string Ev = "global::Bibui.Commands";

var fieldNamesWritten = new HashSet<string>();   // a type's field names are written once, however many roots reach it

//- the shapes: a Place's data (the vessel layer's vessels: a Vessels) and its key. cmdgen follows its
// fields to every type reachable from it and writes, in a static class beside it (VesselsEval), each
// one's shape as data (an EvalType: its Members; a collection's PartType) and a typed reader per type,
// following a path of steps (Addresses) to a leaf. The evaluator steps by this generated shape and these
// readers, not by reflection; for a key, a packer from the arguments a Place is entered with. What is not reachable from a
// Place is not readable. A collection is what C# itself counts and indexes: an array, a
// List/IReadOnlyList/IList, or a type of the layer's own with a Count or Length and an int indexer.
// Nothing is written into the data's own types: they stay data.
foreach (var root in placeTypes)
{
    var rootName = root.Identifier.Text;
    // Every record reachable from the root, in the order reached; the collections' element types.
    var records = new List<TypeDeclarationSyntax> { root };
    var lists = new List<string>();   // a collection's element type, as written: one list type each
    for (int r = 0; r < records.Count; r++)
        foreach (var (_, type, _) in FieldsOf(records[r]))
        {
            var t = type.TrimEnd('?');
            if (kindNames.Contains(LastName(t))) continue;   // a child kind: the tree's
            if (EvalLeaf(t) is not null) continue;
            if (CollectionOf(t, typesHere) is var (element, _))
            {
                if (!lists.Contains(element)) lists.Add(element);
                if (EvalLeaf(element) is null && typesHere.TryGetValue(LastName(element), out var e) && !records.Contains(e)) records.Add(e);
                continue;
            }
            if (typesHere.TryGetValue(LastName(t), out var d) && !records.Contains(d)) records.Add(d);
            // Anything else (a type from elsewhere: a dictionary, a Random) is not read, and not in the shape.
        }

    string TypeOf(string t) => EvalLeaf(t.TrimEnd('?')) is { } leaf ? $"{Ev}.EvalTypes.{(leaf == "Enum" ? "Text" : leaf)}"
        : CollectionOf(t.TrimEnd('?'), typesHere) is var (element, _) ? $"L{lists.IndexOf(element)}"
        : $"T_{LastName(t)}";
    var desc = Text(Attribute(root.AttributeLists, "Description")) ?? "\"\"";
    var ns = NamespaceOf(root);
    if (ns is not null) code.AppendLine($"namespace {ns}\n{{");
    code.AppendLine($$"""

        /// <summary>{{rootName}}, a Place's data or key, to the evaluator: the shape of every type reachable from it, and a reader along a path (Addresses). cmdgen's, from its fields.</summary>
        public static class {{rootName}}Eval
        {
            /// <summary>Its type: what Places.Add is given for it.</summary>
            public static readonly {{Ev}}.EvalType Type;
        """);
    foreach (var rec in records) code.AppendLine($"    static readonly {Ev}.EvalType T_{rec.Identifier.Text};");
    // A row kind's record: each leaf field's own type, what the evaluator gives it a view rule by (case.title),
    // whether a person or an agent may set it (node.set), and what it may be set to (an enum's names; every
    // node of the kind a [Kind] id names).
    bool rowRecord = rowRecordKind.TryGetValue(root, out var rowKindName);
    var ownFields = FieldsOf(root).Where(f => IsRead(f.Type, typesHere) && !kindNames.Contains(LastName(f.Type))).ToList();
    if (rowRecord)
        foreach (var (field, type, at) in ownFields)
        {
            if (at is not ParameterSyntax p) continue;
            if (EvalLeaf(type.TrimEnd('?')) is not { } leaf)
            {
                // A list: its own type too (its name, its description), its parts the list's (PartType, set below).
                if (CollectionOf(type.TrimEnd('?'), typesHere) is not null)
                    code.AppendLine($"    static readonly {Ev}.EvalType F_{field} = new() {{ Name = {Quote($"{rowKindName}.{Snake(field)}")}, Description = {Text(Attribute(p.AttributeLists, "Description")) ?? "\"\""}, Kind = {Ev}.EvalKind.Parts }};");
                continue;
            }
            var kindOf = leaf switch { "Int" => "Int", "Real" => "Real", "Bool" => "Bool", "Node" => "Address", "Expression" => "Opaque", _ => "Text" };
            var fdesc = Text(Attribute(p.AttributeLists, "Description")) ?? "\"\"";
            var fname = Quote($"{rowKindName}.{Snake(field)}");
            var extra = new List<string>();
            if (Attribute(p.AttributeLists, "Editable") is not null) extra.Add("Editable = true");
            if (leaf == "Node") extra.Add($"Read = {Ev}.ModelEval.ReadNode, Text = {Ev}.EvalTypes.Node.Text");
            if (leaf == "Expression") extra.Add($"Text = {Ev}.EvalTypes.Expression.Text");
            if (leaf == "Enum")
            {
                var e = Enums.ByName[LastName(type)];
                extra.Add($"Choices = {Ev}.ModelEval.Names({fname}, {fdesc}, {e}Names.All)");
                if (Enums.Labelled.Contains(LastName(type))) extra.Add($"ChoiceLabels = {e}Names.Labels");
            }
            if (leaf == "Node" && KindIds.Of.TryGetValue(LastName(type), out var idOf))
                extra.Add($"Choices = {Ev}.ModelEval.Every({idOf.Index})");
            code.AppendLine($"    static readonly {Ev}.EvalType F_{field} = new() {{ Name = {fname}, Description = {fdesc}, Kind = {Ev}.EvalKind.{kindOf}{string.Concat(extra.Select(x => ", " + x))} }};");
        }
    for (int i = 0; i < lists.Count; i++) code.AppendLine($"    static readonly {Ev}.EvalType L{i};");
    code.AppendLine($"\n    static {rootName}Eval()\n    {{");
    foreach (var rec in records)
    {
        var recDesc = Text(Attribute(rec.AttributeLists, "Description")) ?? "\"\"";
        code.AppendLine($"        T_{rec.Identifier.Text} = new() {{ Name = \"{Snake(rec.Identifier.Text)}\", Description = {recDesc}, Kind = {Ev}.EvalKind.Parts }};");
    }
    for (int i = 0; i < lists.Count; i++)
        code.AppendLine($"        L{i} = new() {{ Name = \"{Snake(LastName(lists[i]))}[]\", Kind = {Ev}.EvalKind.Parts, PartType = {TypeOf(lists[i])} }};");
    foreach (var rec in records)
    {
        var fs = FieldsOf(rec).Where(f => IsRead(f.Type, typesHere) && !kindNames.Contains(LastName(f.Type))).ToList();
        var r = $"T_{rec.Identifier.Text}";
        bool own = rowRecord && rec == root;
        string MemberType((string Name, string Type, SyntaxNode At) f) =>
            own && f.At is ParameterSyntax && (EvalLeaf(f.Type.TrimEnd('?')) is not null || CollectionOf(f.Type.TrimEnd('?'), typesHere) is not null) ? $"F_{f.Name}" : TypeOf(f.Type);
        code.AppendLine($"        {r}.Members = [{string.Join(", ", fs.Select(f => $"(\"{Snake(f.Name)}\", {MemberType(f)})"))}];");
        code.AppendLine($"        {r}.Fields = [{string.Join(", ", fs.Select(f => $"\"{Snake(f.Name)}\""))}];");
        // A field's name on screen: its [Label], else its key.
        var labelled = fs.Select((f, i) => (i, Label: f.At is ParameterSyntax lp ? Text(Attribute(lp.AttributeLists, "Label")) : null)).Where(x => x.Label is not null).ToList();
        code.AppendLine(labelled.Count == 0 ? $"        {r}.PartName = i => {r}.Members[i].Name;"
            : $"        {r}.PartName = i => i switch {{ {string.Concat(labelled.Select(x => $"{x.i} => {x.Label}, "))}_ => {r}.Members[i].Name }};");
    }
    if (rowRecord)
        foreach (var (field, type, at) in ownFields)
            if (at is ParameterSyntax && EvalLeaf(type.TrimEnd('?')) is null && CollectionOf(type.TrimEnd('?'), typesHere) is var (element, _))
                code.AppendLine($"        F_{field}.PartType = L{lists.IndexOf(element)}.PartType;");
    code.AppendLine($"        Type = T_{rootName};\n    }}");
    code.AppendLine($"\n    /// <summary>What is at <paramref name=\"path\"/> in <paramref name=\"root\"/>: a number, a text, a count; none for a path it has not.</summary>");
    code.AppendLine($"    public static {Ev}.Value Read(in {Qualified(root)} root, global::System.ReadOnlySpan<{Ev}.Step> path) => Read_{rootName}(root, path);");
    foreach (var rec in records)
    {
        var name = rec.Identifier.Text;
        code.AppendLine($$"""

                static {{Ev}}.Value Read_{{name}}(in {{Qualified(rec)}} v, global::System.ReadOnlySpan<{{Ev}}.Step> p)
                {
                    if (p.Length == 0 || p[0].Kind != {{Ev}}.StepKind.Member) return {{Ev}}.Value.None;
                    switch (p[0].At)
                    {
            """);
        var fs = FieldsOf(rec).Where(f => IsRead(f.Type, typesHere) && !kindNames.Contains(LastName(f.Type))).ToList();
        for (int m = 0; m < fs.Count; m++)
        {
            var (field, type, _) = fs[m];
            var t = type.TrimEnd('?');
            bool nullable = type.EndsWith('?') || !IsValueType(t, typesHere);
            if (EvalLeaf(t) is not null)
            {
                code.AppendLine($"            case {m}: return p.Length == 1 ? {LeafRead(t, $"v.{field}")} : {Ev}.Value.None;");
                continue;
            }
            if (CollectionOf(t, typesHere) is var (element, count))
            {
                var at = "c[(int)p[1].At]";
                var next = EvalLeaf(element) is not null ? $"p.Length == 2 ? {LeafRead(element, at)} : {Ev}.Value.None" : $"Read_{LastName(element)}({at}, p[2..])";
                code.AppendLine($$"""
                                case {{m}}:
                                {
                                    var c = v.{{field}};
                                    if ({{(nullable ? "c is null || " : "")}}p.Length < 2) return {{Ev}}.Value.None;
                                    if (p[1].Kind == {{Ev}}.StepKind.Count) return p.Length == 2 ? {{Ev}}.Value.Of((long)c.{{count}}) : {{Ev}}.Value.None;
                                    if (p[1].Kind != {{Ev}}.StepKind.Element || p[1].At >= c.{{count}}) return {{Ev}}.Value.None;
                                    return {{next}};
                                }
                    """);
                continue;
            }
            code.AppendLine(nullable
                ? $"            case {m}: return v.{field} is {{ }} r{m} ? Read_{LastName(t)}(r{m}, p[1..]) : {Ev}.Value.None;"
                : $"            case {m}: return Read_{LastName(t)}(v.{field}, p[1..]);");
        }
        code.AppendLine($"            default: return {Ev}.Value.None;\n        }}\n    }}");
    }
    if (keyTypes.Contains(rootName))
    {
        // The key from a Place's arguments, evaluated, in its fields' order: a number for a number, a bool for
        // a bool, a text for a text.
        var fs = FieldsOf(root);
        code.AppendLine($"\n    /// <summary>A {rootName} from a Place's arguments, one per field in order; false when they make none.</summary>");
        code.AppendLine($"    public static bool Pack(global::System.ReadOnlySpan<{Ev}.Value> args, out {rootName} key)\n    {{");
        code.AppendLine($"        key = default;\n        if (args.Length != {fs.Count}) return false;");
        for (int i = 0; i < fs.Count; i++)
        {
            var (field, type, _) = fs[i];
            var ok = EvalLeaf(type) switch { "Int" => "Int", "Real" => $"Int or {Ev}.EvalKind.Real", "Text" => "Text", _ => "Bool" };
            var get = EvalLeaf(type) switch { "Int" => $"({type})a{i}.AsInt", "Real" => $"({type})a{i}.AsReal", "Text" => $"a{i}.AsText", _ => $"a{i}.AsBool" };
            code.AppendLine($"        var a{i} = {Ev}.Eval.Load(args[{i}]);\n        if (a{i}.Kind is not ({Ev}.EvalKind.{ok})) return false;");
            fs[i] = (field, get, fs[i].At);
        }
        code.AppendLine($"        key = new {rootName} {{ {string.Join(", ", fs.Select(f => $"{f.Name} = {f.Type}"))} }};\n        return true;\n    }}");
    }
    code.AppendLine("}");
    // Each reachable record's field names, as the evaluator names them, for code that steps to one
    // without spelling it (VesselPathFields.Parent): a class of constants beside the type, not in it.
    foreach (var rec in records)
    {
        if (!fieldNamesWritten.Add(rec.Identifier.Text)) continue;
        code.AppendLine($"\n/// <summary>{rec.Identifier.Text}'s fields, by the names the evaluator reads them by.</summary>");
        code.AppendLine($"public static class {rec.Identifier.Text}Fields\n{{");
        foreach (var f in FieldsOf(rec).Where(f => IsRead(f.Type, typesHere) && !kindNames.Contains(LastName(f.Type)))) code.AppendLine($"    public const string {f.Name} = \"{Snake(f.Name)}\";");
        code.AppendLine("}");
    }
    if (ns is not null) code.AppendLine("}");
}

//- the Places' registrations: per class declaring Places, {Class}Places: each Place's name, and Add,
// which registers them all (Places.Add), each read as its data's reader along the path from what its
// query gives at the key.
foreach (var c in places.GroupBy(p => p.Class))
{
    var first = c.First();
    var ns = NamespaceOf(first.Query);
    // The layer's generation: its [PlaceGen] function of its state, given to each of its Places.
    var owner = (first.Query.Parent as TypeDeclarationSyntax)!;
    var genOf = owner.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => Attribute(m.AttributeLists, "PlaceGen") is not null);
    if (genOf is null && first.State is not null)
        Console.Error.WriteLine($"{Where(owner)}: warning CMDGEN31: {c.Key}'s Places read its state and it has no [PlaceGen]: their data is taken never to change, so nothing derived from it is ever made again.");
    var gen = genOf is null ? "" : $", gen: () => {c.Key}.{genOf.Identifier.Text}({(first.State is null ? "" : "state")})";
    if (ns is not null) code.AppendLine($"namespace {ns}\n{{");
    code.AppendLine($"\n/// <summary>{c.Key}'s Places, as its [Place]s declare them: their names, and their registration. cmdgen's.</summary>");
    code.AppendLine($"static class {c.Key}Places\n{{");
    foreach (var p in c) code.AppendLine($"    public const string {p.Query.Identifier.Text}Name = {p.Name};");
    code.AppendLine($"\n    /// <summary>Registers them in <paramref name=\"ctx\"/>, each reading {(first.State is null ? "through its query" : "the layer's state through its query")}.</summary>");
    code.AppendLine($"    public static void Add({Ev}.EvalContext ctx{(first.State is { } st ? $", {st} state" : "")})\n    {{");
    foreach (var p in c)
    {
        bool keyless = LastName(p.Key) == "NoKey";
        var k = keyless ? $"{Ev}.NoKey" : p.Key;
        var passed = string.Join(", ", new[] { first.State is null ? null : "state", keyless ? null : "key" }.Where(x => x is not null));
        var query = $"{c.Key}.{p.Query.Identifier.Text}({passed})";
        var data = LastName(p.Data);
        bool maybe = p.Query.ReturnType.ToString().EndsWith('?') || !IsValueType(p.Data, typesHere);
        var read = maybe ? $"{query} is {{ }} d ? {data}Eval.Read(d, path) : {Ev}.Value.None" : $"{data}Eval.Read({query}, path)";
        code.AppendLine($"        {Ev}.Places.Add<{k}>(ctx, {p.Name}, {p.Description}, {data}Eval.Type,");
        code.AppendLine($"            (in {k} key, global::System.ReadOnlySpan<{Ev}.Step> path) => {read}{(keyless ? "" : $", {LastName(p.Key)}Eval.Type, {LastName(p.Key)}Eval.Pack")}{gen});");
    }
    code.AppendLine("    }\n}");
    if (ns is not null) code.AppendLine("}");
}

//- the lenses: per lens of a [LensSet], a part of <Name>Lens, beside its hand-written drawers: its
// LensFunc, a typed call (MeshLens.Call(of)), a reader per argument (a Once one evaluated, `it` the
// value it is applied to; a PerPart one as written).
foreach (var (lensSet, ns, lenses) in lensSets)
{
    if (ns is not null) code.AppendLine($"namespace {ns}\n{{");
    foreach (var lens in lenses)
    {
        var name = lens.Identifier.Text;
        var ps = (lens.ParameterList?.Parameters ?? default).ToList();
        if (ps.Count == 0 || ps[0].Type?.ToString() is not ("Expr" or "global::Bibui.Commands.Expr"))
        {
            Console.Error.WriteLine($"{Where(lens)}: warning CMDGEN20: {lensSet}.{name} is a lens, and its first parameter is what it draws: an Expr.");
            continue;
        }
        var rows = new List<string>();
        var callParams = new List<string> { $"{Ev}.Expr of" };
        var callArgs = new List<string> { "of" };
        var readers = new List<string>();
        int once = 0, perPart = 0;
        foreach (var p in ps.Skip(1))
        {
            var pname = p.Identifier.Text;
            var type = p.Type!.ToString();
            bool repeats = type is "Expr[]", optional = type is "Expr?", per = Attribute(p.AttributeLists, "PerPart") is not null;
            if (type is not ("Expr" or "Expr?" or "Expr[]"))
            {
                Console.Error.WriteLine($"{Where(p)}: warning CMDGEN20: {lensSet}.{name}.{pname} is a lens's argument, kept as written: an Expr (Expr? when it may be left out, Expr[] for the rest).");
                continue;
            }
            var desc = Text(Attribute(p.AttributeLists, "Description")) ?? "\"\"";
            rows.Add($"new(\"{Snake(pname)}\", {Ev}.ArgWhen.{(per ? "PerPart" : "Once")}, {desc}{(repeats ? ", Repeats: true" : "")})");
            var local = "@" + char.ToLowerInvariant(pname[0]) + pname[1..];
            callParams.Add(repeats ? $"params {Ev}.Expr[] {local}" : optional ? $"{Ev}.Expr? {local} = null" : $"{Ev}.Expr {local}");
            callArgs.Add(repeats ? $".. {local}" : local);
            if (per)
                readers.Add(repeats
                    ? $"/// <summary>Its arguments {Snake(pname)}, as written.</summary>\n    public static {Ev}.Expr[] {pname}(in {Ev}.Value shown) => {Ev}.Lenses.Of(shown, Func) is {{ PerPart.Length: > {perPart} }} lens ? lens.PerPart[{perPart}..] : [];"
                    : $"/// <summary>Its argument {Snake(pname)}, as written (evaluated per part); null when left out.</summary>\n    public static {Ev}.Expr? {pname}(in {Ev}.Value shown) => {Ev}.Lenses.Of(shown, Func) is {{ PerPart.Length: > {perPart} }} lens ? lens.PerPart[{perPart}] : null;");
            else if (!repeats)
                readers.Add($"/// <summary>Its argument {Snake(pname)}, evaluated now, `it` the value it is applied to; none when left out.</summary>\n    public static {Ev}.Value {pname}(in {Ev}.Value shown, {Ev}.EvalContext ctx) => {Ev}.Lenses.Of(shown, Func) is {{ }} lens ? {Ev}.Lenses.Arg(lens, {once}, shown, ctx) : {Ev}.Value.None;");
            if (per) perPart++; else once++;
        }
        var lensDesc = Text(Attribute(lens.AttributeLists, "Description")) ?? "\"\"";
        code.AppendLine();
        code.AppendLine($"/// <summary>The <c>{Snake(name)}</c> lens, as <see cref=\"{lensSet}.{name}\"/> declares it: its function, a call of it, and its arguments read.</summary>");
        code.AppendLine($"static partial class {name}Lens");
        code.AppendLine("{");
        code.AppendLine($"    public static readonly {Ev}.LensFunc Func = new() {{ Name = \"{Snake(name)}\", Description = {lensDesc}, Params = [{string.Join(", ", rows)}] }};");
        code.AppendLine();
        code.AppendLine($"    /// <summary>A call of it over <paramref name=\"of\"/>, its arguments in order: <c>{Snake(name)}(of, ...)</c>.</summary>");
        code.AppendLine($"    public static {Ev}.Expr Call({string.Join(", ", callParams)}) => {Ev}.Lenses.Call(Func, [{string.Join(", ", callArgs)}]);");
        foreach (var r in readers) code.AppendLine("    " + r);
        code.AppendLine("}");
    }
    if (ns is not null) code.AppendLine("}");
}

//- the enums of the enum tables: a value per row, and its name (BodyNames), what a field of it is kept as.
foreach (var (enumName, ns, table, names, labels) in enumTables)
{
    if (ns is not null) code.AppendLine($"namespace {ns}\n{{");
    code.AppendLine();
    code.AppendLine($"/// <summary>The values of <see cref=\"{table}\"/>, a row each, in its order: its rows are indexed by it.</summary>");
    code.AppendLine($"public enum {enumName} {{ {string.Join(", ", names.Select(Pascal))} }}");
    code.AppendLine();
    code.AppendLine($"/// <summary>Each <see cref=\"{enumName}\"/>'s name: what a field of it is kept as, and picked from.</summary>");
    code.AppendLine($"public static class {enumName}Names");
    code.AppendLine("{");
    code.AppendLine($"    public static readonly string[] All = [{string.Join(", ", names.Select(Quote))}];");
    if (labels is not null) code.AppendLine($"    /// <summary>How a person reads each, in the same order: the table's Label column.</summary>\n    public static readonly string[] Labels = [{string.Join(", ", labels.Select(Quote))}];");
    code.AppendLine($"    public static string Name({enumName} value) => All[(int)value];");
    code.AppendLine($"    /// <summary>The value named <paramref name=\"name\"/>; the first for a name it has not.</summary>");
    code.AppendLine($"    public static {enumName} Of(string name) => global::System.Array.IndexOf(All, name) is var i and >= 0 ? ({enumName})i : default;");
    code.AppendLine("}");
    if (ns is not null) code.AppendLine("}");
}

var text = code.ToString();
if (!File.Exists(output) || File.ReadAllText(output) != text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, text);
}
return 0;

// A row kind's record type, and the id type its table is keyed by: its kind's id.
static string RowType(string set, string kind) => $"{set}.{kind}";
static string IdType(string kind) => KindIds.Of.ContainsKey(kind + "Id") ? kind + "Id" : "global::Bibui.Commands.NodeId";

// A row kind's part of its module: the default row a miss reads as (its declared defaults; a text's is ""),
// a new table (its rows read and written whole, a write a new version), the fields of two rows that differ,
// and a field by key, read and set: for code that holds only an id (the evaluator, an outline, node.set).
// A row's leaves at a path are its record's shape's reader ({Record}Eval.Read).
static void RowModule(StringBuilder code, string row, string tid, RecordDeclarationSyntax k, List<ParameterSyntax> fields, List<ParameterSyntax> declared, HashSet<string> kindNames)
{
    const string C = "global::Bibui.Commands";
    var name = k.Identifier.Text;
    var shape = row[(row.LastIndexOf('.') + 1)..] + "Eval";
    string Default(ParameterSyntax p)
    {
        var (value, type, _, _) = Field(p, kindNames);
        if (value == "None") return "default!";   // a child kind the record declares: the tree's, never in a row
        var fallback = Text(Attribute(p.AttributeLists, "DefaultValue"));
        if (fallback is not null && Enums.ByName.ContainsKey(TypeName(p))) fallback = Enums.Qualify(fallback);
        return fallback is not null ? $"({type})({fallback})" : type == "string" ? "\"\"" : "default!";   // a list, an expression: none until written
    }
    string Of(ParameterSyntax p, string access)
    {
        var (value, _, _, write) = Field(p, kindNames);
        return $"{C}.Value.{(value == "Object" ? "OfObject" : "Of")}({write.Replace("value", access)})";
    }
    code.AppendLine($$"""

            //- {{Snake(name)}}: what a node with no row reads as (the declared defaults), its table (by {{tid}}), the
            // fields of two rows that differ, and its fields by key
            public static readonly {{row}} {{name}}Default = new({{string.Join(", ", declared.Select(Default))}});
            static {{C}}.Table<{{row}}, {{tid}}> New{{name}}Table({{C}}.ModelClock clock) => new() { Clock = clock, Default = {{name}}Default };
            internal static {{C}}.Table<{{row}}, {{tid}}> {{name}}Table({{C}}.ModelStore t) => ({{C}}.Table<{{row}}, {{tid}}>)TablesOf(t)[{{name}}]!;
        """);
    code.AppendLine($"    static void {name}Diff(in {row} a, in {row} b, global::System.Collections.Generic.List<(string, {C}.Value, {C}.Value)> into)");
    code.AppendLine("    {");
    foreach (var p in fields)
    {
        var (_, type, _, _) = Field(p, kindNames);
        var f = p.Identifier.Text;
        code.AppendLine($"        if (!global::System.Collections.Generic.EqualityComparer<{type}>.Default.Equals(a.{f}, b.{f})) into.Add((\"{Snake(f)}\", {Of(p, $"a.{f}")}, {Of(p, $"b.{f}")}));");
    }
    code.AppendLine("    }");
    code.AppendLine();
    code.AppendLine($"    static {C}.Value {name}Field(in {row} r, string key) => key switch");
    code.AppendLine("    {");
    foreach (var p in fields) code.AppendLine($"        \"{Snake(p.Identifier.Text)}\" => {Of(p, $"r.{p.Identifier.Text}")},");
    code.AppendLine($"        _ => {C}.Value.None,");
    code.AppendLine("    };");
    code.AppendLine();
    code.AppendLine($"    static bool {name}With({C}.ModelStore t, in {row} r, string key, in {C}.Value v, out {row} with)");
    code.AppendLine("    {");
    code.AppendLine("        with = r;");
    code.AppendLine("        switch (key)");
    code.AppendLine("        {");
    foreach (var p in fields)
    {
        var (value, type, read, _) = Field(p, kindNames);
        // a Value of the field's kind, read as the field's type: an enum's, one of its names; a typed id's, a node of that kind
        var (check, get) = value switch
        {
            "Int" => ($"v.Kind == {C}.EvalKind.Int", "v.AsInt"),
            "Float" => ($"v.Kind is {C}.EvalKind.Real or {C}.EvalKind.Int", "v.AsReal"),
            "Bool" => ($"v.Kind == {C}.EvalKind.Bool", "v.AsBool"),
            "String" when Enums.ByName.ContainsKey(TypeName(p)) => ($"v.Kind == {C}.EvalKind.Text && global::System.Array.IndexOf({type}Names.All, v.AsText) >= 0", "v.AsText"),
            "String" => ($"v.Kind == {C}.EvalKind.Text", "v.AsText"),
            "Id" => ($"v.Type == {C}.EvalTypes.Node && !{type}.From(t, new {C}.NodeId(v.Id)).IsNone", $"{type}.From(t, new {C}.NodeId(v.Id))"),
            "Handle" => ($"v.Type == {C}.EvalTypes.Node", $"new {C}.NodeId(v.Id)"),
            "Expr" => ($"v.Type == {C}.EvalTypes.Expression", $"(v.Holder as {C}.Expr)"),
            _ => ($"v.Type == {C}.EvalTypes.Object", "v.Holder"),
        };
        code.AppendLine($"            case \"{Snake(p.Identifier.Text)}\": if (!({check})) return false; with = r with {{ {p.Identifier.Text} = {string.Format(read, get)}{(value is "Object" or "Expr" ? "!" : "")} }}; return true;");
    }
    code.AppendLine("            default: return false;");
    code.AppendLine("        }");
    code.AppendLine("    }");
}

// A type's name as code outside it writes it: its enclosing types first (Node.Case).
static string Qualified(TypeDeclarationSyntax t)
{
    var name = t.Identifier.Text;
    for (var p = t.Parent as TypeDeclarationSyntax; p is not null; p = p.Parent as TypeDeclarationSyntax) name = p.Identifier.Text + "." + name;
    return name;
}

// A row kind's table's name among the model's tables: Case's is Cases.
static string Plural(string name) => name.EndsWith('s') ? name : name.EndsWith('x') ? name + "es" : name.EndsWith('y') ? name[..^1] + "ies" : name + "s";

// A kind with fields is a row kind: its record is its row (Tables.cs), its fields a table's; the node in
// the tree is its id, kind and links (and its children).
bool IsRowKind(RecordDeclarationSyntax k) => (k.ParameterList?.Parameters ?? default).Any(p => IsRowField(k, p));

// A field of a row kind's row: a parameter that is a value (not a child kind, nor one made by an [In]).
bool IsRowField(RecordDeclarationSyntax k, ParameterSyntax p) =>
    !IsPlaced(placedBy, k.Identifier.Text, p) && Field(p, kindNames).Value != "None";

// A parameter cmdgen made from a child's [In]: a child, not a field of a row kind.
static bool IsPlaced(Dictionary<string, HashSet<string>> placedBy, string kind, ParameterSyntax p) =>
    placedBy.TryGetValue(kind, out var made) && made.Contains(p.Identifier.Text);

// A type's fields, as the evaluator reads them: a record's positional ones, then its public instance fields.
static List<(string Name, string Type, SyntaxNode At)> FieldsOf(TypeDeclarationSyntax t)
{
    var members = new List<(string Name, string Type, SyntaxNode At)>();
    if (t is RecordDeclarationSyntax { ParameterList: { } positional })
        foreach (var p in positional.Parameters) members.Add((p.Identifier.Text, p.Type!.ToString(), p));
    foreach (var f in t.Members.OfType<FieldDeclarationSyntax>())
    {
        if (!f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) || f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword))) continue;
        foreach (var v in f.Declaration.Variables) members.Add((v.Identifier.Text, f.Declaration.Type.ToString(), v));
    }
    return members;
}

// A leaf type's EvalTypes field (Int, Real, Bool, Text; a model node's id, Node; an expression held as
// a value, Expression; an enum table's enum, Enum: a text, its value's name); null for anything else.
static string? EvalLeaf(string t) => t switch
{
    "long" or "int" or "short" or "byte" or "uint" => "Int",
    "double" or "float" => "Real",
    "bool" => "Bool",
    "string" => "Text",
    "NodeId" or "global::Bibui.Commands.NodeId" or "Bibui.Commands.NodeId" => "Node",
    _ when KindIds.Of.ContainsKey(LastName(t)) => "Node",   // a {Kind}Id: its NodeId
    "Expr" or "global::Bibui.Commands.Expr" or "Bibui.Commands.Expr" => "Expression",
    _ when Enums.ByName.ContainsKey(LastName(t)) => "Enum",   // an enum table's: read as its value's name
    _ => null,
};

// A leaf read as a Value.
static string LeafRead(string t, string access) => EvalLeaf(t) switch
{
    "Int" => $"global::Bibui.Commands.Value.Of((long){access})",
    "Real" => $"global::Bibui.Commands.Value.Of((double){access})",
    "Node" when KindIds.Of.ContainsKey(LastName(t)) => $"global::Bibui.Commands.Value.Of({access}.Id)",
    "Bool" or "Node" or "Expression" => $"global::Bibui.Commands.Value.Of({access})",
    "Enum" => $"global::Bibui.Commands.Value.Of({Enums.ByName[LastName(t)]}Names.Name({access}))",
    _ => $"global::Bibui.Commands.Value.Of({access} ?? \"\")",
};

// A collection's element type and what counts it: an array (Length), a List/IReadOnlyList/IList (Count),
// or a type declared here with a Count or Length and an int indexer (its element, the indexer's type).
static (string Element, string Count)? CollectionOf(string t, Dictionary<string, TypeDeclarationSyntax> declared)
{
    if (t.EndsWith("[]")) return (t[..^2], "Length");
    if (t.IndexOf('<') is var lt and > 0 && t.EndsWith('>') && LastName(t[..lt]) is "List" or "IReadOnlyList" or "IList") return (t[(lt + 1)..^1], "Count");
    if (!declared.TryGetValue(LastName(t), out var d)) return null;
    var indexer = d.Members.OfType<IndexerDeclarationSyntax>().FirstOrDefault(i => i.ParameterList.Parameters is [{ Type: { } pt }] && pt.ToString() is "int" or "long");
    if (indexer is null) return null;
    var count = d.Members.SelectMany(m => m switch
    {
        FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.Text),
        PropertyDeclarationSyntax p => [p.Identifier.Text],
        _ => [],
    }).FirstOrDefault(n => n is "Count" or "Length");
    if (count is null) return null;
    var element = indexer.Type is RefTypeSyntax rt ? rt.Type.ToString() : indexer.Type.ToString();
    return (element, count);
}

// Whether a field of this type is read: a leaf, a collection, or a type declared here.
static bool IsRead(string type, Dictionary<string, TypeDeclarationSyntax> declared)
{
    var t = type.TrimEnd('?');
    return EvalLeaf(t) is not null || CollectionOf(t, declared) is not null || declared.ContainsKey(LastName(t));
}

// Whether a type is a value type (a struct, a leaf but a text), so never null.
static bool IsValueType(string t, Dictionary<string, TypeDeclarationSyntax> declared) =>
    EvalLeaf(t) is { } leaf ? leaf is not ("Text" or "Expression")
    : declared.TryGetValue(LastName(t), out var d) && (d is StructDeclarationSyntax || d is RecordDeclarationSyntax r && r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));

// A type's name without its qualifier and nullability: Tests.Queue.Job? is Job.
static string LastName(string type) => type.TrimEnd('?') is var t ? t[(t.LastIndexOf('.') + 1)..] : type;

static string? NamespaceOf(SyntaxNode node) =>
    node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

// The attribute [<name>] or [<name>Attribute] among these lists, by its simple or qualified name, or null.
static AttributeSyntax? Attribute(SyntaxList<AttributeListSyntax> lists, string name) =>
    lists.SelectMany(l => l.Attributes).FirstOrDefault(a => IsNamed(a, name));

static bool IsNamed(AttributeSyntax a, string name) =>
    (a.Name.ToString() is var full && full.IndexOf('<') is var lt and >= 0 ? full[..lt] : full) is var n
    && (n == name || n == name + "Attribute" || n.EndsWith("." + name) || n.EndsWith("." + name + "Attribute"));

// A key's value kind (a ValueKind name, None for a child kind), its C# type, how its getter turns the
// stored value into that type ({0} is the read), and how the setter's value goes in.
static (string Value, string Type, string Read, string Write) Field(ParameterSyntax p, HashSet<string> kinds)
{
    var t = TypeName(p);
    return t switch
    {
        _ when kinds.Contains(t) => ("None", t, "{0}", "value"),
        // A node of a kind, by its id type (ItemId): checked as it is set, kept as the typed id.
        _ when KindIds.Of.ContainsKey(t) => ("Id", t, "{0}", "value.Id"),
        // A value of an enum table's enum (Body): kept as its name, read back as the value.
        _ when Enums.ByName.TryGetValue(t, out var e) => ("String", e, e + "Names.Of({0})", e + "Names.Name(value)"),
        "long" or "Int64" => ("Int", "long", "{0}", "value"),
        "int" or "Int32" => ("Int", "int", "(int){0}", "(long)value"),
        "double" or "Double" => ("Float", "double", "{0}", "value"),
        "float" or "Single" => ("Float", "float", "(float){0}", "(double)value"),
        "bool" or "Boolean" => ("Bool", "bool", "{0}", "value"),
        "string" or "String" => ("String", "string", "{0}", "value"),
        // A node, by its id, of any kind (a typed id, CaseId, says which).
        "NodeId" => ("Handle", "global::Bibui.Commands.NodeId", "{0}", "value"),
        // An expression, kept as written (a view's lens, a card's query): evaluated by what reads it.
        "Expr" => ("Expr", "global::Bibui.Commands.Expr", "{0}", "value"),
        // Anything else: an object the node owns and that is changed in place (a text buffer).
        _ => ("Object", t, "({0} as " + t + ")", "value"),
    };
}

// A parameter's type as written, without a trailing '?' and a qualifier: Picture? is Picture.
static string TypeName(ParameterSyntax p)
{
    var t = (p.Type?.ToString() ?? "").TrimEnd('?');
    return t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
}


// Where a command's argument comes from in a context: named as its kind and of its kind's id type
// (CaseId Case), its kind's slot (c.Of(NodeKinds.Case)), the slot's node taken as that kind's (the code
// that pushed it pushed a node of its kind); null for any other, which the context does not hold.
static (string Slot, string Value)? ContextSlot(ParameterSyntax p, HashSet<string> kinds, string kindsType)
{
    var name = p.Identifier.Text;
    var kind = ParamKind(p)?.Trim('"');
    if (kind is null) return null;
    var slot = kinds.FirstOrDefault(k => Snake(k) == kind);
    if (slot is null || slot != name) return null;
    var at = $"{kindsType}.{slot}";
    return (at, $"{slot}Id.Known(c.Of({at}))");
}

// The node kind of a parameter, as a C# string literal ("panel"): its type's, a {Kind}Id; or null.
static string? ParamKind(ParameterSyntax p) =>
    KindIds.Of.TryGetValue(TypeName(p), out var id) ? Quote(Snake(id.Kind)) : null;

// An attribute's first argument as written (a string literal, a constant), to copy into the table; or null.
static string? Text(AttributeSyntax? attribute) => attribute?.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString();

static string Where(SyntaxNode node)
{
    var line = node.GetLocation().GetLineSpan();
    return $"{line.Path}({line.StartLinePosition.Line + 1})";
}

// Counter_IncrementBy is "counter.increment_by": a part per underscore, each in snake case.
static string WireName(string type) => string.Join('.', type.Split('_').Select(Snake));

static string Snake(string part)
{
    var s = new StringBuilder();
    for (int i = 0; i < part.Length; i++)
    {
        if (char.IsUpper(part[i]) && i > 0) s.Append('_');
        s.Append(char.ToLowerInvariant(part[i]));
    }
    return s.ToString();
}

// An enum table row's name as the enum's value: "card" is Card, "two_words" TwoWords.
static string Pascal(string name) => string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

// Counter_IncrementBy is CounterIncrementBy.
static string MethodName(string type) => type.Replace("_", "");

// A command's label when it has no [Label]: Counter_IncrementBy is "Counter increment by".
static string Label(string type)
{
    var words = WireName(type).Replace('.', ' ').Replace('_', ' ');
    return char.ToUpperInvariant(words[0]) + words[1..];
}

static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

// A positional parameter as a method's argument: Value is value, and a keyword is escaped.
static string Argument(ParameterSyntax p)
{
    var name = p.Identifier.Text;
    name = char.ToLowerInvariant(name[0]) + name[1..];
    return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}

// The enums the enum tables declare, by name: what a field of one is (Field), qualified as written out
// (global::Body), so a member of the same name (BoardNode.Body) does not hide it.
// The id types cmdgen writes: CaseId → (Case, its kind's index as code writes it).
static class KindIds
{
    public static readonly Dictionary<string, (string Kind, string Index)> Of = new();
}

static class Enums
{
    public static readonly Dictionary<string, string> ByName = new();
    public static readonly HashSet<string> Labelled = new();   // those whose table has a Label column

    /// <summary>A value of one as written (Body.Card), qualified: global::Body.Card.</summary>
    public static string Qualify(string value) =>
        value.IndexOf('.') is var dot and > 0 && ByName.TryGetValue(value[..dot], out var full) ? full + value[dot..] : value;
}
