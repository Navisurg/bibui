// The README's quick start: a label and a button that counts its clicks.
//
//   dotnet run --project samples/counter                 # Silk: GLFW + OpenGL, drawn with Skia on the GPU
//   dotnet run --project samples/counter -- --wayland    # raw Wayland, drawn with the managed rasterizer
//
// The app owns its loop: the platform waits for input and puts frames on
// screen, the runtime builds and records them, and a renderer draws them. The
// build is the same code on both routes.

using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
HotReloadListener.Register(runtime.RequestFrame);   // dotnet watch: a code change draws a frame
int count = 0;

// The app: what one frame shows, built from the app's own state.
void Build()
{
    var column = runtime.ColumnFit();
    using (runtime.PushParent(column))
    {
        runtime.Text($"Clicked {count} times");
        if (runtime.Button("add", "Add one").Clicked) count++;
    }
}

var options = new PlatformWindowOptions("Counter", 360, 160);

if (args.Contains("--wayland"))
{
    using var window = WaylandWindow.Open(runtime, options);
    using var managed = new ManagedRenderer(window.Cpu);
    while (WaylandWindow.BeginFrame(window, runtime))
    {
        runtime.BeginBuild();
        Build();
        runtime.EndBuild();
        managed.Draw(runtime.DrawList);
        WaylandWindow.EndFrame(window, runtime);
    }
}
else
{
    using var window = SilkWindow.Open(runtime, options);
    using var skia = new SkiaRenderer(window.Gl);
    window.OnRefresh = Frame;   // keeps drawing during a resize on Windows and macOS
    while (SilkWindow.BeginFrame(window, runtime)) Frame();

    void Frame()
    {
        runtime.BeginBuild();
        Build();
        runtime.EndBuild();
        skia.Draw(runtime.DrawList);
        SilkWindow.EndFrame(window, runtime);
    }
}
