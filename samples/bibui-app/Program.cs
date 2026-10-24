// The interactive demo: a counter, a slider-driven split, a name field and a
// checkbox, in a window. The build is App.cs's; this is the loop around it.
//
//   dotnet run --project samples/bibui-app
//   dotnet run --project samples/bibui-app -- --first-frame   # one frame headless, then exit (CI)

using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Raster.Skia;
using BibuiApp;
using App = Bibui.App.BibuiApp;   // the window and renderer; BibuiApp is this sample's namespace

var runtime = BibuiRuntime.Create();
HotReloadListener.Register(runtime.RequestFrame);   // dotnet watch: a code change draws a frame
var state = new DemoState();

// CI's check that the app starts: one frame built and drawn headless, then exit.
if (args.Contains("--first-frame"))
{
    using var cpu = new SkiaCpuRenderer();
    runtime.BeginBuild(new WindowFrame(720, 1200, 1f, 60f));
    Demo.Build(runtime, state);
    runtime.EndBuild();
    FramePixels.Bgra(runtime, cpu.Draw, out _, out _);
    return;
}

// The window and Skia on its GL context, from Bibui.App.SilkSkiaGl. Its test
// server runs only with BIBUI_TEST_SERVER=1 in the environment.
using var app = App.Open(runtime, new("bibui — interactive demo", 720, 1200));
app.Frame = Frame;
while (app.WaitForFrame()) Frame();

void Frame()
{
    runtime.BeginBuild(app.Window.Frame, app.Events);
    Demo.Build(runtime, state);
    runtime.EndBuild();
    app.Present();
}
