using System.Numerics;
using Bibui.Commands;
using Bibui.Views;
using Bibui.Core;
using Bibui.Core.Runtime;

// The board lens, all of it: parts laid out on a board, each in a box where it sits, drawn inside it
// through its own lens (a case: its card, its kind's view). The board knows nothing of what its parts
// are: the cases through it, board(it.case), lay out cases; any set would do.
//
// Where each part sits, and where the board is looked at, are the view's, not facts: its view state
// (BoardState, by the view's node and, within it, by part). A part is placed the first time it is drawn,
// by its order, left to right; dragged, it moves there, and dragging the board pans it, with no command
// and no generation; as is how large it is drawn (zoom), which the wheel over the board changes, about
// the point under the pointer.
static partial class BoardLens   // the lens: BoardLenses.Board (its function and call, cmdgen's, in the other part)
{
    const int Most = 256;      // the parts drawn; the rest counted
    const float Width = 200;   // a part's box, in pixels
    const float PartFill = 220;   // what a part's block that fills (a tree) is given: its box is as high as what it holds
    const float Notch = 32;    // the wheel's travel per notch, in pixels (BibuiRuntime.Input)
    const float Step = 1.1f;   // the zoom per notch

    /// <summary>
    /// What a board keeps between frames, in the view state of the node it is drawn for (Views.User): where each part
    /// sits, where the board is looked at, and a drag under way. Never a fact.
    /// </summary>
    public sealed class BoardState
    {
        public readonly Dictionary<NodeId, Vector2> At = new();   // by part, its node's handle: where it sits, in board units
        public Vector2 Pan;                                             // the board point at the canvas's top-left corner
        public NodeId Held;                                        // the part being dragged; default for none
        public Vector2 Grab;                                            // where on it it was taken, in board units
        public bool Panning;                                            // the board itself is being dragged
        public Vector2 PanFrom, PanStart;                               // where that drag began, in pixels, and the pan then
        public float Zoom = 1;                                          // how large it is drawn: the wheel's, between 0.25 and 4
    }

    // Its drawing, in a window (filling the room it is given) and as text: what the board's lens rows
    // give its name.
    public static readonly UiDrawer Window = new(Line, Block, Fills: true);
    public static readonly TextDrawer Text = new(TextLine, TextBlock);

    // How many parts it lays out; that it has none, for anything that is not parts.
    static string Summary(in Value shown) =>
        shown.Kind == EvalKind.Parts ? $"board, {Eval.Count(shown)} parts" : "board: nothing to lay out";

    //- in a window

    static void Line(UIView view, in Value shown, string id) => UIView.Ellipsis(view, id, Summary(shown));

    // The canvas: the room it is given, less a margin, its parts cut at its edges. Dragging it pans the
    // board; each part is a box at its place, the one held drawn last, over the others. A press goes to
    // the first box that asks for it, so the parts ask before the canvas: a press on a part drags the
    // part, and only one on the bare board pans it (the pan then shows from the next frame).
    static void Block(UIView view, in Value shown, string id)
    {
        var ui = view.Ui;
        var st = Bibui.Commands.Views.User<BoardState>(Bibui.Commands.Views.From(view.Views, view.Context.Top.View, view.Frame));
        float zoom = st.Zoom;

        var room = ui.Box().Sized(Size.Fill, Size.Fill);
        room.Style.Padding = EdgeInsets.All(8);
        using var _ = ui.Parent(room);
        var canvas = ui.BoxFromId($"{id}/board", InteractionKinds.Clip | InteractionKinds.LeftPress | InteractionKinds.Scroll);
        (canvas.SizeX, canvas.SizeY) = (Size.Fill, Size.Fill);
        canvas.Style.BackgroundColor = UiColor.SurfaceRaised;
        canvas.Style.BorderColor = UiColor.Border;
        canvas.Style.BorderWidth = 1f;
        canvas.Style.CornerRadius = 4f;
        var origin = new Vector2(canvas.Rect.X, canvas.Rect.Y);   // last frame's: where the board's pixels start

        // Its parts: part i is shown[i], as the language evaluates it (Eval.At), so seen through its own
        // view rule (a case: card(it)).
        Span<Value> parts = new Value[Most];
        int n = (int)Math.Min(Eval.Count(shown), Most);
        for (int i = 0; i < n; i++) parts[i] = Eval.At(shown, i, view.EvalCtx);
        Forget(st, parts[..n]);
        int held = -1;
        using (ui.Parent(canvas))
        {
            for (int i = 0; i < n; i++)
            {
                if (st.Held != default && ModelEval.IdOf(parts[i]) == st.Held) { held = i; continue; }
                Part(view, parts[i], i, $"{id}/{i}", st, origin, zoom);
            }
            if (held >= 0) Part(view, parts[held], held, $"{id}/{held}", st, origin, zoom);
        }

        // Panning: a drag that started on the bare board, the parts having taken theirs.
        var hit = ui.InteractionsFor(canvas);
        if (hit.Pressed) (st.Panning, st.PanFrom, st.PanStart) = (true, new Vector2(hit.X, hit.Y), st.Pan);
        if (st.Panning && (hit.Dragging || hit.Released)) st.Pan = st.PanStart - (new Vector2(hit.X, hit.Y) - st.PanFrom) / zoom;
        if (hit.Released) st.Panning = false;

        // Zooming: the wheel over the board, about the point under the pointer: that point stays where it
        // is drawn, the pan moved to keep it; both drawn from the next frame.
        if (hit.ScrollY != 0)
        {
            float to = Math.Clamp(zoom * MathF.Pow(Step, -hit.ScrollY / Notch), 0.25f, 4f);
            var pointer = new Vector2(hit.X, hit.Y) - origin;
            st.Pan += pointer / zoom - pointer / to;
            st.Zoom = to;
        }
    }

    // One part: a box at its place on the board (placed by its order the first time), taking the press
    // that starts its drag; inside, the part as its own lens draws it (a case: its card), its line and its
    // block. Width wide: a box sized by what it holds could not hold a line that fills it (an ellipsis),
    // the one sized by the other (a layout ring).
    static void Part(UIView view, in Value part, int index, string path, BoardState st, Vector2 origin, float zoom)
    {
        var ui = view.Ui;
        var handle = ModelEval.IdOf(part);   // default for a part that is no node: placed by its order each frame
        if (!st.At.TryGetValue(handle, out var at) || handle == default)
        {
            at = new Vector2(20 + index % 4 * (Width + 20), 20 + index / 4 * 100);   // apart by a box's width, and a card's height
            if (handle != default) st.At[handle] = at;
        }
        string place = UIView.Place(view, part, path);
        var box = ui.BoxFromId($"{place}/part", InteractionKinds.LeftPress);
        box.ChildLayoutAxis = LayoutAxis.Y;
        box.Sized(Size.Px(Width), Size.Fit);
        box.Style.Padding = EdgeInsets.All(8);
        box.Style.CornerRadius = 4f;
        box.Style.BackgroundColor = UiColor.Surface;
        box.Style.BorderColor = st.Held == handle && handle != default ? UiColor.Accent : UiColor.Border;
        box.Style.HoverBorderColor = UiColor.BorderHover;
        box.Style.BorderWidth = 1f;

        // A part is drawn on its own, not as this view: its view state is its own node's (a case's tree: the case's).
        using (view.Context.PushView(default))
        using (ui.Parent(box))
        {
            UIView.Draw(view, part, place, Size.Px(PartFill));   // the part as Eval.At gave it: seen through its view already
            var row = ui.Row().Sized(Size.Fill, Size.Fit);   // its row: the commands its kind lists (a case: Open)
            row.Style.Padding = new EdgeInsets(6, 0, 0, 0);
            using (ui.Parent(row)) UIView.Commands(view, part);
        }

        // After what it holds: a press goes to the first box that asks, so a button inside takes its own,
        // and the box only a press on the rest of it. Held,
        // it follows the pointer, less where on it it was taken; let go, it stays there. Placed now, as
        // the layout reads it at the end of the build.
        var hit = ui.InteractionsFor(box);
        Vector2 board = (new Vector2(hit.X, hit.Y) - origin) / zoom + st.Pan;   // the pointer, in board units
        if (hit.Pressed && handle != default) (st.Held, st.Grab) = (handle, board - at);
        if (st.Held == handle && handle != default && (hit.Dragging || hit.Released))
        {
            st.At[handle] = at = board - st.Grab;
            if (hit.Released) st.Held = default;
        }
        var drawn = (at - st.Pan) * zoom;
        box.At(drawn.X, drawn.Y);
    }

    // Drops the places of parts no longer laid out (a part gone): only when there are more places
    // than parts, so a board whose parts stay costs nothing.
    static void Forget(BoardState st, ReadOnlySpan<Value> parts)
    {
        if (st.At.Count <= parts.Length) return;
        var kept = new HashSet<NodeId>();
        foreach (var part in parts) kept.Add(ModelEval.IdOf(part));
        foreach (var handle in st.At.Keys)   // a dictionary may lose entries while it is enumerated
            if (!kept.Contains(handle)) st.At.Remove(handle);
    }

    //- as text

    static string TextLine(TextView view, in Value shown) => Summary(shown);

    // A line per part, as its own lens's line reads. Where each sits is the view's, not drawn here.
    static void TextBlock(TextView view, in Value shown, int depth)
    {
        long n = Math.Min(Eval.Count(shown), Most);
        string pad = TextView.Pad(depth);
        if (n == 0) view.Out.WriteLine($"{pad}[an empty board]");
        for (int i = 0; i < n; i++) view.Out.WriteLine($"{pad}{TextView.Line(view, Eval.At(shown, i, view.EvalCtx))}");
    }
}
