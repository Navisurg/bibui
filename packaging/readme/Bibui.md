# Bibui

A code-first native UI library for .NET. Application code describes each frame
through a runtime; a small layout solver sizes and places the whole tree; a
painter records it into a draw list, which a renderer draws. Components
(`Button`, `Switch`, `Slider`, `Dialog`, `Select`, text fields and more) are
plain functions that compose boxes and return what happened.

This package is all of bibui in one reference. It holds no code of its own;
its dependencies are:

| Package | What it adds |
|---|---|
| `Bibui.App.SilkSkiaGl` | `BibuiApp`: a Silk window drawn by Skia on the GPU, and the frame loop in two calls |
| `Bibui.Core` | the core, the runtime (`BibuiRuntime`) and the components |
| `Bibui.Platform.Silk` | a window through GLFW and OpenGL |
| `Bibui.Platform.Wayland` | a window on raw Wayland, drawn on the CPU; Native AOT |
| `Bibui.Platform.Browser` | a `<canvas>` in a .NET WebAssembly app |
| `Bibui.Raster.Skia`, `Bibui.Gl` | drawing with SkiaSharp, on the GPU or the CPU |
| `Bibui.Raster.Managed.Backend` | drawing with a managed rasterizer and FreeType text, no SkiaSharp |
| `Bibui.Testing` | headless pages for testing an app |

## Hello

```csharp
using Bibui.App;
using Bibui.Core.Runtime;

var runtime = BibuiRuntime.Create();
using var app = BibuiApp.Open(runtime, new("Counter", 360, 160));
int count = 0;

while (app.BeginFrame())
{
    runtime.BeginBuild(app.Events);
    using (runtime.PushParent(runtime.ColumnFit()))
    {
        runtime.Text($"Clicked {count} times");
        if (runtime.Button("add", "Add one").Clicked) count++;
    }
    runtime.EndBuild();
    app.EndFrame();
}
```

## Fewer dependencies

`BibuiApp` comes from one `Bibui.App` package, and an app references only one
of them (each defines `BibuiApp`). For an app on another window or renderer,
or one that ships only what it uses, reference that `Bibui.App` package
instead of `Bibui`: `Bibui.App.WaylandManaged` (raw Wayland and the managed
rasterizer: no third-party NuGet dependencies, and Native AOT), for example.
The `Bibui.App` packages are described at
<https://github.com/Navisurg/bibui/blob/main/docs/app-packages.md>.

The first release supports Linux. Documentation, samples and the source are at
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
