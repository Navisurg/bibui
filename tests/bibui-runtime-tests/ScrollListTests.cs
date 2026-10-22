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

    /// <summary>A list of 1000 rows of 20, 35 and 50 px in turn (or all 20 when even), from the tops it is given.</summary>
    sealed class Uneven
    {
        public ScrollPt Pt;
        public readonly double[] Tops = new double[Count + 1];
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();

        public Uneven(bool even = false)
        {
            for (int i = 0; i < Count; i++) Tops[i + 1] = Tops[i] + (even ? Pitch : 20f + (i % 3) * 15f);
            Runtime.SetBuild(() =>
            {
                var panel = Runtime.BoxFromId("panel").Sized(Size.Fill, Size.Fill);
                using (Runtime.Parent(panel))
                {
                    Runtime.BeginScrollList("list", ref Pt, Tops, Count);
                    for (int i = Pt.First; i < Pt.Last; i++)
                        Runtime.BoxFromId("row-" + i).Sized(Size.Fill, Size.Px(HeightOf(i)));
                    Runtime.EndScrollList(ref Pt);
                }
                return panel;
            }, ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            Frame();
            Frame();
        }

        public float HeightOf(int i) => (float)(Tops[i + 1] - Tops[i]);

        public float ViewOffset => ((Box)Runtime.FindById("list")!).ViewOffset.Y;

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

        // ── Rows of their own heights ───────────────────────────────────────

        run("ScrollList (tops): the rows built are exactly those in view", () =>
        {
            var list = new Uneven();
            list.Pt = new ScrollPt { Idx = 400, Px = 7f };
            list.Frame();
            Eq(400, list.Pt.First, "the target row at the top");
            Near(7f, list.ViewOffset, 0.01f, "offset by the pixels into it");
            float y = -list.ViewOffset;
            for (int i = list.Pt.First; i < list.Pt.Last - 1; i++) y += list.HeightOf(i);
            Eq(true, y < Height, "the last row built starts in view");
            Eq(true, y + list.HeightOf(list.Pt.Last - 1) >= Height, "and reaches its bottom");
        });

        run("ScrollList (tops): rows above the top changing height move nothing in view", () =>
        {
            var list = new Uneven();
            list.Pt = new ScrollPt { Idx = 600, Px = 12f };
            list.Frame();
            (int first, int last, float into) = (list.Pt.First, list.Pt.Last, list.ViewOffset);
            double grew = 0;
            for (int i = 0; i < 600; i++) grew += 80f - list.HeightOf(i);
            for (int i = 0; i < Count; i++) list.Tops[i + 1] = list.Tops[i] + (i < 600 ? 80f : 20f + (i % 3) * 15f);
            Eq(true, grew > 10_000, "the rows above did grow");
            list.Frame();
            Eq((first, last), (list.Pt.First, list.Pt.Last), "the same rows in view");
            Near(into, list.ViewOffset, 0.01f, "the same pixels into the top one");
            Eq(0f, list.Pt.Off, "and nothing to ease");
        });

        run("ScrollList (tops): the wheel moves by pixels across rows of different heights", () =>
        {
            var list = new Uneven();
            list.Wheel(list.HeightOf(0) + list.HeightOf(1) + 5f);
            Eq((2, 5f), (list.Pt.Idx, list.Pt.Px), "two rows and five pixels down");
            Near(-(list.HeightOf(0) + list.HeightOf(1) + 5f), list.Pt.Off, 1e-3f, "and shown where it was, to ease from");
            list.Wheel(-1_000f);
            Eq((0, 0f), (list.Pt.Idx, list.Pt.Px), "not above the first row");
        });

        run("ScrollList (tops): at the end the last row is flush with the bottom", () =>
        {
            var list = new Uneven();
            list.Wheel(1_000_000f);
            for (int f = 0; f < 120; f++) list.Frame();
            Eq(Count, list.Pt.Last, "the last row built");
            float y = -list.ViewOffset;
            for (int i = list.Pt.First; i < list.Pt.Last; i++) y += list.HeightOf(i);
            Near(Height, y, 0.01f, "its bottom at the view's bottom");
        });

        run("ScrollList (tops): dragging the thumb moves the top pixel by its travel", () =>
        {
            var list = new Uneven();
            list.Pt = new ScrollPt { Idx = 300 };
            list.Frame();
            var thumb = (Box)list.Runtime.FindById("list-bar-thumb")!;
            var track = (Box)list.Runtime.FindById("list-bar")!;
            float x = thumb.Rect.X + thumb.Extent.X / 2f, y = thumb.Rect.Y + 2f;
            double from = list.Tops[300];
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left));
            Eq((300, 0f), (list.Pt.Idx, list.Pt.Px), "pressing the thumb moves nothing");
            float free = track.Extent.Y - thumb.Extent.Y;
            list.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, x, y + free / 10f, MouseButton.Left));
            double to = list.Tops[list.Pt.Idx] + list.Pt.Px;
            Near((float)(from + (list.Tops[Count] - Height) / 10), (float)to, 1f, "a tenth of the travel is a tenth of the pixels");
            Eq(0f, list.Pt.Off, "at once, not eased");
        });

        run("ScrollList (tops): a list that shrank under its target shows its end", () =>
        {
            var list = new Uneven();
            list.Pt = new ScrollPt { Idx = 990 };
            list.Frame();
            list.Runtime.SetBuild(() =>
            {
                var panel = list.Runtime.BoxFromId("panel").Sized(Size.Fill, Size.Fill);
                using (list.Runtime.Parent(panel))
                {
                    list.Runtime.BeginScrollList("list", ref list.Pt, list.Tops, 100);
                    list.Runtime.EndScrollList(ref list.Pt);
                }
                return panel;
            }, ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            list.Frame();
            Eq(100, list.Pt.Last, "the last of the hundred in view");
            Near((float)(list.Tops[100] - Height), (float)(list.Tops[list.Pt.Idx] + list.Pt.Px), 0.01f, "flush with the bottom");
            Eq(0f, list.Pt.Off, "at once");
        });

        run("ScrollList (tops): rows of one height scroll as the pitch list does", () =>
        {
            var pitched = new List();
            var even = new Uneven(even: true);
            foreach (float px in new[] { 30f, 700f, -125f, 4000f })
            {
                pitched.Wheel(px);
                even.Wheel(px);
                for (int f = 0; f < 30; f++) { pitched.Frame(); even.Frame(); }
                Eq((pitched.Pt.Idx, pitched.Pt.Px), (even.Pt.Idx, even.Pt.Px), $"the same target after {px} px");
                Eq(pitched.Pt.First, even.Pt.First, "the same row at the top");
                Near(((Box)pitched.Runtime.FindById("list")!).ViewOffset.Y, even.ViewOffset, 0.01f, "the same pixels into it");
            }
        });

        run("ScrollPt: Target over tops moves the target and keeps what is shown", () =>
        {
            double[] tops = [0, 20, 55, 105, 125];
            var pt = new ScrollPt { Idx = 1, Px = 5f, Off = 3f };
            double shown = tops[pt.Idx] + pt.Px + pt.Off;
            pt.Target(3, 10f, tops);
            Eq((3, 10f), (pt.Idx, pt.Px), "the new target");
            Near((float)shown, (float)(tops[pt.Idx] + pt.Px + pt.Off), 1e-4f, "the pixel shown, unmoved");
        });
    }
}
