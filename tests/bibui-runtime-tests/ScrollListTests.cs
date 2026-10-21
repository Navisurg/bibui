using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;

/// <summary>The scroll list's arithmetic: what a ScrollPt shows, and how the wheel, the bar and Target move it.</summary>
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

    /// <summary>A list of 1000 rows filling a 400×300 surface, and the scroll point it keeps.</summary>
    sealed class List
    {
        public ScrollPt Pt;
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();

        public List()
        {
            Runtime.SetBuild(() =>
            {
                var panel = Runtime.BoxFromId("panel").Sized(Size.Fill, Size.Fill);
                using (Runtime.Parent(panel))
                {
                    Runtime.BeginScrollList("list", ref Pt, Pitch, Count);
                    for (int i = Pt.First; i < Pt.Last; i++)
                        Runtime.BoxFromId("row-" + i).Sized(Size.Fill, Size.Px(Pitch));
                    Runtime.EndScrollList(ref Pt);
                }
                return panel;
            }, ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            Frame();
            Frame();   // laid out: the view has its height
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);

        public void Wheel(float pixels) => Frame(WindowEvent.Wheel(100f, 100f, 0f, -pixels / 32f));   // down the list when positive

        public float TargetPx => Pt.Idx * Pitch + Pt.Px;
    }

    /// <summary>A list of 1000 rows of 20, 35 and 50 px in turn (or all 20 when even), with an estimate that is wrong.</summary>
    sealed class Uneven
    {
        public ScrollPt Pt;
        public readonly RowHeights Rows = new(30f);
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        static bool s_even;

        public static float HeightOf(int i) => s_even ? Pitch : 20f + (i % 3) * 15f;

        public Uneven(bool even = false)
        {
            s_even = even;
            Runtime.SetBuild(() =>
            {
                var panel = Runtime.BoxFromId("panel").Sized(Size.Fill, Size.Fill);
                using (Runtime.Parent(panel))
                {
                    Runtime.BeginScrollList("list", ref Pt, Rows, Count);
                    for (int i = Pt.First; i < Pt.Last; i++)
                    {
                        var row = Runtime.BeginScrollRow(ref Pt, i);
                        Runtime.Box().Sized(Size.Fill, Size.Px(HeightOf(i)));
                        Runtime.EndScrollRow(row);
                    }
                    Runtime.EndScrollList(ref Pt);
                }
                return panel;
            }, ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            Frame();
            Frame();
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);

        public void Wheel(float pixels) => Frame(WindowEvent.Wheel(100f, 100f, 0f, -pixels / 32f));
    }

    public static void Register(Action<string, Action> run)
    {
        run("ScrollPt: Target moves the target and keeps what is shown", () =>
        {
            var pt = new ScrollPt { Idx = 10, Px = 5f, Off = 0.25f };
            float shown = pt.Idx + pt.Px / Pitch + pt.Off;
            pt.Target(40, 15f, Pitch);
            Eq((40, 15f), (pt.Idx, pt.Px), "the new target");
            Near(shown, pt.Idx + pt.Px / Pitch + pt.Off, 1e-4f, "the row shown, unmoved");
        });

        run("ScrollList: the rows in sight and the view's offset follow from the scroll point", () =>
        {
            var list = new List();
            list.Pt = new ScrollPt { Idx = 50, Px = 10f, Off = 0f };
            list.Frame();
            Eq((50, 50 + 16), (list.Pt.First, list.Pt.Last), "row 50, half out of the top, to the one half in at the bottom");
            Near(10f, ((Box)list.Runtime.FindById("list")!).ViewOffset.Y, 0.01f, "offset by the pixels into row 50");
            Eq(15, list.Pt.ViewRows, "whole rows in view");
        });

        run("ScrollList: the wheel moves the target by its pixels, and stops at the first and last rows", () =>
        {
            var list = new List();
            list.Wheel(30f);
            Eq((1, 10f), (list.Pt.Idx, list.Pt.Px), "a row and a half, not rounded to a row");
            Near(-1.5f, list.Pt.Off, 1e-4f, "and shown where it was, to ease from");
            list.Wheel(-500f);
            Eq((0, 0f), (list.Pt.Idx, list.Pt.Px), "not above the first row");
            list.Wheel(1_000_000f);
            Near(Count * Pitch - Height, list.TargetPx, 0.01f, "not below the last row, flush with the bottom");
        });

        run("ScrollList: dragging the thumb moves the top row by its travel from where it was pressed", () =>
        {
            var list = new List();
            list.Pt = new ScrollPt { Idx = 300 };
            list.Frame();
            var thumb = (Box)list.Runtime.FindById("list-bar-thumb")!;
            var track = (Box)list.Runtime.FindById("list-bar")!;
            float x = thumb.Rect.X + thumb.Extent.X / 2f, y = thumb.Rect.Y + 2f;   // near its top edge, not its centre

            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left));
            Eq(300, list.Pt.Idx, "pressing the thumb moves nothing");
            float free = track.Extent.Y - thumb.Extent.Y;
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, x, y + free / 10f, MouseButton.Left));
            Near(300f + (Count - Height / Pitch) / 10f, list.Pt.Idx, 1f, "a tenth of the travel is a tenth of the rows");
            Eq(0f, list.Pt.Off, "at once, not eased");
        });

        // ── Rows of different heights ───────────────────────────────────────

        run("ScrollList (uneven): the rows built fill the view, with none missing and none past it", () =>
        {
            var list = new Uneven();
            list.Pt = new ScrollPt { Idx = 400, Px = 7f };
            for (int f = 0; f < 3; f++) list.Frame();
            var view = (Box)list.Runtime.FindById("list")!;
            float top = Uneven.HeightOf(list.Pt.First) - view.ViewOffset.Y, y = -view.ViewOffset.Y;
            for (int i = list.Pt.First; i < list.Pt.Last; i++) y += Uneven.HeightOf(i);
            Eq(true, y >= Height, "the rows built reach the bottom of the view");
            Eq(true, y - Uneven.HeightOf(list.Pt.Last - 1) - Uneven.HeightOf(list.Pt.Last - 2) < Height, "at most one spare row past it");
            Eq(true, top > 0f, "the first row built is in view");
        });

        run("ScrollList (uneven): measuring the rows above the top moves nothing in view", () =>
        {
            var list = new Uneven();
            list.Pt = new ScrollPt { Idx = 600, Px = 12f };
            list.Frame();
            list.Frame();
            int first = list.Pt.First;
            float into = ((Box)list.Runtime.FindById("list")!).ViewOffset.Y;
            double before = list.Rows.Top(600);
            // The rows above turn out to be 80 px each, not the ~35 estimated: row 600 now starts ~27,000 px further down.
            for (int i = 0; i < 600; i++) list.Rows.Measure(i, 80f);
            Eq(true, Math.Abs(list.Rows.Top(600) - before) > 100, "the rows above did move the estimate");
            list.Frame();
            Eq(first, list.Pt.First, "the same row at the top");
            Near(into, ((Box)list.Runtime.FindById("list")!).ViewOffset.Y, 0.01f, "the same pixels into it");
            Eq(0f, list.Pt.Off, "and nothing to ease");
        });

        run("ScrollList (uneven): the wheel moves by pixels across rows of different heights", () =>
        {
            var list = new Uneven();
            for (int f = 0; f < 3; f++) list.Frame();
            list.Wheel(Uneven.HeightOf(0) + Uneven.HeightOf(1) + 5f);
            Eq((2, 5f), (list.Pt.Idx, list.Pt.Px), "two rows and five pixels down");
            list.Wheel(-1_000f);
            Eq((0, 0f), (list.Pt.Idx, list.Pt.Px), "not above the first row");
        });

        run("ScrollList (uneven): at the end the last row is flush with the bottom", () =>
        {
            var list = new Uneven();
            list.Frame();
            list.Wheel(1_000_000f);
            for (int f = 0; f < 120; f++) list.Frame();
            list.Wheel(1_000_000f);
            for (int f = 0; f < 120; f++) list.Frame();
            Eq(Count, list.Pt.Last, "the last row built");
            var view = (Box)list.Runtime.FindById("list")!;
            float y = -view.ViewOffset.Y;
            for (int i = list.Pt.First; i < list.Pt.Last; i++) y += Uneven.HeightOf(i);
            Near(Height, y, 0.5f, "its bottom at the view's bottom");
        });

        run("ScrollList (uneven): rows of one height scroll as the fixed-pitch list does", () =>
        {
            var fixedList = new List();
            var even = new Uneven(even: true);
            for (int f = 0; f < 3; f++) even.Frame();
            foreach (float px in new[] { 30f, 700f, -125f, 4000f })
            {
                fixedList.Wheel(px);
                even.Wheel(px);
                for (int f = 0; f < 30; f++) { fixedList.Frame(); even.Frame(); }
                Eq((fixedList.Pt.Idx, fixedList.Pt.Px), (even.Pt.Idx, even.Pt.Px), $"the same target after {px} px");
                Eq((fixedList.Pt.First, fixedList.Pt.Last - 1), (even.Pt.First, even.Pt.Last - 2), "the same rows, and one spare");
            }
        });
    }
}
