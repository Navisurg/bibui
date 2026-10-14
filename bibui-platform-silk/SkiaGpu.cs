using Bibui.Raster.Skia;
using Bibui.Runtime.Platform;
using SkiaSharp;

namespace Bibui.Runtime.Platform
{
    /// <summary>
    /// What a host that presents through Skia's GPU backend offers on top of
    /// <see cref="IPlatformHost"/>: the context, for GPU-resident offscreen
    /// surfaces and images. Optional — a CPU host does not implement it, and
    /// <c>BibuiWindow.GraphicsContext</c> is then null. Here rather than in
    /// bibui: it is Skia-typed, and bibui references no Skia.
    /// </summary>
    public interface ISkiaGpuPlatformHost : IPlatformHost
    {
        /// <summary>The live Skia GPU context, or null when presenting on the CPU.</summary>
        GRContext? GraphicsContext { get; }

        /// <summary>Byte budget for Skia's GPU resource cache; may be set before <see cref="IPlatformHost.Open"/>.</summary>
        long GpuResourceCacheLimit { get; set; }
    }
}

namespace Bibui.Runtime
{
    /// <summary>
    /// The Skia GPU side of a window and a runtime. These were members of
    /// <see cref="BibuiWindow"/> and <see cref="BibuiRuntime"/> until the Skia
    /// backend left the core libraries; as extension members they keep
    /// their call syntax for an app that references this package.
    /// </summary>
    public static class SkiaGpuExtensions
    {
        extension(BibuiWindow window)
        {
            /// <summary>
            /// The window's Skia GPU context, or null when rendering on the CPU
            /// fallback path (BIBUI_GPU=0, or GL init failed), or when the host does
            /// not present through Skia's GPU backend at all. Non-null only while the
            /// window is open.
            ///
            /// Use it to create GPU-resident resources (offscreen surfaces via
            /// <c>CreateOffscreenSurface</c>, texture-backed images) that
            /// composite into the window without CPU readback or re-upload.
            ///
            /// Rules:
            /// - Only touch it from the render thread (inside render/draw callbacks)
            ///   — the GL context is current only there.
            /// - Anything created from it dies with the window: dispose GPU surfaces
            ///   and images before the window closes.
            /// - If you issue raw GL calls yourself, call ResetContext() before the
            ///   next Skia draw.
            /// </summary>
            public GRContext? GraphicsContext => (window.Host as ISkiaGpuPlatformHost)?.GraphicsContext;

            /// <summary>
            /// Byte budget for Skia's GPU resource cache (glyph atlases, uploaded
            /// textures, gradient ramps). Set before Run or at runtime; hosts that
            /// draw many large images (video frames, plots) should raise this above
            /// the default or hot textures will be evicted and re-uploaded. Zero,
            /// and ignored, for a host that does not present through Skia's GPU backend.
            /// </summary>
            public long GpuResourceCacheLimit
            {
                get => (window.Host as ISkiaGpuPlatformHost)?.GpuResourceCacheLimit ?? 0;
                set
                {
                    if (window.Host is ISkiaGpuPlatformHost gpu) gpu.GpuResourceCacheLimit = value;
                }
            }

            /// <summary>
            /// Creates an offscreen surface for rendering content that is later drawn
            /// into the window (via Snapshot() or Draw()). GPU-backed when the GPU
            /// path is active — drawing happens on the GPU and the snapshot composites
            /// with zero upload — otherwise a CPU raster surface, so callers never
            /// need to branch. Caller owns the surface; dispose it before the window
            /// closes. Render-thread only on the GPU path.
            /// </summary>
            public SKSurface CreateOffscreenSurface(SKImageInfo info)
            {
                if (window.GraphicsContext is { } grContext)
                {
                    var gpu = SKSurface.Create(grContext, budgeted: false, info);
                    if (gpu is not null) return gpu;
                }
                return SKSurface.Create(info)
                    ?? throw new InvalidOperationException($"SKSurface.Create failed for {info.Width}x{info.Height} {info.ColorType}.");
            }
        }

        /// <summary>
        /// <see cref="BibuiRuntime.RenderTo(Bibui.Raster.IRasterSurface)"/> onto a surface
        /// made with SkiaSharp — typically a GPU surface over a window's framebuffer,
        /// which is how the Skia GPU host presents. Only on the Skia raster backend.
        /// </summary>
        public static void RenderTo(this BibuiRuntime runtime, SKSurface target) =>
            runtime.RenderTo(SkiaRendering.Backend(runtime.Raster).Wrap(target));
    }
}
