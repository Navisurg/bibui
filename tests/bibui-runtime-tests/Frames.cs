using Bibui;
using Bibui.Imaging;
using Bibui.Draw;
using Bibui.Raster.Skia;
using Bibui.Runtime;

/// <summary>A frame's pixels, as a test reads them: BGRA8888 premultiplied, top row first.</summary>
public sealed record Pixels(byte[] RawBgraBytes, int Width, int Height)
{
    public byte[] PngBytes => Png.Encode(RawBgraBytes, Width, Height, Width * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);

    /// <summary><paramref name="list"/> drawn by <paramref name="draw"/> into a new <paramref name="width"/> × <paramref name="height"/> buffer.</summary>
    public static Pixels Of(DrawList list, int width, int height, DrawToPixels draw)
    {
        var bgra = GC.AllocateArray<byte>(width * height * 4, pinned: true);
        draw(list, new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), width, height, width * 4));
        return new Pixels(bgra, width, height);
    }
}

/// <summary>
/// Tests read pixels the way an app gets them: the frame built, then its list
/// drawn by a renderer. Skia on the CPU unless a test names another.
/// </summary>
public static class Frames
{
    public static readonly SkiaRenderer Skia = new();

    /// <summary>Builds the runtime's next frame and draws it with <paramref name="draw"/>, Skia by default.</summary>
    public static Pixels Frame(this BibuiRuntime runtime, DrawToPixels? draw = null)
    {
        runtime.EndBuild();
        return Pixels.Of(runtime.DrawList,
            SurfaceOptions.PhysicalSize(runtime.SurfaceWidth, runtime.ScaleFactor),
            SurfaceOptions.PhysicalSize(runtime.SurfaceHeight, runtime.ScaleFactor),
            draw ?? Skia.Draw);
    }

    /// <summary>The runtime's last frame, drawn again with Skia: what is on screen, without building another.</summary>
    public static Pixels LastFrame(this BibuiRuntime runtime) => Pixels.Of(runtime.DrawList,
        SurfaceOptions.PhysicalSize(runtime.SurfaceWidth, runtime.ScaleFactor),
        SurfaceOptions.PhysicalSize(runtime.SurfaceHeight, runtime.ScaleFactor),
        Skia.Draw);

    /// <summary>Records the laid-out <paramref name="root"/> with <paramref name="renderer"/> and draws it, Skia by default.</summary>
    public static Pixels Frame(this BibuiRenderer renderer, UiNode root, SurfaceOptions surface, Bibui.Theming.ThemeTokens theme,
        DrawToPixels? draw = null)
    {
        renderer.Record(root, surface, theme);
        return Pixels.Of(renderer.DrawList,
            SurfaceOptions.PhysicalSize(surface.Width, surface.ScaleFactor),
            SurfaceOptions.PhysicalSize(surface.Height, surface.ScaleFactor),
            draw ?? Skia.Draw);
    }
}
