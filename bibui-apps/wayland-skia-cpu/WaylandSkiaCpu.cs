using Bibui.Core.Draw;
using Bibui.Platform.Wayland;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A native Wayland window, drawn by Skia on the CPU into its shared-memory buffers.
public sealed partial class BibuiApp
{
    private static partial WaylandWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options)
    {
        var opened = WaylandWindow.Open(window, new WaylandHostOptions { AppId = options.AppId });
        runtime.WakeUp = opened.Wake;
        runtime.WindowClipboard = (opened.GetClipboardText, opened.SetClipboardText);
        return opened;
    }

    private partial SkiaCpuRenderer MakeRenderer() => new(Window.Cpu);

    public partial void Present()
    {
        WaylandWindow.BeginDraw(Window);
        Renderer.Draw(Runtime.DrawList);
        WaylandWindow.EndDraw(Window);
    }

    // The app's own renderer, so screenshots hold its textures.
    private partial DrawToPixels ScreenshotDraw() => Renderer.Draw;

    private partial void DisposeRenderer()
    {
        // The window goes next: nothing may wake it after.
        if (Runtime.WakeUp == Window.Wake) Runtime.WakeUp = null;
        Renderer.Dispose();
    }
}
