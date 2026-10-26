using Bibui.Core.Draw;
using Bibui.Gl;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window, drawn by Skia into memory on the CPU, which GlCpuFramebuffer
// shows on the window's GL framebuffer.
public sealed partial class BibuiApp
{
    // The window's GL framebuffer, which _shown draws to.
    private GlTarget _gl = null!;
    private GlCpuFramebuffer _shown = null!;

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options)
    {
        var opened = SilkWindow.Open(SilkGl.Options(window));
        runtime.WakeUp = opened.Wake;
        runtime.WindowClipboard = (opened.GetClipboardText, opened.SetClipboardText);
        return opened;
    }

    private partial SkiaCpuRenderer MakeRenderer()
    {
        _gl = SilkGl.Target(Window);
        _shown = new GlCpuFramebuffer(_gl);
        return new SkiaCpuRenderer(_shown.Cpu);
    }

    public partial bool Present(RasterPixels? into)
    {
        SilkGl.BeginDraw(Window, _gl);
        _shown.BeginDraw();
        Renderer.Draw(Runtime.DrawList);
        bool copied = into is not { } pixels || _shown.Cpu.Pixels.CopyTo(pixels);
        _shown.EndDraw();
        SilkGl.EndDraw(Window);
        return copied;
    }

    // The app's own renderer, so screenshots hold its textures.

    private partial void DisposeRenderer()
    {
        // The window goes next: nothing may wake it after.
        if (Runtime.WakeUp == Window.Wake) Runtime.WakeUp = null;
        Renderer.Dispose();
        _shown.Dispose();
    }
}
