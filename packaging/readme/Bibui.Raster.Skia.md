# Bibui.Raster.Skia

Bibui's renderers on SkiaSharp. `SkiaGlRenderer` (on the GPU, a window's GL
context) and `SkiaCpuRenderer` (into memory) draw a frame's `DrawList` (clips,
layers, shapes, paths, textures, backdrop blur and text) with Skia. Both have
the same calls as each other and as `ManagedRenderer`.

```csharp
using Bibui.Raster.Skia;

using var skia = new SkiaCpuRenderer();
runtime.EndBuild();
skia.Draw(runtime.DrawList, surface);   // an SKSurface of your own
skia.Draw(runtime.DrawList, pixels);    // or RasterPixels: BGRA8888 premultiplied, yours
```

Bound to a window's target, it draws there: `new SkiaGlRenderer(window.Gl)` on
the GPU, through a `Bibui.Platform.Silk` window's GL framebuffer, or
`new SkiaCpuRenderer(window.Cpu)` into the memory a window presents; then
`skia.Draw(runtime.DrawList)` each frame. `SkiaGlRenderer.ForGl(window.Gl)`
answers null when Skia cannot use the GL context, and the app falls back to
the CPU if it wants to.
`CreateTexture(format, kind, width, height, pixels)`, `UpdateTexture` and
`ReleaseTexture` make the textures a custom draw callback draws: RGBA8, R8,
RG8 or A8, Static or Stream (one texture a video's frames overwrite).

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
