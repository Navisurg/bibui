using Bibui.App;
using Bibui.Automation;
using Bibui.Commands;
using Bibui.Views;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

// Board: the cases worked on (each a study, called by a title, with its NMF modes), each seen as a card,
// laid out on a board that fills the window: board(it.case) over the cases. Cards are dragged and the
// board panned and zoomed (the wheel) in the view's view state.
//
//   dotnet run --project samples/board              the window, with a test port (bp --app board)
//   dotnet run --project samples/board -- --text    the view, printed through TextView
//
// The test port answers model.text (bp text) with the same as --text.

if (args.Contains("--text"))
{
    var printed = Workbench.Make();
    Seed(printed);
    Text(printed, TextWriter.Null);         // a pass asks the vessel cache for what the panels show (their segmentations),
    VesselLayer.Produce(printed.Cache);   // and the step after a frame makes them
    Text(printed, Console.Out);
    return 0;
}

var runtime = BibuiRuntime.Create();
using var app = BibuiApp.Open(runtime, new("Board", 1280, 800));
var s = new UiState();
var w = Workbench.Make(wake: runtime.RequestFrame);
Seed(w);
var view = UIView.Make(runtime, w.EvalCtx, w.Types, w.Window, w.Model, s.Views);

using var test = TestSession.Open(runtime,
    new TestPortOptions { Name = "board", Commands = CmdJson.For(BoardJson.ReadingAgainst(() => w.Model.Tree).Options, BoardCmdOps.All), Ops = BoardCmdOps.All },
    screenshots: app.ReadPixels,
    agent: AgentCursor.Start("Claude", UiColor.Hex("#D97757")),
    waitForAnimations: true,
    model: w.Model, schema: NodeKinds.Schema,
    text: () =>
    {
        var writer = new StringWriter();
        Text(w, writer);
        return writer.ToString();
    });
Console.WriteLine($"Test port: {test.Port.SocketPath}");

app.Frame = Frame;
while (app.WaitForFrame())
{
    while (test.TryPull(app.Events, out var cmd, out var ctx)) w.Model.Push(cmd, ctx);   // in the context the client sent
    Frame();
}
return 0;

void Frame()
{
    w.Model.Run();
    Addresses.Begin(w.EvalCtx.Addresses, w.Model.Gen);                                   // a new generation: the addresses of the last are dropped
    Eval.Select(w.EvalCtx);                                                              // the context the frame reads in
    ModelEval.FillViewRulesFromModel(w.EvalCtx, w.Types, Workbench.Published(w.Model));   // the view rules: the schema's defaults, the model's on top
    s.Frame += 1;
    runtime.BeginBuild(app.Window.Frame, app.Events);
    UIView.BeginFrame(view, s.Frame);
    BuildBoard();
    test.DrawAgent(size: 1.6f);
    runtime.EndBuild();
    Views.Evict(s.Views, Workbench.Published(w.Model), s.Frame);   // a node not drawn, or gone: its view state goes

    //- end of frame: drawn; then the vessel cache makes what this frame asked for (the worker's, in
    // case-viewer), shown next frame
    app.Draw();
    bool made = VesselLayer.Produce(w.Cache) > 0;
    if (made) runtime.RequestFrame();
    test.End(settled: !w.Model.HasPending && !made);
    app.Present();
}

// The window: its panels, each any node through any lens (BuildPanel). The first fills the room at
// the left (the cases, as a board); the rest are a column at the right when there are any, its share
// of the width a fact (panels.share), each panel's of the column's height too (panel.weight). Between
// them are grabbers that resize them: left of the column its width, above each panel in it but the top
// one the split with the panel above; a double press evens a split (the column: its default).
//
// NOTE(k): the layout is in two places while it is dragged. The model holds it; a drag's share is the
// view's (ResizeState, in the panels node's view state) only while its grabber is held and for the
// frame it is let go, when its one resize command is pushed; from the next frame, whose commands ran
// before its build, the model's again. Nothing but this build reads the drag's share: the log, --text
// and the port see the model, the layout before the drag until it is let go.
void BuildBoard()
{
    var t = Workbench.Published(w.Model);
    var window = runtime.Row().Sized(Size.Fill, Size.Fill);
    window.Style.BackgroundColor = UiColor.Surface;
    using var _ = runtime.Parent(window);
    var left = App.MainPanel(t);
    if (left.IsNone)
    {
        runtime.Text("No panels open.", ComponentSize.Sm, TextColor.Secondary);
        return;
    }

    //- the main panel: the room at the left, all of it when it is the only one
    var room = runtime.Column().Sized(Size.Fill, Size.Fill);
    room.Style.Padding = EdgeInsets.All(8);
    using (runtime.Parent(room)) BuildPanel(t, left);
    var root = Nodes.RootOf(t);
    var first = PanelId.First(t, root);
    if (first == left) first = first.Next(t);
    if (first.IsNone) return;

    // The drag under way, if any: let go last frame, its command has run, so the model's is true again.
    var rs = Views.User<ResizeState>(Views.From(s.Views, root, s.Frame));
    if (rs.LetGo) (rs.Held, rs.LetGo) = (false, false);

    //- the column's width: its grabber, dragged left to widen it, as a share of the window's width: what
    // the main panel leaves
    var edge = Grabber("panels/grab", Size.Px(8), Size.Fill);
    double share = Resize(rs, edge, default, 1 - left.Share(t), App.ColumnDefault, window.Extent.X, -edge.DragDeltaX, App.ColumnLeast, App.ColumnMost);
    if (rs.LetGo && rs.Grabber == default) w.Model.Push(new BoardCmd.Panels_Resize(share));
    var column = runtime.Column();
    (column.SizeX, column.SizeY) = (SemanticSize.ParentPct((float)share, strictness: 1f), Size.Fill);
    column.Style.Padding = new EdgeInsets(0, 8, 8, 8);

    //- the rest, each in a slot under its grabber (but the top one's); their heights set after, by
    // weight, once a grabber dragged has had its say
    Span<double> weight = stackalloc double[64];
    int n = 0;
    using (runtime.Parent(column))
    {
        UiNode? above = null;
        PanelId previous = default;
        for (var p = first; !p.IsNone && n < weight.Length; p = p.Next(t))
        {
            if (p == left) continue;   // the main panel: at the left, not in the column
            weight[n] = p.Share(t);
            var slot = runtime.Column();
            slot.SizeX = Size.Fill;
            Interaction top = default;
            using (runtime.Parent(slot))
            {
                if (above is not null) top = Grabber($"{p}/grab", Size.Fill, Size.Px(8));
                BuildPanel(t, p);
            }

            // The split with the panel above: the upper's share of the pair's height, from the top of
            // its slot to the bottom of this one.
            if (above is not null)
            {
                NodeId below = p;
                double pair = weight[n - 1] + weight[n];
                double split = Resize(rs, top, below, weight[n - 1] / pair, 0.5, slot.Rect.Y + slot.Extent.Y - above.Rect.Y, top.DragDeltaY, App.PairLeast, App.PairMost);
                (weight[n - 1], weight[n]) = (pair * split, pair - pair * split);
                if (rs.LetGo && rs.Grabber == below)
                    using (w.Model.ContextScope(w.Model.GetCtx() with { PanelFrom = previous, PanelTo = p }))   // the pair: the one above grows by split
                        w.Model.Push(new BoardCmd.Panel_Resize(split));
            }
            above = slot;
            previous = p;
            n++;
        }
    }
    double weights = 0;
    for (int i = 0; i < n; i++) weights += weight[i];
    int at = 0;
    for (var slot = column.FirstChild; slot is not null && at < n; slot = slot.Next, at++)
        slot.SizeY = SemanticSize.ParentPct((float)(weight[at] / weights));
}

// What a grabber's drag makes of a share: the model's (now), but while it is held or let go this frame,
// the share when it was pressed plus how far the pointer has moved since, as a part of the pixels the
// share was of then (span), clamped as the command clamps; a double press starts from even. It is
// measured from the press, not from last frame's rects: those lag the pointer by a frame, and deltas
// against them would compound. Sets LetGo the frame it is let go, for the caller to push its one command.
static double Resize(ResizeState rs, Interaction hit, NodeId grabber, double now, double even, float span, float moved, double least, double most)
{
    if (hit.Pressed && span > 0)
        (rs.Grabber, rs.Held, rs.Start, rs.Span) = (grabber, true, hit.PressCount == 2 ? even : now, span);
    if (!rs.Held || rs.Grabber != grabber) return now;
    rs.Share = Math.Round(Math.Clamp(rs.Start + moved / rs.Span, least, most), 4);
    if (hit.Released) rs.LetGo = true;
    return rs.Share;
}

// A grabber: a strip between panels that is dragged to resize them, lit while the pointer is over it
// and in the accent while held. What a drag means is the caller's.
Interaction Grabber(string id, SemanticSize width, SemanticSize height)
{
    var grab = runtime.BoxFromId(id, InteractionKinds.LeftPress);
    (grab.SizeX, grab.SizeY) = (width, height);
    grab.Style.CornerRadius = 3f;
    grab.Style.HoverBackgroundColor = UiColor.BorderHover;
    var interaction = runtime.InteractionsFor(grab);
    if (interaction.Dragging) grab.Style.BackgroundColor = UiColor.Accent;
    return interaction;
}

// The panel's query, in a box over the window, half its width and centred, opened by its button in the
// panel's title, beside the row's: one line, typed into in place (the panel's
// BibuiStr). Enter parses it: an expression goes to panel.set_lens, which the log keeps, as the
// select's pick and `bp cmd` do; a text that does not parse shows the caret where it stopped and why
// under it. Either way it stays open, to try another. Escape, or a press outside it, closes it: what was
// typed stays the panel's query, a draft until it is set. The panel is the context's (BuildPanel's).
void QueryPopover(ModelStore t)
{
    var panelId = w.Model.GetCtx().Panel;
    if (panelId.Query(t) is not { } query) return;
    var handle = panelId;   // its ids under the panel's, as its title's are
    string popover = $"{handle}/query", id = $"{handle}/query/line";
    var open = runtime.Button($"{handle}/query/open", "Query", size: ComponentSize.Sm);   // as the row's buttons are: a view's, not a command
    if (open.Clicked)
    {
        runtime.OpenDropDown(popover, open.Node);
        runtime.Focus(id);
        s.QueryEdit.Cursor = s.QueryEdit.Mark = (int)query.Length;
        s.QueryError = null;
    }
    if (runtime.BeginDropDown(popover) is not { } box) return;
    // Centred by the height it had last frame: it is as tall as what it holds, known after layout.
    float width = runtime.SurfaceWidth / 2f;
    box.SizeX = SemanticSize.Pixels(width);
    box.SizeY = SemanticSize.ChildrenSum();
    box.At((runtime.SurfaceWidth - width) / 2f, (runtime.SurfaceHeight - box.Extent.Y) / 2f);
    box.Style.Padding = EdgeInsets.All(16);
    box.Style.CornerRadius = 8f;
    box.Style.BorderWidth = runtime.Theme.Borders.Width;
    box.Style.Shadow = runtime.Theme.Elevation.Level(Bibui.Core.Theming.ElevationTokens.Overlay);
    box.Style.BorderColor = UiColor.Border;
    box.Style.BackgroundColor = UiColor.SurfaceRaised;
    runtime.BeginParent(box);

    var frame = runtime.Box().Sized(Size.Fill, Size.Fit);
    frame.Style.Padding = new EdgeInsets(10, 8, 10, 8);
    frame.Style.CornerRadius = 4f;
    frame.Style.BackgroundColor = UiColor.Surface;
    frame.Style.BorderColor = s.QueryError is not null ? UiColor.Destructive : runtime.IsFocused(id) ? UiColor.Accent : UiColor.Border;
    frame.Style.BorderWidth = 1f;
    BibuiWidget line;
    using (runtime.Parent(frame))
    {
        line = runtime.TextField(id, query, s.QueryEdit, placeholder: "an expression over it");
    }
    if (s.QueryError is { } error)
    {
        runtime.Gap(8);
        runtime.Text(error, ComponentSize.Sm, TextColor.Secondary);
    }
    runtime.EndParent(box);
    // After what it holds: a press on its padding is its own, so it neither falls through nor closes it.
    box.Claims = InteractionKinds.Press;
    runtime.InteractionsFor(box);

    // Escape: the line's own while it has the keys, the popover's once a press inside took them.
    bool escape = line.Interaction.Cancelled;
    foreach (var ev in runtime.UnconsumedEvents)
        if (ev is { IsKeyDown: true, Key: Key.Escape }) { escape = true; ev.Eat(); }
    if (escape) { runtime.CloseDropDown(popover); return; }

    // Enter: set, or why not; either way it stays open, and the line keeps the keys (Enter gave them up).
    if (!line.Interaction.Submitted) return;
    runtime.Focus(id);
    if (ExpParser.Parse(query.Str, out var why) is { } e)
    {
        w.Model.Push(new BoardCmd.Panel_SetLens(e));   // in BuildPanel's scope: the panel's
        s.QueryError = null;
    }
    else
    {
        s.QueryError = $"at {why.At}: expected {why.Expected}";
        s.QueryEdit.Cursor = s.QueryEdit.Mark = why.At;
    }
}

// A panel: the node it shows, as its own line reads (seen through its view rule: a case, its card's,
// its title), and its row (Close); under them, the lens it is shown through, by its label, a select of
// the lenses its kind is offered through (Workbench.Offered: picked, panel.set_lens); then the node
// through that lens, the panel node the one whose view state the lens keeps; a block that fills (a tree) takes the rest of the panel. Sharing the column's
// height with the others. The panel is in the context for all of it: what is drawn in it reads it there
// (the query popover, a row's command that takes the panel it is shown in).
void BuildPanel(ModelStore t, PanelId p)
{
    using var _ = w.Model.ContextScope(w.Model.GetCtx() with { Panel = p });
    var box = runtime.Box().Sized(Size.Fill, Size.Fill);
    box.ChildLayoutAxis = LayoutAxis.Y;
    box.Style.Padding = EdgeInsets.All(10);
    box.Style.CornerRadius = 6f;
    box.Style.BackgroundColor = UiColor.SurfaceRaised;
    box.Style.BorderColor = UiColor.Border;
    box.Style.BorderWidth = 1f;
    var panel = p.RowFromId(t);   // its row: what it shows, and through what
    using (runtime.Parent(box))
    {
        var node = Workbench.Shown(w, t, panel.Shows, null);   // through its view rule
        var title = runtime.Row().Sized(Size.Fill, Size.Fit);
        using (runtime.Parent(title))
        {
            using (runtime.Parent(runtime.Box().Sized(SemanticSize.Pixels(4096, strictness: 0f), Size.Fit)))
                if (node.IsNone) UIView.Ellipsis(view, $"{p}/title", "(gone)");
                else w.Window[UIView.DrawnBy(view, node).Name].Line(view, node, $"{p}/title");
            QueryPopover(t);
            runtime.Gap(6);
            UIView.Commands(view, ModelEval.Node(w.Types, t, p));
        }
        var lens = panel.Lens;
        if (EvalTypes.Bare(node.Type) is { } kind && w.Offered.TryGetValue(kind, out var offered))
        {
            string text = "(another lens)";
            foreach (var o in offered) if (ReferenceEquals(o.Lens, lens)) text = o.Label;
            string id = $"{p}/lens";
            var select = Select.Begin(runtime, id, text, width: 300);
            if (select.Open)
                for (int i = 0; i < offered.Length; i++)
                {
                    if (!runtime.SelectItem($"{id}/{i}", offered[i].Label, selected: ReferenceEquals(offered[i].Lens, lens)).Clicked) continue;
                    w.Model.Push(new BoardCmd.Panel_SetLens(offered[i].Lens));
                    select.Close();
                }
            select.End();
        }
        var shown = Workbench.Shown(w, t, panel.Shows, lens);
        if (shown.IsNone)
        {
            runtime.Text(Exprs.ValueText(shown), ComponentSize.Sm, TextColor.Secondary);   // evaluated, and nothing there: why, not a blank panel
            return;
        }
        using (w.Model.ContextScope(w.Model.GetCtx() with { View = p }))   // its lens draws as the panel's view: its view state the panel's
            UIView.Draw(view, shown, $"{p}/shown", Size.Fill);
    }
}

// A board to start from: three cases, each with its NMF modes and the one it works on, and the first panel:
// the cases on a board, each as its card.
static void Seed(Workbench w)
{
    var m = w.Model;
    m.Do(new BoardCmd.Case_Add("Case 0", 4, 0));
    m.Do(new BoardCmd.Case_Add("Case 1", 4, 1));
    m.Do(new BoardCmd.Case_Add("Case 2", 3, 0));
    var t = Workbench.Published(m);
    using (m.ContextScope(m.GetCtx() with { Node = Nodes.IdOf(t, Nodes.Root) }))
        m.Do(new BoardCmd.Panel_Open(BoardLens.Call(Exprs.It.Get(NodeKinds.Names.Case))));   // the root's cases, as a board: board(it.case)
}

// The window as text: its panels, each its node through its lens, as the window shows them.
static void Text(Workbench w, TextWriter output)
{
    var text = TextView.Make(output, w.EvalCtx, w.Text);
    var t = Workbench.Published(w.Model);
    ModelEval.FillViewRulesFromModel(w.EvalCtx, w.Types, t);
    if (PanelId.First(t, Nodes.RootOf(t)).IsNone) { output.WriteLine("   no panels open"); return; }
    for (var p = PanelId.First(t, Nodes.RootOf(t)); !p.IsNone; p = p.Next(t))
    {
        using var _ = w.Model.ContextScope(w.Model.GetCtx() with { Panel = p });   // as BuildPanel: the panel for all of it, the view only for its lens
        var row = p.RowFromId(t);
        var node = Workbench.Shown(w, t, row.Shows, null);
        string title = node.IsNone ? "(gone)" : TextView.Line(text, node);
        var panel = Workbench.Shown(w, t, row.Shows, row.Lens);
        if (panel.IsNone) output.WriteLine($"   panel {p}: {title}");
        else using (w.Model.ContextScope(w.Model.GetCtx() with { View = p })) TextView.Draw(text, panel, $"panel {p}: {title}");
    }
}

// A resize under way, in the panels node's view state: one at a time, as there is one pointer. Which
// grabber holds it, the share when it was pressed and the pixels that share was of, and the share now.
// The build's; see BuildBoard's note for when it, not the model, is what the layout reads.
sealed class ResizeState
{
    public NodeId Grabber;   // whose: default the column's edge; a split's, the panel below it
    public bool Held;             // pressed and not yet let go, or let go this frame
    public bool LetGo;            // let go this frame: its command pushed; cleared the next frame
    public double Start, Share;   // the share when pressed; the share now
    public float Span;            // the pixels the share was of when pressed
}
