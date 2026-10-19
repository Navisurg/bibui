# Bibui.App.SilkSkiaCpu

A bibui app in one package: bibui, a Silk window drawn by Skia on the CPU, shown through the window's GL framebuffer.

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

`BibuiApp` is the same type, with the same calls, in every Bibui.App package:
`Bibui.App.SilkSkiaGl`, `Bibui.App.SilkSkiaCpu`, `Bibui.App.SilkManaged`,
`Bibui.App.WaylandManaged` and `Bibui.App.WaylandSkiaCpu`. Changing the package
changes how the app is drawn, and nothing in its code.

- `app.Renderer` is the renderer, for textures (`CreateTexture`, `UpdateTexture`, `ReleaseTexture`).
- `app.Window` is the window.
- The test server starts with `new BibuiAppOptions { TestServer = true }`, or when `BIBUI_TEST_SERVER=1` is set; otherwise it is off.

For a loop of your own, reference the window and renderer packages this one is made of instead.

See https://github.com/Navisurg/bibui.
