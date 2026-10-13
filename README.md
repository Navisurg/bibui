<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/bibui-logo-on-dark.svg">
    <img src="docs/brand/bibui-logo.svg" alt="bibui" width="280">
  </picture>
</p>

bibui is a code-first UI library for .NET: each frame, your code describes the
window as a tree of boxes in plain C#, and bibui lays it out and paints it.
Components are ordinary functions that return what happened to them, and each
one is checked pixel by pixel against a reference picture.

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

A window with a button and a counter. Replace `Program.cs` with:

```csharp
using Bibui;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using static Bibui.Arrange;

var runtime = new BibuiRuntime(new SkiaRasterBackend());
var label = BibuiStr.FromStrLit("Add one"u8);
int count = 0;

runtime.Mount(() =>
{
    var add = Button.Build(runtime, "add", label);
    if (add.Clicked) count++;
    var text = runtime.Text(System.Text.Encoding.UTF8.GetBytes($"Clicked {count} times"));
    return Column(text, Space(12f), add).FillX().FillY().Padded(EdgeInsets.All(24f));
});

new BibuiWindow(runtime, new SilkPlatformHost()).Run("Counter");
```

```bash
dotnet run
```

The build function runs every frame. `Button.Build` reports whether the
button was clicked, so the app changes its own state right there. There are no
callbacks. Labels are UTF-8 (`"Add one"u8`), and the window is cleared to the
theme's background colour before each frame is painted.

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

  var runtime = new BibuiRuntime(new ManagedRasterBackend());
  // ... runtime.Mount(...) as above ...
  new BibuiWindow(runtime, new WaylandPlatformHost(new WaylandHostOptions { AppId = "dev.example.counter" }))
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
