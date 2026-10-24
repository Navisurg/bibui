// The interactive demo: a counter, a slider-driven split, a name field and a
// checkbox, in a window. The build is App.cs's; this is the loop around it.
//
//   dotnet run --project samples/bibui-app
//   dotnet run --project samples/bibui-app -- --first-frame   # one frame headless, then exit (CI)

using Bibui.Core;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using BibuiApp;

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

using var window = SilkWindow.Open(SilkGl.Options(new("bibui — interactive demo", 720, 1200)));
runtime.WakeUp = window.Wake;
runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
var gl = SilkGl.Target(window);
using var skia = new SkiaGlRenderer(gl);

// Off unless asked for (BIBUI_TEST_SERVER=1); the loop steps it. Its screenshots
// draw on the CPU, with a renderer of their own beside the window's GPU one.
using var testServer = BibuiTestServer.StartIfEnabled(runtime, draw: new SkiaCpuRenderer().Draw);

window.OnRefresh = Frame;   // draws at a new size, and during a resize on Windows and macOS
while (!window.IsClosing)
{
    // Nothing happening: wait for input. A frame wanted: sleep until it is due,
    // and take the input that came meanwhile.
    bool idle = runtime.FramesRequested == 0 && window.Events.Count == 0;
    if (!idle) runtime.SleepUntilNextFrame();
    var events = window.GetEvents(wait: idle);
    if (runtime.FramesRequested == 0 && events.Count == 0) continue;
    // Taken before the frame is built, so what the build asks for is still owed after it.
    runtime.ConsumeFrame();
    Frame();
}
runtime.WakeUp = null;

void Frame()
{
    // While a test-server command runs, the server is the keyboard and mouse.
    runtime.BeginBuild(window.Frame, testServer?.Step(window.Events) ?? window.Events);
    Demo.Build(runtime, state);
    runtime.EndBuild();
    SilkGl.BeginDraw(window, gl);
    skia.Draw(runtime.DrawList);
    SilkGl.EndDraw(window);
}
