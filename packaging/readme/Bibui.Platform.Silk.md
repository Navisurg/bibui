# Bibui.Platform.Silk

A window for bibui through GLFW and OpenGL (Silk.NET). It references no
renderer: `window.Gl` is its GL framebuffer, for a renderer that draws with GL,
and `window.Cpu` is CPU memory it uploads and shows, for one that draws on the
CPU. The clipboard goes through TextCopy.

```csharp
var runtime = BibuiRuntime.Create();
using var window = SilkWindow.Open(runtime, new PlatformWindowOptions("My app", 960, 640));
using var skia = SkiaGlRenderer.ForGl(window.Gl)        // Bibui.Raster.Skia
    ?? new SkiaCpuRenderer(window.Cpu);                  // no usable GL: draw on the CPU
while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    /* your frame */
    runtime.EndBuild();
    skia.Draw(runtime.DrawList);
    SilkWindow.EndFrame(window, runtime);
}
```

Set `window.OnRefresh` to the loop body to keep drawing during a resize on
Windows and macOS. An app drawing with Skia references `Bibui.Raster.Skia`
as well. It runs under the JIT; for a Native
AOT app, use `Bibui.Platform.Wayland` with `Bibui.Raster.Managed.Backend`.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
