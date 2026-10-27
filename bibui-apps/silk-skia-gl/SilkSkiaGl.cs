using Bibui.Core.Draw;
using Bibui.Gl;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window, drawn by Skia on its GL context: everything on the GPU.
public sealed partial class BibuiApp
{
    // The window's GL framebuffer, which Renderer draws into, and what reads a frame back
    // out of it: made the first time a frame is read.
    private GlTarget _gl = null!;
    private GlFramebufferReader? _read;

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options)
    {
        var opened = SilkWindow.Open(SilkGl.Options(window));
        runtime.WakeUp = opened.Wake;
        runtime.WindowClipboard = (opened.GetClipboardText, opened.SetClipboardText);
        return opened;
    }

    private partial SkiaGlRenderer MakeRenderer() => new(_gl = SilkGl.Target(Window));

    private partial void DrawFrame()
    {
        SilkGl.BeginDraw(Window, _gl);
        Renderer.Draw(Runtime.DrawList);
    }

    // Before the swap: after it the back buffer's pixels are undefined.
    private partial bool ReadFrame(RasterPixels into) => (_read ??= new GlFramebufferReader(_gl)).Read(into);

    private partial void PresentFrame() => SilkGl.EndDraw(Window);

    private partial void DisposeRenderer()
    {
        // The window goes next: nothing may wake it after.
        if (Runtime.WakeUp == Window.Wake) Runtime.WakeUp = null;
        Renderer.Dispose();
    }
}
