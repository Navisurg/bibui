namespace Bibui.Core.Draw;

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

    /// <summary>
    /// Where this frame's memory may differ from the frame shown before it, in its
    /// pixels; null for anywhere (the default). Whoever draws sets it — a damage-tracked
    /// renderer bound to this target does so itself — and a platform that can say so
    /// tells the compositor (<c>wl_surface.damage_buffer</c>), then sets it back to null.
    /// A superset is always right; leaving it null is always right.
    /// </summary>
    public IReadOnlyList<PixelRect>? Damage;
}
