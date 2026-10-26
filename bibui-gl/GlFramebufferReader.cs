using Bibui.Core.Draw;
using Silk.NET.OpenGL;

namespace Bibui.Gl;

/// <summary>
/// What was drawn into a GL target's framebuffer, read back into memory: a frame
/// as shown, for a screenshot. Read after drawing and before the swap: after it,
/// the back buffer's pixels are undefined.
/// <code>
/// var read = new GlFramebufferReader(gl);
/// // in the frame, between the draw and the swap:
/// renderer.Draw(runtime.DrawList);
/// read.Read(pixels);
/// SilkGl.EndDraw(window);
/// </code>
/// <para>A GPU-to-CPU copy, which waits for the GPU to finish the frame: for the
/// frame a screenshot asks for, not every frame. On the thread where the context
/// is current. It moves the read framebuffer binding and the pixel store: a
/// renderer that caches GL state (Skia) resets it before it draws again.</para>
/// </summary>
public sealed class GlFramebufferReader
{
    private readonly GlTarget _target;
    private readonly GL _gl;

    /// <summary>A reader of <paramref name="target"/>'s framebuffer.</summary>
    public GlFramebufferReader(GlTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _gl = GL.GetApi(target.GetProcAddress);
    }

    /// <summary>
    /// Copies the framebuffer into <paramref name="into"/>: BGRA8888 premultiplied,
    /// top row first, as a CPU renderer draws. False, and nothing copied, when
    /// <paramref name="into"/> is not the framebuffer's size.
    /// </summary>
    public unsafe bool Read(RasterPixels into)
    {
        int w = _target.Width, h = _target.Height;
        if (into.Width != w || into.Height != h || w <= 0 || h <= 0) return false;

        // RGBA, which every GL reads back (BGRA is not GL ES's), at the memory's row length.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _target.Framebuffer);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.PixelStore(PixelStoreParameter.PackRowLength, into.RowBytes / 4);
        _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)into.Address);
        _gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _target.Framebuffer);

        // GL's rows run bottom-up and are RGBA; memory's run top-down and are BGRA:
        // swap the rows end for end and red with blue, in one pass.
        byte* first = (byte*)into.Address;
        for (int top = 0, bottom = h - 1; top <= bottom; top++, bottom--)
        {
            byte* a = first + (nint)top * into.RowBytes, b = first + (nint)bottom * into.RowBytes;
            for (int x = 0; x < w * 4; x += 4)
            {
                byte ar = a[x], ag = a[x + 1], ab = a[x + 2], aa = a[x + 3];
                (a[x], a[x + 1], a[x + 2], a[x + 3]) = (b[x + 2], b[x + 1], b[x], b[x + 3]);
                if (a != b) (b[x], b[x + 1], b[x + 2], b[x + 3]) = (ab, ag, ar, aa);
            }
        }
        return true;
    }
}
