// The browser host, shared by the browser spikes: a bibui runtime per
// <canvas>, driven from JS (bibui-host.js). The browser owns the loop, so it
// is inverted: JS calls Frame for each visible surface once per
// requestAnimationFrame, and input is queued as WindowEvents for that
// surface's next BeginBuild, as a desktop window's event pump queues them.
//
// Each frame: build, ManagedRenderer into a pinned BGRA premultiplied buffer,
// convert to RGBA straight alpha, hand the bytes to JS (putImageData).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using Bibui;
using Bibui.Draw;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

namespace Bibui.Browser;

/// <summary>One canvas: its runtime, its renderer, its queued input and its pixels.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserSurface : IDisposable
{
    public readonly int Handle;
    public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
    readonly Action<BibuiRuntime> _build;
#if SKIA
    readonly Bibui.Raster.Skia.SkiaCpuRenderer _renderer = new();
#else
    readonly ManagedRenderer _renderer = new();
#endif
    internal readonly List<WindowEvent> Events = new();
    byte[] _bgra = [], _rgba = [];
    int _w, _h;
    internal bool Dirty = true;
    // The clipboard as the last paste event gave it: the runtime reads the
    // clipboard synchronously, and the browser only hands it over in a paste.
    internal string? PastedText;

    public double BuildMs, DrawMs, ConvertMs;

    public BrowserSurface(int handle, Action<BibuiRuntime> build)
    {
        Handle = handle;
        _build = build;
        Runtime.WindowClipboard = (() => PastedText, BrowserHost.WriteClipboard);
    }

    /// <summary>
    /// One frame if one is owed (input, a resize, or the runtime asking for
    /// frames while it animates), or always with <paramref name="force"/>.
    /// False when nothing was drawn.
    /// </summary>
    public bool Frame(bool force)
    {
        var rt = Runtime;
        if (!force && !Dirty && Events.Count == 0 && rt.FramesRequested == 0) return false;
        Dirty = false;
        rt.ConsumeFrame();

        long t0 = Stopwatch.GetTimestamp();
        rt.BeginBuild(Events);
        Events.Clear();
        _build(rt);
        rt.EndBuild();
        long t1 = Stopwatch.GetTimestamp();

        int w = SurfaceOptions.PhysicalSize(rt.SurfaceWidth, rt.ScaleFactor);
        int h = SurfaceOptions.PhysicalSize(rt.SurfaceHeight, rt.ScaleFactor);
        if (w <= 0 || h <= 0) return false;
        if (w != _w || h != _h)
        {
            (_w, _h) = (w, h);
            _bgra = GC.AllocateArray<byte>(w * h * 4, pinned: true);
            _rgba = new byte[w * h * 4];
        }
        _renderer.Draw(rt.DrawList, new RasterPixels(Marshal.UnsafeAddrOfPinnedArrayElement(_bgra, 0), w, h, w * 4));
        long t2 = Stopwatch.GetTimestamp();
        BgraPremulToRgba(_bgra, _rgba);
        long t3 = Stopwatch.GetTimestamp();
        BrowserHost.Present(Handle, _rgba, w, h);

        double f = Stopwatch.Frequency / 1000.0;
        (BuildMs, DrawMs, ConvertMs) = ((t1 - t0) / f, (t2 - t1) / f, (t3 - t2) / f);
        return true;
    }

    // ImageData is RGBA with straight alpha; the renderer draws BGRA premultiplied.
    static void BgraPremulToRgba(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var s = MemoryMarshal.Cast<byte, uint>(src);
        var d = MemoryMarshal.Cast<byte, uint>(dst);
        for (int i = 0; i < s.Length; i++)
        {
            uint p = s[i];
            uint a = p >> 24;
            if (a == 255) { d[i] = (p & 0xFF00FF00) | ((p >> 16) & 0xFF) | ((p & 0xFF) << 16); continue; }
            if (a == 0) { d[i] = 0; continue; }
            uint b = (p & 0xFF) * 255 / a, g = ((p >> 8) & 0xFF) * 255 / a, r = ((p >> 16) & 0xFF) * 255 / a;
            d[i] = (a << 24) | (b << 16) | (g << 8) | r;
        }
    }

    public void Dispose() => _renderer.Dispose();
}

/// <summary>
/// The JS side's entry points, by surface handle. The app sets
/// <see cref="Factory"/>: what a surface named by an id builds each frame.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class BrowserHost
{
    /// <summary>The build for an id, or null for an id the app does not know.</summary>
    public static Func<string, Action<BibuiRuntime>?> Factory { get; set; } = _ => null;

    /// <summary>The ids <see cref="Factory"/> knows, for <c>demos()</c>.</summary>
    public static Func<string[]> Ids { get; set; } = () => [];

    static readonly Dictionary<int, BrowserSurface> _surfaces = new();
    static int _next = 1;

    public static BrowserSurface? Surface(int handle) => _surfaces.GetValueOrDefault(handle);

    /// <summary>A new surface building <paramref name="id"/>, or 0 for an unknown id.</summary>
    [JSExport]
    public static int Create(string id)
    {
        var build = Factory(id);
        if (build is null) return 0;
        int h = _next++;
        _surfaces[h] = new BrowserSurface(h, build);
        return h;
    }

    [JSExport]
    public static void Destroy(int handle)
    {
        if (_surfaces.Remove(handle, out var s)) s.Dispose();
    }

    [JSExport] public static string[] List() => Ids();

    [JSExport]
    public static void Resize(int handle, int physicalWidth, int physicalHeight, double scale)
    {
        if (Surface(handle) is not { } s) return;
        s.Runtime.ResizeSurface(physicalWidth, physicalHeight, (float)scale);
        s.Dirty = true;
    }

    /// <summary>Pointer: kind 0 move, 1 down, 2 up. Position in CSS pixels. Button is DOM's (0 left, 1 middle, 2 right).</summary>
    [JSExport]
    public static void Pointer(int handle, int kind, double x, double y, int button, int modifiers)
    {
        if (Surface(handle) is not { } s) return;
        var k = kind switch { 1 => WindowEventKind.MouseDown, 2 => WindowEventKind.MouseUp, _ => WindowEventKind.MouseMove };
        var b = button switch { 0 => MouseButton.Left, 1 => MouseButton.Middle, 2 => MouseButton.Right, _ => MouseButton.Unknown };
        s.Events.Add(WindowEvent.Mouse(k, (float)x, (float)y, kind == 0 ? MouseButton.Unknown : b, (KeyModifiers)modifiers));
    }

    /// <summary>A wheel, in notches (positive is up/left, as GLFW reports it).</summary>
    [JSExport]
    public static void Wheel(int handle, double x, double y, double dx, double dy, int modifiers)
        => Surface(handle)?.Events.Add(WindowEvent.Wheel((float)x, (float)y, (float)dx, (float)dy, (KeyModifiers)modifiers));

    /// <summary>
    /// A key went down or up: <paramref name="key"/> is bibui's <see cref="Key"/>
    /// (GLFW's code, which bibui-host.js maps from KeyboardEvent.code), and
    /// <paramref name="text"/> what it typed, when it typed something: queued
    /// after the key-down, as GLFW's char callback follows its key callback.
    /// The runtime turns these into Press/Navigate/Edit/Submit/Copy and Text
    /// exactly as it does for a desktop window.
    /// </summary>
    [JSExport]
    public static void KeyEvent(int handle, bool down, int key, int modifiers, string? text)
    {
        if (Surface(handle) is not { } s) return;
        var m = (KeyModifiers)modifiers;
        s.Events.Add(down ? WindowEvent.KeyPress((Key)key, m) : WindowEvent.KeyRelease((Key)key, m));
        if (down && !string.IsNullOrEmpty(text))
            foreach (var r in text.EnumerateRunes()) s.Events.Add(WindowEvent.Text(r));
    }

    /// <summary>A paste event's text, and the Ctrl+V that asks for it.</summary>
    [JSExport]
    public static void Paste(int handle, string text)
    {
        if (Surface(handle) is not { } s) return;
        s.PastedText = text;
        s.Events.Add(WindowEvent.KeyPress(Key.V, KeyModifiers.Ctrl));
        s.Events.Add(WindowEvent.KeyRelease(Key.V, KeyModifiers.Ctrl));
    }

    /// <summary>The canvas lost focus: whatever was held is not held any more.</summary>
    [JSExport]
    public static void Blur(int handle) => Surface(handle)?.Events.Add(WindowEvent.FocusLost());

    [JSExport] public static bool Frame(int handle, bool force) => Surface(handle)?.Frame(force) ?? false;

    [JSExport] public static double BuildMs(int handle) => Surface(handle)?.BuildMs ?? 0;
    [JSExport] public static double DrawMs(int handle) => Surface(handle)?.DrawMs ?? 0;
    [JSExport] public static double ConvertMs(int handle) => Surface(handle)?.ConvertMs ?? 0;

    [JSImport("present", "bibui-host")]
    internal static partial void Present(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height);

    [JSImport("writeClipboard", "bibui-host")]
    internal static partial void WriteClipboard(string text);
}
