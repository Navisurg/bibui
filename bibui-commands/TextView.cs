namespace Bibui.Commands;

// The lenses' drawing halves as text: what a person reads in a terminal, and an agent reads instead
// of a screenshot. A value is drawn through the first lens in its lens chain that has a drawer here,
// found by the lens's name, else by its shape (Lenses.Generic); the core lenses' drawers are here (text,
// edit, fields, table), an app adds its own (mesh). A drawer has two forms: its line (the value on one
// line: a table's cell, a field's value) and its block (its full form, under its line: a thing's parts,
// a table's rows). Two forms because a value is drawn both inside its parent's row and on its own, and
// only the line has to fit in the parent's. A drawer draws only its value: the name it is shown under is its
// parent's to lay out (a field's name, a column's expression). A lens's arguments are its call's, as
// written. What the drawing is about (which panel, which view) is the app's context, on its model's
// stack: an app's drawer reads it there; the core drawers need none.

/// <summary>A value under a lens, on one line.</summary>
public delegate string TextLine(TextView view, in Value shown);
/// <summary>A value under a lens, in full, under its line, at <paramref name="depth"/>.</summary>
public delegate void TextBlock(TextView view, in Value shown, int depth);
/// <summary>A lens's drawing as text: its line, and its block when it has more to show than a line.</summary>
public sealed record TextDrawer(TextLine Line, TextBlock? Block = null);

/// <summary>What text drawing needs: where it writes, what names it evaluates with, and the drawers by lens name (the app's table, filled where it hooks up).</summary>
public sealed class TextView
{
    public required TextWriter Out;
    public required EvalContext EvalCtx;
    public required Dictionary<string, TextDrawer> Drawers;   // lens name → its drawing as text: the app's, given at Make

    // The core lenses' drawings as text, for the app's drawer table: text, edit and pick as the value's
    // text; fields, a line per part; table, a row per part.
    public static readonly TextDrawer PlainDrawer = new(PlainLine);
    public static readonly TextDrawer FieldsDrawer = new(PlainLine, FieldsBlock);
    public static readonly TextDrawer TableDrawer = new(TableLine, TableBlock);
    public static readonly TextDrawer RowsDrawer = new(RowsLine, RowsBlock);

    /// <summary>A text view drawing with <paramref name="drawers"/>, the app's table of how each lens is drawn as text.</summary>
    public static TextView Make(TextWriter output, EvalContext ctx, Dictionary<string, TextDrawer> drawers) =>
        new() { Out = output, EvalCtx = ctx, Drawers = drawers };

    /// <summary>
    /// What <paramref name="e"/> evaluates to, drawn as text through its view rule and lenses, under
    /// its own text: what an eval command answers, as <c>bp text</c> draws a panel. It reads the model
    /// as <paramref name="ctx"/>'s names do, with <c>it</c> the node <paramref name="it"/> (the one it is
    /// asked in: a panel's), or nothing.
    /// </summary>
    public static string Answer(Expr e, EvalContext ctx, Dictionary<string, TextDrawer> drawers, NodeId it = default)
    {
        var output = new StringWriter();
        var outer = ctx.DynamicVar;
        ctx.DynamicVar = !it.IsNone && ctx.Model is { } model ? ModelEval.Node(model.Types, model.Tree(), it) : Value.None;
        Draw(Make(output, ctx, drawers), Eval.Run(e, ctx), Exprs.Text(e));
        ctx.DynamicVar = outer;
        return output.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> under <paramref name="name"/>: its line after the name, and its block
    /// under it, through the drawer of the lens it is drawn through (<see cref="DrawnBy"/>).
    /// </summary>
    public static void Draw(TextView view, in Value value, string name, int depth = 0)
    {
        var shown = Eval.View(value, view.EvalCtx);   // seen through its view rule, however it was reached (a field read by its hook)
        var drawer = view.Drawers[DrawnBy(view, shown).Name];
        view.Out.WriteLine($"{Pad(depth)}{name}: {drawer.Line(view, shown)}");
        drawer.Block?.Invoke(view, shown, depth + 1);
    }

    /// <summary><paramref name="value"/> on one line, through the lens it is drawn through: a table's cell.</summary>
    public static string Line(TextView view, in Value value)
    {
        var shown = Eval.View(value, view.EvalCtx);
        return view.Drawers[DrawnBy(view, shown).Name].Line(view, shown);
    }

    /// <summary>The lens <paramref name="value"/> is drawn through: the first in its lens chain with a drawer here, else the core lens for its shape (<see cref="Lenses.Generic"/>).</summary>
    public static LensFunc DrawnBy(TextView view, in Value value)
    {
        for (var t = value.Type; t?.Lens is { } lens; t = lens.Inner)
            if (view.Drawers.ContainsKey(lens.Func.Name)) return lens.Func;
        // By its shape, when the app draws that lens as text; as text when it does not (an editable
        // field's edit, which only a window draws), as UIView falls back.
        var shaped = Commands.Lenses.Generic(value);
        return view.Drawers.ContainsKey(shaped.Name) ? shaped : Commands.Lenses.Text;
    }


    /// <summary>The value without the lens it is shown with.</summary>
    public static Value Bare(in Value v) => v with { Type = EvalTypes.Bare(v.Type) };

    public static string Pad(int depth) => new(' ', 3 + depth * 3);

    // A cell's text, cut to its column.
    static string Cut(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

    static string PlainLine(TextView view, in Value shown) => Exprs.ValueText(Bare(shown));

    // A thing's parts, each under its name, each through its own lens: part i is shown[i], as the
    // language evaluates it (Eval.At). The first 32.
    static void FieldsBlock(TextView view, in Value shown, int depth)
    {
        long n = Math.Min(Eval.Count(shown), 32);
        var named = EvalTypes.Bare(shown.Type)?.PartName;
        for (int i = 0; i < n; i++) Draw(view, Eval.At(shown, i, view.EvalCtx), named?.Invoke(i) ?? $"[{i}]", depth);
    }

    // rows(it, a, b, …): its first part's line; under it, the first's block, then each other part, its line and block.
    static string RowsLine(TextView view, in Value shown) => Line(view, Eval.At(shown, 0, view.EvalCtx));

    static void RowsBlock(TextView view, in Value shown, int depth)
    {
        long n = Math.Min(Eval.Count(shown), 32);
        if (n > 0 && Eval.At(shown, 0, view.EvalCtx) is var first && view.Drawers[DrawnBy(view, first).Name].Block is { } block) block(view, first, depth);
        for (int i = 1; i < n; i++) Draw(view, Eval.At(shown, i, view.EvalCtx), $"[{i}]", depth);
    }

    static string TableLine(TextView view, in Value shown) => $"{Eval.Count(shown)} rows";

    // Its rows, the first RowsDefault: row r is shown[r] (Eval.At), and
    // a column per expression (its table lens's, else its parts' fields), evaluated here with `it` the
    // row; each cell its value's line. A column is an expression, not a field name, so a table can show
    // anything computed from its rows, not only what they hold.
    static void TableBlock(TextView view, in Value shown, int depth)
    {
        var columns = Commands.Lenses.Columns(shown);
        int limit = (int)Commands.Lenses.RowsDefault;
        long count = Eval.Count(shown);
        long rows = Math.Min(count, limit);
        string pad = Pad(depth);
        if (columns.Length > 0)
            view.Out.WriteLine($"{pad}{"",-16}{string.Concat(columns.Select(c => $"{Cut(Exprs.Text(c), 27),-28}"))}".TrimEnd());
        var ctx = view.EvalCtx;
        var outer = ctx.DynamicVar;
        for (int r = 0; r < rows; r++)
        {
            var part = Eval.At(shown, r, ctx);
            var row = EvalTypes.Bare(part.Type)?.Label is { } label ? label(part) : $"[{r}]";
            var line = $"{pad}{Cut(row, 15),-16}";
            foreach (var column in columns)
            {
                ctx.DynamicVar = part;   // it, the row, only while its cell is evaluated: the cell's line is drawn as anywhere
                var cell = Eval.Run(column, ctx);
                ctx.DynamicVar = outer;
                line += $"{Cut(Line(view, cell), 27),-28}";
            }
            view.Out.WriteLine(line.TrimEnd());
        }
        if (rows < count) view.Out.WriteLine($"{pad}… {count - rows} more");
    }
}
