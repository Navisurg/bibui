using System.Runtime.CompilerServices;

namespace Bibui.Commands;

// Evaluation: one small expression language over everything an app holds, whoever holds it. The language knows nothing of any of it: an expression names things (Name),
// steps from a thing to a part of it (Member, Index), calls (Call), and compares what it finds (Not,
// Binary: a condition, never arithmetic). What a name is, and what stepping from a value means, is the value's type's,
// through its hooks (EvalType's Member, Index, Count, Call, Read), and a type comes from whoever owns the
// state: the model's types read the tree through its schema (ModelEval), a cache's read the cache, a
// database client's would ask the database. Nothing is checked at run time but state: a type with no
// hook for a step answers None, as a node gone does.
//
// A lens is a call too: `bitmap(x, w, h)` evaluates x and gives it back as the same value with a lensed
// type, which records the lens and its arguments past x as written; a lens over a lens keeps both. So
// how a value is to be seen travels with the value, and the reader decides what to do with it. A type
// may have a view rule: a lens call over `it`, applied to every value of that type an evaluation gives,
// so the value is seen through it by whoever reads it; raw(x) is x without its view rule. Evaluating never shows anything: what a lens draws
// is the UI's, found by the lens's name (Lenses.cs).
//
// A step to a field gives its address, not its value: where the value lives, as its type's Read reads
// it; whatever needs the value (an operator, a function's hook, printing) reads through it. Stepping
// therefore reads nothing until a leaf is needed, and the result still says where it came from. An
// address names a place once and for all, so it can be kept, written as a literal ($15), and given to
// a command that writes there.
//
// A part is evaluation too: a set's part i is what of[i] evaluates to
// (Eval.At), seen through its view rule as any value is. There is no other way to a part: a reader
// counts (Eval.Count) and asks for each, and evaluates what it wants of it (a table's columns, a
// tree's parent) with `it` bound to it.
//
// Evaluating reads; it never changes state. A change is a command, which may take what an
// evaluation found (a node's handle, a field's address) as an argument.

/// <summary>
/// What to evaluate, as data: built in code, never parsed (a text form, printed by
/// <see cref="Exprs.Text"/>, is a serialization for people and clients). Built once and evaluated
/// as often as wanted.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ExprJson))]
public abstract record Expr
{
    private Expr() { }

    /// <summary>Its text (<see cref="Exprs.Text"/>), in a command's or a log's print.</summary>
    public sealed override string ToString() => Exprs.Text(this);

    /// <summary>A name: what <see cref="EvalContext"/> says it is (root, a cache, a function, a lens).</summary>
    public sealed record Name(string Id) : Expr;
    /// <summary><c>it</c>: the part being expanded, while a set is (a column of a table); none anywhere else.</summary>
    public sealed record It : Expr;
    /// <summary><c>of.key</c>: a part of a value, by its name, as its type's <see cref="EvalType.Member"/> says.</summary>
    public sealed record Member(Expr Of, string Key) : Expr;
    /// <summary><c>of[at]</c>: a part of a value, by a value (a number, a handle), as its type's <see cref="EvalType.Index"/> says.</summary>
    public sealed record Index(Expr Of, Expr At) : Expr;
    /// <summary><c>callee(args)</c>: a function's result, or a lens over its argument.</summary>
    public sealed record Call(Expr Callee, Expr[] Args) : Expr;
    /// <summary>A value as it is: a number, a text; an address ($15), found in the tree being evaluated by whoever owns its space (<see cref="EvalContext.Addresses"/>).</summary>
    public sealed record Lit(Value Value) : Expr;
    public sealed record Not(Expr Of) : Expr;
    /// <summary>A comparison or a condition: what a column, a filter or a check asks of what it found.</summary>
    public sealed record Binary(BinaryOp Op, Expr Left, Expr Right) : Expr;
}

public enum BinaryOp : byte { Eq, NotEq, Less, LessEq, Greater, GreaterEq, And, Or }

/// <summary>
/// What a value is, at the language's level: what can be done with it. A primitive (Int, Real, Bool,
/// Text) is compared and printed; an Address is read through; Parts is stepped into, its parts got by
/// name (Member) or by place (Index), counted by Count: a list, a model node, a record, a range of
/// numbers alike, so a reader handles every collection one way; a Function is called; a LensFunc is called too, keeping its arguments past the first
/// as written (a lens: Lenses.cs); Opaque is only printed (an expression, a text buffer). A value seen
/// through a lens has the kind of what it shows. Which parts, and where they come from, is the type's
/// hooks'; whether they are named (PartName) or alike (PartType) is how it is drawn by default.
/// </summary>
/// <summary>
/// Whether a value is what was asked for, set where the value is made. Produced: it is, and a none
/// that is Produced means nothing is there (a field never written). Pending: asked for and not made
/// yet; its Holder is what is being made. The rest say why an expression found nothing, its Holder the
/// part of the expression that failed. A step from a value that is not Produced gives it as it is, so
/// an expression says why its first step failed.
/// </summary>
public enum Status : byte { Produced, Pending, UnknownName, NoMember, NoPart, NotCallable, NodeGone }

public enum EvalKind : byte { None, Int, Real, Bool, Text, Parts, Function, LensFunc, Address, Opaque }

/// <summary>
/// A result: what it is (<see cref="Type"/>), and where it is or what it holds, read only by its
/// type's hooks: a value is a location plus the type that knows how to read it, never the data itself. Read as "in <see cref="Holder"/>, at
/// <see cref="At"/>, while it is still <see cref="Id"/>": a model node is its tree, its slot and its
/// id; a struct record, the list holding it and its index there; a class record, the object itself.
/// A number is in <see cref="At"/> (an int, a bool) or <see cref="Real"/>, a text in
/// <see cref="Holder"/>. <c>default</c> is None: nothing there (a node gone, a key never written, a
/// step its type has no hook for).
/// </summary>
public readonly record struct Value(EvalType? Type, long At, long Id, double Real, object? Holder)
{
    public int Address { get; init; }    // where it is, when it is somewhere: an address in the evaluator's table (Addresses), in some Place; 0 for none
    public string? Text { get; init; }   // a text's: itself
    public Status Status { get; init; }  // whether it is what was asked for (Produced), or why not; Holder: what that is about

    public static readonly Value None = default;
    public bool IsNone => Type is null;
    public EvalKind Kind => Type?.Kind ?? EvalKind.None;

    public static Value Of(long n) => new(EvalTypes.Int, n, 0, 0, null);
    public static Value Of(double x) => new(EvalTypes.Real, 0, 0, x, null);
    public static Value Of(bool b) => new(EvalTypes.Bool, b ? 1 : 0, 0, 0, null);
    public static Value Of(string s) => new(EvalTypes.Text, 0, 0, 0, null) { Text = s };
    public static Value Of(NodeId id) => new(EvalTypes.Node, 0, id.Value, 0, null);
    public static Value Of(Expr? e) => new(EvalTypes.Expression, 0, 0, 0, e);
    public static Value OfObject(object? o) => new(EvalTypes.Object, 0, 0, 0, o);

    public long AsInt => Kind == EvalKind.Int || Kind == EvalKind.Bool ? At : Kind == EvalKind.Real ? (long)Real : 0;
    public double AsReal => Kind == EvalKind.Real ? Real : Kind == EvalKind.Int ? At : 0;
    public bool AsBool => Kind switch { EvalKind.Bool or EvalKind.Int => At != 0, EvalKind.None => false, EvalKind.Real => Real != 0, _ => true };
    public string AsText => Kind == EvalKind.Text ? Text ?? "" : "";

    /// <summary>
    /// The object this value holds, if it is a <typeparamref name="T"/> (a class record, a cache's
    /// entry, an expression); null for anything else, and for None. What code that is handed a value
    /// asks for in place of casting <see cref="Holder"/>: <c>shown.As&lt;Vessels&gt;()</c>. A struct
    /// record is not held as itself (its holder is the list it is in), so it is read through its type.
    /// </summary>
    public T? As<T>() where T : class => Holder as T;
}

// The hooks a type may have: each takes the value stepped from. Delegates, not interfaces: a type is
// a row of functions, filled by whoever makes the type, any left null meaning "no such step".
public delegate Value MemberHook(in Value of, string name);
public delegate Value IndexHook(in Value of, in Value at);
public delegate long CountHook(in Value of);
/// <summary>A function's result from its arguments, evaluated; <paramref name="call"/> is the call as written, for a hook that keeps it (a lens keeps its arguments unevaluated, so they can be evaluated later against the value it shows).</summary>
public delegate Value CallHook(in Value callee, ReadOnlySpan<Value> args, Expr.Call call);
public delegate string TextHook(in Value of);
/// <summary>The value an address holds now: the one place an address is turned into data.</summary>
public delegate Value ReadHook(in Value at);

/// <summary>
/// A type: its name and what it is, and the steps from a value of it, as hooks.
/// Made by whoever owns the values; made once, not per value.
/// </summary>
public sealed class EvalType
{
    public required string Name;
    public string Description = "";

    // --- The language: what Eval.Run steps through, one hook per kind of Expr. Kind says how a value
    // of it compares and whether a call evaluates all its arguments (a lens: only the first); Read is
    // what Eval.Load reads an address through. A null hook is "no such step": the step gives None.
    // How a value of it is seen is not the type's: it is its view rule, the context's (EvalContext.ViewRules). ---
    public EvalKind Kind;
    public MemberHook? Member;   // of.name
    public IndexHook? Index;     // of[at]: a set's part at a place (0 to Count - 1), a thing's by its place among its parts
    public CountHook? Count;     // how many parts a set has (count(x))
    public CallHook? Call;       // a function's body; a lens's wrapping of its argument
    public ReadHook? Read;       // an address's: the value there now

    // --- Shape: how a thing or a set is laid out, for whatever lays it out. Parts with a PartName are
    // drawn as fields, named by it; parts alike as a table, a column per field of their PartType. A
    // value somewhere (Value.Address) whose type has Members or a PartType and no hook for the step is
    // stepped by the evaluator itself, with no hook code per type: of.name to the member's address,
    // of[i] to the element's, count(of) and a leaf read by its Place's reader (Addresses, Places). ---
    public (string Name, EvalType Type)[]? Members;   // a thing's members, in order: what of.name steps to by shape
    public EvalType? PartType;              // a set's: the type of every part, when they share one
    public string[] Fields = [];            // a thing's fields, by name, when fixed (a kind's, a record's): a table's default columns over a set of it
    public Func<long, string>? PartName;    // what its part at a place is called, when its parts are named (a node's members)
    public Expr[]? FieldColumns;            // a table's default columns over parts of this type: it.<field> per field, made the first time a table asks (Lenses.Columns)

    // --- Display: how a value is printed in a line (Exprs.ValueText) and named as a table's row. ---
    public TextHook? Text;       // how it is printed, when not by its kind
    public TextHook? Label;      // how it is named in a word, as a table's row (a node: its kind and handle); none: by its place

    // --- Editing: what a person or an agent may change by its address (node.set), and to what.
    // An editable address is drawn with the edit lens, or the pick lens when it has Choices. ---
    public bool Editable;                   // an address's: a person or an agent may set it (node.set), so it is shown with the edit lens
    public Value Choices;                   // an editable address's, when what it may be set to is a set: made once, with the type, read a window at
                                            // a part at a time (Count, Eval.At); one with no space of its own (Holder null: every node of a kind) is read in the
                                            // space its field is (Lenses.ChoicesOf). None when it has none
    public string[]? ChoiceLabels;          // Choices' parts, in their order, as a person reads them (an enum table's Label column); null: drawn as they are

    // --- Lens: set only on the type of a value seen through a lens, made by Lenses.Apply: the lens,
    // what it shows, its call. Such a type has the kind of what it shows, and the language's steps
    // resolved when it is made (a data lens's own, else what it shows'); shape, display and editing
    // are the bare type's (EvalTypes.Bare). ---
    public Lens? Lens;
}

/// <summary>The types every evaluation has: the operators' values, and lenses.</summary>
public static class EvalTypes
{
    public static readonly EvalType Int = new() { Name = "int", Kind = EvalKind.Int };
    public static readonly EvalType Real = new() { Name = "real", Kind = EvalKind.Real };
    public static readonly EvalType Bool = new() { Name = "bool", Kind = EvalKind.Bool };
    public static readonly EvalType Text = new() { Name = "text", Kind = EvalKind.Text };

    /// <summary>A node, by its id (a field naming one, an address literal $15): read through, the node it names in the model being evaluated (ModelEval).</summary>
    public static readonly EvalType Node = new() { Name = "node", Kind = EvalKind.Address, Text = (in Value v) => $"${v.Id}" };
    /// <summary>An expression held as a value (a panel's lens): printed as written, not evaluated.</summary>
    public static readonly EvalType Expression = new() { Name = "expr", Kind = EvalKind.Opaque, Text = (in Value v) => v.Holder is Expr e ? Exprs.Text(e) : "none" };
    /// <summary>An object held as a value (a list in a row, a text buffer): as it is.</summary>
    public static readonly EvalType Object = new() { Name = "object", Kind = EvalKind.Opaque, Text = (in Value v) => v.Holder is System.Collections.ICollection c ? $"{c.Count} items" : v.Holder?.ToString() ?? "none" };

    /// <summary>A function value: calling it runs <paramref name="call"/>.</summary>
    public static Value Function(string name, string description, CallHook call) =>
        new(new EvalType { Name = name, Description = description, Kind = EvalKind.Function, Call = call }, 0, 0, 0, null);

    /// <summary>The type a value is, under every lens over it.</summary>
    public static EvalType? Bare(EvalType? t)
    {
        while (t?.Lens is { } lens) t = lens.Inner;
        return t;
    }
}

/// <summary>
/// What an evaluation sees: its symbol table, what each name in an expression means (Symbols); how an
/// address literal finds what it names (AddressLiterals); the Places it may enter and the addresses
/// within them (Places, Addresses); the view rules (ViewRules, NodeViewRules); and the evaluation's own
/// state (DynamicVar, Viewing). Filled by the layers that own the state, each adding its own: the language its builtin
/// functions, the model its root and its handles, a layer its Places, the lens table its lenses.
/// Nothing else is reachable: what an app lets be evaluated is what it named.
/// </summary>
public sealed class EvalContext
{
    /// <summary>The symbol table: each name an expression may use, a function that says what it names now (so a name over the model reads the tree as it is when evaluated).</summary>
    public readonly Dictionary<string, Func<Value>> Symbols = new();

    /// <summary>
    /// Address literals, by their type: how the space that owns them finds what one names in the
    /// world these names see (the model: the node in the tree being evaluated, or none).
    /// </summary>
    public readonly Dictionary<EvalType, Func<Value, Value>> AddressLiterals = new();

    /// <summary>
    /// What <c>it</c> names now: a dynamically scoped variable, bound by whoever evaluates over a value
    /// and put back after, so a nested evaluation over another value never leaves `it` changed: a part while a table's columns
    /// are evaluated over it, a value while its view rule is applied, a
    /// field while its lens's arguments are (<see cref="Lenses.Arg"/>), a panel's node while its lens is.
    /// </summary>
    public Value DynamicVar;

    /// <summary>
    /// The view rules: by type, how every value of it is seen, an expression over it (it), almost always
    /// a lens call, applied when it is evaluated (Eval.View), so every reader sees it: a case as card(it),
    /// a board's zoom as range(it, 0.25, 4). Kept in the evaluation's context, not on the types, which
    /// every context shares: two contexts may see the same type differently. Filled by the layers (a
    /// field's own, from its schema: ModelEval.AddSymbols) and by the app where it hooks up its layers
    /// (its own choices). One table for both: a field's type is its own (board.zoom, not every double), so
    /// a view rule by type is a view rule of that one field, with no separate table keyed by field.
    /// </summary>
    public readonly Dictionary<EvalType, Expr> ViewRules = new();

    /// <summary>
    /// A value's own view rule, before its type's: the value of that type with that id (a model node's own
    /// rule: one case seen otherwise than the rest); the more specific rule wins. Keyed by the type too, so a node's rule is not its sets' (they carry its id). Filled as ViewRules is.
    /// </summary>
    public readonly Dictionary<(EvalType Type, long Id), Expr> NodeViewRules = new();

    /// <summary>The Places it may enter, by id - 1 (Places.Add): registered at startup, kept.</summary>
    public readonly List<PlaceInfo> Places = new();

    /// <summary>The addresses of the generation being evaluated (Addresses): where every value somewhere is, by id, within the Places.</summary>
    public readonly AddressTable Addresses = new();

    /// <summary>The model this context reads, when it reads one: its types, its published tree, and its Place (ModelEval.AddSymbols).</summary>
    public ModelRoot? Model;

    /// <summary>The types whose view is being applied now: one is not applied again inside itself (a view rule over `it` that evaluates to the same type would otherwise recurse forever).</summary>
    public readonly List<EvalType> Viewing = new();

    // A name added twice is a mistake (two layers claiming one name, a lens named as a function): Add throws.
    public void Add(string name, Func<Value> value) => Symbols.Add(name, value);
    public void Add(string name, Value value) => Symbols.Add(name, () => value);
}

public static class Eval
{
    /// <summary>
    /// The context evaluations on this thread are in: what a
    /// type's hooks, given only values, read through (its places, its model). Set by the evaluator's entry
    /// points, and by an app once a frame (Select) for what it reads outside an evaluation.
    /// </summary>
    [ThreadStatic] public static EvalContext? Current;

    /// <summary>Selects <paramref name="ctx"/> as this thread's context.</summary>
    public static void Select(EvalContext ctx) => Current = ctx;

    /// <summary>The language's own functions' names: what code calls them by (Exprs.Call(Eval.CountName, …)).</summary>
    public const string CountName = "count", RawName = "raw";

    // raw(x): x without its view rule. Eval.Run evaluates it itself, since it must stop the view rule
    // that evaluating x would apply; it has no body.
    static readonly EvalType Raw = new() { Name = RawName, Description = "A value as it is, without its view rule.", Kind = EvalKind.Function };

    /// <summary>The language's own functions, as symbols: count(set), raw(x); and places, the Places it may enter.</summary>
    public static void AddBuiltinFunctionSymbols(EvalContext ctx)
    {
        Places.AddListing(ctx);
        ctx.Add(CountName, EvalTypes.Function(CountName, "How many parts a set has.",
            (in Value _, ReadOnlySpan<Value> args, Expr.Call _) => args.Length == 1 ? Value.Of(Count(args[0])) : Value.None));
        ctx.Add(RawName, new Value(Raw, 0, 0, 0, null));
    }

    /// <summary>
    /// <paramref name="e"/>'s value, with <paramref name="ctx"/> as what its names mean, seen through
    /// its view rule (<see cref="View"/>). Total: None where there is nothing.
    /// </summary>
    public static Value Run(Expr e, EvalContext ctx)
    {
        Current = ctx;   // hooks are given values, not contexts: they read the selected one
        var v = Step(e, ctx, out bool raw);
        if (v.Status != Status.Produced && v.Holder is null) v = v with { Holder = e };   // why is the step's or its hook's; where, this
        return raw || e is Expr.It or Expr.Not or Expr.Binary ? v : View(v, ctx);
    }

    /// <summary>
    /// <paramref name="v"/> seen through its view rule: the view's lens call evaluated with `it` as
    /// <paramref name="v"/>; as it is when its type has none, while that view is being applied (a view
    /// over a value of its own type), or when the view gives nothing.
    /// </summary>
    public static Value View(in Value v, EvalContext ctx)
    {
        if (v.Type is not { } type || ctx.Viewing.Contains(type)) return v;
        if (!(ctx.NodeViewRules.Count > 0 && ctx.NodeViewRules.TryGetValue((type, v.Id), out var view)) && !ctx.ViewRules.TryGetValue(type, out view)) return v;
        ctx.Viewing.Add(type);
        var outer = ctx.DynamicVar;
        ctx.DynamicVar = v;
        var seen = Run(view, ctx);
        ctx.DynamicVar = outer;
        ctx.Viewing.Remove(type);
        return seen.IsNone ? v : seen;
    }

    // One step of an expression, its parts evaluated (and seen through their views) by Run. raw says
    // the result is not to be seen through its view either: raw(x).
    static Value Step(Expr e, EvalContext ctx, out bool raw)
    {
        raw = false;
        switch (e)
        {
            case Expr.Lit lit:
                return lit.Value.Type is { } literal && ctx.AddressLiterals.TryGetValue(literal, out var literalOf) ? literalOf(lit.Value) : lit.Value;
            case Expr.It: return ctx.DynamicVar;
            case Expr.Name name: return ctx.Symbols.TryGetValue(name.Id, out var named) ? named() : Value.None with { Status = Status.UnknownName };
            case Expr.Member m:
            {
                var of = Load(Run(m.Of, ctx));   // a none, as it came; else its type's hook or shape, which say NoMember for a key it has not
                return of.IsNone ? of : of.Type!.Member is { } member ? member(of, m.Key) : MemberStep(of, m.Key);
            }
            case Expr.Index ix:
            {
                var of = Run(ix.Of, ctx);
                if (of.IsNone) return of;
                var part = IndexStep(of, Load(Run(ix.At, ctx)));
                return part is { IsNone: true, Status: Status.Produced } ? Value.None with { Status = Status.NoPart } : part;
            }
            case Expr.Call call:
            {
                var callee = Run(call.Callee, ctx);
                if (callee.Type == Raw)
                {
                    raw = true;
                    return call.Args.Length == 1 ? Step(call.Args[0], ctx, out _) : Value.None;
                }
                if (callee.Type?.Call is not { } body) return callee.IsNone ? callee : Value.None with { Status = Status.NotCallable };
                var args = new Args();
                // A lens evaluates only what it shows; the rest are its own, kept as written.
                int n = Math.Min(callee.Kind == EvalKind.LensFunc ? Math.Min(call.Args.Length, 1) : call.Args.Length, Args.Size);
                for (int i = 0; i < n; i++) args[i] = Run(call.Args[i], ctx);
                return body(callee, ((Span<Value>)args)[..n], call);
            }
            // A side not Produced is the answer: not known yet. && and || stop at the left when it
            // decides, so the right may be a step that only the left makes safe to take.
            case Expr.Not not:
            {
                var of = Load(Run(not.Of, ctx));
                return of.Status != Status.Produced ? of : Value.Of(!of.AsBool);
            }
            case Expr.Binary b:
            {
                var left = Load(Run(b.Left, ctx));
                if (left.Status != Status.Produced) return left;
                if (b.Op is BinaryOp.And or BinaryOp.Or && left.AsBool == (b.Op == BinaryOp.Or)) return Value.Of(left.AsBool);
                var right = Load(Run(b.Right, ctx));
                return right.Status != Status.Produced ? right : b.Op is BinaryOp.And or BinaryOp.Or ? Value.Of(right.AsBool) : Compare(b.Op, left, right);
            }
        }
        return Value.None;
    }

    /// <summary>The value an address holds now; any other value as it is. For a function's hook that needs the value of an argument.</summary>
    public static Value Load(in Value v) => v.Type is { Kind: EvalKind.Address, Read: { } read } ? read(v) : v;

    /// <summary>
    /// The value of <c>of[i]</c>: its part at <paramref name="i"/>, seen through its view rule, as any
    /// evaluation of <c>of[i]</c> is. The one way a reader gets a
    /// part: a board's, a table's rows, a tree's paths; none past the last.
    /// </summary>
    public static Value At(in Value of, long i, EvalContext ctx)
    {
        Current = ctx;
        return View(IndexStep(of, Value.Of(i)), ctx);
    }

    /// <summary>How many parts <paramref name="of"/> has (count(x)): 0 for anything that has none.</summary>
    public static long Count(in Value of)
    {
        var v = Load(of);
        if (v.Type?.Count is { } count) return count(v);
        if (v.Address == 0 || Current is not { } ctx) return 0;
        var shape = EvalTypes.Bare(v.Type);
        if (shape?.PartType is not null) return Addresses.Read(ctx, Addresses.Child(ctx.Addresses, v.Address, new Step(StepKind.Count, 0))).AsInt;   // a set, by shape: its Place's reader counts
        return shape?.Members?.Length ?? 0;   // a thing, by shape: its members
    }

    /// <summary>The value of <c>of.name</c>, as the language steps it: its type's hook, else by its shape; not yet seen through its view rule.</summary>
    public static Value Member(in Value of, string name, EvalContext ctx)
    {
        Current = ctx;
        var v = Load(of);
        return v.Type?.Member is { } member ? member(v, name) : MemberStep(v, name);
    }

    /// <summary>
    /// <c>of.name</c> by shape: the member's address, its value read when it is a leaf (a number, a text, a
    /// node's id, an expression: anything not laid out as parts); none for a name its type has not, or a value
    /// nowhere. What a type's member hook gives a name it does not handle itself (a model node: its fields).
    /// </summary>
    public static Value MemberStep(in Value of, string name)
    {
        if (of.Address == 0 || EvalTypes.Bare(of.Type)?.Members is not { } members || Current is not { } ctx) return Value.None;
        for (int i = 0; i < members.Length; i++)
            if (members[i].Name == name) return ValueAt(ctx, members[i].Type, Addresses.Child(ctx.Addresses, of.Address, new Step(StepKind.Member, i)));
        return Value.None with { Status = Status.NoMember };   // a shape that has no such member
    }

    // What is at an address of type t: a leaf (anything not laid out as parts: a number, a text, a node's id)
    // read now, by its Place's reader, typed as the member it is (a field's own type: its view rule, whether it
    // is editable) and kept where it is (its address: what a write finds the node and key by); anything else,
    // the address itself.
    // An address-kind member (a reference: a model node's handle field) is not read here: Load reads it
    // through its type, to what it names.
    static Value ValueAt(EvalContext ctx, EvalType t, int address) =>
        t.Members is null && t.PartType is null && t.Kind != EvalKind.Address
            ? Addresses.Read(ctx, address) is { IsNone: false } leaf ? leaf with { Type = t, Address = address } : Value.None
            : new Value(t, 0, 0, 0, null) { Address = address };

    // of[at], of and at evaluated: the part its type's Index hook gives (a lensed type's: its lens's,
    // else what it shows'), not yet seen through its view: Run and At each apply that once.
    internal static Value IndexStep(in Value of, in Value at)
    {
        var v = Load(of);
        if (v.Type?.Index is { } index) return index(v, at);
        // by shape: a set's element, while it is one of its; a thing's member at that place among them
        if (v.Address == 0 || at is not { Kind: EvalKind.Int, At: >= 0 } || at.At >= Count(v)) return Value.None;
        var ctx = Current!;
        var shape = EvalTypes.Bare(v.Type);
        if (shape?.PartType is { } part) return ValueAt(ctx, part, Addresses.Child(ctx.Addresses, v.Address, new Step(StepKind.Element, at.At)));
        if (shape?.Members is { } members) return ValueAt(ctx, members[at.At].Type, Addresses.Child(ctx.Addresses, v.Address, new Step(StepKind.Member, at.At)));
        return Value.None;
    }

    // A comparison: equality of anything, order of numbers; none for an order of anything else.
    static Value Compare(BinaryOp op, in Value l, in Value r)
    {
        if (op is BinaryOp.Eq or BinaryOp.NotEq) return Value.Of(Same(l, r) == (op == BinaryOp.Eq));
        bool numbers = l.Kind is EvalKind.Int or EvalKind.Real && r.Kind is EvalKind.Int or EvalKind.Real;
        if (!numbers) return Value.None;
        int order = l.Kind == EvalKind.Real || r.Kind == EvalKind.Real ? l.AsReal.CompareTo(r.AsReal) : l.AsInt.CompareTo(r.AsInt);
        return Value.Of(op switch
        {
            BinaryOp.Less => order < 0, BinaryOp.LessEq => order <= 0,
            BinaryOp.Greater => order > 0, _ => order >= 0,
        });
    }

    // The same value: numbers by number; anything else by its type and where it is (a node by its
    // slot and id, whichever tree it was read from), so a lens over a value is the value.
    static bool Same(in Value a, in Value b)
    {
        var l = Load(a);
        var r = Load(b);
        if (l.Kind is EvalKind.Int or EvalKind.Real or EvalKind.Bool && r.Kind is EvalKind.Int or EvalKind.Real or EvalKind.Bool)
            return l.Kind == EvalKind.Real || r.Kind == EvalKind.Real ? l.AsReal == r.AsReal : l.AsInt == r.AsInt;
        if (l.Kind == EvalKind.Text && r.Kind == EvalKind.Text) return l.AsText == r.AsText;
        if (EvalTypes.Bare(l.Type) != EvalTypes.Bare(r.Type)) return false;
        if (l.IsNone) return true;
        return l.At == r.At && l.Id == r.Id && (l.Kind == EvalKind.Parts ? true : Equals(l.Holder, r.Holder));
    }

    // A call's arguments, evaluated, on the stack.
    [InlineArray(Size)]
    struct Args
    {
        public const int Size = 8;
        Value _0;
    }
}

/// <summary>Building expressions in code, and printing them and their values.</summary>
public static class Exprs
{
    public static Expr Name(string id) => new Expr.Name(id);
    public static readonly Expr It = new Expr.It();
    public static Expr Lit(long n) => new Expr.Lit(Value.Of(n));
    public static Expr Lit(double x) => new Expr.Lit(Value.Of(x));
    public static Expr Lit(string s) => new Expr.Lit(Value.Of(s));
    public static Expr Lit(in Value v) => new Expr.Lit(v);
    public static Expr Call(string name, params Expr[] args) => new Expr.Call(new Expr.Name(name), args);

    public static Expr Get(this Expr of, string name) => new Expr.Member(of, name);
    public static Expr At(this Expr of, long index) => new Expr.Index(of, Lit(index));
    public static Expr At(this Expr of, Expr at) => new Expr.Index(of, at);
    public static Expr Op(this Expr l, BinaryOp op, Expr r) => new Expr.Binary(op, l, r);

    /// <summary>
    /// Its text: <c>root.graph.mode[0].source.name</c>, <c>count(x) &gt; 2</c>; what
    /// <see cref="ExpParser.Parse"/> reads back as the same expression (docs/proposals/expression-grammar.md),
    /// parenthesised only where the order of operations needs it.
    /// </summary>
    public static string Text(Expr e) => e switch
    {
        Expr.Name n => n.Id,
        Expr.It => "it",
        Expr.Member m => $"{Operand(m.Of, ExprLevel.Postfix)}.{m.Key}",
        Expr.Index ix => $"{Operand(ix.Of, ExprLevel.Postfix)}[{Text(ix.At)}]",
        Expr.Call c => $"{Operand(c.Callee, ExprLevel.Postfix)}({string.Join(", ", c.Args.Select(Text))})",
        Expr.Lit l => LitText(l.Value),
        Expr.Not n => $"!{Operand(n.Of, ExprLevel.Unary)}",
        // A chain leans left (a && b && c is (a && b) && c), so a right side as loose as its operator
        // keeps its parentheses; one comparison never takes another bare (a < b < c does not parse).
        Expr.Binary b => $"{Operand(b.Left, Level(b) == ExprLevel.Compare ? Level(b) + 1 : Level(b))} {OpText(b.Op)} {Operand(b.Right, Level(b) + 1)}",
        _ => "?",
    };

    // How tightly each form holds together (ExprLevel), as the parser reads it: an operand looser than
    // its place needs its parentheses.
    static int Level(Expr e) => e switch
    {
        Expr.Binary b => ExprSymbols.Table[(int)b.Op].Level,
        Expr.Not => ExprLevel.Unary,
        _ => ExprLevel.Postfix,
    };
    static string Operand(Expr e, int least) => Level(e) < least ? $"({Text(e)})" : Text(e);

    // A literal as the parser reads it back: an int as it is, a real always with a point or an exponent
    // (2.0, not 2, which is an int), every digit kept; a text quoted, escaped; an address as $id.
    static string LitText(in Value v)
    {
        switch (v.Kind)
        {
            case EvalKind.Int: return v.At.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case EvalKind.Bool: return v.At != 0 ? "true" : "false";
            case EvalKind.Real:
            {
                if (!double.IsFinite(v.Real)) return "none";   // no text for NaN or infinity: it does not parse
                var r = v.Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return r.AsSpan().IndexOfAny('.', 'E') >= 0 ? r : r + ".0";
            }
            case EvalKind.Text:
            {
                var sb = new System.Text.StringBuilder("\"");
                foreach (char c in v.AsText)
                    sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", _ => c.ToString() });
                return sb.Append('"').ToString();
            }
            default: return ValueText(v);   // an address: its type's text, $15
        }
    }

    static string OpText(BinaryOp op) => ExprSymbols.Table[(int)op].Text;

    /// <summary>A value as text: a number, a quoted text, a thing as its type prints it, a set's parts; a lens is named around it.</summary>
    public static string ValueText(in Value v)
    {
        if (v.Type?.Lens is { } lens) return $"{lens.Func.Name}({ValueText(v with { Type = lens.Inner })})";
        if (v.Type is { Kind: EvalKind.Address, Read: { } read }) return ValueText(read(v));
        if (v.Type?.Text is { } text) return text(v);
        switch (v.Kind)
        {
            case EvalKind.None: return NoneText(v);
            case EvalKind.Int: return v.At.ToString();
            case EvalKind.Real: return v.Real.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            case EvalKind.Bool: return v.At != 0 ? "true" : "false";
            case EvalKind.Text: return $"\"{v.AsText}\"";
            case EvalKind.Parts when v.Type!.Count is not null:
            {
                long n = Eval.Count(v);
                var parts = new string[Math.Min(n, 16)];
                for (int i = 0; i < parts.Length; i++) parts[i] = ValueText(Eval.IndexStep(v, Value.Of((long)i)));   // as they are: printing applies no views
                return $"[{string.Join(", ", parts)}{(n > parts.Length ? ", …" : "")}]";
            }
            default: return v.Type!.Name;
        }
    }

    // A none, and why: what was asked for and not made yet, or the part of the expression that found nothing.
    static string NoneText(in Value v) => v.Status switch
    {
        Status.Produced => "none",
        Status.Pending => $"pending: {v.Holder} not produced yet",
        _ => $"none: {(v.Holder is Expr where ? Text(where) : "it")} {Failed[(int)v.Status]}",
    };
    static readonly string[] Failed = ["", "", "is no name", "is no member", "is no part", "is not a function", "is gone"];   // by Status
}
