# Bibui.Raster.Skia

Bibui's renderer on SkiaSharp. `SkiaRenderer` draws a frame's `DrawList`
(clips, layers, shapes, paths, textures, backdrop blur and text) with Skia on
the CPU or on the GPU.

```csharp
using Bibui.Raster.Skia;

using var skia = new SkiaRenderer();
runtime.EndBuild();
skia.Draw(runtime.DrawList, surface);   // an SKSurface of your own
skia.Draw(runtime.DrawList, pixels);    // or RasterPixels: BGRA8888 premultiplied, yours
```

Bound to a window's target, it draws there: `new SkiaRenderer(window.Gl)` on
the GPU, through a `Bibui.Platform.Silk` window's GL framebuffer, or
`new SkiaRenderer(window.Cpu)` into the memory a window presents; then
`skia.Draw(runtime.DrawList)` each frame. `SkiaRenderer.ForGl(window.Gl)`
answers null when Skia cannot use the GL context, and the app falls back to
the CPU if it wants to.
`CreateTexture`, `UpdateTexture` and `ReleaseTexture` make the textures a
custom draw callback draws.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
