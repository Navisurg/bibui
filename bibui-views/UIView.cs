using Bibui.Commands;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

namespace Bibui.Views;

// The lenses' drawing halves as bibui widgets: what TextView is to text. A value is drawn through the
// first lens in its lens chain that has a drawer, found by the lens's name in the app's drawer table
// (the app's, given at Make), else as text. A drawer has two forms: its line (the value in a row)
// and its block (its full form: a board's canvas, a card's mode). It draws only its value, into the
// parent in scope. Each is given an id, its place's, for the boxes it makes: a node by its handle
// (12#12), a field by its node's handle and key (12#12/index); anything else by the path it was
// reached by. What is drawn twice in a frame keeps its id the first time, and the second gets a suffix
// (12#12~2), so a client clicks the first.
//
// The UI never writes the model directly: it pushes commands onto the model's queue (Model.Push), and
// they are applied with the next frame's. So every change a person makes is a logged command an agent
// could also send: a pick sets its field with node.set (Model.SetField), and a node's row is the
// commands its kind's [Row] lists that the context it is drawn in can fill, as buttons (Commands).
// What only the view keeps (a board's zoom, a tree's pan) is view state (ViewStates), never a command.
//
// NOTE(k): the ids are strings made per frame, a few per value drawn; the core's two-part box id
// (open item) removes them.

public delegate void UiLine(UIView view, in Value shown, string id);
public delegate void UiBlock(UIView view, in Value shown, string id);
/// <summary>
/// A lens's drawing as widgets: its line, and its block when it has more to show than a line. A block
/// that fills (a board) takes the room it is given; any other is as tall as what it draws.
/// </summary>
public sealed record UiDrawer(UiLine Line, UiBlock? Block = null, bool Fills = false);

public sealed class UIView
{
    public required BibuiRuntime Ui;
    public required EvalContext EvalCtx;
    public required ModelTypes Types;
    public required Model Model;                                    // where its writes go (Push)
    public required ViewStates Views;                                    // what a view keeps between frames (a board's layout and zoom, a tree's pan, zoom and drawing), by the node it is drawn for
    public long Frame;                                                   // this frame: what the view states it touches are kept for
    public required Dictionary<string, UiDrawer> Drawers;                // lens name → its drawing in a window: the app's, given at Make
    public required ContextStack Context;                                // what is being drawn: the nodes it is about, by kind, and the view's node (Context.View: whose view state a lens keeps); pushed as drawing descends

    readonly HashSet<string> used = new();                               // the ids given this frame
    readonly List<RowItem> row = new();                                  // a node's row, read again for each node drawn (Commands)
    readonly Dictionary<string, (BibuiStr Text, long Frame)> texts = new(); // a cut line's text, by its id: set again when it reads otherwise; dropped when not drawn
    readonly Dictionary<string, (ScrollPt At, long Frame)> scrolls = new(); // a table's scroll, by its id: what it keeps between frames; dropped when not drawn
    static readonly TextEditState NoEdit = new();                        // the cut lines' caret: never edited (no events), so one serves all

    // The core lenses' drawing in a window, for the app's drawer table: the value as text; a thing's
    // parts, a row each (fields); parts alike, a row each and a column per expression (table). What a
    // value without a lens is drawn as, by its shape (DrawnBy: Lenses.Generic), so any value can be shown
    // before an app writes a lens for it.
    public static readonly UiDrawer PlainDrawer = new(PlainLine);
    public static readonly UiDrawer FieldsDrawer = new(PlainLine, FieldsBlock, Fills: true);
    public static readonly UiDrawer TableDrawer = new(TableLine, TableBlock, Fills: true);
    public static readonly UiDrawer RowsDrawer = new(RowsLine, RowsBlock);
    public static readonly UiDrawer PickDrawer = new(PickLine);

    const float Label = 140;   // a fields row's name, a table row's label: its width, in pixels
    const float Cell = 160;    // a table's column: its width, in pixels, given up in a narrower row
    const float Pitch = 28;    // a table row's height, in pixels

    // A box asking for width pixels and giving them up in a narrower row, laid out along X: what a cut
    // line (Ellipsis) is put in, as it fills its parent: a box sized by it could not hold it (a ring).
    static Box Yielding(BibuiRuntime ui, float width)
    {
        var box = ui.Box().Sized(SemanticSize.Pixels(width, strictness: 0f), Size.Fit);
        box.ChildLayoutAxis = LayoutAxis.X;
        return box;
    }

    // A box exactly width pixels wide, laid out along X: a row's name or label, which keeps its width.
    static Box Fixed(BibuiRuntime ui, float width)
    {
        var box = ui.Box().Sized(Size.Px(width), Size.Fit);
        box.ChildLayoutAxis = LayoutAxis.X;
        return box;
    }

    /// <summary>A view drawing with <paramref name="drawers"/>, the app's table of how each lens is drawn in a window.</summary>
    public static UIView Make(BibuiRuntime ui, EvalContext ctx, ModelTypes types, Dictionary<string, UiDrawer> drawers, Model model, ViewStates views) =>
        new() { Ui = ui, EvalCtx = ctx, Types = types, Drawers = drawers, Model = model, Views = views, Context = new ContextStack() };

    /// <summary>The start of a frame's drawing: the ids given in the last are free again.</summary>
    public static void BeginFrame(UIView view, long frame)
    {
        view.used.Clear();
        view.Context.Clear();   // the last frame's contexts are gone
        view.Frame = frame;
        foreach (var (id, (_, drawn)) in view.texts)   // a dictionary may lose entries while it is enumerated
            if (drawn < frame - 1) view.texts.Remove(id);
        foreach (var (id, (_, drawn)) in view.scrolls)
            if (drawn < frame - 1) view.scrolls.Remove(id);
    }

    /// <summary>
    /// <paramref name="text"/> on one line in the box in scope, ending in "…" when the box is narrower
    /// (the box must not be sized by its text). <paramref name="id"/> names it, and keeps its buffer.
    /// </summary>
    public static void Ellipsis(UIView view, string id, string text, TextColor color = TextColor.Primary)
    {
        if (!view.texts.TryGetValue(id, out var kept)) kept = (new BibuiStr(Math.Max(16, text.Length)), 0);
        if (!kept.Text.Str.SequenceEqual(text)) kept.Text.ReplaceRange((0, (int)kept.Text.Length), text);
        view.texts[id] = (kept.Text, view.Frame);
        using (view.Ui.FontSize(ComponentSize.Sm))
            view.Ui.EllipsisTextField($"{id}/text", kept.Text, NoEdit, events: 0, textColor: color);
    }

    /// <summary>
    /// The id of <paramref name="value"/>'s place: its handle, a node's; its handle and key, a field's;
    /// else <paramref name="path"/>. Suffixed when already given this frame.
    /// </summary>
    public static string Place(UIView view, in Value value, string path)
    {
        var bare = Bare(value);
        string id = ModelEval.IsNode(bare) ? ModelEval.IdOf(bare).ToString()
            : ModelEval.AddressOf(view.Types, bare) is ({ } node, { } key) && key is not null ? $"{node}/{key}"
            : path;
        return Unique(view, id);
    }

    /// <summary>The id, or, given already this frame, the id with the first suffix not given (12#12~2).</summary>
    public static string Unique(UIView view, string id)
    {
        if (view.used.Add(id)) return id;
        for (int n = 2; ; n++) if (view.used.Add($"{id}~{n}")) return $"{id}~{n}";
    }

    /// <summary>
    /// The lens <paramref name="value"/> is drawn through: the first in its lens chain with a drawer here;
    /// else the core lens for its shape (Lenses.Generic: named parts as fields, parts alike as a table),
    /// when it has a drawer here; else text.
    /// </summary>
    public static LensFunc DrawnBy(UIView view, in Value value)
    {
        for (var t = value.Type; t?.Lens is { } lens; t = lens.Inner)
            if (view.Drawers.ContainsKey(lens.Func.Name)) return lens.Func;
        var shape = Bibui.Commands.Lenses.Generic(value);
        return view.Drawers.ContainsKey(shape.Name) ? shape : Bibui.Commands.Lenses.Text;
    }

    public static Value Bare(in Value v) => v with { Type = EvalTypes.Bare(v.Type) };

    /// <summary>
    /// <paramref name="value"/> as it is seen: its lens's line, then its block under it, each in a box of its
    /// own (a line lays out the box it is drawn in). A block that fills (a tree's canvas, a table) is given
    /// <paramref name="fill"/>: the rest of a panel (Size.Fill); a fixed height where its box is sized by what
    /// it holds (a board's part, a row of rows). What every container draws a value with.
    /// </summary>
    public static void Draw(UIView view, in Value value, string id, SemanticSize fill)
    {
        var drawer = view.Drawers[DrawnBy(view, value).Name];
        using (view.Ui.Parent(view.Ui.Box().Sized(Size.Fill, Size.Fit))) drawer.Line(view, value, id);
        DrawBlock(view, drawer, value, id, fill);
    }

    // value's block, by its drawer, in a box of its own: fill high when it fills, else as high as it is; nothing without one.
    static void DrawBlock(UIView view, UiDrawer drawer, in Value value, string id, SemanticSize fill)
    {
        if (drawer.Block is not { } block) return;
        using (view.Ui.Parent(view.Ui.Box().Sized(Size.Fill, drawer.Fills ? fill : Size.Fit))) block(view, value, id);
    }

    /// <summary>
    /// A node's row: its kind's [Row], from the schema (Schemas.RowOf: each command it lists while the
    /// context can make it), a button each, labelled by its [Short]; pressed, made from the context
    /// (OpInfo.FromContext) and pushed. The context is the one the row is drawn in, with the node in its
    /// kind's slot: a command takes what it needs from it (the case, the panel it is shown in). Nothing
    /// for anything that is not a node.
    /// </summary>
    public static void Commands(UIView view, in Value shown)
    {
        var bare = Bare(Eval.Load(shown));
        if (!ModelEval.IsNode(bare)) return;
        var (t, slot) = ModelEval.NodeOf(bare);
        if (Schemas.Of(view.Types.Schema, Nodes.At(t, slot).Kind) is not { } kind) return;
        using var _ = view.Context.Push(kind.Index, Nodes.IdOf(t, slot));
        view.row.Clear();
        Schemas.RowOf(kind, view.Context.Top, view.row);
        foreach (var item in view.row)
        {
            if (item.Command is not { } op) continue;   // a field the row lists: not drawn here
            if (view.Ui.Button(Unique(view, $"{ModelEval.IdOf(bare)}/{op.Wire}"), op.Short ?? op.Label, size: ComponentSize.Sm).Clicked
                && op.FromContext?.Invoke(view.Context.Top) is { } cmd)
                view.Model.Push(cmd);
            view.Ui.Gap(6);
        }
    }

    static void PlainLine(UIView view, in Value shown, string id)
    {
        var bare = Bare(Eval.Load(shown));
        Ellipsis(view, id, bare.Kind == EvalKind.Text ? bare.AsText : Exprs.ValueText(bare));   // a text as itself, not quoted as an expression writes it
    }

    //- fields and table: a value by its shape, when no lens in its chain has a drawer

    // A thing's parts, a row each: its name, then the part through its own lens's line (part i is
    // shown[i], Eval.At). A part that is a list is expanded under its row, as its table, filling what is
    // left: so a node whose member is a list (the cases' case) shows that list as rows. The first 64.
    static void FieldsBlock(UIView view, in Value shown, string id)
    {
        using var _ = view.Context.PushView(default);   // a part is drawn on its own, not as this view
        Parts(view, shown, id, EvalTypes.Bare(shown.Type)?.PartName ?? (i => $"[{i}]"), Size.Fill, Size.Fill);
    }

    //- rows(it, a, b, …): its parts, one under another, each as it is seen

    // Its first part's line (a title), where any value's line goes.
    static void RowsLine(UIView view, in Value shown, string id)
    {
        var first = Eval.At(shown, 0, view.EvalCtx);
        view.Drawers[DrawnBy(view, first).Name].Line(view, first, $"{id}/0");
    }

    // Under it: its first part's block, then each other part, one under another; a part that fills is given
    // RowsFill, as its box is as high as what it holds. The node is the view its parts are drawn for (so a
    // part's view state, a tree's pan, is the node's).
    static void RowsBlock(UIView view, in Value shown, string id)
    {
        // The node it lays out (a case, for its card): the view its parts are drawn for, and in its kind's slot.
        using var scope = view.Context.Push();
        if (ModelEval.NodeOf(Bare(shown)) is ({ } t, > 0 and var slot)) scope.View(Nodes.IdOf(t, slot)).Set(Nodes.At(t, slot).Kind, Nodes.IdOf(t, slot));
        else scope.View(default);
        Parts(view, shown, id, null, Size.Fit, Size.Px(RowsFill));
    }

    // A value's parts, one under another (fields' and rows'): each its line (after its name, named; rows'
    // first, already its own line, not again), then its block. Each part evaluated as it is reached: they are
    // few (a thing's members, an expression's arguments); a long list is a part of its own, drawn as a table,
    // which draws the rows in view.
    static void Parts(UIView view, in Value shown, string id, Func<long, string>? named, SemanticSize height, SemanticSize fill)
    {
        var ui = view.Ui;
        long n = Math.Min(Eval.Count(shown), 64);
        var column = ui.Box().Sized(Size.Fill, height);
        column.ChildLayoutAxis = LayoutAxis.Y;
        using var _ = ui.Parent(column);
        for (int i = 0; i < n; i++)
        {
            var part = Eval.At(shown, i, view.EvalCtx);
            var drawer = view.Drawers[DrawnBy(view, part).Name];
            var at = named is null ? $"{id}/{i}" : $"{id}/{named(i)}";
            if (named is not null)
                using (ui.Parent(ui.Row().Sized(Size.Fill, Size.Px(Pitch))))
                {
                    using (ui.Parent(Fixed(ui, Label))) Ellipsis(view, Unique(view, $"{at}/name"), named(i), TextColor.Secondary);
                    using (ui.Parent(Yielding(ui, 4096))) drawer.Line(view, part, at);   // the rest of the row
                }
            else if (i > 0)
                using (ui.Parent(ui.Box().Sized(Size.Fill, Size.Fit))) drawer.Line(view, part, at);
            DrawBlock(view, drawer, part, at, fill);
        }
    }

    const float RowsFill = 220;   // a rows part that fills (a tree), its height

    //- pick(it, choices, label): an editable field's value, chosen from a set, as a select

    // The value as its choice's label; open, a select item per choice (at most 64, each read as it is listed);
    // chosen, node.set at the field's address: an int field picked from things, the choice's place; a text, the
    // choice's text; a node's id, the chosen node's. Its choices are its lens's (pick's first argument), else
    // its type's (an enum's names, a handle's nodes); each shown by pick's label (it the choice), else as it is seen.
    static void PickLine(UIView view, in Value shown, string id)
    {
        var ctx = view.EvalCtx;
        var value = Eval.Load(Bare(shown));
        var lens = Bibui.Commands.Lenses.Of(shown, Bibui.Commands.Lenses.Pick);
        var choices = lens is { Once.Length: > 0 } ? Bibui.Commands.Lenses.Arg(lens, 0, shown, ctx) : Bibui.Commands.Lenses.ChoicesOf(shown);
        var label = lens is { PerPart.Length: > 0 } ? lens.PerPart[0] : null;
        long n = Math.Min(Eval.Count(choices), 64);
        bool byPlace = value.Kind == EvalKind.Int && n > 0 && Eval.At(choices, 0, ctx).Kind != EvalKind.Int;   // an index into things
        long current = -1;
        if (byPlace) current = value.AsInt;
        else for (long i = 0; i < n && current < 0; i++) if (Same(value, Eval.At(choices, i, ctx))) current = i;

        string Text(in Value choice)
        {
            var shownAs = choice;
            if (label is not null)
            {
                var outer = ctx.DynamicVar;
                ctx.DynamicVar = choice;
                shownAs = Eval.Load(Eval.Run(label, ctx));
                ctx.DynamicVar = outer;
            }
            var bare = Bare(shownAs);
            return bare.Kind == EvalKind.Text ? bare.AsText : Exprs.ValueText(bare);
        }

        var select = Select.Begin(view.Ui, Unique(view, id), current >= 0 && current < n ? Text(Eval.At(choices, current, ctx)) : Exprs.ValueText(value), width: 160);
        if (select.Open)
            for (long i = 0; i < n; i++)
            {
                var choice = Eval.At(choices, i, ctx);
                if (!view.Ui.SelectItem($"{id}/{i}", Text(choice), selected: i == current).Clicked) continue;
                if (ModelEval.AddressOf(view.Types, Bare(shown)) is var (node, key) && key is not null)
                    view.Model.SetField(node, key, byPlace ? Exprs.Lit(i) : ChoiceLit(choice));
                select.Close();
            }
        select.End();
    }

    // A choice as node.set is given it: a literal of its value, as its text would be written (a node, $7).
    static Expr ChoiceLit(in Value choice)
    {
        var v = Eval.Load(Bare(choice));
        if (ModelEval.IsNode(v)) return Exprs.Lit(Value.Of(ModelEval.IdOf(v)));
        return v.Kind switch
        {
            EvalKind.Text => Exprs.Lit(v.AsText),
            EvalKind.Int => Exprs.Lit(v.AsInt),
            EvalKind.Real => Exprs.Lit(v.AsReal),
            EvalKind.Bool => Exprs.Lit(Value.Of(v.AsBool)),
            _ => Exprs.Lit(v),
        };
    }

    // Whether a field's value is this choice: a node, by its id; else by value.
    static bool Same(in Value value, in Value choice)
    {
        var c = Bare(choice);
        if (ModelEval.IsNode(value) || ModelEval.IsNode(c)) return value.Id == c.Id && value.Id != 0;
        return value.Kind == c.Kind && value.Kind switch
        {
            EvalKind.Text => value.AsText == c.AsText,
            EvalKind.Int or EvalKind.Bool => value.AsInt == c.AsInt,
            EvalKind.Real => value.AsReal == c.AsReal,
            _ => false,
        };
    }

    static void TableLine(UIView view, in Value shown, string id) =>
        Ellipsis(view, id, $"{Eval.Count(shown)} rows", TextColor.Secondary);

    // Its rows: a header of its columns (its table lens's, else its parts' fields), then a row per part
    // in sight (only those are built: a scroll list), its label the part through its own lens's line (a
    // case: its card's, its title), a cell per column, evaluated here with `it` the row and drawn through
    // its own lens's line; then the commands its row offers (a case: Open). A column is an expression, so
    // a table shows anything computed from its rows, not only what they hold.
    static void TableBlock(UIView view, in Value shown, string id)
    {
        var ui = view.Ui;
        var ctx = view.EvalCtx;
        var columns = Bibui.Commands.Lenses.Columns(shown);
        int count = (int)Math.Min(Eval.Count(shown), int.MaxValue);

        var header = ui.Row().Sized(Size.Fill, Size.Px(Pitch));
        using (ui.Parent(header))
        {
            ui.Box().Sized(Size.Px(Label), Size.Px(1));
            for (int c = 0; c < columns.Length; c++)
            {
                var name = Exprs.Text(columns[c]);
                using (ui.Parent(Yielding(ui, Cell)))
                    Ellipsis(view, Unique(view, $"{id}/column/{c}"), name.StartsWith("it.") ? name[3..] : name, TextColor.Secondary);
            }
        }

        var at = view.scrolls.TryGetValue(id, out var kept) ? kept.At : default;
        var (first, last) = ui.BeginRowScrollList($"{id}/rows", ref at, Pitch, count);
        var outer = ctx.DynamicVar;
        for (int r = first; r < last; r++)
        {
            var part = Eval.At(shown, r, ctx);
            var row = ui.Row().Sized(Size.Fill, Size.Px(Pitch));
            using (ui.Parent(row))
            {
                using (ui.Parent(Fixed(ui, Label))) view.Drawers[DrawnBy(view, part).Name].Line(view, part, $"{id}/{r}");
                foreach (var column in columns)
                {
                    ctx.DynamicVar = part;   // it, the row, only while its cell is evaluated
                    var cell = Eval.Run(column, ctx);
                    ctx.DynamicVar = outer;
                    using (ui.Parent(Yielding(ui, Cell))) view.Drawers[DrawnBy(view, cell).Name].Line(view, cell, $"{id}/{r}/{Exprs.Text(column)}");
                }
                Commands(view, part);
            }
        }
        ui.EndRowScrollList(ref at, Pitch, count);
        view.scrolls[id] = (at, view.Frame);
    }
}
