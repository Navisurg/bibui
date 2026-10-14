using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Bibui.Theming;
using Bibui.Raster;
using Bibui.Renderer;

namespace Bibui;

/// <summary>
/// One frame, in two passes that never mix: <see cref="Layout"/> solves every
/// node's rect, then <see cref="Render"/> or <see cref="RenderTo"/> paints
/// the tree as laid out. Painting lays nothing out.
/// </summary>
public sealed class BibuiRenderer
{
    // One RenderCaches and one Fonts per renderer: typefaces load once, and
    // both paint paths share the path caches and the measure and line caches.
    private readonly RenderCaches _caches;
    private readonly Fonts _fonts;
    private readonly Painter _painter;

    /// <summary>
    /// A renderer on the default raster backend (<see cref="RasterBackends"/>):
    /// Skia, when the app references Bibui.Raster.Skia, the cross-platform
    /// default. The extension methods for SkiaSharp surfaces
    /// (<c>RenderTo(SKSurface, …)</c>, <c>SkiaSurface</c>) live there too.
    /// Under Native AOT pass the backend instead: the default is loaded by
    /// name, which a trimmed publish may have dropped.
    /// </summary>
    [RequiresUnreferencedCode(RasterBackends.TrimMessage)]
    public BibuiRenderer() : this(RasterBackends.CreateDefault()) { }

    /// <summary>
    /// A renderer on <paramref name="raster"/>, which it now owns. Chosen
    /// explicitly — there is no discovery, so nothing here needs reflection.
    /// </summary>
    public BibuiRenderer(IRasterBackend raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        _caches = new RenderCaches(raster);
        _fonts = new Fonts(raster);
        _painter = new Painter(_caches, _fonts);
        _measure = MeasureLeaf;
    }

    /// <summary>The raster backend this renderer paints with.</summary>
    public IRasterBackend Raster => _caches.Backend;

    /// <summary>
    /// Whether the frame just painted left anything mid-transition — a fade
    /// still travelling, a spinner turning. A host that draws on demand reads
    /// this after each render to decide whether to schedule another; one that
    /// draws continuously can ignore it.
    /// </summary>
    public bool IsAnimating => _painter.IsAnimating;

    /// <summary>This renderer's fonts: what it measures and draws text with, and its caches.</summary>
    internal Fonts Fonts => _fonts;

    // ── Layout ───────────────────────────────────────────────────────────────

    // What a text is measured in, for the frame being laid out; a field so the
    // delegate the solver calls is made once.
    private readonly MeasureLeaf _measure;
    private ThemeTokens? _measureTheme;
    private float _measureScale = 1f;

    /// <summary>
    /// Lays <paramref name="root"/> out over the whole surface: every node's
    /// rect, extent and content size, for the paint and for the next frame's
    /// asks. A text measures itself in this renderer's fonts.
    /// </summary>
    public void Layout(UiNode root, SurfaceOptions surface, ThemeTokens theme)
    {
        (_measureTheme, _measureScale) = (theme, surface.ScaleFactor);
        LayoutSolver.Layout(root, System.Numerics.Vector2.Zero,
            new System.Numerics.Vector2(surface.Width, surface.Height), _measure);
    }

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

    // ── Paint ────────────────────────────────────────────────────────────────

    private static readonly bool _profile = Environment.GetEnvironmentVariable("BIBUI_PROFILE") == "1";
    private static int _renderCount;

    // Pooled across renders to avoid allocating ~6.7MB (1080p) of raster surface
    // storage and ~6.7MB of BGRA output buffer on every frame. Reused as long
    // as the surface size is unchanged; reallocated on resize.
    private IRasterSurface? _pooledSurface;
    private int _pooledSurfaceW;
    private int _pooledSurfaceH;
    private byte[]? _pooledRawBgra;

    /// <summary>
    /// Immediate-mode fast path: paints the laid-out <paramref name="root"/>
    /// onto a caller-supplied surface (one from <see cref="Raster"/>, or over
    /// memory a host owns). No pixel readback and no PNG factory.
    /// </summary>
    public void RenderTo(IRasterSurface target, UiNode root, SurfaceOptions surface, ThemeTokens theme, FrameInput? input = null)
        => _painter.Paint(target, root, surface, theme, input ?? FrameInput.None);

    /// <summary>
    /// Paints the laid-out <paramref name="root"/> on the CPU and reads the
    /// pixels back: the verification and readback path.
    /// </summary>
    public RenderOutput Render(UiNode root, SurfaceOptions surface, ThemeTokens theme, FrameInput? input = null)
    {
        var physW = SurfaceOptions.PhysicalSize(surface.Width, surface.ScaleFactor);
        var physH = SurfaceOptions.PhysicalSize(surface.Height, surface.ScaleFactor);

        var t0 = Stopwatch.GetTimestamp();
        long a0 = _profile ? GC.GetAllocatedBytesForCurrentThread() : 0;

        // Reuse the surface across renders. A raster surface owns
        // ~physW*physH*4 bytes of pixel storage; allocating fresh every frame
        // burns ~6-8MB/frame at typical sizes and dominates GC pressure.
        if (_pooledSurface is null || _pooledSurfaceW != physW || _pooledSurfaceH != physH)
        {
            _pooledSurface?.Dispose();
            _pooledSurface = _caches.Backend.CreateSurface(physW, physH);
            _pooledSurfaceW = physW;
            _pooledSurfaceH = physH;
        }
        var target = _pooledSurface;

        var t1 = Stopwatch.GetTimestamp(); // after surface alloc

        _painter.Paint(target, root, surface, theme, input ?? FrameInput.None);

        var t2 = Stopwatch.GetTimestamp(); // after the paint
        long a2 = _profile ? GC.GetAllocatedBytesForCurrentThread() : 0;

        // Read raw BGRA pixels straight out of the CPU raster buffer — no PNG encode.
        // The output array is also pooled (sized once per surface size) — the live
        // runtime consumes RawBgraBytes synchronously before the next Render call.
        int rawLen = physW * physH * 4;
        if (_pooledRawBgra is null || _pooledRawBgra.Length != rawLen)
            _pooledRawBgra = new byte[rawLen];
        var rawBytes = _pooledRawBgra;
        if (!target.ReadPixels(rawBytes, physW, physH))
            throw new InvalidOperationException($"Reading back the {physW}x{physH} frame failed.");

        var t3 = Stopwatch.GetTimestamp(); // after ReadPixels

        // Capture locals for the deferred PNG factory (used only by verification/testing).
        int capturedW = physW, capturedH = physH;
        var capturedRaw = rawBytes;
        var backend = _caches.Backend;

        var result = new RenderOutput
        {
            RawBgraBytes = rawBytes,
            Width = physW,
            Height = physH,
            PngFactory = () => backend.EncodePng(capturedRaw, capturedW, capturedH),
        };

        if (_profile)
        {
            long a3 = GC.GetAllocatedBytesForCurrentThread();
            double freq = Stopwatch.Frequency / 1000.0;
            int n = System.Threading.Interlocked.Increment(ref _renderCount);
            int rawKb = rawBytes.Length / 1024;
            Console.WriteLine(
                $"[Painter #{n}] {physW}x{physH}  " +
                $"SurfaceAlloc={Ms(t1-t0,freq):F2}ms  " +
                $"Paint={Ms(t2-t1,freq):F2}ms  " +
                $"ReadPixels={Ms(t3-t2,freq):F2}ms  " +
                $"Total={Ms(t3-t0,freq):F2}ms  " +
                $"PaintAlloc={(a2-a0)/1024}KB  PostAlloc={(a3-a2)/1024}KB  ({rawKb}KB raw, PNG deferred)");
        }

        return result;

        static double Ms(long ticks, double freq) => ticks / freq;
    }
}
