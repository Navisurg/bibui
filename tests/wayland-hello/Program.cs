// wayland-hello: a bibui app on the raw Wayland host.
//
//   dotnet run --project tests/wayland-hello -c Release            # animates: a frame per compositor callback
//   dotnet run --project tests/wayland-hello -c Release -- --still # draws once, then idles at 0% CPU
//   dotnet run --project tests/wayland-hello -c Release -- --first-frame
//                                                                    # headless: one frame, then exits (CI's start-up check)
//
// No GLFW, no GL: libwayland-client, an xdg-shell toplevel, and frames drawn on
// the CPU by the managed rasterizer straight into wl_shm buffers. Input
// is live: the switch, the button and the text field take the pointer and
// keyboard, and the pad at the bottom shows what the host delivered — pointer
// position, buttons, wheel travel, and the modifiers the last event carried. HiDPI and cursors are
// handled too. It closes from the compositor (Super+W, or whatever closes a window).
//
// Native AOT: dotnet publish tests/wayland-hello -c Release -r linux-x64 -p:PublishAot=true

using Bibui;
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using Bibui.TextEdit;

bool animate = !args.Contains("--still");
int clicks = 0;
var typed = new BibuiStr(64);
var typedEdit = new TextEditState();
bool typing = false;
float wheelX = 0f, wheelY = 0f;
string lastButton = "none yet";
KeyModifiers lastModifiers = KeyModifiers.None;

var runtime = BibuiRuntime.Create();
runtime.Surface = new SurfaceOptions { Width = 760, Height = 560, ScaleFactor = 1f };
long frame = 0;

// One frame of the app, built from the state above, top to bottom.
void Build()
{
    frame++;
    // Keep asking while animating: one request per frame keeps the loop
    // going, and the compositor's frame callbacks set the pace.
    if (animate) runtime.RequestFrame();
    float t = (MathF.Sin(frame / 40f) + 1f) / 2f;

    var page = runtime.ColumnFill()
        .Styled(new Style { BackgroundColor = UiColor.Background, Padding = EdgeInsets.All(28f) });
    using var inPage = runtime.PushParent(page);
    using (runtime.PushFontFace(weight: FontWeight.Bold))
        runtime.Text("Hello from raw Wayland", ComponentSize.Xxl, TextColor.Primary);
    runtime.Gap(16f);
    runtime.Text("wl_shm buffers, CPU raster, frame-callback pacing, xkbcommon input. No GLFW, no GL.",
        textColor: TextColor.Secondary);
    runtime.Gap(16f);

    var toggle = runtime.Switch("anim", animate, label: animate ? "Animating (click to stop)" : "Still (click to animate)");
    if (toggle.Clicked) { animate = !animate; runtime.RequestFrame(); }
    runtime.Gap(16f);

    var hello = runtime.Button("hello", clicks == 0 ? "Click me" : $"Clicked {clicks}×");
    if (hello.Clicked) clicks++;
    runtime.Gap(16f);

    // A press focuses the field; Escape or Enter lets it go.
    Box field = runtime.Box();
    TextField.InputStyle(runtime, field, typing);
    field.FillX().Px(height: 40f);
    BibuiWidget line;
    using (runtime.PushParent(field))
        line = runtime.TextField("typed", typed, typedEdit, typing,
            placeholder: "Type here: dead keys, AltGr, held keys repeat, Ctrl+A, Ctrl+Left");
    if (line.Pressed) typing = true;
    foreach (var ev in runtime.UnconsumedEvents)
        if (typing && ev is { IsKeyDown: true, Key: Key.Escape } or { Kind: BibUiEventKind.Submit })
        {
            typing = false;
            ev.Eat();
        }
    runtime.Gap(16f);

    // The bar a frame moves: a groove and a fill inside it.
    Box groove = runtime.Box().FillX().Px(height: 14f);
    groove.Style.CornerRadius = 7f;
    groove.Style.BackgroundColor = UiColor.SurfaceRaised;
    groove.Style.BorderColor = UiColor.Border;
    groove.Style.Padding = EdgeInsets.All(1f);
    using (runtime.PushParent(groove))
    {
        Box fill = runtime.Box().Px(24f + t * 420f, 12f);
        fill.Style.CornerRadius = 6f;
        fill.Style.BackgroundColor = UiColor.Accent;
    }
    runtime.Gap(16f);

    // The pad takes every button and the wheel, and shows what arrived.
    Box pad = runtime.BoxFromId("pad", InteractionKinds.All);
    Interaction p = runtime.InteractionsFor(pad);
    wheelX += p.ScrollX;
    wheelY += p.ScrollY;
    if (p.Pressed) lastButton = "left";
    else if (p.RightPressed) lastButton = "right";
    else if (p.MiddlePressed) lastButton = "middle";
    if (p.Hovering) lastModifiers = p.Modifiers;

    pad.FillX().Px(height: 96f);
    pad.ChildLayoutAxis = LayoutAxis.Y;
    pad.Style.CornerRadius = 10f;
    pad.Style.Padding = EdgeInsets.All(12f);
    pad.Style.BorderWidth = 1f;
    pad.Style.BorderColor = p.Hovering ? UiColor.Accent : UiColor.Border;
    pad.Style.BackgroundColor = UiColor.SurfaceRaised;
    using (runtime.PushParent(pad))
    {
        runtime.Text(p.Hovering ? $"pointer at {p.X:0.#}, {p.Y:0.#}" : "Move over this pad, press any button, scroll (Shift+wheel too)",
            ComponentSize.Sm);
        runtime.Gap(6f);
        runtime.Text($"last press: {lastButton}    wheel total: x {wheelX:0.#} px, y {wheelY:0.#} px (a notch is 32 px)",
            ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(6f);
        runtime.Text($"modifiers with the last event: {lastModifiers}", ComponentSize.Sm, TextColor.Secondary);
    }
    runtime.Gap(16f);

    runtime.Text($"frame {frame} · {runtime.SurfaceWidth}×{runtime.SurfaceHeight} at {runtime.ScaleFactor:0.###}×",
        ComponentSize.Sm, TextColor.Secondary);
}

// CI's start-up check: one frame built and drawn headless, then exit.
if (args.Contains("--first-frame"))
{
    using var cpu = new ManagedRenderer();
    runtime.BeginBuild();
    Build();
    runtime.EndBuild();
    FramePixels.Bgra(runtime, cpu.Draw, out _, out _);
    return 0;
}

if (!WaylandWindow.IsSupported)
{
    Console.Error.WriteLine("wayland-hello needs a Wayland session (WAYLAND_DISPLAY) and libwayland-client.");
    return 1;
}

// The renderer is named: the Wayland window brings none, and Native AOT keeps only what is named.
using var window = WaylandWindow.Open(runtime, new("bibui — wayland-hello", 760, 560),
    new WaylandHostOptions { AppId = "bibui-wayland-hello" });
using var managed = new ManagedRenderer(window.Cpu);
while (WaylandWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    Build();
    runtime.EndBuild();
    WaylandWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    WaylandWindow.EndDraw(window);
    WaylandWindow.EndFrame(window, runtime);
}
return 0;
