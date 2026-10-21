namespace Bibui.Core.Draw;

/// <summary>
/// SVG path data for a curated subset of Lucide icons (ISC licence; the icons
/// Lucide takes from Feather are MIT). The notices are in NOTICE.
/// All paths are defined on a 24×24 viewBox with strokeWidth=2, linecap=round,
/// linejoin=round. Rendered by scaling from 24px to the target icon size.
/// https://lucide.dev
///
/// An app adds icons of its own through <see cref="Bibui.Core.Icons.Register"/>;
/// they are kept apart from the built-in set, which they may not replace.
/// </summary>
internal static class IconRegistry
{
    private static readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // Navigation
        ["arrow-left"]     = "M19 12H5M5 12l7 7M5 12l7-7",
        ["arrow-right"]    = "M5 12h14M13 5l7 7-7 7",
        ["arrow-up"]       = "M12 19V5M5 12l7-7 7 7",
        ["arrow-down"]     = "M12 5v14M5 12l7 7 7-7",
        ["chevron-left"]   = "M15 18l-6-6 6-6",
        ["chevron-right"]  = "M9 18l6-6-6-6",
        ["chevron-up"]     = "M18 15l-6-6-6 6",
        ["chevron-down"]   = "M6 9l6 6 6-6",

        // Actions
        ["plus"]           = "M12 5v14M5 12h14",
        ["minus"]          = "M5 12h14",
        ["x"]              = "M18 6L6 18M6 6l12 12",
        ["check"]          = "M20 6L9 17l-5-5",
        ["trash"]          = "M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6",
        ["trash-2"]        = "M3 6h18M8 6V4h8v2M19 6l-1 14a2 2 0 01-2 2H9a2 2 0 01-2-2L6 6",
        ["edit"]           = "M11 4H4a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2v-7M18.5 2.5a2.121 2.121 0 013 3L12 15l-4 1 1-4 9.5-9.5z",
        ["pencil"]         = "M17 3a2.828 2.828 0 114 4L7.5 20.5 2 22l1.5-5.5L17 3z",
        ["copy"]           = "M8 17H5a2 2 0 01-2-2V5a2 2 0 012-2h10a2 2 0 012 2v3M13 21h6a2 2 0 002-2v-6a2 2 0 00-2-2h-6a2 2 0 00-2 2v6a2 2 0 002 2z",
        ["save"]           = "M19 21H5a2 2 0 01-2-2V5a2 2 0 012-2h11l5 5v11a2 2 0 01-2 2zM17 21v-8H7v8M7 3v5h8",
        ["upload"]         = "M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4M17 8l-5-5-5 5M12 3v12",
        ["download"]       = "M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4M7 10l5 5 5-5M12 15V3",
        ["search"]         = "M11 17.25a6.25 6.25 0 110-12.5 6.25 6.25 0 010 12.5zM21 21l-3.5-3.5",

        // Media
        ["play"]           = "M5 3l14 9-14 9V3z",
        ["pause"]          = "M6 4h4v16H6zM14 4h4v16h-4z",
        ["stop"]           = "M6 4h12v12H6z",
        ["skip-forward"]   = "M5 4l10 8-10 8V4zM19 5v14",
        ["skip-back"]      = "M19 20L9 12l10-8v16zM5 19V5",
        ["volume-2"]       = "M11 5L6 9H2v6h4l5 4V5zM19.07 4.93a10 10 0 010 14.14M15.54 8.46a5 5 0 010 7.07",

        // UI / misc
        ["settings"]       = "M12 2a10 10 0 100 20A10 10 0 0012 2zm0 6a4 4 0 100 8 4 4 0 000-8z",
        ["settings-2"]     = "M20 7h-9M14 17H5M17 17a3 3 0 100-6 3 3 0 000 6zM7 7a3 3 0 100 6 3 3 0 000-6z",
        ["sliders"]        = "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6",
        ["user"]           = "M20 21v-2a4 4 0 00-4-4H8a4 4 0 00-4 4v2M12 11a4 4 0 100-8 4 4 0 000 8z",
        ["users"]          = "M17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2M9 11a4 4 0 100-8 4 4 0 000 8zM23 21v-2a4 4 0 00-3-3.87M16 3.13a4 4 0 010 7.75",
        ["log-out"]        = "M9 21H5a2 2 0 01-2-2V5a2 2 0 012-2h4M16 17l5-5-5-5M21 12H9",
        ["log-in"]         = "M15 3h4a2 2 0 012 2v14a2 2 0 01-2 2h-4M10 17l5-5-5-5M15 12H3",
        ["home"]           = "M3 9l9-7 9 7v11a2 2 0 01-2 2H5a2 2 0 01-2-2zM9 22V12h6v10",
        ["menu"]           = "M3 12h18M3 6h18M3 18h18",
        ["more-horizontal"] = "M12 13a1 1 0 100-2 1 1 0 000 2zM19 13a1 1 0 100-2 1 1 0 000 2zM5 13a1 1 0 100-2 1 1 0 000 2z",
        ["more-vertical"]  = "M12 13a1 1 0 100-2 1 1 0 000 2zM12 6a1 1 0 100-2 1 1 0 000 2zM12 20a1 1 0 100-2 1 1 0 000 2z",
        ["external-link"]  = "M18 13v6a2 2 0 01-2 2H5a2 2 0 01-2-2V8a2 2 0 012-2h6M15 3h6v6M10 14L21 3",
        ["link"]           = "M10 13a5 5 0 007.54.54l3-3a5 5 0 00-7.07-7.07l-1.72 1.71M14 11a5 5 0 00-7.54-.54l-3 3a5 5 0 007.07 7.07l1.71-1.71",
        ["filter"]         = "M22 3H2l8 9.46V19l4 2v-8.54L22 3z",
        ["refresh-cw"]     = "M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0114.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0020.49 15",
        ["alert-circle"]   = "M12 22a10 10 0 100-20 10 10 0 000 20zM12 8v4M12 16h.01",
        ["info"]           = "M12 22a10 10 0 100-20 10 10 0 000 20zM12 16v-4M12 8h.01",
        ["check-circle"]   = "M22 11.08V12a10 10 0 11-5.93-9.14M22 4L12 14.01l-3-3",
        ["loader"]         = "M12 2v4M12 18v4M4.93 4.93l2.83 2.83M16.24 16.24l2.83 2.83M2 12h4M18 12h4M4.93 19.07l2.83-2.83M16.24 7.76l2.83-2.83",
        ["eye"]            = "M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8zM12 15a3 3 0 100-6 3 3 0 000 6z",
        ["eye-off"]        = "M17.94 17.94A10.07 10.07 0 0112 20c-7 0-11-8-11-8a18.45 18.45 0 015.06-5.94M9.9 4.24A9.12 9.12 0 0112 4c7 0 11 8 11 8a18.5 18.5 0 01-2.16 3.19M1 1l22 22",
        ["camera"]         = "M23 19a2 2 0 01-2 2H3a2 2 0 01-2-2V8a2 2 0 012-2h4l2-3h6l2 3h4a2 2 0 012 2zM12 17a4 4 0 100-8 4 4 0 000 8z",
        ["image"]          = "M21 19V5a2 2 0 00-2-2H5a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2zM8.5 10a1.5 1.5 0 100-3 1.5 1.5 0 000 3zM21 15l-5-5L5 21",
        ["file"]           = "M13 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V9zM13 2v7h7",
        ["folder"]         = "M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z",
        ["mail"]           = "M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2zM22 6l-10 7L2 6",
        ["send"]           = "M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z",
        ["star"]           = "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z",
        ["heart"]          = "M20.84 4.61a5.5 5.5 0 00-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 00-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 000-7.78z",
        ["sunrise"]        = "M17 18a5 5 0 00-10 0M12 2v7M4.22 10.22l1.42 1.42M1 18h2M21 18h2M18.36 11.64l1.42-1.42M23 22H1M16 5l-4-3-4 3",
        ["check-square"]   = "M9 11l3 3L22 4M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11",
        ["bell"]           = "M18 8A6 6 0 006 8c0 7-3 9-3 9h18s-3-2-3-9M13.73 21a2 2 0 01-3.46 0",
        ["calendar"]       = "M3 4h18v18H3zM16 2v4M8 2v4M3 10h18",
        ["clock"]          = "M12 22a10 10 0 100-20 10 10 0 000 20zM12 6v6l4 2",
        ["lock"]           = "M19 11H5a2 2 0 00-2 2v7a2 2 0 002 2h14a2 2 0 002-2v-7a2 2 0 00-2-2zM7 11V7a5 5 0 0110 0v4",
        ["unlock"]         = "M19 11H5a2 2 0 00-2 2v7a2 2 0 002 2h14a2 2 0 002-2v-7a2 2 0 00-2-2zM7 11V7a5 5 0 019.9-1",
        ["zap"]            = "M13 2L3 14h9l-1 8 10-12h-9l1-8z",
        ["grid"]           = "M3 3h7v7H3zM14 3h7v7h-7zM14 14h7v7h-7zM3 14h7v7H3z",
        ["tag"]            = "M20.59 13.41l-7.17 7.17a2 2 0 01-2.83 0L2 12V2h10l8.59 8.59a2 2 0 010 2.82zM7 7h.01",
        ["share"]          = "M4 12v8a2 2 0 002 2h12a2 2 0 002-2v-8M16 6l-4-4-4 4M12 2v13",
        ["maximize-2"]     = "M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7",
        ["minimize-2"]     = "M5 15H3v6h6v-2M19 9h2V3h-6v2M3 21l7-7M21 3l-7 7",

        // Sources and assistants
        ["sparkles"]       = "M9.94 15.5A2 2 0 008.5 14.06l-6.14-1.58a.5.5 0 010-.96L8.5 9.94A2 2 0 009.94 8.5l1.58-6.14a.5.5 0 01.96 0L14.06 8.5A2 2 0 0015.5 9.94l6.14 1.58a.5.5 0 010 .96L15.5 14.06a2 2 0 00-1.44 1.44l-1.58 6.14a.5.5 0 01-.96 0zM20 3v4M22 5h-4",
        ["message-circle"] = "M7.9 20A9 9 0 104 16.1L2 22z",
        ["git-branch"]     = "M6 3v12M18 9a3 3 0 100-6 3 3 0 000 6zM6 21a3 3 0 100-6 3 3 0 000 6zM18 9a9 9 0 01-9 9",
        ["graduation-cap"] = "M22 10L12 5 2 10l10 5 10-5zM22 10v6M6 12v5c3 3 9 3 12 0v-5",
        ["door-open"]      = "M13 4h3a2 2 0 012 2v14M2 20h3M13 20h9M10 12v.01M13 4.56v16.16a1 1 0 01-1.24.97L5 20V5.56a2 2 0 011.52-1.94l4-1A2 2 0 0113 4.56z",
        ["briefcase"]      = "M16 20V4a2 2 0 00-2-2h-4a2 2 0 00-2 2v16M4 6h16a2 2 0 012 2v10a2 2 0 01-2 2H4a2 2 0 01-2-2V8a2 2 0 012-2z",
        ["rotate-ccw"]     = "M3 12a9 9 0 109-9 9.75 9.75 0 00-6.74 2.74L3 8M3 3v5h5",
        ["file-text"]      = "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8zM14 2v6h6M16 13H8M16 17H8M10 9H8",
        ["square"]         = "M5 3h14a2 2 0 012 2v14a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2z",
        ["list-checks"]    = "M3 17l2 2 4-4M3 7l2 2 4-4M13 6h8M13 12h8M13 18h8",
    };

    // Icons an app registered (Icons.Register). Written rarely, at start-up, and
    // read every frame, so it is copy-on-write: a writer builds a new dictionary
    // under the lock and publishes it whole, and a reader takes the current one
    // with no lock at all.
    private static Dictionary<string, string> _app = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _appLock = new();

    /// <summary>Returns the SVG path data for the given icon name — built-in or registered — or null if not found.</summary>
    public static string? Get(string? name)
    {
        if (name is null) return null;
        if (_paths.TryGetValue(name, out var p)) return p;
        return Volatile.Read(ref _app).TryGetValue(name, out p) ? p : null;
    }

    /// <summary>True when <paramref name="name"/> is a built-in or registered icon.</summary>
    public static bool Has(string? name) =>
        name is not null && (_paths.ContainsKey(name) || Volatile.Read(ref _app).ContainsKey(name));

    /// <summary>True when <paramref name="name"/> is one of bibui's own icons.</summary>
    public static bool IsBuiltIn(string? name) => name is not null && _paths.ContainsKey(name);

    /// <summary>Every icon name: the built-in set, then the app's.</summary>
    public static IEnumerable<string> All => _paths.Keys.Concat(Volatile.Read(ref _app).Keys);

    /// <summary>
    /// Adds an app icon. The caller (<c>Icons.Register</c>) has validated the
    /// name and the path; this only decides a clash: a built-in name throws, the
    /// same name with the same path again is a no-op, with a different path throws.
    /// </summary>
    public static void Register(string name, string svgPathData)
    {
        if (_paths.ContainsKey(name))
            throw new ArgumentException($"'{name}' is one of bibui's own icons; an app icon needs a name of its own.", nameof(name));
        lock (_appLock)
        {
            var current = _app;
            if (current.TryGetValue(name, out var existing))
            {
                if (string.Equals(existing, svgPathData, StringComparison.Ordinal)) return;
                throw new ArgumentException($"An icon called '{name}' is already registered with other path data.", nameof(name));
            }
            var next = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase) { [name] = svgPathData };
            Volatile.Write(ref _app, next);
        }
    }
}
