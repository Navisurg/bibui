# Bibui.Platform.Silk

A window for bibui through GLFW and OpenGL (Silk.NET). Frames are drawn by
Skia on the GPU when OpenGL is available and on the CPU otherwise
(`BIBUI_GPU=0` forces the CPU). The clipboard goes through TextCopy.

```csharp
var runtime = new BibuiRuntime(new SkiaRasterBackend());
runtime.Mount(() => /* your frame */);
new BibuiWindow(runtime, new SilkPlatformHost()).Run("My app");
```

This package brings `Bibui.Raster.Skia`. It runs under the JIT; for a Native
AOT app, use `Bibui.Platform.Wayland` with `Bibui.Raster.Managed.Backend`.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
