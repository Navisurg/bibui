# Bibui.Commands

An app's model and the commands that change it. The model is a tree of nodes, each with a typed
row of facts; every change is a command record, pushed from wherever it starts (a button, a key,
a test port, an agent) and applied in one place, in the order pushed. Values in the model are read
through a small expression language and seen through lenses. Before the app compiles, cmdgen
writes what the declarations imply: typed ids, row tables, wire names, push methods and a table
of what each command is.

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using Bibui.Commands;

[NodeSet]
public static partial class Node
{
    public sealed record Root();

    [Description("A panel: a node shown through a lens.")]
    [In(typeof(Root), Many = true)]
    [Row(typeof(AppCmd.Panel_Close))]                 // the buttons a panel's row shows
    public sealed record Panel(NodeId Shows, Expr? Lens);
}

[CommandSet]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
public abstract partial record AppCmd                 // cmdgen makes it a Cmd
{
    [Description("Closes a panel.")]
    public sealed record Panel_Close(PanelId Panel) : AppCmd;
}

static void Apply(ModelStore store, Cmd cmd, Pushes push)   // the app's commands; the built-ins are the model's
{
    switch (cmd)
    {
        case AppCmd.Panel_Close(var panel): Nodes.Release(store, panel); break;
    }
}

var model = new Model(Apply, wake: runtime.RequestFrame);
model.Push(new AppCmd.Panel_Close(panel));   // applied at model.Run(), at the top of the next frame
```

- **Nodes.** A `[NodeSet]` declares the app's kinds of node as records. Each kind gets a typed id
  (`PanelId`), a record struct whose zero value is no node, so lookups return it instead of null.
  A node's facts are its row, in its kind's table: read with `id.RowFromId(store)` or a getter
  per field, written whole with `Modify`. `[In]` says where a kind may sit in the tree; the tree
  itself (`ModelStore`, edited through `Nodes`) holds only ids, kinds and links.
- **Commands.** A command is a record nested in an `abstract partial record` marked
  `[CommandSet]`. Its wire name comes from its type name (`Panel_Close` is `panel.close`, the
  `$cmd` its JSON carries). The library's own commands (`BuiltinCmd`: `node.set`, which sets
  one field of a node to the value of an expression, and `view_rule.set`) go through the same
  model, log and wire (`CmdJson.For`).
- **The `Model`** is the only writer of the tree. It queues what is pushed and applies it in
  `Run`, one command at a time; commands pushed while one is applied run in the same `Run`. A
  command that changed the tree publishes a generation, kept in a log with the command that
  made it (`TryEntry`), so an older generation can still be read.
- **Expressions and lenses.** `Expr` is a small language over everything the app exposes:
  the model's nodes and fields, and any `[Place]` an app registers (a cache, a computed layer).
  A lens is a function call that says how a value is seen (`board(it.case)`, `rows(it, …)`);
  a view rule says which lens a type, or one node, is seen through by default.
- **What a command is.** `[Description]` on the record and its arguments, `[Label]`, `[Short]`
  and `[Icon]` for where it is shown, and `[ReadsOnly]`. cmdgen copies them into a table,
  `<Set>Ops.All`: an `OpInfo` per command. Each `OpInfo` can also build its command from a
  `Context` (the nodes the code drawing it is about, by kind), so library code can show a
  node's row of commands for any app. Nothing is reflected.
- **Reading the model from outside.** `ModelJson` answers a node and what is under it, the
  command log, and the schema as JSON, for a test port or a CLI. `TextView` draws a value
  through its lenses as text.

cmdgen runs as a build step the package brings (`buildTransitive`), before the compiler, so
System.Text.Json's source generator sees the wire names it writes like hand-written ones. It
warns of a command no `case` handles (CMDGEN4), and of one with no `[Description]` (CMDGEN5).
