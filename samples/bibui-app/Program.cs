// The interactive demo: a counter, a slider-driven split, a name field and a
// checkbox, in a window. The build is App.cs's; this is the loop around it.
//
//   dotnet run --project samples/bibui-app
//   dotnet run --project samples/bibui-app -- --first-frame   # one frame headless, then exit (CI)

using Bibui;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using BibuiApp;

var runtime = BibuiRuntime.Create();
runtime.Surface = new SurfaceOptions { Width = 720, Height = 1200 };
HotReloadListener.Register(runtime.RequestFrame);   // dotnet watch: a code change draws a frame
var state = new DemoState();

// CI's check that the app starts: one frame built and drawn headless, then exit.
if (args.Contains("--first-frame"))
{
    using var cpu = new SkiaCpuRenderer();
    runtime.BeginBuild();
    Demo.Build(runtime, state);
    runtime.EndBuild();
    FramePixels.Bgra(runtime, cpu.Draw, out _, out _);
    return;
}

using var window = SilkWindow.Open(runtime, new("bibui — interactive demo", 720, 1200));
using var skia = new SkiaGlRenderer(window.Gl);

// Off unless asked for (BIBUI_TEST_SERVER=1); the loop steps it. Its screenshots
// draw on the CPU, with a renderer of their own beside the window's GPU one.
using var testServer = BibuiTestServer.StartIfEnabled(runtime, draw: new SkiaCpuRenderer().Draw);

window.OnRefresh = Frame;   // keeps drawing during a resize on Windows and macOS
while (SilkWindow.BeginFrame(window, runtime)) Frame();

void Frame()
{
    // While a test-server command runs, the server is the keyboard and mouse.
    runtime.BeginBuild(testServer?.Step(window.Events) ?? window.Events);
    Demo.Build(runtime, state);
    runtime.EndBuild();
    skia.Draw(runtime.DrawList);
    SilkWindow.EndFrame(window, runtime);
}
