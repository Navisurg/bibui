# Bibui.Platform.Wayland

A window for bibui on raw Wayland: libwayland-client, an xdg-shell toplevel,
frames drawn on the CPU straight into `wl_shm` buffers and paced by the
compositor's frame callbacks. No GLFW, no OpenGL, no SkiaSharp. Linux only.

```csharp
var runtime = BibuiRuntime.Create();
using var window = WaylandWindow.Open(new PlatformWindowOptions("My app", 960, 640),
    new WaylandHostOptions { AppId = "my-app" });
runtime.WakeUp = window.Wake;
runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
window.OnRefresh = runtime.RequestFrame;               // a configure or a new scale: draw again
using var managed = new ManagedRenderer(window.Cpu);   // Bibui.Raster.Managed.Backend
while (!window.IsClosing)
{
    bool idle = runtime.FramesRequested == 0 && window.Events.Count == 0;
    var events = window.GetEvents(wait: idle);         // without waiting: the compositor's frame callback
    if (runtime.FramesRequested == 0 && events.Count == 0) continue;
    runtime.ConsumeFrame();
    runtime.BeginBuild(window.Frame, events);
    /* your frame */
    runtime.EndBuild();
    WaylandWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    WaylandWindow.EndDraw(window);
}
```

The window knows no runtime. `GetEvents(wait: true)` blocks until something
happens; with a frame owed, `GetEvents(wait: false)` waits for the compositor's
frame callback, which paces the loop as vsync would. `window.Frame` is the
buffer's size, the scale and the output's refresh rate.

`window.Cpu` is this frame's `wl_shm` buffer; a renderer bound to it draws
there, so `new SkiaCpuRenderer(window.Cpu)` works too. `WaylandWindow.IsSupported`
says whether this session can open a window. With
`Bibui.Raster.Managed.Backend` this is bibui's Native AOT route. It needs
`libwayland-client` and `libxkbcommon` at run time.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
