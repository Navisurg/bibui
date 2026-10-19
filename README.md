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

**Platforms:** Linux on x64, and the browser through .NET WebAssembly. Other
platforms are not supported yet.

## Install

bibui needs .NET 10. Add one `Bibui.App` package: it brings bibui, a window
and a renderer.

```bash
dotnet new console -n Counter && cd Counter
dotnet add package Bibui.App.SilkSkiaGl
```

| Package | Window | Renderer | Native AOT |
|---|---|---|---|
| `Bibui.App.SilkSkiaGl` | Silk (GLFW) | Skia on the GPU (OpenGL) | No, JIT only for now |
| `Bibui.App.SilkSkiaCpu` | Silk (GLFW) | Skia on the CPU | No, JIT only for now |
| `Bibui.App.SilkManaged` | Silk (GLFW) | managed rasterizer, on the CPU | No, JIT only for now |
| `Bibui.App.WaylandManaged` | Wayland | managed rasterizer, on the CPU | Yes |
| `Bibui.App.WaylandSkiaCpu` | Wayland | Skia on the CPU | Yes, beside `libSkiaSharp.so` |

Each has the same `BibuiApp` type with the same calls, so changing the package
changes how your app is drawn and nothing in its code. [Bibui.App
packages](docs/app-packages.md) says where they differ. Headless tests use
`Bibui.Testing`. In the browser there is no `Bibui.App` package: a browser app
references `Bibui.Platform.Browser` and a renderer ([bibui in the
browser](docs/browser.md)).

## Quick start

[`samples/counter`](samples/counter/Program.cs) is a simple bibui app: a label
and a button that counts its clicks.

```csharp
using Bibui.App;
using Bibui.Runtime;

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

The app owns its loop:

- `app.BeginFrame()` waits until a frame is wanted and collects its input;
- `runtime.BeginBuild` / `EndBuild` build the frame and record what to draw;
- `app.EndFrame()` draws that into the window, presents it, and paces the loop.

The build is ordinary code inside that loop, and the app's state is its own
variables. Run it:

```bash
dotnet run --project samples/counter
```

![The quick start's window after three clicks: "Clicked 3 times" above an "Add one" button](docs/images/quick-start-counter.png)

bibui has no dedicated `Column` type that contains a list of its children.
Instead every node (`UiNode`, which `Box` and `Text` derive from) links to its
`Parent`, its `FirstChild`/`LastChild` and its siblings `Prev`/`Next`, so any
box holds a subtree.
`ColumnFit` is a fast path for a `Box` that lays its children out on the
y-axis and is sized to fit its children.

In the sample, we use `runtime.PushParent` to define a scope where all boxes
built through the runtime are added as children of `column`.

You may also explicitly append the children to the `column` `Box`:

```csharp
var column = runtime.ColumnFit();

var text = runtime.Text($"Clicked {count} times");
UiNode.PushIntoParent(column, text);

var button = runtime.Button(id: "add", "Add one");
UiNode.PushIntoParent(column, button);
```

This appends each node to `column`'s children.

`runtime.Button` is itself a composition: a `Box` that claims left presses,
with a `Text` inside. `button.Clicked` reports a click, so the app reacts to it
inline, with no callbacks.

### Writing the loop yourself

A `Bibui.App` package is a window package and a renderer package, put
together. Reference those instead (`Bibui`, `Bibui.Platform.Silk`,
`Bibui.Raster.Skia`) and the same loop is:

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

That is the way to draw several renderers into one window, step a test server
of your own, or bring a window or renderer that bibui does not ship.
[`samples/bibui-app`](samples/bibui-app/Program.cs) is written this way.

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

## Three routes

The runtime records each frame into a draw list; a renderer draws that list,
into a window a platform provides. You pick both explicitly, and nothing is
discovered at run time.

- **Silk + Skia.** `SilkWindow` opens the window with GLFW, and `SkiaGlRenderer`
  draws into its OpenGL framebuffer. This route runs on the JIT only for now:
  Silk.NET itself is not trim-safe, so it does not publish with Native AOT.
- **Wayland + managed raster.** `WaylandWindow` talks to the compositor through
  libwayland-client, and `ManagedRenderer` draws on the CPU into its
  shared-memory buffers, with a rasterizer written in C# and FreeType/HarfBuzz
  text. There is no GLFW, GL or Skia on this route, and it publishes as a
  single Native AOT binary of about 5 MB:
  `dotnet run --project tests/wayland-hello`.

  ```bash
  dotnet publish -c Release -r linux-x64 -p:PublishAot=true
  ```

- **Browser + managed raster.** `BrowserWindow` is a `<canvas>` in a .NET
  WebAssembly app, and `ManagedRenderer` draws each frame on the CPU, with
  FreeType and HarfBuzz built for the browser. A page may not block, so the
  browser owns the loop: the frame is handed to `BrowserWindow.Run` and drawn
  on `requestAnimationFrame` when there is something to draw. Several apps can
  share one page. The counter is about 1.7 MB to download with Brotli:
  [`samples/counter-browser`](samples/counter-browser/Program.cs).

Your build code is the same on every route. [Publishing with Native
AOT](docs/aot.md) and [bibui in the browser](docs/browser.md) have the details.

## Dependencies

bibui uses .NET 10, and the platform + rasterizer route your application uses
decides which other dependencies it needs. In every case the dependency tree is small:

| Package | NuGet dependencies | System libraries |
|---|---|---|
| `Bibui` | `System.IO.Hashing` | `libfreetype6`, `libharfbuzz0b` (text) |
| `Bibui.Raster.Skia` | `SkiaSharp` (with its Linux native assets), `Bibui.Gl` | `libfontconfig1` |
| `Bibui.Gl` | `Silk.NET.OpenGL` (the GL binding only) | an OpenGL driver |
| `Bibui.Platform.Silk` | `Silk.NET` windowing, input and OpenGL (GLFW), `Bibui.Gl`, `TextCopy` | an OpenGL driver; GLFW itself ships with Silk.NET |
| `Bibui.Platform.Wayland` | none | `libwayland-client`, `libwayland-cursor`, `libxkbcommon` |
| `Bibui.Platform.Browser` | none | none: FreeType and HarfBuzz ship inside it, built for the browser; building an app needs the `wasm-tools` workload |
| `Bibui.Raster.Managed.Backend` | none (`Bibui.Raster.Managed`) | none beyond `Bibui`'s |

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
- [bibui in the browser](docs/browser.md): a bibui app on a canvas, with .NET
  WebAssembly, and what does not work there yet.
- Samples: [`samples/showcase`](samples/showcase) is an observatory console
  that uses most of bibui, [`samples/bibui-app`](samples/bibui-app) is a short
  interactive app, and [`samples/component-gallery`](samples/component-gallery)
  has a page per component.

## Building from source

```bash
dotnet build bibui/bibui.csproj -c Release
dotnet run --project tests/bibui-runtime-tests -c Release   # runtime, components and layout tests
dotnet run --project samples/component-gallery              # every component in one window
```

[CONTRIBUTING.md](CONTRIBUTING.md) lists every test project and the rules a
change keeps.

## Licence

bibui is licensed under the Apache License 2.0. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
