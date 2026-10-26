using Bibui.Core.Draw;
using Bibui.Core.Imaging;

namespace Bibui.Core.Runtime;

/// <summary>
/// A frame as pixels, for screenshots, tests and tools. A window app gets the
/// frame it shows from <c>BibuiApp.Present(into)</c>, into memory from
/// <see cref="Pinned"/>, and encodes it with <see cref="Png(ReadOnlySpan{byte}, int, int, float, RectF?)"/>.
/// Without a window, the runtime's last <see cref="BibuiRuntime.DrawList"/> is
/// drawn by a renderer the caller names (<see cref="Bgra"/>). Between frames, on
/// the loop's thread.
/// </summary>
public static class FramePixels
{
    /// <summary>
    /// Pinned memory for a <paramref name="width"/> × <paramref name="height"/>
    /// frame, BGRA8888, and the <see cref="RasterPixels"/> over it: what a renderer
    /// draws, or a window app copies, a frame into. Pinned for good, so keep it and
    /// reuse it while the size holds.
    /// </summary>
    public static RasterPixels Pinned(int width, int height, out byte[] bgra)
    {
        bgra = GC.AllocateArray<byte>(Math.Max(1, width * height * 4), pinned: true);
        return new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), width, height, width * 4);
    }

    /// <summary>
    /// The runtime's last frame drawn by <paramref name="draw"/> into a new buffer:
    /// BGRA8888 premultiplied, top row first, the frame's physical size.
    /// </summary>
    public static byte[] Bgra(BibuiRuntime runtime, DrawToPixels draw, out int width, out int height)
    {
        (width, height) = (runtime.Window.Width, runtime.Window.Height);
        draw(runtime.DrawList, Pinned(width, height, out var bgra));
        return bgra;
    }

    /// <summary>The same pixels, encoded as a PNG, as <see cref="Png(ReadOnlySpan{byte}, int, int, float, RectF?)"/> does.</summary>
    public static byte[] Png(BibuiRuntime runtime, DrawToPixels draw, RectF? region = null)
    {
        var bgra = Bgra(runtime, draw, out int w, out int h);
        return Png(bgra, w, h, runtime.ScaleFactor, region);
    }

    /// <summary>
    /// A frame's pixels (BGRA8888 premultiplied, top row first, rows packed) encoded
    /// as a PNG: the whole frame, or with <paramref name="region"/> only that part of
    /// it, a rect in logical pixels (a node's, from the tree) cut to the frame;
    /// <paramref name="scale"/> is physical pixels per logical one.
    /// </summary>
    public static byte[] Png(ReadOnlySpan<byte> bgra, int width, int height, float scale, RectF? region = null)
    {
        int w = width, h = height;
        if (region is not { } r) return Imaging.Png.Encode(bgra, w, h, w * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);

        // The region in physical pixels, whole pixels covering it, inside the frame.
        int left = Math.Clamp((int)MathF.Floor(r.Left * scale), 0, w), top = Math.Clamp((int)MathF.Floor(r.Top * scale), 0, h);
        int right = Math.Clamp((int)MathF.Ceiling(r.Right * scale), left, w), bottom = Math.Clamp((int)MathF.Ceiling(r.Bottom * scale), top, h);
        int cw = Math.Max(1, right - left), ch = Math.Max(1, bottom - top);
        if (left + cw > w) left = w - cw;
        if (top + ch > h) top = h - ch;
        var cut = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            bgra.Slice(((top + y) * w + left) * 4, cw * 4).CopyTo(cut.AsSpan(y * cw * 4));
        return Imaging.Png.Encode(cut, cw, ch, cw * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);
    }
}
