using Bibui;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using Bibui.Theming;

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

    /// <summary>A list of 1000 rows filling a 400×300 surface, the scroll point it keeps and the rows it last built.</summary>
    sealed class List
    {
        public ScrollPt Pt;
        public ScrollListRows Rows;
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();

        public List()
        {
            Runtime.SetBuild(() =>
            {
                Rows = Runtime.BeginScrollList("list", ref Pt, Pitch, Count);
                for (int i = Rows.Min; i < Rows.Max; i++)
                    Runtime.BoxFromId("row-" + i).FillX().Px(height: Pitch);
                Runtime.EndScrollList(ref Pt, Rows);
                return Rows.Area.FillX().FillY();
            }, ThemeRegistry.Load("bibui-dark"), new SurfaceOptions { Width = 400, Height = (int)Height, ScaleFactor = 1f });
            Frame();
            Frame();   // laid out: the view has its height
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);

        public void Wheel(float pixels) => Frame(WindowEvent.Wheel(100f, 100f, 0f, -pixels / 32f));   // down the list when positive

        public float TargetPx => Pt.Idx * Pitch + Pt.Px;
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
            Eq((50, 50 + 16), (list.Rows.Min, list.Rows.Max), "row 50, half out of the top, to the one half in at the bottom");
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
    }
}
