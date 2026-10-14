using System.Runtime.InteropServices;
using Bibui.Platform.Wayland;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Raster;
using Bibui.Raster.Managed.Backend;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

namespace Bibui.WaylandTests;

/// <summary>
/// The GPU path: which way the host presents (<see cref="GpuSelection"/>) as
/// pure logic, and one live probe that sets EGL and Skia's GL backend up on a
/// surface that is never given a role or committed, so nothing appears on screen.
/// </summary>
internal static unsafe class GpuTests
{
    public static void Register(Harness t)
    {
        using var skia = new SkiaRasterBackend();
        using var managed = new ManagedRasterBackend();
        static Func<bool> Egl(bool available, int[] probes) => () => { probes[0]++; return available; };

        t.Run("gpu: Skia, EGL present and nothing set → the GPU path", () =>
        {
            var probes = new int[1];
            Assert.Eq(GpuChoice.Gpu, GpuSelection.Decide(null, true, skia, Egl(true, probes)));
            Assert.Eq(GpuChoice.Gpu, GpuSelection.Decide("1", true, skia, Egl(true, probes)), "BIBUI_GPU=1");
            Assert.Eq(GpuChoice.Gpu, GpuSelection.Decide("", true, skia, Egl(true, probes)), "BIBUI_GPU empty");
        });

        t.Run("gpu: BIBUI_GPU=0 (false, off, no) forces wl_shm, before anything is probed", () =>
        {
            foreach (var off in new[] { "0", "false", "off", "no", " OFF ", "False" })
            {
                var probes = new int[1];
                Assert.Eq(GpuChoice.OffByEnvironment, GpuSelection.Decide(off, true, skia, Egl(true, probes)), $"BIBUI_GPU={off}");
                Assert.Eq(0, probes[0], "EGL not probed");
            }
        });

        t.Run("gpu: WaylandHostOptions.Gpu = false forces wl_shm", () =>
        {
            var probes = new int[1];
            Assert.Eq(GpuChoice.OffByOptions, GpuSelection.Decide(null, false, skia, Egl(true, probes)));
            Assert.Eq(0, probes[0], "EGL not probed");
        });

        t.Run("gpu: the managed rasterizer stays on wl_shm, and EGL is not even loaded", () =>
        {
            var probes = new int[1];
            Assert.True((IRasterBackend)managed is not IGlRasterBackend, "the managed backend draws on the CPU only");
            Assert.Eq(GpuChoice.CpuBackend, GpuSelection.Decide(null, true, managed, Egl(true, probes)));
            Assert.Eq(0, probes[0], "EGL not probed");
        });

        t.Run("gpu: no libEGL / libwayland-egl → wl_shm", () =>
        {
            var probes = new int[1];
            Assert.Eq(GpuChoice.NoEgl, GpuSelection.Decide(null, true, skia, Egl(false, probes)));
            Assert.Eq(1, probes[0], "probed once");
        });

        t.Run("gpu: the Skia backend offers GL", () =>
            Assert.True((IRasterBackend)skia is IGlRasterBackend, "SkiaRasterBackend implements IGlRasterBackend"));

        t.Run("gpu: a host that is not open says CPU and names no renderer", () =>
        {
            var host = new WaylandPlatformHost();
            Assert.Eq(HostPresentation.CpuPixels, host.Presentation);
            Assert.True(host.GpuRenderer is null && host.GlContext is null, "no GPU before Open");
            Assert.True(new WaylandHostOptions().Gpu, "GPU allowed by default");
        });

        t.Run("live probe: EGL + Skia GL draw a frame like the CPU does (surface never shown)", () =>
        {
            if (!WaylandPlatformHost.IsSupported) throw new SkipException("no Wayland session");
            if (!Bibui.Platform.Wayland.Egl.IsAvailable) throw new SkipException("no libEGL / libwayland-egl");
            if (GpuSelection.IsOff(Environment.GetEnvironmentVariable(GpuSelection.EnvironmentVariable)))
                throw new SkipException("BIBUI_GPU=0");
            Probe();
        });
    }

    private static WlCompositor _compositor;

    [UnmanagedCallersOnly]
    private static void OnGlobal(void* data, WlRegistry registry, uint name, byte* iface, uint version)
    {
        if (WlUtf8.Read(iface) == WlCompositor.InterfaceName) _compositor = registry.Bind<WlCompositor>(name, Math.Min(version, 4u));
    }

    private static void Probe()
    {
        var display = WaylandNative.wl_display_connect(null);
        Assert.True(display != 0, "connect");
        var registryListener = WlRegistry.Listener.Create(new() { Global = &OnGlobal });
        GpuPresenter? gpu = null;
        try
        {
            _compositor = default;
            var registry = new WlDisplay(display).GetRegistry();
            registry.AddListener(registryListener);
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "roundtrip");
            Assert.True(!_compositor.IsNull, "wl_compositor");
            var surface = _compositor.CreateSurface();

            const int W = 96, H = 64;
            var runtime = new BibuiRuntime(new SkiaRasterBackend());
            runtime.Mount(() => Bibui.Arrange.FillY(Bibui.Arrange.FillX(runtime.Box()))
                    .Styled(new Bibui.Style { BackgroundColor = Bibui.UiColor.Accent, CornerRadius = 12f }),
                new Bibui.SurfaceOptions { Width = W, Height = H, ScaleFactor = 1f });

            gpu = GpuPresenter.TryCreate(display, surface.Handle, W, H, (IGlRasterBackend)runtime.Raster, out var error);
            if (gpu is null) throw new SkipException($"EGL did not set up here: {error}");
            Console.WriteLine($"         GL_RENDERER: {gpu.Renderer} — {gpu.Version}");
            Assert.True(gpu.Renderer.Length > 0, "the renderer is named");

            var target = gpu.Target(W, H);
            runtime.RenderTo(target);
            gpu.Gl.Flush();
            var gpuPixels = new byte[W * H * 4];
            Assert.True(target.ReadPixels(gpuPixels, W, H), "read the framebuffer back");
            var cpuPixels = runtime.Render().RawBgraBytes;

            // The GPU antialiases the rounded corners its own way; the rest is
            // the same flat colour. Compare by mean channel difference.
            long sum = 0;
            for (int i = 0; i < gpuPixels.Length; i++) sum += Math.Abs(gpuPixels[i] - cpuPixels[i]);
            double mean = (double)sum / gpuPixels.Length;
            Console.WriteLine($"         GPU vs CPU: mean channel difference {mean:0.###}");
            Assert.True(mean < 1.5, $"GPU frame matches the CPU frame (mean difference {mean:0.###})");
            int centre = (H / 2 * W + W / 2) * 4;
            Assert.True(gpuPixels.AsSpan(centre, 4).SequenceEqual(cpuPixels.AsSpan(centre, 4)), "the centre pixel is the accent, exactly");

            gpu.Dispose();
            gpu = null;
            surface.Destroy();
            _compositor.DestroyProxy();
            registry.DestroyProxy();
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "clean teardown");
        }
        finally
        {
            gpu?.Dispose();
            WaylandNative.wl_display_disconnect(display);
            WlRegistry.Listener.Free(registryListener);
        }
    }
}
