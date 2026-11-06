namespace Bibui.Commands;

// Lenses: how a value is seen. A lens is called as a function is (LensFunc), and returns its value
// unchanged with a lensed type: the type carries the lens, the type it shows, and the call as written
// (Lens). The value itself is never copied or converted; only its type says how to see it. A lens over
// a lens keeps both: crop(bitmap(seg), rect) is seg, seen through bitmap, then through crop.
//
// A lens has one of three roles, by what it has:
//
//   draws       a drawer, the UI's, found by the lens's name (table, fields, mesh, bars)
//   changes     data hooks (LensFunc.Member, Index, Count): the parts its value has, for everyone who reads
//   the data    it (omitting members, a slice of a list); none yet
//   changes     neither: the drawer that draws the value finds it in the lens chain and reads its
//   how it is   arguments (a number base, a precision); none yet
//   drawn
//
// A drawer draws the value through the first lens in its chain that it has a drawer for; a value seen
// through none it draws by its shape (Generic: named parts as fields, parts alike as a table, an
// editable field edited or picked, anything else as text), so every value can be drawn without an app
// declaring anything. Which lens a value of a type is seen through is its view rule (EvalContext.ViewRules,
// kept in the evaluation's context: a field's own from its schema, such as [Range], and the app's, such as
// a case as a card). The rule is applied when the value is evaluated, so every reader sees it, not only a
// drawer.
//
// The lenses an app has are the lens layer's table (LensTable): every lens by name, the core ones (here)
// and the app's, each entry written out where the app hooks its layers up, read after: what lenses
// there are. A lens's name is also its name to expressions (the EvalContext: a value to call, of the
// table's Callable type) and to drawers (their tables).
//
// A lens's arguments past its value are kept as written, for whoever uses them, each said by the lens
// to be evaluated once (a pick's choices) or once per part with `it` bound to it (a table's columns,
// a bar's length): Lens.Once, read by the lens's drawer (a pick's choices), and Lenses.Columns, which
// a table's drawer evaluates per row. A lens's arguments live in its call, nowhere else: to change one
// is to set another call (a panel's lens, a view rule). How a view is being looked at (a board's pan and
// zoom, a tree's layout) is view state, by the node it is drawn for (ViewStates), never a fact.

/// <summary>
/// A set of lenses, a record each (cmdgen): the lens is named after the record (<c>Mesh</c>: mesh), its
/// first parameter is what it draws (an <see cref="Expr"/>), the rest its arguments in order (each an
/// <see cref="Expr"/>, <c>Expr?</c> when it may be left out, <c>Expr[]</c> for the rest; evaluated once,
/// or per part with <see cref="PerPartAttribute"/>). The arguments live in the lens's call and nowhere
/// else: a view is its call, so there is no settings store to keep in step with it. cmdgen writes, in
/// a part of <c>MeshLens</c> beside its hand-written drawers: its <see cref="LensFunc"/> (<c>Func</c>), a
/// typed call (<c>Call(of, …)</c>), and a reader per argument, from the call the value is seen through.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LensSetAttribute : Attribute;

/// <summary>A lens's argument evaluated once per part, with `it` the part (a bar's length, a table's column).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PerPartAttribute : Attribute;

/// <summary>When a lens's argument past its value is evaluated: once, for the value (a pick's choices), or once per part, with `it` bound to the part (a table's columns).</summary>
public enum ArgWhen : byte { Once, PerPart }

/// <summary>One argument a lens takes past its value: its name and what it is (for whoever reads the lens: a palette, an agent), when it is evaluated, and whether it repeats (a table's columns: any number).</summary>
public sealed record LensParam(string Name, ArgWhen When, string Description, bool Repeats = false);

/// <summary>
/// A lens, as it is called: made once, by whoever owns it, put into the app's lens table and named for
/// expressions by its <see cref="Name"/> (a value of the table's <see cref="LensTable.Callable"/> type). Its drawing is the UI's,
/// found by the same name.
/// </summary>
public sealed class LensFunc
{
    public required string Name;
    public string Description = "";
    public LensParam[] Params = [];   // its arguments past the value, in order; a last one that repeats takes the rest

    // --- Data: how a value seen through it is stepped (its members, its parts, how many), in place of
    // the shown type's own steps; null keeps the shown type's. Set only by a lens that changes the data
    // (omitting members, a slice), so every reader of the value sees the changed data, not only a drawer. ---
    public MemberHook? Member;
    public IndexHook? Index;
    public CountHook? Count;
}

/// <summary>
/// The lens layer's tables: every lens an app has, by name, core and the app's, filled at startup, an
/// entry per lens, where the app hooks its layers up; and the lensed types made as lenses are applied.
/// Read after: whether a name is a lens, and the type of a value seen through a lens (<see cref="Lenses.Apply"/>).
/// </summary>
public sealed class LensTable
{
    public readonly Dictionary<string, LensFunc> ByName = new();   // lens name → the lens

    // Lens, type seen and call → the type of a value of that type seen through that lens so called. Looked
    // up on every application, made on a miss: a lens applied each frame (a panel's, a view rule, a
    // table's cells) gives the same type each time, made the first, not a new one each. Filled by the
    // thread that evaluates (the app's owner thread), as the EvalContext is read by it: not locked.
    public readonly Dictionary<(LensFunc Lens, EvalType Inner, Expr.Call Call), EvalType> LensedTypes = new();

    /// <summary>
    /// The type of this table's lenses as values to call (what a lens's name holds for expressions): the LensFunc in
    /// Holder; called, its value seen through it, its type found in this table's LensedTypes. Eval.Run
    /// evaluates only its first argument; the rest are the lens's, kept as written.
    /// </summary>
    public readonly EvalType Callable;

    public LensTable() => Callable = new EvalType
    {
        Name = "lens", Description = "A lens: called over a value, the value seen through it.", Kind = EvalKind.LensFunc,
        Call = (in Value callee, ReadOnlySpan<Value> args, Expr.Call call) =>
            args.Length == 1 && callee.As<LensFunc>() is { } func ? Lenses.Apply(this, func, args[0], call) : Value.None,
        Text = (in Value v) => v.As<LensFunc>()?.Name ?? "lens",
    };
}

/// <summary>
/// A lens applied: on the type of a value seen through it (<see cref="EvalType.Lens"/>). Its arguments
/// past the value, as written, split as the lens says: those evaluated once, and those evaluated per part.
/// </summary>
public sealed class Lens
{
    public required LensFunc Func;
    public required EvalType Inner;     // the type of what it shows: a lensed type itself, when lenses stack
    public required Expr.Call Call;     // as written: printed, stored, and the lensed type's identity
    public Expr[] Once = [];            // the Once arguments, in the order the lens takes them
    public Expr[] PerPart = [];         // the PerPart arguments, in the order the lens takes them (a table's columns)
}

public static class Lenses
{
    // The lenses every UI draws: what a value without one is drawn as, by its shape (Generic), and what
    // an expression may name to draw a value so (table(x, it.a, it.b)).
    public static readonly LensFunc Text = new() { Name = "text", Description = "A value, as text." };
    public static readonly LensFunc Edit = new() { Name = "edit", Description = "An editable field's value, changed in place: set by node.set at its address." };
    public static readonly LensFunc Pick = new()
    {
        Name = "pick", Description = "An editable field's value, chosen from a set: each choice drawn as it is seen.",
        Params = [new("choices", ArgWhen.Once, "What it is picked from (a set, any parts); none: what the field may be set to (a handle's: the nodes of its kind)."),
                  new("label", ArgWhen.PerPart, "How each choice is shown, it the choice (it.index); none: as it is seen.")],
    };
    public static readonly LensFunc Range = new()
    {
        Name = "range", Description = "An editable number field's value, between two bounds: slid along them.",
        Params = [new("min", ArgWhen.Once, "Its least value."), new("max", ArgWhen.Once, "Its greatest value."),
                  new("step", ArgWhen.Once, "What it moves by; none: continuously (an int field: whole steps).")],
    };
    public static readonly LensFunc Fields = new() { Name = "fields", Description = "A thing's parts, each named, each drawn as it is seen." };

    /// <summary>
    /// A data lens: a value laid out as the expressions it is given, a part each, in order, each evaluated with
    /// `it` the value and seen through its own lens: rows(it, it.title, it.picked), a case as its title then its
    /// mode. What the value expands to is said by expressions, not by its type, and a drawer draws each part
    /// as it draws any value. Stepped by name, it is the value's own (rows(it, …).title is it.title).
    /// </summary>
    public static readonly LensFunc Rows = new()
    {
        Name = "rows", Description = "A value as the expressions it is given (it the value), a part each, in order, each seen through its own lens.",
        Params = [new("parts", ArgWhen.Once, "What each part is, `it` the value: it.title, tree(vessels(it.title, …).paths, …).", Repeats: true)],
        Count = (in Value of) => Of(of, Rows!) is { } lens ? lens.Once.Length : 0,   // Rows: itself, set by the time a value is seen through it
        Index = (in Value of, in Value at) =>
            Of(of, Rows!) is { } lens && Eval.Current is { } ctx && at is { Kind: EvalKind.Int, At: >= 0 } ? Run(lens, (int)at.At, of, ctx) : Value.None,
    };
    public static readonly LensFunc Table = new()
    {
        Name = "table", Description = "Parts, a row each, a column per expression over the part (it).",
        Params = [new("columns", ArgWhen.PerPart, "What each column shows of a part; none: the parts' fields.", Repeats: true)],
    };

    /// <summary>The most rows the table lens draws as text.</summary>
    public const long RowsDefault = 16;

    /// <summary>
    /// <paramref name="v"/> seen through <paramref name="func"/>, called as <paramref name="call"/>: the
    /// value, its type the lensed type for that lens, type and call, looked up in
    /// <paramref name="table"/>'s LensedTypes and made there on a miss. None stays none.
    /// </summary>
    public static Value Apply(LensTable table, LensFunc func, in Value v, Expr.Call call)
    {
        if (v.Type is not { } inner) return v;   // nothing to see through it: the none as it came, its status kept
        if (!table.LensedTypes.TryGetValue((func, inner, call), out var lensed))
            table.LensedTypes[(func, inner, call)] = lensed = TypeOf(func, inner, call);
        return v with { Type = lensed };
    }

    // The type of a value of type inner seen through func, called as call: its kind and its steps
    // resolved now, a data lens's own over what it shows'; nothing else, which is the bare type's. Its
    // arguments past the value split as the lens takes them: by place, a last that repeats taking the rest.
    static EvalType TypeOf(LensFunc func, EvalType inner, Expr.Call call)
    {
        var once = new List<Expr>();
        var perPart = new List<Expr>();
        for (int i = 1; i < call.Args.Length; i++)
        {
            int at = Math.Min(i - 1, func.Params.Length - 1);
            if (at < 0 || (i - 1 >= func.Params.Length && !func.Params[at].Repeats)) break;   // more than it takes: not its
            (func.Params[at].When == ArgWhen.Once ? once : perPart).Add(call.Args[i]);
        }
        return new EvalType
        {
            Name = func.Name, Description = func.Description, Kind = inner.Kind,
            Member = func.Member ?? inner.Member, Index = func.Index ?? inner.Index,
            Count = func.Count ?? inner.Count,
            Call = inner.Call, Read = inner.Read,
            Lens = new Lens { Func = func, Inner = inner, Call = call, Once = [.. once], PerPart = [.. perPart] },
        };
    }

    /// <summary>The lens <paramref name="func"/> as applied to <paramref name="v"/>, wherever it is in its lens chain; null when it is not.</summary>
    public static Lens? Of(in Value v, LensFunc func)
    {
        for (var t = v.Type; t?.Lens is { } lens; t = lens.Inner)
            if (lens.Func == func) return lens;
        return null;
    }

    /// <summary>
    /// A call of <paramref name="func"/> over its arguments, as an expression writes it: what a
    /// [LensSet]'s typed call makes (MeshLens.Call(of)). Arguments left out at the end (null) are not written.
    /// </summary>
    public static Expr Call(LensFunc func, params Expr?[] args)
    {
        int n = args.Length;
        while (n > 0 && args[n - 1] is null) n--;
        var written = new Expr[n];
        for (int i = 0; i < n; i++) written[i] = args[i] ?? Exprs.Name("none");
        return new Expr.Call(new Expr.Name(func.Name), written);
    }

    /// <summary>
    /// What <paramref name="field"/> may be set to: its type's choices (EvalType.Choices, made once with the
    /// type): every node of a kind, read in the tree the context reads; an enum's names. None when it has none.
    /// </summary>
    public static Value ChoicesOf(in Value field)
    {
        if (EvalTypes.Bare(field.Type)?.Choices is not { IsNone: false } set) return Value.None;
        return set;
    }

    /// <summary>
    /// <paramref name="lens"/>'s <paramref name="i"/>-th Once argument (a range's max, a pick's choices),
    /// evaluated now with `it` the value it is applied to (<paramref name="of"/>, under its lenses), as it
    /// was in the call's first argument: so range(it, 0, owner(it).source.frames) bounds each field by its
    /// own node's frame count, not one bound shared by every field of the type. The result is read through
    /// (a field reads as its value) and bare (the value as it is, not through its view rule). None past its
    /// arguments.
    /// </summary>
    public static Value Arg(Lens lens, int i, in Value of, EvalContext ctx) => Run(lens, i, of, ctx) is var v ? v with { Type = EvalTypes.Bare(v.Type) } : v;

    /// <summary>
    /// <paramref name="lens"/>'s <paramref name="i"/>-th Once argument, evaluated as <see cref="Arg"/> evaluates
    /// it, but as it is seen (its lenses kept): what a lens is given to show (rows' parts, each through its own
    /// lens). None past its arguments.
    /// </summary>
    public static Value Run(Lens lens, int i, in Value of, EvalContext ctx)
    {
        if (i >= lens.Once.Length) return Value.None;
        var outer = ctx.DynamicVar;
        ctx.DynamicVar = of with { Type = EvalTypes.Bare(of.Type) };
        var v = Eval.Load(Eval.Run(lens.Once[i], ctx));
        ctx.DynamicVar = outer;
        return v;
    }

    /// <summary>A call of a lens <paramref name="table"/> has, by its name (any call, without a table): what a view rule or a view's lens must be.</summary>
    public static bool IsCall(LensTable? table, Expr lens) =>
        lens is Expr.Call { Callee: Expr.Name name } && (table is null || table.ByName.ContainsKey(name.Id));

    /// <summary>
    /// The core lens <paramref name="v"/> is drawn through when no lens in its chain is drawn: by its
    /// shape. Named parts (a node, a record): fields; parts alike (a list): a table; an editable field:
    /// picked when it has choices, else edited; anything else: text.
    /// </summary>
    public static LensFunc Generic(in Value v) => EvalTypes.Bare(v.Type) switch
    {
        { Kind: EvalKind.Parts, PartName: not null } => Fields,
        { Kind: EvalKind.Parts } => Table,
        { Editable: true, Choices.IsNone: false } => Pick,
        { Editable: true } => Edit,
        _ => Text,
    };

    /// <summary>
    /// A table's columns over <paramref name="shown"/>: its table lens's, when it was given some; else
    /// a column per field of its parts (it.name, it.index), kept on the parts' type (FieldColumns).
    /// </summary>
    public static Expr[] Columns(in Value shown)
    {
        if (Of(shown, Table) is { PerPart.Length: > 0 } table) return table.PerPart;
        if (EvalTypes.Bare(shown.Type)?.PartType is not { Fields.Length: > 0 } part) return [];
        return part.FieldColumns ??= [.. part.Fields.Select(f => Exprs.It.Get(f))];
    }
}
