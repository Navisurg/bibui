namespace Bibui.Raster;

// How a host that owns an OpenGL (or OpenGL ES) context lets the runtime's
// raster backend draw on the GPU, without the host naming the backend or the
// backend naming the host. The Wayland host makes an EGL context and asks
// `runtime.Raster as IGlRasterBackend`; the Skia backend answers with a
// GRContext over that context. A backend that draws only on the CPU (the
// managed rasterizer) does not implement it, and the host keeps its CPU path.

/// <summary>
/// Looks up an OpenGL entry point by name (<c>eglGetProcAddress</c>,
/// <c>glXGetProcAddress</c>), or 0 when there is none.
/// </summary>
public delegate nint GlProcAddress(string name);

/// <summary>
/// A raster backend that can also draw on the GPU, through the OpenGL or
/// OpenGL ES context a host makes current. Optional: a host checks for it with
/// <c>runtime.Raster as IGlRasterBackend</c> and draws on the CPU when it is absent.
/// </summary>
public interface IGlRasterBackend : IRasterBackend
{
    /// <summary>
    /// Binds this backend to the GL context current on the calling thread.
    /// Null, with <paramref name="error"/> saying why, when the backend cannot
    /// drive that context (missing extensions, a software GL it refuses).
    /// Everything made from the result must be used on this thread, with the
    /// same context current, and disposed before the context is destroyed.
    /// </summary>
    IGlRasterContext? CreateGlContext(GlProcAddress getProcAddress, out string? error);
}

/// <summary>A raster backend bound to one GL context.</summary>
public interface IGlRasterContext : IDisposable
{
    /// <summary>
    /// A render target over framebuffer <paramref name="framebuffer"/> (0 is the
    /// window's own) of <paramref name="width"/> by <paramref name="height"/>
    /// physical pixels, RGBA8 with its origin at the bottom left as GL has it,
    /// <paramref name="stencilBits"/> of stencil and <paramref name="samples"/>
    /// samples per pixel (0 or 1: none). The caller owns it, disposes it when
    /// the framebuffer changes size, and draws through it with
    /// <c>BibuiRuntime.RenderTo(IRasterSurface)</c>.
    /// </summary>
    IRasterSurface CreateFramebufferSurface(uint framebuffer, int width, int height, int stencilBits, int samples);

    /// <summary>Sends everything drawn so far to GL. Call before swapping buffers.</summary>
    void Flush();

    /// <summary>
    /// Something other than this backend changed GL state (the host's own GL
    /// calls): forget what the backend had cached about it.
    /// </summary>
    void ResetState();

    /// <summary>Byte budget for the backend's GPU resource cache (glyph atlases, textures, gradients).</summary>
    long ResourceCacheLimit { get; set; }
}
