using Bibui.Draw;
using Bibui.Imaging;

namespace Bibui.Runtime;

/// <summary>
/// A runtime's last frame as pixels: its <see cref="BibuiRuntime.DrawList"/>
/// drawn by the renderer the caller names into a new buffer the size of the
/// frame. For screenshots, tests and tools; a window draws into memory of its
/// own. Take <see cref="BibuiRuntime.FrameLock"/> around it when another thread
/// may build.
/// </summary>
public static class FramePixels
{
    /// <summary>BGRA8888 premultiplied, top row first, the frame's physical size.</summary>
    public static byte[] Bgra(BibuiRuntime runtime, DrawToPixels draw, out int width, out int height)
    {
        width = SurfaceOptions.PhysicalSize(runtime.SurfaceWidth, runtime.ScaleFactor);
        height = SurfaceOptions.PhysicalSize(runtime.SurfaceHeight, runtime.ScaleFactor);
        var bgra = GC.AllocateArray<byte>(width * height * 4, pinned: true);
        draw(runtime.DrawList, new RasterPixels(
            System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), width, height, width * 4));
        return bgra;
    }

    /// <summary>The same pixels, encoded as a PNG.</summary>
    public static byte[] Png(BibuiRuntime runtime, DrawToPixels draw)
    {
        var bgra = Bgra(runtime, draw, out int w, out int h);
        return Imaging.Png.Encode(bgra, w, h, w * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);
    }
}
