// The README's quick start: a label and a button that counts its clicks. This
// directory is only the app; samples/counter-gl, counter-cpu and counter-wayland
// compile it, each with a different Bibui.App package, and the code is the same.
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

    runtime.BeginCenterXY();
    var column = runtime.BeginParent(runtime.Column());
    {
        runtime.Text($"Clicked {count} times");
        runtime.Gap(12);
        if (runtime.Button("add", "Add one").Clicked)
            count++;
    }
    runtime.EndParent(column);
    runtime.EndCenterXY();

    runtime.EndBuild();
    app.Present();
}
