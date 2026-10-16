# Bibui.Platform.Wayland

A window for bibui on raw Wayland: libwayland-client, an xdg-shell toplevel,
frames drawn on the CPU straight into `wl_shm` buffers and paced by the
compositor's frame callbacks. No GLFW, no OpenGL, no SkiaSharp. Linux only.

```csharp
var runtime = BibuiRuntime.Create();
var managed = new ManagedRenderer();       // Bibui.Raster.Managed.Backend
runtime.Mount(() => /* your frame */);

if (WaylandPlatformHost.IsSupported)
    BibuiWindow.Create(runtime).WithWayland(managed.Draw, new WaylandHostOptions { AppId = "my-app" })
        .Run("My app");
```

The host draws each frame with the `DrawToPixels` you pass first; any
renderer's `Draw` converts to it, so `skia.Draw` works too. With
`Bibui.Raster.Managed.Backend` this is bibui's Native AOT route. It needs
`libwayland-client` and `libxkbcommon` at run time.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
