using Bibui.Raster.Skia;
using Bibui.Runtime;
using BibuiApp;

BibuiRuntime runtime = new(new SkiaRasterBackend());   // the backend, named
HotReloadListener.Register(runtime.RequestFrame);

// The whole UI: App.cs builds it, and the runtime tests drive the same build headless.
App.Mount(runtime);

// --first-frame: one frame headless, then exit — CI's check that the app starts.
if (args.Contains("--first-frame")) { runtime.Render(); return; }

// Off unless asked for: BIBUI_TEST_SERVER=1.
using var testServer = BibuiTestServer.StartIfEnabled(runtime);

var window = new BibuiWindow(runtime, new Bibui.Runtime.Platform.SilkPlatformHost());
window.Run("bibui — interactive demo");
