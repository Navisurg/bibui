using Bibui.Core.Draw;
using Bibui.Gl;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window, drawn by Skia on its GL context: everything on the GPU.
public sealed partial class BibuiApp
{
    // The window's GL framebuffer, which Renderer draws into.
    private GlTarget _gl = null!;

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options)
    {
        var opened = SilkWindow.Open(SilkGl.Options(window));
        runtime.WakeUp = opened.Wake;
        runtime.WindowClipboard = (opened.GetClipboardText, opened.SetClipboardText);
        return opened;
    }

    private partial SkiaGlRenderer MakeRenderer() => new(_gl = SilkGl.Target(Window));

    public partial void Present()
    {
        SilkGl.BeginDraw(Window, _gl);
        Renderer.Draw(Runtime.DrawList);
        SilkGl.EndDraw(Window);
    }

    private partial void DisposeRenderer()
    {
        // The window goes next: nothing may wake it after.
        if (Runtime.WakeUp == Window.Wake) Runtime.WakeUp = null;
        Renderer.Dispose();
    }
}
