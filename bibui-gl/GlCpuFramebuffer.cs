using System.Runtime.InteropServices;
using Bibui.Draw;
using Silk.NET.OpenGL;

namespace Bibui.Gl;

/// <summary>
/// CPU memory shown on a GL framebuffer: what a renderer that draws on the CPU
/// (<c>ManagedRenderer</c>, <c>SkiaCpuRenderer</c>) draws a GL window's frames
/// into. A GL user like a GPU renderer is, and in its place: the app draws one
/// or the other into the framebuffer each frame, never both.
/// <code>
/// using var shown = new GlCpuFramebuffer(window.Gl);
/// using var managed = new ManagedRenderer(shown.Cpu);
/// while (SilkWindow.BeginFrame(window, runtime))
/// {
///     runtime.BeginBuild(window.Events); Build(); runtime.EndBuild();
///     SilkWindow.BeginDraw(window);
///     shown.BeginDraw();                  // the memory at the framebuffer's size now
///     managed.Draw(runtime.DrawList);
///     shown.EndDraw();                    // upload, blit onto the framebuffer
///     SilkWindow.EndDraw(window);         // swap
///     SilkWindow.EndFrame(window, runtime);
/// }
/// </code>
/// <para>The memory is uploaded into a BGRA8 texture (<see cref="GlTextures"/>)
/// and copied onto the framebuffer with one blit, flipped: memory rows run
/// top-down, GL's bottom-up. On the thread where the context is current.</para>
/// </summary>
public sealed class GlCpuFramebuffer : IDisposable
{
    /// <summary>Memory shown on <paramref name="target"/>'s framebuffer; nothing is made until <see cref="BeginDraw"/>.</summary>
    public GlCpuFramebuffer(GlTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _gl = GL.GetApi(target.GetProcAddress);
    }

    private readonly GlTarget _target;
    private readonly GL _gl;

    /// <summary>
    /// The memory a CPU renderer draws into: BGRA8888 premultiplied, the
    /// framebuffer's size as of the last <see cref="BeginDraw"/>. Its pixels are
    /// pointed at that memory; bind the renderer to it once.
    /// </summary>
    public CpuTarget Cpu { get; } = new();

    // The memory, pinned so a renderer draws into it where it lies; the texture
    // it is uploaded into; the read framebuffer that texture is attached to for
    // the blit. Remade when the framebuffer's size changes.
    private byte[] _memory = [];
    private uint _texture, _readFramebuffer;
    private int _width, _height;

    /// <summary>
    /// Sizes the memory to the framebuffer's size now: call it once a frame,
    /// after the window has taken this frame's resize and before the renderer
    /// draws. The pixels are kept while the size holds.
    /// </summary>
    public void BeginDraw()
    {
        int w = _target.Width, h = _target.Height;
        if (w == _width && h == _height) return;
        if (_texture != 0) GlTextures.Delete(_gl, _texture);
        (_width, _height, _texture) = (w, h, 0);
        _memory = GC.AllocateArray<byte>(Math.Max(1, w * h * 4), pinned: true);
        Cpu.Pixels = new RasterPixels(Marshal.UnsafeAddrOfPinnedArrayElement(_memory, 0), w, h, w * 4);
        if (w <= 0 || h <= 0) return;   // minimised: nothing to show
        _texture = GlTextures.Create(_gl, TextureFormat.BGRA8, w, h);
        if (_readFramebuffer == 0) _readFramebuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFramebuffer);
        _gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _texture, 0);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
    }

    /// <summary>Uploads the memory and copies it onto the framebuffer, for the swap that follows.</summary>
    public void EndDraw()
    {
        if (_texture == 0) return;
        GlTextures.Upload(_gl, _texture, TextureFormat.BGRA8, _width, _height, _memory, _width * 4);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFramebuffer);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _target.Framebuffer);
        _gl.BlitFramebuffer(0, 0, _width, _height, 0, _height, _width, 0,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _target.Framebuffer);
    }

    /// <summary>Deletes the texture and the read framebuffer: before the window's GL context goes.</summary>
    public void Dispose()
    {
        if (_readFramebuffer != 0) _gl.DeleteFramebuffer(_readFramebuffer);
        if (_texture != 0) GlTextures.Delete(_gl, _texture);
        (_readFramebuffer, _texture) = (0, 0);
    }
}
