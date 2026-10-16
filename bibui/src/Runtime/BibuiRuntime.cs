using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Bibui.Theming;
using Bibui.Draw;
using Bibui.Renderer;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// The core interactive runtime. Owns the app's build function, current state,
/// theme, render surface, and last rendered output. Event dispatch methods
/// (DispatchClick, DispatchFill) queue input for the boxes that ask about it.
///
/// Thread safety: all public members are guarded by an internal lock. Rendering
/// (CPU-bound Skia) is performed outside the lock to avoid blocking dispatch.
/// </summary>
public sealed partial class BibuiRuntime
{
    // Lays out and records every frame; what draws the list is the loop
    // owner's choice, and none of the runtime's business.
    private readonly BibuiRenderer Renderer = new();

    private BibuiRuntime() { }

    /// <summary>
    /// A runtime. It lays frames out and records them into <see cref="DrawList"/>;
    /// whoever runs the loop draws that list with the renderer it chose.
    /// </summary>
    public static BibuiRuntime Create() => new();

    /// <summary>
    /// The last frame's draws, in paint order: recorded by <see cref="EndBuild"/>.
    /// Valid until the next frame records; read it under <see cref="FrameLock"/>
    /// when another thread may build.
    /// </summary>
    public DrawList DrawList => Renderer.DrawList;
    private readonly object _syncRoot = new();
    // Held around a frame: building and recording it, and whatever draws the
    // list it recorded. Two threads build frames (the window's loop and the
    // test server); without it one resets the list the other is drawing.
    private readonly object _renderLock = new();

    /// <summary>
    /// The frame's lock: <see cref="EndBuild"/> takes it, and so must a caller
    /// that draws <see cref="DrawList"/> while another thread may build the next
    /// frame (a window and <see cref="BibuiTestServer"/>). Re-entrant.
    /// </summary>
    public object FrameLock => _renderLock;

    // ── Core app state ───────────────────────────────────────────────────────
    private Func<UiNode>? _build;
    private ThemeTokens _theme = ThemeLoader.BibuiDark();
    private SurfaceOptions _surface = new();
    private UiNode? _lastTree;
    private Dictionary<string, Box> _interactiveBoxes = new();

    // ── Pointer state ────────────────────────────────────────────────────────
    private float _mouseX = float.NaN;
    private float _mouseY = float.NaN;

    // ── Per-frame UI event queue ─────────────────────────────────────────────
    // Pushed by BibuiWindow on every OS key event; iterated by widgets in the
    // build callback via UnconsumedEvents.
    // Cleared at the end of each EndBuild().
    private readonly List<BibUiEventHandle> _frameEvents = new();

    /// <summary>
    /// Frames per second the loop aims for. The animation clock is derived from
    /// it, so this is also what "one second" means to <see cref="Dt"/>.
    /// </summary>
    public float TargetHz { get; set; } = 60f;

    /// <summary>
    /// Seconds per frame — nominal, <c>1/TargetHz</c>, never measured.
    ///
    /// Animation advances a fixed amount per frame rather than by elapsed time.
    /// That is the point on a loop that blocks when idle: a window woken after
    /// ten minutes reports an ordinary frame, not a 600-second one, so the first
    /// interaction after a quiet spell animates instead of snapping straight to
    /// its end state. Measuring, clamped or not, cannot tell that gap apart from
    /// a stall. The cost is that a machine missing its frame budget animates
    /// slower in wall-clock than the numbers say, which is the trade a nominal
    /// <c>1/target_hz</c> makes deliberately.
    /// </summary>
    public float Dt => 1f / TargetHz;

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
    /// Frames the host still owes. Zero means the loop may go back to waiting
    /// on events. Public because a host driving its own loop needs it, in the
    /// same way it needs <see cref="WakeUp"/> and <see cref="ConsumeFrame"/>.
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
    /// Wakes a host that blocks between frames — for BibuiWindow, a call to
    /// <c>ContinueEvents()</c>. Set by the host; invoked on any state change,
    /// from any thread (main, Timer thread, VideoService pump, etc.), so a
    /// handler must be non-blocking and thread-safe.
    /// </summary>
    public Action? WakeUp { get; set; }

    /// <summary>Called by the host after a frame is drawn. Saturates at zero.</summary>
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
    /// Mounts a static (non-interactive) build function. Use this for read-only
    /// inspection of a fixed UI tree. For interactive apps, use MountStateful.
    /// </summary>
    public void Mount(Func<UiNode> build, SurfaceOptions? surface = null, string theme = "bibui-dark")
        => MountStateful<object?>(null, (_, __) => build(), surface, theme);

    /// <summary>Mounts a static build function with an explicit <see cref="ThemeTokens"/> (custom theme).</summary>
    public void Mount(Func<UiNode> build, ThemeTokens theme, SurfaceOptions? surface = null)
        => MountStateful<object?>(null, (_, __) => build(), theme, surface);

    /// <summary>
    /// Replaces the active theme on a mounted runtime and invalidates so the next render re-tints.
    /// The supported, thread-safe way to swap themes at runtime (no reflection on private state).
    /// </summary>
    public void SetTheme(ThemeTokens theme)
    {
        lock (_syncRoot)
        {
            _theme = theme;
            _lastTree = null;
        }
        RequestFrame();
    }

    /// <summary>
    /// Mounts a stateful app. The build function receives the current state and
    /// a <see cref="SetState{T}"/> handle. Call <c>setState.Set(newState)</c> from
    /// synchronous handlers and <c>setState.Update(s =&gt; ...)</c> from async
    /// continuations (post-<c>await</c> code) to avoid stale-closure clobbering.
    /// </summary>
    public void MountStateful<T>(
        T initialState,
        Func<T, SetState<T>, UiNode> build,
        SurfaceOptions? surface = null,
        string theme = "bibui-dark")
        => MountStatefulCore(initialState, build, ThemeRegistry.Load(theme), surface);

    /// <summary>
    /// Mounts a stateful app with an explicit <see cref="ThemeTokens"/> object, so apps can drive a
    /// fully custom theme (e.g. a custom accent that flows into every component) without reflecting
    /// onto private runtime state.
    /// </summary>
    public void MountStateful<T>(
        T initialState,
        Func<T, SetState<T>, UiNode> build,
        ThemeTokens theme,
        SurfaceOptions? surface = null)
        => MountStatefulCore(initialState, build, theme, surface);

    private void MountStatefulCore<T>(
        T initialState,
        Func<T, SetState<T>, UiNode> build,
        ThemeTokens theme,
        SurfaceOptions? surface)
    {
        object? boxedState = initialState;

        void Set(T newState)
        {
            lock (_syncRoot)
            {
                boxedState = newState;
            }
            RequestFrame();
        }

        void Update(Func<T, T> updater)
        {
            lock (_syncRoot)
            {
                boxedState = updater((T)boxedState!);
            }
            RequestFrame();
        }

        var setState = new SetState<T>(Set, Update);

        lock (_syncRoot)
        {
            _build = () => build((T)boxedState!, setState);
            _surface = surface ?? new SurfaceOptions { Width = 800, Height = 600, ScaleFactor = 1f };
            _theme = theme;
            _lastTree = null;
            _interactiveBoxes = new();
        }
        RequestFrame();
    }

    private static readonly bool _profile = Environment.GetEnvironmentVariable("BIBUI_PROFILE") == "1";

    // ── The frame ────────────────────────────────────────────────────────────

    /// <summary>
    /// One frame: runs the mounted build, lays the tree out and records it into
    /// <see cref="DrawList"/>, then sweeps what nobody asked for. Draws nothing:
    /// the loop owner draws the list with its renderer. A frame still animating
    /// asks for the next (<see cref="RequestFrame"/>).
    /// </summary>
    public void EndBuild()
    {
        lock (_renderLock)
        {
            Func<UiNode>? build;
            SurfaceOptions surface;
            ThemeTokens theme;
            lock (_syncRoot)
            {
                build = _build ?? throw new InvalidOperationException("Runtime not mounted. Call Mount or MountStateful first.");
                surface = _surface;
                theme = PaintedTheme(_theme);
            }

            var t0 = Stopwatch.GetTimestamp();
            BeginDropDowns();
            var tree = build();
            EndDropDowns(tree);
            var t1 = Stopwatch.GetTimestamp();
            var interactiveBoxes = CollectInteractiveBoxes(tree, out bool transformed);
            // Every rect is solved before anything is recorded.
            Renderer.Layout(tree, surface, theme);
            var pointerTransforms = transformed ? PointerTransforms(tree) : null;
            var t2 = Stopwatch.GetTimestamp();

            // Captured after build(), so state the app sets while building lands
            // in this frame.
            FrameInput input;
            lock (_syncRoot) input = CaptureFrameInput();
            Renderer.Record(tree, surface, theme, input);
            var t3 = Stopwatch.GetTimestamp();

            if (_profile)
            {
                double freq = Stopwatch.Frequency / 1000.0;
                Console.WriteLine(
                    $"[Runtime.EndBuild]  build={(t1 - t0) / freq:F2}ms  " +
                    $"CollectBoxes+Layout={(t2 - t1) / freq:F2}ms  record={(t3 - t2) / freq:F2}ms");
            }

            lock (_syncRoot)
            {
                _interactiveBoxes = interactiveBoxes;
                _pointerTransforms = pointerTransforms;
                _lastTree = tree;
            }

            // Will the next frame differ from this one? Asked of the renderer,
            // which knows because it stepped the values: a hover fade, an easing
            // height, a client draw calling ctx.Animate.
            if (Renderer.IsAnimating) RequestFrame();

            SweepFrame();       // sweep whatever nobody asked for this frame
            ClearFrameEvents(); // per-frame events: drop the leftovers
        }
    }

    /// <summary>
    /// Snapshots the transient interaction state into an immutable FrameInput.
    /// THE single place runtime state is captured for a frame — new frame state
    /// gets added here, in FrameInput, and in the painter, and
    /// nowhere else. Must be called under _syncRoot.
    /// </summary>
    private FrameInput CaptureFrameInput() => new()
    {
        Dt              = Dt,
        ReducedMotion   = ReducedMotion,
    };

    /// <summary>
    /// The active theme tokens. Use inside build lambdas to style nodes with
    /// semantic colors (e.g. <c>BackgroundColor = UiColor.Surface</c>) instead
    /// of hardcoded hex values. Swapping the mounted theme will propagate.
    /// </summary>
    public ThemeTokens Theme { get { lock (_syncRoot) return _theme; } }


    // ── UI event queue ───────────────────────────────────────────────────────
    // All access is main-thread-only: BibuiWindow enqueues from the GLFW input
    // pump, the build callback iterates during RenderLocked, ClearFrameEvents
    // runs at the end of RenderLocked. No locks needed.

    /// <summary>
    /// Pushes a UI event onto the per-frame queue. Returns its handle, so the
    /// caller can eat it once it knows whether anything acted on it.
    /// </summary>
    internal BibUiEventHandle EnqueueEvent(BibUiEvent ev)
    {
        var handle = new BibUiEventHandle(ev);
        _frameEvents.Add(handle);
        return handle;
    }

    /// <summary>
    /// All events for the current frame that haven't been eaten yet. Use for
    /// global handlers (Escape closes dialog, Ctrl+S saves, etc.). Iterate and
    /// call <c>handle.Eat()</c> on events you consume so subsequent iterators
    /// see them as gone.
    /// </summary>
    public IEnumerable<BibUiEventHandle> UnconsumedEvents
        => _frameEvents.Where(h => !h.Consumed);

    private void ClearFrameEvents() => _frameEvents.Clear();

    /// <summary>
    /// Updates the render surface to the given physical pixel dimensions and
    /// triggers a re-render. Call this from the window's FramebufferResize event.
    /// </summary>
    public void ResizeSurface(int physicalWidth, int physicalHeight, float? scaleOverride = null)
    {
        lock (_syncRoot)
        {
            var scale = scaleOverride ?? _surface.ScaleFactor;
            _surface = new SurfaceOptions
            {
                Width       = SurfaceOptions.LogicalSize(physicalWidth,  scale),
                Height      = SurfaceOptions.LogicalSize(physicalHeight, scale),
                ScaleFactor = scale,
            };
            _lastTree = null;
        }
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
    /// and the drag anchor land exactly as they would for a person. No-ops if
    /// no box that claims a press carries the name.
    /// </summary>
    public void DispatchClick(string id)
    {
        Box? box;
        lock (_syncRoot) _interactiveBoxes.TryGetValue(id, out box);
        if (box is null || box.Claims == InteractionKinds.None || box.Claims == InteractionKinds.Disabled) return;

        // Last frame's geometry, which is the same frame an ask would test
        // against. A box that has never been painted has no centre to aim at.
        var pos = box.Rect;
        var size = box.Extent;
        if (size.X <= 0f || size.Y <= 0f) return;

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
    }

    /// <summary>
    /// Replaces the whole text of the field named <paramref name="id"/> with
    /// <paramref name="newValue"/>, the way a person would: presses the field —
    /// which its application focuses — builds a frame, then selects all and types
    /// the value, or deletes for an empty one. The keys are queued; the next
    /// frame applies them. No-ops if no box that asks carries the name.
    /// </summary>
    public void DispatchFill(string id, string newValue)
    {
        ArgumentNullException.ThrowIfNull(newValue);
        bool asks;
        lock (_syncRoot) asks = _interactiveBoxes.ContainsKey(id);
        if (!asks) return;

        DispatchClick(id);
        EndBuild();
        var keys = new List<BibUiEvent> { BibuiWindow.BibUiEventFromKey(Key.A, ctrl: true, shift: false) };
        if (newValue.Length == 0)
            keys.Add(BibuiWindow.BibUiEventFromKey(Key.Backspace, ctrl: false, shift: false));
        foreach (var rune in newValue.EnumerateRunes())
            keys.Add(new BibUiEvent { Kind = BibUiEventKind.Text, Char = rune.IsBmp ? (char)rune.Value : '\0', CodePoint = (uint)rune.Value });
        ProcessEvents(keys);
    }

    public float ScaleFactor { get { lock (_syncRoot) return _surface.ScaleFactor; } }
    public int SurfaceWidth  { get { lock (_syncRoot) return _surface.Width;  } }
    public int SurfaceHeight { get { lock (_syncRoot) return _surface.Height; } }

    /// <summary>The tree the last frame laid out and painted.</summary>
    internal UiNode? LastTree { get { lock (_syncRoot) return _lastTree; } }

    /// <summary>
    /// The node named <paramref name="id"/> in the tree the last frame laid out
    /// and painted, or null: for tests and the test server, which read its
    /// <see cref="UiNode.Rect"/>, <see cref="UiNode.Extent"/> and content.
    ///
    /// <para>Taken under the render lock, so never in the middle of a build.
    /// The next build resets and reuses the nodes it describes again, so read
    /// what is needed and do not keep the node across a frame.</para>
    /// </summary>
    public UiNode? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_renderLock) return Find(LastTree, id);

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
