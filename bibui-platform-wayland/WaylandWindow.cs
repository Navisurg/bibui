using System.Diagnostics;
using System.Runtime.InteropServices;
using Bibui.Core;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>How a <see cref="WaylandWindow"/> is set up, beyond its title and size.</summary>
public sealed record WaylandHostOptions
{
    /// <summary>xdg_toplevel app id — what the compositor's window rules match. Default "bibui".</summary>
    public string AppId { get; init; } = "bibui";

    /// <summary>Minimum logical width the compositor may give the window.</summary>
    public int MinWidth { get; init; } = 240;

    /// <summary>Minimum logical height the compositor may give the window.</summary>
    public int MinHeight { get; init; } = 160;

    /// <summary>Buffers in rotation: 2 is double buffering, 3 (the default) triple.</summary>
    public int Buffers { get; init; } = 3;

    /// <summary>
    /// How long <see cref="WaylandWindow.GetEvents"/> waits for the compositor's
    /// frame callback, or for a buffer to come back, before drawing anyway. A
    /// compositor sends no frame callbacks to a window it is not showing
    /// (another workspace); the bound keeps the loop from stalling on one, at
    /// the cost of drawing a hidden window this often while it animates.
    /// </summary>
    public TimeSpan FrameWait { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// A window on raw Wayland: libwayland-client and the generated bindings, an
/// xdg-shell toplevel, and frames drawn on the CPU straight into wl_shm
/// buffers. No GLFW, no GL, no Silk.NET windowing. Linux under a Wayland
/// compositor only (Hyprland is the one it is built for). It knows no runtime:
/// the loop hands the runtime <see cref="Frame"/> and <see cref="Events"/>, and
/// wires <see cref="Wake"/> and the clipboard to it.
///
/// <list type="bullet">
/// <item><b>Drawing</b>: <see cref="Cpu"/> holds a free shm buffer from
/// <see cref="BeginDraw"/> to <see cref="EndDraw"/>; a renderer bound to it
/// (<c>new ManagedRenderer(window.Cpu)</c>) draws there, with no copy.
/// <c>EndDraw</c> attaches it, damages it whole, and commits it with a frame
/// callback, whether or not the renderer repainted all of it.</item>
/// <item><b>Pacing</b>: <see cref="GetEvents"/> without waiting waits for the
/// previous frame's callback, so the compositor sets the frame rate, as vsync
/// would (<see cref="PacesFrames"/>). A pending configure skips the wait: a
/// resize is drawn at once. <see cref="Frame"/>'s refresh rate is the output's
/// the window is on.</item>
/// <item><b>Buffers</b>: triple-buffered by default, each tracked from commit
/// to wl_buffer.release (<see cref="BufferPool{TBuffer}"/>); a resize retires
/// the old ones as the compositor lets go of them.</item>
/// <item><b>Configure</b>: the xdg handshake is state
/// (<see cref="ToplevelConfigure"/>); the new size is in <see cref="Frame"/>,
/// <see cref="OnRefresh"/> draws it, and the serial is acked just before the
/// commit of the first frame drawn for it.</item>
/// <item><b>Ping/pong</b> is answered inside dispatch, which runs while
/// <see cref="GetEvents"/> waits, for events or for the compositor, so a busy
/// or hidden window never trips the compositor's not-responding check.</item>
/// <item><b>Wake</b> is an eventfd polled beside the display's fd.</item>
/// </list>
///
/// <para>Input, the clipboard, and HiDPI and cursors
/// are features (<see cref="IWaylandFeature"/>) that bind their own globals and
/// queue events through the host. The clipboard is <see cref="WaylandClipboard"/>;
/// input is <see cref="WaylandInput"/>, whose key-repeat timerfd
/// <see cref="Pump"/> polls beside the display.</para>
///
/// <code>
/// using var window = WaylandWindow.Open(new("My app", 960, 640));
/// runtime.WakeUp = window.Wake;
/// runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
/// window.OnRefresh = runtime.RequestFrame;
/// using var managed = new ManagedRenderer(window.Cpu);
/// while (!window.IsClosing)
/// {
///     bool idle = runtime.FramesRequested == 0 &amp;&amp; window.Events.Count == 0;
///     var events = window.GetEvents(wait: idle);
///     if (runtime.FramesRequested == 0 &amp;&amp; events.Count == 0) continue;
///     runtime.ConsumeFrame();
///     runtime.BeginBuild(window.Frame, events);
///     // ... build ...
///     runtime.EndBuild();
///     WaylandWindow.BeginDraw(window);
///     managed.Draw(runtime.DrawList);
///     WaylandWindow.EndDraw(window);
/// }
/// </code>
///
/// <para>Scale (<see cref="WaylandScaleAndCursor"/>): the buffer is
/// round(logical × scale) at the compositor's preferred fractional scale, shown
/// at the logical size through wp_viewport; positions stay logical.</para>
/// </summary>
public sealed unsafe class WaylandWindow : IDisposable
{
    private readonly WaylandHostOptions _options;

    /// <summary>
    /// The shm buffer this frame is drawn into, from <see cref="BeginDraw"/>
    /// to <see cref="EndDraw"/>: for a renderer bound to it. Buffers are
    /// in rotation, so it holds a different one from frame to frame.
    /// </summary>
    public Bibui.Core.Draw.CpuTarget Cpu { get; } = new();

    // The buffer Cpu points at, between BeginDraw and EndDraw.
    private ShmBuffer? _buffer;

    /// <summary>
    /// What the keyboard and mouse did since the last frame, in order, handed over
    /// from the listeners' queue by <see cref="GetEvents"/>. The loop hands it to
    /// the runtime (<c>runtime.BeginBuild(window.Frame, window.Events)</c>), which empties it.
    /// </summary>
    public readonly List<WindowEvent> Events = [];

    // Connection and globals
    private nint _display;
    private WlRegistry _registry;
    private WlCompositor _compositor;
    private WlShm _shm;
    private XdgWmBase _wmBase;
    private ZxdgDecorationManagerV1 _decorationManager;

    // The window
    private WlSurface _surface;
    private XdgSurface _xdgSurface;
    private XdgToplevel _toplevel;
    private ZxdgToplevelDecorationV1 _decoration;
    private WlCallback _frameCallback;
    private BufferPool<ShmBuffer>? _pool;

    // What the compositor asked for, and what is drawn.
    private ToplevelConfigure _configure = new();
    private uint? _ackSerial;
    private int _logicalW, _logicalH, _fbW, _fbH;

    // A frame the window owes: a configure to answer, a new size or scale, a
    // frame that found no buffer free. OnRefresh is called for it from GetEvents.
    private bool _redraw;
    private GCHandle _self;
    private int _wakeFd = -1;
    private volatile bool _open;
    private bool _closing;
    private Exception? _listenerError;

    // Everything that happened, queued by listeners during dispatch and handed
    // over by WaitEvents to Events. Listeners do nothing else.
    private readonly List<WindowEvent> _queue = [];

    private readonly WaylandInput _input;
    private readonly WaylandClipboard _clipboard;
    private readonly WaylandScaleAndCursor _scale = new();
    private readonly IWaylandFeature[] _features;

    internal WaylandWindow(WaylandHostOptions? options = null)
    {
        _options = options ?? new WaylandHostOptions();
        if (_options.Buffers is < 2 or > 4) throw new ArgumentOutOfRangeException(nameof(options), "Buffers must be 2, 3 or 4");
        // Input reports its serials for the clipboard's set_selection, and
        // its pointer's life and enters to the cursor.
        _input = new WaylandInput(_queue.Add, serial => LastInputSerial = serial, CursorHooks);
        // The clipboard copies with LastInputSerial and dispatches while a paste waits.
        _clipboard = new WaylandClipboard(() => LastInputSerial, () => { if (_display != 0) Pump(0); },
                                          () => { if (_display != 0) WaylandNative.wl_display_flush(_display); });
        // The clipboard comes before input: it binds a wl_seat proxy of its own
        // and passes the global on, where input binds it and claims it.
        _features = [_clipboard, _input, _scale];
    }

    // ── Clipboard hook, for input ──────────────────────────────────────────────

    /// <summary>
    /// The serial of the latest input event the seat delivered to this window
    /// (wl_keyboard.enter / key, wl_pointer.button), or null before any. The
    /// input feature sets it; the clipboard needs it for
    /// wl_data_device.set_selection, which a compositor ignores without a
    /// recent serial. Until it is set, a copy stays in-process.
    /// </summary>
    internal uint? LastInputSerial { get; set; }

    /// <summary>True when this process can reach a Wayland compositor: libwayland-client loads and WAYLAND_DISPLAY is set.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && WaylandNative.IsAvailable;

    /// <summary>Framebuffer size in physical px.</summary>
    public int FramebufferWidth => _fbW;

    /// <inheritdoc cref="FramebufferWidth"/>
    public int FramebufferHeight => _fbH;

    /// <summary>Physical px per logical px: the scale the frame is drawn at.</summary>
    public float Scale => SurfaceScale.ToFactor(_scale120);

    /// <summary>The refresh rate of the output the window is on, in Hz; 60 when the compositor told none.</summary>
    public float RefreshRate => _scale.RefreshHz is > 0f and var hz ? hz : 60f;

    /// <summary>This frame's window, for <c>runtime.BeginBuild(window.Frame, window.Events)</c>.</summary>
    public WindowFrame Frame => new(Math.Max(1, _fbW), Math.Max(1, _fbH), Scale, RefreshRate);

    /// <summary>
    /// True: <see cref="GetEvents"/> without waiting waits for the compositor's
    /// frame callback, which paces the loop as vsync would, so the loop needs no
    /// sleep of its own.
    /// </summary>
    public bool PacesFrames => true;

    /// <summary>
    /// Called from <see cref="GetEvents"/> when the window must be drawn without
    /// input asking: a configure to answer, a new size or scale, a frame that was
    /// drawn and not shown. The app's frame, or a request for one
    /// (<c>runtime.RequestFrame</c>).
    /// </summary>
    public Action? OnRefresh { get; set; }

    /// <summary>True once the compositor or <see cref="Close"/> has asked the window to close.</summary>
    public bool IsClosing => _closing || !_open;

    /// <summary>The modifiers held now, asked of the keyboard rather than accumulated.</summary>
    public KeyModifiers HeldModifiers => _input.HeldModifiers;

    public string? GetClipboardText() => _clipboard.GetText();
    public void SetClipboardText(string text) => _clipboard.SetText(text);
    public void SetCursor(CursorShape shape) => _scale.SetCursor(shape);

    /// <summary>Cursor → input: where the pointer's owner reports the pointer, its enter serial and its leave.</summary>
    internal IPointerCursorHooks CursorHooks => _scale;

    // ── Open / Shutdown ──────────────────────────────────────────────────────

    internal void Open(PlatformWindowOptions options)
    {
        if (_open) throw new InvalidOperationException("The window is already open.");
        _closing = false;
        _listenerError = null;
        _configure = new ToplevelConfigure();
        _fbW = _fbH = 0;
        _scale120 = SurfaceScale.One;
        _queue.Clear();
        _self = GCHandle.Alloc(this);
        void* data = (void*)GCHandle.ToIntPtr(_self);

        _display = WaylandNative.wl_display_connect(null);
        if (_display == 0)
            throw new InvalidOperationException("wl_display_connect failed: no Wayland compositor (is WAYLAND_DISPLAY set?)");
        if (_wakeFd < 0)
        {
            // One per host for its whole life, never closed: Wake may run on
            // any thread at any time, and a closed fd number can be reused.
            _wakeFd = Libc.eventfd(0, Libc.EFD_CLOEXEC | Libc.EFD_NONBLOCK);
            if (_wakeFd < 0) throw new InvalidOperationException($"eventfd failed: errno {Libc.Errno}");
        }

        _registry = new WlDisplay(_display).GetRegistry();
        _registry.AddListener(RegistryListener, data);
        Roundtrip(); // globals → binds
        if (_compositor.IsNull || _shm.IsNull || _wmBase.IsNull)
            throw new InvalidOperationException("The compositor lacks wl_compositor, wl_shm or xdg_wm_base.");

        _pool = new BufferPool<ShmBuffer>(new ShmAllocator(_shm, BufferListener, data), _options.Buffers);

        _surface = _compositor.CreateSurface();
        _scale.Attach(_compositor, _shm, _surface); // scale listeners and the viewport
        _xdgSurface = _wmBase.GetXdgSurface(_surface);
        _xdgSurface.AddListener(XdgSurfaceListener, data);
        _toplevel = _xdgSurface.GetToplevel();
        _toplevel.AddListener(ToplevelListener, data);
        _toplevel.SetTitle(options.Title);
        _toplevel.SetAppId(_options.AppId);
        _toplevel.SetMinSize(_options.MinWidth, _options.MinHeight);
        if (!_decorationManager.IsNull)
        {
            _decoration = _decorationManager.GetToplevelDecoration(_toplevel);
            _decoration.SetMode(ZxdgToplevelDecorationV1.Mode.ServerSide);
        }

        // Asked for, before the first configure: a tiling compositor decides
        // otherwise, and a zero in the configure means "keep this".
        _logicalW = Math.Max(options.Width, _options.MinWidth);
        _logicalH = Math.Max(options.Height, _options.MinHeight);
        _open = true;

        // The initial commit, with no buffer, asks for the first configure;
        // nothing may be attached before it arrives.
        _surface.Commit();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!_configure.EverConfigured && !_closing)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException("The compositor sent no configure within 5 s.");
            Pump(100);
        }
        ApplyConfigure();
    }

    /// <summary>Destroys the window and disconnects from the compositor.</summary>
    public void Dispose()
    {
        _open = false;
        try
        {
            if (_display != 0)
            {
                _pool?.Clear();
                if (!_frameCallback.IsNull) _frameCallback.DestroyProxy();
                if (!_decoration.IsNull) _decoration.Destroy();
                if (!_toplevel.IsNull) _toplevel.Destroy();
                if (!_xdgSurface.IsNull) _xdgSurface.Destroy();
                if (!_surface.IsNull) _surface.Destroy();
                foreach (var f in _features) f.Destroy();
                if (!_decorationManager.IsNull) _decorationManager.Destroy();
                if (!_wmBase.IsNull) _wmBase.Destroy();
                if (!_shm.IsNull) _shm.DestroyProxy();
                if (!_compositor.IsNull) _compositor.DestroyProxy();
                if (!_registry.IsNull) _registry.DestroyProxy();
                WaylandNative.wl_display_flush(_display);
                WaylandNative.wl_display_disconnect(_display);
            }
        }
        finally
        {
            _display = 0;
            _pool = null;
            _frameCallback = default; _decoration = default; _toplevel = default; _xdgSurface = default; _surface = default;
            _decorationManager = default; _wmBase = default; _shm = default; _compositor = default; _registry = default;
            _ackSerial = null;
            if (_self.IsAllocated) _self.Free();
        }
    }

    // ── The loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes what the compositor has queued into <see cref="Events"/> and returns
    /// them. With <paramref name="wait"/>, for when nothing else is happening, it
    /// first waits until something does: input, a configure, a
    /// <see cref="Wake"/> (from any thread), or the window closing. Without, a
    /// frame is wanted: it waits until the compositor wants one too (the last
    /// frame's callback), and takes a configure that came meanwhile, so the frame
    /// is built at the size it will be shown at. A resize is not an event: it is
    /// in <see cref="Frame"/>, and <see cref="OnRefresh"/> is called for it.
    /// </summary>
    public List<WindowEvent> GetEvents(bool wait)
    {
        if (_display == 0) return Events;
        bool haveWork = !wait || _queue.Count > 0 || _configure.HasPending || _closing || ScaleChanged || _redraw;
        Pump(haveWork ? 0 : -1);
        ApplyConfigure();
        if (ScaleChanged && _configure.EverConfigured) ApplySize(); // moved to an output of another scale
        if (!wait) WaitForCompositor();
        Events.AddRange(_queue);
        _queue.Clear();
        if (_redraw)
        {
            _redraw = false;
            OnRefresh?.Invoke();
        }
        return Events;
    }

    /// <summary>Makes a waiting <see cref="GetEvents"/> return. Safe from any thread.</summary>
    public void Wake()
    {
        int fd = _wakeFd;
        if (!_open || fd < 0) return;
        ulong one = 1;
        Libc.write(fd, &one, sizeof(ulong)); // EAGAIN when the counter is full: already awake
    }

    /// <summary>Asks the window to close: <see cref="IsClosing"/> turns true.</summary>
    public void Close()
    {
        _closing = true;
        Wake();
    }

    // ── A frame's buffer ─────────────────────────────────────────────────────

    /// <summary>
    /// Waits until the compositor wants a frame, and takes a configure that came
    /// meanwhile, so the frame is built at the size it will be shown at. Nothing
    /// to wait for when there is nothing to draw into (closing, not configured yet).
    /// </summary>
    private void WaitForCompositor()
    {
        if (_display == 0 || _closing || _pool is null || !_configure.EverConfigured) return;

        // Pace: draw when the compositor wants a frame. A configure waiting to
        // be acked does not wait — the new size should appear at once.
        if (!_frameCallback.IsNull && _ackSerial is null)
        {
            WaitFor(() => _frameCallback.IsNull || _configure.HasPending || _closing);
            ApplyConfigure();
            if (!_frameCallback.IsNull)
            {
                // Not shown (another workspace), or the compositor is slow:
                // stop waiting for this callback and draw anyway.
                _frameCallback.DestroyProxy();
                _frameCallback = default;
            }
        }
    }

    /// <summary>
    /// Takes a free buffer and points <see cref="Cpu"/> at it. When every buffer
    /// is still the compositor's (or the window is closing), <see cref="Cpu"/> is
    /// memory of the window's own that nothing shows, the commit is skipped, and
    /// the window owes a frame again (<see cref="OnRefresh"/>): the frame is drawn,
    /// never dropped half-way.
    /// </summary>
    internal void AcquireBuffer()
    {
        var buffer = _pool is null || _closing ? null : _pool.TryAcquire(_fbW, _fbH);
        if (buffer is null && _pool is not null && !_closing)
        {
            WaitFor(() => _pool.BusyCount < _pool.Count || _closing);
            buffer = _pool.TryAcquire(_fbW, _fbH, allowExtra: true);
        }
        if (buffer is null || _closing)
        {
            _buffer = null;
            Cpu.Pixels = Unshown(_fbW, _fbH);
            _redraw = true;
            return;
        }
        _buffer = buffer;
        Cpu.Pixels = buffer.Pixels;
    }

    // Memory the size of a frame that is drawn into and not shown: what a renderer
    // gets when no buffer is free. Made the first time it is needed.
    private byte[] _unshown = [];
    private Bibui.Core.Draw.RasterPixels Unshown(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (_unshown.Length != w * h * 4) _unshown = GC.AllocateArray<byte>(w * h * 4, pinned: true);
        return new Bibui.Core.Draw.RasterPixels(Marshal.UnsafeAddrOfPinnedArrayElement(_unshown, 0), w, h, w * 4);
    }


    /// <summary>Shows the buffer <see cref="AcquireBuffer"/> took: acks a configure, attaches, damages and commits. Nothing when it took none.</summary>
    internal void Commit()
    {
        if (_buffer is not { } buffer || _display == 0 || _pool is null) return;
        _buffer = null;
        if (_ackSerial is uint serial)
        {
            _xdgSurface.AckConfigure(serial);
            _ackSerial = null;
        }
        _surface.Attach(buffer.Wl, 0, 0);
        // The whole buffer is damaged on every commit. The compositor's copy
        // of it costs less than finding which pixels changed would.
        if (_surface.Version >= WlSurface.DamageBufferSince) _surface.DamageBuffer(0, 0, buffer.Width, buffer.Height);
        else _surface.Damage(0, 0, _logicalW, _logicalH);
        _frameCallback = _surface.Frame();
        _frameCallback.AddListener(FrameListener, (void*)GCHandle.ToIntPtr(_self));
        _scale.BeforeCommit(_logicalW, _logicalH, _scale120); // viewport destination / buffer scale
        _surface.Commit();
        _pool.Submitted(buffer);
        WaylandNative.wl_display_flush(_display);
    }

    // ── Configure ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a completed configure: the new size is taken, the window owes a
    /// frame (a configure of the same size, a focus change say, is still answered
    /// with a commit), and the serial waits to be acked by the commit that shows
    /// it. Runs on the loop, never inside dispatch.
    /// </summary>
    private void ApplyConfigure()
    {
        if (!_configure.HasPending) return;
        var c = _configure.Take(_logicalW, _logicalH, _options.MinWidth, _options.MinHeight);
        _ackSerial = c.Serial;
        _logicalW = c.Width;
        _logicalH = c.Height;
        ApplySize();
        _redraw = true;
    }

    // ── Buffer size and scale ─────────────────────────────────────────────────
    // The one place the framebuffer is sized. Logical size from the configure,
    // scale from WaylandScaleAndCursor (fractional, else integer, else 1); the
    // buffer is round(logical × scale) (FramebufferSize), drawn at that size and
    // scale (Frame), and presented at the logical size through the viewport
    // (BeforeCommit in Commit). A change of either owes a frame, and the pool
    // retires the old size's buffers as the compositor releases them.

    private uint _scale120 = SurfaceScale.One;

    private bool ScaleChanged => _scale.Wanted120 != _scale120;

    /// <summary>The buffer for a logical size at a scale in 120ths.</summary>
    internal static (int W, int H) FramebufferSize(int logicalW, int logicalH, uint scale120) =>
        (SurfaceScale.Physical(logicalW, scale120), SurfaceScale.Physical(logicalH, scale120));

    /// <summary>Takes a new buffer size or scale, and owes a frame for it.</summary>
    private void ApplySize()
    {
        uint scale120 = _scale.Wanted120;
        var (fbW, fbH) = FramebufferSize(_logicalW, _logicalH, scale120);
        if (fbW == _fbW && fbH == _fbH && scale120 == _scale120) return;
        bool scaleChanged = scale120 != _scale120;
        _fbW = fbW;
        _fbH = fbH;
        _scale120 = scale120;
        if (scaleChanged) _scale.OnScaleApplied();
        _redraw = true;
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One round of libwayland's prepare_read / poll / read_events / dispatch,
    /// waiting at most <paramref name="timeoutMs"/> (-1: until something
    /// arrives) on the display and the wake fd. Waits not at all when events
    /// were already queued.
    /// </summary>
    private void Pump(int timeoutMs)
    {
        int dispatched = 0;
        while (WaylandNative.wl_display_prepare_read(_display) != 0)
            dispatched += Check(WaylandNative.wl_display_dispatch_pending(_display));
        if (dispatched > 0 || _closing) timeoutMs = 0;

        short displayEvents = Libc.POLLIN;
        if (WaylandNative.wl_display_flush(_display) < 0 && Libc.Errno == Libc.EAGAIN)
            displayEvents |= Libc.POLLOUT; // the socket is full: wake when it drains

        var fds = stackalloc Libc.PollFd[3];
        fds[0] = new Libc.PollFd { Fd = WaylandNative.wl_display_get_fd(_display), Events = displayEvents };
        fds[1] = new Libc.PollFd { Fd = _wakeFd, Events = Libc.POLLIN };
        // The key-repeat timer, so a repeat runs on this thread (-1, ignored by poll, while there is none).
        fds[2] = new Libc.PollFd { Fd = _input.RepeatFd, Events = Libc.POLLIN };
        int n = Libc.poll(fds, 3, timeoutMs);
        if (n > 0 && (fds[0].Revents & Libc.POLLIN) != 0) Check(WaylandNative.wl_display_read_events(_display));
        else WaylandNative.wl_display_cancel_read(_display);
        if (n > 0 && (fds[1].Revents & Libc.POLLIN) != 0)
        {
            ulong drained;
            Libc.read(_wakeFd, &drained, sizeof(ulong));
        }
        Check(WaylandNative.wl_display_dispatch_pending(_display));
        if (n > 0 && (fds[2].Revents & Libc.POLLIN) != 0) _input.OnRepeatTimer();

        if (_listenerError is { } e)
        {
            _listenerError = null;
            throw new InvalidOperationException("A Wayland event handler failed.", e);
        }
    }

    /// <summary>Dispatches until <paramref name="done"/> or <see cref="WaylandHostOptions.FrameWait"/> has passed.</summary>
    private void WaitFor(Func<bool> done)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(_options.FrameWait.TotalSeconds * Stopwatch.Frequency);
        while (!done())
        {
            long left = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (left <= 0) return;
            Pump((int)Math.Min(left, int.MaxValue));
        }
    }

    private void Roundtrip() => Check(WaylandNative.wl_display_roundtrip(_display));

    private int Check(int rc)
    {
        if (rc >= 0) return rc;
        int err = _display == 0 ? 0 : WaylandNative.wl_display_get_error(_display);
        _closing = true;
        throw new InvalidOperationException($"The Wayland connection failed: {Marshal.GetPInvokeErrorMessage(err)} (errno {err}).");
    }

    // ── Listeners ────────────────────────────────────────────────────────────
    // Static [UnmanagedCallersOnly] functions in tables built once per process.
    // The data pointer is a GCHandle to the host. They record what happened,
    // send what the protocol requires at once (pong), and nothing else.

    private static WaylandWindow Host(void* data) => (WaylandWindow)GCHandle.FromIntPtr((nint)data).Target!;

    private static readonly WlRegistry.Listener* RegistryListener =
        WlRegistry.Listener.Create(new() { Global = &OnGlobal, GlobalRemove = &OnGlobalRemove });
    private static readonly XdgWmBase.Listener* WmBaseListener =
        XdgWmBase.Listener.Create(new() { Ping = &OnPing });
    private static readonly XdgSurface.Listener* XdgSurfaceListener =
        XdgSurface.Listener.Create(new() { Configure = &OnSurfaceConfigure });
    private static readonly XdgToplevel.Listener* ToplevelListener =
        XdgToplevel.Listener.Create(new() { Configure = &OnToplevelConfigure, Close = &OnClose, ConfigureBounds = &OnConfigureBounds });
    private static readonly WlCallback.Listener* FrameListener =
        WlCallback.Listener.Create(new() { Done = &OnFrameDone });
    private static readonly WlBuffer.Listener* BufferListener =
        WlBuffer.Listener.Create(new() { Release = &OnBufferRelease });

    [UnmanagedCallersOnly]
    private static void OnGlobal(void* data, WlRegistry registry, uint name, byte* ifacePtr, uint version)
    {
        var h = Host(data);
        try
        {
            var iface = WlUtf8.Read(ifacePtr)!;
            switch (iface)
            {
                case WlCompositor.InterfaceName:
                    h._compositor = registry.Bind<WlCompositor>(name, Math.Min(version, 6u));
                    return;
                case WlShm.InterfaceName:
                    h._shm = registry.Bind<WlShm>(name, 1);
                    return;
                case XdgWmBase.InterfaceName:
                    h._wmBase = registry.Bind<XdgWmBase>(name, Math.Min(version, XdgWmBase.InterfaceVersion));
                    h._wmBase.AddListener(WmBaseListener, data);
                    return;
                case ZxdgDecorationManagerV1.InterfaceName:
                    h._decorationManager = registry.Bind<ZxdgDecorationManagerV1>(name, 1);
                    return;
            }
            foreach (var f in h._features)
                if (f.TryBind(registry, name, iface, version)) return;
        }
        catch (Exception e) { h._listenerError ??= e; }
    }

    [UnmanagedCallersOnly]
    private static void OnGlobalRemove(void* data, WlRegistry registry, uint name)
    {
        var h = Host(data);
        try { foreach (var f in h._features) f.OnGlobalRemove(name); }
        catch (Exception e) { h._listenerError ??= e; }
    }

    [UnmanagedCallersOnly]
    private static void OnPing(void* data, XdgWmBase wmBase, uint serial) => wmBase.Pong(serial);

    [UnmanagedCallersOnly]
    private static void OnSurfaceConfigure(void* data, XdgSurface surface, uint serial) =>
        Host(data)._configure.OnSurfaceConfigure(serial);

    [UnmanagedCallersOnly]
    private static void OnToplevelConfigure(void* data, XdgToplevel toplevel, int width, int height, WlArray* states) =>
        Host(data)._configure.OnToplevelConfigure(width, height, states->As<uint>());

    [UnmanagedCallersOnly]
    private static void OnConfigureBounds(void* data, XdgToplevel toplevel, int width, int height) =>
        Host(data)._configure.OnConfigureBounds(width, height);

    [UnmanagedCallersOnly]
    private static void OnClose(void* data, XdgToplevel toplevel) => Host(data)._closing = true;

    [UnmanagedCallersOnly]
    private static void OnFrameDone(void* data, WlCallback callback, uint time)
    {
        var h = Host(data);
        if (h._frameCallback == callback) h._frameCallback = default;
        callback.DestroyProxy(); // done is a destructor event
    }

    [UnmanagedCallersOnly]
    private static void OnBufferRelease(void* data, WlBuffer buffer)
    {
        var h = Host(data);
        if (h._pool?.Find(b => b.Wl == buffer) is { } b) h._pool.Released(b);
    }

    // ── The module: open, then bracket every frame's drawing ────────────────

    /// <summary>
    /// Opens a window: connects, makes the toplevel and waits for its first
    /// configure, so <see cref="Frame"/> has its size.
    /// </summary>
    public static WaylandWindow Open(PlatformWindowOptions options, WaylandHostOptions? wayland = null)
    {
        var window = new WaylandWindow(wayland);
        try { window.Open(options); }
        catch { window.Dispose(); throw; }
        return window;
    }

    /// <summary>
    /// Points <see cref="Cpu"/> at a free <c>wl_shm</c> buffer, to draw this
    /// frame into: after the build, before the renderer draws.
    /// </summary>
    public static void BeginDraw(WaylandWindow window) => window.AcquireBuffer();

    /// <summary>Shows what was drawn into <see cref="Cpu"/>: attaches, damages and commits the buffer.</summary>
    public static void EndDraw(WaylandWindow window) => window.Commit();
}
