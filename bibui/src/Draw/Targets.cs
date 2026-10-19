namespace Bibui.Draw;

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
