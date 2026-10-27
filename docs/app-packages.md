# Bibui.App packages

A bibui app in one package: each `Bibui.App` package brings bibui, a window
and a renderer, wired together, and what the app's loop calls.

```bash
dotnet add package Bibui.App.SilkSkiaGl
```

```csharp
using Bibui.App;
using Bibui.Core.Runtime;

var runtime = BibuiRuntime.Create();
using var app = BibuiApp.Open(runtime, new("Counter", 360, 160));
int count = 0;
app.Frame = Frame;

while (app.WaitForFrame()) Frame();

void Frame()
{
    runtime.BeginBuild(app.Window.Frame, app.Events);
    var column = runtime.BeginParent(runtime.Column());
    runtime.Text($"Clicked {count} times");
    if (runtime.Button("add", "Add one").Clicked) count++;
    runtime.EndParent(column);
    runtime.EndBuild();
    app.Present();
}
```

- `app.WaitForFrame()` waits until a frame is wanted (input, or one the
  runtime owes) and due, collects its input into `app.Events`, and returns
  false once the window is closing. With nothing happening it waits on the OS
  and uses no CPU.
- `app.Window.Frame` is the window as the frame starts: its size, scale and
  refresh rate, which the runtime lays the frame out on and steps animations
  by.
- `app.Present()` draws the runtime's draw list into the window and shows it.
- `app.Frame` is the app's frame, for the window to call where the loop cannot
  run: a resize, which on Windows and macOS holds the loop. Without one, a
  resize asks the loop for a frame.
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
renderer that it was built with (`[1.0.0]`, not `>= 1.0.0`). They are
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
  shown on the window's GL framebuffer. The runtime paces the loop to the
  monitor's refresh rate; `app.Frame` keeps the window drawing while it is
  resized on Windows and macOS, where the OS holds the loop.
- **The Wayland packages** talk to the compositor directly, with no GLFW and no
  GL: the frame is drawn into the window's shared-memory buffers.
  The compositor's frame callback paces the loop, as vsync would, and a
  configure or a new scale draws a frame. The compositor groups and themes
  windows by an application id:
  `new BibuiAppOptions { AppId = "dev.example.app" }`.
  Both publish with Native AOT: `WaylandManaged` as one binary with no native
  dependency beyond libwayland-client, `WaylandSkiaCpu` beside Skia's
  `libSkiaSharp.so`. The Silk packages run on the JIT
  ([Publishing with Native AOT](https://github.com/Navisurg/bibui/blob/main/docs/aot.md) says why).
- **There is no `Bibui.App` package for the browser.** `BibuiApp` is a loop
  the app owns (`while (app.WaitForFrame())`), and a page may not block: in the
  browser the frame is handed to `BrowserWindow.Run` and called on
  `requestAnimationFrame` instead. A browser app references
  `Bibui.Platform.Browser` and a renderer, as [bibui in the
  browser](https://github.com/Navisurg/bibui/blob/main/docs/browser.md) shows.
- **Test-server screenshots** are drawn by the app's own renderer, so they hold
  its textures, except on `SilkSkiaGl`, where the GPU draws into the window
  and a separate CPU renderer draws the screenshots.

The counter is one app, [`samples/counter`](https://github.com/Navisurg/bibui/tree/main/samples/counter),
compiled by three projects that differ only in their package:
[`samples/counter-gl`](https://github.com/Navisurg/bibui/tree/main/samples/counter-gl)
(`SilkSkiaGl`), [`samples/counter-cpu`](https://github.com/Navisurg/bibui/tree/main/samples/counter-cpu)
(`SilkSkiaCpu`) and [`samples/counter-wayland`](https://github.com/Navisurg/bibui/tree/main/samples/counter-wayland)
(`WaylandManaged`, one Native AOT binary).

## The test server

The test server lets a test or an agent drive a running app: click, type,
read the tree, take a screenshot. It starts when the app asks,
`BibuiApp.Open(runtime, new("App", 800, 600), new BibuiAppOptions { TestServer = true })`,
or when the environment variable `BIBUI_TEST_SERVER=1` is set. Otherwise it is
off.

## Writing the loop yourself

A `Bibui.App` package is a window package and a renderer package, put
together. Reference those instead (for `SilkSkiaGl`: `Bibui.Core`,
`Bibui.Platform.Silk`, `Bibui.Raster.Skia`) to write the loop yourself, draw
more than one renderer into a window, or bring a window or renderer of your
own:

```csharp
using var window = SilkWindow.Open(SilkGl.Options(new("Counter", 360, 160)));
runtime.WakeUp = window.Wake;
runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
var gl = SilkGl.Target(window);
using var skia = new SkiaGlRenderer(gl);

while (!window.IsClosing)
{
    bool idle = runtime.FramesRequested == 0 && window.Events.Count == 0;
    if (!idle) runtime.SleepUntilNextFrame();
    var events = window.GetEvents(wait: idle);
    if (runtime.FramesRequested == 0 && events.Count == 0) continue;
    runtime.ConsumeFrame();
    runtime.BeginBuild(window.Frame, events);
    Build();
    runtime.EndBuild();
    SilkGl.BeginDraw(window, gl);
    skia.Draw(runtime.DrawList);
    SilkGl.EndDraw(window);
}
```

The window knows no runtime: the loop wires its wake and clipboard to the
runtime, hands the runtime its `Frame` and events each frame, and tells it when
to wait. Without input or a frame owed, `GetEvents(wait: true)` blocks; with a
frame owed, the runtime sleeps until it is due and `GetEvents(wait: false)`
takes what came meanwhile. A Wayland window paces the loop itself
(`window.PacesFrames`): its `GetEvents(wait: false)` waits for the compositor's
frame callback, so the loop does not sleep, and it draws into `window.Cpu`
between `WaylandWindow.BeginDraw` and `EndDraw`. `window.OnRefresh` is what the
window calls on a resize: the frame (build and draw), or `runtime.RequestFrame`.

More at https://github.com/Navisurg/bibui.
