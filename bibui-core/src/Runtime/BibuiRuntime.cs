using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Bibui.Core.Theming;
using Bibui.Core.Draw;
using Bibui.Core.TextEdit;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Core.Runtime;

/// <summary>
/// The core interactive runtime: builds, lays out and records frames, and holds
/// what lives across them (retained boxes, interaction state, the theme and the
/// surface). Input is queued for the boxes that ask about it.
///
/// <para>One thread: the app's loop, which applies input, builds and draws. The
/// only members safe from any other thread are <see cref="RequestFrame"/> and
/// what it calls (<see cref="WakeUp"/>), and <see cref="FramesRequested"/> and
/// <see cref="FrameCount"/>, which read counters.</para>
/// </summary>
public sealed partial class BibuiRuntime
{
    // What a frame is laid out and recorded with: the fonts text is measured
    // and drawn in, the caches recording reads, and the painter that records.
    // One set per runtime, for its life. What draws the list is the loop
    // owner's choice, and none of the runtime's business.
    private readonly Fonts _fonts = new();
    private readonly RenderCaches _caches = new();
    private readonly Painter _painter;

    private BibuiRuntime()
    {
        _painter = new Painter(_caches, _fonts);
        _measure = MeasureLeaf;
    }

    /// <summary>
    /// A runtime. It lays frames out and records them into <see cref="DrawList"/>;
    /// whoever runs the loop draws that list with the renderer it chose.
    /// </summary>
    public static BibuiRuntime Create() => new();

    /// <summary>
    /// The last frame's draws, in paint order: recorded by <see cref="EndBuild"/>,
    /// valid until the next frame records.
    /// </summary>
    public DrawList DrawList { get; } = new();

    // ── Core app state ───────────────────────────────────────────────────────
    private ThemeTokens _theme = ThemeLoader.BibuiDark();
    private SurfaceOptions _surface = new() { Width = 800, Height = 600, ScaleFactor = 1f };
    private UiNode? _lastTree;
    private Dictionary<string, Box> _interactiveBoxes = new();

    // ── Pointer state ────────────────────────────────────────────────────────
    private float _mouseX = float.NaN;
    private float _mouseY = float.NaN;

    // ── The frame's events ───────────────────────────────────────────────────
    // An intrusive list, oldest first, of events taken from a pool that the end
    // of every frame empties at once: events are born and die with a frame, so
    // steady input allocates nothing. Queued by BeginBuild (and the dispatches),
    // read by the boxes that ask during the build, dropped by EndBuild.
    private readonly List<BibUiEvent> _eventPool = new();
    private int _eventsUsed;
    private BibUiEvent? _firstEvent, _lastEvent;

    /// <summary>
    /// The display's refresh rate, which a window sets as it opens: the Silk
    /// window paces its loop to it, and a frame after a wait for input stands
    /// for one frame of it (<see cref="Dt"/>).
    /// </summary>
    public float TargetHz { get; set; } = 60f;

    /// <summary>
    /// Seconds this frame stands for: what every animation advances by. The
    /// window measures it and sets it as each frame starts (its BeginFrame),
    /// before the build, so it holds for the whole frame. A frame that follows
    /// a wait for input is one frame at <see cref="TargetHz"/>: the wait is not
    /// frame time, and a window woken after ten minutes must not step its
    /// animations by ten minutes. Without a window (headless, a test) it stays
    /// 1/60 unless set, so a run is deterministic.
    /// </summary>
    public float Dt { get; set; } = 1f / 60f;

    /// <summary>
    /// How many frames one request buys. More than one deliberately: a request
    /// is a *continuation*, not a dirty flag, and some states need a frame or
    /// two to settle. Overshooting costs three idle frames; undershooting leaves
    /// the UI stalled mid-transition until an unrelated event, which is the
    /// failure nobody would connect back to this.
    /// </summary>
    private const int FramesPerRequest = 4;

    private int _framesRequested = FramesPerRequest;

    /// <summary>
    /// Frames the loop still owes. Zero means it may go back to waiting on
    /// events. Public because a platform's BeginFrame reads it and takes one
    /// (<see cref="ConsumeFrame"/>) for the frame it starts, as it sets
    /// <see cref="WakeUp"/>.
    /// </summary>
    public int FramesRequested => Volatile.Read(ref _framesRequested);

    /// <summary>
    /// Asks for the next few frames to run.
    ///
    /// Not "something changed" — "keep going". A view waiting on an async
    /// resource asks again on every frame it is still waiting, so the producer
    /// never needs to know a UI exists: the consumer re-states its need, and
    /// stops when the need is met. Call it from a build that is mid-animation
    /// over state the renderer cannot see — a camera easing toward a target, a
    /// video advancing its clock.
    ///
    /// Wakes a parked loop as well as counting, because the two are useless
    /// apart: a counter the sleeping thread will never read schedules nothing,
    /// and a wake with nothing owed is a frame the loop declines to draw. One
    /// verb, safe from any thread — a Timer, an async continuation, a resize.
    /// </summary>
    public void RequestFrame()
    {
        Interlocked.Exchange(ref _framesRequested, FramesPerRequest);
        WakeUp?.Invoke();
    }

    /// <summary>
    /// Wakes a window that blocks between frames — for Silk, a call to
    /// <c>ContinueEvents()</c>. Set by the platform when it opens a window; invoked on any state change,
    /// from any thread (main, Timer thread, VideoService pump, etc.), so a
    /// handler must be non-blocking and thread-safe.
    /// </summary>
    public Action? WakeUp { get; set; }

    // At least one frame owed, without topping up a request already counting down.
    private void OweOneFrame()
    {
        if (Interlocked.CompareExchange(ref _framesRequested, 1, 0) == 0) WakeUp?.Invoke();
    }

    /// <summary>Called by the platform as a frame starts (its BeginFrame), before it is built. Saturates at zero.</summary>
    public void ConsumeFrame()
    {
        int n;
        do
        {
            n = Volatile.Read(ref _framesRequested);
            if (n == 0) return;
        }
        while (Interlocked.CompareExchange(ref _framesRequested, n - 1, n) != n);
    }

    // ── Mounting ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The surface frames are laid out on: logical size and scale. A window
    /// sets it when it opens and on every resize; a headless caller sets it
    /// once. Asks for a frame.
    /// </summary>
    public SurfaceOptions Surface
    {
        get => _surface;
        set
        {
            _surface = value;
            _lastTree = null;
            RequestFrame();
        }
    }

    /// <summary>
    /// Replaces the active theme and asks for a frame, which is painted in it.
    /// </summary>
    public void SetTheme(ThemeTokens theme)
    {
        _theme = theme;
        _lastTree = null;
        RequestFrame();
    }

    private static readonly bool _profile = Environment.GetEnvironmentVariable("BIBUI_PROFILE") == "1";

    // ── The frame ────────────────────────────────────────────────────────────

    // The frame's root: the runtime's own box, the size of the surface. What
    // the app builds with nothing else pushed lands under it.
    private readonly Box _root = new();

    // This frame's surface and theme, as BeginBuild found them: the whole frame
    // is laid out and painted on the same pair, whatever another thread sets.
    private SurfaceOptions _frameSurface = null!;
    private ThemeTokens _frameTheme = null!;
    private long _buildStarted;

    /// <summary>
    /// Opens a frame: everything built from here to <see cref="EndBuild"/> is
    /// this frame's tree. The runtime's root is the parent in scope, so a
    /// component built with nothing else pushed lands under it, and a node built
    /// some other way is linked under <see cref="ParentInScope"/>. One node built
    /// at the top is the frame's tree and fills the surface, as a window's
    /// content does; several are stacked top to bottom.
    ///
    /// <para><paramref name="events"/> is what the keyboard and mouse did since the
    /// last frame (<c>window.Events</c>, or a test server's): the frame's input,
    /// which the boxes built from here ask about. It is emptied. None for a frame
    /// nothing happened to, as a headless one.</para>
    /// </summary>
    public void BeginBuild(List<WindowEvent>? events = null)
    {
        if (events is not null) ApplyInput(events);
        _frameSurface = _surface;
        _frameTheme = PaintedTheme(_theme);
        Rates = MotionRates.From(_theme.Motion, Dt, Instant);
        _buildStarted = Stopwatch.GetTimestamp();
        BeginDropDowns();
        ResetForBuild(_root);
        _root.SizeX = _root.SizeY = SemanticSize.ParentPct(1f);
        _root.ChildLayoutAxis = LayoutAxis.Y;
        _parentStack.Add(_root);
    }

    /// <summary>
    /// Closes the frame <see cref="BeginBuild"/> opened: lays the tree out and
    /// records it into <see cref="DrawList"/>, then sweeps what nobody asked
    /// for. Draws nothing: the loop owner draws the list with its renderer. A
    /// frame still animating asks for the next (<see cref="RequestFrame"/>).
    /// </summary>
    public void EndBuild()
    {
        if (_parentStack.Count == 0 || _parentStack[^1] != _root)
            throw new InvalidOperationException(
                "EndBuild without its BeginBuild, or with a BeginParent still open.");
        _parentStack.RemoveAt(_parentStack.Count - 1);
        var surface = _frameSurface;
        var theme = _frameTheme;

        // The frame's tree. One node built at the top is it, and fills the
        // surface, as a window's content does; several are stacked top to
        // bottom under the root, which fills it.
        UiNode tree = _root;
        if (_root.FirstChild is { } only && only == _root.LastChild)
        {
            (_root.FirstChild, _root.LastChild) = (null, null);
            (only.Parent, only.Prev, only.Next) = (null, null, null);
            tree = only;
        }

        var t0 = _buildStarted;
        EndDropDowns(tree);
        var t1 = Stopwatch.GetTimestamp();
        var interactiveBoxes = CollectInteractiveBoxes(tree, out bool transformed);
        // Every rect is solved before anything is recorded. A text measures
        // itself in this runtime's fonts.
        (_measureTheme, _measureScale) = (theme, surface.ScaleFactor);
        _fonts.BeginFrameRuns();
        LayoutSolver.Layout(tree, System.Numerics.Vector2.Zero,
            new System.Numerics.Vector2(surface.Width, surface.Height), _measure);
        var pointerTransforms = transformed ? PointerTransforms(tree) : null;
        var t2 = Stopwatch.GetTimestamp();

        // Captured after the build, so state the app sets while building
        // lands in this frame.
        FrameInput input;
        input = CaptureFrameInput();
        _painter.Record(DrawList, tree, surface, theme, input);
        // What nobody asked for lately is evicted from the caches recording read.
        _caches.EndFrame();
        _fonts.EndFrame();
        var t3 = Stopwatch.GetTimestamp();

        if (_profile)
        {
            double freq = Stopwatch.Frequency / 1000.0;
            Console.WriteLine(
                $"[Runtime.EndBuild]  build={(t1 - t0) / freq:F2}ms  " +
                $"CollectBoxes+Layout={(t2 - t1) / freq:F2}ms  record={(t3 - t2) / freq:F2}ms");
        }

        _interactiveBoxes = interactiveBoxes;
        _pointerTransforms = pointerTransforms;
        _lastTree = tree;

        // Will the next frame differ from this one? Asked of the painter,
        // which knows because it stepped the values: a hover fade, an easing
        // height, a client draw calling ctx.Animate.
        if (_painter.IsAnimating) RequestFrame();

        // A frame that handled input built from state the input then changed:
        // what a click did shows in the frame after it, so one more is owed.
        if (_firstEvent is not null) OweOneFrame();

        SweepFrame();       // sweep whatever nobody asked for this frame
        ClearFrameEvents(); // per-frame events: drop the leftovers
        Interlocked.Increment(ref _frames);
    }

    private long _frames;

    /// <summary>Frames built so far: <see cref="EndBuild"/> counts one each. Read from any thread.</summary>
    public long FrameCount => Interlocked.Read(ref _frames);

    // What a text is measured in, for the frame being laid out; a field so the
    // delegate the solver calls is made once.
    private readonly MeasureLeaf _measure;
    private ThemeTokens? _measureTheme;
    private float _measureScale = 1f;

    // What a leaf sized by what it holds measures: a text its lines, an icon
    // its side.
    private System.Numerics.Vector2 MeasureLeaf(UiNode node, float width)
    {
        switch (node)
        {
            case Text text:
                var measured = _fonts.Measure(text, Fonts.FontOf(text, _measureTheme!, _measureScale));
                return new System.Numerics.Vector2(measured.Width, measured.Height);
            case Icon icon:
                float side = icon.Size.In(_measureTheme!);
                return new System.Numerics.Vector2(side, side);
            default:
                throw new InvalidOperationException($"{node.GetType().Name} '{node.Id}' is sized by its content, and has none.");
        }
    }

    /// <summary>
    /// Snapshots the transient interaction state into an immutable FrameInput.
    /// THE single place runtime state is captured for a frame — new frame state
    /// gets added here, in FrameInput, and in the painter, and
    /// nowhere else.
    /// </summary>
    private FrameInput CaptureFrameInput() => new()
    {
        Dt              = Dt,
        ReducedMotion   = ReducedMotion,
        Rates           = Rates,
    };

    /// <summary>
    /// The active theme tokens. Use inside build lambdas to style nodes with
    /// semantic colors (e.g. <c>BackgroundColor = UiColor.Surface</c>) instead
    /// of hardcoded hex values. Swapping the mounted theme will propagate.
    /// </summary>
    public ThemeTokens Theme => _theme;


    // ── The frame's event list ───────────────────────────────────────────────

    /// <summary>
    /// A fresh event, linked last in the frame's list: from the pool, which grows
    /// to the busiest frame's count and then stops growing.
    /// </summary>
    internal BibUiEvent PushEvent()
    {
        if (_eventsUsed == _eventPool.Count) _eventPool.Add(new BibUiEvent());
        var ev = _eventPool[_eventsUsed++];
        ev.Reset();
        if (_lastEvent is null) _firstEvent = ev; else _lastEvent.Next = ev;
        _lastEvent = ev;
        return ev;
    }

    /// <summary>
    /// The events of the current frame that nothing has eaten yet, oldest first.
    /// Use for global handlers (Escape closes a dialog, Ctrl+S saves): call
    /// <c>ev.Eat()</c> on the ones you act on, so the loops after yours do not see
    /// them. Allocates nothing.
    /// </summary>
    public UnconsumedEventList UnconsumedEvents => new(_firstEvent);

    // Every event of the frame back to the pool at once.
    private void ClearFrameEvents()
    {
        _firstEvent = _lastEvent = null;
        _eventsUsed = 0;
    }

    /// <summary>
    /// Updates the render surface to the given physical pixel dimensions and
    /// triggers a re-render. Call this from the window's FramebufferResize event.
    /// </summary>
    public void ResizeSurface(int physicalWidth, int physicalHeight, float? scaleOverride = null)
    {
        var scale = scaleOverride ?? _surface.ScaleFactor;
        _surface = new SurfaceOptions
        {
            Width       = SurfaceOptions.LogicalSize(physicalWidth,  scale),
            Height      = SurfaceOptions.LogicalSize(physicalHeight, scale),
            ScaleFactor = scale,
        };
        _lastTree = null;
        RequestFrame();
    }

    // ── Event dispatch ───────────────────────────────────────────────────────
    //
    // Nothing here renders: dispatch reads the last painted frame's rects, which
    // is the frame the user was looking at. A caller driving the runtime by hand
    // builds the next frame itself (EndBuild).

    /// <summary>
    /// Clicks the box named <paramref name="id"/>: a press and a release at its
    /// centre, through the same queue a real pointer uses, so focus, hot state
    /// and the drag anchor land exactly as they would for a person. Queued for
    /// the next frame. False, and nothing queued, when no box that claims a
    /// press carries the name.
    /// </summary>
    public bool DispatchClick(string id)
    {
        Box? box;
        _interactiveBoxes.TryGetValue(id, out box);
        if (box is null || box.Claims == InteractionKinds.None || box.Claims == InteractionKinds.Disabled) return false;

        // Last frame's geometry, which is the same frame an ask would test
        // against. A box that has never been painted has no centre to aim at.
        var pos = box.Rect;
        var size = box.Extent;
        if (size.X <= 0f || size.Y <= 0f) return false;

        float cx = pos.X + size.X / 2f;
        float cy = pos.Y + size.Y / 2f;

        // Where the centre is drawn, which is where a person would click it.
        if (PointerTransformOf(id) is { } drawn)
        {
            var centre = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(cx, cy), drawn);
            (cx, cy) = (centre.X, centre.Y);
        }
        ProcessEvents(new[]
        {
            new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = cx, Y = cy, TargetId = id },
            new BibUiEvent { Kind = BibUiEventKind.MouseUp,   Button = MouseButton.Left, X = cx, Y = cy, TargetId = id },
        });
        return true;
    }

    public float ScaleFactor => _surface.ScaleFactor;
    public int SurfaceWidth  => _surface.Width;
    public int SurfaceHeight => _surface.Height;

    /// <summary>The tree the last frame laid out and painted.</summary>
    internal UiNode? LastTree => _lastTree;

    /// <summary>
    /// The node named <paramref name="id"/> in the tree the last frame laid out
    /// and painted, or null: for tests and the test server, which read its
    /// <see cref="UiNode.Rect"/>, <see cref="UiNode.Extent"/> and content.
    ///
    /// <para>Read it between frames, on the loop's thread. The next build
    /// resets and reuses the nodes it describes, so read what is needed and do
    /// not keep the node across a frame.</para>
    /// </summary>
    public UiNode? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Find(LastTree, id);

        static UiNode? Find(UiNode? node, string id)
        {
            if (node is null || node.Id == id) return node;
            for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
                if (Find(child, id) is { } found) return found;
            return null;
        }
    }

    /// <summary>The boxes with an id that claim something: what a named dispatch can reach.</summary>
    private static Dictionary<string, Box> CollectInteractiveBoxes(UiNode root, out bool transformed)
    {
        var boxes = new Dictionary<string, Box>();
        bool any = false;
        Collect(root);
        transformed = any;
        return boxes;

        void Collect(UiNode node)
        {
            any |= node.Style.HasTransform;
            if (node is Box { Id: { } id } box && box.Claims != InteractionKinds.None)
                boxes[id] = box;
            for (UiNode? c = node.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
                Collect(c);
        }
    }
}
