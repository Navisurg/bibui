using SkiaSharp;

namespace Bibui.Raster.Skia;

public sealed partial class SkiaRasterBackend
{
    /// <summary>
    /// Skia's GPU backend over the GL context current on this thread: a
    /// GRContext built from <paramref name="getProcAddress"/>, so it works for
    /// whatever made the context (EGL, GLX, GLFW). Null when Skia cannot drive it.
    /// </summary>
    public IGlRasterContext? CreateGlContext(GlProcAddress getProcAddress, out string? error)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        var glInterface = GRGlInterface.Create(name => getProcAddress(name));
        if (glInterface is null || !glInterface.Validate())
        {
            glInterface?.Dispose();
            error = "Skia could not assemble a GL interface from this context.";
            return null;
        }
        var context = GRContext.CreateGl(glInterface);
        if (context is null)
        {
            glInterface.Dispose();
            error = "GRContext.CreateGl returned null for this context.";
            return null;
        }
        error = null;
        return new SkiaGlContext(this, glInterface, context);
    }
}

/// <summary>The Skia backend bound to one GL context: a GRContext and the framebuffers drawn through it.</summary>
public sealed class SkiaGlContext : IGlRasterContext
{
    private readonly SkiaRasterBackend _backend;
    private readonly GRGlInterface _interface;

    internal SkiaGlContext(SkiaRasterBackend backend, GRGlInterface glInterface, GRContext context)
    {
        _backend = backend;
        _interface = glInterface;
        Native = context;
    }

    /// <summary>The Skia GPU context, for GPU-resident surfaces and images of the app's own.</summary>
    public GRContext Native { get; }

    public IRasterSurface CreateFramebufferSurface(uint framebuffer, int width, int height, int stencilBits, int samples)
    {
        const uint GlRgba8 = 0x8058;
        var target = new GRBackendRenderTarget(width, height, samples, stencilBits, new GRGlFramebufferInfo(framebuffer, GlRgba8));
        var surface = SKSurface.Create(Native, target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        if (surface is null)
        {
            target.Dispose();
            throw new InvalidOperationException($"SKSurface.Create returned null for GL framebuffer {framebuffer} at {width}x{height}.");
        }
        return new SkiaFramebufferSurface(new SkiaRasterSurface(_backend, surface, owns: true), target);
    }

    public void Flush() => Native.Flush();

    public void ResetState() => Native.ResetContext();

    public long ResourceCacheLimit
    {
        get => Native.GetResourceCacheLimit();
        set => Native.SetResourceCacheLimit(value);
    }

    public void Dispose()
    {
        // Abandoning is for a context already lost; this one is live, so free what it holds.
        Native.Dispose();
        _interface.Dispose();
    }
}

/// <summary>A GPU surface over a GL framebuffer, and the render target it was made from, disposed together.</summary>
internal sealed class SkiaFramebufferSurface(SkiaRasterSurface surface, GRBackendRenderTarget target) : IRasterSurface
{
    public int Width => surface.Width;
    public int Height => surface.Height;
    public IRasterCanvas Canvas => surface.Canvas;
    public bool ReadPixels(Span<byte> bgraPremul, int width, int height) => surface.ReadPixels(bgraPremul, width, height);

    public void Dispose()
    {
        surface.Dispose();
        target.Dispose();
    }
}
