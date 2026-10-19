<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/bibui-logo-on-dark.svg">
    <img src="docs/brand/bibui-logo.svg" alt="bibui" width="280">
  </picture>
</p>

bibui is an immediate-mode UI library for .NET.

The library consists of a few UI node types such as a `Box` and `Text`,
and everything you build is a composition of these simple primitives.

Zenith is a clauded showcase app provided as a reference for inspiration on how you build
applications with bibui.

![Zenith, the showcase app, built with bibui](samples/showcase/screenshots/overview-dark.png)

**Platforms:** the first release supports Linux on x64. Other platforms are not
supported yet. We expect macOS and, and maybe even iOS, to be supported in the near feature.

## Install

**Prerequisites** 
- .NET 10.

**Getting started with a bibui app**

bibui provides a set of [meta-packages](bibui-apps/) for getting up and running with a bibui app. Each package pulls in everything you need to get started:

```bash
dotnet new console -n Counter && cd Counter
dotnet add package Bibui.App.SilkSkiaGl
```

This creates a bibui app that runs in a Silk.NET window and renders using the Skia OpenGL backend. 
Below we give the set of app platforms currently supported by bibui

| Package | Window | Renderer | Native AOT |
|---|---|---|---|
| `Bibui.App.SilkSkiaGl` | Silk (GLFW) | Skia on the GPU (OpenGL) | No, JIT only for now |
| `Bibui.App.SilkSkiaCpu` | Silk (GLFW) | Skia on the CPU | No, JIT only for now |
| `Bibui.App.SilkManaged` | Silk (GLFW) | managed rasterizer, on the CPU | No, JIT only for now |
| `Bibui.App.WaylandManaged` | Wayland | managed rasterizer, on the CPU | Yes |
| `Bibui.App.WaylandSkiaCpu` | Wayland | Skia on the CPU | Yes, beside `libSkiaSharp.so` |

Each app platform has the same API, and you can therefore change the target platform by changing the package reference. We provide the `counter-gl` and `counter-cpu` samples in the [samples](samples/) directory. They compile the same app, [`samples/counter`](samples/counter/Program.cs), each with a different package: `counter-gl` with `Bibui.App.SilkSkiaGl` (Skia on OpenGL) and `counter-cpu` with `Bibui.App.SilkSkiaCpu` (Skia on the CPU).

## Quick start

[`samples/counter`](samples/counter/Program.cs) is a simple bibui app: a label
and a button that counts its clicks.

```csharp
using Bibui.App;
using Bibui.Core.Runtime;

var runtime = BibuiRuntime.Create();
using var app = BibuiApp.Open(runtime, new("Counter", 360, 160));
int count = 0;

while (app.BeginFrame())
{
    runtime.BeginBuild(app.Events);
    var column = runtime.BeginParent(runtime.Column());
    {
        runtime.Text($"Clicked {count} times");
        if (runtime.Button("add", "Add one").Clicked)
            count++;
    }
    runtime.EndParent(column);

    runtime.EndBuild();
    app.EndFrame();
}
```

You can run the app using the Skia+OpenGL backend with

```bash
dotnet run --project samples/counter-gl
```

![The quick start's window after three clicks: "Clicked 3 times" above an "Add one" button](docs/images/quick-start-counter.png)


## The bibui UI tree structure

bibui has no dedicated `Column` type that contains a list of its children.
Instead every node (`UiNode`, which `Box` and `Text` derive from) links to its
`Parent`, its `FirstChild`/`LastChild` and its siblings `Prev`/`Next`, so any
box holds a subtree.
`Column()` is a fast path for a `Box` that lays its children out on the
y-axis and is sized to fit its children.

In the sample, `runtime.BeginParent(column)` and `runtime.EndParent(column)`
bracket a scope where all boxes built through the runtime are added as
children of `column`.

Each node is built where it goes: the text, then the button, in the order
they appear in the column.

`runtime.Button` is itself a composition: a `Box` that claims left presses,
with a `Text` inside. `button.Clicked` reports a [Interaction](bibui/src/Public/Interaction.cs) as a struct with all the interactions the `Box` took.

## bibui app from scratch

A `Bibui.App` package is a fast path for gluing together a window package and a renderer package. 

You may instead reference directly `Bibui.Core`, `Bibui.Platform.Silk`,
`Bibui.Raster.Skia`, and then you will have to write the main app loop as:

```csharp
using var window = SilkWindow.Open(runtime, new("Counter", 360, 160));
using var skia = new SkiaGlRenderer(window.Gl);

while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    Build();
    runtime.EndBuild();
    SilkWindow.BeginDraw(window);
    skia.Draw(runtime.DrawList);
    SilkWindow.EndDraw(window);
    SilkWindow.EndFrame(window, runtime);
}
```

The example demonstrates 4 layers in bibui:

1. Window and input layer (`BeginFrame`)
2. Bibui UI tree building layer (`BeginBuild`)
3. Draw layer (`EndBuild` + `BeginDraw`)
4. Render layer (`EndDraw`/`EndFrame`)

Data only flows from one layer down to the next with each layer being able
to freely interpret the data. In the example above each layer does the following:

1. `BeginFrame` consumes Silk input and window events and convert them to bibui `WindowEvent`
2. `BeginBuild` converts the `WindowWevent` stream to intent-based `BibuiEvent`, and clears previous frames builder state. 
`EndBuild` walks the UI tree and performs an auto layout pass to produce a final UI tree with coordinates for each node in the tree.
3.  `EndBuild` walks the final resolved UI tree and issues [draw operations](bibui/src/Draw/DrawList.cs). This doesn't do any drawing, but specifies intent for what to be rendered in a separate pass.
4. `Skia.Draw` takes the list of draw operations produced by the previous layer and interprets them into a series of Skia operations.
`EndDraw` and `EndFrame` marks the end of drawing, and the screen is updated.

This way, a new platform can be added by creating

- A new platform layer that translates the platform events to `WindowEvent`s
- A render layer that interprets the draw operations.

### The test server

The test server lets a test or an agent drive a running app: click, type,
read the tree, take a screenshot. A `BibuiApp` starts it when asked,
`BibuiApp.Open(runtime, window, new BibuiAppOptions { TestServer = true })`,
or when the environment variable `BIBUI_TEST_SERVER=1` is set. Otherwise it is
off.

## Component Library

bibui comes with components built from the same `Box` and `Text` primitives:
buttons, switches, toggles, sliders, text fields, tabs, menus, dialogs, a
Markdown viewer and more. Below, a multiline text field holds Markdown source,
`runtime.Markdown` draws it as it is typed, and a switch changes the theme.

![A Markdown editor built with bibui: typing in a multiline field updates a live Markdown preview, ticking a task box in the preview ticks it in the source, and a switch changes between the dark and light themes](docs/images/component-library.gif)

Every component, with pictures, is in [Components](docs/components/index.md).

## Dependencies

bibui uses .NET 10, and the platform + rasterizer route your application uses
decides which other dependencies it needs. In every case the dependency tree is small:

| Package | NuGet dependencies | System libraries |
|---|---|---|
| `Bibui.Core` | `System.IO.Hashing` | `libfreetype6`, `libharfbuzz0b` (text) |
| `Bibui.Raster.Skia` | `SkiaSharp` (with its Linux native assets), `Bibui.Gl` | `libfontconfig1` |
| `Bibui.Gl` | `Silk.NET.OpenGL` (the GL binding only) | an OpenGL driver |
| `Bibui.Platform.Silk` | `Silk.NET` windowing, input and OpenGL (GLFW), `Bibui.Gl`, `TextCopy` | an OpenGL driver; GLFW itself ships with Silk.NET |
| `Bibui.Platform.Wayland` | none | `libwayland-client`, `libwayland-cursor`, `libxkbcommon` |
| `Bibui.Platform.Browser` | none | none: FreeType and HarfBuzz ship inside it, built for the browser; building an app needs the `wasm-tools` workload |
| `Bibui.Raster.Managed.Backend` | none (`Bibui.Raster.Managed`) | none beyond `Bibui.Core`'s |

The Wayland + managed route has no third-party NuGet dependencies at all, and
it is the route that publishes with Native AOT; Silk.NET keeps the other on the
JIT for now.

## Documentation

- [Getting started](docs/getting-started.md): from an empty project to a
  component with state, a headless test and a Native AOT binary.
- [Concepts](docs/concepts.md): boxes, layout, asking for interactions,
  retained boxes, painting.
- [Theming](docs/theming.md): built-in themes, theme files and tokens.
- [Components](docs/components/index.md): every component, with pictures.
- [Native AOT](docs/aot.md): publishing one native binary, and the trimming
  rules for app code.
- Samples: [`samples/showcase`](samples/showcase) is an observatory console
  that uses most of bibui, [`samples/bibui-app`](samples/bibui-app) is a short
  interactive app, and [`samples/component-gallery`](samples/component-gallery)
  has a page per component.

## Building from source

```bash
dotnet build bibui-core/bibui-core.csproj -c Release
dotnet run --project tests/bibui-runtime-tests -c Release   # runtime, components and layout tests
dotnet run --project samples/component-gallery              # every component in one window
```

[CONTRIBUTING.md](CONTRIBUTING.md) lists every test project and the rules a
change keeps.

## Licence

bibui is licensed under the Apache License 2.0. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
