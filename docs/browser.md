# bibui in the browser

A bibui app runs in the browser as a .NET WebAssembly app: the runtime and the
app's build code run on .NET in the page, the managed rasterizer draws each
frame on the CPU, and the frame is put on a `<canvas>`. The build code is the
same as on the desktop; the window is `BrowserWindow`, from the package
`Bibui.Platform.Browser`.

This page covers setting a browser app up, how its frames are driven and why
that differs from a desktop loop, embedding several apps in one page, and
what does not work yet.

## Set up

You need the .NET 10 SDK and its `wasm-tools` workload. bibui measures and
shapes text with FreeType and HarfBuzz; the package brings them built for the
browser, and linking them into the app is a native relink, which the
workload does:

```bash
dotnet workload install wasm-tools
```

A browser app is a `Microsoft.NET.Sdk.WebAssembly` project (the SDK of the
`wasmbrowser` template, not Blazor), with the window package and a renderer:

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Bibui.Platform.Browser" Version="0.10.1" />
    <PackageReference Include="Bibui.Raster.Managed.Backend" Version="0.10.1" />
  </ItemGroup>
</Project>
```

The page has a canvas with a CSS size, and starts .NET with `runMain`, which,
unlike `run`, leaves the runtime running when `Main` returns:

```html
<canvas id="app" style="width: 100vw; height: 100vh"></canvas>
<script type="module">
  import { dotnet } from './_framework/dotnet.js';
  const runtime = await dotnet.create();
  await runtime.runMain();
</script>
```

[`samples/counter-browser`](../samples/counter-browser/Program.cs) is the
counter of the quick start on this route. Publish it and serve the result
with any static file server:

```bash
dotnet publish samples/counter-browser -c Release -o out/counter-browser
python3 -m http.server 8000 -d out/counter-browser/wwwroot
```

## The frame

```csharp
using Bibui.Platform.Browser;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

var runtime = BibuiRuntime.Create();
var window = await BrowserWindow.OpenAsync(runtime, "app");   // <canvas id="app">
var managed = new ManagedRenderer(window.Cpu);
int count = 0;

BrowserWindow.Run(window, runtime, () =>
{
    runtime.BeginBuild(window.Events);
    using (runtime.PushParent(runtime.ColumnFit()))
    {
        runtime.Text($"Clicked {count} times");
        if (runtime.Button("add", "Add one").Clicked) count++;
    }
    runtime.EndBuild();
    BrowserWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    BrowserWindow.EndDraw(window);
});
```

The body of the frame is a desktop loop's body: `window.Events` into
`BeginBuild`, the build, `EndBuild`, and the renderer's draw between
`BeginDraw` and `EndDraw`. What differs is who calls it.

A desktop app owns its loop: `while (WaylandWindow.BeginFrame(window, runtime))`
waits until there is a frame to draw, and the loop runs on the app's thread
until the window closes. A page may not do that. Code that blocks freezes the
page, and the browser shows nothing until it returns. So the app hands the
frame to `BrowserWindow.Run`, which returns at once, and the browser calls the
frame on `requestAnimationFrame`:

- A window draws when there is something to draw: input on its canvas, a
  frame its runtime owes (`runtime.RequestFrame()`, which animations ask for),
  or a change in its canvas's size. A window with nothing owed draws nothing,
  and when no window on the page owes anything the loop stops: an idle page
  costs nothing until input, a resize or a request starts it again.
- A window draws only while its canvas is in the viewport. One scrolled out of
  sight is drawn when it comes back.
- `runtime.Dt` is the time since the window's last frame, or one frame at
  `runtime.TargetHz` after a wait for input, and at most a quarter of a second,
  so a tab the browser stopped drawing comes back without a jump.
- A frame that throws stops its own window, and the exception is written to
  the browser's console. The other windows on the page keep drawing.

`OpenAsync` is asynchronous because the first window loads the package's
module, `bibui-browser.js`, before it can attach to the canvas. `window.Close()`
(or `Dispose`) stops drawing and lets go of the canvas, which keeps its last
frame.

## Size and pixels

The page decides the canvas's size, with CSS. The window keeps the canvas's
backing store at that size in device pixels, CSS size × devicePixelRatio, and
draws every frame at exactly that size, as the browser reports it (a
ResizeObserver on the device-pixel content box where the browser has one,
the CSS size × devicePixelRatio where it does not, and a watch on the ratio
itself for zoom and a move to another screen). devicePixelRatio is the
runtime's scale, so the frame is sharp at 1.25 and 2 as on the desktop, and
positions stay in CSS pixels.

A canvas with no CSS size of its own is as large as its backing store, which
would grow with every frame at a ratio above 1; such a canvas keeps the CSS
size it had when the window opened.

The renderer draws BGRA with premultiplied alpha into `window.Cpu`, the
canvas takes RGBA with straight alpha, and `EndDraw` converts one to the
other and puts the frame on the canvas with `putImageData`. The memory is kept
from frame to frame, so the managed rasterizer's damage tracking
(`new ManagedRenderer { DamageTracking = true }`) repaints only what changed.
`SkiaCpuRenderer` draws into `window.Cpu` as well, with SkiaSharp's own
WebAssembly assets (`SkiaSharp.NativeAssets.WebAssembly`).

## Input

The canvas takes the keyboard when it is pressed (it is given `tabindex="0"`
when it has none).

- **Pointer**: moves, presses and releases, in CSS pixels; the canvas holds
  the pointer while a button is down. Leaving the canvas moves the pointer off
  everything in it.
- **Wheel**: scrolls what is under the pointer while the canvas has focus;
  otherwise the wheel scrolls the page, so a page of embedded apps scrolls as
  a page does.
- **Keys**: a key-down and key-up for each key, as the desktop hosts report
  them: `KeyboardEvent.code`, the physical key, is the `Key`, so a shortcut on
  Ctrl+Z is the same key on every layout. Cmd (Meta) is reported as Super,
  which the runtime takes as Ctrl for shortcuts. A key that types a character
  reports it after its key-down; AltGr types.
- **The page's own keys**: while the canvas has focus, the keys the page
  would act on (the arrows, Space, Page Up and Down, Home, End, Backspace,
  Delete, Enter, the keys that type, and Ctrl+A) go to bibui only. Tab is not
  kept: bibui does not move focus with it, so it leaves the canvas as it
  leaves any element. Browser shortcuts (Ctrl+R, Ctrl+L, Ctrl+T) are not kept.
- **Clipboard**: copying writes through `navigator.clipboard`. Pasting takes
  the text of the paste event the browser fires for Ctrl+V and Cmd+V; the
  browser offers the clipboard only in that event.
- **Cursor**: `window.SetCursor(CursorShape.Text)` sets the canvas's CSS cursor.

## Several apps on one page

Each window has its own canvas and runtime, and every window on the page is
drawn from the one `requestAnimationFrame` loop. A page that embeds apps it
does not start itself (a documentation page with live examples, say) boots
.NET through the package's module, which finds `_framework/` from its own
URL, and passes each canvas to an export of the app:

```csharp
public static partial class Embed
{
    [JSExport]
    public static async Task Mount(JSObject canvas)
    {
        var runtime = BibuiRuntime.Create();
        var window = await BrowserWindow.OpenAsync(runtime, canvas);
        var managed = new ManagedRenderer(window.Cpu);
        BrowserWindow.Run(window, runtime, () => { /* the frame */ });
    }
}
```

```js
const bibui = await import('/app/_content/Bibui.Platform.Browser/bibui-browser.js');
const runtime = await bibui.boot();                       // loads /app/_framework/, runs Main
const { Embed } = await runtime.getAssemblyExports('my-app');
for (const canvas of document.querySelectorAll('canvas.demo')) await Embed.Mount(canvas);
```

## Size of an app

The counter, published in Release on the interpreter, is about 5.8 MB, 1.7 MB
with Brotli: the .NET runtime and its interpreter (2.7 MB of it), the base
library, bibui with its embedded fonts, the managed rasterizer, and FreeType
and HarfBuzz. A server or CDN that compresses serves the smaller figure.

Ahead-of-time compilation (`-p:RunAOTCompilation=true`) compiles the app's
code to WebAssembly: frames take a third to a half of the time, and the
download is about 1.7 times larger. bibui's packages are trim-safe, so either way the
publish trims what the app does not use.

## What does not work yet

- **Text input beyond the keyboard.** There is no text-input element behind
  the canvas, so dead keys (´ then e), IME composition (Chinese, Japanese,
  Korean input) and on-screen keyboards on phones and tablets do not type.
- **Touch** arrives as a pointer: a tap presses and releases. A touch drag
  scrolls the page, as it does over any element, and bibui sees the press
  cancelled; a page that wants drags in the app gives the canvas
  `touch-action: none`. There are no gestures.
- **The GPU.** Frames are drawn on the CPU; there is no WebGL or WebGPU renderer.
- **Accessibility.** A canvas is one image to a screen reader.
- **The test server and `Bibui.App`.** The test server listens on a socket, and
  `BibuiApp`'s blocking loop cannot run in a page, so there is no
  `Bibui.App` package for the browser ([Bibui.App packages](app-packages.md)).
- **Threads.** The app runs on the page's one thread; a frame that takes long
  holds the page up for that long.
- **Emscripten version.** FreeType and HarfBuzz are built for .NET 10's
  Emscripten (3.1.56); the package refuses another target framework.

## How FreeType and HarfBuzz are linked

On the desktop, bibui loads FreeType and HarfBuzz from the system
(`libfreetype.so.6`, `libharfbuzz.so.0`). A browser has no system libraries,
so the package carries both as WebAssembly static libraries, built by
`bibui-platform-browser/native/build.sh` from release tarballs pinned by
version and SHA-256, with the workload's own Emscripten, and its targets add
them to the app's link (`NativeFileReference`). They are built as the .NET
runtime is: WebAssembly exception handling, with setjmp and longjmp on it,
and SIMD.

The app's link finds a P/Invoke's library by the static library's file name
without its extension, so the archives are named after bibui's import names:
`libfreetype.so.6.a` and `libharfbuzz.so.0.a`. That leaves bibui's imports,
and the desktop routes that load the system libraries, exactly as they are.
