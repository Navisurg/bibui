// The README's quick start on iOS: a label and a button that counts its taps,
// under a title, with a switch between a black and a white screen.
// samples/counter is the counter on the desktop; here UIKit owns the main
// thread, so the loop runs inside SilkIosWindow.RunApp. The window is the
// whole screen; the title sits at the top of its safe area, the rest centred
// below it.
using Bibui;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using Bibui.Theming;

SilkIosWindow.RunApp(() =>
{
    var runtime = BibuiRuntime.Create();
    using var window = SilkIosWindow.Open(runtime, new("Counter", 0, 0));
    using var skia = new SkiaGlRenderer(window.Gl);
    int count = 0;
    bool white = false;

    while (SilkIosWindow.BeginFrame(window, runtime))
    {
        runtime.BeginBuild(window.Events);
        bool flip = false;
        // The screen, black or white: the title at the top of its safe area,
        // below the Dynamic Island, and the rest centred down what is left.
        // Each row centres what it holds across the screen.
        var screen = runtime.ColumnFill();
        screen.Style.BackgroundColor = white ? "#FFFFFF" : "#000000";
        screen.Style.Padding = window.SafeArea with { Top = window.SafeArea.Top + 16f };
        using (runtime.PushParent(screen))
        {
            using (runtime.PushParent(runtime.RowFillX()))
            using (runtime.Centred())
            using (runtime.PushFontSize(ComponentSize.Xxl))
            using (runtime.PushFontFace(FontSlot.Heading, FontWeight.Bold))
                runtime.Text("Hello iOS, from Bibui!");
            using var centred = runtime.Centred();
            using (runtime.PushParent(runtime.RowFillX()))
            using (runtime.Centred())
                flip = runtime.Switch("white", white, "White background").Clicked;
            runtime.Gap(24f);
            using (runtime.PushParent(runtime.RowFillX()))
            using (runtime.Centred())
                runtime.Text($"Clicked {count} times");
            runtime.Gap(8f);
            using (runtime.PushParent(runtime.RowFillX()))
            using (runtime.Centred())
                if (runtime.Button("add", "Add one").Clicked) count++;
        }
        runtime.EndBuild();
        // Between frames, so a frame is built in one theme: the light one's
        // text and controls on white, the dark one's on black.
        if (flip)
        {
            white = !white;
            runtime.SetTheme(white ? ThemeLoader.BibuiLight() : ThemeLoader.BibuiDark());
        }
        SilkIosWindow.BeginDraw(window);
        skia.Draw(runtime.DrawList);
        SilkIosWindow.EndDraw(window);
        SilkIosWindow.EndFrame(window, runtime);
    }
});
