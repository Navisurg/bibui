using System.Diagnostics.CodeAnalysis;
using Bibui.Runtime.Platform;
using Bibui.TextEdit;


namespace Bibui.Runtime;

/// <summary>
/// Runs a BibuiRuntime in a native window.
///
/// The window itself — creating it, its event pump, its framebuffer, how a
/// frame reaches the screen, the clipboard — belongs to an
/// <see cref="IPlatformHost"/>, passed in: <c>new BibuiWindow(runtime, host)</c>,
/// or chosen after <see cref="Create"/>: <c>BibuiWindow.Create(runtime).WithSilk()</c>.
/// The cross-platform host is SilkPlatformHost (GLFW + OpenGL, with a CPU
/// fallback under BIBUI_GPU=0) in Bibui.Platform.Silk; the raw Wayland
/// one is WaylandPlatformHost in Bibui.Platform.Wayland.
///
/// What stays here is the same on every platform: the frame loop and its
/// pacing, deciding whether a wake draws, and the OS boundary's translation of
/// what happened into what it means:
///   Mouse press → MouseDown with a click count (double/triple click)
///   Wheel → Scroll, Shift+wheel horizontal
///   Key → a text-edit intent (BibUiEventFromKey), reading the clipboard on paste
///
/// Call Run() from your application's main thread. It blocks until the window closes.
/// </summary>
public sealed class BibuiWindow
{
    private readonly BibuiRuntime _runtime;
    // Chosen at the latest when the window runs: Create names a default
    // without making it, so a window that then calls WithSilk() never loads
    // the Wayland host.
    private IPlatformHost? _host;
    private readonly Func<IPlatformHost>? _defaultHost;

    // ── The event queue ──────────────────────────────────────────────────────
    // The host's callbacks translate what happened into a WindowEvent and append
    // here. That is all they do: no runtime call, and therefore no render, can
    // run inside the platform's own dispatch. The frame drains this and applies
    // it at one point.
    private readonly List<WindowEvent> _events = new();

    // Restores the continuous loop, for comparing against the event-driven one.
    private static readonly bool _alwaysRender =
        Environment.GetEnvironmentVariable("BIBUI_ALWAYS_RENDER") == "1";

    private static readonly bool _profile = Environment.GetEnvironmentVariable("BIBUI_PROFILE") == "1";

    // What the window events mean, which is what the runtime acts on. Translated
    // here because this is the OS boundary — it is where a Paste can reach the
    // system clipboard, and where a key becomes a text-editing intent.
    private readonly List<BibUiEvent> _intents = new();

    // This frame's move, if one arrived. Held so later moves can update it in
    // place rather than pile up: the intent keeps its position in the sequence
    // and ends up carrying where the pointer finished.
    private BibUiEvent? _moveIntent;

    // Multi-click tracking. Here rather than in the runtime because it needs the
    // raw stream and a clock: presses that make up a double-click arrive in
    // different frames, and the runtime only ever sees one frame's worth.
    private long  _lastClickTicks;
    private float _lastClickX, _lastClickY;
    private int   _clickStreak;
    private const long MultiClickWindowMs = 500;
    private const float MultiClickRadius  = 5f;

    private WindowFrameProfile? _frameProfile;
    // Recorded per frame: a GPU host has torn its context down by the time the profile prints.
    private bool _frameProfileGpu;

    /// <summary>
    /// How many presses in a row this one is: 1, 2 or 3. A press restarts the
    /// count unless it lands close enough to the last one, soon enough after it.
    /// </summary>
    private int CountClick(float x, float y)
    {
        long nowMs = Environment.TickCount64;
        bool inWindow = (nowMs - _lastClickTicks) <= MultiClickWindowMs
                     && MathF.Abs(x - _lastClickX) <= MultiClickRadius
                     && MathF.Abs(y - _lastClickY) <= MultiClickRadius;
        _clickStreak = inWindow ? Math.Min(_clickStreak + 1, 3) : 1;
        _lastClickTicks = nowMs;
        _lastClickX = x; _lastClickY = y;
        return _clickStreak;
    }

    /// <summary>
    /// The platform this window runs on. Asking settles it: on a window from
    /// <see cref="Create"/> with no host chosen, this makes the default.
    /// </summary>
    public IPlatformHost Host
    {
        get
        {
            if (_host is null) Attach(_defaultHost!());
            return _host!;
        }
    }

    /// <summary>
    /// A window on <paramref name="host"/>. Chosen explicitly — there is no
    /// discovery, so nothing here needs reflection, and the host an app names is
    /// the only one it links. The cross-platform one is
    /// <c>new BibuiWindow(runtime, new SilkPlatformHost())</c> (Bibui.Platform.Silk:
    /// GLFW + OpenGL); on Wayland, <c>new WaylandPlatformHost()</c>
    /// (Bibui.Platform.Wayland). The runtime's wake-up and its default clipboard
    /// go through the host from here on.
    /// </summary>
    public BibuiWindow(BibuiRuntime runtime, IPlatformHost host)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(host);
        _runtime = runtime;
        Attach(host);
    }

    private BibuiWindow(BibuiRuntime runtime, Func<IPlatformHost> defaultHost)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _defaultHost = defaultHost;
    }

    /// <summary>
    /// A window for <paramref name="runtime"/> that opens on the raw Wayland
    /// host (Bibui.Platform.Wayland) unless another is chosen before it runs:
    /// <c>BibuiWindow.Create(runtime).WithSilk()</c> (Bibui.Platform.Silk:
    /// GLFW + OpenGL, cross-platform) or <c>.WithWayland()</c>. The default is
    /// not made until the window runs, so choosing another loads nothing of it.
    ///
    /// <para>The default is loaded by name (<see cref="PlatformHosts"/>), which
    /// a trimmed or Native AOT publish may have dropped, so a publish that calls
    /// this warns (IL2026). An AOT app passes the host:
    /// <c>new BibuiWindow(runtime, new WaylandPlatformHost())</c>.</para>
    /// </summary>
    [RequiresUnreferencedCode(PlatformHosts.TrimMessage)]
    public static BibuiWindow Create(BibuiRuntime runtime) => new(runtime, static () => PlatformHosts.CreateDefault());

    /// <summary>
    /// Makes <paramref name="host"/> the platform this window opens on, in
    /// place of the default <see cref="Create"/> named. Returns the window, so
    /// it chains. Only before the host is settled — by <see cref="Run"/>, by
    /// reading <see cref="Host"/>, or by the constructor that takes one.
    /// </summary>
    public BibuiWindow WithHost(IPlatformHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_host is not null)
            throw new InvalidOperationException(
                "The platform host is chosen before the window runs: call WithHost (WithSilk, WithWayland) " +
                "straight after BibuiWindow.Create(runtime).");
        Attach(host);
        return this;
    }

    private void Attach(IPlatformHost host)
    {
        _host = host;

        // How anything that is not an OS event gets the loop moving again.
        // Safe from any thread, and a no-op until the window is open.
        _runtime.WakeUp = host.Wake;

        // ClipboardGet/ClipboardSet's defaults read this; an application that
        // replaced them keeps its own.
        _runtime.PlatformHost = host;
    }

    /// <summary>
    /// Turns one window event into what it means, and raises the events that
    /// belong to the OS event rather than to its consequences. Mouse moves
    /// produce at most one intent between them: a frame needs where the pointer
    /// ended up, not the path it took.
    /// </summary>
    private void Translate(in WindowEvent e)
    {
        var modifiers = CurrentModifiers();

        switch (e.Kind)
        {
            case WindowEventKind.MouseDown:
                _intents.Add(new BibUiEvent
                {
                    Kind = BibUiEventKind.MouseDown, Button = e.Button, X = e.X, Y = e.Y,
                    Modifiers = modifiers,
                    ClickCount = e.Button == MouseButton.Left ? CountClick(e.X, e.Y) : 1,
                });
                break;

            case WindowEventKind.MouseUp:
                _intents.Add(new BibUiEvent
                {
                    Kind = BibUiEventKind.MouseUp, Button = e.Button, X = e.X, Y = e.Y,
                    Modifiers = modifiers,
                });
                break;

            case WindowEventKind.MouseMove:
                if (_moveIntent is null)
                {
                    _moveIntent = new BibUiEvent
                    {
                        Kind = BibUiEventKind.MouseMove, X = e.X, Y = e.Y,
                        Modifiers = modifiers,
                    };
                    _intents.Add(_moveIntent);
                }
                else
                {
                    _moveIntent.X = e.X;
                    _moveIntent.Y = e.Y;
                }
                break;

            case WindowEventKind.Scroll:
            {
                // Mice without a horizontal wheel/trackpad axis: Shift+wheel
                // scrolls horizontally instead (standard desktop convention).
                float dx = e.ScrollX * 32f;
                float dy = -e.ScrollY * 32f; // wheel up = content up = negative offset delta
                if ((modifiers & KeyModifiers.Shift) != 0 && dx == 0f) { dx = dy; dy = 0f; }

                _intents.Add(new BibUiEvent
                {
                    Kind = BibUiEventKind.Scroll, X = e.X, Y = e.Y, ScrollX = dx, ScrollY = dy,
                    Modifiers = modifiers,
                });
                break;
            }

            case WindowEventKind.KeyChar:
                if (!char.IsControl(e.Char))
                {
                    _intents.Add(new BibUiEvent
                    {
                        Kind = BibUiEventKind.Text, Key = Key.Unknown,
                        Char = e.Char, CodePoint = e.Char,
                    });
                }
                break;

            case WindowEventKind.KeyDown:
            {
                var keyEvent = BibUiEventFromKey(e.Key, (modifiers & KeyModifiers.Ctrl) != 0, (modifiers & KeyModifiers.Shift) != 0, _runtime.ClipboardGet);
                // The switch above decided on Ctrl and Shift; this widens what
                // the event reports to everything that was held, Alt and Super
                // included, which the two bools cannot say.
                keyEvent.Modifiers = modifiers;
                _intents.Add(keyEvent);
                break;
            }

            case WindowEventKind.KeyUp:
                _intents.Add(new BibUiEvent { Kind = BibUiEventKind.Release, Key = e.Key });
                break;
        }
    }


    /// <summary>
    /// Translates a key press into a text-edit intent, with the modifier state supplied rather
    /// than read from a keyboard. Every caller has it to hand already — the window pump captures
    /// it with the event, and <see cref="BibuiTestServer"/> synthesises presses over a socket
    /// with no keyboard at all — so both produce identical events. Duplicating this switch is
    /// how the two would drift. <paramref name="clipboard"/> is read on Ctrl+V; null means
    /// there is no clipboard, and the paste is empty.
    /// </summary>
    public static BibUiEvent BibUiEventFromKey(Key key, bool ctrl, bool shift, Func<string?>? clipboard = null)
    {
        BibUiEvent ev = new();

        ev.Key   = key;
        ev.Ctrl  = ctrl;
        ev.Shift = shift;

        // Handle modifiers
        if (ev.Shift)
        {
            ev.Flags |= TextActionFlags.KeepMark;
        }

        // All other inputs. Kind is assigned per-case so consumers can filter
        // (text widgets only care about Text/Navigate/Edit; global handlers
        // get Press for everything else).
        switch (key)
        {
            case Key.Left:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.Delta  = -1;
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.Right:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.Delta  = 1;
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.Backspace:
                ev.Kind   = BibUiEventKind.Edit;
                ev.Flags |= TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection;
                ev.Delta  = -1;
                // With ctrl, back to the start of the word, as ctrl+left moves.
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.Delete:
                ev.Kind   = BibUiEventKind.Edit;
                ev.Flags |= TextActionFlags.Delete | TextActionFlags.NoNavigationWithSelection;
                ev.Delta  = 1;
                // With ctrl, on to the end of the word, as ctrl+right moves.
                if (ev.Ctrl) ev.Flags |= TextActionFlags.WordScan;
                break;
            case Key.C when ev.Ctrl:
                ev.Copy();
                break;
            case Key.Home:
                ev.Kind   = BibUiEventKind.Navigate;
                // Backwards: LineScan takes its direction from the delta, the
                // same way WordScan does.
                ev.Delta  = -1;
                ev.Flags |= TextActionFlags.LineScan;
                break;
            case Key.End:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.Delta  = 1;
                ev.Flags |= TextActionFlags.LineScan;
                break;
            case Key.A when ev.Ctrl:
                ev.SelectAll();
                break;
            case Key.Enter:
                ev.Kind = BibUiEventKind.Submit;
                break;
            case Key.Up:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.DeltaY = -1;
                break;
            case Key.Down:
                ev.Kind   = BibUiEventKind.Navigate;
                ev.DeltaY = 1;
                break;
            case Key.X when ev.Ctrl:
                // Cut = Copy + Delete; the Copy block fills CopyString first,
                // then Delete clears the range and collapses the cursor. Said as
                // a copy that also deletes, because that is what it is.
                ev.Copy();
                ev.Flags |= TextActionFlags.Delete;
                ev.Kind = BibUiEventKind.Edit;   // it deletes, so it is an edit
                break;
            case Key.V when ev.Ctrl:
                // Read the clipboard at the WM boundary so the event stays
                // I/O-free (and so the splice is testable without touching the
                // system clipboard).
                // No clipboard (null) pastes nothing: the host's clipboard
                // arrives through the argument, as the runtime's ClipboardGet.
                ev.Paste(clipboard?.Invoke());
                break;
            default:
                ev.Kind = BibUiEventKind.Press;
                break;
        }

        return ev;
    }

    /// <summary>
    /// Opens the window and runs the event loop. Blocks until the window is closed.
    /// </summary>
    public void Run(string title = "bibui")
    {
        // BIBUI_PROFILE=1 (or BIBUI_PROFILE_FRAMES=N, which skips the per-render
        // log lines that would land inside the timed phases): split each frame
        // into render / submit / swap and print percentiles on exit. Started
        // here so cold start covers the pre-warm render and window creation.
        _frameProfile = _profile || WindowFrameProfile.CloseAfterFrames > 0 ? new WindowFrameProfile() : null;

        // Read surface size directly from the runtime (saves a pre-window render).
        var logicalW = _runtime.SurfaceWidth;
        var logicalH = _runtime.SurfaceHeight;

        // Pre-warm Skia + JIT + font resolution BEFORE the Wayland surface
        // exists. Once the host maps the surface, Hyprland's ANR
        // manager starts expecting xdg_wm_base.pong within
        // misc:anr_missed_pings cycles (default 3). The first
        // _runtime.Render() call is the slow one (SkiaSharp native load, JIT,
        // font cache build) — doing it here means there's no compositor
        // clock running yet, so there's nothing to miss. Subsequent
        // Render() calls hit Runtime's clean-output cache and are cheap.
        _runtime.Render();

        var host = Host;
        try
        {
            host.Open(_runtime, new PlatformWindowOptions(title, logicalW, logicalH));

            // Pacing: measure each iteration's elapsed time and sleep the
            // remainder of the 16.67 ms frame budget. This is the ceiling on a
            // *busy* window; an idle one runs no iterations at all, because
            // WaitEvents blocks.
            var frameSw = System.Diagnostics.Stopwatch.StartNew();
            // One statement of the rate: the same number paces this loop and
            // sets what a second means to an animation, so the two cannot
            // disagree about how fast the app is meant to be running.
            double targetMs = 1000.0 / _runtime.TargetHz;
            double nextFrameMs = 0;
            // Being awake is the gate. WaitEvents blocks until the OS has
            // something, so reaching the render means something happened —
            // there is no "did anything change" to ask, and input needs no
            // cooperation from the runtime to schedule itself.
            //
            // What input cannot schedule asks explicitly, and everything asks
            // the same way: RequestFrame, called by the app for state only it
            // can see (a camera easing, a video clock) and by the renderer for
            // what it left mid-transition.
            host.Run(() =>
            {
                // Everything that happened, applied at one point — not
                // re-entrantly inside the platform's own dispatch, which is
                // where a click used to run a render.
                var events = _events;
                host.WaitEvents(events);

                // The pointer this frame is the fold of this frame's events, and
                // it is published before anything acts on them, so a callback
                // and a page's Build afterwards agree about it.

                _intents.Clear();
                _moveIntent = null;
                foreach (var e in events) Translate(e);

                _runtime.ProcessEvents(_intents);

                // Awake is not the same as having something to draw. A present's
                // delete_id reply on Wayland, a focus change, a compositor ping,
                // and — during a drag — every motion event the fold collapses all
                // wake the wait and leave nothing behind. Drawing on those is what
                // makes an idle window cost a busy one.
                //
                // Waiting on them must not spin either, which is why this is a
                // decision and not a second wait: the pacing below runs on every
                // iteration, so a wake that draws nothing costs one loop and a
                // sleep rather than a hot spin on WaitEvents.
                bool draw = _alwaysRender
                         || events.Count > 0
                         || _runtime.FramesRequested > 0;

                if (draw && !host.IsClosing)
                {
                    var d0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var timings = host.Present(_runtime);
                    if (_frameProfile is { } fp)
                    {
                        fp.RenderMs = timings.RenderMs;
                        fp.SubmitMs = timings.SubmitMs;
                        fp.EndFrame(System.Diagnostics.Stopwatch.GetTimestamp() - d0);
                    }
                    if (host.Presentation == HostPresentation.GpuSurface) _frameProfileGpu = true;
                    _runtime.ConsumeFrame();

                    if (_frameProfile is { } fp2 && WindowFrameProfile.CloseAfterFrames > 0
                        && fp2.Frames >= WindowFrameProfile.CloseAfterFrames)
                        host.Close();
                }

                // Frames still owed: do not let the next WaitEvents block, or the
                // settle margin would wait on unrelated input. An animation in
                // progress is already counted here — the render asked for its
                // own next frame — so there is only ever one thing to consult.
                if (_alwaysRender || _runtime.FramesRequested > 0)
                {
                    host.Wake();
                }

                // A host whose Present waits for the compositor is paced by it.
                if (host.PacesFrames) return;
                nextFrameMs += targetMs;
                var remaining = nextFrameMs - frameSw.Elapsed.TotalMilliseconds;
                if (remaining > 1) Thread.Sleep((int)remaining);
                // Resync if we fell more than one frame behind (e.g. after a stall)
                else if (remaining < -targetMs) nextFrameMs = frameSw.Elapsed.TotalMilliseconds;
            });
            _frameProfile?.Print(gpu: _frameProfileGpu, host.FramebufferWidth, host.FramebufferHeight);
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>
    /// What is held, asked of the host rather than accumulated: a value that is
    /// asked for cannot go stale, so there is nothing to reset when focus is lost.
    /// Super reports as Ctrl as well, so Cmd-drag on macOS means Ctrl-drag
    /// elsewhere, and stays distinguishable for anything that cares.
    /// </summary>
    private KeyModifiers CurrentModifiers()
    {
        var m = Host.HeldModifiers;
        if ((m & KeyModifiers.Super) != 0) m |= KeyModifiers.Ctrl;
        return m;
    }
}
