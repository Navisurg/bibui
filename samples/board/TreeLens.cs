using System.Numerics;
using Bibui.Commands;
using Bibui.Views;
using Bibui.Core;

/// <summary>A tree drawn: each part, by its place, its ends, its width, its colour; what they cover. Made once, read only after.</summary>
public sealed class TreeDrawing
{
    public required Vector2[] From, To;
    public required float[] Width;
    public required Rgba[] Colour;
    public Vector2 Min, Max;            // what the parts cover: what a view is fitted to
}

/// <summary>How one view looks at a tree, in the view state of the node it is drawn for (Views.User): never a fact, never what it draws.</summary>
public sealed class TreeState
{
    public ulong FittedTo;              // the set it was last fitted to, by what its address means (Addresses.Hash): another (a mode picked) is fitted again; another by is not
    public Vector2 CanvasSize;          // the canvas's size it was last looked at in, in logical pixels: another (a panel resized) keeps what is in view
    public float Zoom = 1;              // logical pixels per unit of length
    public Vector2 Pan;                 // the point, in units of length, at the canvas's top-left corner
    public bool Panning;                // being dragged
    public Vector2 PanFrom, PanStart;   // where that drag began, in pixels, and the pan then
}

// The tree lens, all of it: a set of parts drawn as a tree, by bibui's drawing layer (the canvas box's
// Draw, recorded into the frame and drawn by the app's renderer), on a canvas that pans and zooms. It
// knows nothing of what the parts are. What it draws is its first argument, a set, its parts read as
// any reader reads them (part i is set[i]: Eval.At); how each is drawn is its other arguments, kept as
// written and evaluated here (Make) once per part with `it` bound to the part, as a table's columns are
// evaluated once per row:
//
//   tree(vessels(it.title, it.mode.index).paths, it.parent, it.length_mm, it.radius_mm, it.radius_mm > 1)
//        what                                   parent     length        width         by: the colour
//
// Each part is a stroke from the end of its parent (an index into the set; a parent comes before its
// part, and anything else is a root, at the origin), at an angle of its own (from its place: the same
// each time), as long and as wide as it says; a root grows up. By colours it: a number shaded blue to
// red between the least and the most any part gives, a condition lit or faint, none the plain colour.
// Parent, length and width are required: a call without them draws nothing and says so; by may be
// left out (the plain colour). What they give is not checked: that is a type check's, not the lens's.
//
// Each frame it is drawn from its data and its view state alone: each part's arguments evaluated, the
// tree laid out, a line recorded per part; how it is looked at is its view state (TreeState, by the node
// it is drawn for). Nothing derived is kept between frames: the arguments are evaluated again each frame,
// so a change to the data shows on the next frame with nothing to invalidate. NOTE(k): it allocates its
// arrays each frame (GC churn), until a data lens gathers the parts into an app's own memory.
static partial class TreeLens   // the lens: BoardLenses.Tree (its function, call and readers, cmdgen's, in the other part)
{
    const float Notch = 32;    // the wheel's travel per notch, in pixels (BibuiRuntime.Input)
    const float Step = 1.1f;   // the zoom per notch

    static readonly Rgba Plain = new(217, 119, 87), Lit = new(255, 0, 20), Faint = new(90, 90, 90, 110);


    // Its drawing, in a window (filling the room it is given) and as text: what the lens rows give its name.
    public static readonly UiDrawer Window = new(Line, Block, Fills: true);
    public static readonly TextDrawer Text = new((TextView v, in Value shown) => Summary(shown));

    // How many parts it draws, and by what; that its call lacks what it needs; that it has nothing to
    // draw (yet: a segmentation pending).
    static string Summary(in Value shown) =>
        !Complete(shown) ? "tree: needs a parent, a length and a width: tree(set, parent, length, width[, by])"
        : Eval.Load(shown) is { Kind: EvalKind.Parts } set && Eval.Count(set) > 0
            ? By(shown) is { } by ? $"tree, {Eval.Count(set)} parts, by {Exprs.Text(by)}" : $"tree, {Eval.Count(set)} parts"
            : "tree: nothing to draw yet";

    // Its call has what it needs: a parent, a length and a width, each an expression per part.
    static bool Complete(in Value shown) => Parent(shown) is not null && Length(shown) is not null && Width(shown) is not null;

    static void Line(UIView view, in Value shown, string id) => UIView.Ellipsis(view, id, Summary(shown));

    // The canvas: the tree drawn where the view state says, cut at the canvas's edges. Dragged, it
    // pans; the wheel zooms about the pointer.
    static void Block(UIView view, in Value shown, string id)
    {
        var ui = view.Ui;
        var set = Eval.Load(shown);
        // A set with no parts (a pending segmentation's paths) has nothing to draw.
        if (!Complete(shown) || set.Kind != EvalKind.Parts || set.Address == 0 || Eval.Count(set) == 0) return;
        ulong which = Addresses.Hash(view.EvalCtx, set.Address);   // the set, the same in every generation
        var st = Bibui.Commands.Views.User<TreeState>(Bibui.Commands.Views.From(view.Views, view.Model.GetCtx().View, view.Frame));
        var drawing = Make(shown, set, view.EvalCtx);

        var canvas = ui.BoxFromId($"{id}/tree", InteractionKinds.Clip | InteractionKinds.LeftPress | InteractionKinds.Scroll);
        (canvas.SizeX, canvas.SizeY) = (Size.Fill, Size.Fill);
        canvas.Style.BackgroundColor = UiColor.Surface;
        var origin = new Vector2(canvas.Rect.X, canvas.Rect.Y);   // last frame's: where the canvas's pixels start
        var canvasSize = new Vector2(canvas.Extent.X, canvas.Extent.Y);

        // Fitted the first frame the canvas has a size, and again for another set: the whole tree,
        // centred, a margin around it.
        if (st.FittedTo != which && canvasSize.X > 0 && canvasSize.Y > 0 && drawing.From.Length > 0)
        {
            var size = drawing.Max - drawing.Min;
            st.Zoom = 0.9f * MathF.Min(canvasSize.X / MathF.Max(size.X, 1e-3f), canvasSize.Y / MathF.Max(size.Y, 1e-3f));
            st.Pan = drawing.Min - (canvasSize / st.Zoom - size) / 2;
            st.FittedTo = which;
        }

        // Resized (the panels' column, a split): what was at the canvas's centre stays there, and the
        // zoom follows the side that changed most, so what was in view stays in view.
        else if (st.CanvasSize.X > 0 && st.CanvasSize.Y > 0 && canvasSize.X > 0 && canvasSize.Y > 0 && canvasSize != st.CanvasSize)
        {
            var centre = st.Pan + st.CanvasSize / 2 / st.Zoom;
            st.Zoom *= MathF.Min(canvasSize.X / st.CanvasSize.X, canvasSize.Y / st.CanvasSize.Y);
            st.Pan = centre - canvasSize / 2 / st.Zoom;
        }
        if (canvasSize.X > 0 && canvasSize.Y > 0) st.CanvasSize = canvasSize;
        canvas.Draw = (_, ctx) => Paint(st, drawing, ctx);

        // Panning: a drag on the canvas. Zooming: the wheel, about the point under the pointer: that
        // point of the tree stays where it is drawn.
        var hit = ui.InteractionsFor(canvas);
        var pointer = new Vector2(hit.X, hit.Y) - origin;
        if (hit.Pressed) (st.Panning, st.PanFrom, st.PanStart) = (true, pointer, st.Pan);
        if (st.Panning && (hit.Dragging || hit.Released)) st.Pan = st.PanStart - (pointer - st.PanFrom) / st.Zoom;
        if (hit.Released) st.Panning = false;
        if (hit.ScrollY != 0)
        {
            float to = Math.Clamp(st.Zoom * MathF.Pow(Step, -hit.ScrollY / Notch), 1e-3f, 1e4f);
            st.Pan += pointer / st.Zoom - pointer / to;
            st.Zoom = to;
        }
    }

    // At paint: a line per part, in the canvas's own physical pixels, round at its ends. What it reads
    // is this frame's drawing, and where the view state, as the build left it, says it is looked at.
    static void Paint(TreeState st, TreeDrawing d, DrawContext ctx)
    {
        float s = st.Zoom * ctx.Scale;
        for (int i = 0; i < d.From.Length; i++)
        {
            var a = (d.From[i] - st.Pan) * s;
            var b = (d.To[i] - st.Pan) * s;
            ctx.DrawLine(a.X, a.Y, b.X, b.Y, new Paint { Stroke = d.Colour[i], StrokeWidth = MathF.Max(1, d.Width[i] * s), Cap = StrokeCap.Round });
        }
    }

    // A tree, made from the set the lens shows: for each part, what the lens's arguments say of it
    // (its parent, length, width and colour, `it` the part), then laid out and coloured from those
    // alone. Run each frame it is drawn, on the UI thread.
    static TreeDrawing Make(in Value shown, in Value set, EvalContext ctx)
    {
        Expr parentOf = TreeLens.Parent(shown)!;   // it.parent       (Block drew nothing without them: Complete)
        Expr lengthOf = TreeLens.Length(shown)!;   // it.length_mm
        Expr widthOf = TreeLens.Width(shown)!;     // it.radius_mm
        Expr? colourOf = TreeLens.By(shown);       // it.radius_mm > 1; none: the plain colour

        //- read: for each part, set[i] (a segmentation's path i, as the language evaluates it: Eval.At),
        // `it` that part, its parent, length, width and colour value
        int n = (int)Eval.Count(set);
        var parent = new long[n];
        var length = new float[n];
        var width = new float[n];
        var colour = new Value[n];
        var outer = ctx.DynamicVar;
        for (int i = 0; i < n; i++)
        {
            ctx.DynamicVar = Eval.At(set, i, ctx);
            var p = Eval.Load(Eval.Run(parentOf, ctx));
            parent[i] = p.Kind == EvalKind.Int ? p.AsInt : -1;
            length[i] = (float)Eval.Load(Eval.Run(lengthOf, ctx)).AsReal;
            width[i] = (float)Eval.Load(Eval.Run(widthOf, ctx)).AsReal;
            if (colourOf is not null) colour[i] = Eval.Load(Eval.Run(colourOf, ctx));
        }
        ctx.DynamicVar = outer;

        //- laid out: each part a stroke from its parent's end, turned from its parent's direction by
        // an angle of its own (from its place: the same each time, within 0.7 radians either way). A
        // parent comes before its part; a part with none (or a later one) is a root, at the origin,
        // pointing up.
        var d = new TreeDrawing { From = new Vector2[n], To = new Vector2[n], Width = new float[n], Colour = new Rgba[n] };
        var direction = new float[n];
        d.Min = new Vector2(float.MaxValue);
        d.Max = new Vector2(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            bool root = parent[i] < 0 || parent[i] >= i;
            var from = root ? Vector2.Zero : d.To[parent[i]];
            float heading = root ? -MathF.PI / 2 : direction[parent[i]];
            direction[i] = heading + ((i * 2654435761u % 1000) / 1000f - 0.5f) * 1.4f;
            var to = from + new Vector2(MathF.Cos(direction[i]), MathF.Sin(direction[i])) * length[i];

            d.From[i] = from;
            d.To[i] = to;
            d.Width[i] = width[i];
            d.Min = Vector2.Min(d.Min, Vector2.Min(from, to));
            d.Max = Vector2.Max(d.Max, Vector2.Max(from, to));
        }

        //- coloured: a number shaded blue to red between the least and the most any part gives (all
        // alike: the middle); true lit; false or nothing faint. No colour argument: the plain colour.
        if (colourOf is null)
        {
            d.Colour.AsSpan().Fill(Plain);
            return d;
        }
        double least = double.MaxValue, most = double.MinValue;
        for (int i = 0; i < n; i++)
            if (colour[i].Kind is EvalKind.Int or EvalKind.Real)
            {
                least = Math.Min(least, colour[i].AsReal);
                most = Math.Max(most, colour[i].AsReal);
            }
        for (int i = 0; i < n; i++)
        {
            var v = colour[i];
            if (v.Kind is EvalKind.Int or EvalKind.Real)
            {
                float f = most > least ? (float)((v.AsReal - least) / (most - least)) : 0.5f;
                d.Colour[i] = new Rgba((byte)(60 + 195 * f), (byte)(70 * (1 - f)), (byte)(235 * (1 - f) + 20));
            }
            else if (v.Kind == EvalKind.Bool && v.AsBool) d.Colour[i] = Lit;
            else d.Colour[i] = Faint;
        }
        return d;
    }
}
