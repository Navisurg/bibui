# Bibui.Text.FreeType

bibui's text engine on the system FreeType and HarfBuzz: shaping, exact
subpixel measuring, a glyph coverage cache and gamma-correct compositing. It
is the text of `Bibui.Raster.Managed.Backend`, and has no SkiaSharp.

The native code it calls is `libfreetype.so.6` and `libharfbuzz.so.0`, through
source-generated `LibraryImport`, so it is Native AOT compatible. Install
FreeType and HarfBuzz on the machine (`libfreetype6`, `libharfbuzz0b` on
Debian and Ubuntu).

Most apps reference `Bibui.Raster.Managed.Backend` and never use this package
directly.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
