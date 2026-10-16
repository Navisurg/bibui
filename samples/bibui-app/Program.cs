using Bibui.Raster.Skia;
using Bibui.Runtime;
using BibuiApp;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
var skia = new SkiaRenderer();                     // the renderer, named
HotReloadListener.Register(runtime.RequestFrame);

// The whole UI: App.cs builds it, and the runtime tests drive the same build headless.
App.Mount(runtime);

// --first-frame: one frame headless, built and drawn, then exit — CI's check that the app starts.
if (args.Contains("--first-frame"))
{
    runtime.EndBuild();
    FramePixels.Bgra(runtime, skia.Draw, out _, out _);
    return;
}

// Off unless asked for: BIBUI_TEST_SERVER=1.
using var testServer = BibuiTestServer.StartIfEnabled(runtime, draw: skia.Draw);

var window = BibuiWindow.Create(runtime).WithSilk(skia);
window.Run("bibui — interactive demo");
