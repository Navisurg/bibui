using Bibui.Raster;

namespace Bibui.Platform.Wayland;

/// <summary>Whether the host presents through EGL and the GPU, and if not, why not.</summary>
internal enum GpuChoice
{
    /// <summary>Try EGL: the backend can draw through GL, and nothing said no.</summary>
    Gpu,
    /// <summary>BIBUI_GPU=0 (or false/off/no) in the environment.</summary>
    OffByEnvironment,
    /// <summary><see cref="WaylandHostOptions.Gpu"/> is false.</summary>
    OffByOptions,
    /// <summary>The runtime's raster backend draws only on the CPU (the managed rasterizer).</summary>
    CpuBackend,
    /// <summary>libEGL.so.1 or libwayland-egl.so.1 does not load.</summary>
    NoEgl,
}

/// <summary>
/// Which way a frame reaches the compositor. The GPU path needs all of: a
/// raster backend that draws through GL (<see cref="IGlRasterBackend"/>, i.e.
/// Skia), EGL on the machine, and nobody turning it off — BIBUI_GPU=0, the
/// switch every bibui window path reads, or <see cref="WaylandHostOptions.Gpu"/>.
/// Anything else, or an EGL that fails to set up, is the wl_shm path.
/// </summary>
internal static class GpuSelection
{
    public const string EnvironmentVariable = "BIBUI_GPU";

    public static GpuChoice Decide(string? environment, bool allowedByOptions, IRasterBackend raster, Func<bool> eglAvailable)
    {
        if (IsOff(environment)) return GpuChoice.OffByEnvironment;
        if (!allowedByOptions) return GpuChoice.OffByOptions;
        if (raster is not IGlRasterBackend) return GpuChoice.CpuBackend;
        // Probed last: loading libraries is the only part that costs anything.
        if (!eglAvailable()) return GpuChoice.NoEgl;
        return GpuChoice.Gpu;
    }

    /// <summary>The host's own choice, from the process environment and the machine.</summary>
    public static GpuChoice Decide(bool allowedByOptions, IRasterBackend raster) =>
        Decide(Environment.GetEnvironmentVariable(EnvironmentVariable), allowedByOptions, raster, static () => Egl.IsAvailable);

    internal static bool IsOff(string? value) =>
        value is not null && value.Trim().ToLowerInvariant() is "0" or "false" or "off" or "no";

    /// <summary>
    /// BIBUI_GPU_LOG=1 prints which path a window took (and the GL renderer).
    /// A GPU path that was wanted and failed is printed regardless.
    /// </summary>
    public static bool Verbose => Environment.GetEnvironmentVariable("BIBUI_GPU_LOG") is "1" or "true";
}

/// <summary>The GPU path cannot present any more: the host goes back to wl_shm.</summary>
internal sealed class GpuPresentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The GPU presentation path: an EGL context on the host's own wl_display, a
/// wl_egl_window over its wl_surface, and the raster backend bound to that
/// context (<see cref="IGlRasterContext"/>), drawing each frame straight into
/// the window's framebuffer.
///
/// <para>Swap interval 0: <c>eglSwapBuffers</c> attaches, damages and commits
/// without waiting, and the host keeps pacing by its own wl_surface.frame
/// callbacks, exactly as on the shm path. Anything set on the surface before
/// the swap (the frame callback, the configure ack, the viewport) goes out in
/// the commit the swap makes.</para>
///
/// <para>Desktop OpenGL first, OpenGL ES 3 then 2 when the driver offers no
/// desktop GL. An RGBA8 config with 8 bits of stencil, whose alpha makes the
/// buffer ARGB8888 — what the shm path presents too.</para>
/// </summary>
internal sealed unsafe class GpuPresenter : IDisposable
{
    private nint _display, _context, _eglSurface, _window;
    private IGlRasterContext? _gl;
    private IRasterSurface? _target;
    private int _width, _height;
    private const int StencilBits = 8;

    private GpuPresenter() { }

    /// <summary>GL_RENDERER of the context, e.g. "AMD Radeon Graphics (radeonsi, …)".</summary>
    public string Renderer { get; private set; } = "";

    /// <summary>GL_VERSION of the context.</summary>
    public string Version { get; private set; } = "";

    /// <summary>The raster backend's GL context.</summary>
    public IGlRasterContext Gl => _gl!;

    /// <summary>
    /// Sets EGL up on <paramref name="wlDisplay"/> for <paramref name="wlSurface"/>
    /// at <paramref name="width"/>×<paramref name="height"/>, makes it current on
    /// this thread and binds <paramref name="backend"/> to it. Null, with the
    /// reason, when any step fails; everything made so far is released.
    /// </summary>
    public static GpuPresenter? TryCreate(nint wlDisplay, nint wlSurface, int width, int height,
                                          IGlRasterBackend backend, out string? error)
    {
        var p = new GpuPresenter();
        try
        {
            error = p.Init(wlDisplay, wlSurface, width, height, backend);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            error = e.Message;
        }
        if (error is null) return p;
        p.Dispose();
        return null;
    }

    private string? Init(nint wlDisplay, nint wlSurface, int width, int height, IGlRasterBackend backend)
    {
        try { _display = Egl.eglGetPlatformDisplay(Egl.EGL_PLATFORM_WAYLAND_KHR, wlDisplay, null); }
        catch (EntryPointNotFoundException) { _display = 0; } // EGL 1.4: no platform displays
        if (_display == 0) _display = Egl.eglGetDisplay(wlDisplay);
        if (_display == 0) return "no EGL display for the Wayland connection";
        int major, minor;
        if (Egl.eglInitialize(_display, &major, &minor) == 0) { _display = 0; return $"eglInitialize failed ({Egl.Error()})"; }

        nint config = 0;
        int* ctxAttribs = stackalloc int[3];
        foreach (var (api, renderable, clientVersion) in new (uint, int, int)[]
                 {
                     (Egl.EGL_OPENGL_API, Egl.EGL_OPENGL_BIT, 0),
                     (Egl.EGL_OPENGL_ES_API, 0x40 /* EGL_OPENGL_ES3_BIT */, 3),
                     (Egl.EGL_OPENGL_ES_API, Egl.EGL_OPENGL_ES2_BIT, 2),
                 })
        {
            if (Egl.eglBindAPI(api) == 0) continue;
            config = ChooseConfig(renderable);
            if (config == 0) continue;
            ctxAttribs[0] = clientVersion == 0 ? Egl.EGL_NONE : Egl.EGL_CONTEXT_CLIENT_VERSION;
            ctxAttribs[1] = clientVersion;
            ctxAttribs[2] = Egl.EGL_NONE;
            _context = Egl.eglCreateContext(_display, config, 0, ctxAttribs);
            if (_context != 0) break;
        }
        if (_context == 0) return $"no EGL context with an RGBA8 + stencil window config ({Egl.Error()})";

        _window = Egl.wl_egl_window_create(wlSurface, width, height);
        if (_window == 0) return "wl_egl_window_create failed";
        _width = width;
        _height = height;
        _eglSurface = Egl.eglCreateWindowSurface(_display, config, _window, null);
        if (_eglSurface == 0) return $"eglCreateWindowSurface failed ({Egl.Error()})";
        if (Egl.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) == 0)
            return $"eglMakeCurrent failed ({Egl.Error()})";
        // The host paces by frame callbacks; a swap that waited for vblank too
        // would block the loop a second time.
        Egl.eglSwapInterval(_display, 0);

        Renderer = Egl.GlString(Egl.GL_RENDERER) ?? "?";
        Version = Egl.GlString(Egl.GL_VERSION) ?? "?";

        _gl = backend.CreateGlContext(Egl.ProcAddress, out var glError);
        if (_gl is null) return glError ?? "the raster backend could not use the GL context";
        return null;
    }

    private nint ChooseConfig(int renderable)
    {
        int* attribs = stackalloc int[]
        {
            Egl.EGL_SURFACE_TYPE, Egl.EGL_WINDOW_BIT,
            Egl.EGL_RENDERABLE_TYPE, renderable,
            Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_ALPHA_SIZE, 8,
            Egl.EGL_STENCIL_SIZE, StencilBits,
            Egl.EGL_NONE,
        };
        const int Max = 64;
        nint* configs = stackalloc nint[Max];
        int count;
        if (Egl.eglChooseConfig(_display, attribs, configs, Max, &count) == 0 || count == 0) return 0;
        // ChooseConfig sorts deeper colour first (10-bit); the framebuffer is wrapped as RGBA8, so take exactly that.
        for (int i = 0; i < count; i++)
        {
            int r, a, s;
            Egl.eglGetConfigAttrib(_display, configs[i], Egl.EGL_RED_SIZE, &r);
            Egl.eglGetConfigAttrib(_display, configs[i], Egl.EGL_ALPHA_SIZE, &a);
            Egl.eglGetConfigAttrib(_display, configs[i], Egl.EGL_STENCIL_SIZE, &s);
            if (r == 8 && a == 8 && s == StencilBits) return configs[i];
        }
        return 0;
    }

    /// <summary>
    /// The surface to draw this frame into, at <paramref name="width"/>×<paramref name="height"/>
    /// physical pixels: the window resized (wl_egl_window_resize, applied at the
    /// next swap) and the render target remade when the size changed.
    /// </summary>
    public IRasterSurface Target(int width, int height)
    {
        if (_target is not null && width == _width && height == _height) return _target;
        if (width != _width || height != _height)
        {
            Egl.wl_egl_window_resize(_window, width, height, 0, 0);
            _width = width;
            _height = height;
        }
        _target?.Dispose();
        _target = null;
        try { return _target = _gl!.CreateFramebufferSurface(0, width, height, StencilBits, 0); }
        catch (InvalidOperationException e) { throw new GpuPresentException(e.Message, e); }
    }

    /// <summary>
    /// Flushes the backend's GL work and swaps: attach, damage, commit (and
    /// flush) on the wl_surface. A swap EGL refuses (a lost context, a surface
    /// the driver dropped) is a <see cref="GpuPresentException"/>, which the
    /// host answers by going back to wl_shm rather than ending the app.
    /// </summary>
    public void Swap()
    {
        _gl!.Flush();
        if (Egl.eglSwapBuffers(_display, _eglSurface) == 0)
            throw new GpuPresentException($"eglSwapBuffers failed ({Egl.Error()})");
    }

    /// <summary>Releases everything, in the order GL needs: Skia's objects while the context is current, then EGL's.</summary>
    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
        _gl?.Dispose();
        _gl = null;
        if (_display != 0)
        {
            Egl.eglMakeCurrent(_display, 0, 0, 0);
            if (_eglSurface != 0) Egl.eglDestroySurface(_display, _eglSurface);
            if (_context != 0) Egl.eglDestroyContext(_display, _context);
            Egl.eglTerminate(_display);
            Egl.eglReleaseThread();
        }
        if (_window != 0) Egl.wl_egl_window_destroy(_window);
        _display = _context = _eglSurface = _window = 0;
    }
}
