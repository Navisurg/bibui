# Bibui.App packages

A bibui app in one package: each `Bibui.App` package brings bibui, a window
and a renderer, and runs the frame in two calls.

```bash
dotnet add package Bibui.App.SilkSkiaGl
```

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

- `app.BeginFrame()` waits until a frame is wanted and collects its input into
  `app.Events`; it returns false once the window is closing.
- `app.EndFrame()` draws the runtime's draw list into the window, presents it,
  and paces the loop.
- `app.Renderer` is the renderer, for textures (`CreateTexture`,
  `UpdateTexture`, `ReleaseTexture`); `app.Window` is the window.

## The packages

`BibuiApp` is the same type, with the same calls, in every package. Changing
the package changes how the app is drawn, and nothing in its code. Because it
is the same type, an app references exactly one `Bibui.App` package, and a
library references none: two in one app do not compile (CS0433). A library
takes the parts it needs (`Bibui.Core`, a renderer) and leaves the choice of window
and renderer to the app.

Each package depends on exactly the version of `Bibui.Core`, its window and its
renderer that it was built with (`[0.10.1]`, not `>= 0.10.1`). They are
released together, so an app that also references one of them directly
references the same version.

| Package | Window | Renderer | Native AOT |
|---|---|---|---|
| `Bibui.App.SilkSkiaGl` | Silk (GLFW), OpenGL | Skia, on the GPU | No |
| `Bibui.App.SilkSkiaCpu` | Silk (GLFW), OpenGL | Skia, on the CPU | No |
| `Bibui.App.SilkManaged` | Silk (GLFW), OpenGL | bibui's managed rasterizer, on the CPU | No |
| `Bibui.App.WaylandManaged` | Wayland | bibui's managed rasterizer, on the CPU | Yes |
| `Bibui.App.WaylandSkiaCpu` | Wayland | Skia, on the CPU | Yes, beside `libSkiaSharp.so` |

Where they differ:

- **The Silk packages** open the window with GLFW and present through OpenGL.
  `SilkSkiaCpu` and `SilkManaged` draw on the CPU into memory, which is then
  shown on the window's GL framebuffer. Set `app.OnRefresh` to the loop's body
  to keep drawing while the window is resized on Windows and macOS, where the
  OS blocks the loop.
- **The Wayland packages** talk to the compositor directly, with no GLFW and no
  GL: the frame is drawn into the window's shared-memory buffers.
  `app.OnRefresh` is never called, because a Wayland window never blocks the
  loop. The compositor groups and themes windows by an application id:
  `new BibuiAppOptions { AppId = "dev.example.app" }`.
  Both publish with Native AOT: `WaylandManaged` as one binary with no native
  dependency beyond libwayland-client, `WaylandSkiaCpu` beside Skia's
  `libSkiaSharp.so`. The Silk packages run on the JIT: Silk.NET is not
  trim-safe.
- **There is no `Bibui.App` package for the browser.** `BibuiApp` is a loop
  the app owns (`while (app.BeginFrame())`), and a page may not block: in the
  browser the frame is handed to `BrowserWindow.Run` and called on
  `requestAnimationFrame` instead. A browser app references
  `Bibui.Platform.Browser` and a renderer, as [bibui in the
  browser](https://github.com/Navisurg/bibui/blob/main/docs/browser.md) shows.
- **Test-server screenshots** are drawn by the app's own renderer, so they hold
  its textures, except on `SilkSkiaGl`, where the GPU draws into the window
  and a separate CPU renderer draws the screenshots.

The counter is in the repository three times, with the same code and a
different package: [`samples/counter`](https://github.com/Navisurg/bibui/tree/main/samples/counter)
(`SilkSkiaGl`), [`samples/counter-cpu`](https://github.com/Navisurg/bibui/tree/main/samples/counter-cpu)
(`SilkSkiaCpu`) and [`samples/counter-wayland`](https://github.com/Navisurg/bibui/tree/main/samples/counter-wayland)
(`WaylandManaged`, one Native AOT binary).

## The test server

The test server lets a test or an agent drive a running app: click, type,
read the tree, take a screenshot. It starts when the app asks,
`BibuiApp.Open(runtime, window, new BibuiAppOptions { TestServer = true })`,
or when the environment variable `BIBUI_TEST_SERVER=1` is set. Otherwise it is
off.

## Writing the loop yourself

A `Bibui.App` package is a window package and a renderer package, put
together. Reference those instead (for `SilkSkiaGl`: `Bibui.Core`,
`Bibui.Platform.Silk`, `Bibui.Raster.Skia`) to write the loop yourself, draw
more than one renderer into a window, or bring a window or renderer of your
own:

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

More at https://github.com/Navisurg/bibui.
