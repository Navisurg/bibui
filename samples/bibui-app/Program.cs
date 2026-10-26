// The interactive demo: a counter, a slider-driven split, a name field and a
// checkbox, in a window. The build is App.cs's; this is the loop around it.
//
//   dotnet run --project samples/bibui-app
//   dotnet run --project samples/bibui-app -- --first-frame   # one frame headless, then exit (CI)
//   BIBUI_TEST_PORT=1 dotnet run --project samples/bibui-app  # with a test port (Bibui.Automation)

using Bibui.Automation;
using Bibui.Core;
using Bibui.Core.Draw;
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

// The window and Skia on its GL context, from Bibui.App.SilkSkiaGl.
using var app = App.Open(runtime, new("bibui — interactive demo", 720, 1200));

// A test port, off unless asked for (BIBUI_TEST_PORT=1): a client drives the app
// through its socket. A screenshot is the frame the window shows, copied out as it
// is presented: the path and region asked for wait here until then, and the
// memory it is copied into is kept while the window's size holds.
using var testPort = Environment.GetEnvironmentVariable("BIBUI_TEST_PORT") == "1"
    ? TestPort.Open(runtime, new TestPortOptions { Name = "bibui-app" }) : null;
if (testPort is not null) Console.Error.WriteLine($"Test port: {testPort.SocketPath}");
string? shotPath = null;
RectF? shotRegion = null;
byte[] shot = [];
RasterPixels shotPixels = default;

app.Frame = Frame;
while (app.WaitForFrame())
{
    // A test port's requests: its events join the window's; a screenshot is taken
    // from the frame presented next.
    if (testPort is not null)
        while (testPort.TryPullCmd(out var cmd))
            switch (cmd.Kind)
            {
                case TestCmdKind.Events: app.Events.AddRange(cmd.Events); break;
                case TestCmdKind.Screenshot: (shotPath, shotRegion) = (cmd.Path, cmd.Region); break;
                case TestCmdKind.App: testPort.RefuseCmd("This demo has no commands of its own."); break;
            }
    Frame();
}

void Frame()
{
    runtime.BeginBuild(app.Window.Frame, app.Events);
    Demo.Build(runtime, state);
    runtime.EndBuild();

    // The frame shown, copied out if a screenshot asked for it; written, then answered.
    // Not copied (a resize came between), it is taken from the next frame.
    var (w, h) = (runtime.Window.Width, runtime.Window.Height);
    if (shotPath is not null && (shotPixels.Width != w || shotPixels.Height != h)) shotPixels = FramePixels.Pinned(w, h, out shot);
    if (app.Present(shotPath is null ? null : shotPixels) && shotPath is not null)
    {
        File.WriteAllBytes(shotPath, FramePixels.Png(shot, w, h, runtime.ScaleFactor, shotRegion));
        shotPath = null;
    }
    testPort?.AnswerCmds(settled: shotPath is null);   // no state to report, and only a screenshot to wait for
}
