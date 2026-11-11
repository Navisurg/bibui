# Bibui.Platform.Silk

A window for bibui through GLFW (Silk.NET). It knows no runtime and no
graphics API: it opens, takes input, and hands the runtime what it is each
frame (`window.Frame`: its size, scale and the refresh rate of its monitor).
The OpenGL route is `SilkGl`: `SilkGl.Options` opens the window with a GL
context, `SilkGl.Target` is the framebuffer a renderer draws into, between
`SilkGl.BeginDraw` and `EndDraw` (the swap). A renderer on the CPU draws into
memory that `Bibui.Gl`'s `GlCpuFramebuffer` shows on it. The clipboard goes
through TextCopy.

Files dropped on the window from another application reach the runtime as a
`WindowEvent.FilesDropped`, for a box claiming `InteractionKinds.Drop`. GLFW
reports only the drop: the paths, and no drag before them, so a target is
never lit while a drag is over it. The drop lands where the pointer was last
reported, which Windows, macOS and X11 move to the drop point. On Wayland GLFW
reports no pointer during a drag, so there the drop has no position and is the
window's, in the frame's events, rather than a box's.

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
