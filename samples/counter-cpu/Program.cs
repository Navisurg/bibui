// The counter drawn on the CPU: the same code as samples/counter, with the
// Bibui.App.SilkSkiaCpu package in place of Bibui.App.SilkSkiaGl.
using Bibui.App;
using Bibui.Runtime;

var runtime = BibuiRuntime.Create();
using var app = BibuiApp.Open(runtime, new("Counter (CPU)", 360, 160));
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
