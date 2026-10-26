using Bibui.Core.Draw;
using Bibui.Core.Imaging;

namespace Bibui.Core.Runtime;

/// <summary>
/// A runtime's last frame as pixels: its <see cref="BibuiRuntime.DrawList"/>
/// drawn by the renderer the caller names into a new buffer the size of the
/// frame. For screenshots, tests and tools; a window draws into memory of its
/// own. Between frames, on the loop's thread.
/// </summary>
public static class FramePixels
{
    /// <summary>BGRA8888 premultiplied, top row first, the frame's physical size.</summary>
    public static byte[] Bgra(BibuiRuntime runtime, DrawToPixels draw, out int width, out int height)
    {
        (width, height) = (runtime.Window.Width, runtime.Window.Height);
        var bgra = GC.AllocateArray<byte>(width * height * 4, pinned: true);
        draw(runtime.DrawList, new RasterPixels(
            System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), width, height, width * 4));
        return bgra;
    }

    /// <summary>
    /// The same pixels, encoded as a PNG: the whole frame, or with
    /// <paramref name="region"/> only that part of it, a rect in logical pixels
    /// (a node's, from the tree) cut to the frame.
    /// </summary>
    public static byte[] Png(BibuiRuntime runtime, DrawToPixels draw, RectF? region = null)
    {
        var bgra = Bgra(runtime, draw, out int w, out int h);
        if (region is not { } r) return Imaging.Png.Encode(bgra, w, h, w * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);

        // The region in physical pixels, whole pixels covering it, inside the frame.
        float scale = runtime.ScaleFactor;
        int left = Math.Clamp((int)MathF.Floor(r.Left * scale), 0, w), top = Math.Clamp((int)MathF.Floor(r.Top * scale), 0, h);
        int right = Math.Clamp((int)MathF.Ceiling(r.Right * scale), left, w), bottom = Math.Clamp((int)MathF.Ceiling(r.Bottom * scale), top, h);
        int cw = Math.Max(1, right - left), ch = Math.Max(1, bottom - top);
        if (left + cw > w) left = w - cw;
        if (top + ch > h) top = h - ch;
        var cut = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            bgra.AsSpan(((top + y) * w + left) * 4, cw * 4).CopyTo(cut.AsSpan(y * cw * 4));
        return Imaging.Png.Encode(cut, cw, ch, cw * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);
    }
}
