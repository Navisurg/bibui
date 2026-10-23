using Bibui.Core.Theming;

namespace ComponentGallery;

/// <summary>The window host the gallery opens on.</summary>
public enum GalleryHost
{
    /// <summary>GLFW + OpenGL (Bibui.Platform.Silk), cross-platform. Draws through Skia's GL backend.</summary>
    Silk,
    /// <summary>Raw Wayland through libwayland-client (Bibui.Platform.Wayland), frames drawn on the CPU into wl_shm buffers.</summary>
    Wayland,
}

/// <summary>The raster backend the gallery draws with.</summary>
public enum GalleryRaster
{
    /// <summary>SkiaSharp (Bibui.Raster.Skia).</summary>
    Skia,
    /// <summary>The managed rasterizer with FreeType/HarfBuzz text (Bibui.Raster.Managed).</summary>
    Managed,
}

/// <summary>
/// The gallery's command line, parsed without opening anything, so the
/// choice of host and backend can be checked with <c>--dry-run</c>.
/// </summary>
public sealed record GalleryOptions(
    GalleryHost Host,
    GalleryRaster Raster,
    bool Reference,
    bool Check,
    IReadOnlyList<(int Width, int Height)> Sizes,
    string? OutDir,
    bool Help,
    bool DryRun,
    IReadOnlyList<string> Notices,
    string? Accent = null,
    ThemeMode Mode = ThemeMode.Dark,
    bool FirstFrame = false)
{
    public const string Usage =
        """
        Usage: component-gallery [reference|check] [--size WxH]... [--out DIR]
                                 [--host silk|wayland] [--raster skia|managed]
                                 [--accent #RRGGBB [--mode dark|light]]
                                 [--first-frame] [--dry-run] [--help]

          (no arguments)      opens the gallery on the Silk host (GLFW + GL) with Skia
          reference           renders every page, and its open state, to reference/
          check               lays every page, and its open state, out headlessly at each
                              --size (default 640x480 840x600 900x700 1100x820 1600x1000) and
                              reports what is cut, spills out of its box, or is off the
                              surface; exits 1 if anything is
          --size WxH          the surface size for reference or check, in logical pixels;
                              may be given more than once (reference: default 1100x820,
                              the window's initial size)
          --out DIR           where reference or check writes its pictures (check writes
                              none without it)
          --host silk         GLFW + OpenGL, cross-platform (default)
          --host wayland      raw Wayland (libwayland-client, wl_shm buffers)
          --raster skia       SkiaSharp (default)
          --raster managed    the managed rasterizer with FreeType/HarfBuzz text;
                              needs --host wayland (the Silk host draws through Skia's GL backend)
          --accent #RRGGBB    a theme generated from this one seed (Theme.FromSeed)
                              instead of neutral-dark; with reference, written to
                              reference-seed/ rather than over reference/
          --mode dark|light   the generated theme's mode (default dark); needs --accent
          --first-frame       render the first frame headless on the chosen backend, and exit
          --dry-run           print the host and backend that would be used, and exit
          --help              print this and exit

          --omarchy           deprecated: the same as --host wayland --raster managed
        """;

    /// <summary>
    /// Parses <paramref name="args"/>. Returns null and sets <paramref name="error"/>
    /// when a value is unknown or the host and backend cannot go together.
    /// Arguments it does not know are left alone, as before.
    /// </summary>
    public static GalleryOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        GalleryHost? host = null;
        GalleryRaster? raster = null;
        bool reference = false, check = false, help = false, dryRun = false, firstFrame = false, deprecatedAlias = false;
        string? accent = null;
        ThemeMode? mode = null;
        var notices = new List<string>();
        var sizes = new List<(int, int)>();
        string? outDir = null;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "reference": reference = true; break;
                case "check": check = true; break;
                case "--size":
                    if (i + 1 >= args.Count || ParseSize(args[++i]) is not { } size)
                    {
                        error = "--size needs a value WxH, e.g. 900x700.";
                        return null;
                    }
                    sizes.Add(size);
                    break;
                case "--out":
                    if (i + 1 >= args.Count) { error = "--out needs a directory."; return null; }
                    outDir = args[++i];
                    break;
                case "--help" or "-h": help = true; break;
                case "--dry-run": dryRun = true; break;
                case "--first-frame": firstFrame = true; break;
                case "--omarchy": deprecatedAlias = true; break;
                case "--host":
                    if (i + 1 >= args.Count) { error = "--host needs a value: silk or wayland."; return null; }
                    switch (args[++i])
                    {
                        case "silk": host = GalleryHost.Silk; break;
                        case "wayland": host = GalleryHost.Wayland; break;
                        default: error = $"Unknown --host '{args[i]}': expected silk or wayland."; return null;
                    }
                    break;
                case "--raster":
                    if (i + 1 >= args.Count) { error = "--raster needs a value: skia or managed."; return null; }
                    switch (args[++i])
                    {
                        case "skia": raster = GalleryRaster.Skia; break;
                        case "managed": raster = GalleryRaster.Managed; break;
                        default: error = $"Unknown --raster '{args[i]}': expected skia or managed."; return null;
                    }
                    break;
                case "--accent":
                    if (i + 1 >= args.Count || !IsColour(args[i + 1])) { error = "--accent needs a colour: #RRGGBB."; return null; }
                    accent = args[++i];
                    break;
                case "--mode":
                    if (i + 1 >= args.Count) { error = "--mode needs a value: dark or light."; return null; }
                    switch (args[++i])
                    {
                        case "dark": mode = ThemeMode.Dark; break;
                        case "light": mode = ThemeMode.Light; break;
                        default: error = $"Unknown --mode '{args[i]}': expected dark or light."; return null;
                    }
                    break;
            }
        }

        // --omarchy is kept so existing scripts and habits still work; it says
        // what to type instead, and refuses to contradict an explicit choice.
        if (deprecatedAlias)
        {
            if (host is GalleryHost.Silk || raster is GalleryRaster.Skia)
            {
                error = "--omarchy means --host wayland --raster managed, which contradicts the --host/--raster given.";
                return null;
            }
            host = GalleryHost.Wayland;
            raster = GalleryRaster.Managed;
            notices.Add("--omarchy is deprecated; use --host wayland --raster managed.");
        }

        var h = host ?? GalleryHost.Silk;
        var r = raster ?? GalleryRaster.Skia;
        if (h == GalleryHost.Silk && r == GalleryRaster.Managed)
        {
            error = "--raster managed needs --host wayland: the Silk host draws through Skia's GL backend.";
            return null;
        }

        if (mode is not null && accent is null)
        {
            error = "--mode chooses the mode of a theme generated from --accent; give --accent too.";
            return null;
        }

        return new GalleryOptions(h, r, reference, check, sizes, outDir, help, dryRun, notices,
            accent, mode ?? ThemeMode.Dark, firstFrame);
    }

    private static (int, int)? ParseSize(string value)
    {
        var parts = value.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
            && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    /// <summary>The theme the gallery is drawn in: generated from <see cref="Accent"/>, or neutral-dark.</summary>
    public ThemeTokens Theme() =>
        Accent is { } seed ? Bibui.Core.Theming.Theme.FromSeed(seed, mode: Mode) : ThemeLoader.Load("bibui-dark");

    private static bool IsColour(string s) =>
        s.Length == 7 && s[0] == '#' && s.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    /// <summary>A one-line description of the host and backend, for --dry-run.</summary>
    public string Describe() =>
        $"host: {Host.ToString().ToLowerInvariant()}, raster: {Raster.ToString().ToLowerInvariant()}"
        + (Accent is null ? "" : $", accent: {Accent}, mode: {Mode.ToString().ToLowerInvariant()}");
}
