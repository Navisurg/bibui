using System.Runtime.InteropServices;
using Bibui.Core.Draw;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// Draws a <see cref="DrawList"/> with Skia on the CPU. Bound to memory a
/// platform presents (<see cref="CpuTarget"/>), it draws a frame with
/// <see cref="Draw(DrawList)"/>; bound or not, it draws into memory the caller
/// owns (<see cref="Draw(DrawList, RasterPixels)"/>: screenshots, tests) or onto
/// an <see cref="SKSurface"/> the caller made. Its textures are in CPU memory.
/// <see cref="SkiaGlRenderer"/> is the same drawing on the GPU.
///
/// <para>Not thread-safe: one thread draws at a time.</para>
/// </summary>
public sealed class SkiaCpuRenderer : IDisposable
{
    /// <summary>A renderer bound to nothing: it draws into the memory or the surface each call names.</summary>
    public SkiaCpuRenderer() { }

    /// <summary>
    /// A renderer bound to <paramref name="target"/>, memory a platform presents:
    /// <see cref="Draw(DrawList)"/> draws into whatever memory it holds then.
    /// </summary>
    public SkiaCpuRenderer(CpuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    private readonly CpuTarget? _target;
    private readonly SkiaOps _ops = new();

    // The surface over the memory Draw(list, pixels) was last handed, so a host
    // that draws into the same few buffers makes no surface per frame.
    private SKSurface? _wrapped;
    private RasterPixels _wrappedPixels;

    /// <summary>Draws <paramref name="list"/> into the memory the bound target holds now.</summary>
    public void Draw(DrawList list)
        => Draw(list, (_target ?? throw new InvalidOperationException(
            "This renderer is bound to no target: draw with Draw(list, pixels).")).Pixels);

    /// <summary>
    /// Draws <paramref name="target"/>'s pixels from <paramref name="list"/>:
    /// BGRA8888 premultiplied memory the caller owns, a <c>wl_shm</c> buffer or a
    /// test's array.
    /// </summary>
    public void Draw(DrawList list, RasterPixels target)
    {
        if (_wrapped is null || _wrappedPixels != target)
        {
            _wrapped?.Dispose();
            var info = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _wrapped = SKSurface.Create(info, target.Address, target.RowBytes)
                ?? throw new InvalidOperationException(
                    $"SKSurface.Create returned null over {target.Width}x{target.Height} (row {target.RowBytes} bytes).");
            _wrappedPixels = target;
        }
        Draw(list, _wrapped);
    }

    /// <summary>Draws <paramref name="list"/> onto <paramref name="surface"/>, then ages what is cached: once per frame.</summary>
    public void Draw(DrawList list, SKSurface surface) => _ops.Draw(list, surface);

    // ── Textures ─────────────────────────────────────────────────────────────
    // In CPU memory, made by usage code, which owns them: a draw list only
    // names a handle.

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> texture of
    /// <paramref name="format"/>, from <paramref name="pixels"/> (rows
    /// <paramref name="rowBytes"/> apart, or packed tight when 0; RGBA8 is straight
    /// alpha). A Static one is a copy of the pixels; a Stream one a buffer its
    /// updates overwrite. Empty pixels make a Stream texture of zeros. The caller
    /// owns it: <see cref="ReleaseTexture"/> frees it.
    /// </summary>
    public TextureHandle CreateTexture(TextureFormat format, TextureKind kind, int width, int height,
        ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        var texture = new SkiaTexture { Format = format, Kind = kind, Width = width, Height = height };
        bool blank = pixels.IsEmpty && kind == TextureKind.Stream;
        int stride = blank ? 0 : TexturePixels.RowBytes(format, width, height, pixels, rowBytes);
        if (kind == TextureKind.Static)
        {
            texture.Image = SKImage.FromPixelCopy(SkiaOps.Info(format, width, height), pixels, stride)
                ?? throw new ArgumentException($"Skia took no {width}×{height} {format} image from these pixels.", nameof(pixels));
        }
        else
        {
            texture.Pixels = GC.AllocateArray<byte>(width * height * format.BytesPerPixel(), pinned: true);
            if (!blank) Copy(pixels, stride, texture);
            texture.Image = Wrap(texture);
        }
        return _ops.Textures.Add(texture);
    }

    /// <summary>
    /// Overwrites a Stream texture's pixels, the whole texture, at the size and
    /// format it was made with. A Static texture does not change: its update is
    /// ignored. So is a released handle.
    /// </summary>
    public void UpdateTexture(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        if (_ops.Textures.Get(texture) is not { Kind: TextureKind.Stream } t) return;
        Copy(pixels, TexturePixels.RowBytes(t.Format, t.Width, t.Height, pixels, rowBytes), t);
        // The image over the buffer is made again: Skia takes an image's pixels not
        // to change under it. A wrapper, not a copy.
        t.Image!.Dispose();
        t.Image = Wrap(t);
        _ops.Textures.Touch(texture);
    }

    /// <summary>Frees <paramref name="texture"/>; drawing it afterwards draws nothing.</summary>
    public void ReleaseTexture(TextureHandle texture) => _ops.Textures.Remove(texture)?.Image?.Dispose();

    // A Stream texture's pixels, rows packed tight in the buffer it owns.
    private static void Copy(ReadOnlySpan<byte> pixels, int stride, SkiaTexture into)
    {
        int row = into.Width * into.Format.BytesPerPixel();
        for (int y = 0; y < into.Height; y++)
            pixels.Slice(y * stride, row).CopyTo(into.Pixels.AsSpan(y * row, row));
    }

    private static SKImage Wrap(SkiaTexture t) =>
        SKImage.FromPixels(SkiaOps.Info(t.Format, t.Width, t.Height),
            Marshal.UnsafeAddrOfPinnedArrayElement(t.Pixels!, 0), t.Width * t.Format.BytesPerPixel())
        ?? throw new InvalidOperationException($"Skia took no {t.Format} image over the texture's pixels.");

    public void Dispose()
    {
        for (var node = _ops.Textures.FirstLive; node is not null; node = node.Next) node.Item!.Image?.Dispose();
        _ops.Dispose();
        _wrapped?.Dispose();
        _wrapped = null;
    }
}
