using Bibui.Core.Draw;
using Silk.NET.OpenGL;

namespace Bibui.Gl;

/// <summary>
/// Reads what was drawn into a GL target's framebuffer back into memory: between
/// the draw and the swap, after which the back buffer is undefined. It waits for
/// the GPU, so it is for a screenshot's frame, not every frame.
/// </summary>
public sealed class GlFramebufferReader(GlTarget target)
{
    private readonly GL _gl = GL.GetApi(target.GetProcAddress);

    /// <summary>
    /// Copies the framebuffer into <paramref name="into"/> as BGRA8888, top row
    /// first. False, and nothing copied, when it is not the framebuffer's size.
    /// </summary>
    public unsafe bool Read(RasterPixels into)
    {
        int w = target.Width, h = target.Height;
        if (into.Width != w || into.Height != h || w <= 0 || h <= 0) return false;

        // RGBA, which every GL can read back (GL ES cannot read BGRA).
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.Framebuffer);
        _gl.PixelStore(PixelStoreParameter.PackRowLength, into.RowBytes / 4);
        _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)into.Address);
        _gl.PixelStore(PixelStoreParameter.PackRowLength, 0);

        // GL's rows run bottom-up: swap them end for end, and red with blue.
        for (int top = 0, bottom = h - 1; top <= bottom; top++, bottom--)
        {
            byte* a = (byte*)into.Address + (nint)top * into.RowBytes, b = (byte*)into.Address + (nint)bottom * into.RowBytes;
            for (int x = 0; x < w * 4; x += 4)
            {
                (byte ar, byte ag, byte ab, byte aa) = (a[x], a[x + 1], a[x + 2], a[x + 3]);
                (a[x], a[x + 1], a[x + 2], a[x + 3]) = (b[x + 2], b[x + 1], b[x], b[x + 3]);
                if (a != b) (b[x], b[x + 1], b[x + 2], b[x + 3]) = (ab, ag, ar, aa);
            }
        }
        return true;
    }
}
