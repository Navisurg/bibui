using Bibui.Core.Draw;
using Bibui.Platform.Wayland;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Text;

namespace Bibui.App;

// A native Wayland window, drawn by Skia on the CPU into its shared-memory buffers.
public sealed partial class BibuiApp
{
    // On Linux, the system's colour emoji font is fontconfig's.
    private static partial void SetUpText(Fonts fonts) => Fontconfig.UseColorEmoji(fonts);

    private static partial WaylandWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options)
    {
        var opened = WaylandWindow.Open(window, new WaylandHostOptions { AppId = options.AppId, MinWidth = options.MinWidth, MinHeight = options.MinHeight });
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
