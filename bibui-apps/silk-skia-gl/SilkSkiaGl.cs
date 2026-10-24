using Bibui.Core.Draw;
using Bibui.Gl;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

// A Silk window, drawn by Skia on its GL context: everything on the GPU.
public sealed partial class BibuiApp
{
    // Test-server screenshots: GL draws into the window, not into memory, so a
    // CPU renderer of its own draws them. Textures made on Renderer are not in it.
    private SkiaCpuRenderer? _screenshots;

    // The window's GL framebuffer, which Renderer draws into.
    private GlTarget _gl = null!;

    /// <summary>Called while the window is being resized, where the OS blocks the loop (Windows, macOS): set it to the loop's body to keep drawing.</summary>
    public Action? OnRefresh { get => Window.OnRefresh; set => Window.OnRefresh = value; }

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options) =>
        SilkWindow.Open(runtime, SilkGl.Options(window));

    private partial SkiaGlRenderer MakeRenderer() => new(_gl = SilkGl.Target(Window));

    private partial bool BeginWindowFrame() => SilkWindow.BeginFrame(Window, Runtime);

    private partial void Present()
    {
        SilkGl.BeginDraw(Window, _gl);
        Renderer.Draw(Runtime.DrawList);
        SilkGl.EndDraw(Window);
    }

    private partial void EndWindowFrame() => SilkWindow.EndFrame(Window, Runtime);

    private partial DrawToPixels ScreenshotDraw() => (_screenshots ??= new SkiaCpuRenderer()).Draw;

    private partial void DisposeRenderer()
    {
        Renderer.Dispose();
        _screenshots?.Dispose();
    }
}
