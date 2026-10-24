# Bibui.Platform.Silk

A window for bibui through GLFW (Silk.NET). It knows no runtime and no
graphics API: it opens, takes input, and hands the runtime what it is each
frame (`window.Frame`: its size, scale and the refresh rate of its monitor).
The OpenGL route is `SilkGl`: `SilkGl.Options` opens the window with a GL
context, `SilkGl.Target` is the framebuffer a renderer draws into, between
`SilkGl.BeginDraw` and `EndDraw` (the swap). A renderer on the CPU draws into
memory that `Bibui.Gl`'s `GlCpuFramebuffer` shows on it. The clipboard goes
through TextCopy.

```csharp
var runtime = BibuiRuntime.Create();
using var window = SilkWindow.Open(SilkGl.Options(new PlatformWindowOptions("My app", 960, 640)));
runtime.WakeUp = window.Wake;
runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
var gl = SilkGl.Target(window);
using var skia = new SkiaGlRenderer(gl);                 // Bibui.Raster.Skia
while (!window.IsClosing)
{
    bool idle = runtime.FramesRequested == 0 && window.Events.Count == 0;
    if (!idle) runtime.SleepUntilNextFrame();
    var events = window.GetEvents(wait: idle);
    if (runtime.FramesRequested == 0 && events.Count == 0) continue;
    runtime.ConsumeFrame();
    runtime.BeginBuild(window.Frame, events);
    /* your frame */
    runtime.EndBuild();
    SilkGl.BeginDraw(window, gl);
    skia.Draw(runtime.DrawList);
    SilkGl.EndDraw(window);
}
```

`GetEvents(wait: true)` blocks until something happens, so an idle app uses no
CPU; with a frame owed, the runtime sleeps until it is due. Set
`window.OnRefresh` to the frame (build and draw) to draw at a new size and keep
drawing during a resize on Windows and macOS. An app drawing with Skia references `Bibui.Raster.Skia`
as well. It runs under the JIT; for a Native
AOT app, use `Bibui.Platform.Wayland` with `Bibui.Raster.Managed.Backend`.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
