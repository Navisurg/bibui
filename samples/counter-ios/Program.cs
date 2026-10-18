// The README's quick start on iOS: a label and a button that counts its taps.
// samples/counter is the same app on the desktop; here UIKit owns the main
// thread, so the loop runs inside SilkIosWindow.RunApp. The window is the
// whole screen; the counter sits in its middle, clear of the notch and the
// home indicator.
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

SilkIosWindow.RunApp(() =>
{
    var runtime = BibuiRuntime.Create();
    using var window = SilkIosWindow.Open(runtime, new("Counter", 0, 0));
    using var skia = new SkiaGlRenderer(window.Gl);
    int count = 0;

    while (SilkIosWindow.BeginFrame(window, runtime))
    {
        runtime.BeginBuild(window.Events);
        // Centred across the screen, in a column centred down it.
        using (runtime.PushParent(runtime.RowFill()))
        using (runtime.Centred())
        using (runtime.PushParent(runtime.ColumnFillY()))
        using (runtime.Centred())
        {
            runtime.Text($"Clicked {count} times");
            if (runtime.Button("add", "Add one").Clicked) count++;
        }
        runtime.EndBuild();
        SilkIosWindow.BeginDraw(window);
        skia.Draw(runtime.DrawList);
        SilkIosWindow.EndDraw(window);
        SilkIosWindow.EndFrame(window, runtime);
    }
});
