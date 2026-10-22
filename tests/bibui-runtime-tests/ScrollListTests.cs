using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;

/// <summary>RowScrollList and ScrollList, and the bar beside either: where a list rests, checked to the pixel.</summary>
static class ScrollListTests
{
    const int Count = 1000;
    const float Height = 300f;   // the surface's, and so the list's
    const float Pitch = 24f;

    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new Exception($"{message}: expected {expected} ± {tolerance} but got {actual}");
    }

    // Rows of 20, 35 and 50 px in turn, and row 100 taller than the view.
    static float Uneven(int i) => i == 100 ? 10_000f : 20f + (i % 3) * 15f;

    /// <summary>A list of 1000 rows in a 400×300 surface with its bar: RowScrollList, or ScrollList given heights.</summary>
    sealed class Lab
    {
        public ScrollPt Pt;
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        readonly Func<int, float> _height;
        public int First, Last;
        public float Bottom;   // where the last row built ends, from the top of the view

        public Lab(ScrollBy by, Func<int, float>? heightOf = null)
        {
            _height = heightOf ?? (_ => Pitch);
            Runtime.SetBuild(() =>
            {
                var panel = Runtime.BoxFromId("panel").Sized(Size.Fill, Size.Fill);
                using (Runtime.Parent(panel))
                {
                    var area = Runtime.BeginParent(Runtime.Row().Sized(Size.Fill, Size.Fill));
                    float built = 0f;
                    if (heightOf is null)
                    {
                        (First, Last) = Runtime.BeginRowScrollList("list", ref Pt, Pitch, Count, by);
                        for (int i = First; i < Last; i++) { Runtime.Box().Sized(Size.Fill, Size.Px(Pitch)); built += Pitch; }
                        Bottom = built - ((Box)Runtime.ParentInScope).ViewOffset.Y;
                        Runtime.EndRowScrollList(ref Pt, Pitch, Count, by);
                    }
                    else
                    {
                        var rows = Runtime.BeginScrollList("list", ref Pt, Count);
                        for (int i = rows.Row; rows.More; i = rows.Row)
                            if (rows.Next(ref Pt, heightOf(i))) { Runtime.Box().Sized(Size.Fill, Size.Px(heightOf(i))); built += heightOf(i); }
                        (First, Last) = (Pt.Idx, Pt.Idx + rows.Built);
                        Bottom = built - ((Box)Runtime.ParentInScope).ViewOffset.Y;
                        Runtime.EndScrollList(ref Pt, rows);
                    }
                    Runtime.Scrollbar("list-bar", ref Pt, Count, First, Last);
                    Runtime.EndParent(area);
                }
                return panel;
            }, ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            Frame();
            Frame();   // laid out: the view has its height
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);
        public void Wheel(float pixels) => Frame(WindowEvent.Wheel(100f, 100f, 0f, -pixels / 32f));   // down the list when positive
        public void Settle() { for (int f = 0; f < 300; f++) Frame(); }

        /// <summary>Pixels from the top of row 0 to the top of row <paramref name="row"/>, plus <paramref name="px"/>.</summary>
        public double At(int row, float px = 0f)
        {
            double y = px;
            for (int i = 0; i < row; i++) y += _height(i);
            return y;
        }

        public double Target => At(Pt.Idx, Pt.Px);
        public double End => At(Count) - Height;   // the top of the last screenful
    }

    public static void Register(Action<string, Action> run)
    {
        run("RowScrollList: the rows in sight and the view's offset follow from the scroll point", () =>
        {
            var list = new Lab(ScrollBy.Pixels);
            list.Pt = new ScrollPt { Idx = 50, Px = 10f };
            list.Frame();
            Eq((50, 50 + 14), (list.First, list.Last), "row 50, part of it above, to the spare row below");
            Near(10f, ((Box)list.Runtime.FindById("list")!).ViewOffset.Y, 0.01f, "offset by the pixels into row 50");
        });

        run("RowScrollList: the wheel moves whole rows by rows and pixels by pixels, from the first row to the last", () =>
        {
            var rows = new Lab(ScrollBy.Rows);
            for (int k = 0; k < 40; k++) rows.Wheel(7f);
            rows.Settle();
            Eq((11, 16f), (rows.Pt.Idx, rows.Pt.Px), "by rows, 280 px: 11 rows, 16 px held unseen");
            rows.Wheel(1_000_000f);
            rows.Settle();
            Eq(987, rows.Pt.Idx, "at the end, the last whole row whose screenful reaches the last row");
            Eq(true, rows.Bottom >= Height, "the last row clipped, not a gap left");
            var pixels = new Lab(ScrollBy.Pixels);
            pixels.Wheel(1_000_000f);
            pixels.Settle();
            Near(pixels.End, pixels.Target, 0.01, "by pixels, the last row on the bottom");
            pixels.Wheel(-2_000_000f);
            pixels.Settle();
            Eq((0, 0f), (pixels.Pt.Idx, pixels.Pt.Px), "and back, not above the first row");
        });

        run("ScrollList: the wheel moves the target exactly through rows of their own heights, a tall one too, and what is shown eases there", () =>
        {
            var list = new Lab(ScrollBy.Pixels, Uneven);
            for (int k = 0; k < 40; k++) list.Wheel(7f);
            Near(280, list.Target, 0.01, "40 × 7 px down");
            Eq(true, list.Pt.Off != 0f, "what is shown still easing there");
            list.Wheel(7000f);
            Near(7280, list.Target, 0.01, "a fling of 7,000 px, into the row taller than the view");
            list.Wheel(-6000f);
            list.Settle();
            Near(1280, list.Target, 0.01, "and 6,000 px back up, out of it");
            Near(list.Pt.Px, ((Box)list.Runtime.FindById("list")!).ViewOffset.Y, 0.01, "at rest, shown where the target is");
        });

        run("ScrollList: the end, reached by the wheel or by a target on the last row, is the last row on the bottom", () =>
        {
            var wheel = new Lab(ScrollBy.Pixels, Uneven);
            for (int k = 0; k < 3000 && wheel.Last < Count; k++) wheel.Wheel(32f);
            for (int k = 0; k < 20; k++) wheel.Wheel(32f);
            wheel.Settle();
            Near(wheel.End, wheel.Target, 0.01, "by the wheel, the last screenful, and no further");
            Near(Height, wheel.Bottom, 0.01, "the last row on the bottom");
            var target = new Lab(ScrollBy.Pixels, Uneven);
            target.Pt.Target(Count - 1);
            target.Settle();
            Near(target.End, target.Target, 0.01, "by a target, the same");
        });

        run("ScrollList: rows of one height scroll as RowScrollList's do by pixels", () =>
        {
            var rows = new Lab(ScrollBy.Pixels);
            var heights = new Lab(ScrollBy.Pixels, _ => Pitch);
            foreach (float px in new[] { 30f, 700f, -125f, 4000f, -2200f })
            {
                rows.Wheel(px);
                heights.Wheel(px);
                rows.Settle();
                heights.Settle();
                Near(rows.Target, heights.Target, 0.01, $"the same place after {px} px");
                Eq(rows.First, heights.First, "the same row at the top");
            }
        });

        run("Scrollbar: a press on the lane shows the row under it; a press on the thumb moves nothing until it drags", () =>
        {
            var list = new Lab(ScrollBy.Pixels, Uneven);
            var lane = (Box)list.Runtime.FindById("list-bar")!;
            float x = lane.Rect.X + lane.Extent.X / 2f;
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, x, lane.Rect.Y + 150f, MouseButton.Left));
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, x, lane.Rect.Y + 150f, MouseButton.Left));
            list.Frame();
            var thumb = (Box)list.Runtime.FindById("list-bar-thumb")!;
            Near(150f, thumb.Rect.Y - lane.Rect.Y + thumb.Extent.Y / 2f, 1f, "the thumb's centre at the press");
            int idx = list.Pt.Idx;
            float y = thumb.Rect.Y + 3f;   // near its top edge, not its centre
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left));
            list.Frame();
            Eq(idx, list.Pt.Idx, "pressed on the thumb and held, not moved");
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, x, y + 60f, MouseButton.Left));
            Eq(true, list.Pt.Idx > idx, "dragged down, the list follows");
        });
    }
}
