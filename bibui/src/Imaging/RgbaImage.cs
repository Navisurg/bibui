using System.Buffers;

namespace Bibui.Imaging;

/// <summary>Whether an image's colour channels are already multiplied by its alpha.</summary>
public enum AlphaMode
{
    /// <summary>Colour is independent of alpha, as PNG stores it. What <see cref="Png.Decode(ReadOnlySpan{byte})"/> returns.</summary>
    Straight,

    /// <summary>Colour is multiplied by alpha, as a rasterizer's surface holds it.</summary>
    Premultiplied,
}

/// <summary>The byte order of a four-byte pixel in memory someone else laid out.</summary>
public enum ChannelOrder
{
    /// <summary>R, G, B, A: <see cref="RgbaImage"/>'s order, and PNG's.</summary>
    Rgba,

    /// <summary>B, G, R, A: what a CPU renderer's frames (<c>RasterPixels</c>) hold on little-endian machines.</summary>
    Bgra,
}

/// <summary>
/// A plain 8-bit RGBA picture: four bytes per pixel, always in the order
/// R, G, B, A, rows <see cref="Stride"/> bytes apart. Nothing about it depends on
/// which backend drew it.
///
/// <para>
/// The pixels are a <see cref="Memory{T}"/>, so an image can be a view of memory
/// it does not own: a managed rasterizer's buffer (<see cref="RgbaImage(Memory{byte}, int, int, int, AlphaMode)"/>),
/// a native Skia pixmap (<see cref="Wrap"/>), or a rectangle of another image
/// (<see cref="View"/>). None of these copy. A Skia surface wraps without a swizzle
/// only if it was made with <c>SKColorType.Rgba8888</c>; Skia's platform default on
/// Linux is BGRA.
/// </para>
///
/// <para>
/// <see cref="Alpha"/> records whether colour is premultiplied. Decoding gives
/// straight colour, because that is what PNG stores and it loses nothing; a
/// rasterizer's surface is premultiplied, and <see cref="Png.Encode(RgbaImage)"/>
/// divides it out. For an opaque image the two are the same bytes.
/// </para>
/// </summary>
public sealed class RgbaImage
{
    public const int BytesPerPixel = 4;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes from the start of one row to the start of the next; at least <c>Width * 4</c>.</summary>
    public int Stride { get; }

    public AlphaMode Alpha { get; }

    /// <summary>The pixel memory, starting at the first byte of row 0.</summary>
    public Memory<byte> Memory { get; }

    /// <summary>The pixel memory as a span. Rows are <see cref="Stride"/> apart; use <see cref="Row"/> to skip the padding.</summary>
    public Span<byte> Pixels => Memory.Span;

    /// <summary>A new, packed, zeroed (transparent black) image.</summary>
    public RgbaImage(int width, int height, AlphaMode alpha = AlphaMode.Straight)
        : this(new byte[PackedLength(width, height)], width, height, width * BytesPerPixel, alpha)
    {
    }

    /// <summary>Wraps existing memory without copying it.</summary>
    public RgbaImage(Memory<byte> pixels, int width, int height, int stride, AlphaMode alpha = AlphaMode.Straight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (stride < (long)width * BytesPerPixel)
            throw new ArgumentOutOfRangeException(nameof(stride), $"Stride {stride} is shorter than a row of {width} pixels.");
        long needed = height == 0 ? 0 : (long)(height - 1) * stride + (long)width * BytesPerPixel;
        if (pixels.Length < needed)
            throw new ArgumentException($"{pixels.Length} bytes cannot hold {width}x{height} at stride {stride} ({needed} needed).", nameof(pixels));

        Width = width;
        Height = height;
        Stride = stride;
        Alpha = alpha;
        Memory = pixels;
    }

    /// <summary>
    /// Wraps native memory, such as <c>SKPixmap.GetPixels()</c>, without copying.
    /// The caller keeps that memory alive and unmoved for as long as the image is used.
    /// </summary>
    public static RgbaImage Wrap(IntPtr pixels, int width, int height, int stride, AlphaMode alpha)
    {
        if (pixels == IntPtr.Zero) throw new ArgumentNullException(nameof(pixels));
        long length = height == 0 ? 0 : (long)(height - 1) * stride + (long)width * BytesPerPixel;
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(height), "Image is larger than 2 GiB.");
        return new RgbaImage(new NativeMemoryManager(pixels, (int)length).Memory, width, height, stride, alpha);
    }

    /// <summary>The <see cref="Width"/>×4 bytes of row <paramref name="y"/>, without the stride padding.</summary>
    public Span<byte> Row(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return Memory.Span.Slice(y * Stride, Width * BytesPerPixel);
    }

    /// <summary>A rectangle of this image, sharing its memory: writes through either are seen by both.</summary>
    public RgbaImage View(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"{width}x{height} at ({x},{y}) is outside {Width}x{Height}.");
        if (width == 0 || height == 0)
            return new RgbaImage(Memory<byte>.Empty, width, height, width * BytesPerPixel, Alpha);
        int start = y * Stride + x * BytesPerPixel;
        int length = (height - 1) * Stride + width * BytesPerPixel;
        return new RgbaImage(Memory.Slice(start, length), width, height, Stride, Alpha);
    }

    /// <summary>A packed copy that owns its pixels.</summary>
    public RgbaImage Clone()
    {
        var copy = new RgbaImage(Width, Height, Alpha);
        for (int y = 0; y < Height; y++)
            Row(y).CopyTo(copy.Row(y));
        return copy;
    }

    private static int PackedLength(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        long length = (long)width * height * BytesPerPixel;
        if (length > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height} is larger than an array can hold.");
        return (int)length;
    }

    private sealed unsafe class NativeMemoryManager(IntPtr pointer, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new((void*)pointer, length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle((byte*)pointer + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
