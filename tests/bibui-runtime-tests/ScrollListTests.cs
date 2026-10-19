using Bibui;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using Bibui.Theming;

/// <summary>
/// The scroll list: which rows it builds, how the wheel, the bar and the
/// caller move it, how it eases, and where it stops.
/// </summary>
static class ScrollListTests
{
    const float Pitch = 20f;
    const int Count = 1000;
    const float Height = 300f;   // the surface's, and so the list's

    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void Near(float expected, float actual, float tolerance, string message)
    {
        if (MathF.Abs(expected - actual) > tolerance) throw new Exception($"{message}: expected {expected} ± {tolerance} but got {actual}");
    }

    static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    /// <summary>A list of <see cref="Rows"/> rows filling a 400×300 surface, and the scroll point it keeps.</summary>
    sealed class List
    {
        public ScrollPt Pt;
        public int Rows = Count;
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();

        public List(ThemeTokens? theme = null)
        {
            Runtime.SetBuild(() =>
            {
                var rows = Runtime.BeginScrollList("list", ref Pt, Pitch, Rows);
                for (int i = rows.Min; i < rows.Max; i++)
                    Runtime.BoxFromId("row-" + i).FillX().Px(height: Pitch);
                Runtime.EndScrollList(ref Pt, rows);
                return rows.Area.FillX().FillY();
            }, theme ?? ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            Frames(3);
        }

        public void Frames(int n, List<WindowEvent>? events = null)
        {
            for (int i = 0; i < n; i++) Runtime.Step(i == 0 ? events : null);
        }

        public void Settle() => Frames(90);

        /// <summary>One frame with the wheel turned over the list by <paramref name="pixels"/>, down the list when positive.</summary>
        public void Wheel(float pixels) => Frames(1, [WindowEvent.Wheel(100f, 100f, 0f, -pixels / 32f)]);

        public bool Built(int row) => Runtime.FindById("row-" + row) is not null;

        public Box Box(string id) => (Box)Runtime.FindById(id)!;

        public float TargetPx => Pt.Idx * Pitch + Pt.Px;
    }

    public static void Register(Action<string, Action> run)
    {
        run("ScrollList: only the rows in sight are built", () =>
        {
            var list = new List();
            IsTrue(list.Built(0) && list.Built(14), "0 to 14 fill 300px of 20px rows");
            IsTrue(!list.Built(40) && !list.Built(Count - 1), "rows far below are not built");
            Eq(15, list.Pt.ViewRows, "whole rows in view");
        });

        run("ScrollList: the wheel moves the target by its pixels, and the rows follow with the fraction as an offset", () =>
        {
            var list = new List();
            list.Wheel(30f);
            Eq((1, 10f), (list.Pt.Idx, list.Pt.Px), "a row and a half, not rounded to a row");
            list.Settle();
            Eq(0f, list.Pt.Off, "settled");
            IsTrue(!list.Built(0) && list.Built(1), "row 1 is the first built");
            Near(10f, list.Box("list").ViewOffset.Y, 0.01f, "row 1 half out of the top");
        });

        run("ScrollList: it eases to where the wheel sent it, from where it was", () =>
        {
            var list = new List();
            list.Wheel(200f);
            list.Frames(1);
            IsTrue(list.Pt.Off < 0f && list.Pt.Off > -10f, $"on its way, shown above the target (Off {list.Pt.Off})");
            IsTrue(list.Runtime.FramesRequested > 0, "and asking for frames while it moves");
            list.Settle();
            Eq(0f, list.Pt.Off, "arrived");
            IsTrue(list.Built(10) && !list.Built(9), "row 10 at the top");
        });

        run("ScrollList: it stops at the top, and at the last row flush with the bottom", () =>
        {
            var list = new List();
            list.Wheel(-500f);
            Eq((0, 0f), (list.Pt.Idx, list.Pt.Px), "not above the first row");
            list.Wheel(1_000_000f);
            list.Settle();
            Near(Count * Pitch - Height, list.TargetPx, 0.01f, "not below the last");
            var last = list.Box("row-" + (Count - 1));
            Near(Height, last.Rect.Y + last.Extent.Y, 0.5f, "the last row's bottom on the view's");
        });

        run("ScrollList: a target the caller moves is where the list eases to", () =>
        {
            var list = new List();
            list.Pt.Target(500, 0f, Pitch);
            Near(-500f, list.Pt.Off, 0.001f, "what is shown stays put");
            list.Settle();
            IsTrue(list.Built(500) && !list.Built(499), "row 500 at the top");
            Near(0f, list.Box("row-500").Rect.Y, 0.5f, "flush with the view's top");
        });

        run("ScrollList: pressing the track pages by the rows in view", () =>
        {
            var list = new List();
            var after = list.Box("list-bar-after");
            float x = after.Rect.X + after.Extent.X / 2f, y = after.Rect.Y + after.Extent.Y / 2f;
            list.Frames(1, [WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left), WindowEvent.Mouse(WindowEventKind.MouseUp, x, y, MouseButton.Left)]);
            Eq(15, list.Pt.Idx, "a page of 15 rows down");
            IsTrue(list.Pt.Off < 0f, "eased, not jumped");
        });

        run("ScrollList: dragging the thumb moves the rows by its travel from where it was pressed", () =>
        {
            var list = new List();
            list.Pt.Target(300, 0f, Pitch);
            list.Pt.Off = 0f;   // jump there
            list.Frames(2);
            var thumb = list.Box("list-bar-thumb");
            float x = thumb.Rect.X + thumb.Extent.X / 2f, y = thumb.Rect.Y + 2f;   // near its top edge, not its centre

            list.Frames(1, [WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left)]);
            Eq(300, list.Pt.Idx, "pressing the thumb anywhere on it moves nothing");

            var track = list.Box("list-bar");
            float free = track.Extent.Y - thumb.Extent.Y;
            list.Frames(1, [WindowEvent.Mouse(WindowEventKind.MouseMove, x, y + free / 10f, MouseButton.Left)]);
            Near(300f + (Count - Height / Pitch) / 10f, list.Pt.Idx, 1f, "a tenth of the travel is a tenth of the rows");
            Eq(0f, list.Pt.Off, "under the pointer, not easing after it");
            list.Frames(1, [WindowEvent.Mouse(WindowEventKind.MouseUp, x, y + free / 10f, MouseButton.Left)]);
        });

        run("ScrollList: a list that fits has no thumb and does not scroll", () =>
        {
            var list = new List { Rows = 5 };
            list.Frames(2);
            IsTrue(list.Runtime.FindById("list-bar-thumb") is null, "no thumb");
            IsTrue(list.Runtime.FindById("list-bar") is not null, "the bar keeps its lane");
            list.Wheel(100f);
            Eq((0, 0f), (list.Pt.Idx, list.Pt.Px), "the wheel moves nothing");
        });

        run("ScrollList: with motion off, the wheel lands on the next frame", () =>
        {
            var list = new List(ThemeRegistry.Load("bibui-dark") with { Motion = new MotionTokens { Easing = MotionEasing.None } });
            list.Wheel(100f);
            list.Frames(1);
            Eq(0f, list.Pt.Off, "no travel");
            IsTrue(list.Built(5) && !list.Built(4), "row 5 at the top");
        });

        run("ScrollList: a list that shrinks shows its end at once", () =>
        {
            var list = new List();
            list.Wheel(10_000f);
            list.Settle();
            list.Rows = 100;
            list.Frames(2);
            IsTrue(list.Built(99), "the new last row is in sight");
            Near(100 * Pitch - Height, list.TargetPx, 0.01f, "the target pulled back into range");
        });

        run("ScrollList: a frame that scrolls allocates nothing beyond its rows", () =>
        {
            var list = new List();
            list.Wheel(5000f);   // far down, easing: the frames below scroll
            long Measure()
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                list.Frames(1);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Measure();
            long moving = Measure();
            list.Settle();
            long settled = Measure();
            IsTrue(moving <= settled, $"a scrolling frame allocates no more than a still one ({moving} vs {settled} bytes)");
        });
    }
}
