// The counter in the browser: the same build as samples/counter, on a
// <canvas> (wwwroot/index.html) through Bibui.Platform.Browser, drawn by the
// managed rasterizer. The browser owns the loop, so the frame's body is handed
// to BrowserWindow.Run, which draws it on requestAnimationFrame whenever there
// is a frame to draw: input, a resize, or one the runtime owes.
using Bibui.Platform.Browser;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;

// The app runs only in the browser, as BrowserWindow does.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

var runtime = BibuiRuntime.Create();
var window = await BrowserWindow.OpenAsync("counter");
runtime.WakeUp = window.Wake;
runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
var managed = new ManagedRenderer(window.Cpu);
int count = 0;

BrowserWindow.Run(window, () => runtime.FramesRequested > 0, () =>
{
    // Taken before the frame is built, so what the build asks for is still owed after it.
    runtime.ConsumeFrame();
    runtime.BeginBuild(window.Frame, window.Events);
    var column = runtime.BeginParent(runtime.Column());
    runtime.Text($"Clicked {count} times");
    if (runtime.Button("add", "Add one").Clicked) count++;
    runtime.EndParent(column);
    runtime.EndBuild();
    BrowserWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    BrowserWindow.EndDraw(window);
});
