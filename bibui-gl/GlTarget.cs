namespace Bibui.Gl;

/// <summary>
/// A GL framebuffer a platform presents: what a renderer bound to it draws into.
/// The platform owns it and keeps it current (the size on a resize); the
/// renderer reads it on every draw, on the loop's thread. What
/// <see cref="Bibui.Core.Draw.RasterPixels"/> is to CPU memory.
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
