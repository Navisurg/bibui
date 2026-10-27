// The interactive demo: a counter, a slider-driven split, a name field and a
// checkbox, in a window. The build is App.cs's; this is the loop around it.
//
//   dotnet run --project samples/bibui-app
//   dotnet run --project samples/bibui-app -- --first-frame   # one frame headless, then exit (CI)
//   BIBUI_TEST_PORT=1 dotnet run --project samples/bibui-app  # with a test port (Bibui.Automation)

using Bibui.Automation;
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
// through its socket. Its screenshots are drawn on the CPU, with a renderer of
// their own beside the window's GPU one.
using var testPort = Environment.GetEnvironmentVariable("BIBUI_TEST_PORT") == "1"
    ? TestPort.Open(runtime, new TestPortOptions { Name = "bibui-app" }) : null;
using var shots = testPort is null ? null : new SkiaCpuRenderer();
if (testPort is not null) Console.Error.WriteLine($"Test port: {testPort.SocketPath}");

app.Frame = Frame;
while (app.WaitForFrame())
{
    // A test port's requests: its events join the window's; the frame on screen is
    // still the runtime's draw list here, so a screenshot is drawn from it.
    if (testPort is not null)
        while (testPort.TryPullCmd(out var cmd))
            switch (cmd.Kind)
            {
                case TestCmdKind.Events: app.Events.AddRange(cmd.Events); break;
                case TestCmdKind.Screenshot: File.WriteAllBytes(cmd.Path!, FramePixels.Png(runtime, shots!.Draw, cmd.Region)); break;
                case TestCmdKind.App: testPort.RefuseCmd("This demo has no commands of its own."); break;
            }
    Frame();
}

void Frame()
{
    runtime.BeginBuild(app.Window.Frame, app.Events);
    Demo.Build(runtime, state);
    runtime.EndBuild();
    testPort?.AnswerCmds();   // no state to report, and nothing pending to wait for
    app.Present();
}
