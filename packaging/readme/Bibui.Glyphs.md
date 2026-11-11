# Bibui.Glyphs

Glyphs as coverage masks, for a rasterizer that draws text itself. It renders
the glyphs of a `Bibui.Text` strike (a font at a pixel size) with FreeType, at
four quarter-pixel phases, keeps them in a cache the caller owns, places them in device
pixels, and gives the gamma table to blend them with (`MaskGamma`). Masks are
grayscale, or for subpixel text three samples a pixel. Its
settings and placement rule are those Skia's text resolves to, so the masks
reproduce Skia's glyphs.

A colour face's glyphs (an emoji font's bitmaps, as Noto Color Emoji has, or
COLRv0 layers) are images instead: `GlyphMask.IsColor`, with premultiplied
BGRA in `GlyphMask.Pixels`, a bitmap scaled from the size the font carries to
the strike's, placed on whole pixels. They are drawn as they are, their alpha
scaled by the text colour's, not filled with it.

The managed rasterizer (`Bibui.Raster.Managed.Backend`) draws its text with
it:

```csharp
using Bibui.Glyphs;

var cache = new Dictionary<FontStrike, Dictionary<uint, GlyphMask>>();   // the renderer's, for its life
int n = GlyphMasks.PlacedFromGlyphs(cache, strike, glyphs, positions, originX, baselineY, placed, TextAntialias.Grayscale);
var table = MaskGamma.Table(r, g, b);
for (int i = 0; i < n; i++)
{
    var m = placed[i].Mask;
    if (m.IsColor) canvas.DrawPremultiplied(m.Pixels, m.Width, m.Height, placed[i].X, placed[i].Y, (byte)(argb >> 24));
    else canvas.DrawA8(m.Coverage, m.Width, m.Height, placed[i].X, placed[i].Y, argb, table);
}
```

The cache is confined to the thread of the text engine its strikes come from,
since a miss loads through the strike's FreeType face. Its masks are
immutable and may be read anywhere.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
