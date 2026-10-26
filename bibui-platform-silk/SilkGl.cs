using Bibui.Gl;
using Silk.NET.Windowing;

namespace Bibui.Core.Runtime.Platform;

/// <summary>
/// OpenGL on a <see cref="SilkWindow"/>: the options that open it with a GL
/// context, the <see cref="GlTarget"/> a renderer draws into, and the bracket
/// around each frame's drawing. A renderer on the GPU draws into the target
/// (<c>new SkiaGlRenderer(gl)</c>); a renderer on the CPU draws into memory that
/// <c>Bibui.Gl</c>'s <c>GlCpuFramebuffer</c> shows on it.
/// </summary>
public static class SilkGl
{
    // A renderer may clip with the stencil (Skia's GL backend does).
    private const int StencilBits = 8;

    /// <summary><see cref="SilkWindow.DefaultOptions"/> with a GL 3.3 core context and a stencil buffer.</summary>
    public static WindowOptions Options(PlatformWindowOptions options) => SilkWindow.DefaultOptions(options) with
    {
        API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        PreferredStencilBufferBits = StencilBits,
    };

    /// <summary>
    /// The window's GL framebuffer, FBO 0, on the context <see cref="Options"/>
    /// opened it with; <see cref="BeginDraw"/> keeps its size current. Once per
    /// window, on the thread that opened it.
    /// </summary>
    public static GlTarget Target(SilkWindow window)
    {
        var context = window.Native.GLContext
            ?? throw new InvalidOperationException("The window has no GL context: open it with SilkGl.Options.");
        // VSync = false in the options does not reach the context: on Wayland the
        // swap interval stays EGL's default of 1, Mesa asks for a frame callback on
        // every swap, and the hang DefaultOptions describes happens anyway. Set it
        // here, with the context current.
        context.MakeCurrent();
        context.SwapInterval(0);
        return new GlTarget
        {
            GetProcAddress = name => context.TryGetProcAddress(name, out var at) ? at : 0,
            Framebuffer = 0,
            StencilBits = StencilBits,
            Width = window.FramebufferWidth,
            Height = window.FramebufferHeight,
        };
    }

    /// <summary>
    /// Makes the window's GL context current and <paramref name="gl"/> its current
    /// size, to draw this frame into its framebuffer: after the build, before the
    /// renderer draws. With one window the context already is current; with
    /// several, each draws in its own.
    /// </summary>
    public static void BeginDraw(SilkWindow window, GlTarget gl)
    {
        window.Native.GLContext!.MakeCurrent();
        gl.Width = window.FramebufferWidth;
        gl.Height = window.FramebufferHeight;
    }

    /// <summary>Shows what was drawn into the framebuffer: swaps.</summary>
    public static void EndDraw(SilkWindow window) => window.Native.GLContext!.SwapBuffers();
}
