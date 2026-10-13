# Bibui.Platform.Wayland

A window for bibui on raw Wayland: libwayland-client, an xdg-shell toplevel,
frames drawn on the CPU straight into `wl_shm` buffers and paced by the
compositor's frame callbacks. No GLFW, no OpenGL, no SkiaSharp. Linux only.

```csharp
var runtime = new BibuiRuntime(new ManagedRasterBackend());
runtime.Mount(() => /* your frame */);

if (WaylandPlatformHost.IsSupported)
    new BibuiWindow(runtime, new WaylandPlatformHost(new WaylandHostOptions { AppId = "my-app" }))
        .Run("My app");
```

With `Bibui.Raster.Managed.Backend` this is bibui's Native AOT route. It needs
`libwayland-client` and `libxkbcommon` at run time.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
