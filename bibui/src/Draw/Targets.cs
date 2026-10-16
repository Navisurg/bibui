namespace Bibui.Draw;

/// <summary>
/// A GL framebuffer a platform presents: what a renderer bound to it draws into.
/// The platform owns it and keeps it current (the size on a resize); the
/// renderer reads it on every draw, on the loop's thread. What
/// <see cref="RasterPixels"/> is to CPU memory.
/// </summary>
public sealed class GlTarget
{
    /// <summary>The GL context's entry points by name. Fixed for the window's life; the platform's.</summary>
    public Func<string, nint> GetProcAddress = null!;

    /// <summary>The framebuffer drawn into: 0, the window's default. The platform's.</summary>
    public uint Framebuffer;

    /// <summary>The framebuffer's size in physical px, current from the last resize. The platform's.</summary>
    public int Width, Height;

    /// <summary>Stencil bits the framebuffer was made with: a renderer that clips with the stencil needs them.</summary>
    public int StencilBits;
}

/// <summary>
/// CPU memory a platform presents: a <c>wl_shm</c> buffer, or the memory a GL
/// window uploads and shows. The platform owns it and points it at this frame's
/// memory before the frame is drawn; a renderer bound to it reads it on every
/// draw, on the loop's thread.
/// </summary>
public sealed class CpuTarget
{
    /// <summary>This frame's memory: BGRA8888 premultiplied. The platform's.</summary>
    public RasterPixels Pixels;
}
