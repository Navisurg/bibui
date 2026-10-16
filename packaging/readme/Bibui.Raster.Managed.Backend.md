# Bibui.Raster.Managed.Backend

bibui's renderer with no SkiaSharp: `ManagedRenderer` draws a frame's
`DrawList` (shapes, clips, layers, textures) through the managed rasterizer
(`Bibui.Raster.Managed`), and text from the glyph masks of bibui's own FreeType
and HarfBuzz engine.

```csharp
using Bibui.Raster.Managed.Backend;

var managed = new ManagedRenderer();
runtime.EndBuild();
managed.Draw(runtime.DrawList, pixels);   // RasterPixels: BGRA8888 premultiplied, yours
```

A host takes `managed.Draw` as its `DrawToPixels`. `DamageTracking` (or
`BIBUI_DAMAGE=1`) redraws only what changed since the last list onto a buffer
that still holds an earlier frame; `LastRepaint` says what was redrawn.
`CreateTexture`, `UpdateTexture` and `ReleaseTexture` make the textures a
custom draw callback draws.

With `Bibui.Platform.Wayland` this is bibui's Native AOT route: an app that
ships no libSkiaSharp. It needs FreeType and HarfBuzz on the machine.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
