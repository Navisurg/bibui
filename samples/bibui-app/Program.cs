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
    int w = runtime.SurfaceWidth, h = runtime.SurfaceHeight;
    var pixels = GC.AllocateArray<byte>(w * h * 4, pinned: true);
    skia.Draw(runtime.DrawList, new Bibui.Draw.RasterPixels(
        System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0), w, h, w * 4));
    return;
}

// Off unless asked for: BIBUI_TEST_SERVER=1.
using var testServer = BibuiTestServer.StartIfEnabled(runtime, draw: skia.Draw);

var window = BibuiWindow.Create(runtime).WithSilk(skia);
window.Run("bibui — interactive demo");
