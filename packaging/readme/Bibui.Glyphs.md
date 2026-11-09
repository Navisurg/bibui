# Bibui.Glyphs

Glyphs as coverage masks, for a rasterizer that draws text itself. It renders
the glyphs of a `Bibui.Text` strike (a font at a pixel size) with FreeType, at
four quarter-pixel phases, keeps them in a `GlyphCache`, places them in device
pixels, and gives the gamma table to blend them with (`MaskGamma`). Its
settings and placement rule are those Skia's text resolves to, so the masks
reproduce Skia's glyphs.

The managed rasterizer (`Bibui.Raster.Managed.Backend`) draws its text with
it:

```csharp
using Bibui.Glyphs;

var cache = new GlyphCache();   // the renderer's, for its life
int n = GlyphMasks.PlacedFromGlyphs(cache, strike, glyphs, positions, originX, baselineY, placed);
var table = MaskGamma.Table(r, g, b);
for (int i = 0; i < n; i++)
    canvas.DrawA8(placed[i].Mask.Coverage, placed[i].Mask.Width, placed[i].Mask.Height,
        placed[i].X, placed[i].Y, argb, table);
```

A cache is confined to the thread of the text engine its strikes come from,
since a miss loads through the strike's FreeType face. Its masks are
immutable and may be read anywhere.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
