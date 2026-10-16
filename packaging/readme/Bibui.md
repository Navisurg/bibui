# Bibui

A code-first native UI library for .NET. Application code describes each frame
through a runtime; a small layout solver sizes and places the whole tree; a
painter records it into a draw list, which a renderer you choose draws. Components (`Button`, `Switch`,
`Slider`, `Dialog`, `Select`, text fields and more) are plain functions that
compose boxes and return what happened. There are no callbacks: a box claims
what it takes, and you ask about it.

This package holds the core, the runtime (`BibuiRuntime`) and the components.
Add a platform window and a renderer:

| Package | What it adds |
|---|---|
| `Bibui.Platform.Silk` | A window through GLFW and OpenGL |
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

var runtime = BibuiRuntime.Create();
int clicks = 0;

using var window = SilkWindow.Open(runtime, new PlatformWindowOptions("Hello", 480, 240));
using var skia = new SkiaRenderer(window.Gl);   // Bibui.Raster.Skia
while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild();
    var button = runtime.Button("hello", "Click me");
    if (button.Clicked) clicks++;
    UiNode.PushIntoParent(runtime.ParentInScope,
        Column(
            runtime.Text($"Clicked {clicks} times"),
            Space(12f),
            button.Node)
        .FillX().FillY()
        .Styled(new Style { BackgroundColor = UiColor.Background, Padding = EdgeInsets.All(24f) }));
    runtime.EndBuild();
    skia.Draw(runtime.DrawList);
    SilkWindow.EndFrame(window, runtime);
}
```

The app owns the loop: the window waits for input and puts frames on screen,
the runtime builds and records them, and the renderer draws them. The app's
state is its own fields; a click writes one and the next frame reads it.
`samples/counter` is this loop in full.

The first release supports Linux. Documentation, samples and the source are at
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
