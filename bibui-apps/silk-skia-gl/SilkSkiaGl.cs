using Bibui.Core.Draw;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window, drawn by Skia on its GL context: everything on the GPU.
public sealed partial class BibuiApp
{
    /// <summary>Called while the window is being resized, where the OS blocks the loop (Windows, macOS): set it to the loop's body to keep drawing.</summary>
    public Action? OnRefresh { get => Window.OnRefresh; set => Window.OnRefresh = value; }

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options) =>
        SilkWindow.Open(runtime, window);

    private partial SkiaGlRenderer MakeRenderer() => new(Window.Gl);

    private partial bool BeginWindowFrame() => SilkWindow.BeginFrame(Window, Runtime);

    private partial void Present()
    {
        SilkWindow.BeginDraw(Window);
        Renderer.Draw(Runtime.DrawList);
        SilkWindow.EndDraw(Window);
    }

    private partial void EndWindowFrame() => SilkWindow.EndFrame(Window, Runtime);

    private partial void DisposeRenderer()
    {
        Renderer.Dispose();
    }
}
