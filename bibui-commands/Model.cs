using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bibui.Commands;

/// <summary>
/// A command: the one way a model changes. The library's own (<see cref="BuiltinCmd"/>) and each app's
/// (its [CommandSet], a record deriving from this, cmdgen's table beside it) are one set to the model, its
/// log and the wire, told apart by their $cmd names (<see cref="CmdJson"/>). Not closed: a set is closed
/// in its own assembly, and cmdgen checks the app's against its apply.
/// </summary>
public abstract record Cmd;

/// <summary>
/// A model's one state change: <paramref name="cmd"/> applied to <paramref name="tree"/>, in place:
/// the only thing it writes, in the context it was pushed in (<paramref name="ctx"/>: which panel, which
/// case; its arguments say what to do). It may push follow-ups through <paramref name="push"/>; they run
/// in the same <see cref="Model{TCtx}.Run"/>, after this batch, in this command's context unless given
/// another. It is given the tree to write rather than the model, so it can be run on a tree that is not
/// the model's (a dry run on a fork, a replay, a test), and cannot reach the model's log or its published
/// generations. A command that must also act on the app's runtime is the rare one: the app's apply
/// captures what it needs, and says so where it does.
/// </summary>
public delegate void Apply<TCtx>(ModelStore tree, Cmd cmd, in TCtx ctx, Pushes<TCtx> push) where TCtx : struct;

/// <summary>
/// Where commands are pushed, and what they answer: what an apply is given for its follow-ups and
/// its output, and what <c>model.Cmds</c> pushes through (cmdgen's push methods:
/// <c>push.Cmds.PanelSpawn()</c>). Onto a model's queue, in the context on top of its stack (inside an
/// apply, the running command's), and its outputs; or into a list (a dry run, a test), in the context
/// it was made with, where an output goes nowhere.
/// </summary>
public readonly struct Pushes<TCtx> where TCtx : struct
{
    private readonly Model<TCtx>? _model;
    private readonly List<(Cmd Cmd, TCtx Ctx)>? _into;
    private readonly TCtx _ctx;   // the list's: what a push without one is queued with

    public Pushes(Model<TCtx> model) => _model = model;
    public Pushes(List<(Cmd Cmd, TCtx Ctx)> into, in TCtx ctx = default) => (_into, _ctx) = (into, ctx);

    /// <summary>Queues <paramref name="cmd"/> in the context on top: the running command's, inside an apply.</summary>
    public void Push(Cmd cmd)
    {
        if (_model is not null) _model.Push(cmd);
        else _into?.Add((cmd, _ctx));
    }

    /// <summary>Queues <paramref name="cmd"/> in <paramref name="ctx"/>: the running command's with a field changed (<c>ctx with { Node = x }</c>).</summary>
    public void Push(Cmd cmd, in TCtx ctx)
    {
        if (_model is not null) _model.Push(cmd, ctx);
        else _into?.Add((cmd, ctx));
    }

    /// <summary>
    /// What the command running answers, as text: a read's result (an eval's value), kept with the
    /// model's <see cref="Model{TCtx}.Outputs"/> until whoever asked takes them. A command has no return value
    /// (it is queued and runs later), so this is how a read answers its caller.
    /// </summary>
    public void Output(string text) => _model?.Outputs.Add(text);
}

/// <summary>
/// The commands every model runs itself, beside its app's: the library's, so an app neither declares
/// nor applies them (Model.Run runs them; the app's apply never sees one). Pushed as any command is
/// (<see cref="Model{TCtx}.SetViewRuleForType"/> is one), logged as any is, and on the wire by their $cmd names, beside
/// the app's (<see cref="CmdJson"/>): an app's command of the same name is a mistake its JSON refuses.
/// </summary>
public abstract record BuiltinCmd : Cmd
{
    private BuiltinCmd() { }

    /// <summary>
    /// Sets a view rule: a type's (a kind, case; a list of one, case[]; a field, frame.zoom), how every
    /// value of it is seen; or, given a node, that node's own, seen so before its type's rule. None removes
    /// it. Nothing for a node gone, a type the schema does not name, or a lens that is not a call of a lens the model's table has.
    /// </summary>
    public sealed record ViewRule_Set : BuiltinCmd
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ViewRuleType Type;  // a type's rule: the type; none when Node names one (not written)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public NodeId Node;        // a node's own rule: the node; none for a type's (not written)
        public Expr? Lens;         // none: the rule removed
    }

    /// <summary>
    /// Sets an editable field of a node, by its key (title, picked), to
    /// <see cref="Value"/>: an expression field to the expression; any other to its value, evaluated against
    /// the model alone with `it` the node (2, "text", $7, $7.title). Nothing for a node gone, a key that is no
    /// editable field, or a value none or not of its type (ModelEval.Set). The generic setter, for what knows
    /// a field only by its address (a drawer, an agent): code that knows the field pushes its typed command.
    /// </summary>
    public sealed record Node_Set : BuiltinCmd
    {
        public NodeId Node;        // whose field
        public string Key = "";    // which: a field of its kind
        public Expr? Value;        // what it becomes: an expression, its text on the wire
    }

    /// <summary>The built-in commands as a client lists them, beside the app's (a test port's schema): each its wire name and type.</summary>
    public static readonly OpInfo[] Ops =
    [
        new("view_rule.set", "Set view rule",
            "Sets a view rule: a type's (Type: case, case[], frame.zoom), how every value of it is seen; or a node's own (Node: #4), seen before its type's. No Lens removes it. Nothing for a node gone, a type the schema does not name, or a lens that is not a call of a lens.",
            OpKind.Command, OpFlags.None, typeof(ViewRule_Set),
            [new("Type", "string", "The type whose rule it is; none when Node is given."),
             new("Node", "NodeId", "The node whose own rule it is; none for a type's."),
             new("Lens", "Expr?", "A lens called over it (rows(it, it.title)); none removes the rule.")]),
        new("node.set", "Set field",
            "Sets an editable field of a node, by its key (title, picked): an expression field to the expression; any other to its value, evaluated against the model with it the node (2, \"text\", $7, $7.title). Nothing for a node gone, a key that is no editable field, or a value none or not of its type.",
            OpKind.Command, OpFlags.None, typeof(Node_Set),
            [new("Node", "NodeId", "The node whose field it is."),
             new("Key", "string", "The field's key: title, picked."),
             new("Value", "Expr", "An expression, its text: a literal (2, \"text\", true, $7) or over the model, it the node ($7.title).")]),
    ];
}

/// <summary>The built-in commands' JSON metadata, generated at compile time; read through <see cref="CmdJson"/>, with the app's.</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(Cmd))]
[JsonSerializable(typeof(BuiltinCmd.ViewRule_Set))]
[JsonSerializable(typeof(BuiltinCmd.Node_Set))]
public partial class BuiltinJson : JsonSerializerContext;

/// <summary>
/// Commands on the wire: one JSON type for <see cref="Cmd"/>, its $cmd naming the app's commands (its table,
/// cmdgen's {Set}Ops) and the library's (<see cref="BuiltinCmd.Ops"/>) alike. What a test port reads a
/// command with and a log writes one as. Made once, at startup.
/// </summary>
public static class CmdJson
{
    /// <summary>
    /// <see cref="Cmd"/> as JSON, read with <paramref name="app"/>'s options (its converters, its strictness)
    /// and its metadata, the built-ins' beside it, each command by its wire name in <paramref name="ops"/>
    /// or the library's. An app command with a built-in's name: InvalidOperationException, at startup.
    /// </summary>
    public static JsonTypeInfo<Cmd> For(JsonSerializerOptions app, IReadOnlyList<OpInfo> ops)
    {
        foreach (var op in ops)
            foreach (var builtin in BuiltinCmd.Ops)
                if (op.Wire == builtin.Wire) throw new InvalidOperationException($"{op.Type.Name} is {op.Wire}: the library's command of that name. Name it another.");
        var options = new JsonSerializerOptions(app)
        {
            IncludeFields = true,   // the built-ins' arguments are fields, whatever the app's options say
            TypeInfoResolver = JsonTypeInfoResolver.Combine(app.TypeInfoResolver, BuiltinJson.Default)
                .WithAddedModifier(info =>
                {
                    if (info.Type != typeof(Cmd)) return;
                    var poly = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$cmd" };
                    foreach (var op in ops) poly.DerivedTypes.Add(new JsonDerivedType(op.Type, op.Wire));
                    foreach (var op in BuiltinCmd.Ops) poly.DerivedTypes.Add(new JsonDerivedType(op.Type, op.Wire));
                    info.PolymorphismOptions = poly;
                }),
        };
        return (JsonTypeInfo<Cmd>)options.GetTypeInfo(typeof(Cmd));
    }
}

/// <summary>
/// One published generation, as the model's log holds it: the command that made it (the app's or the
/// library's) and the context it ran in, and the generation of the command that pushed that one (0 when
/// it came from outside: a key, a click, a client). What it changed is the tree's edits and the tables'
/// writes stamped with it, kept while the log holds it.
/// </summary>
public readonly record struct LogEntry<TCtx>(long Gen, long CauseGen, Cmd Cmd, TCtx Ctx) where TCtx : struct;

/// <summary>
/// An app's model: its tree of facts (<see cref="Tree"/>), and the one place it changes. Commands
/// are pushed from anywhere on the owner thread (the build, key bindings, a test port) and run
/// together, in the order pushed, when the loop calls <see cref="Run"/>, usually at the top of a
/// frame. A command that changed the model publishes a generation: logged with the command that made
/// it; the tables keep the row versions it wrote, and the tree what it did to the shape, for as long as
/// the log holds it. The tree is one, written in place. The app's runtime
/// (what it derives from the tree and keeps to draw it) is not here: the app holds the model, not
/// the other way round. One thread runs it; nothing here is shared.
/// <para>
/// It also keeps the context stack: what the code running now is about (which panel, which case: the
/// app's <typeparamref name="TCtx"/>, cmdgen's Context). Code says where it is in a scope
/// (<see cref="ContextScope"/>), and a command is queued with a copy of the top, as rad's
/// <c>rd_cmd</c> copies its registers. The model never reads a field of it: it stores, copies and hands
/// it back, so it needs nothing of <typeparamref name="TCtx"/> but that it is a value.
/// </para>
/// <code>
/// var ui = new Model&lt;Context&gt;(App.Apply, wake: runtime.RequestFrame, context: Context.Ops);
/// using (ui.ContextScope(ui.GetCtx() with { Panel = p }))
///     ui.Push(new AppCmd.Panel_Close());   // queued with { Panel = p }
/// ui.Run();                                // applied in that context, and published if it changed the tree
/// </code>
/// </summary>
public sealed class Model<TCtx> where TCtx : struct
{
    /// <summary>The tree, and through it the tables: read by anyone on the owner thread; written only by the apply, inside <see cref="Run"/>.</summary>
    public readonly ModelStore Tree;

    /// <summary>The lenses a view rule may call, checked as one is set (view_rule.set); null: any call. Set once, by the app, before the first Run.</summary>
    public LensTable? Lenses;

    /// <summary>The last published generation; 0 before any command changed the tree. Readers compare it.</summary>
    public long Gen;

    /// <summary>
    /// What the commands run answered, in the order they ran (<see cref="Pushes{TCtx}.Output"/>):
    /// written by the apply, inside <see cref="Run"/>; taken and cleared by whoever answers for them
    /// (a test port, once its answer is due). A command that only answers changes nothing, so it
    /// publishes no generation and the log does not keep it.
    /// </summary>
    public readonly List<string> Outputs = new();

    /// <summary>The app's functions over its context (cmdgen's Context.Ops): what a command lacks, and its context on the wire; null: none.</summary>
    public readonly ContextOps<TCtx>? Context;

    /// <summary>
    /// Whether a command is offered where code is now: none of the context it declares is missing from
    /// the top (<see cref="ContextOps{TCtx}.Missing"/>). Made once, so a row asking every frame makes no garbage.
    /// </summary>
    public readonly Func<OpInfo, bool> Offers;

    private readonly Apply<TCtx> _apply;
    private EvalContext? _writing;    // node.set's: the model alone, over Tree (ModelEval.WriteContext); made the first time one runs
    private readonly Action? _wake;   // asks the loop for a frame, so what is pushed gets run (runtime.RequestFrame)
    private readonly LogEntry<TCtx>[] _log;   // a ring: generation g at (g - 1) % length, while it is among the last length
    private List<(Cmd Cmd, TCtx Ctx, long Cause)> _pending = new(64), _running = new(64);   // each in its context, with the generation that pushed it
    private TCtx[] _stack = new TCtx[64];   // the context scopes open now; [0] the empty context, never popped
    private int _top;

    /// <summary>
    /// A model whose log keeps the last <paramref name="logLength"/> generations. <paramref name="wake"/>
    /// is called on every push. It starts from <paramref name="tree"/> (cmdgen's NodeKinds.NewTree: the
    /// root and the view rules the schema declares), or an empty one. <paramref name="context"/> is the
    /// app's context functions (cmdgen's Context.Ops).
    /// </summary>
    public Model(Apply<TCtx> apply, Action? wake = null, int logLength = 256, ModelStore? tree = null, ContextOps<TCtx>? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logLength, 1);
        (_apply, _wake, _log, Context) = (apply, wake, new LogEntry<TCtx>[logLength], context);
        Tree = tree ?? Nodes.NewTree(gen: 1);
        Offers = op => Context?.Missing(_stack[_top], op) is null;
    }

    /// <summary>Commands are pushed and not yet run: what a test port waits for before it answers.</summary>
    public bool HasPending => _pending.Count > 0;

    //- the context

    /// <summary>
    /// The context on top: what the code running now is about. A copy, as it is when called: read it
    /// where it is used (<c>model.GetCtx() with { … }</c> inside <see cref="ContextScope"/>), never kept
    /// across a scope, or a nested scope built from it drops what the outer one set.
    /// </summary>
    public TCtx GetCtx() => _stack[_top];

    /// <summary>
    /// <paramref name="ctx"/> on top until the scope is disposed: the code inside it, and the commands it
    /// pushes, are in it. Only in a <c>using</c>, so scopes end in the order they began.
    /// </summary>
    public Scope ContextScope(in TCtx ctx)
    {
        if (++_top == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
        _stack[_top] = ctx;
        return new(this);
    }

    /// <summary>A context scope: on top until disposed. Only in a <c>using</c>.</summary>
    public readonly ref struct Scope
    {
        private readonly Model<TCtx> _model;
        internal Scope(Model<TCtx> model) => _model = model;
        public void Dispose() => _model._top--;
    }

    //- pushing

    /// <summary>Queues <paramref name="cmd"/> for the next <see cref="Run"/> in the context on top, and wakes the loop for it.</summary>
    public void Push(Cmd cmd) => Push(cmd, _stack[_top]);

    /// <summary>Queues <paramref name="cmd"/> for the next <see cref="Run"/> in <paramref name="ctx"/> (a client's, read from the wire; a follow-up's, changed), and wakes the loop for it.</summary>
    public void Push(Cmd cmd, in TCtx ctx)
    {
        _pending.Add((cmd, ctx, 0));
        _wake?.Invoke();
    }

    /// <summary>
    /// Queues the built-in command that sets the view rule for <paramref name="type"/> (a kind, case;
    /// a list of one, case[]; a field, frame.zoom): every value of it seen through <paramref name="lens"/>,
    /// a lens called over it; none, no rule. Run with the app's commands, in the order pushed, and logged.
    /// </summary>
    public void SetViewRuleForType(ViewRuleType type, Expr? lens) => Push(new BuiltinCmd.ViewRule_Set { Type = type, Lens = lens });

    /// <summary>Queues the built-in command that sets <paramref name="node"/>'s own view rule: seen through <paramref name="lens"/> before its type's; none, its type's again.</summary>
    public void SetViewRuleForNode(NodeId node, Expr? lens) => Push(new BuiltinCmd.ViewRule_Set { Node = node, Lens = lens });

    /// <summary>Queues the built-in node.set: <paramref name="node"/>'s field <paramref name="key"/> set to <paramref name="value"/> (a literal, Exprs.Lit, from a drawer; any expression over the model).</summary>
    public void SetField(NodeId node, string key, Expr value) => Push(new BuiltinCmd.Node_Set { Node = node, Key = key, Value = value });

    /// <summary>
    /// Pushes <paramref name="cmd"/> and runs it now, with whatever was pushed before it: for code
    /// that must see its effect in the same frame, such as a drag the layout follows: the drag writes
    /// the model as it moves, rather than keeping a shadow copy until release. NOTE(k): the tree is
    /// written in place, so never call this from inside a loop over it.
    /// </summary>
    public void Do(Cmd cmd)
    {
        Push(cmd);
        Run();
    }

    //- running

    /// <summary>
    /// Runs what is pushed, one command at a time, in the order pushed, each in its context: on top of the
    /// stack while it runs, as rad installs a command's registers, and given to the apply. Each command
    /// that changed the tree publishes a generation. Commands pushed while one runs (its follow-ups) run in
    /// the same Run, after the batch they were pushed in, and remember which generation pushed them;
    /// a follow-up of a command that changed nothing inherits that command's cause.
    /// </summary>
    public void Run()
    {
        while (_pending.Count > 0)
        {
            (_pending, _running) = (_running, _pending);
            for (int i = 0; i < _running.Count; i++)
            {
                var (cmd, ctx, cause) = _running[i];
                int pushedBefore = _pending.Count;
                long changes = Tree.Tree.Changes + Tree.Clock.Changes;   // the tree's edits and the tables' writes
                using (ContextScope(ctx))
                {
                    // The library's commands, the app's never seeing them; then the app's.
                    switch (cmd)
                    {
                        case BuiltinCmd.ViewRule_Set { Node.IsNone: true } set: ModelEval.SetViewRuleForType(Tree, Lenses, set.Type, set.Lens); break;
                        case BuiltinCmd.ViewRule_Set set: ModelEval.SetViewRuleForNode(Tree, Lenses, set.Node, set.Lens); break;
                        case BuiltinCmd.Node_Set set when Tree.Schema is { } schema:
                            ModelEval.Set(Tree, _writing ??= ModelEval.WriteContext(Tree, schema), set.Node, set.Key, set.Value);
                            break;
                        case BuiltinCmd.Node_Set: break;   // a tree without a schema has no fields
                        default: _apply(Tree, cmd, ctx, new Pushes<TCtx>(this)); break;
                    }
                }
                long by = cause;
                if (Tree.Tree.Changes + Tree.Clock.Changes != changes) by = Publish(cmd, ctx, cause);
                for (int k = pushedBefore; k < _pending.Count; k++) _pending[k] = _pending[k] with { Cause = by };
            }
            _running.Clear();
        }
    }

    // Logs the generation written, and opens the next: what no generation the log still holds reads
    // (row versions, edits of the shape) is let go.
    private long Publish(Cmd cmd, in TCtx ctx, long cause)
    {
        Gen = Tree.Clock.Gen;
        _log[(Gen - 1) % _log.Length] = new LogEntry<TCtx>(Gen, cause, cmd, ctx);
        Tree.Clock.Gen = Gen + 1;
        Nodes.Retire(Tree, OldestGen);
        return Gen;
    }

    //- reading the log

    /// <summary>The oldest generation the log still holds; above <see cref="Gen"/> when it holds none.</summary>
    public long OldestGen => Math.Max(1, Gen - _log.Length + 1);

    /// <summary>Generation <paramref name="gen"/>'s entry, if the log still holds it.</summary>
    public bool TryEntry(long gen, out LogEntry<TCtx> entry)
    {
        bool held = gen >= OldestGen && gen <= Gen;
        entry = held ? _log[(gen - 1) % _log.Length] : default;
        return held;
    }
}
