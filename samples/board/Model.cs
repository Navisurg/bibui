using System.ComponentModel;
using System.Text.Json.Serialization;
using Bibui.Commands;
using Bibui.Core;

// The board's model: the cases worked on (each a study, called by a title, holding its data: the NMF
// modes of its factorisation, and which of them it works on), and the panels the window shows, each
// the same: any node through an expression over it, a lens call (board(it.case)). The main panel
// is the window's left (the cases, as a board), the rest a column at its right. Only App.Apply writes
// it. A case is seen as a card wherever it is read: its view rule ([ViewRule]: rows(it, it.title, it.picked)), in
// the tree the model starts with (NodeKinds.NewTree), in the built-in view_rules list, and changed there,
// or for one node (a view_rule child of it: case.tree), by the built-in view_rule.set. Where each card
// sits is its panel's view state, never a fact.
//
// The tree is the model's shape: each node its id, kind and links. What a node says is its row, in its
// kind's table (Tables.cs), read and written whole by App.Apply through the node's typed id (caseId.RowFromId(store), Modify). A case's modes are
// its data, a list in its row, not nodes.
//
//   root
//     case                    its row: Case("Case 1", Modes: [mode 0, mode 1, …], Picked: 1)
//       view_rule             its own, while its card shows its tree (case.tree): lens: rows(it, it.title, it.picked, tree(…))
//     view_rules              the library's: every rule in effect, the schema's from the start
//       view_rule             type: "case", lens: rows(it, it.title, it.picked)
//     panel                   its row: Panel(Shows: → the root, Lens: board(it.case), Main, Share: 0.7)
//     panel                   its row: Panel(Shows: → a case (case.open), Lens: tree(vessels(it.title, it.modes[it.picked].index).paths, …), Share: 1)
[NodeSet]
public static partial class Node
{
    [Description("The board: the cases worked on and the panels the window shows, each under it (their [In]).")]
    public sealed record Root();

    //- the cases

    [Description("A case: a study worked on, called by its title, and its data: the NMF modes of its factorisation, one of them worked on (picked on its card). Seen as a card.")]
    [In(typeof(Root), Many = true)]
    [Row(typeof(BoardCmd.Case_Open), typeof(BoardCmd.Case_Tree))]
    [ViewRule("rows(it, it.title, it.picked)")]   // its card: its title, then the mode it works on
    public record struct Case(
        [Description("What it is called.")] [Editable] string Title,
        [Description("Its NMF modes, in order. NOTE(k): shared by every version of the case's row (a version copies the row, not the list): a command that changes it gives the row a new list, never changes this one.")] List<Mode> Modes,
        [Description("The mode it works on: its place in Modes.")] [Editable] [ViewRule("pick(it, owner(it).modes, it.index)")] long Picked);

    //- the panels

    [Description("A panel: a node through an expression over it, a lens call. The main one is the window's left; the rest share a column at its right.")]
    [In(typeof(Root), Many = true)]
    [Row(typeof(BoardCmd.Panel_Close))]
    public sealed record Panel(
        [Description("The node it shows.")] NodeId Shows,
        [Description("How it shows its node: a lens called over it (it), as a case is opened: tree(vessels(it.title, it.modes[it.picked].index).paths, it.parent, it.length_mm, it.radius_mm).")] Expr? Lens,
        [Description("Whether it is the main panel, the window's left: one is (the first opened; the next, once it is closed).")] bool Main,
        [Description("Its share: the main panel's, of the window's width (panels.resize sets it); another's, of the column's height, against the others' (panel.resize).")] [DefaultValue(1.0)] double Share,
        [Description("What is typed in its query: its lens's text, set with the lens, or a draft that is not its lens yet (Enter sets it, when it parses). One text, edited in place: typing is not a command, so an older generation shows the text as it is now; what is set is the lens, by panel.set_lens.")] [Editable] BibuiStr? Query);
}

/// <summary>An NMF mode: one component of its case's factorisation, where a vessel segmentation is run. Data in a case's row, not a node.</summary>
public readonly record struct Mode(long Index);

// The commands: the only writes. Each names nodes by handle and values as values, so a client sends
// any of them as JSON (bp cmd), but for an expression (a view's lens), which is pushed from the app.
[CommandSet]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
public abstract partial record class BoardCmd
{
    private BoardCmd() { }

    //- the cases

    [Description("Adds a case: its title, the NMF modes of its factorisation (0 to Modes - 1), and the one it works on (Index).")]
    public sealed record Case_Add(string Title, long Modes, long Index) : BoardCmd;
    [Description("Sets the mode a case works on: one of its own modes, by its place among them.")]
    public sealed record Case_SetMode(CaseId Case, long Mode) : BoardCmd;
    [Description("Opens a case in a new panel beside the board, its mode's vessels drawn as their tree (panel.open, with that lens).")]
    [Short("Open")]
    public sealed record Case_Open(CaseId Case) : BoardCmd;
    [Description("Shows its vessels' tree under its card, or hides it: its own view rule, rows(it, it.title, it.picked, tree(...)), set or removed; its kind's otherwise.")]
    [Short("Tree")]
    public sealed record Case_Tree(CaseId Case) : BoardCmd;

    //- the panels

    [Description("Opens a new panel, the last: a node through a lens called over it (none: through its view rule).")]
    public sealed record Panel_Open(NodeId Node, Expr? Lens) : BoardCmd;
    [Description("Shows a panel's node through another expression over it (it): a lens its kind is offered through (Workbench.Offered), as the panel's select picks it, or any typed in its query.")]
    public sealed record Panel_SetLens(PanelId Panel, Expr Lens) : BoardCmd;
    [Description("Moves the border between the main panel and the rest: the rest's column gets Share of the window's width.")]
    public sealed record Panels_Resize(double Share) : BoardCmd;
    [Description("Moves the border between two neighbouring panels of the column: First gets Share of the height they share, the pair's sum kept.")]
    public sealed record Panel_Resize(PanelId First, PanelId Second, double Share) : BoardCmd;
    [Description("Closes a panel.")]
    [Short("X")]
    public sealed record Panel_Close(PanelId Panel) : BoardCmd;

    //- reads

    [Description("Evaluates an expression against the model and answers its value drawn as text, through its view rule and lenses, as a panel shows it (bp eval). Changes nothing.")]
    [ReadsOnly]
    public sealed record Eval(Expr Expr) : BoardCmd;
}

// Every command lands here, the only writer of the model.
public static class App
{
    // The bounds of a resize: the panels' column's share of the width, and a panel's share of the
    // height it splits with its neighbour. The build lays out a drag with the same bounds, so what it
    // shows while the command is pending is what the command will set.
    public const double ColumnLeast = 0.15, ColumnMost = 0.85, ColumnDefault = 0.3, PairLeast = 0.1, PairMost = 0.9;

    // A view's lens: none (the node as its shape is drawn), or a call of a lens the lens table has, by
    // its name, checked as it is set: a call of a name that is no lens is refused, not shown later as nothing.
    static bool IsLens(LensTable lenses, Expr? lens) => lens is null || Lenses.IsCall(lenses, lens);

    // A new panel: a node through a lens, the last of the panels; the main one when there is none (the
    // first opened). Nothing for a node gone or a lens that is none.
    static void Open(ModelStore store, LensTable lenses, NodeId node, Expr? lens)
    {
        if (!Nodes.Holds(store, node) || !IsLens(lenses, lens)) return;
        bool main = MainPanel(store).IsNone;
        var query = new BibuiStr(64);
        query.ReplaceRange((0, 0), lens is null ? "" : Exprs.Text(lens));
        PanelId.New(store, Nodes.RootOf(store)).Modify(store, new Node.Panel(node, lens, main, main ? 1 - ColumnDefault : 1.0, query));
    }

    // A panel's lens, and its query made the lens's text: however the lens is set (the query's Enter,
    // the select, a command), the query shows what it is.
    static void SetLens(ModelStore store, PanelId panel, Expr lens)
    {
        var row = panel.RowFromId(store);
        panel.Modify(store, row with { Lens = lens });
        row.Query?.ReplaceRange((0, (int)row.Query.Length), Exprs.Text(lens));
    }

    /// <summary>The main panel: the window's left; none when no panel is open.</summary>
    public static PanelId MainPanel(ModelStore store)
    {
        for (var p = PanelId.First(store, Nodes.RootOf(store)); !p.IsNone; p = p.Next(store))
            if (p.Main(store)) return p;
        return default;
    }

    // How a case is shown in a panel: its mode's vessels, drawn as their tree, once per way of reading
    // them (VesselLayer.Interpretations: the tree lens's by), named by that reading. Made once, from
    // the readings: a reading added there is offered here. The first, the plain one, is how a case opens.
    public static readonly (Expr Lens, string Label)[] CaseViews = MakeCaseViews();
    static readonly Expr OpenLens = CaseViews[0].Lens;

    // A case as its card with its tree under it: its own view rule while its tree is shown (case.tree).
    static readonly Expr CardWithTree = Lenses.Call(Lenses.Rows, Exprs.It, Exprs.It.Get(CaseFields.Title), Exprs.It.Get(CaseFields.Picked), OpenLens);

    // tree(vessels(it.title, it.modes[it.picked].index).paths, it.parent, it.length_mm, it.radius_mm, by) per
    // reading: the picked mode's vessel paths, each drawn from its parent, as long and as wide as it is; by
    // none, the plain colour. The vessel layer knows nothing of cases: its key is built here, from the case's
    // facts (its title, as the source, and its picked mode's index), as the expression joining the two.
    static (Expr, string)[] MakeCaseViews()
    {
        var key = new[] { Exprs.It.Get(CaseFields.Title), Exprs.It.Get(CaseFields.Modes).At(Exprs.It.Get(CaseFields.Picked)).Get(ModeFields.Index) };
        var paths = Exprs.Call(VesselLayerPlaces.SegmentationName, key).Get(VesselsFields.Paths);
        Expr parent = Exprs.It.Get(VesselPathFields.Parent), length = Exprs.It.Get(VesselPathFields.LengthMm), width = Exprs.It.Get(VesselPathFields.RadiusMm);

        var views = new (Expr, string)[VesselLayer.Interpretations.Length];
        for (int i = 0; i < views.Length; i++)
            views[i] = VesselLayer.Interpretations[i] is { } by
                ? (TreeLens.Call(paths, parent, length, width, by), $"by {Exprs.Text(by)}")
                : (TreeLens.Call(paths, parent, length, width), "plain");
        return views;
    }

    /// <summary>
    /// One command, applied to <paramref name="store"/>, the model's: its tree and its rows, the rows read and
    /// written through each node's typed id; <paramref name="lenses"/> is what a panel's lens may call; <paramref name="ctx"/>
    /// and <paramref name="text"/>, what an eval reads names with and draws its answer through (Workbench.Make's).
    /// </summary>
    public static void Apply(ModelStore store, Cmd cmd, Pushes push, LensTable lenses, EvalContext ctx, Dictionary<string, TextDrawer> text)
    {
        var root = Nodes.RootOf(store);
        switch (cmd)
        {
            //- the cases
            case BoardCmd.Case_Add(var title, var count, var index):
            {
                if (count < 1 || index < 0 || index >= count) break;   // a mode it would not have
                var modes = new List<Mode>((int)count);
                for (long i = 0; i < count; i++) modes.Add(new Mode(i));
                CaseId.New(store, root).Modify(store, new Node.Case(title, modes, index));   // its node, and its row
                break;
            }
            case BoardCmd.Case_SetMode(var @case, var mode):
            {
                if (mode < 0 || mode >= (@case.Modes(store)?.Count ?? 0)) break;   // one of its own modes only
                @case.Modify(store, @case.RowFromId(store) with { Picked = mode });   // a new row: the one before kept for the generations that read it
                break;
            }
            case BoardCmd.Case_Tree(var caseId):
            {
                // The case's own rule (its view_rule child), not its kind's: the other cards stay as they are.
                bool showing = !ModelEval.ViewRuleFromNode(store, caseId).IsNone;
                ModelEval.SetViewRuleForNode(store, lenses, caseId, showing ? null : CardWithTree);
                break;
            }
            case BoardCmd.Case_Open(var opened):
                Open(store, lenses, opened, OpenLens);   // its picked mode's vessels, as their tree
                break;

            //- the panels
            case BoardCmd.Panel_Open(var node, var lens):
                Open(store, lenses, node, lens);
                break;
            case BoardCmd.Panel_SetLens(var panel, var lens):
                SetLens(store, panel, lens);   // any expression over its node: typed, it need not be a lens
                break;
            case BoardCmd.Panels_Resize(var share):
            {
                // The column's share of the width is what the main panel leaves.
                var main = MainPanel(store);
                if (main.IsNone) break;
                main.Modify(store, main.RowFromId(store) with { Share = Math.Round(1 - Math.Clamp(share, ColumnLeast, ColumnMost), 4) });
                break;
            }
            case BoardCmd.Panel_Resize(var first, var second, var share):
            {
                // One grows, the other gives way, the pair's sum kept. To four places: a pointer held
                // still measures the same share, so changes nothing.
                Node.Panel a = first.RowFromId(store), b = second.RowFromId(store);
                if (a.Main || b.Main) break;   // two of the column's
                double pair = a.Share + b.Share, grown = Math.Round(pair * Math.Clamp(share, PairLeast, PairMost), 4);
                first.Modify(store, a with { Share = grown });
                second.Modify(store, b with { Share = Math.Round(pair - grown, 4) });
                break;
            }
            case BoardCmd.Panel_Close(var panel):
            {
                bool wasMain = panel.Main(store);   // false once released: its row reads as the default
                Nodes.Release(store, panel);         // nothing once released
                // The next panel takes the main one's place, so there is one while any is open.
                var next = PanelId.First(store, root);
                if (wasMain && !next.IsNone)
                    next.Modify(store, next.RowFromId(store) with { Main = true, Share = 1 - ColumnDefault });
                break;
            }

            //- a read: answered, nothing written, so no generation
            case BoardCmd.Eval(var e):
                push.Output(TextView.Answer(e, ctx, text));
                break;
        }
    }
}
