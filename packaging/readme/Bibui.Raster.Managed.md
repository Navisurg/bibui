# Bibui.Raster.Managed

A deterministic CPU rasterizer in managed code: analytic-coverage path fill,
clips, opacity layers, hairlines and blur over premultiplied 32-bit pixels. No
native dependencies, and Native AOT compatible.

It is the core of `Bibui.Raster.Managed.Backend`, which puts bibui's raster
interface over it. Most apps reference that package and never use this one
directly.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
