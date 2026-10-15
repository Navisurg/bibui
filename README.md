<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/bibui-logo-on-dark.svg">
    <img src="docs/brand/bibui-logo.svg" alt="bibui" width="280">
  </picture>
</p>

bibui is an immediate-mode UI library for .NET.
The library consists of a few UI components such as a `Box` and `Text` and
everything you build is a composition of these simple primitives.

A clauded demo app can be used as inspiration for what bibui can build:

![Zenith, the showcase app, built with bibui](samples/showcase/screenshots/overview-dark.png)

**Platforms:** the first release supports Linux on x64. Other platforms are not
psupported yet.

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
It sets up Skia for rendering and Silk to handle input and window events:

```csharp
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create().WithSkia();
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

BibuiWindow.Create(runtime).WithSilk().Run(title: "Counter");
```

Run it in a empty console app with

```bash
dotnet run
```

![The quick start's window after three clicks: "Clicked 3 times" above an "Add one" button](docs/images/quick-start-counter.png)

bibui has no dedicated `Column` type that contains a list of children.
Instead the `Box` type contains `Next, Prev, Parent, FirstChild` pointers
to define a subtree rooted at the `Box` node.
`ColumnFit` is a fast path for a `Box` that lays its children out on the
y-axis and is sized to fit its children.

Any `Box` in bibui may have children added to it like so:

```csharp
var column = runtime.ColumnFit();

var text = runtime.Text($"Clicked {count} times");
UiNode.PushIntoParent(column, text);

var button = runtime.Button(id: "add", "Add one");
UiNode.PushIntoParent(column, button);
```

This updates the `Next/Prev` pointers between the children of the `column`
node.

In the example, we use a scope through `runtime.PushParent` that within
the scope adds the parent to the `column`. Any UI nodes defines outside,
will not hang off of the `column` subtree.

`Button` is another box composition that consumed left presses,
and we can react on these events inline with the UI builder code.

## Two routes

bibui draws through a raster backend and runs in a window that a host
provides. You pick both explicitly, and nothing is discovered at run time.

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

  var runtime = BibuiRuntime.Create().WithManagedRasterizer();
  // ... runtime.Mount(...) as above ...
  BibuiWindow.Create(runtime).WithWayland(new WaylandHostOptions { AppId = "dev.example.counter" })
      .Run("Counter");
  ```

  ```bash
  dotnet publish -c Release -r linux-x64 -p:PublishAot=true
  ```

Your build code is the same on both routes. [Publishing with Native
AOT](docs/aot.md) has the details.

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
