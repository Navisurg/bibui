using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;

/// <summary>
/// The scroll lists: RowScrollList, rows of one pitch, and ScrollList, rows of
/// their own heights, by rows and by pixels; and the bar beside either. Where
/// a list rests is checked to the pixel: the heights of the rows above Idx,
/// and Px.
/// </summary>
static class ScrollListTests
{
    const int Count = 1000;
    const float Height = 300f;   // the surface's, and so the list's
    const float Pitch = 24f;     // RowScrollList's rows, and ScrollList's when even
    const float RowStep = 32f;   // ScrollList by rows: a notch

    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new Exception($"{message}: expected {expected} ± {tolerance} but got {actual}");
    }

    // Rows of 20, 35 and 50 px in turn.
    static float Uneven(int i) => 20f + (i % 3) * 15f;

    /// <summary>
    /// A list of 1000 rows filling a 400×300 surface with its bar, the ScrollPt
    /// it keeps, and what it built last frame: the rows, and where the last of
    /// them ends below the top of the view.
    /// </summary>
    sealed class Lab
    {
        public ScrollPt Pt;
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        public readonly Func<int, float> HeightOf;
        public int First, Last;
        public float Bottom;

        /// <summary>RowScrollList when <paramref name="heightOf"/> is null, else ScrollList with those heights.</summary>
        public Lab(ScrollBy by, Func<int, float>? heightOf = null)
        {
            HeightOf = heightOf ?? (_ => Pitch);
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
                        var rows = Runtime.BeginScrollList("list", ref Pt, Count, by, RowStep);
                        for (int i = rows.Row; rows.More; i = rows.Row)
                        {
                            float h = heightOf(i);
                            if (!rows.Next(ref Pt, h)) continue;
                            Runtime.Box().Sized(Size.Fill, Size.Px(h));
                            built += h;
                        }
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

        /// <summary>Where the target is, in pixels from the top of row 0.</summary>
        public double At()
        {
            double y = Pt.Px;
            for (int i = 0; i < Pt.Idx; i++) y += HeightOf(i);
            return y;
        }

        public double Total()
        {
            double y = 0;
            for (int i = 0; i < Count; i++) y += HeightOf(i);
            return y;
        }

        public float ViewOffset => ((Box)Runtime.FindById("list")!).ViewOffset.Y;
    }

    public static void Register(Action<string, Action> run)
    {
        // ── RowScrollList ───────────────────────────────────────────────────

        run("RowScrollList: the rows in sight and the view's offset follow from the scroll point", () =>
        {
            var list = new Lab(ScrollBy.Pixels);
            list.Pt = new ScrollPt { Idx = 50, Px = 10f };
            list.Frame();
            Eq((50, 50 + 14), (list.First, list.Last), "row 50, part of it above, to the spare row below");
            Near(10f, list.ViewOffset, 0.01f, "offset by the pixels into row 50");
        });

        run("RowScrollList by rows: the wheel moves whole rows, keeping the rest unseen", () =>
        {
            var list = new Lab(ScrollBy.Rows);
            for (int k = 0; k < 40; k++) list.Wheel(7f);
            list.Settle();
            Eq((11, 16f), (list.Pt.Idx, list.Pt.Px), "280 px: 11 rows, 16 px held");
            Near(0f, list.ViewOffset, 0.01f, "resting on a row's top");
        });

        run("RowScrollList: at the end, by rows the last row is clipped, by pixels it rests on the bottom", () =>
        {
            var rows = new Lab(ScrollBy.Rows);
            rows.Wheel(1_000_000f);
            rows.Settle();
            Eq((987, Count), (rows.Pt.Idx, rows.Last), "by rows, the last whole row whose screenful reaches the last row");
            Eq(true, rows.Bottom >= Height, "the last row at or past the bottom");
            var pixels = new Lab(ScrollBy.Pixels);
            pixels.Wheel(1_000_000f);
            pixels.Settle();
            Near(Count * Pitch - Height, pixels.At(), 0.01, "by pixels, the last screenful");
            Near(Height, pixels.Bottom, 0.01, "the last row on the bottom");
        });

        run("RowScrollList: Target eases there", () =>
        {
            var list = new Lab(ScrollBy.Rows);
            list.Pt.Target(500);
            list.Frame();
            Eq(true, list.First < 500, "still on its way, a frame in");
            list.Settle();
            Eq((500, 500, 0f), (list.Pt.Idx, list.First, list.Pt.Off), "at rest on row 500");
        });

        // ── ScrollList ──────────────────────────────────────────────────────

        run("ScrollList by pixels: the wheel moves exactly through rows of their own heights, either way", () =>
        {
            var list = new Lab(ScrollBy.Pixels, Uneven);
            for (int k = 0; k < 40; k++) list.Wheel(7f);
            list.Frame();
            Near(280, list.At(), 0.01, "40 × 7 px down");
            for (int k = 0; k < 25; k++) list.Wheel(-9f);
            list.Frame();
            Near(55, list.At(), 0.01, "25 × 9 px up");
            list.Wheel(7000f);
            list.Frame();
            Near(7055, list.At(), 0.01, "a fling of 7,000 px down, in one frame");
            list.Wheel(-6000f);
            list.Frame();
            Near(1055, list.At(), 0.01, "and 6,000 px up");
            Near(list.Pt.Px, list.ViewOffset, 0.01, "the view offset by the pixels into the row at the top");
        });

        run("ScrollList by pixels: a row taller than the view scrolls through like any other", () =>
        {
            var list = new Lab(ScrollBy.Pixels, i => i == 100 ? 100_000f : Uneven(i));
            list.Pt = new ScrollPt { Idx = 99 };
            list.Frame();
            list.Wheel(3200f);
            list.Frame();
            Eq(100, list.Pt.Idx, "into the tall row");
            Near(3200 - Uneven(99), list.Pt.Px, 0.01, "3,200 px from the top of row 99");
            list.Wheel(100_000f - list.Pt.Px + 5f);
            list.Frame();
            Eq((101, 5f), (list.Pt.Idx, list.Pt.Px), "and out of it, 5 px into the next");
        });

        run("ScrollList by pixels: the end, reached by the wheel or by a target on the last row, is the last row on the bottom", () =>
        {
            var wheel = new Lab(ScrollBy.Pixels, Uneven);
            for (int k = 0; k < 2000 && wheel.Last < Count; k++) wheel.Wheel(32f);
            for (int k = 0; k < 20; k++) wheel.Wheel(32f);
            wheel.Frame();
            Near(wheel.Total() - Height, wheel.At(), 0.01, "by the wheel, the last screenful, and no further");
            Near(Height, wheel.Bottom, 0.01, "the last row on the bottom");
            var target = new Lab(ScrollBy.Pixels, Uneven);
            target.Pt = new ScrollPt { Idx = Count - 1 };
            target.Frame();
            target.Frame();
            Near(target.Total() - Height, target.At(), 0.01, "by a target, the same");
        });

        run("ScrollList by rows: a row a step of the wheel, the rest held; a tall row is one step", () =>
        {
            var list = new Lab(ScrollBy.Rows, i => i == 10 ? 100_000f : Uneven(i));
            for (int k = 0; k < 40; k++) list.Wheel(7f);
            list.Frame();
            Eq(8, list.Pt.Idx, "280 px is 8 steps of 32");
            Near(0.75f, list.Pt.Off, 1e-4f, "and three quarters of one held");
            Eq(0f, list.Pt.Px, "resting on a row's top");
            list.Wheel(32f * 3.25f);
            list.Frame();
            Eq(12, list.Pt.Idx, "four more: past the tall row in one");
        });

        run("ScrollList by rows: the end, reached by the wheel or by a target on the last row, clips the last row, never a gap", () =>
        {
            var wheel = new Lab(ScrollBy.Rows, Uneven);
            for (int k = 0; k < 1200; k++) wheel.Wheel(32f);
            wheel.Frame();
            int idx = wheel.Pt.Idx;
            Eq(Count, wheel.Last, "by the wheel, the last row built");
            Eq(true, wheel.Bottom >= Height && wheel.Bottom - Uneven(idx) < Height, "the last whole row that leaves no gap");
            for (int k = 0; k < 20; k++) wheel.Wheel(32f);
            wheel.Frame();
            Eq(idx, wheel.Pt.Idx, "twenty more notches move nothing");
            var target = new Lab(ScrollBy.Rows, Uneven);
            target.Pt = new ScrollPt { Idx = Count - 1 };
            target.Frame();
            target.Frame();
            Eq((idx, 0f), (target.Pt.Idx, target.Pt.Px), "by a target, the same row, on its top");
        });

        run("ScrollList: Target shows the row at once, by rows and by pixels", () =>
        {
            foreach (var by in new[] { ScrollBy.Rows, ScrollBy.Pixels })
            {
                var list = new Lab(by, Uneven);
                list.Pt.Target(500);
                list.Frame();
                list.Frame();
                Eq((500, 500), (list.Pt.Idx, list.First), $"{by}: row 500 at the top, and staying");
            }
        });

        run("ScrollList: rows of one height scroll as RowScrollList's do, by pixels", () =>
        {
            var rows = new Lab(ScrollBy.Pixels);
            var heights = new Lab(ScrollBy.Pixels, _ => Pitch);
            foreach (float px in new[] { 30f, 700f, -125f, 4000f, -2200f })
            {
                rows.Wheel(px);
                heights.Wheel(px);
                rows.Settle();
                heights.Settle();
                Near(rows.At(), heights.At(), 0.01, $"the same place after {px} px");
                Eq(rows.First, heights.First, "the same row at the top");
            }
        });

        // ── The bar ─────────────────────────────────────────────────────────

        run("Scrollbar: a press on the lane puts the thumb's centre where it is", () =>
        {
            var list = new Lab(ScrollBy.Pixels, Uneven);
            var lane = (Box)list.Runtime.FindById("list-bar")!;
            float x = lane.Rect.X + lane.Extent.X / 2f, y = lane.Rect.Y + 150f;
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left));
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, x, y, MouseButton.Left));
            list.Frame();
            var thumb = (Box)list.Runtime.FindById("list-bar-thumb")!;
            Near(150f, thumb.Rect.Y - lane.Rect.Y + thumb.Extent.Y / 2f, 1f, "the thumb's centre at the press");
            Eq(0f, list.Pt.Px, "on a row's top");
        });

        run("Scrollbar: a press on the thumb moves nothing until it drags", () =>
        {
            var list = new Lab(ScrollBy.Rows, Uneven);
            list.Pt = new ScrollPt { Idx = 300 };
            list.Frame();
            list.Frame();
            var thumb = (Box)list.Runtime.FindById("list-bar-thumb")!;
            float x = thumb.Rect.X + thumb.Extent.X / 2f, y = thumb.Rect.Y + 3f;   // near its top edge, not its centre
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left));
            list.Frame();
            Eq(300, list.Pt.Idx, "pressed and held, not moved");
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, x, y + 60f, MouseButton.Left));
            Eq(true, list.Pt.Idx > 300, "dragged down, the list follows");
        });

        run("Scrollbar: under the pointer the lane comes forward, over the thumb too", () =>
        {
            var list = new Lab(ScrollBy.Rows, Uneven);
            var thumb = (Box)list.Runtime.FindById("list-bar-thumb")!;
            float x = thumb.Rect.X + thumb.Extent.X / 2f, y = thumb.Rect.Y + thumb.Extent.Y / 2f;
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown));
            for (int f = 0; f < 30; f++) list.Frame();
            Near(1f, ((Box)list.Runtime.FindById("list-bar")!).HotT, 0.01f, "the lane hot, with the pointer on the thumb");
        });
    }
}
