using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

/// <summary>runtime.Cursor: the pointer's shape a build asks for, and what reaches the window.</summary>
static class CursorTests
{
    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void Seq(CursorShape[] expected, List<CursorShape> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new Exception($"{message}: expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]");
    }

    /// <summary>
    /// A 400×200 surface: a handle 0..100 across that asks for the resize shape,
    /// and a link 50..150 drawn over it that asks for the hand, asked first as
    /// what is on top asks. The window's SetCursor is a list of what it was told.
    /// </summary>
    sealed class Lab
    {
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        public readonly List<CursorShape> Shown = [];

        public Lab()
        {
            Runtime.WindowCursor = Shown.Add;
            Runtime.SetBuild(() =>
            {
                var root = Runtime.BoxFromId("root").Sized(Size.Fill, Size.Fill);
                var link = Runtime.BoxFromId("link", InteractionKinds.LeftPress);
                var handle = Runtime.BoxFromId("handle", InteractionKinds.LeftPress);
                // The link is on top, so it asks first.
                var onLink = Runtime.InteractionsFor(link);
                if (onLink.Hovering) Runtime.Cursor = CursorShape.Pointer;
                var onHandle = Runtime.InteractionsFor(handle);
                if (onHandle.Hovering || onHandle.Dragging) Runtime.Cursor = CursorShape.ResizeHorizontal;
                using (Runtime.Parent(root))
                {
                    handle.Sized(Size.Px(100f), Size.Px(200f));
                    (handle.FloatingX, handle.FloatingY, handle.FixedPosition) = (true, true, new(0f, 0f));
                    link.Sized(Size.Px(100f), Size.Px(200f));
                    (link.FloatingX, link.FloatingY, link.FixedPosition) = (true, true, new(50f, 0f));
                }
                return root;
            }, TestBuild.Window(400, 200));
            Frame();
            Frame();   // laid out: the boxes have their rects
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);

        public static WindowEvent Move(float x, float y) => WindowEvent.Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown);
        public static WindowEvent Down(float x, float y) => WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left);
        public static WindowEvent Drag(float x, float y) => WindowEvent.Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Left);
        public static WindowEvent Up(float x, float y) => WindowEvent.Mouse(WindowEventKind.MouseUp, x, y, MouseButton.Left);
    }

    public static void Register(Action<string, Action> run)
    {
        run("Cursor: Default with nothing asking, and the window is told nothing", () =>
        {
            var lab = new Lab();
            lab.Frame(Lab.Move(300f, 100f));
            Eq(CursorShape.Default, lab.Runtime.Cursor, "nothing under the pointer asks");
            Seq([], lab.Shown, "a window starts at Default, so nothing changed");
        });

        run("Cursor: a box under the pointer asks for its shape, and the window is told once, on the change", () =>
        {
            var lab = new Lab();
            lab.Frame(Lab.Move(20f, 100f));
            Eq(CursorShape.ResizeHorizontal, lab.Runtime.Cursor, "over the handle");
            Seq([CursorShape.ResizeHorizontal], lab.Shown, "told on the change");
            lab.Frame(Lab.Move(30f, 100f));
            lab.Frame();
            Seq([CursorShape.ResizeHorizontal], lab.Shown, "the same shape again tells it nothing");
            lab.Frame(Lab.Move(300f, 100f));
            Eq(CursorShape.Default, lab.Runtime.Cursor, "Default again as the build begins, nothing asking");
            Seq([CursorShape.ResizeHorizontal, CursorShape.Default], lab.Shown, "and told so when the pointer left");
        });

        run("Cursor: where boxes overlap, the shape is the top one's, the one that asked first", () =>
        {
            var lab = new Lab();
            lab.Frame(Lab.Move(75f, 100f));
            Eq(CursorShape.Pointer, lab.Runtime.Cursor, "over both, the link on top");
            lab.Frame(Lab.Move(120f, 100f));
            Eq(CursorShape.Pointer, lab.Runtime.Cursor, "over the link only");
            lab.Frame(Lab.Move(25f, 100f));
            Eq(CursorShape.ResizeHorizontal, lab.Runtime.Cursor, "over the handle only");
            Seq([CursorShape.Pointer, CursorShape.ResizeHorizontal], lab.Shown, "each change told once");
        });

        run("Cursor: a held box keeps its shape while the drag leaves it", () =>
        {
            var lab = new Lab();
            lab.Frame(Lab.Move(20f, 100f));
            lab.Frame(Lab.Down(20f, 100f));
            lab.Frame(Lab.Drag(300f, 100f));
            Eq(CursorShape.ResizeHorizontal, lab.Runtime.Cursor, "held, the pointer off it");
            lab.Frame(Lab.Up(300f, 100f));
            lab.Frame();
            Eq(CursorShape.Default, lab.Runtime.Cursor, "let go away from it");
            Seq([CursorShape.ResizeHorizontal, CursorShape.Default], lab.Shown, "told on each change");
        });

        run("Cursor: a window given anew starts from Default, so the shape is told to it", () =>
        {
            var lab = new Lab();
            lab.Frame(Lab.Move(20f, 100f));
            var next = new List<CursorShape>();
            lab.Runtime.WindowCursor = next.Add;
            lab.Frame();
            Seq([CursorShape.ResizeHorizontal], next, "the new window is told the shape the build asks for");
            lab.Runtime.WindowCursor = null;
            lab.Frame(Lab.Move(300f, 100f));   // no window: nothing to tell, nothing thrown
            Eq(CursorShape.Default, lab.Runtime.Cursor, "still asked for");
        });
    }
}
