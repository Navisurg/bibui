# Bibui.Platform.Browser

A window for bibui in the browser: a `<canvas>` in a .NET WebAssembly app
(`Microsoft.NET.Sdk.WebAssembly`, no Blazor), drawn on the CPU and shown with
`putImageData`. FreeType and HarfBuzz come with the package as WebAssembly
static libraries, linked into the app.

```csharp
var runtime = BibuiRuntime.Create();
var window = await BrowserWindow.OpenAsync("app");            // <canvas id="app">
runtime.WakeUp = window.Wake;
runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
var managed = new ManagedRenderer(window.Cpu);                // Bibui.Raster.Managed.Backend
BrowserWindow.Run(window, () => runtime.FramesRequested > 0, () =>
{
    runtime.ConsumeFrame();
    runtime.BeginBuild(window.Frame, window.Events);
    /* your frame */
    runtime.EndBuild();
    BrowserWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    BrowserWindow.EndDraw(window);
});
```

The browser owns the loop, so the app hands its frame to `BrowserWindow.Run`,
which returns at once; the frame is drawn on `requestAnimationFrame` whenever
the window has input, a frame is owed (`frameOwed`) or it was resized, and its
canvas is in view. `window.Frame` is the canvas's size in device pixels,
devicePixelRatio, and the display's refresh rate, measured from
`requestAnimationFrame`.
Several windows on one page share one loop. `window.Cpu` is the frame's memory
at the canvas's size in device pixels.

It needs the `wasm-tools` workload (`dotnet workload install wasm-tools`):
linking FreeType and HarfBuzz is a native relink of the app. Dead keys, IME
composition and on-screen keyboards do not type yet. More in
[docs/browser.md](https://github.com/Navisurg/bibui/blob/main/docs/browser.md).

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0; FreeType is
used under the FreeType License (FTL) and HarfBuzz under its MIT licence
(both in the package's licenses/ folder).
