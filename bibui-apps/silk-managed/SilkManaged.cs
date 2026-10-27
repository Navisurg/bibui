using Bibui.Core.Draw;
using Bibui.Gl;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window, drawn by the managed rasterizer (no native drawing library) into memory, which GlCpuFramebuffer
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

    private partial ManagedRenderer MakeRenderer()
    {
        _gl = SilkGl.Target(Window);
        _shown = new GlCpuFramebuffer(_gl);
        return new ManagedRenderer(_shown.Cpu);
    }

    private partial void DrawFrame()
    {
        SilkGl.BeginDraw(Window, _gl);
        _shown.BeginDraw();
        Renderer.Draw(Runtime.DrawList);
    }

    private partial bool ReadFrame(RasterPixels into) => _shown.Cpu.Pixels.CopyTo(into);

    private partial void PresentFrame()
    {
        _shown.EndDraw();
        SilkGl.EndDraw(Window);
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
