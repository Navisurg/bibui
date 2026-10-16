# Bibui.Raster.Managed.Backend

bibui's raster backend with no SkiaSharp: shapes, clips, layers, images and PNG
through the managed rasterizer (`Bibui.Raster.Managed`), and text drawn from
the glyph masks of bibui's own FreeType and HarfBuzz engine.

```csharp
var runtime = BibuiRuntime.Create().WithManagedRasterizer();
```

With `Bibui.Platform.Wayland` this is bibui's Native AOT route: an app that
ships no libSkiaSharp. It needs FreeType and HarfBuzz on the machine.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
