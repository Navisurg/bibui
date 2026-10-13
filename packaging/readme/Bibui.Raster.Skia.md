# Bibui.Raster.Skia

Bibui's raster backend on SkiaSharp: surfaces, clips, layers, shapes, paths,
images, backdrop blur and text, drawn by Skia on the CPU or, through
`Bibui.Platform.Silk`, on the GPU.

Name it when you create the runtime:

```csharp
var runtime = new BibuiRuntime(new SkiaRasterBackend());
```

Referencing this package also makes Skia the backend the parameterless
`new BibuiRuntime()` and `new BibuiRenderer()` draw with. Naming the backend
is what trimming and Native AOT keep.

Skia-typed extras live here too: `SkiaCustomDraw.Of` draws into a box with an
`SKCanvas`, and `renderer.RenderTo(SKSurface, …)` renders a frame onto a Skia
surface of your own.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
