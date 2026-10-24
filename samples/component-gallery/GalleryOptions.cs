using Bibui.Core.Theming;

namespace ComponentGallery;

/// <summary>
/// The gallery's command line, parsed without opening anything. The window
/// and renderer are chosen at build time (<c>-p:App=...</c>, samples/BibuiApp.props).
/// </summary>
public sealed record GalleryOptions(
    int Port,
    bool Reference,
    bool Check,
    IReadOnlyList<(int Width, int Height)> Sizes,
    string? OutDir,
    bool Help,
    string? Accent = null,
    ThemeMode Mode = ThemeMode.Dark,
    bool FirstFrame = false)
{
    public const string Usage =
        """
        Usage: component-gallery [reference|check] [--size WxH]... [--out DIR]
                                 [--accent #RRGGBB [--mode dark|light]]
                                 [--port N] [--first-frame] [--help]

          (no arguments)      opens the gallery on the Bibui.App package it was built
                              with: SilkSkiaGl unless -p:App=... chose another
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
          --accent #RRGGBB    a theme generated from this one seed (Theme.FromSeed)
                              instead of neutral-dark; with reference, written to
                              reference-seed/ rather than over reference/
          --mode dark|light   the generated theme's mode (default dark); needs --accent
          --port N            the BibuiTestServer port (default 44334); the server
                              runs only with BIBUI_TEST_SERVER=1 in the environment
          --first-frame       render the first frame headless, and exit
          --help              print this and exit
        """;

    /// <summary>
    /// Parses <paramref name="args"/>. Returns null and sets <paramref name="error"/>
    /// when a value is unknown.
    /// Arguments it does not know are left alone, as before.
    /// </summary>
    public static GalleryOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        int port = 44334;
        bool reference = false, check = false, help = false, firstFrame = false;
        string? accent = null;
        ThemeMode? mode = null;
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
                case "--first-frame": firstFrame = true; break;
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
                case "--port":
                    if (i + 1 < args.Count && int.TryParse(args[i + 1], out int p)) { port = p; i++; }
                    break;
            }
        }

        if (mode is not null && accent is null)
        {
            error = "--mode chooses the mode of a theme generated from --accent; give --accent too.";
            return null;
        }

        return new GalleryOptions(port, reference, check, sizes, outDir, help,
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
}
