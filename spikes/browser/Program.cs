// Spike: the counter sample (samples/counter) in the browser. The browser
// owns the loop, so it is inverted: main.js calls Frame once per
// requestAnimationFrame, and pointer events are queued for the next frame.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Bibui;
using Bibui.Draw;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

Console.WriteLine("browser-counter: managed Main");
return 0;

[System.Runtime.Versioning.SupportedOSPlatform("browser")]
public static partial class Host
{
    static BibuiRuntime? _runtime;
#if SKIA
    static readonly Bibui.Raster.Skia.SkiaCpuRenderer _renderer = new();
#else
    static readonly ManagedRenderer _renderer = new();
#endif
    static readonly List<WindowEvent> _events = new();
    static byte[] _bgra = [], _rgba = [];
    static int _w, _h;
    static int _count;
    static bool _heavy;
    static bool _dirty = true;

    public static double LastBuildMs, LastDrawMs, LastConvertMs;

    [JSExport]
    public static void Start(bool heavy)
    {
        _heavy = heavy;
        _runtime = BibuiRuntime.Create();
    }

    [JSExport]
    public static void Resize(int physicalWidth, int physicalHeight, double scale)
    {
        _runtime!.ResizeSurface(physicalWidth, physicalHeight, (float)scale);
        _dirty = true;
    }

    [JSExport] public static void PointerMove(double x, double y) { _events.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, (float)x, (float)y, MouseButton.Unknown)); }
    [JSExport] public static void PointerDown(double x, double y, int button) { _events.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, (float)x, (float)y, Button(button))); }
    [JSExport] public static void PointerUp(double x, double y, int button) { _events.Add(WindowEvent.Mouse(WindowEventKind.MouseUp, (float)x, (float)y, Button(button))); }
    [JSExport] public static void Wheel(double x, double y, double dx, double dy) { _events.Add(WindowEvent.Wheel((float)x, (float)y, (float)dx, (float)dy)); }

    static MouseButton Button(int b) => b switch { 0 => MouseButton.Left, 1 => MouseButton.Middle, 2 => MouseButton.Right, _ => MouseButton.Unknown };

    /// <summary>
    /// One frame, if one is owed: build, draw into BGRA, convert to RGBA and
    /// hand the bytes to JS. Returns false when nothing was drawn.
    /// </summary>
    [JSExport]
    public static bool Frame(bool force)
    {
        var rt = _runtime!;
        if (!force && !_dirty && _events.Count == 0 && rt.FramesRequested == 0) return false;
        _dirty = false;
        rt.ConsumeFrame();

        long t0 = Stopwatch.GetTimestamp();
        rt.BeginBuild(_events);
        _events.Clear();
        if (_heavy) BuildHeavy(rt); else BuildCounter(rt);
        rt.EndBuild();
        long t1 = Stopwatch.GetTimestamp();

        int w = SurfaceOptions.PhysicalSize(rt.SurfaceWidth, rt.ScaleFactor);
        int h = SurfaceOptions.PhysicalSize(rt.SurfaceHeight, rt.ScaleFactor);
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
        Present(_rgba, w, h);

        double f = Stopwatch.Frequency / 1000.0;
        (LastBuildMs, LastDrawMs, LastConvertMs) = ((t1 - t0) / f, (t2 - t1) / f, (t3 - t2) / f);
        return true;
    }

    [JSExport] public static double BuildMs() => LastBuildMs;
    [JSExport] public static double DrawMs() => LastDrawMs;
    [JSExport] public static double ConvertMs() => LastConvertMs;
    [JSExport] public static int Count() => _count;

    [JSImport("present", "main.js")]
    static partial void Present([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height);

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

    // samples/counter's build, unchanged.
    static void BuildCounter(BibuiRuntime runtime)
    {
        using (runtime.PushParent(runtime.ColumnFit()))
        {
            runtime.Text($"Clicked {_count} times");
            if (runtime.Button("add", "Add one").Clicked) _count++;
        }
    }

    // A heavier scene: a grid of labelled buttons.
    static void BuildHeavy(BibuiRuntime runtime)
    {
        using (runtime.PushParent(runtime.ColumnFit()))
        {
            runtime.Text($"Clicked {_count} times");
            for (int r = 0; r < 12; r++)
                using (runtime.PushParent(runtime.RowFit()))
                    for (int c = 0; c < 8; c++)
                        if (runtime.Button($"b{r}_{c}", $"Button {r * 8 + c}").Clicked) _count++;
        }
    }
}
