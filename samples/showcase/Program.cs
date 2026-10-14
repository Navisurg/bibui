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
using Bibui;
using Bibui.Platform.Wayland;
using Bibui.Raster;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using Bibui.Theming;
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

IRasterBackend NewBackend() => raster switch
{
    "managed" => new ManagedRasterBackend(),
#if !WAYLAND_ONLY
    "skia" => new Bibui.Raster.Skia.SkiaRasterBackend(),
#endif
    _ => throw new ArgumentException($"--raster {raster}: expected skia or managed"),
};

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
    var rt = new BibuiRuntime(NewBackend());
    rt.Mount(() => app.Build(rt), app.Theme(), new SurfaceOptions { Width = 1440, Height = 900, ScaleFactor = 1f });
    _ = rt.Render().RawBgraBytes[0];
    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"FIRST_FRAME main_ms={ms:F2} rss_mb={Environment.WorkingSet / 1048576.0:F1}"));
    return 0;
}

if (Has("--selftest"))
    return SelfTest.Run(NewBackend, NewApp);

if (Arg("--shots") is { } dir)
{
    Directory.CreateDirectory(dir);
    Screenshots.WriteAll(dir, NewBackend, NewApp, all: Has("--all"));
    return 0;
}

if (Arg("--png") is { } png)
{
    var (w, h) = ParseSize(Arg("--size") ?? "1440x900");
    var app = NewApp();
    app.Live = false;
    File.WriteAllBytes(png, Screenshots.Render(NewBackend(), app, w, h, (_, _) => { }));
    Console.WriteLine($"wrote {png}");
    return 0;
}

// ── The window ──────────────────────────────────────────────────────────────

var live = NewApp();
var runtime = new BibuiRuntime(NewBackend());
runtime.Mount(() => live.Build(runtime), live.Theme(), new SurfaceOptions { Width = 1440, Height = 900 });

IPlatformHost platform;
if (host == "wayland")
{
    if (!WaylandPlatformHost.IsSupported)
    {
        Console.Error.WriteLine("--host wayland needs a Wayland session (WAYLAND_DISPLAY) and libwayland-client.");
        return 1;
    }
    platform = new WaylandPlatformHost(new WaylandHostOptions { AppId = "bibui-showcase", MinWidth = 640, MinHeight = 480 });
}
else
{
#if WAYLAND_ONLY
    return 2;
#else
    platform = new SilkPlatformHost();
#endif
}
new BibuiWindow(runtime, platform).Run("Zenith — bibui showcase");
return 0;

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
