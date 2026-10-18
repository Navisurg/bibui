# Bibui.Platform.Silk

A window for bibui through GLFW and OpenGL (Silk.NET). It references no
renderer: `window.Gl` is its GL framebuffer, and whatever draws a frame draws it
there, between `SilkWindow.BeginDraw` and `EndDraw` (the swap). A renderer on the CPU draws into memory that
`Bibui.Gl`'s `GlCpuFramebuffer` shows on it. The clipboard goes through TextCopy.

```csharp
var runtime = BibuiRuntime.Create();
using var window = SilkWindow.Open(runtime, new PlatformWindowOptions("My app", 960, 640));
using var skia = new SkiaGlRenderer(window.Gl);          // Bibui.Raster.Skia
while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    /* your frame */
    runtime.EndBuild();
    SilkWindow.BeginDraw(window);
    skia.Draw(runtime.DrawList);
    SilkWindow.EndDraw(window);
    SilkWindow.EndFrame(window, runtime);
}
```

Set `window.OnRefresh` to the loop body to keep drawing during a resize on
Windows and macOS. An app drawing with Skia references `Bibui.Raster.Skia`
as well. It runs under the JIT; for a Native
AOT app, use `Bibui.Platform.Wayland` with `Bibui.Raster.Managed.Backend`.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
