// Zenith: an observatory console, and the showcase of what bibui can do.
//
//   dotnet run --project samples/showcase -c Release                                   # Silk host + Skia
//   dotnet run --project samples/showcase -c Release -- --host wayland --raster managed  # raw Wayland + managed raster
//   dotnet run --project samples/showcase -c Release -- --shots samples/showcase/screenshots
//                                                     # headless: the README's pictures, no window
//   dotnet run --project samples/showcase -c Release -- --png out.png --size 1280x800 --light
//   dotnet run --project samples/showcase -c Release -- --first-frame                   # time to the first frame
//
// Native AOT, the Wayland + managed route only (Silk and Skia left out of the binary):
//   dotnet publish samples/showcase -c Release -r linux-x64 -p:PublishAot=true -p:ShowcaseRoute=wayland

using System.Diagnostics;
using System.Globalization;
using Bibui.Core;
using Bibui.Platform.Wayland;
using Bibui.Core.Draw;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;
using Showcase;

long started = Stopwatch.GetTimestamp();

string? Arg(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
bool Has(string name) => args.Contains(name);

if (Has("--help") || Has("-h"))
{
    Console.WriteLine("""
        showcase: Zenith, an observatory console built with bibui.

          (no arguments)              open the window: Silk host, Skia raster
          --host silk|wayland         the window host
          --raster skia|managed       the rasterizer (managed: C#, FreeType/HarfBuzz text)
          --accent #RRGGBB            the seed colour the theme is generated from
          --light                     start in light mode
          --shots DIR [--all]         render the README's screenshots headlessly into DIR and exit
          --png FILE [--size WxH]     render one frame headlessly into FILE and exit
          --page overview|targets|telemetry|journal
          --first-frame               print the time to the first frame and exit
          --selftest                  drive the app headless through clicks and keys, check it, exit
        """);
    return 0;
}

#if WAYLAND_ONLY
string host = Arg("--host") ?? "wayland";
#else
string host = Arg("--host") ?? "silk";
#endif
string raster = Arg("--raster") ?? (host == "wayland" ? "managed" : "skia");
#if WAYLAND_ONLY
if (host != "wayland" || raster != "managed")
{
    Console.Error.WriteLine("This build carries only the Wayland host and the managed raster (ShowcaseRoute=wayland).");
    if (host != "wayland" && !Has("--shots") && !Has("--png") && !Has("--first-frame") && !Has("--selftest")) return 2;
    host = "wayland";
    raster = "managed";
}
#endif

// What draws a frame's list into pixels: the renderer --raster names.
DrawToPixels NewDraw() => raster switch
{
    "managed" => new ManagedRenderer().Draw,
#if !WAYLAND_ONLY
    "skia" => new Bibui.Raster.Skia.SkiaCpuRenderer().Draw,
#endif
    _ => throw new ArgumentException($"--raster {raster}: expected skia or managed"),
};

// The renderer --raster names, bound to memory a window shows: what to draw a
// frame with, and what to dispose when the window is done.
(IDisposable Owner, Action<DrawList> Draw) RendererFor(CpuTarget target)
{
    if (raster == "managed") { var managed = new ManagedRenderer(target); return (managed, managed.Draw); }
#if !WAYLAND_ONLY
    var skia = new Bibui.Raster.Skia.SkiaCpuRenderer(target);
    return (skia, skia.Draw);
#else
    throw new ArgumentException($"--raster {raster}: this build carries only the managed raster");
#endif
}

AppIcons.Register();
var themeFiles = RegisterThemeFiles();

App NewApp()
{
    var app = new App(themeFiles);
    if (Arg("--accent") is { } accent) app.Accent = accent;
    if (Has("--light")) app.Dark = false;
    if (Arg("--page") is { } page && Enum.TryParse<Page>(page, ignoreCase: true, out var p)) app.Page = p;
    return app;
}

if (Has("--first-frame"))
{
    var app = NewApp();
    app.Live = false;
    var rt = app.Headless(1440, 900);
    app.Frame(rt);
    _ = FramePixels.Bgra(rt, NewDraw(), out _, out _)[0];
    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"FIRST_FRAME main_ms={ms:F2} rss_mb={Environment.WorkingSet / 1048576.0:F1}"));
    return 0;
}

if (Has("--selftest"))
    return SelfTest.Run(NewApp);

if (Arg("--shots") is { } dir)
{
    Directory.CreateDirectory(dir);
    Screenshots.WriteAll(dir, NewDraw, raster, NewApp, all: Has("--all"));
    return 0;
}

if (Arg("--png") is { } png)
{
    var (w, h) = ParseSize(Arg("--size") ?? "1440x900");
    var app = NewApp();
    app.Live = false;
    File.WriteAllBytes(png, Screenshots.Render(NewDraw(), app, w, h, (_, _) => { }));
    Console.WriteLine($"wrote {png}");
    return 0;
}

// ── The window ──────────────────────────────────────────────────────────────
// The app's own loop: the window waits for input and shows frames, the app
// builds them, a renderer draws them.

var live = NewApp();
var runtime = BibuiRuntime.Create();
runtime.SetTheme(live.Theme());
var options = new PlatformWindowOptions("Zenith — bibui showcase", 1440, 900);

if (host == "wayland")
{
    if (!WaylandWindow.IsSupported)
    {
        Console.Error.WriteLine("--host wayland needs a Wayland session (WAYLAND_DISPLAY) and libwayland-client.");
        return 1;
    }
    using var window = WaylandWindow.Open(runtime, options, new WaylandHostOptions { AppId = "bibui-showcase", MinWidth = 640, MinHeight = 480 });
    var (renderer, draw) = RendererFor(window.Cpu);
    using (renderer)
    {
        while (WaylandWindow.BeginFrame(window, runtime))
        {
            live.Frame(runtime, window.Events);
            WaylandWindow.BeginDraw(window);
            draw(runtime.DrawList);
            WaylandWindow.EndDraw(window);
            WaylandWindow.EndFrame(window, runtime);
        }
    }
    return 0;
}

#if WAYLAND_ONLY
return 2;
#else
{
    using var window = SilkWindow.Open(runtime, options);
    // Skia on the window's GL; --raster managed draws on the CPU, into memory
    // shown on the window's GL framebuffer (Bibui.Gl).
    using var shown = raster == "skia" ? null : new Bibui.Gl.GlCpuFramebuffer(window.Gl);
    var (renderer, draw) = shown is null
        ? new Bibui.Raster.Skia.SkiaGlRenderer(window.Gl) is var skia ? ((IDisposable)skia, (Action<DrawList>)skia.Draw) : default
        : RendererFor(shown.Cpu);
    using (renderer)
    {
        window.OnRefresh = Frame;   // keeps drawing during a resize on Windows and macOS
        while (SilkWindow.BeginFrame(window, runtime)) Frame();
    }
    return 0;

    void Frame()
    {
        live.Frame(runtime, window.Events);
        SilkWindow.BeginDraw(window);
        shown?.BeginDraw();
        draw(runtime.DrawList);
        shown?.EndDraw();
        SilkWindow.EndDraw(window);
        SilkWindow.EndFrame(window, runtime);
    }
}
#endif

static (int W, int H) ParseSize(string s)
{
    var parts = s.Split('x');
    return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
}

// Theme files beside the binary: each is registered once, by the name inside it.
static List<string> RegisterThemeFiles()
{
    var names = new List<string>();
    string dir = Path.Combine(AppContext.BaseDirectory, "themes");
    if (!Directory.Exists(dir)) return names;
    foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
    {
        try
        {
            string name = ThemeRegistry.RegisterFile(file);
            _ = ThemeRegistry.Load(name);   // resolve now, so a broken file is reported at start-up
            names.Add(name);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"theme file skipped: {e.Message}");
        }
    }
    return names;
}
