# Bibui.Platform.Wayland

A window for bibui on raw Wayland: libwayland-client, an xdg-shell toplevel,
frames drawn on the CPU straight into `wl_shm` buffers and paced by the
compositor's frame callbacks. No GLFW, no OpenGL, no SkiaSharp. Linux only.

```csharp
using var window = WaylandWindow.Open(new PlatformWindowOptions("My app", 960, 640),
    new WaylandHostOptions { AppId = "my-app" });
using var managed = new ManagedRenderer(window.Cpu);   // Bibui.Raster.Managed.Backend
```

Most apps take it through `Bibui.App.WaylandManaged` or `WaylandSkiaCpu`,
which run the loop. Written out ([as for any window](https://github.com/Navisurg/bibui/blob/main/docs/app-packages.md#writing-the-loop-yourself)),
the Wayland window paces it: with a frame owed, `GetEvents(wait: false)` waits
for the compositor's frame callback, as vsync would. `window.Frame` is the
buffer's size, the scale and the output's refresh rate.

`window.Cpu` is this frame's `wl_shm` buffer; a renderer bound to it draws
there, so `new SkiaCpuRenderer(window.Cpu)` works too. `WaylandWindow.IsSupported`
says whether this session can open a window. With
`Bibui.Raster.Managed.Backend` this is bibui's Native AOT route. It needs
`libwayland-client` and `libxkbcommon` at run time.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
