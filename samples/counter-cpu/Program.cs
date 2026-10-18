// The counter, drawn on the CPU: Skia draws into memory, and GlCpuFramebuffer
// shows that memory on the Silk window's GL framebuffer. samples/counter is the
// same app drawn on the GPU; only the renderer and GlCpuFramebuffer's draw
// bracket, inside the window's, differ.
using Bibui.Gl;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
using var window = SilkWindow.Open(runtime, new("Counter (CPU)", 360, 160));
using var shown = new GlCpuFramebuffer(window.Gl);
using var skia = new SkiaCpuRenderer(shown.Cpu);
int count = 0;

while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    using (runtime.PushParent(runtime.ColumnFit()))
    {
        runtime.Text($"Clicked {count} times");
        if (runtime.Button("add", "Add one").Clicked) count++;
    }
    runtime.EndBuild();
    SilkWindow.BeginDraw(window);
    shown.BeginDraw();
    skia.Draw(runtime.DrawList);
    shown.EndDraw();
    SilkWindow.EndDraw(window);
    SilkWindow.EndFrame(window, runtime);
}
