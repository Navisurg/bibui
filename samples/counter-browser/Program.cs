// The counter in the browser: the same build as samples/counter, on a
// <canvas> (wwwroot/index.html) through Bibui.Platform.Browser, drawn by the
// managed rasterizer. The browser owns the loop, so the frame's body is handed
// to BrowserWindow.Run, which draws it on requestAnimationFrame whenever there
// is a frame to draw.
using Bibui.Platform.Browser;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;

// The app runs only in the browser, as BrowserWindow does.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

var runtime = BibuiRuntime.Create();
var window = await BrowserWindow.OpenAsync(runtime, "counter");
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
