# Bibui.Platform.Silk

A window for bibui through GLFW (Silk.NET). It knows no runtime and no
graphics API: it opens, takes input, and hands the runtime what it is each
frame (`window.Frame`: its size, scale and the refresh rate of its monitor).
The OpenGL route is `SilkGl`: `SilkGl.Options` opens the window with a GL
context, `SilkGl.Target` is the framebuffer a renderer draws into, between
`SilkGl.BeginDraw` and `EndDraw` (the swap). A renderer on the CPU draws into
memory that `Bibui.Gl`'s `GlCpuFramebuffer` shows on it. The clipboard goes
through TextCopy.

```csharp
using var window = SilkWindow.Open(SilkGl.Options(new PlatformWindowOptions("My app", 960, 640)));
var gl = SilkGl.Target(window);
using var skia = new SkiaGlRenderer(gl);   // Bibui.Raster.Skia
```

Most apps take it through a `Bibui.App` package (`Bibui.App.SilkSkiaGl`,
`SilkSkiaCpu`, `SilkManaged`), which runs the loop. [Writing the loop yourself](https://github.com/Navisurg/bibui/blob/main/docs/app-packages.md#writing-the-loop-yourself) shows the
loop on the window and a renderer directly. It runs under the JIT; for a
Native AOT app, use `Bibui.App.WaylandManaged`.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
