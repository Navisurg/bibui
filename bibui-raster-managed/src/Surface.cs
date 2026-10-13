using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>
/// Premultiplied 32-bit pixels: either memory the surface owns (pinned, so a
/// pointer to it stays valid) or memory the caller owns, such as a
/// <c>wl_shm</c> buffer, which must stay valid and pinned while the surface
/// is used.
/// </summary>
public sealed unsafe class Surface
{
    private readonly uint[]? _owned;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Pixels from one row to the next (bytes / 4).</summary>
    public int RowPixels { get; }

    public PixelOrder Order { get; }

    internal uint* Base { get; }

    /// <summary>A surface of its own, cleared to transparent.</summary>
    public Surface(int width, int height, PixelOrder order = PixelOrder.Bgra8888)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        RowPixels = width;
        Order = order;
        _owned = GC.AllocateArray<uint>(checked(width * height), pinned: true);
        Base = (uint*)Unsafe.AsPointer(ref _owned[0]);
    }

    private Surface(nint address, int width, int height, int rowBytes, PixelOrder order)
    {
        Width = width;
        Height = height;
        RowPixels = rowBytes / 4;
        Order = order;
        Base = (uint*)address;
    }

    /// <summary>
    /// A surface over caller memory. <paramref name="rowBytes"/> must be a
    /// multiple of 4 and at least <c>width * 4</c>.
    /// </summary>
    public static Surface Wrap(nint address, int width, int height, int rowBytes, PixelOrder order = PixelOrder.Bgra8888)
    {
        if (address == 0) throw new ArgumentNullException(nameof(address));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rowBytes < width * 4 || (rowBytes & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(rowBytes), "rowBytes must be a multiple of 4 and at least width * 4.");
        return new Surface(address, width, height, rowBytes, order);
    }

    public IRect Bounds => new(0, 0, Width, Height);

    /// <summary>One row of pixels.</summary>
    public Span<uint> Row(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return new Span<uint>(Base + (long)y * RowPixels, Width);
    }

    /// <summary>The pixel at (x, y) as stored: <c>0xAARRGGBB</c> for BGRA, <c>0xAABBGGRR</c> for RGBA.</summary>
    public uint GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        return Row(y)[x];
    }

    /// <summary>The pixel at (x, y) as premultiplied <c>0xAARRGGBB</c>, whatever the order.</summary>
    public uint GetPixelArgb(int x, int y)
    {
        uint p = GetPixel(x, y);
        return Order == PixelOrder.Rgba8888 ? PixelMath.SwapRB(p) : p;
    }

    /// <summary>
    /// Copies every pixel, tightly packed, into <paramref name="dst"/>
    /// (<c>Width * Height</c> entries), in the surface's own order.
    /// </summary>
    public void CopyTo(Span<uint> dst)
    {
        if (dst.Length < Width * Height) throw new ArgumentException("Too small.", nameof(dst));
        for (int y = 0; y < Height; y++) Row(y).CopyTo(dst.Slice(y * Width, Width));
    }

    /// <summary>
    /// A snapshot of <paramref name="region"/> (backdrop blur): its pixels,
    /// tightly packed (<c>region.Width</c> per row), in the surface's own order.
    /// The region must lie within the surface.
    /// </summary>
    public void ReadPixels(IRect region, Span<uint> dst)
    {
        if (region.IsEmpty) return;
        if (region.Left < 0 || region.Top < 0 || region.Right > Width || region.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(region), "The region must lie within the surface.");
        int w = region.Width;
        if (dst.Length < w * region.Height) throw new ArgumentException("Too small.", nameof(dst));
        for (int y = region.Top; y < region.Bottom; y++)
            new ReadOnlySpan<uint>(Base + (long)y * RowPixels + region.Left, w).CopyTo(dst.Slice((y - region.Top) * w, w));
    }
}

/// <summary>
/// An 8-bit coverage mask over a pixel rect: what a clip is when it is not a
/// plain pixel-aligned rect, and what a shadow's blur works on.
/// </summary>
public sealed unsafe class CoverageMask
{
    private byte[] _data;

    /// <summary>The pixels the mask covers; row r of <see cref="Data"/> is device row Bounds.Top + r.</summary>
    public IRect Bounds { get; private set; }

    /// <summary>Bytes from one row to the next (= Bounds.Width).</summary>
    public int Stride => Bounds.Width;

    /// <summary>The coverage bytes, row-major; only the first Stride·Height are meaningful.</summary>
    public Span<byte> Data => _data.AsSpan(0, Bounds.Width * Bounds.Height);

    internal byte* Ptr { get; private set; }

    internal int Capacity => _data.Length;

    internal CoverageMask(int capacity)
    {
        _data = GC.AllocateArray<byte>(Math.Max(1, capacity), pinned: true);
        Ptr = (byte*)Unsafe.AsPointer(ref _data[0]);
    }

    /// <summary>Re-targets the mask at <paramref name="bounds"/> and zeroes it.</summary>
    internal void Reset(IRect bounds)
    {
        int n = bounds.Width * bounds.Height;
        if (n > _data.Length)
        {
            _data = GC.AllocateArray<byte>(n, pinned: true);
            Ptr = (byte*)Unsafe.AsPointer(ref _data[0]);
        }
        Bounds = bounds;
        new Span<byte>(Ptr, n).Clear();
    }

    /// <summary>A pointer such that <c>Origin[y * Stride + x]</c> is the coverage at device (x, y).</summary>
    internal byte* Origin => Ptr - (long)Bounds.Top * Bounds.Width - Bounds.Left;

    /// <summary>Coverage at device (x, y); 0 outside the bounds.</summary>
    public byte this[int x, int y] => Bounds.Contains(x, y) ? Origin[(long)y * Stride + x] : (byte)0;
}
