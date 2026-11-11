using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.TextEdit;

/// <summary>
/// Files dragged in from another application, as the runtime takes them from a
/// window: the drag's position, the box that is its target, the drop and who
/// gets it. No window: the events are the ones a platform would send.
/// </summary>
static class DropTests
{
    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    /// <summary>
    /// A 400 × 200 surface: "left" and "right" halves that take drops, and a
    /// button-like "cover" over the top-left corner of "left" that takes only
    /// presses and asks first. What each reported is kept per frame.
    /// </summary>
    sealed class Lab
    {
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        public Interaction Left, Right, Cover;
        public readonly List<BibUiEvent> Unclaimed = [];

        // The cover asks first, then right, then left. The halves meet without
        // overlapping until this is set: then right covers the whole surface.
        public bool Overlap;

        public Lab()
        {
            Runtime.SetBuild(() =>
            {
                var cover = Runtime.BoxFromId("cover", InteractionKinds.LeftPress);
                var left = Runtime.BoxFromId("left", InteractionKinds.Drop);
                var right = Runtime.BoxFromId("right", InteractionKinds.Drop);
                Cover = Runtime.InteractionsFor(cover);
                Right = Runtime.InteractionsFor(right);
                Left = Runtime.InteractionsFor(left);
                Unclaimed.Clear();
                foreach (var ev in Runtime.UnconsumedEvents)
                    if (ev.Kind is BibUiEventKind.FileDragOver or BibUiEventKind.FileDragLeave or BibUiEventKind.FilesDropped)
                        Unclaimed.Add(Copy(ev));

                var root = new Box { ChildLayoutAxis = LayoutAxis.Y }.Sized(Size.Px(400f), Size.Px(200f));
                left.Sized(Size.Px(200f), Size.Px(200f)).At(0f, 0f);
                right.Sized(Size.Px(Overlap ? 400f : 200f), Size.Px(200f)).At(Overlap ? 0f : 200f, 0f);
                cover.Sized(Size.Px(100f), Size.Px(100f)).At(0f, 0f);
                UiNode.PushIntoParent(root, left);
                UiNode.PushIntoParent(root, right);
                UiNode.PushIntoParent(root, cover);
                return root;
            }, TestBuild.Window(400, 200));
            Runtime.Step();   // the rects the asks test against
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);

        // An event lives only until its frame ends; what the test reads after is a copy.
        static BibUiEvent Copy(BibUiEvent ev) => new() { Kind = ev.Kind, X = ev.X, Y = ev.Y, Paths = ev.Paths };
    }

    public static void Register(Action<string, Action> run)
    {
        run("File drop: the box claiming Drop under the drop takes the files, and where they fell", () =>
        {
            var lab = new Lab();
            lab.Frame(WindowEvent.FileDragOver(250f, 50f), WindowEvent.FilesDropped(250f, 60f, ["/tmp/a.png", "/tmp/b c.txt"]));
            IsTrue(lab.Right.Dropped, "the right half was dropped on");
            Eq("/tmp/a.png|/tmp/b c.txt", string.Join("|", lab.Right.DroppedFiles!), "the paths, in order");
            Eq((250f, 60f), (lab.Right.DropX, lab.Right.DropY), "where they were let go");
            IsTrue(!lab.Left.Dropped && float.IsNaN(lab.Left.DropX), "the left half was not");
            Eq(0, lab.Unclaimed.Count(e => e.Kind == BibUiEventKind.FilesDropped), "and the drop is taken: nobody after it sees it");
            Eq(null, lab.Runtime.FileDragPosition, "a drop ends the drag");

            lab.Frame();
            IsTrue(!lab.Right.Dropped, "a drop is reported in its frame only");
        });

        run("File drop: a box that does not claim it is not in the way; of two that do, the first to ask has it", () =>
        {
            var lab = new Lab();
            // Into the cover's corner: it takes presses, not files.
            lab.Frame(WindowEvent.FilesDropped(20f, 20f, ["/tmp/x"]));
            IsTrue(lab.Left.Dropped, "the drop goes through a box that only takes presses");
            IsTrue(!lab.Cover.Dropped, "which reports nothing of it");

            lab.Overlap = true;   // right now covers left as well, and asks first
            lab.Frame();
            lab.Frame(WindowEvent.FilesDropped(20f, 150f, ["/tmp/y"]));
            IsTrue(lab.Right.Dropped, "the first box to ask about the point has the drop");
            IsTrue(!lab.Left.Dropped, "the one under it, asking after, gets nothing");
        });

        run("File drop: a drop no box claims stays in the frame's events, for the app", () =>
        {
            var lab = new Lab();
            lab.Frame(WindowEvent.FilesDropped(500f, 50f, ["/home/u/notes.md"]));   // off both halves
            IsTrue(!lab.Left.Dropped && !lab.Right.Dropped, "neither half");
            var drop = lab.Unclaimed.Single(e => e.Kind == BibUiEventKind.FilesDropped);
            Eq("/home/u/notes.md", string.Join("|", drop.Paths!), "the app reads the paths off the event");
            Eq((500f, 50f), (drop.X, drop.Y), "and where");
        });

        run("File drag: the position and the target follow the drag; moves in one frame are one, where it ended", () =>
        {
            var lab = new Lab();
            Eq(null, lab.Runtime.FileDragPosition, "no drag before one enters");

            lab.Frame(WindowEvent.FileDragOver(50f, 150f), WindowEvent.FileDragOver(120f, 150f), WindowEvent.FileDragOver(250f, 150f));
            Eq(new Vector2(250f, 150f), lab.Runtime.FileDragPosition, "where the drag got to");
            Eq(1, lab.Unclaimed.Count(e => e.Kind == BibUiEventKind.FileDragOver), "three moves are one event");
            Eq(250f, lab.Unclaimed.Single(e => e.Kind == BibUiEventKind.FileDragOver).X, "carrying where it ended");
            IsTrue(lab.Right.FileDragOver && !lab.Left.FileDragOver, "the half under it is the target, the other is not");
            IsTrue(!lab.Cover.FileDragOver && !lab.Cover.Hovering, "a box that takes no drops is not lit, nor hovered");

            lab.Frame();
            IsTrue(lab.Right.FileDragOver, "the drag is still there in a frame with no events of its own");

            lab.Frame(WindowEvent.FileDragOver(20f, 20f));
            IsTrue(lab.Left.FileDragOver && !lab.Right.FileDragOver, "moved under the cover, onto the left half");

            lab.Overlap = true;
            lab.Frame();   // laid out overlapping
            lab.Frame();   // asked against it
            IsTrue(lab.Right.FileDragOver && !lab.Left.FileDragOver, "of two targets over the point, the first to ask");

            lab.Frame(WindowEvent.FileDragLeave());
            Eq(null, lab.Runtime.FileDragPosition, "a leave ends it");
            IsTrue(!lab.Right.FileDragOver && !lab.Left.FileDragOver, "and nothing is lit");
            Eq(1, lab.Unclaimed.Count(e => e.Kind == BibUiEventKind.FileDragLeave), "the leave is an event of its own");
        });

        run("File drag: a leave and a new drag in one frame are a drag that is there", () =>
        {
            var lab = new Lab();
            lab.Frame(WindowEvent.FileDragOver(50f, 150f), WindowEvent.FileDragLeave(), WindowEvent.FileDragOver(300f, 150f));
            Eq(new Vector2(300f, 150f), lab.Runtime.FileDragPosition, "the second drag stands");
            Eq("FileDragOver,FileDragLeave,FileDragOver", string.Join(",", lab.Unclaimed.Select(e => e.Kind)), "in the order they happened");
            IsTrue(lab.Right.FileDragOver, "and has its target");
        });

        run("File drop: events a test queues itself do what a window's do", () =>
        {
            var lab = new Lab();
            // A loop's frames, as a platform takes them, until none is owed.
            for (int i = 0; i < 40 && lab.Runtime.FramesRequested > 0; i++) { lab.Runtime.ConsumeFrame(); lab.Frame(); }
            Eq(0, lab.Runtime.FramesRequested, "idle, owing no frame");
            lab.Runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.FileDragOver, X = 30f, Y = 150f });
            IsTrue(lab.Runtime.FramesRequested > 0, "a drag asks for a frame");
            lab.Frame();
            IsTrue(lab.Left.FileDragOver, "the target lights");
            lab.Runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.FilesDropped, X = 30f, Y = 150f, Paths = ["/a"] });
            lab.Frame();
            IsTrue(lab.Left.Dropped && !lab.Left.FileDragOver, "the drop lands and the drag is over");
            Eq(null, lab.Runtime.FileDragPosition, "no drag left");
        });
    }
}
