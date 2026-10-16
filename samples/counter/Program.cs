// The README's quick start: a label and a button that counts its clicks.
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
using var window = SilkWindow.Open(runtime, new("Counter", 360, 160));
using var skia = new SkiaRenderer(window.Gl);
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
    skia.Draw(runtime.DrawList);
    SilkWindow.EndFrame(window, runtime);
}
