# Bibui

A code-first native UI library for .NET. Application code describes each frame
through a runtime; a small layout solver sizes and places the whole tree; a
painter draws it through a raster backend. Components (`Button`, `Switch`,
`Slider`, `Dialog`, `Select`, text fields and more) are plain functions that
compose boxes and return what happened. There are no callbacks: a box claims
what it takes, and you ask about it.

This package holds the core, the runtime (`BibuiRuntime`, `BibuiWindow`) and the
components. Add a window host and a raster backend:

| Package | What it adds |
|---|---|
| `Bibui.Platform.Silk` | A window through GLFW and OpenGL (brings `Bibui.Raster.Skia`) |
| `Bibui.Platform.Wayland` | A window on raw Wayland, drawn on the CPU; Native AOT |
| `Bibui.Raster.Skia` | Drawing with SkiaSharp |
| `Bibui.Raster.Managed.Backend` | Drawing with a managed rasterizer and FreeType text, no SkiaSharp |
| `Bibui.Testing` | Headless pages for testing an app |

## Hello

```csharp
using Bibui;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using static Bibui.Arrange;

var runtime = new BibuiRuntime(new SkiaRasterBackend());
int clicks = 0;

runtime.Mount(() =>
{
    var button = Button.Build(runtime, "hello", BibuiStr.FromStrLit("Click me"u8));
    if (button.Clicked) clicks++;

    return Column(
            runtime.Text(System.Text.Encoding.UTF8.GetBytes($"Clicked {clicks} times")),
            Space(12f),
            button.Node)
        .FillX().FillY()
        .Styled(new Style { BackgroundColor = UiColor.Background, Padding = EdgeInsets.All(24f) });
});

new BibuiWindow(runtime, new SilkPlatformHost()).Run("Hello");
```

The first release supports Linux. Documentation, samples and the source are at
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
