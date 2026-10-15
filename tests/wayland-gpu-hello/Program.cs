// wayland-gpu-hello: the raw Wayland host with Skia on the GPU.
//
//   dotnet run --project tests/wayland-gpu-hello -c Release              # animates, on the GPU
//   BIBUI_GPU=0 dotnet run --project tests/wayland-gpu-hello -c Release  # the same on the CPU, into wl_shm
//   BIBUI_GPU_LOG=1 BIBUI_PROFILE_FRAMES=600 BIBUI_ALWAYS_RENDER=1 dotnet run --project tests/wayland-gpu-hello -c Release
//                                                                          # says which path and renderer, 600 frames, closes
//   dotnet run --project tests/wayland-gpu-hello -c Release -- --first-frame   # headless: one frame, exits
//   dotnet run --project tests/wayland-gpu-hello -c Release -- --shot FILE.png # reads frame 30 back from the
//                                                                          # window's framebuffer, writes it, closes
//
// The whole of "Skia on the GPU" is the backend the runtime is made with:
//   new BibuiWindow(new BibuiRuntime(new SkiaRasterBackend()), new WaylandPlatformHost(...))
// The host sees a backend that draws through GL and sets EGL up on the window;
// with the managed backend, BIBUI_GPU=0, or no working EGL it draws into wl_shm.
//
// Native AOT: dotnet publish tests/wayland-gpu-hello -c Release -r linux-x64 -p:PublishAot=true

using Bibui;
using Bibui.Platform.Wayland;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using static Bibui.Arrange;
using Button = Bibui.Runtime.Button;

int clicks = 0;
long frame = 0;
var runtime = new BibuiRuntime(new SkiaRasterBackend());
var host = new WaylandPlatformHost(new WaylandHostOptions { AppId = "bibui-wayland-gpu-hello" });
// --shot: the frame as the window drew it, read back from its framebuffer (GPU memory on the GPU path).
string? shot = args.SkipWhile(a => a != "--shot").Skip(1).FirstOrDefault();
runtime.CapturePixels = shot is not null;

runtime.Mount(() =>
{
    frame++;
    if (shot is not null && frame == 31 && runtime.LastOutput is { } drawn)
    {
        File.WriteAllBytes(shot, drawn.PngBytes);
        Console.Error.WriteLine($"wrote {shot}: {drawn.Width}x{drawn.Height}, {host.Presentation}, {host.GpuRenderer ?? "no GPU"}");
        host.Close();
    }
    runtime.RequestFrame(); // animate: the compositor's frame callbacks set the pace
    float t = (MathF.Sin(frame / 40f) + 1f) / 2f;

    var hello = Button.Build(runtime, "hello", clicks == 0 ? "Click me" : $"Clicked {clicks}×");
    if (hello.Clicked) clicks++;

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

    string path = host.Presentation == HostPresentation.GpuSurface
        ? $"GPU: {host.GpuRenderer}"
        : "CPU: Skia into wl_shm buffers";
    return Column(
            runtime.Text("Skia on the GPU, on raw Wayland", ComponentSize.Xxl),
            Space(16f),
            runtime.Text(path, textColor: TextColor.Secondary),
            Space(16f),
            hello.Node,
            Space(16f),
            groove,
            Space(16f),
            runtime.Text($"frame {frame} · {runtime.SurfaceWidth}×{runtime.SurfaceHeight} at {runtime.ScaleFactor:0.###}×",
                ComponentSize.Sm, TextColor.Secondary))
        .FillX().FillY()
        .Styled(new Style { BackgroundColor = UiColor.Background, Padding = EdgeInsets.All(28f) });
}, new SurfaceOptions { Width = 640, Height = 320, ScaleFactor = 1f });

if (args.Contains("--first-frame"))
{
    runtime.Render();
    return 0;
}
if (!WaylandPlatformHost.IsSupported)
{
    Console.Error.WriteLine("wayland-gpu-hello needs a Wayland session (WAYLAND_DISPLAY) and libwayland-client.");
    return 1;
}
new BibuiWindow(runtime, host).Run("bibui — wayland-gpu-hello");
return 0;

