using Bibui.Core.Draw;
using Bibui.Platform.Wayland;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A native Wayland window, drawn by Skia on the CPU into its shared-memory buffers.
public sealed partial class BibuiApp
{
    /// <summary>Called while the window is being resized, where the OS blocks the loop. A Wayland window never blocks it, so this is never called.</summary>
    public Action? OnRefresh { get; set; }

    private static partial WaylandWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options) =>
        WaylandWindow.Open(runtime, window, new WaylandHostOptions { AppId = options.AppId });

    private partial SkiaCpuRenderer MakeRenderer() => new(Window.Cpu);

    private partial bool BeginWindowFrame() => WaylandWindow.BeginFrame(Window, Runtime);

    private partial void Present()
    {
        WaylandWindow.BeginDraw(Window);
        Renderer.Draw(Runtime.DrawList);
        WaylandWindow.EndDraw(Window);
    }

    private partial void EndWindowFrame() => WaylandWindow.EndFrame(Window, Runtime);

    // The app's own renderer, so screenshots hold its textures.

    private partial void DisposeRenderer() => Renderer.Dispose();
}
