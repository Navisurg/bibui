namespace Bibui.Core.Draw;

/// <summary>
/// Pixels the core made on the CPU and a renderer draws as an image
/// (<see cref="DrawOpKind.Pixels"/>): premultiplied ARGB, one <c>uint</c> a
/// pixel (BGRA bytes in memory), rows packed tight. Unlike a texture it needs
/// no renderer to make it, so whatever the core draws itself (a 3D view's
/// rasterised mesh) reaches every renderer the same way.
///
/// <para>The writer bumps <see cref="Version"/> after each change, so damage
/// tracking and a renderer's upload see it. The array is pinned: a renderer may
/// wrap it where it lies.</para>
/// </summary>
public sealed class PixelImage
{
    private static long s_nextId;

    /// <summary>Who this image is, for a renderer's cache: never reused.</summary>
    public long Id { get; } = Interlocked.Increment(ref s_nextId);

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Premultiplied 0xAARRGGBB, <see cref="Width"/> × <see cref="Height"/>.</summary>
    public uint[] Argb { get; private set; } = [];

    /// <summary>Bumped by the writer after each change to the pixels or the size.</summary>
    public uint Version { get; private set; }

    /// <summary>Sizes the image to <paramref name="width"/> × <paramref name="height"/>, keeping the array while it is large enough. The pixels are undefined after a resize.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        if (Argb.Length < width * height) Argb = GC.AllocateArray<uint>(width * height, pinned: true);
        (Width, Height) = (width, height);
        Version++;
    }

    /// <summary>Says the pixels changed.</summary>
    public void Touch() => Version++;

    /// <summary>The pixels, rows tight.</summary>
    public Span<uint> Pixels => Argb.AsSpan(0, Width * Height);
}
