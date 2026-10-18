# Bibui.Gl

Bibui's GL layer: `GlTarget`, the GL framebuffer a platform presents, and
`GlTextures`, the GL texture calls a renderer on that context makes its
textures with. It goes through the `Silk.NET.OpenGL` binding and has no window
and no renderer of its own.

`Bibui.Platform.Silk` makes a `GlTarget` (`window.Gl`), and
`Bibui.Raster.Skia`'s `SkiaGlRenderer` draws into it and makes its textures
with `GlTextures`. An app references the window and the renderer; this comes
with them.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
