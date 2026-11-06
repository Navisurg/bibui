using System.Text.Json.Serialization;
using Bibui.Commands;
using Bibui.Views;
using Bibui.Core;
using Bibui.Core.TextEdit;
using CoreLenses = Bibui.Commands.Lenses;

// What the board keeps beside the model: what evaluations see and how values are shown (Workbench),
// and what the build keeps between frames that is not a fact (UiState).

// What evaluations see, made once at startup and only read after: the model, its types (with the
// board's view lenses among its kinds), the names an expression may use, the view rules, the lenses.
sealed class Workbench
{
    public required Model Model;
    public required VesselCache Cache;     // the vessel layer's: segmentations, by what they are made from
    public required LensTable Lenses;      // every lens, by name: the core ones and the app's
    public required Dictionary<string, UiDrawer> Window;    // lens name → how it is drawn in a window (UIView's drawers)
    public required Dictionary<string, TextDrawer> Text;    // lens name → how it is drawn as text (TextView's drawers)
    public required ModelTypes Types;      // from NodeKinds.Schema (the view lenses' kinds among its kinds)
    public required EvalContext EvalCtx;   // what an evaluation sees: its symbols, $handles, the view rules
    public required Dictionary<EvalType, (Expr Lens, string Label)[]> Offered;   // by kind: the lenses a panel on a node of it offers to show it through, each named by what sets it apart

    /// <summary>What evaluations see, made once.</summary>
    public static Workbench Make(Action? wake = null)
    {
        var cache = new VesselCache();
        var ctx = new EvalContext();

        //- the lenses: a row per lens: the lens, how it is drawn in a window, and how it is drawn as text. The core ones (Lenses.cs), what a value without a lens is drawn
        // as by its shape (in a window: as text); then the app's, declared in BoardLenses.cs and drawn in a
        // file of its own (BoardLens.cs, TreeLens.cs). Null: not drawn there.
        (LensFunc Func, UiDrawer? Window, TextDrawer? Text)[] rows =
        [
            (CoreLenses.Text,   UIView.PlainDrawer,  TextView.PlainDrawer),    // text: any value, as text
            (CoreLenses.Fields, UIView.FieldsDrawer, TextView.FieldsDrawer),   // fields: named parts, a row each
            (CoreLenses.Table,  UIView.TableDrawer,  TextView.TableDrawer),    // table: parts alike, a row each, a column per expression
            (CoreLenses.Rows,   UIView.RowsDrawer,   TextView.RowsDrawer),     // rows: the expressions it is given, one under another
            (CoreLenses.Pick,   UIView.PickDrawer,   TextView.PlainDrawer),    // pick: an editable field, chosen from a set
            (BoardLens.Func,    BoardLens.Window,    BoardLens.Text),          // board: parts, each where it sits
            (TreeLens.Func,     TreeLens.Window,     TreeLens.Text),           // tree: a segmentation's paths, coloured by by
        ];

        // Each row fills every table its lens is in, under its name: the lens table; the symbols, the lens
        // as a value to call (board(x)); the two drawer tables. A name added twice is a mistake: Add throws.
        var lenses = new LensTable();
        var window = new Dictionary<string, UiDrawer>();
        var text = new Dictionary<string, TextDrawer>();
        foreach (var row in rows)
        {
            lenses.ByName.Add(row.Func.Name, row.Func);
            ctx.Add(row.Func.Name, new Value(lenses.Callable, 0, 0, 0, row.Func));
            if (row.Window is not null) window.Add(row.Func.Name, row.Window);
            if (row.Text is not null) text.Add(row.Func.Name, row.Text);
        }

        //- the model's types, from its schema (the view lenses' kinds, BoardLenses.cs, among them), and
        // the model: its commands applied with the lens table, so the view's lens is checked as it is set.
        var types = ModelEval.MakeEvalTypes(NodeKinds.Schema);
        var model = new Model((store, cmd, push) => App.Apply(store, cmd, push, lenses, ctx, text), wake, tree: NodeKinds.NewTree());   // starts with the schema's view rules
        model.Lenses = lenses;   // what view_rule.set may call

        //- the symbols: what each name in an expression means, each layer adding its own
        Eval.AddBuiltinFunctionSymbols(ctx);                         // count, raw
        ModelEval.AddSymbols(ctx, types, () => Published(model));    // root, owner; addresses: $slot#id, the node it names
        VesselLayerPlaces.Add(ctx, cache);                           // its Places: vessels(case, index), vessel_cache

        //- the lenses a panel offers, by the kind of node it shows: a case, each way
        var offered = new Dictionary<EvalType, (Expr Lens, string Label)[]>();
        offered.Add(types.Kinds[NodeKinds.Case]!, App.CaseViews);

        return new Workbench { Model = model, Cache = cache, Lenses = lenses, Window = window, Text = text, Types = types, EvalCtx = ctx, Offered = offered };
    }

    /// <summary>The tree evaluations read: the model's one tree, written in place by its commands (between frames).</summary>
    public static ModelStore Published(Model model) => model.Tree;

    /// <summary>
    /// What a panel shows: its lens called over its node, `it` the node while it is evaluated
    /// (board(it.case): the cases, laid out); without a lens, the node seen through its view rule (a
    /// case: card(it)), as every value is: never the bare node. None when its node is gone.
    /// </summary>
    public static Value Shown(Workbench w, ModelStore t, NodeId shows, Expr? lens)
    {
        var node = ModelEval.Node(w.Types, t, Nodes.SlotOf(t, shows));
        if (node.IsNone) return node;
        if (lens is null) return Eval.View(node, w.EvalCtx);
        var outer = w.EvalCtx.DynamicVar;
        w.EvalCtx.DynamicVar = node;
        var shown = Eval.Run(lens, w.EvalCtx);
        w.EvalCtx.DynamicVar = outer;
        return shown;
    }
}

// The build's own, between frames: never a fact, never in the log.
sealed class UiState
{
    public long Frame;                          // counted at the top of each frame; what view states are touched with
    public readonly ViewStates Views = new();   // per node a view is drawn for: the board's layout and zoom, a tree's pan, zoom and drawing; dropped with its node
    // The panels' query popovers: one is typed in at a time, so one caret and selection serve them all,
    // and why its last Enter did not parse.
    public readonly TextEditState QueryEdit = new();
    public string? QueryError;
}

// Commands as JSON, generated at compile time, for the test port.
[JsonSourceGenerationOptions(IncludeFields = true, AllowOutOfOrderMetadataProperties = true,
    PropertyNameCaseInsensitive = true, RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BoardCmd))]
public partial class BoardJson : JsonSerializerContext
{
    /// <summary>The commands' JSON, a node's id read as its kind's id type only when <paramref name="tree"/>'s tree holds a node of that kind there: a command naming a node of another kind is refused as it is read.</summary>
    public static BoardJson ReadingAgainst(Func<ModelStore> tree)
    {
        var options = new System.Text.Json.JsonSerializerOptions(Default.Options);
        foreach (var converter in NodeIdsJson.For(tree)) options.Converters.Add(converter);
        return new BoardJson(options);
    }
}
