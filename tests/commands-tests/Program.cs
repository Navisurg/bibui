// Bibui.Commands as an app uses it: the command set below is run through cmdgen before this
// compiles, and the tests read what it wrote, and run a Model of it; and the model's tree.
//
//   dotnet run --project tests/commands-tests

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bibui.Commands;

int passed = 0, failed = 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}");
        Console.WriteLine($"         {ex.Message}");
        failed++;
    }
}

// The shelf's rows, set through their tables: what a command does (a row kind's fields are its table's).
static void SetRule(ModelStore t, Expr rule)
{
    var root = RootId.At(t, Nodes.Root);
    root.Modify(t, root.RowFromId(t) with { Rule = rule });
}
static void SetName(ModelStore t, int item, string name)
{
    var it = ItemId.At(t, item);
    it.Modify(t, it.RowFromId(t) with { Name = name });
}

// The shelf's lists: found, or made the first time.
static int ListsOrNew(ModelStore t) => Nodes.Child(t, Nodes.Root, ShelfKinds.Lists) is var c and not 0 ? c : Nodes.New(t, Nodes.Root, ShelfKinds.Lists);

static void Eq<T>(T expected, T actual, string message)
{
    if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
}

Run("Model: commands run in the order pushed, and follow-ups in the same Run, after their batch", () =>
{
    var log = new Log();
    var model = new Model((tree, cmd, m) =>
    {
        log.Ran.Add(TestCmdOps.Of((TestCmd)cmd).Wire);
        if (cmd is TestCmd.Counter_Reset) m.Cmds.CounterSet(0);   // a follow-up
    });
    model.Cmds.CounterIncrementBy(2);
    model.Cmds.CounterReset();
    model.Cmds.CounterIncrementBy(3);
    Eq(true, model.HasPending, "pending before Run");
    model.Run();
    Eq("counter.increment_by counter.reset counter.increment_by counter.set", string.Join(' ', log.Ran), "the order run");
    Eq(false, model.HasPending, "nothing pending after");
});

Run("Model: a command that only answers publishes nothing, its output kept until taken", () =>
{
    var model = new Model((tree, cmd, push) => push.Output($"ran {TestCmdOps.Of((TestCmd)cmd).Wire}\n"));
    model.Cmds.CounterReset();
    model.Cmds.CounterIncrementBy(2);
    model.Run();
    Eq(0L, model.Gen, "nothing written: no generation, nothing in the log");
    Eq("ran counter.reset\nran counter.increment_by\n", string.Concat(model.Outputs), "what each answered, in the order they ran");
    model.Outputs.Clear();
    model.Cmds.CounterReset();
    model.Run();
    Eq(1, model.Outputs.Count, "taken, then only the next run's");
});

Run("Model: a push wakes the loop, once per push", () =>
{
    int wakes = 0;
    var model = new Model((ModelStore _, Cmd _, Pushes _) => { }, wake: () => wakes++);
    model.Cmds.CounterReset();
    model.Cmds.CounterReset();
    Eq(2, wakes, "wakes");
});

// A count, as the tests' apply keeps it: a row of the root in a table of counts (a hand-made table, as a
// node set's are generated): increment adds, set sets, reset pushes a set of 0 (a follow-up), peek reads
// and changes nothing.
static Table<CountRow, NodeId> Counts(ModelStore t)
{
    foreach (var table in t.Tables) if (table is Table<CountRow, NodeId> counts) return counts;
    var made = new Table<CountRow, NodeId> { Clock = t.Clock, Default = new(0) };   // the root's row, by its NodeId: a row any node may have
    t.Tables = [.. t.Tables, made];
    return made;
}

// A row as generation gen saw it (the default when its ring no longer reaches gen).
static T RowAt<T, TId>(Table<T, TId> table, TId id, long gen) where TId : struct { table.FromIdAt(id, gen, out var row); return row; }

static void Count(ModelStore t, Cmd cmd, Pushes m)
{
    var root = Nodes.IdOf(t, Nodes.Root);
    long n = Counts(t).FromId(root).N;
    switch (cmd)
    {
        case TestCmd.Counter_IncrementBy(var amount): Counts(t).SetRow(root, new(n + amount)); break;
        case TestCmd.Counter_Set(var value): Counts(t).SetRow(root, new(value)); break;
        case TestCmd.Counter_Reset: m.Cmds.CounterSet(0); break;
        case TestCmd.Counter_Peek or TestCmd.Counter_Point: break;
    }
}

static long CountOf(ModelStore t) => Counts(t).FromId(Nodes.IdOf(t, Nodes.Root)).N;

Run("Model: a command that changes the model publishes one generation; one that changes nothing publishes none", () =>
{
    var model = new Model(Count);
    model.Cmds.CounterIncrementBy(2);
    model.Cmds.CounterPeek();
    model.Cmds.CounterIncrementBy(3);
    model.Run();
    Eq(2L, model.Gen, "two generations");
    Eq(true, model.TryEntry(2, out var second), "the second in the log");
    Eq(new TestCmd.Counter_IncrementBy(3), second.Cmd, "made by the second increment");
    Eq(5L, CountOf(model.Tree), "the model now");
    model.Cmds.CounterSet(5);   // the same value: nothing changes
    model.Run();
    Eq(2L, model.Gen, "no generation for a set to the same value");
});

Run("Model: a follow-up remembers the generation that pushed it; one pushed by a command that changed nothing inherits its cause", () =>
{
    var model = new Model(Count);
    model.Cmds.CounterIncrementBy(4);
    model.Run();
    model.Cmds.CounterReset();   // changes nothing itself, pushes Set(0)
    model.Run();
    Eq(2L, model.Gen, "the set published");
    model.TryEntry(2, out var set);
    Eq(new TestCmd.Counter_Set(0), set.Cmd, "the follow-up made generation 2");
    Eq(0L, set.CauseGen, "its cause, reset, changed nothing and came from outside");

    var chained = new Model((t, cmd, m) =>
    {
        Count(t, cmd, m);
        if (cmd is TestCmd.Counter_IncrementBy(1)) m.Cmds.CounterIncrementBy(10);   // a follow-up of a command that changed the model
    });
    chained.Cmds.CounterIncrementBy(1);
    chained.Run();
    chained.TryEntry(2, out var follow);
    Eq(1L, follow.CauseGen, "pushed by generation 1");
    Eq(11L, CountOf(chained.Tree), "both ran");
});

Run("Model: an old generation's rows read as they were after later commands", () =>
{
    var model = new Model(Count);
    model.Cmds.CounterIncrementBy(1);
    model.Cmds.CounterIncrementBy(1);
    model.Cmds.CounterIncrementBy(1);
    model.Run();
    var root = Nodes.IdOf(model.Tree, Nodes.Root);
    Eq(1L, RowAt(Counts(model.Tree), root, 1).N, "generation 1");
    Eq(2L, RowAt(Counts(model.Tree), root, 2).N, "generation 2");
    Eq(3L, RowAt(Counts(model.Tree), root, 3).N, "generation 3");
    Eq(3L, CountOf(model.Tree), "and now");
});

Run("Model: the log keeps the last generations, and drops the oldest; a row's own history is its ring's", () =>
{
    var model = new Model(Count, logLength: 4);
    for (int i = 0; i < 6; i++) model.Cmds.CounterIncrementBy(1);
    model.Run();
    var root = Nodes.IdOf(model.Tree, Nodes.Root);
    Eq(6L, model.Gen, "six generations");
    Eq(3L, model.OldestGen, "the log holds 3 to 6");
    Eq(false, model.TryEntry(2, out _), "generation 2 is gone");
    Eq(3L, RowAt(Counts(model.Tree), root, 3).N, "generation 3's row");
    Eq(2L, RowAt(Counts(model.Tree), root, 2).N, "and generation 2's: a row keeps its last versions whatever the log holds");
    Eq(1, Counts(model.Tree).Count, "one row");
});

Run("NodeId: written #1043, read back (with or without its #), and in a command's JSON as that string", () =>
{
    var id = new NodeId(1043);
    Eq("#1043", id.ToString(), "its text");
    Eq(true, NodeId.TryParse("#1043", out var back) && back == id, "read back");
    Eq(true, NodeId.TryParse("1043", out back) && back == id, "and without its #");
    Eq(false, NodeId.TryParse("23#1043", out _), "a slot and an id: not an id");
    Eq(false, NodeId.TryParse("a7", out _), "not a number");
});

Run("Place generations: the model's is its generation, moved by a command that changes it; a Place given none never moves; an address's is its Place's", () =>
{
    // An apply that adds an item for Counter_IncrementBy, and changes nothing for Counter_Peek.
    var model = new Model((t, cmd, _) => { if (cmd is TestCmd.Counter_IncrementBy) ItemId.New(t, ListsOrNew(t)); }, tree: ShelfKinds.NewTree());
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, ModelEval.MakeEvalTypes(ShelfKinds.Schema), () => model.Tree);
    Places.AddListing(ctx);
    PlaceInfo Named(string name) => ctx.Places.Find(p => p.Name == name)!;
    long gen = Named("model").Gen();
    model.Do(new TestCmd.Counter_IncrementBy(1));
    Eq(true, Named("model").Gen() > gen, "a command that changed the tree: moved");
    gen = Named("model").Gen();
    model.Do(new TestCmd.Counter_Peek());
    Eq(gen, Named("model").Gen(), "a command that changed nothing: not moved");
    Eval.Run(Exprs.Name("root").Get("lists").Get("item").At(0).Get("name"), ctx);
    Eq(gen, Named("model").Gen(), "read: not moved");
    Eq(0L, Named(Places.ListingName).Gen(), "places, given none: never");
    Eq(gen, Addresses.Gen(ctx, Eval.Run(Exprs.Name("root"), ctx).Address), "an address's: its Place's");
});

Run("rows: a value as the expressions it is given, a part each, it the value, each seen through its own lens; stepped by name, the value's own", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    SetName(t, Nodes.New(t, lists, ShelfKinds.Item), "a");
    var types = ModelEval.MakeEvalTypes(ShelfKinds.Schema);
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, types, () => t);
    var lenses = new LensTable();
    foreach (var lens in new[] { Lenses.Text, Lenses.Rows }) ctx.Add(lens.Name, new Value(lenses.Callable, 0, 0, 0, lens));
    var item = Exprs.Name("root").Get("lists").Get("item").At(0);

    var seen = Eval.Run(Exprs.Call("rows", item, Exprs.It.Get("name"), Exprs.Call("text", Exprs.It.Get("weight"))), ctx);
    Eq("rows", seen.Type?.Lens?.Func.Name, "seen through rows");
    Eq(2L, Eval.Count(seen), "a part per expression past it");
    Eq("\"a\"", Exprs.ValueText(TextView.Bare(Eval.At(seen, 0, ctx))), "a part: its expression, it the value");
    Eq("text", Eval.At(seen, 1, ctx).Type?.Lens?.Func.Name, "each seen through its own lens");
    Eq("\"a\"", Exprs.ValueText(TextView.Bare(Eval.Member(seen, "name", ctx))), "by name: the value's own member");
});

Run("Own view rules: a node's own rule is a view_rule child of it; it wins over its kind's for that node only; built into the context each frame", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    var (a, b) = (ItemId.New(t, lists), ItemId.New(t, lists));
    var types = ModelEval.MakeEvalTypes(ShelfKinds.Schema);
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, types, () => t);
    var lenses = new LensTable();
    ctx.Add(Lenses.Text.Name, new Value(lenses.Callable, 0, 0, 0, Lenses.Text));
    var items = Exprs.Name("root").Get("lists").Get("item");

    ModelEval.SetViewRuleForNode(t, null, a, Exprs.Call("text", Exprs.It));   // what view_rule.set does
    var rule = ModelEval.ViewRuleFromNode(t, a);
    Eq(true, !rule.IsNone && Nodes.At(t, Nodes.SlotOf(t, rule)).Parent == a.SlotIn(t), "a view_rule child of the node");
    ModelEval.FillViewRulesFromModel(ctx, types, t);
    Eq("text", Eval.Run(items.At(0), ctx).Type?.Lens?.Func.Name, "the node: seen through its own rule");
    Eq(null, Eval.Run(items.At(1), ctx).Type?.Lens, "another of its kind: not");
    Eq(null, Eval.Run(items, ctx).Type?.Lens, "nor the set holding it");

    ModelEval.SetViewRuleForNode(t, null, a, null);
    Eq(true, ModelEval.ViewRuleFromNode(t, a).IsNone, "released: none");
    ModelEval.FillViewRulesFromModel(ctx, types, t);
    Eq(null, Eval.Run(items.At(0), ctx).Type?.Lens, "removed: its kind's again (none here)");
});

Run("Typed ids by node: a node's children of a kind (First, Next; none past the last), a typed id where a NodeId is taken, a release by id total", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    var listsId = Nodes.IdOf(t, lists);
    var (a, b) = (ItemId.New(t, lists), ItemId.New(t, lists));
    var seen = new List<ItemId>();
    for (var item = ItemId.First(t, listsId); !item.IsNone; item = item.Next(t)) seen.Add(item);
    Eq("2 a b", $"{seen.Count} {(seen[0] == a ? "a" : "?")} {(seen[1] == b ? "b" : "?")}", "its items, in order, each its typed id");
    Eq(a, ItemId.First(t, listsId), "the first");
    NodeId asNode = a;
    Eq(a.Id, asNode, "a typed id is a NodeId as it is");
    Nodes.Release(t, a);
    Nodes.Release(t, a);   // gone: nothing
    Eq(b, ItemId.First(t, listsId), "the first, once a is released");
    Eq(true, ItemId.First(t, new NodeId(9999)).IsNone && b.Next(t).IsNone && a.Next(t).IsNone, "none: under a node gone, past the last, after one released");
});

Run("Context: a reader monad: code reads the top; a scope sets slots over it (local), and the top is as it was when the scope ends", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    var (a, b) = (ItemId.New(t, lists), ItemId.New(t, lists));
    var ctx = new ContextStack();
    Eq(true, ctx.Top.Of(ShelfKinds.Item).IsNone && ctx.Top.View.IsNone, "empty to begin with");
    using (ctx.Push(ShelfKinds.Lists, Nodes.IdOf(t, lists)))
    {
        using (ctx.Push(ShelfKinds.Item, a))
        {
            Eq((a.Id, Nodes.IdOf(t, lists)), (ctx.Top.Of(ShelfKinds.Item), ctx.Top.Of(ShelfKinds.Lists)), "inner: its own slot, and the outer's");
            using (ctx.Push(ShelfKinds.Item, b))
                Eq(b.Id, ctx.Top.Of(ShelfKinds.Item), "a nearer scope's node shadows the outer's");
            Eq(a.Id, ctx.Top.Of(ShelfKinds.Item), "and the outer's is back, nothing restored by hand");
            using (ctx.PushView(a)) Eq(a.Id, ctx.Top.View, "the view: a slot like any");
        }
        Eq(true, ctx.Top.Of(ShelfKinds.Item).IsNone, "popped: as it was");
    }
    int Deep(int n) { using var _ = ctx.Push(ShelfKinds.Item, n % 2 == 0 ? a : b); return n == 0 ? (ctx.Top.Of(ShelfKinds.Item) == a.Id ? 1 : 0) : Deep(n - 1); }
    Eq(1, Deep(40), "40 deep: past its first block, grown, each frame its own");
    Eq(true, ctx.Top.Of(ShelfKinds.Item).IsNone, "every frame popped");

    // Several slots in one scope; a context kept after its scope, good until the frame's Clear.
    Context kept;
    using (ctx.Push().Set(ShelfKinds.Item, a).View(b)) kept = ctx.Top;
    Eq((a.Id, b.Id), (kept.Of(ShelfKinds.Item), kept.View), "two slots, one scope; read after it ended");
    Eq(true, ctx.Top.View.IsNone, "the top: as it was");
    ctx.Clear();
    Eq(true, ctx.Top.Of(ShelfKinds.Item).IsNone, "cleared: a new frame begins empty");
});

Run("Typed ids: only a check makes one (From, At, New); a command's JSON reads one against the tree, refusing another kind's", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    var item = ItemId.New(t, lists);
    Eq(ShelfKinds.Item, Nodes.KindOf(t, item.Id), "New: a node of its kind");
    Eq(item, ItemId.From(t, item.Id), "From: an item's id");
    Eq(true, ItemId.From(t, Nodes.IdOf(t, lists)).IsNone, "From: another kind's id, none");
    Eq(true, ItemId.At(t, lists).IsNone, "At: another kind's slot, none");
    Eq(true, default(ItemId).Id.IsNone, "default: none");
    item.Modify(t, item.RowFromId(t) with { Name = "a" });
    Eq("a", item.RowFromId(t).Name, "its row, through its id");
    Eq(("a", 1.0), (item.Name(t), item.Weight(t)), "a field, through its getter: the newest row's");

    var options = new JsonSerializerOptions(Json.Default.Options);
    foreach (var converter in ShelfIdsJson.For(() => t)) options.Converters.Add(converter);
    var json = new Json(options);
    var text = JsonSerializer.Serialize<TestCmd>(new TestCmd.Counter_Point(item), json.TestCmd);
    Eq($"{{\"$cmd\":\"counter.point\",\"At\":\"{item.Id}\"}}", text, "in a command's JSON: its NodeId's text");
    Eq(new TestCmd.Counter_Point(item), JsonSerializer.Deserialize(text, json.TestCmd), "read back from it");
    var wrong = $"{{\"$cmd\":\"counter.point\",\"At\":\"{Nodes.IdOf(t, lists)}\"}}";
    Eq(true, Refused(wrong), "another kind's id: refused as it is read");

    Nodes.Release(t, item.SlotIn(t));
    Eq(true, Refused(text), "a released item's id: refused too");
    Eq("", item.RowFromId(t).Name, "kept after: its row reads as the default");
    item.Modify(t, item.RowFromId(t) with { Name = "b" });
    Eq("", item.RowFromId(t).Name, "and a write is refused: still the default");

    bool Refused(string cmd)
    {
        try { JsonSerializer.Deserialize(cmd, json.TestCmd); return false; }
        catch (JsonException) { return true; }
    }
});

// The children's keys of a node, in order: what a test compares.
static string Kids(ModelStore t, int parent)
{
    var ctx = new List<string>();
    for (int c = Nodes.At(t, parent).First; c != 0; c = Nodes.At(t, c).Next) ctx.Add(K.Name(Nodes.At(t, c).Kind));
    return string.Join(' ', ctx);
}

Run("ModelStore: new nodes are their parent's last child; a node is its id, kind and links", () =>
{
    var t = Nodes.NewTree();
    int a = Nodes.New(t, Nodes.Root, K.A), b = Nodes.New(t, Nodes.Root, K.B);
    Eq("a b", Kids(t, Nodes.Root), "children in order");
    Eq(Nodes.Root, Nodes.At(t, a).Parent, "its parent");
    Eq(b, Nodes.SlotOf(t, Nodes.IdOf(t, b)), "found by its id");
    Eq(K.B, Nodes.KindOf(t, Nodes.IdOf(t, b)), "its kind, by its id");
    Eq(0, Nodes.SlotOf(t, new NodeId(999)), "an id never given: none");
});

Run("Value: a row's field read by key is a Value, as the evaluator's: a number, a text, a node by its id, an expression", () =>
{
    var id = new NodeId(7);
    Eq(-7L, Value.Of(-7).AsInt, "an int");
    Eq(0.55, Value.Of(0.55).AsReal, "a real");
    Eq(true, Value.Of(true).AsBool, "a bool");
    Eq("#5E5E66", Value.Of("#5E5E66").AsText, "a text");
    Eq(true, Value.Of(id) is { Type: var node, Id: 7 } && node == EvalTypes.Node, "a node, by its id");
    Eq("$7", Exprs.ValueText(Value.Of(id)), "printed as an address");
    Eq("", Value.Of(-7).AsText, "an int read as a text: none");
});

Run("ModelStore: a handle resolves to nil once its node is released, never to the node its slot holds next", () =>
{
    var t = Nodes.NewTree();
    int panel = Nodes.New(t, Nodes.Root, K.Panel);
    var focused = Nodes.IdOf(t, panel);
    Eq(panel, Nodes.SlotOf(t, focused), "the panel");
    Nodes.Release(t, panel);
    Nodes.New(t, Nodes.Root, K.Panel);   // takes the released slot
    Eq(0, Nodes.SlotOf(t, focused), "nothing, not the new panel");
    Eq(false, Nodes.Holds(t, focused), "the tree holds it no more");
});

Run("ModelStore: unlink and insert move a node, its children with it", () =>
{
    var t = Nodes.NewTree();
    int a = Nodes.New(t, Nodes.Root, K.A), b = Nodes.New(t, Nodes.Root, K.B), c = Nodes.New(t, Nodes.Root, K.C);
    Nodes.New(t, c, K.X);
    Nodes.Unlink(t, c);
    Eq("a b", Kids(t, Nodes.Root), "c out");
    Nodes.InsertChild(t, Nodes.Root, 0, c);
    Eq("c a b", Kids(t, Nodes.Root), "c first");
    Nodes.Unlink(t, a);
    Nodes.InsertChild(t, Nodes.Root, b, a);
    Eq("c b a", Kids(t, Nodes.Root), "a after b, last");
    Eq(a, Nodes.At(t, Nodes.Root).Last, "the parent's last");
    Eq("x", Kids(t, c), "c kept its child");
});

Run("ModelStore: a released slot is reused with a new id, and old handles resolve to nil", () =>
{
    var t = Nodes.NewTree();
    int panel = Nodes.New(t, Nodes.Root, K.Panel);
    var handle = Nodes.IdOf(t, panel);
    int count = t.Tree.Count;
    Nodes.Release(t, panel);
    Eq(0, Nodes.SlotOf(t, handle), "the released node's handle");
    Eq("", Kids(t, Nodes.Root), "gone from its parent");

    int again = Nodes.New(t, Nodes.Root, K.Panel);   // takes the released slot
    Eq(count, t.Tree.Count, "no fresh slot taken");
    var reused = Nodes.IdOf(t, again);
    Eq(true, reused.Value > handle.Value, "a new, larger id");
    Eq(0, Nodes.SlotOf(t, handle), "the old handle still resolves to nil, whatever its slot holds now");
    Eq(again, Nodes.SlotOf(t, reused), "the new handle resolves");
});

Run("ModelStore: what each generation did to the shape is kept (added, removed, moved), the top of a subtree once, and dropped as the log lets it go", () =>
{
    var t = Nodes.NewTree(1);
    int a = Nodes.New(t, Nodes.Root, K.A);
    Nodes.New(t, a, K.Under);
    t.Clock.Gen = 2;
    int b = Nodes.New(t, Nodes.Root, K.B);
    Nodes.Unlink(t, a);
    Nodes.InsertChild(t, Nodes.Root, b, a);
    t.Clock.Gen = 3;
    Nodes.Release(t, a);
    string Edits() => string.Join(" ", t.Edits.Skip(t.EditsRetired).Select(e => $"{e.Gen}{e.Op switch { EditOp.Add => "+", EditOp.Remove => "-", _ => ">" }}{K.Name(e.Kind)}"));
    Eq("1+a 1+under 2+b 2>a 3-a", Edits(), "each edit, in its generation; the release of a said once, for its top");
    Nodes.Retire(t, 2);
    Eq("2+b 2>a 3-a", Edits(), "the edits of a generation the log no longer holds: dropped");
    Eq(Kinds.Root, t.Edits[^1].ParentKind, "a removed node's parent, kept: the node is gone from the tree");
});

Run("ModelStore: releasing a subtree frees every node in it", () =>
{
    var t = Nodes.NewTree();
    int a = Nodes.New(t, Nodes.Root, K.A);
    int b = Nodes.New(t, a, K.B);
    Nodes.New(t, b, K.C);
    Nodes.New(t, b, K.D);
    Nodes.New(t, a, K.E);
    int keep = Nodes.New(t, Nodes.Root, K.Keep);
    Nodes.Release(t, a);
    int free = 0;
    for (int s = t.Tree.FreeFirst; s != 0; s = Nodes.At(t, s).Next) free++;
    Eq(5, free, "a, b, c, d and e on the free list");
    Eq("keep", Kids(t, Nodes.Root), "its sibling stays");
    Eq(keep, Nodes.At(t, Nodes.Root).First, "and is first");
});

Run("Schema: a handle argument's [Kind] is in the table, and kinds are found by name", () =>
{
    Eq("item", TestCmdOps.ByWire("counter.point")!.Arg("At")!.Kind, "the argument's kind, by name");
    Eq(null, TestCmdOps.ByWire("counter.increment_by")!.Arg("Amount")!.Kind, "a number has none");

    var schema = ShelfKinds.Schema;
    Eq(ShelfKinds.Item, Schemas.Named(schema, "item")!.Index, "a kind, by name: its index");
    Eq("item", Schemas.Of(schema, ShelfKinds.Item)!.Name, "and by index: its name");
    Eq("What it is called.", Schemas.Of(schema, ShelfKinds.Item)!.Shape!.Members![0].Type.Description, "its fields: its row's shape, each its own type");
    Eq(null, Schemas.Named(schema, "line"), "a kind not described");
    Eq(Kinds.ViewRule, Schemas.Named(schema, Schemas.ViewRuleKey)!.Index, "the library's kinds beside the app's");
});

Run("Nodes.HasKind: an id is of a kind only while its node is live and of that kind", () =>
{
    var t = Nodes.NewTree();
    int point = Nodes.New(t, Nodes.Root, K.Point);
    int x = Nodes.New(t, point, K.X);
    Eq(true, Nodes.HasKind(t, Nodes.IdOf(t, point), K.Point), "a point");
    Eq(false, Nodes.HasKind(t, Nodes.IdOf(t, x), K.Point), "its x's id is not a point");
    Eq(true, Nodes.HasKind(t, Nodes.IdOf(t, x), K.X), "an x");
    var gone = Nodes.IdOf(t, point);
    Nodes.Release(t, point);
    Eq(false, Nodes.HasKind(t, gone, K.Point), "a released point");
});

Run("Views: one state per node, kept while asked for, dropped when not, or when its node is gone", () =>
{
    var t = Nodes.NewTree();
    int a = Nodes.New(t, Nodes.Root, K.A), b = Nodes.New(t, Nodes.Root, K.B);
    var idA = Nodes.IdOf(t, a);
    var views = new ViewStates();
    var va = Views.From(views, Nodes.IdOf(t, a), frame: 1);
    Views.User<List<int>>(va).Add(7);
    Eq(7, Views.User<List<int>>(Views.From(views, Nodes.IdOf(t, a), frame: 2))[0], "the same state, and its data, the next frame");
    Views.From(views, Nodes.IdOf(t, b), frame: 2);
    Views.Evict(views, t, frame: 2);
    Eq(2, views.ByNode.Count, "both asked for in frame 2: kept");
    Views.From(views, Nodes.IdOf(t, a), frame: 3);
    Views.Evict(views, t, frame: 3);
    Eq(1, views.ByNode.Count, "b not asked for in frame 3: dropped");
    Nodes.Release(t, a);
    Views.From(views, idA, frame: 4);   // asked for, but its node is gone
    Views.Evict(views, t, frame: 4);
    Eq(false, views.ByNode.ContainsKey(idA), "a's node released: dropped");
    int again = Nodes.New(t, Nodes.Root, K.A);             // the slot reused, a new id
    Eq(0, Views.User<List<int>>(Views.From(views, Nodes.IdOf(t, again), frame: 5)).Count, "a new node in the old slot gets fresh state");
});

Run("cmdgen: the table holds each command's wire name, label, description, contracts and arguments", () =>
{
    var set = TestCmdOps.ByWire("counter.increment_by")!;
    Eq("Add", set.Label, "its [Label]");
    Eq("Adds to the count.", set.Description, "its [Description]");
    Eq(OpFlags.None, set.Flags, "no contract");
    Eq(typeof(TestCmd.Counter_IncrementBy), set.Type, "its type");
    Eq("Amount:int:How much to add.", string.Join(',', set.Args.Select(a => $"{a.Name}:{a.Type}:{a.Description}")), "its argument");

    var reset = TestCmdOps.Of(new TestCmd.Counter_Reset());
    Eq(OpFlags.None, reset.Flags, "no contract");
    Eq(OpFlags.ReadOnly, TestCmdOps.ByWire("counter.peek")!.Flags, "[ReadsOnly]");

    var bare = TestCmdOps.ByWire("counter.set")!;
    Eq("Counter set", bare.Label, "a label made from the name, without [Label]");
    Eq("", bare.Description, "no description");
    Eq("", bare.Arg("Value")!.Description, "an argument without one");

    Eq(reset, TestCmdOps.ByLabel("Reset"), "by label");
    Eq(reset, TestCmdOps.ByType(typeof(TestCmd.Counter_Reset)), "by type");
    Eq(null, TestCmdOps.ByWire("counter.nope"), "an unknown wire name");
    Eq(6, TestCmdOps.All.Length, "every command");
});

Run("CmdJson: one JSON for Cmd, the app's commands and the library's by their $cmd names; an app's with a built-in's name refused at startup", () =>
{
    var json = CmdJson.For(Json.Default.Options, TestCmdOps.All);
    Eq(new TestCmd.Counter_IncrementBy(4), JsonSerializer.Deserialize("{\"$cmd\":\"counter.increment_by\",\"Amount\":4}", json), "the app's");
    var set = JsonSerializer.Deserialize("{\"$cmd\":\"view_rule.set\",\"Type\":\"item\",\"Lens\":\"frame(it)\"}", json) as BuiltinCmd.ViewRule_Set;
    Eq("item frame(it)", set is null ? "none" : $"{set.Type} {Exprs.Text(set.Lens!)}", "the library's, through the same JSON");
    Eq("{\"$cmd\":\"view_rule.set\",\"Type\":\"item\",\"Lens\":\"frame(it)\"}", JsonSerializer.Serialize<Cmd>(set!, json), "written as read");
    OpInfo clash = TestCmdOps.All[0] with { Wire = "view_rule.set" };
    bool refused;
    try { CmdJson.For(Json.Default.Options, [clash]); refused = false; }
    catch (InvalidOperationException) { refused = true; }
    Eq(true, refused, "an app's view_rule.set: two commands of one name");
});

Run("cmdgen: the wire ctx are the JSON's", () =>
{
    var json = JsonSerializer.Serialize<TestCmd>(new TestCmd.Counter_IncrementBy(4), Json.Default.TestCmd);
    Eq("{\"$cmd\":\"counter.increment_by\",\"Amount\":4}", json, "written");
    Eq(new TestCmd.Counter_IncrementBy(4), JsonSerializer.Deserialize(json, Json.Default.TestCmd), "read back");
});

Run("cmdgen: a kind's name and table; an id resolves only to a live node of the kind asked for", () =>
{
    var t = Nodes.NewTree();
    var root = Nodes.Root;
    var lists = ListsOrNew(t);
    Eq("lists", ShelfKinds.Names.Lists, "a kind's name, as an expression and the port call it");
    Eq(lists, Nodes.Child(t, root, ShelfKinds.Lists), "found by it");
    var a = Nodes.New(t, lists, ShelfKinds.Item);
    var items = new ShelfTables(t).Items;
    var item = ItemId.At(t, a);
    Eq(1.0, items.FromId(item).Weight, "a field never written: the row's default, through its table");
    items.SetRow(item, items.FromId(item) with { Name = "a" });
    Eq("a", items.FromId(item).Name, "a field, written and read as its row");
    Eq(true, Nodes.HasKind(t, Nodes.IdOf(t, a), ShelfKinds.Item), "an item's id, asked as an item");
    Eq(false, Nodes.HasKind(t, Nodes.IdOf(t, lists), ShelfKinds.Item), "another kind's: no");
    Eq(true, ItemId.From(t, Nodes.IdOf(t, lists)).IsNone, "nor an ItemId of it: the items table is never asked for another kind's row");
    var gone = Nodes.IdOf(t, a);
    Nodes.Release(t, a);
    Eq(false, Nodes.HasKind(t, gone, ShelfKinds.Item), "a released item's: no");
});

Run("cmdgen: a list's items step to the next and the previous of their kind, past other keys", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    var a = Nodes.New(t, lists, ShelfKinds.Item);
    new ShelfTables(t).Lists.SetRow(ListsId.At(t, lists), ShelfKinds.ListsDefault with { Title = "shopping" });   // a field: in its row, not among the items
    var b = Nodes.New(t, lists, ShelfKinds.Item);
    var c = Nodes.New(t, lists, ShelfKinds.Item);
    Eq(a, Nodes.Child(t, lists, ShelfKinds.Item), "the first");
    Eq(b, Nodes.NextOfKind(t, a), "the next");
    Eq(c, Nodes.NextOfKind(t, b), "the next");
    Eq(default(int), Nodes.NextOfKind(t, c), "none after the last");
    Nodes.Release(t, b);
    Eq(c, Nodes.NextOfKind(t, a), "a released item is stepped over");
});

Run("ModelValue: an expression field holds an Expr, said so by the schema and printed as written by the evaluator", () =>
{
    var rule = Exprs.Name("root").Get("lists").Get("item").At(0);
    var t = Nodes.NewTree();
    SetRule(t, rule);
    Eq(true, ModelEval.FieldOf(ShelfKinds.Schema, t, Schemas.Of(ShelfKinds.Schema, Kinds.Root)!, Nodes.IdOf(t, Nodes.Root), "rule").Type == EvalTypes.Expression, "held as an expression, in its row");
    Eq(rule, new ShelfTables(t).Roots.FromId(RootId.At(t, Nodes.Root)).Rule, "read back: the same expression");
    Eq(EvalKind.Opaque, Schemas.Of(ShelfKinds.Schema, Kinds.Root)!.Shape!.Members!.First(m => m.Name == "rule").Type.Kind, "the schema's type for it: opaque, printed as written");
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, ModelEval.MakeEvalTypes(ShelfKinds.Schema), () => t);
    Eq("root.lists.item[0]", Exprs.ValueText(Eval.Run(Exprs.Name("root").Get("rule"), ctx)), "the evaluator prints it, not evaluates it");
    long changes = t.Clock.Changes;
    SetRule(t, rule);
    Eq(changes, t.Clock.Changes, "set to the same expression: no edit");
});

Run("Parts: a node, a list and a record are parts; a node is told from them by ModelEval, and each is shown by its shape", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    SetName(t, Nodes.New(t, lists, ShelfKinds.Item), "a");
    SetRule(t, Exprs.Name("root"));
    var types = ModelEval.MakeEvalTypes(ShelfKinds.Schema);
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, types, () => t);
    var list = Eval.Run(Exprs.Name("root").Get("lists").Get("item"), ctx);
    var item = Eval.Run(Exprs.Name("root").Get("lists").Get("item").At(0), ctx);
    var name = Eval.Run(Exprs.Name("root").Get("lists").Get("item").At(0).Get("name"), ctx);
    var rule = Eval.Load(Eval.Run(Exprs.Name("root").Get("rule"), ctx));
    Eq(EvalKind.Parts, list.Kind, "a list: parts");
    Eq(EvalKind.Parts, item.Kind, "a node: parts");
    Eq(EvalKind.Opaque, rule.Kind, "an expression: opaque");
    Eq(true, ModelEval.IsNode(item), "a node is a node");
    Eq(false, ModelEval.IsNode(list), "a list holds its parent, but is no node");
    Eq(false, ModelEval.IsNode(name), "a field's address is no node");
    Eq(Nodes.IdOf(t, Nodes.Child(t, lists, ShelfKinds.Item)), ModelEval.IdOf(item), "its handle");
    Eq("fields", Lenses.Generic(item).Name, "named parts: drawn as fields");
    Eq("table", Lenses.Generic(list).Name, "parts alike: drawn as a table");
    Eq("text", Lenses.Generic(rule).Name, "an opaque value: drawn as text");
});

Run("Lenses: called, the value seen through the lens; lenses stack; arguments split as the lens says; a view rule seen wherever its values are read, raw(x) without it", () =>
{
    var t = Nodes.NewTree();
    var lists = ListsOrNew(t);
    SetName(t, Nodes.New(t, lists, ShelfKinds.Item), "a");
    SetName(t, Nodes.New(t, lists, ShelfKinds.Item), "b");
    var types = ModelEval.MakeEvalTypes(ShelfKinds.Schema);
    var ctx = new EvalContext();
    var lenses = new LensTable();
    Eval.AddBuiltinFunctionSymbols(ctx);
    ModelEval.AddSymbols(ctx, types, () => t);
    var crop = new LensFunc { Name = "crop", Params = [new("from", ArgWhen.Once, "Where it starts.")] };
    lenses.ByName.Add(Lenses.Table.Name, Lenses.Table);
    lenses.ByName.Add(Lenses.Text.Name, Lenses.Text);
    lenses.ByName.Add(crop.Name, crop);
    foreach (var lens in lenses.ByName.Values) ctx.Add(lens.Name, new Value(lenses.Callable, 0, 0, 0, lens));
    Eq(true, lenses.ByName["crop"] == crop && lenses.ByName.ContainsKey("table"), "the lens table: the core lenses and one added, by name");
    var items = Exprs.Name("root").Get("lists").Get("item");

    // A lens: the same value, its type carrying the lens, its kind and steps what it shows'.
    var table = Eval.Run(Exprs.Call("table", items, Exprs.It.Get("name")), ctx);
    Eq(EvalKind.Parts, table.Kind, "a table of parts is parts");
    Eq(2L, table.Type!.Count!(table), "its steps are what it shows'");
    Eq("table", table.Type.Lens!.Func.Name, "its lens");
    Eq("it.name", Exprs.Text(Lenses.Columns(table)[0]), "its columns: its per-part arguments");
    Eq("table(root.lists.item, it.name)", Exprs.Text(table.Type.Lens.Call), "the call as written, kept");
    var call = Exprs.Call("table", items, Exprs.It.Get("name"));
    int made = lenses.LensedTypes.Count;
    var once = Eval.Run(call, ctx);
    Eq(true, ReferenceEquals(once.Type, Eval.Run(call, ctx).Type), "the same call again: the same type, from the lens table's cache");
    Eq(made + 1, lenses.LensedTypes.Count, "made once, on the first miss");

    // Stacked: both kept, each with its own arguments.
    var both = Eval.Run(Exprs.Call("crop", Exprs.Call("table", items, Exprs.It.Get("name")), Exprs.Lit(1)), ctx);
    Eq(true, Lenses.Of(both, crop) is { Once.Length: 1 } && Lenses.Of(both, Lenses.Table) is { PerPart.Length: 1 }, "a lens over a lens keeps both");
    Eq(types.Sets[ShelfKinds.Item], EvalTypes.Bare(both.Type), "under both, the list");
    Eq("1", Exprs.ValueText(Eval.Run(Lenses.Of(both, crop)!.Once[0], ctx)), "a once argument, kept as written, evaluated when asked");

    // A view rule: every value of the type seen through it, wherever it is read; raw(x) without it. Kept
    // in the evaluation context (EvalContext.ViewRules), not on the type.
    var name = types.ByField[("item", "name")];
    ctx.ViewRules.Add(name, Exprs.Call("text", Exprs.It));
    var seen = Eval.Run(items.At(0).Get("name"), ctx);
    Eq("text", seen.Type?.Lens?.Func.Name, "a field read: seen through its view rule");
    Eq(EvalKind.Text, seen.Kind, "a leaf: read where it was stepped to, by the node's shape");
    Eq("\"a\"", Exprs.ValueText(seen with { Type = EvalTypes.Bare(seen.Type) }), "its value");
    Eq(true, seen.Address != 0 && ModelEval.AddressOf(types, seen) is ({ } on, "name") && on == Nodes.IdOf(t, Nodes.Child(t, ListsOrNew(t), ShelfKinds.Item)), "kept where it is: its node and key, from its address");
    Eq(null, Eval.Run(Exprs.Call("raw", items.At(0).Get("name")), ctx).Type?.Lens, "raw: without its view");
    Eq("text", Eval.At(Eval.Run(items.At(0), ctx), 0, ctx).Type?.Lens?.Func.Name, "a part, x[0] (Eval.At): seen through its view too");
    var item = Eval.Run(items.At(0), ctx);
    var byHook = item.Type!.Member!(item, "name");   // a field read by its hook, as a settings row reads one
    Eq(null, byHook.Type?.Lens, "read by its hook: not yet seen through its view");
    Eq("text", Eval.View(byHook, ctx).Type?.Lens?.Func.Name, "Eval.View: seen through it, as a drawer sees it");
    var other = new EvalContext();
    ModelEval.AddSymbols(other, types, () => t);
    Eq(null, Eval.Run(Exprs.Name("root").Get("lists").Get("item").At(0).Get("name"), other).Type?.Lens, "another context, the same types: not seen through this one's views");
    ctx.ViewRules.Remove(name);

    // A data lens: its steps in place of what it shows', for whoever reads it.
    var first = new LensFunc { Name = "first", Count = (in Value _) => 1 };
    lenses.ByName.Add(first.Name, first);
    ctx.Add(first.Name, new Value(lenses.Callable, 0, 0, 0, first));
    Eq("1", Exprs.ValueText(Eval.Run(Exprs.Call("count", Exprs.Call("first", items)), ctx)), "count through a data lens: its own");
});

Run("Addresses: a Place's start at a key (the layer's own, kept by the Place) and the steps from it, interned; the evaluator steps by a type's shape, reading only leaves, through the Place's reader; dropped on a new generation, the Places kept", () =>
{
    var ctx = new EvalContext();
    var table = ctx.Addresses;
    // A hand-made layer: shelves of items, each shelf by its key. Its shape is data; its reader is its own.
    var itemType = new EvalType { Name = "shelf_item", Kind = EvalKind.Parts, Members = [("name", EvalTypes.Text), ("weight", EvalTypes.Real)] };
    var itemsType = new EvalType { Name = "shelf_item[]", Kind = EvalKind.Parts, PartType = itemType };
    var shelfType = new EvalType { Name = "shelf", Kind = EvalKind.Parts, Members = [("label", EvalTypes.Text), ("items", itemsType)] };
    var keyType = new EvalType { Name = "shelf_key", Kind = EvalKind.Parts, Members = [("id", EvalTypes.Int)] };
    string[][] names = [["a", "b"], ["c"]];
    int reads = 0;
    var shelf = Places.Add(ctx, "shelf", "A shelf, by its key.", shelfType, (in ShelfKey k, ReadOnlySpan<Step> path) =>
    {
        reads++;
        return path switch
        {
            [{ Kind: StepKind.Member, At: 0 }] => Value.Of($"shelf {k.Id}"),
            [{ Kind: StepKind.Member, At: 1 }, { Kind: StepKind.Count }] => Value.Of((long)names[k.Id].Length),
            [{ Kind: StepKind.Member, At: 1 }, { Kind: StepKind.Element, At: var i }, { Kind: StepKind.Member, At: 0 }] => Value.Of(names[k.Id][i]),
            [{ Kind: StepKind.Member, At: 1 }, { Kind: StepKind.Element, At: var i }, { Kind: StepKind.Member, At: 1 }] => Value.Of(1.5 * i),
            _ => Value.None,
        };
    }, keyType, (ReadOnlySpan<Value> args, out ShelfKey key) =>
    {
        key = args.Length == 1 ? new ShelfKey((int)Eval.Load(args[0]).AsInt) : default;
        return args.Length == 1;
    });
    Eval.AddBuiltinFunctionSymbols(ctx);
    Value Run(Expr e) => Eval.Load(Eval.Run(e, ctx));
    var s0 = Exprs.Call("shelf", Exprs.Lit(0));
    var s1 = Exprs.Call("shelf", Exprs.Lit(1));

    reads = 0;
    var items = Run(s0.Get("items"));
    Eq(0, reads, "a member that is no leaf: an address, nothing read");
    Eq(true, Run(Exprs.Call("shelf")).IsNone, "entered by its name with no key: none");
    Eq("\"shelf 0\"", Exprs.ValueText(Run(s0.Get("label"))), "a member that is a leaf: read by the Place's reader");
    Eq(2L, Run(Exprs.Call("count", s0.Get("items"))).AsInt, "a set's count, by shape: read by the reader");
    Eq("\"b\"", Exprs.ValueText(Run(s0.Get("items").At(1).Get("name"))), "an element's member");
    Eq("1.5", Exprs.ValueText(Run(s0.Get("items").At(1).Get("weight"))), "and another");
    Eq(true, Run(s0.Get("items").At(2)).IsNone, "past its last: none");
    Eq(true, Eval.At(items, 1, ctx).Address == Run(s0.Get("items").At(1)).Address, "the same steps, however reached: the same address");
    Eq(true, Run(s0).Address != Run(s1).Address, "another key: another address");
    Eq(new ShelfKey(1), Places.KeyOf(ctx, shelf, Run(s1.Get("items").At(0)).Address), "an address's key: the layer's own, from its start's Place");

    // A key of the layer's own, whatever it is: here a text and a number, kept by the Place, not as bytes.
    var named = Places.Add(ctx, "named", "Entered by no name: no key packer.", shelfType, (in NamedKey k, ReadOnlySpan<Step> _) => Value.Of(k.Name));
    int n1 = Places.Start(ctx, named, new NamedKey("north", 2)), n2 = Places.Start(ctx, named, new NamedKey("north", 2));
    Eq(true, n1 == n2 && n1 != Places.Start(ctx, named, new NamedKey("south", 2)), "a key with a text: the same key, the same start; another, another");
    Eq(new NamedKey("north", 2), Places.KeyOf(ctx, named, n1), "read back whole, as the layer's own key");
    Eq(default(NamedKey), Places.KeyOf(ctx, named, Run(s1).Address), "asked of an address in another Place: none");
    Eq(false, ctx.Symbols.ContainsKey("named"), "a Place with no key packer: not entered by name");

    ulong before = Addresses.Hash(ctx, Run(s1.Get("items").At(0)).Address);
    Eq(true, before != 0 && before != Addresses.Hash(ctx, Run(s0.Get("items").At(0)).Address) && before != Addresses.Hash(ctx, Run(s1.Get("items")).Address),
        "an address's hash (XxHash3): what it means, so another key or another step hashes otherwise");
    ulong north = Addresses.Hash(ctx, n1);
    Addresses.Begin(table, 7);
    Eq(1, table.Count, "a new generation: its addresses dropped");
    Eq(before, Addresses.Hash(ctx, Run(s1.Get("items").At(0)).Address), "the same address in the next generation: the same hash, its id another");
    Places.Start(ctx, named, new NamedKey("south", 2));   // first this time: north's index in the Place's keys is another
    Eq(north, Addresses.Hash(ctx, Places.Start(ctx, named, new NamedKey("north", 2))), "and a text key's: by the key, not by its index in the Place's keys");
    Eq(2, named.Keys.All.Count, "the Place's keys: dropped with the generation, only those entered since kept");
    Eq("\"c\"", Exprs.ValueText(Run(s1.Get("items").At(0).Get("name"))), "its Places kept: stepped and read again");
});

Run("Places from [Place<K, D>]: cmdgen writes the registration, each data's shape and reader, and its key's packer; entered by name, listed in places", () =>
{
    var ctx = new EvalContext();
    var town = new Library { Name = "town", Books = [new("a", 10, 0.5), new("b", 20, 0.7)], Years = [1999, 2004] };
    town.Loans.Items[0] = new("c", 30, 1.0);
    town.Loans.Count = 1;
    var libraries = new Dictionary<int, Library> { [1] = town };
    Eval.AddBuiltinFunctionSymbols(ctx);
    LibraryLayerPlaces.Add(ctx, libraries);   // cmdgen's: library(id), lobby
    string Text(Expr e) => Exprs.ValueText(Eval.Load(Eval.Run(e, ctx)));
    var lib = Exprs.Call(LibraryLayerPlaces.FindName, Exprs.Lit(1));

    Eq("\"town\"", Text(lib.Get("name")), "a leaf of the data");
    Eq("2", Text(Exprs.Call("count", lib.Get("books"))), "a list's count");
    Eq("\"b\"", Text(lib.Get("books").At(1).Get("title")), "a list's element's member");
    Eq("20", Text(lib.Get("books").At(1).Get("pages")), "another");
    Eq("2004", Text(lib.Get("years").At(1)), "an array of leaves");
    Eq("1", Text(Exprs.Call("count", lib.Get("loans"))), "the layer's own collection: its Count");
    Eq("\"c\"", Text(lib.Get("loans").At(0).Get("title")), "and its indexer");
    Eq("none: library(1).books[2] is no part", Text(lib.Get("books").At(2)), "past the last: none, and why");
    Eq("none", Text(lib.Get("corner").Get("label")), "a nested record that is null: none");
    Eq("none", Text(Exprs.Name(LibraryLayerPlaces.LobbyName).Get("label")), "a keyless Place, entered by its bare name: its data none yet");
    town.Corner = new Corner { Label = "by the window" };
    Eq("\"by the window\"", Text(lib.Get("corner").Get("label")), "and once it is there: read through it, the same address");
    Eq("\"by the window\"", Text(Exprs.Name(LibraryLayerPlaces.LobbyName).Get("label")), "and the keyless Place's, read the same");
    Eq("none", Text(Exprs.Call("library", Exprs.Lit(2)).Get("name")), "a key the layer has not: none");
    Eq("none", Text(Exprs.Call("library", Exprs.Lit("one")).Get("name")), "arguments that make no key: none");
    var book = Eval.Run(lib.Get("books").At(0), ctx);
    Eq(3L, Eval.Count(book), "a record by shape: its parts are its members");
    Eq("\"a\"", Exprs.ValueText(Eval.Load(Eval.At(book, 0, ctx))), "its member at an address");
    Eq("pages", LibraryEval.Type.Members![1].Type.PartType!.Members![1].Name, "the shape is data: the data's members, its list's part's members");

    var places = Exprs.Name(Places.ListingName);
    Eq("3", Text(Exprs.Call("count", places)), "places: every Place, itself among them");
    Eq("\"library(id: int) → library\"", Text(places.At(1).Get("signature")), "how one is entered, from its key's fields");
    Eq("\"lobby → corner\"", Text(places.At(2).Get("signature")), "a keyless one: by its bare name");
    Eq("\"A library, by its id.\"", Text(places.At(1).Get("description")), "and what it is");
});

Run("Tables: a node's row from its making, its last versions in a ring; a write equal to the newest changes nothing, one in the same generation replaces it; released, gone, its id stale, its entry reused", () =>
{
    var t = ShelfKinds.NewTree();                       // generation 1
    var lists = ListsOrNew(t);
    var items = new ShelfTables(t).Items;
    var a = ItemId.New(t, lists);
    Eq(1.0, items.FromId(a).Weight, "a new node's row, added with it: the declared defaults");
    Eq(1L, items.GenFromId(a), "written in generation 1");

    t.Clock.Gen = 2; items.SetRow(a, items.FromId(a) with { Name = "first" });
    t.Clock.Gen = 3; items.SetRow(a, items.FromId(a) with { Name = "second" });
    Eq("second", items.FromId(a).Name, "the newest");
    Eq(3L, items.GenFromId(a), "and its generation: what a reader compares to know it read what is stale");
    Eq("first", RowAt(items, a, 2).Name, "generation 2 reads the version it saw");
    Eq("", RowAt(items, a, 1).Name, "generation 1, the row as made");

    long changes = t.Clock.Changes;
    items.SetRow(a, items.FromId(a));
    Eq(changes, t.Clock.Changes, "equal to the newest: nothing written");
    Eq(3L, items.GenFromId(a), "and its generation stays: a reader that read it is not stale");
    items.SetRow(a, items.FromId(a) with { Name = "third" });
    Eq("first", RowAt(items, a, 2).Name, "a second write in generation 3 replaces 3's version, not 2's");

    for (int g = 4; g < 4 + Table<Shelf.Item, ItemId>.History; g++) { t.Clock.Gen = g; items.SetRow(a, items.FromId(a) with { Name = $"v{g}" }); }
    Eq(false, items.FromIdAt(a, 2, out _), "older than its ring keeps: said so, not guessed");
    Eq(true, items.FromIdAt(a, t.Clock.Gen - 1, out var recent) && recent.Name == $"v{t.Clock.Gen - 1}", "a recent generation still reads its version");

    var b = ItemId.New(t, lists);
    Eq(2, items.Count, "two rows");
    Nodes.Release(t, a.SlotIn(t));
    Eq("", items.FromId(a).Name, "released: its row reads as the default");
    Eq(0L, items.GenFromId(a), "and has no generation: gone");
    Eq(1, items.Count, "its entry freed, the other moved into it");
    Eq(b, ItemId.At(t, b.SlotIn(t)), "the moved row: still found by its id");
    changes = t.Clock.Changes;
    items.SetRow(a, ShelfKinds.ItemDefault with { Name = "after" });
    Eq(changes, t.Clock.Changes, "a write to a released node's id: a stale id, nothing written");
    var c = ItemId.New(t, lists);
    Eq(2, items.Count, "a new row: the freed entry, its ring reused");
    Eq("", items.FromId(c).Name, "with nothing of the closed row's");
    Eq("#42", new NodeId(42).ToString(), "an id written #42");
});

Run("View rules: in the tree a model starts with (the schema's: NodeKinds.NewTree), changed by the built-in command (Model.SetViewRuleForType), logged, read each frame", () =>
{
    var model = new Model((_, _, _) => { }, tree: ShelfKinds.NewTree());   // an app's apply that knows nothing of view rules
    var types = ModelEval.MakeEvalTypes(ShelfKinds.Schema);
    var ctx = new EvalContext();
    var (item, zoom) = (types.Kinds[ShelfKinds.Item]!, types.ByField[("frame", "zoom")]);
    var framed = Exprs.Call("frame", Exprs.It);
    void Fill() => ModelEval.FillViewRulesFromModel(ctx, types, model.Tree);

    ModelEval.FillViewRulesFromModel(ctx, types, model.Tree);
    Eq(0L, model.Gen, "the schema's rules are the tree's start, not commands");
    Eq("range(it, 0.5, 2.0, 0.25)", ctx.ViewRules.TryGetValue(zoom, out var range) ? Exprs.Text(range) : "none", "[Range] frame.zoom: read from the tree, as any rule");

    model.SetViewRuleForType(ShelfKinds.Types.Item, framed);
    model.Run();
    Eq(1L, model.Gen, "set at runtime: a built-in command, its own generation; the app's apply never asked");
    Eq(true, model.TryEntry(1, out var first) && first.Cmd is BuiltinCmd.ViewRule_Set { Type.Name: "item" }, "logged, as the library's");
    Fill();
    Eq(true, ReferenceEquals(framed, ctx.ViewRules[item]), "the expression the tree holds, not a copy");
    model.SetViewRuleForType(ShelfKinds.Types.FrameZoom, framed);
    model.Run();
    Fill();
    Eq(true, ReferenceEquals(framed, ctx.ViewRules[zoom]), "a rule the schema started with, replaced as any is");
    model.SetViewRuleForType(ShelfKinds.Types.Item, null);
    model.Run();
    Fill();
    Eq(false, ctx.ViewRules.ContainsKey(item), "removed: no rule");
    long gen = model.Gen;
    model.SetViewRuleForType(ShelfKinds.Types.FrameZoom, framed);
    model.Run();
    Eq(gen, model.Gen, "the same rule again: no change, no generation");
    model.SetViewRuleForType(new("items"), framed);
    model.SetViewRuleForType(new("item.nope"), framed);
    model.Run();
    Eq(gen, model.Gen, "a type the schema does not name: refused, nothing written");
    model.SetViewRuleForType(ShelfKinds.Types.ItemList, framed);
    model.SetViewRuleForType(ShelfKinds.Types.ItemName, framed);
    model.Run();
    Fill();
    Eq(true, ReferenceEquals(framed, ctx.ViewRules[types.Named["item[]"]]) && ReferenceEquals(framed, ctx.ViewRules[types.Named["item.name"]]), "a list of a kind, a field: named by the schema, set");
});

Run("View rules: a node's own, by the same built-in command (Model.SetViewRuleForNode), read before its kind's; a gone node or a call of no lens refused", () =>
{
    var model = new Model((_, _, _) => { }, tree: ShelfKinds.NewTree());
    var lenses = new LensTable();
    lenses.ByName.Add("frame", new LensFunc { Name = "frame", Description = "" });
    model.Lenses = lenses;
    var types = ModelEval.MakeEvalTypes(ShelfKinds.Schema);
    var ctx = new EvalContext();
    var item = types.Kinds[ShelfKinds.Item]!;
    var framed = Exprs.Call("frame", Exprs.It);
    var lists = ListsOrNew(model.Tree);
    var a = Nodes.IdOf(model.Tree, Nodes.New(model.Tree, lists, ShelfKinds.Item));

    model.SetViewRuleForNode(a, framed);
    model.Run();
    ModelEval.FillViewRulesFromModel(ctx, types, model.Tree);
    Eq(true, ctx.NodeViewRules.TryGetValue((item, a.Value), out var own) && ReferenceEquals(framed, own), "its own: by its kind's type and its id");
    Eq(false, ctx.ViewRules.ContainsKey(item), "its kind's untouched");

    long gen = model.Gen;
    model.SetViewRuleForNode(a, Exprs.Call("nolens", Exprs.It));
    model.SetViewRuleForNode(new NodeId(9999), framed);
    model.Run();
    Eq(gen, model.Gen, "a call of no lens, a node gone: nothing written");

    model.SetViewRuleForNode(a, null);
    model.Run();
    ModelEval.FillViewRulesFromModel(ctx, types, model.Tree);
    Eq(false, ctx.NodeViewRules.ContainsKey((item, a.Value)), "removed: its kind's again");
});

Run("node.set: a built-in; its value an expression over the model alone, `it` the node; refused for a field not editable, a value not of its type, none", () =>
{
    var model = new Model((_, _, _) => { }, tree: ShelfKinds.NewTree());
    var t = model.Tree;
    var lists = ListsOrNew(t);
    var a = Nodes.IdOf(t, Nodes.New(t, lists, ShelfKinds.Item));
    var b = Nodes.IdOf(t, Nodes.New(t, lists, ShelfKinds.Item));
    var root = Nodes.IdOf(t, Nodes.Root);
    string Name(NodeId n) => new ShelfTables(t).Items.FromId(ItemId.From(t, n)).Name;

    model.SetField(a, "name", Exprs.Lit("first"));
    model.Run();
    Eq("first", Name(a), "a literal");
    long gen = model.Gen;
    var wire = CmdJson.For(Json.Default.Options, TestCmdOps.All);
    model.Push(JsonSerializer.Deserialize($"{{\"$cmd\":\"node.set\",\"Node\":\"{b}\",\"Key\":\"name\",\"Value\":\"${a.Value}.name\"}}", wire)!);
    model.Run();
    Eq("first", Name(b), "another node's field, read as the command runs: from the wire, as an agent sends it");
    Eq(true, model.TryEntry(gen + 1, out var logged) && logged.Cmd is BuiltinCmd.Node_Set { Key: "name" }, "logged as the library's");

    var fr = FrameId.New(t, Nodes.Root);
    model.SetField(fr.Id, "zoom", Exprs.Lit(2L));
    model.SetField(fr.Id, "framed", Exprs.Lit(Value.Of(b)));
    model.Run();
    var frame = fr.RowFromId(t);
    Eq((2.0, b), (frame.Zoom, frame.Framed.Id), "an int read as its real; a node as its id");

    gen = model.Gen;
    model.SetField(fr.Id, "zoom", Exprs.Lit("big"));                 // not its type
    model.SetField(fr.Id, "framed", Exprs.Lit(Value.Of(lists == 0 ? default : Nodes.IdOf(t, lists))));   // a node of another kind
    model.SetField(root, "zoom", Exprs.Lit(2L));                    // a key its kind has not
    model.SetField(a, "name", Exprs.Name("root").Get("nope"));       // none
    model.SetField(a, "nope", Exprs.Lit("x"));                       // no such field
    model.SetField(new NodeId(9999), "name", Exprs.Lit("x"));        // a node gone
    model.Run();
    Eq(gen, model.Gen, "each refused: nothing written");
    var frames = new EvalContext();   // the context a frame selected: a node.set run mid-frame (Model.Do) leaves it selected
    Eval.Select(frames);
    model.SetField(a, "name", Exprs.Lit("again"));
    model.Run();
    Eq(true, ReferenceEquals(frames, Eval.Current) && Name(a) == "again", "the thread's context as it was");
});

Run("Fields: a frame's, each sort an editable field may be: a range's and a pick's view rules, an enum, a handle; set by key", () =>
{
    var t = ShelfKinds.NewTree();   // with the schema's view rules
    var schema = ShelfKinds.Schema;
    var types = ModelEval.MakeEvalTypes(schema);
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, types, () => t);
    string Zoom() => Exprs.ValueText(Eval.Run(Exprs.Name("root").Get("frame").Get("zoom"), ctx));
    Eq("none", Zoom(), "no frame: none");
    var fr = FrameId.New(t, Nodes.Root);
    Eq("1", Zoom(), "a frame: its default before it is set");
    var zoom = types.ByField[("frame", "zoom")];
    ModelEval.FillViewRulesFromModel(ctx, types, t);
    Eq("range(it, 0.5, 2.0, 0.25)", ctx.ViewRules.TryGetValue(zoom, out var range) ? Exprs.Text(range) : "none", "[Range]: its view rule, from its schema, through the tree");
    Eq("pick(it, frame_styles)", ctx.ViewRules.TryGetValue(types.ByField[("frame", "style")], out var pick) ? Exprs.Text(pick) : "none", "[Pick]: the same");

    // An enum field: kept as its value's name, its choices the enum's names, only those set.
    Eq("thin,thick", string.Join(",", types.ByField[("frame", "border")].Choices.Holder as string[] ?? []), "[EnumTable]: the field's choices, from the table's rows");
    var border = types.ByField[("frame", "border")];
    Eq(true, border.Choices is { Type.Count: { } count } set && count(set) == 2, "its type's choices: picked from them");
    Eq("Thin line,Thick line", string.Join(",", border.ChoiceLabels ?? []), "each shown by its label: the table's Label column");
    Eq(EvalKind.Text, border.Kind, "a leaf: kept as its name");
    Eq(Edge.Thin, fr.RowFromId(t).Border, "its default, as the enum");
    var writing = ModelEval.WriteContext(t, schema);
    Eq(true, ModelEval.Set(t, writing, fr.Id, "border", Exprs.Lit("thick")), "set to one of its names");
    Eq(Edge.Thick, fr.RowFromId(t).Border, "read back as the enum");
    Eq(false, ModelEval.Set(t, writing, fr.Id, "border", Exprs.Lit("wavy")), "a name it has not: refused");
    Eq(3, Frames.Edges[(int)Edge.Thick].Width, "the table indexed by it");

    // A handle field: every node of its kind, made once with no tree, read in the tree its field is.
    var lists = ListsOrNew(t);
    Nodes.New(t, lists, ShelfKinds.Item);
    Nodes.New(t, lists, ShelfKinds.Item);
    var framed = types.ByField[("frame", "framed")];
    Eq(true, framed.Choices is { Holder: null, Address: 0 }, "its type's choices: made once, with the type; no tree, no address of their own");
    Eq(ShelfKinds.Item, ModelEval.KindOf(framed.Choices), "the kind they are every node of: what the field may name (the schema's ref)");
    var field = Eval.Run(Exprs.Name("root").Get("frame").Get("framed"), ctx);
    Eq(2L, Eval.Count(Lenses.ChoicesOf(field)), "read in the tree the context reads (its Model's), as every model value is: its two items");
    var fromSchema = ctx.ViewRules[zoom];
    ctx.ViewRules[zoom] = Exprs.Call("text", Exprs.It);
    var lenses = new LensTable();
    ctx.Add(Lenses.Text.Name, new Value(lenses.Callable, 0, 0, 0, Lenses.Text));
    var setting = Exprs.Name("root").Get("frame").Get("zoom");
    Eq("text", Eval.Run(setting, ctx).Type?.Lens?.Func.Name, "the field's one type, its view, before it is set");
    Eq(true, ModelEval.Set(t, writing, fr.Id, "zoom", Exprs.Lit(2.0)), "set at the node, by its key");
    Eq("text", Eval.Run(setting, ctx).Type?.Lens?.Func.Name, "the same type, its view, after");
    Eq<(bool, string?)>((true, "zoom"), ModelEval.AddressOf(types, Eval.Run(setting, ctx)) is var (n, k) ? (Nodes.At(t, Nodes.SlotOf(t, n)).Kind == ShelfKinds.Frame, k) : default, "its address: the node (the frame), and the key");
    // A lens's argument, with `it` the field it is applied to: owner(it), the node it is on.
    ctx.Add(Lenses.Range.Name, new Value(lenses.Callable, 0, 0, 0, Lenses.Range));
    ctx.ViewRules[zoom] = Exprs.Call("range", Exprs.It, Exprs.Lit(0), Exprs.Call(ModelEval.OwnerName, Exprs.It).Get("zoom"));
    var slid = Eval.Run(setting, ctx);
    ctx.DynamicVar = Value.Of("unrelated");   // what `it` was before: not what the argument sees
    Eq("2", Exprs.ValueText(Lenses.Arg(Lenses.Of(slid, Lenses.Range)!, 1, slid, ctx)), "its max: owner(it).zoom, read through, `it` the field");
    Eq("\"unrelated\"", Exprs.ValueText(ctx.DynamicVar), "`it` put back after");
    Eq("none", Exprs.ValueText(Eval.Run(Exprs.Call(ModelEval.OwnerName, Exprs.Lit(3)), ctx)), "owner of what is not a field: none");
    ctx.DynamicVar = default;
    ctx.ViewRules.Remove(zoom);
    Eq("2", Zoom(), "read back");
    Eq(false, ModelEval.Set(t, writing, Nodes.IdOf(t, Nodes.Root), "zoom", Exprs.Lit(3.0)), "a key its kind has not: refused");
    ctx.ViewRules[zoom] = fromSchema;
});

//- the text form: ExpParser reads what Exprs.Text prints (docs/proposals/expression-grammar.md)

// The same expression, node for node: Call's arguments are an array, which record equality compares
// by reference, so compared here by their elements.
static bool SameExpr(Expr a, Expr b) => (a, b) switch
{
    (Expr.Name x, Expr.Name y) => x.Id == y.Id,
    (Expr.It, Expr.It) => true,
    (Expr.Member x, Expr.Member y) => x.Key == y.Key && SameExpr(x.Of, y.Of),
    (Expr.Index x, Expr.Index y) => SameExpr(x.Of, y.Of) && SameExpr(x.At, y.At),
    (Expr.Call x, Expr.Call y) => SameExpr(x.Callee, y.Callee) && x.Args.Length == y.Args.Length && x.Args.Zip(y.Args).All(z => SameExpr(z.First, z.Second)),
    (Expr.Lit x, Expr.Lit y) => x.Value.Type == y.Value.Type && x.Value.At == y.Value.At && x.Value.Id == y.Value.Id
                               && (x.Value.Real == y.Value.Real || double.IsNaN(x.Value.Real) && double.IsNaN(y.Value.Real)) && Equals(x.Value.Holder, y.Value.Holder),
    (Expr.Not x, Expr.Not y) => SameExpr(x.Of, y.Of),
    (Expr.Binary x, Expr.Binary y) => x.Op == y.Op && SameExpr(x.Left, y.Left) && SameExpr(x.Right, y.Right),
    _ => false,
};

static Expr Parsed(string text) =>
    ExpParser.Parse(text, out var error) ?? throw new Exception($"did not parse: {ExpParser.Describe(text, error)}");

Run("Parse: each form of Expr, and printed back as written", () =>
{
    string[] texts =
    [
        "root", "it", "it.mode.source", "root.graph.mode[2]", "vessels(it.mode)", "f()", "f(a)(b)", "a.b[0](x).c",
        "3", "-1", "0", "2.5", "2.0", "-0.25", "1E-05", "\"Arterial\"", "\"a \\\"b\\\" \\\\ c\\n\"", "true", "false", "$15",
        "!it.done", "!!a", "a == b", "a != b", "a < b", "a <= b", "a > b", "a >= b", "a && b", "a || b",
        "a && b && c", "a || b && c", "(a || b) && c", "a && (b && c)", "!a == b", "!(a == b)", "(a < b) == c",
        "(!a).b", "(a && b).c", "x[i == 0]", "table(cases, it.title, vessels(it.mode).volume_ml)",
        "count(root.lists.item) > 2 && it.weight != -1", "it.it.true.false",
    ];
    foreach (var text in texts) Eq(text, Exprs.Text(Parsed(text)), $"`{text}` read and printed");
});

Run("Parse: what Exprs.Text prints reads back as the same expression", () =>
{
    var it = Exprs.It;
    Expr[] built =
    [
        Exprs.Call("table", Exprs.Name("cases"), it.Get("title"), Exprs.Call("vessels", it.Get("mode")).Get("volume_ml")),
        it.Get("weight").Op(BinaryOp.Greater, Exprs.Lit(1.0)),
        it.Get("a").Op(BinaryOp.And, it.Get("b").Op(BinaryOp.And, it.Get("c"))),                      // leaning right: kept by parentheses
        it.Get("a").Op(BinaryOp.Or, it.Get("b")).Op(BinaryOp.And, it.Get("c")),
        it.Get("a").Op(BinaryOp.Eq, it.Get("b")).Op(BinaryOp.Eq, it.Get("c")),                                                     // a comparison of a comparison
        new Expr.Not(it.Get("a").Op(BinaryOp.Eq, Exprs.Lit(0))),
        new Expr.Member(new Expr.Not(it), "x"),
        Exprs.Lit(2.0), Exprs.Lit(0.1), Exprs.Lit(1e300), Exprs.Lit(-7), Exprs.Lit(long.MinValue),
        Exprs.Lit("quote \" backslash \\ tab \t line \n end"),
        new Expr.Lit(Value.Of(new NodeId(1043))).Get("name"),
        Exprs.Name("root").Get("lists").Get("item").At(Exprs.Lit(0)),
    ];
    foreach (var e in built)
    {
        var text = Exprs.Text(e);
        Eq(true, SameExpr(e, Parsed(text)), $"`{text}` reads back as what printed it");
    }
});

Run("Parse: precedence and grouping, as the tree they make", () =>
{
    var e = Parsed("a || b && c == d");
    Eq(true, e is Expr.Binary { Op: BinaryOp.Or, Right: Expr.Binary { Op: BinaryOp.And, Right: Expr.Binary { Op: BinaryOp.Eq } } }, "|| loosest, then &&, then ==");
    Eq(true, Parsed("a && b && c") is Expr.Binary { Op: BinaryOp.And, Left: Expr.Binary { Op: BinaryOp.And } }, "a chain leans left");
    Eq(true, Parsed("!a == b") is Expr.Binary { Left: Expr.Not }, "! binds tighter than a comparison");
    Eq(true, Parsed("!a.b") is Expr.Not { Of: Expr.Member }, "a step binds tighter than !");
    Eq(true, Parsed("3.x") is Expr.Member { Of: Expr.Lit { Value.Kind: EvalKind.Int }, Key: "x" }, "a point without a digit after it is a step");
    Eq(EvalKind.Int, Parsed("2") is Expr.Lit l ? l.Value.Kind : EvalKind.None, "2 is an int");
    Eq(EvalKind.Real, Parsed("2.0") is Expr.Lit r ? r.Value.Kind : EvalKind.None, "2.0 is a real");
    Eq(EvalTypes.Node, Parsed(" $15 ") is Expr.Lit a ? a.Value.Type : null, "an address: a node, by its id");
    Eq("table(cases, it.title)", Exprs.Text(Parsed("  table ( cases ,\n\tit . title )  ")), "space between tokens means nothing");
});

Run("Parse: a text that is not an expression says where it stopped and what it wanted", () =>
{
    (string Text, int At, string Expected)[] bad =
    [
        ("", 0, "an expression"),
        ("heat(plane(fit, \"tmax\")", 23, "\",\" or \")\""),
        ("a.", 2, "a key after \".\""),
        ("a[0", 3, "\"]\""),
        ("(a", 2, "\")\""),
        ("a b", 2, "the end, or an operator"),
        ("a < b < c", 6, "no second comparison: add parentheses"),
        ("a = b", 2, "\"==\""),
        ("a & b", 2, "\"&&\""),
        ("\"open", 0, "a closing '\"'"),
        ("$", 0, "an address: $ and digits"),
        ("$ 15", 0, "an address: $ and digits"),
        ("$99999999999999999999", 0, "an address whose id fits a long"),
        ("99999999999999999999", 0, "an int that fits a long"),
        ("- 1", 0, "an expression"),
        ("f(a,)", 4, "an expression"),
        ("it.1", 3, "a key after \".\""),
    ];
    foreach (var (text, at, expected) in bad)
    {
        Eq(null, ExpParser.Parse(text, out var error), $"`{text}` does not parse");
        Eq(new ParseError(at, expected), error, $"`{text}`: where and why");
    }
    ExpParser.Parse("f(x", out var e2);
    Eq("f(x\n   ^ at 3: expected \",\" or \")\"", ExpParser.Describe("f(x", e2), "described: a caret under where it stopped");
});

Run("Parse: spans give each node its place in the text", () =>
{
    var spans = new Dictionary<Expr, (int From, int To)>(ReferenceEqualityComparer.Instance);
    const string text = "vessels(it.mode).volume_ml > 2";
    var e = ExpParser.Parse(text, out _, spans)!;
    var compare = (Expr.Binary)e;
    var member = (Expr.Member)compare.Left;
    Eq("vessels(it.mode).volume_ml > 2", text[spans[e].From..spans[e].To], "the whole");
    Eq("vessels(it.mode).volume_ml", text[spans[member].From..spans[member].To], "a step: from where its value starts");
    Eq("it.mode", text[spans[((Expr.Call)member.Of).Args[0]].From..spans[((Expr.Call)member.Of).Args[0]].To], "an argument");
    Eq("2", text[spans[compare.Right].From..spans[compare.Right].To], "a literal");
});

Run("Parse: an expression in JSON is its text, both ways", () =>
{
    var e = Parsed("table(root.lists.item, it.name, it.weight > 1.5)");
    var json = JsonSerializer.Serialize(e);
    Eq("\"table(root.lists.item, it.name, it.weight \\u003E 1.5)\"", json, "written as its text");
    Eq(true, SameExpr(e, JsonSerializer.Deserialize<Expr>(json)!), "read back as the same expression");
    string? why = null;
    try { JsonSerializer.Deserialize<Expr>("\"f(\""); } catch (JsonException ex) { why = ex.Message; }
    Eq("f(\n  ^ at 2: expected an expression", why, "a text that does not parse: the parser's description");
});

Run("Parse: an address literal finds its node through the model, as one built in code does", () =>
{
    var t = Nodes.NewTree();
    var ctx = new EvalContext();
    ModelEval.AddSymbols(ctx, ModelEval.MakeEvalTypes(ShelfKinds.Schema), () => t);
    var item = Nodes.New(t, ListsOrNew(t), ShelfKinds.Item);
    SetName(t, item, "pear");
    long id = Nodes.IdOf(t, item).Value;
    Eq("\"pear\"", Exprs.ValueText(Eval.Run(Parsed($"${id}.name"), ctx)), "$id.name, parsed");
    Eq($"none: ${id + 99} is gone", Exprs.ValueText(Eval.Run(Parsed($"${id + 99}.name"), ctx)), "an address naming no node: none, at evaluation, and why");
});

//- Status: why a value is none, said where it was made

// A shelf with one item, the language's builtins, and `later`: a hook that answers pending, as a
// cache does for what it has not made yet.
(EvalContext Ctx, ModelStore Tree) StatusShelf()
{
    var t = Nodes.NewTree();
    var ctx = new EvalContext();
    Eval.AddBuiltinFunctionSymbols(ctx);
    ModelEval.AddSymbols(ctx, ModelEval.MakeEvalTypes(ShelfKinds.Schema), () => t);
    SetName(t, Nodes.New(t, ListsOrNew(t), ShelfKinds.Item), "pear");
    ctx.Add("later", EvalTypes.Function("later", "Answers pending.",
        (in Value _, ReadOnlySpan<Value> _, Expr.Call _) => new Value(null, 0, 0, 0, "the thing asked for") { Status = Status.Pending }));
    return (ctx, t);
}

string Said(EvalContext ctx, string text) => Exprs.ValueText(Eval.Run(Parsed(text), ctx));

Run("Status: nothing there is none, and Produced", () =>
{
    var (ctx, _) = StatusShelf();
    Eq("none", Said(ctx, "root.frame"), "no frame under it: that is the answer");
    Eq(Status.Produced, Eval.Run(Parsed("root.frame"), ctx).Status, "Produced");
});

Run("Status: a name nothing registered", () =>
{
    var (ctx, _) = StatusShelf();
    Eq("none: shelf is no name", Said(ctx, "shelf.lists"), "the first step's why, through the steps after it");
});

Run("Status: a member the kind does not have", () =>
{
    var (ctx, _) = StatusShelf();
    Eq("none: root.lists.titel is no member", Said(ctx, "root.lists.titel"), "no member, by its key");
});

Run("Status: a part that is not there", () =>
{
    var (ctx, _) = StatusShelf();
    Eq("none: root.lists.item[7] is no part", Said(ctx, "root.lists.item[7].name"), "past the last");
});

Run("Status: a call of what cannot be called", () =>
{
    var (ctx, _) = StatusShelf();
    Eq("none: root(1) is not a function", Said(ctx, "root(1)"), "a node is not a function");
});

Run("Status: pending stays pending through the steps and conditions after it", () =>
{
    var (ctx, _) = StatusShelf();
    Eq("pending: the thing asked for not produced yet", Said(ctx, "later()"), "the hook's own status");
    Eq("pending: the thing asked for not produced yet", Said(ctx, "later().volume_ml"), "a member of it: still pending, not no member");
    Eq("pending: the thing asked for not produced yet", Said(ctx, "later() > 2 && true"), "a condition on it: not known yet");
    Eq("pending: the thing asked for not produced yet", Said(ctx, "!later()"), "nor its not");
});

Console.WriteLine($"\nResults: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Log
{
    public readonly List<string> Ran = [];
}

[CommandSet]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
public abstract partial record TestCmd
{
    [Label("Add"), Description("Adds to the count.")]
    public sealed record Counter_IncrementBy([Description("How much to add.")] int Amount) : TestCmd;

    [Label("Reset"), Description("Sets the count to zero.")]
    public sealed record Counter_Reset : TestCmd;

    // No [Label], no [Description]: cmdgen warns (CMDGEN5), and the table makes do.
    public sealed record Counter_Set(int Value) : TestCmd;

    [Description("Reads the count.")]
    [ReadsOnly]
    public sealed record Counter_Peek : TestCmd;

    [Description("Not handled anywhere: cmdgen warns (CMDGEN4).")]
    public sealed record Counter_Unused : TestCmd;

    [Description("Carries a handle: its JSON is the handle's text.")]
    public sealed record Counter_Point(ItemId At) : TestCmd;
}

[JsonSerializable(typeof(TestCmd))]
partial class Json : JsonSerializerContext;

// A node set: what cmdgen writes for one (its kind names, its tables, an expression field) is tested
// above, over plain trees.
[NodeSet]
public static partial class Shelf
{
    [Description("A shelf: its lists, the rule that picks from them, and its frame.")]
    public sealed record Root(
        [Description("The lists.")] Lists? Lists,
        [Description("Which item to pick: an expression over the shelf, kept as written.")] Expr? Rule,
        [Description("How it is framed.")] Frame? Frame);

    [Description("The items, in order, and a title among them.")]
    public sealed record Lists(
        [Description("What the list is called: a field among its items.")] string Title,
        [Description("One item.")] [Many] Item Item);

    [Description("An item.")]
    public sealed record Item(
        [Description("What it is called.")] [Editable] string Name,
        [Description("How much it counts.")] [DefaultValue(1.0)] double Weight);

    [Description("How a shelf is framed: a field of each sort an editable field may be.")]
    public sealed record Frame(
        [Description("Its magnification.")] [Editable] [Range(0.5, 2, Step = 0.25)] [DefaultValue(1.0)] double Zoom,
        [Description("Its style, by name.")] [Editable] [Pick("frame_styles")] [DefaultValue("plain")] string Style,
        [Description("Its border: one of Frames.Edges.")] [Editable] [DefaultValue(Edge.Thin)] Edge Border,
        [Description("The item it frames.")] [Editable] ItemId Framed);
}

// An enum table: its rows are Edge's values (cmdgen writes Edge and EdgeNames from the first column).
public static class Frames
{
    [EnumTable("Edge")]
    public static readonly (string Name, string Label, int Width)[] Edges = [("thin", "Thin line", 1), ("thick", "Thick line", 3)];
}

// The tables test's row.
public readonly record struct NoteRow(string Text, long N);

// The model tests' count.
public readonly record struct CountRow(long N);

// The addresses test's keys: a shelf's, and one with a text, as a layer's own key may be.
public readonly record struct ShelfKey(int Id);
public readonly record struct NamedKey(string Name, long Floor);

// The Places test's layer: libraries by id, each its books, a ring of loans of its own, and a corner that
// may be there; and the lobby, the first library's corner. Only the queries are marked: cmdgen reaches the
// rest through their data's fields.
static class LibraryLayer
{
    [Place<ShelfKey, Library>("library", "A library, by its id.")]
    public static Library? Find(Dictionary<int, Library> libraries, in ShelfKey key) => libraries.GetValueOrDefault(key.Id);

    [Place<NoKey, Corner>("lobby", "The first library's corner.")]
    public static Corner? Lobby(Dictionary<int, Library> libraries) => libraries.GetValueOrDefault(1)?.Corner;
}

[Description("A library: its name, its books, its loans, the years it was open, and a corner.")]
public sealed class Library
{
    public string Name = "";
    public List<Book> Books = new();
    public LoanRing Loans = new();
    public long[] Years = [];
    public Corner? Corner;
}

public readonly record struct Book(string Title, long Pages, double Weight);

public sealed class Corner
{
    public string Label = "";
}

// A collection of the layer's own: what C# counts and indexes (a Count and an int indexer), no interface.
public sealed class LoanRing
{
    public int Count;
    public Book[] Items = new Book[4];
    public ref readonly Book this[int i] => ref Items[i];
}

// Kinds for the tests of a plain tree: a tree needs no schema, so any index will do; each named for Kids.
static class K
{
    public const ushort A = 100, B = 101, C = 102, D = 103, E = 104, X = 105, Under = 106, Keep = 107, Panel = 108, Point = 109, Note = 110;
    static readonly string[] Names = ["a", "b", "c", "d", "e", "x", "under", "keep", "panel", "point", "note"];
    public static string Name(ushort kind) => kind is >= A and <= Note ? Names[kind - A] : kind.ToString();
}
