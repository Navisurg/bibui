// Zenith: an observatory console, and the showcase of what bibui can do.
//
//   dotnet run --project samples/showcase -c Release                                   # Silk + Skia on the GPU
//   dotnet run --project samples/showcase -c Release -p:App=WaylandManaged             # raw Wayland + managed raster
//   dotnet run --project samples/showcase -c Release -- --shots samples/showcase/screenshots
//                                                     # headless: the README's pictures, no window
//   dotnet run --project samples/showcase -c Release -- --png out.png --size 1280x800 --light
//   dotnet run --project samples/showcase -c Release -- --first-frame                   # time to the first frame
//
// The window and renderer are a Bibui.App package, chosen at build time (-p:App=...,
// samples/BibuiApp.props). Native AOT on Wayland + managed (no Silk, no Skia in the binary):
//   dotnet publish samples/showcase -c Release -r linux-x64 -p:PublishAot=true -p:App=WaylandManaged

using System.Diagnostics;
using System.Globalization;
using Bibui.App;
using Bibui.Core;
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

          (no arguments)              open the window, on the Bibui.App package built with
                                      (SilkSkiaGl unless -p:App=... chose another)
          --raster skia|managed       what draws the headless pictures (managed: C#,
                                      FreeType/HarfBuzz text; skia where the package brings it)
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

#if APP_SKIA
string raster = Arg("--raster") ?? "skia";
#else
string raster = Arg("--raster") ?? "managed";
#endif

// What draws a frame's list into pixels headless: the renderer --raster names.
DrawToPixels NewDraw() => raster switch
{
    "managed" => new ManagedRenderer().Draw,
#if APP_SKIA
    "skia" => new Bibui.Raster.Skia.SkiaCpuRenderer().Draw,
#endif
    _ => throw new ArgumentException($"--raster {raster}: expected skia or managed (skia only where the Bibui.App package brings it)"),
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
// The app's own loop on its Bibui.App package: the window waits for input and
// shows frames, the app builds them, the package's renderer draws them.

var live = NewApp();
var runtime = BibuiRuntime.Create();
runtime.SetTheme(live.Theme());
using var bibuiApp = BibuiApp.Open(runtime, new("Zenith — bibui showcase", 1440, 900),
    new BibuiAppOptions { AppId = "bibui-showcase", MinWidth = 640, MinHeight = 480 });
bibuiApp.Frame = Frame;
while (bibuiApp.WaitForFrame()) Frame();
return 0;

void Frame()
{
    live.Frame(runtime, bibuiApp.Window.Frame, bibuiApp.Events);
    bibuiApp.Present();
}

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
