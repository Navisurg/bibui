using System.Runtime.InteropServices;
using Bibui.Core;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Platform.Wayland;

// HiDPI and cursors.
//
// Coordinates. Everything the window reports is logical — surface-local, the
// same units xdg_toplevel.configure sizes the window in, and what the Silk host
// reports. wl_pointer's surface_x/surface_y already are that: with a viewport
// the surface is the logical size whatever the buffer is, so the input feature passes them
// through unscaled. Only the buffer is physical: round(logical × scale).

/// <summary>
/// Scale arithmetic in wp_fractional_scale_v1's unit, 120ths: 150 is 1.25.
/// An integer scale from the fallbacks is n × 120.
/// </summary>
internal static class SurfaceScale
{
    public const uint One = 120;

    public static float ToFactor(uint scale120) => scale120 / (float)One;

    /// <summary>
    /// Physical pixels for a logical extent: round half away from zero, as the
    /// protocol asks. The runtime sizes its frame by the same function
    /// (<see cref="WindowFrame.PhysicalSize"/>), so the buffer the host draws
    /// into and the frame the runtime lays out are always the same size.
    /// </summary>
    public static int Physical(int logical, uint scale120) => WindowFrame.PhysicalSize(logical, ToFactor(scale120));
}

/// <summary>
/// Which scale the surface wants, from whatever the compositor offers, best first:
/// <list type="number">
/// <item>wp_fractional_scale_v1.preferred_scale (needs wp_viewporter to present it);</item>
/// <item>wl_surface.preferred_buffer_scale (wl_surface v6), an integer;</item>
/// <item>the largest wl_output.scale among the outputs the surface is on;</item>
/// <item>1.</item>
/// </list>
/// And the refresh rate of those outputs (<see cref="RefreshHz"/>), which the
/// compositor's frame callbacks pace the window at.
/// Pure state: the listeners feed it, the host reads <see cref="Wanted120"/>
/// and <see cref="RefreshHz"/>.
/// </summary>
internal sealed class ScaleState
{
    private uint? _fractional;
    private int? _preferredBuffer;
    private readonly Dictionary<nint, int> _outputScale = [];
    private readonly Dictionary<nint, int> _outputRefreshMilliHz = [];
    private readonly HashSet<nint> _entered = [];

    public void OnFractional(uint scale120) => _fractional = scale120 > 0 ? scale120 : null;
    public void OnPreferredBufferScale(int factor) => _preferredBuffer = factor > 0 ? factor : null;
    public void OnOutputScale(nint output, int factor) => _outputScale[output] = Math.Max(1, factor);

    /// <summary>wl_output.mode: only the current mode says what the output runs at.</summary>
    public void OnOutputMode(nint output, bool current, int refreshMilliHz)
    {
        if (current && refreshMilliHz > 0) _outputRefreshMilliHz[output] = refreshMilliHz;
    }

    public void OnEnter(nint output) => _entered.Add(output);
    public void OnLeave(nint output) => _entered.Remove(output);

    public void OnOutputGone(nint output)
    {
        _outputScale.Remove(output);
        _outputRefreshMilliHz.Remove(output);
        _entered.Remove(output);
    }

    /// <summary>True while the scale is fractional-capable (a fractional scale arrived).</summary>
    public bool IsFractional => _fractional is not null;

    public uint Wanted120
    {
        get
        {
            if (_fractional is uint f) return f;
            if (_preferredBuffer is int p) return (uint)p * SurfaceScale.One;
            int max = 0;
            foreach (var o in _entered)
                if (_outputScale.TryGetValue(o, out var s)) max = Math.Max(max, s);
            return max > 0 ? (uint)max * SurfaceScale.One : SurfaceScale.One;
        }
    }

    /// <summary>
    /// The fastest refresh rate among the outputs the surface is on, in Hz; 0
    /// while none is known (not shown yet, or the compositor sent no mode).
    /// </summary>
    public float RefreshHz
    {
        get
        {
            int max = 0;
            foreach (var o in _entered)
                if (_outputRefreshMilliHz.TryGetValue(o, out var r)) max = Math.Max(max, r);
            return max / 1000f;
        }
    }
}

/// <summary>bibui's <see cref="CursorShape"/> in cursor-shape-v1 and in an XCursor theme.</summary>
internal static class CursorShapes
{
    public static WpCursorShapeDeviceV1.Shape ToProtocol(CursorShape shape) => shape switch
    {
        CursorShape.Pointer          => WpCursorShapeDeviceV1.Shape.Pointer,
        CursorShape.Text             => WpCursorShapeDeviceV1.Shape.Text,
        CursorShape.Crosshair        => WpCursorShapeDeviceV1.Shape.Crosshair,
        CursorShape.ResizeHorizontal => WpCursorShapeDeviceV1.Shape.EwResize,
        CursorShape.ResizeVertical   => WpCursorShapeDeviceV1.Shape.NsResize,
        CursorShape.ResizeAll        => WpCursorShapeDeviceV1.Shape.Move,
        CursorShape.NotAllowed       => WpCursorShapeDeviceV1.Shape.NotAllowed,
        CursorShape.Wait             => WpCursorShapeDeviceV1.Shape.Wait,
        _                            => WpCursorShapeDeviceV1.Shape.Default,
    };

    /// <summary>Theme names to try, CSS name first (what current themes ship), then the X11 legacy name.</summary>
    public static (string Css, string Legacy) ThemeNames(CursorShape shape) => shape switch
    {
        CursorShape.Pointer          => ("pointer", "hand2"),
        CursorShape.Text             => ("text", "xterm"),
        CursorShape.Crosshair        => ("crosshair", "cross"),
        CursorShape.ResizeHorizontal => ("ew-resize", "sb_h_double_arrow"),
        CursorShape.ResizeVertical   => ("ns-resize", "sb_v_double_arrow"),
        CursorShape.ResizeAll        => ("move", "fleur"),
        CursorShape.NotAllowed       => ("not-allowed", "crossed_circle"),
        CursorShape.Wait             => ("wait", "watch"),
        _                            => ("default", "left_ptr"),
    };
}

/// <summary>Where a cursor is shown: cursor-shape-v1, a theme, or nowhere.</summary>
internal interface ICursorSink
{
    /// <summary>Shows <paramref name="shape"/>, under the serial of the pointer's latest enter.</summary>
    void Show(uint serial, CursorShape shape);
}

/// <summary>
/// When to show a cursor. Wayland lets a client set the pointer's image only
/// with the serial of the enter that brought the pointer in, and the image
/// is undefined after an enter until the client sets one — so every enter
/// shows the wanted shape, and a change while inside shows it again with the
/// same serial. Outside the window nothing is sent.
/// </summary>
internal sealed class CursorState(ICursorSink sink)
{
    private uint? _serial;
    private CursorShape? _shown;

    public ICursorSink Sink { get; set; } = sink;

    public CursorShape Wanted { get; private set; }

    public bool Inside => _serial is not null;

    public void Enter(uint serial)
    {
        _serial = serial;
        Sink.Show(serial, Wanted);
        _shown = Wanted;
    }

    public void Leave()
    {
        _serial = null;
        _shown = null;
    }

    public void Set(CursorShape shape)
    {
        Wanted = shape;
        if (_serial is uint s && _shown != shape)
        {
            Sink.Show(s, shape);
            _shown = shape;
        }
    }

    /// <summary>Shows the wanted shape again — the sink changed, or the scale the theme was loaded at.</summary>
    public void Reshow()
    {
        if (_serial is uint s)
        {
            Sink.Show(s, Wanted);
            _shown = Wanted;
        }
    }
}

/// <summary>No way to set a cursor (no pointer yet): the compositor's stays.</summary>
internal sealed class NoCursor : ICursorSink
{
    public static readonly NoCursor Instance = new();
    public void Show(uint serial, CursorShape shape) { }
}

/// <summary>cursor-shape-v1: the compositor draws the shape from its own theme, at its own scale.</summary>
internal sealed class ShapeDeviceCursor(WpCursorShapeDeviceV1 device) : ICursorSink
{
    public WpCursorShapeDeviceV1 Device { get; } = device;
    public void Show(uint serial, CursorShape shape) => Device.SetShape(serial, CursorShapes.ToProtocol(shape));
}

/// <summary>
/// The fallback when the compositor lacks cursor-shape-v1: the XCursor theme
/// through libwayland-cursor, drawn on a cursor surface of our own. Loaded at
/// XCURSOR_SIZE × the integer scale above the surface's, and shown with that
/// buffer scale so it is sharp. First frame only — no animation.
/// </summary>
internal sealed unsafe class ThemeCursor(WlCompositor compositor, WlShm shm, WlPointer pointer, Func<uint> scale120) : ICursorSink
{
    private nint _theme;
    private int _themeScale;
    private WlSurface _surface;

    public static bool IsAvailable => NativeLibrary.TryLoad(WaylandCursorNative.Library, out _);

    public void Show(uint serial, CursorShape shape)
    {
        int scale = (int)Math.Ceiling(scale120() / (double)SurfaceScale.One);
        if (_theme == 0 || scale != _themeScale) Load(scale);
        if (_theme == 0) return;

        var (css, legacy) = CursorShapes.ThemeNames(shape);
        WaylandCursorNative.Cursor* cursor = null;
        foreach (var name in (ReadOnlySpan<string>)[css, legacy, "default", "left_ptr"])
            if ((cursor = Find(name)) != null) break;
        if (cursor == null || cursor->ImageCount == 0) return;
        var image = cursor->Images[0];
        nint buffer = WaylandCursorNative.wl_cursor_image_get_buffer(image);
        if (buffer == 0) return;

        // A theme without this size hands back the nearest; drawn at a scale it
        // does not divide, it would be a protocol error, so show it unscaled.
        int bufferScale = image->Width % _themeScale == 0 && image->Height % _themeScale == 0 ? _themeScale : 1;
        if (_surface.IsNull) _surface = compositor.CreateSurface();
        if (_surface.Version >= WlSurface.SetBufferScaleSince) _surface.SetBufferScale(bufferScale);
        else bufferScale = 1;
        _surface.Attach(WlBuffer.FromHandle(buffer), 0, 0);
        _surface.Damage(0, 0, int.MaxValue, int.MaxValue);
        _surface.Commit();
        pointer.SetCursor(serial, _surface, (int)image->HotspotX / bufferScale, (int)image->HotspotY / bufferScale);
    }

    private WaylandCursorNative.Cursor* Find(string name)
    {
        var p = WlUtf8.Alloc(name);
        try { return WaylandCursorNative.wl_cursor_theme_get_cursor(_theme, p); }
        finally { WlUtf8.Free(p); }
    }

    private void Load(int scale)
    {
        if (_theme != 0) WaylandCursorNative.wl_cursor_theme_destroy(_theme);
        int size = int.TryParse(Environment.GetEnvironmentVariable("XCURSOR_SIZE"), out var s) && s > 0 ? s : 24;
        var name = WlUtf8.Alloc(Environment.GetEnvironmentVariable("XCURSOR_THEME"));
        try { _theme = WaylandCursorNative.wl_cursor_theme_load(name, size * scale, shm.Handle); }
        finally { WlUtf8.Free(name); }
        _themeScale = scale;
    }

    public void Destroy()
    {
        if (!_surface.IsNull) _surface.Destroy();
        _surface = default;
        if (_theme != 0) WaylandCursorNative.wl_cursor_theme_destroy(_theme);
        _theme = 0;
    }
}

/// <summary>libwayland-cursor: XCursor themes as wl_shm buffers. Only the fallback uses it.</summary>
internal static unsafe class WaylandCursorNative
{
    public const string Library = "libwayland-cursor.so.0";

    [StructLayout(LayoutKind.Sequential)]
    public struct Image
    {
        public uint Width, Height, HotspotX, HotspotY, Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Cursor
    {
        public uint ImageCount;
        public Image** Images;
        public byte* Name;
    }

    [DllImport(Library, ExactSpelling = true)] public static extern nint wl_cursor_theme_load(byte* name, int size, nint shm);
    [DllImport(Library, ExactSpelling = true)] public static extern void wl_cursor_theme_destroy(nint theme);
    [DllImport(Library, ExactSpelling = true)] public static extern Cursor* wl_cursor_theme_get_cursor(nint theme, byte* name);
    [DllImport(Library, ExactSpelling = true)] public static extern nint wl_cursor_image_get_buffer(Image* image);
}

/// <summary>
/// What the pointer's owner (<see cref="WaylandInput"/>) tells the cursor:
/// which wl_pointer there is, and when it enters and leaves the window.
/// <see cref="WaylandWindow"/> hands its <see cref="WaylandScaleAndCursor"/>
/// over as this. All on the loop thread; calling from inside a listener is fine.
/// </summary>
internal interface IPointerCursorHooks
{
    /// <summary>The seat has a pointer (after wl_seat.get_pointer), before its first enter.</summary>
    void AttachPointer(WlPointer pointer);

    /// <summary>The pointer is going; call before wl_pointer.release.</summary>
    void DetachPointer();

    /// <summary>wl_pointer.enter on the window's surface, with the event's serial.</summary>
    void OnPointerEnter(uint serial);

    /// <summary>wl_pointer.leave from the window's surface.</summary>
    void OnPointerLeave();
}

/// <summary>
/// HiDPI and cursors.
///
/// <para><b>Scale.</b> Binds wp_fractional_scale_manager_v1 and wp_viewporter,
/// and wl_output for the integer fallback; listens on the window's surface
/// (<see cref="Attach"/>) for preferred_scale, preferred_buffer_scale and
/// enter/leave. The host reads <see cref="Wanted120"/>, sizes its buffers
/// round(logical × scale), and calls <see cref="BeforeCommit"/> so the commit
/// that shows them also says how: wp_viewport.set_destination(logical) with
/// buffer scale 1, or — without a viewporter — wl_surface.set_buffer_scale(n).
/// A fractional scale is only taken when it can be presented, i.e. with a
/// viewporter.</para>
///
/// <para><b>Cursor.</b> Binds wp_cursor_shape_manager_v1. The pointer is
/// the input feature's: it hands it over with <see cref="AttachPointer"/> and reports
/// <see cref="OnPointerEnter"/> / <see cref="OnPointerLeave"/>; the host's
/// <see cref="SetCursor"/> sets the wanted shape. Without cursor-shape the
/// XCursor theme is drawn instead (<see cref="ThemeCursor"/>); without
/// libwayland-cursor either, nothing is set and the compositor's cursor stays.</para>
/// </summary>
internal sealed unsafe class WaylandScaleAndCursor : IWaylandFeature, IPointerCursorHooks
{
    private WpFractionalScaleManagerV1 _fractionalManager;
    private WpViewporter _viewporter;
    private WpCursorShapeManagerV1 _cursorShapeManager;
    private readonly Dictionary<uint, WlOutput> _outputs = [];

    private WlCompositor _compositor;
    private WlShm _shm;
    private WlSurface _surface;
    private WpFractionalScaleV1 _fractional;
    private WpViewport _viewport;
    private (int W, int H) _destination;
    private int _bufferScale = 1;

    private WlPointer _pointer;
    private ShapeDeviceCursor? _shapeDevice;
    private ThemeCursor? _themeCursor;
    private readonly CursorState _cursor = new(NoCursor.Instance);

    private GCHandle _self;

    public ScaleState State { get; private set; } = new();

    /// <summary>
    /// The scale the surface wants now, in 120ths. Fractional only when it can
    /// be presented: the fractional-scale object is created only beside a viewport.
    /// </summary>
    public uint Wanted120 => State.Wanted120;

    /// <summary>The refresh rate of the outputs the surface is on, in Hz; 0 while unknown.</summary>
    public float RefreshHz => State.RefreshHz;

    /// <summary>The cursor shape asked for last.</summary>
    public CursorShape Cursor => _cursor.Wanted;

    private void* Data
    {
        get
        {
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            return (void*)GCHandle.ToIntPtr(_self);
        }
    }

    public bool TryBind(WlRegistry registry, uint name, string iface, uint version)
    {
        switch (iface)
        {
            case WpFractionalScaleManagerV1.InterfaceName:
                _fractionalManager = registry.Bind<WpFractionalScaleManagerV1>(name, 1);
                return true;
            case WpViewporter.InterfaceName:
                _viewporter = registry.Bind<WpViewporter>(name, 1);
                return true;
            case WpCursorShapeManagerV1.InterfaceName:
                _cursorShapeManager = registry.Bind<WpCursorShapeManagerV1>(name, 1);
                return true;
            case WlOutput.InterfaceName when version >= WlOutput.Listener.ScaleSince:
                var output = registry.Bind<WlOutput>(name, Math.Min(version, 4u));
                output.AddListener(OutputListener, Data);
                _outputs[name] = output;
                return true;
        }
        return false;
    }

    public void OnGlobalRemove(uint name)
    {
        if (!_outputs.Remove(name, out var output)) return;
        State.OnOutputGone(output.Handle);
        if (output.Version >= WlOutput.ReleaseSince) output.Release();
        else output.DestroyProxy();
    }

    /// <summary>
    /// The window's surface exists: listen for its scale, and give it a
    /// viewport. Called once by the host, before the first commit.
    /// </summary>
    public void Attach(WlCompositor compositor, WlShm shm, WlSurface surface)
    {
        _compositor = compositor;
        _shm = shm;
        _surface = surface;
        surface.AddListener(SurfaceListener, Data);
        if (!_viewporter.IsNull)
        {
            _viewport = _viewporter.GetViewport(surface);
            if (!_fractionalManager.IsNull)
            {
                _fractional = _fractionalManager.GetFractionalScale(surface);
                _fractional.AddListener(FractionalListener, Data);
            }
        }
        _destination = default;
        _bufferScale = 1;
    }

    /// <summary>
    /// Called by the host just before the commit of a buffer drawn for
    /// <paramref name="logicalW"/> × <paramref name="logicalH"/> at
    /// <paramref name="scale120"/>: says how the buffer maps onto the surface.
    /// Both are double-buffered, so they land with that buffer.
    /// </summary>
    public void BeforeCommit(int logicalW, int logicalH, uint scale120)
    {
        if (!_viewport.IsNull)
        {
            if (_destination != (logicalW, logicalH))
            {
                _viewport.SetDestination(logicalW, logicalH);
                _destination = (logicalW, logicalH);
            }
            return;
        }
        int n = (int)(scale120 / SurfaceScale.One);
        if (n != _bufferScale && _surface.Version >= WlSurface.SetBufferScaleSince)
        {
            _surface.SetBufferScale(n);
            _bufferScale = n;
        }
    }

    /// <summary>The window's scale was applied: a theme-drawn cursor is redrawn for it.</summary>
    public void OnScaleApplied()
    {
        if (_themeCursor is not null) _cursor.Reshow();
    }

    // ── Cursor: the hooks the input feature calls ──────────────────────────────────

    /// <summary>
    /// The seat has a pointer (wl_seat.get_pointer). Call once per
    /// pointer, before its first enter. <see cref="DetachPointer"/> before
    /// releasing it.
    /// </summary>
    public void AttachPointer(WlPointer pointer)
    {
        DetachPointer();
        _pointer = pointer;
        if (!_cursorShapeManager.IsNull)
        {
            _shapeDevice = new ShapeDeviceCursor(_cursorShapeManager.GetPointer(pointer));
            _cursor.Sink = _shapeDevice;
        }
        else if (ThemeCursor.IsAvailable && !_compositor.IsNull && !_shm.IsNull)
        {
            _themeCursor = new ThemeCursor(_compositor, _shm, pointer, () => Wanted120);
            _cursor.Sink = _themeCursor;
        }
    }

    /// <summary>The pointer is going (seat capability lost, or shutdown). Call before wl_pointer.release.</summary>
    public void DetachPointer()
    {
        _cursor.Leave();
        _cursor.Sink = NoCursor.Instance;
        _shapeDevice?.Device.Destroy();
        _shapeDevice = null;
        _themeCursor?.Destroy();
        _themeCursor = null;
        _pointer = default;
    }

    /// <summary>
    /// wl_pointer.enter on the window's surface, with its serial. Sets
    /// the wanted cursor, which Wayland requires after every enter. Call from
    /// the enter listener (inside dispatch is fine: it only sends a request).
    /// </summary>
    public void OnPointerEnter(uint serial) => _cursor.Enter(serial);

    /// <summary>wl_pointer.leave from the window's surface.</summary>
    public void OnPointerLeave() => _cursor.Leave();

    /// <summary>The window's <see cref="WaylandWindow.SetCursor"/>: shown now if the pointer is inside, else on the next enter.</summary>
    public void SetCursor(CursorShape shape) => _cursor.Set(shape);

    public void Destroy()
    {
        DetachPointer();
        if (!_fractional.IsNull) _fractional.Destroy();
        if (!_viewport.IsNull) _viewport.Destroy();
        _fractional = default;
        _viewport = default;
        _surface = default;
        _compositor = default;
        _shm = default;
        foreach (var o in _outputs.Values)
        {
            if (o.Version >= WlOutput.ReleaseSince) o.Release();
            else o.DestroyProxy();
        }
        _outputs.Clear();
        if (!_cursorShapeManager.IsNull) _cursorShapeManager.Destroy();
        if (!_viewporter.IsNull) _viewporter.Destroy();
        if (!_fractionalManager.IsNull) _fractionalManager.Destroy();
        _cursorShapeManager = default;
        _viewporter = default;
        _fractionalManager = default;
        State = new ScaleState(); // the next connection's outputs are new objects
        if (_self.IsAllocated) _self.Free();
    }

    // ── Listeners ────────────────────────────────────────────────────────────

    private static WaylandScaleAndCursor Self(void* data) => (WaylandScaleAndCursor)GCHandle.FromIntPtr((nint)data).Target!;

    private static readonly WpFractionalScaleV1.Listener* FractionalListener =
        WpFractionalScaleV1.Listener.Create(new() { PreferredScale = &OnPreferredScale });
    private static readonly WlSurface.Listener* SurfaceListener =
        WlSurface.Listener.Create(new() { Enter = &OnSurfaceEnter, Leave = &OnSurfaceLeave, PreferredBufferScale = &OnPreferredBufferScale });
    private static readonly WlOutput.Listener* OutputListener =
        WlOutput.Listener.Create(new() { Mode = &OnOutputMode, Scale = &OnOutputScale });

    [UnmanagedCallersOnly]
    private static void OnPreferredScale(void* data, WpFractionalScaleV1 self, uint scale) => Self(data).State.OnFractional(scale);

    [UnmanagedCallersOnly]
    private static void OnPreferredBufferScale(void* data, WlSurface self, int factor) => Self(data).State.OnPreferredBufferScale(factor);

    [UnmanagedCallersOnly]
    private static void OnSurfaceEnter(void* data, WlSurface self, WlOutput output) => Self(data).State.OnEnter(output.Handle);

    [UnmanagedCallersOnly]
    private static void OnSurfaceLeave(void* data, WlSurface self, WlOutput output) => Self(data).State.OnLeave(output.Handle);

    [UnmanagedCallersOnly]
    private static void OnOutputMode(void* data, WlOutput output, WlOutput.Mode flags, int width, int height, int refresh) =>
        Self(data).State.OnOutputMode(output.Handle, (flags & WlOutput.Mode.Current) != 0, refresh);

    [UnmanagedCallersOnly]
    private static void OnOutputScale(void* data, WlOutput output, int factor) => Self(data).State.OnOutputScale(output.Handle, factor);
}
