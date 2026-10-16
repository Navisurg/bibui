# Bibui.Raster.Skia

Bibui's renderer on SkiaSharp. `SkiaRenderer` draws a frame's `DrawList`
(clips, layers, shapes, paths, textures, backdrop blur and text) with Skia on
the CPU or, through `Bibui.Platform.Silk`, on the GPU.

```csharp
using Bibui.Raster.Skia;

using var skia = new SkiaRenderer();
runtime.EndBuild();
skia.Draw(runtime.DrawList, surface);   // an SKSurface of your own
skia.Draw(runtime.DrawList, pixels);    // or RasterPixels: BGRA8888 premultiplied, yours
```

`BibuiWindow.Create(runtime).WithSilk(skia)` hands it to a window.
`CreateTexture`, `UpdateTexture` and `ReleaseTexture` make the textures a
custom draw callback draws.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
