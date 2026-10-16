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
supported yet.

## Install

bibui needs .NET 10. Add `Bibui` and one host package, which opens the window:

```bash
dotnet new console -n Counter && cd Counter
dotnet add package Bibui
dotnet add package Bibui.Platform.Silk
```

| Route | Packages | Native AOT |
|---|---|---|
| Silk + Skia (GLFW and OpenGL, CPU fallback) | `Bibui`, `Bibui.Platform.Silk` | No, JIT only for now |
| Wayland + managed raster | `Bibui`, `Bibui.Platform.Wayland`, `Bibui.Raster.Managed.Backend` | Yes |
| Headless tests | `Bibui.Testing` | |

## Quick start

Below, a simple bibui app with a label and a button is shown.
It draws with Skia and uses Silk to handle input and window events:

```csharp
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
using var skia = new SkiaRenderer();
int count = 0;

runtime.Mount(() =>
{
    var column = runtime.ColumnFit();
    using (runtime.PushParent(column))
    {
        runtime.Text($"Clicked {count} times");

        var button = runtime.Button(id: "add", "Add one");
        if (button.Clicked)
            count++;
    }
    return column;
});

BibuiWindow.Create(runtime).WithSilk(skia).Run(title: "Counter");
```

Run it from the project created above:

```bash
dotnet run
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

## Component Library

bibui comes with components built from the same `Box` and `Text` primitives:
buttons, switches, toggles, sliders, text fields, tabs, menus, dialogs, a
Markdown viewer and more. Below, a multiline text field holds Markdown source,
`runtime.Markdown` draws it as it is typed, and a switch changes the theme.

![A Markdown editor built with bibui: typing in a multiline field updates a live Markdown preview, ticking a task box in the preview ticks it in the source, and a switch changes between the dark and light themes](docs/images/component-library.gif)

Every component, with pictures, is in [Components](docs/components/index.md).

## Two routes

The runtime records each frame into a draw list; a renderer draws that list,
and a host provides the window. You pick both explicitly, and nothing is
discovered at run time.

- **Silk + Skia.** `SilkPlatformHost` opens the window with GLFW and renders
  with Skia on OpenGL when it can, falling back to the CPU otherwise
  (`BIBUI_GPU=0` forces the fallback). This route runs on the JIT only for
  now: Silk.NET itself is not trim-safe, so it does not publish with Native AOT.
- **Wayland + managed raster.** `WaylandPlatformHost` talks to the compositor
  through libwayland-client and draws on the CPU into shared-memory buffers,
  with a rasterizer written in C# and FreeType/HarfBuzz text. There is no GLFW,
  GL or Skia on this route, and it publishes as a single Native AOT binary of
  about 5 MB:

  ```csharp
  using Bibui.Platform.Wayland;
  using Bibui.Raster.Managed.Backend;

  var runtime = BibuiRuntime.Create();
  var managed = new ManagedRenderer();
  // ... runtime.Mount(...) as above ...
  BibuiWindow.Create(runtime).WithWayland(managed.Draw, new WaylandHostOptions { AppId = "dev.example.counter" })
      .Run("Counter");
  ```

  ```bash
  dotnet publish -c Release -r linux-x64 -p:PublishAot=true
  ```

Your build code is the same on both routes. [Publishing with Native
AOT](docs/aot.md) has the details.

## Dependencies

bibui uses .NET 10, and the platform + rasterizer route your application uses
decides which other dependencies it needs. In every case the dependency tree is small:

| Package | NuGet dependencies | System libraries |
|---|---|---|
| `Bibui` | `System.IO.Hashing` | `libfreetype6`, `libharfbuzz0b` (text) |
| `Bibui.Raster.Skia` | `SkiaSharp` (with its Linux native assets) | `libfontconfig1` |
| `Bibui.Platform.Silk` | `Silk.NET` windowing, input and OpenGL (GLFW), `TextCopy`, `Bibui.Raster.Skia` | an OpenGL driver for the GPU path (the CPU fallback works without one); GLFW itself ships with Silk.NET |
| `Bibui.Platform.Wayland` | none | `libwayland-client`, `libwayland-cursor`, `libxkbcommon` |
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
