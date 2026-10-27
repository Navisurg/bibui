# Bibui.Core

A code-first native UI library for .NET. Application code describes each frame
through a runtime; a small layout solver sizes and places the whole tree; a
painter records it into a draw list, which a renderer you choose draws. Components (`Button`, `Switch`,
`Slider`, `Dialog`, `Select`, text fields and more) are plain functions that
compose boxes and return what happened. There are no callbacks: a box claims
what it takes, and you ask about it.

This package holds the core, the runtime (`BibuiRuntime`) and the components.
An app runs it on a window and a renderer, most simply through one package
that puts them together:

| Package | What it adds |
|---|---|
| `Bibui` | All of bibui: `Bibui.App.SilkSkiaGl` and every window and renderer |
| `Bibui.App.*` | One window and one renderer, with the loop's calls (`BibuiApp`) |
| `Bibui.Platform.Silk`, `Bibui.Platform.Wayland`, `Bibui.Platform.Browser` | A window on its own |
| `Bibui.Raster.Skia`, `Bibui.Raster.Managed.Backend` | A renderer on its own |
| `Bibui.Testing` | Headless pages for testing an app |
| `Bibui.Automation` | A test port: drive a running app over a socket |

## Hello

With `Bibui` or a `Bibui.App` package referenced:

```csharp
using Bibui.App;
using Bibui.Core;
using Bibui.Core.Runtime;

var runtime = BibuiRuntime.Create();
int clicks = 0;
using var app = BibuiApp.Open(runtime, new("Hello", 480, 240));
app.Frame = Frame;
while (app.WaitForFrame()) Frame();

void Frame()
{
    runtime.BeginBuild(app.Window.Frame, app.Events);
    var page = runtime.BeginParent(runtime.Column().Sized(Size.Fill, Size.Fill));
    page.Style = new Style { BackgroundColor = UiColor.Background, Padding = EdgeInsets.All(24f) };
    runtime.Text($"Clicked {clicks} times");
    runtime.Gap(12f);
    if (runtime.Button("hello", "Click me").Clicked) clicks++;
    runtime.EndParent(page);
    runtime.EndBuild();
    app.Present();
}
```

The app owns the loop: `WaitForFrame` waits for input or a frame the runtime
owes, `Frame` builds the frame on the window as it is (`app.Window.Frame`: its
size, scale and refresh rate), and `Present` draws it. The app's state is its
own fields; a click writes one and the next frame reads it.

The first release supports Linux. Documentation, samples and the source are at
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
