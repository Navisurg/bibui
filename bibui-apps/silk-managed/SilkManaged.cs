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
    private GlCpuFramebuffer _shown = null!;

    /// <summary>Called while the window is being resized, where the OS blocks the loop (Windows, macOS): set it to the loop's body to keep drawing.</summary>
    public Action? OnRefresh { get => Window.OnRefresh; set => Window.OnRefresh = value; }

    private static partial SilkWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options) =>
        SilkWindow.Open(runtime, window);

    private partial ManagedRenderer MakeRenderer()
    {
        _shown = new GlCpuFramebuffer(Window.Gl);
        return new ManagedRenderer(_shown.Cpu);
    }

    private partial bool BeginWindowFrame() => SilkWindow.BeginFrame(Window, Runtime);

    private partial void Present()
    {
        SilkWindow.BeginDraw(Window);
        _shown.BeginDraw();
        Renderer.Draw(Runtime.DrawList);
        _shown.EndDraw();
        SilkWindow.EndDraw(Window);
    }

    private partial void EndWindowFrame() => SilkWindow.EndFrame(Window, Runtime);

    // The app's own renderer, so screenshots hold its textures.

    private partial void DisposeRenderer()
    {
        Renderer.Dispose();
        _shown.Dispose();
    }
}
