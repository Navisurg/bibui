using System.Reflection;
using Bibui.Raster;

namespace Bibui.Renderer;

/// <summary>
/// Which typeface draws which text: resolves (family, weight) to a typeface and
/// splits a string into runs by glyph coverage. The policy is bibui's — embedded
/// Inter first, registered fonts, system fallback per codepoint — and the
/// typefaces come from the renderer's <see cref="IRasterBackend"/>.
/// </summary>
internal sealed class FontProvider
{
    private readonly IRasterBackend _backend;

    // Per-(family, weight) resolved-typeface cache. "" family key = today's platform-default
    // probe / embedded Inter / backend default fallback chain.
    private readonly Dictionary<(string Family, int Weight), RasterTypeface> _cache = new();

    // Per-codepoint fallback typeface cache. Key encodes (codepoint, weight) as a long
    // so we can store nulls (codepoints with no fallback found) and avoid re-querying
    // the system font matcher on every text measurement.
    private readonly Dictionary<long, RasterTypeface?> _fallbackCache = new();

    // Typeface runs, addressed by a content hash of (text, family, weight) — see RunsKey.
    // Touch-evicted rather than unbounded like the two dictionaries above, because its
    // keys come from arbitrary UI text rather than a fixed set of fonts: text that stops
    // being drawn falls out on its own. Made by Fonts, which is what ticks it —
    // this class has no EndFrame of its own to be called out of step.
    private readonly ResourceCache<RunsKey, TextRun> _runs;

    /// <summary>Run cache is created by, and evicted by, <see cref="Fonts"/>; typefaces come from <paramref name="backend"/>.</summary>
    internal FontProvider(IRasterBackend backend, ResourceCache<RunsKey, TextRun> runs)
    {
        _backend = backend;
        _runs = runs;
    }


    /// <summary>The default (no family) typeface at weight 400. Identical to historical behavior.</summary>
    public RasterTypeface Regular => Resolve(null, 400);

    /// <summary>
    /// The default (no family) typeface at weight 600. Named "Medium" for historical reasons —
    /// it has always actually loaded at SemiBold (600), not 500.
    /// </summary>
    public RasterTypeface Medium => Resolve(null, 600);

    /// <summary>
    /// Resolves a typeface for the given family/weight. Resolution order: an explicitly
    /// registered <see cref="FontRegistry"/> entry for (family, weight), then a system-installed
    /// then — when no family was named — the embedded Inter, then a system font
    /// matching the named family (or the platform probe), then the embedded Inter
    /// again for a named family that is not installed, then
    /// the backend's <see cref="IRasterBackend.DefaultTypeface"/>. Results are cached per (family, weight).
    ///
    /// The default path deliberately does not consult installed fonts: a render
    /// whose typeface depends on the machine is not reproducible.
    /// </summary>
    public RasterTypeface Resolve(string? family, int weight)
    {
        var key = (family ?? "", weight);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var tf = Load(family, weight);
        _cache[key] = tf;
        return tf;
    }

    private RasterTypeface Load(string? family, int weight)
    {
        if (family is not null)
        {
            var registered = FontRegistry.TryResolve(family, weight) is { } bytes
                ? _backend.LoadTypeface(bytes)
                : null;
            if (registered is not null) return registered;
        }

        // The embedded face wins over anything installed. Text is part of the
        // render, and "identical spec + identical binary = identical PNG" cannot
        // hold if the typeface is whatever the machine happens to have: this box
        // resolves Noto Sans, a CI runner resolves DejaVu Sans, and every
        // text-bearing baseline disagrees between them.
        //
        // A caller that names a family still gets it if it is installed -- that
        // is a deliberate ask, not a default.
        if (family is null)
        {
            var builtIn = TryLoadEmbedded(weight >= 600
                ? "Bibui.Assets.Fonts.Inter-SemiBold.ttf"
                : "Bibui.Assets.Fonts.Inter-Regular.ttf");
            if (builtIn is not null) return builtIn;
        }

        var systemFamily = family ?? PlatformFontFamily();
        var system = systemFamily is not null ? _backend.MatchFamily(systemFamily, weight) : null;
        if (system is not null) return system;

        // A named family that is not installed still falls back to the built-in.
        var embedded = TryLoadEmbedded(weight >= 600
            ? "Bibui.Assets.Fonts.Inter-SemiBold.ttf"
            : "Bibui.Assets.Fonts.Inter-Regular.ttf");
        if (embedded is not null) return embedded;

        return _backend.DefaultTypeface;
    }

    private RasterTypeface? TryLoadEmbedded(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return _backend.LoadTypeface(bytes);
    }

    private string? PlatformFontFamily()
    {
        // Prefer fonts with broad Unicode coverage (arrows, ellipsis, dingbats).
        // Return null to let the backend choose its default when no match is found.
        if (OperatingSystem.IsMacOS()) return "Helvetica Neue";
        if (OperatingSystem.IsLinux()) return FindLinuxSansFamily();
        if (OperatingSystem.IsWindows()) return "Segoe UI";
        return null;
    }

    private static readonly string[] s_linuxSansFamilies =
    [
        "Noto Sans",
        "DejaVu Sans",
        "Liberation Sans",
        "Ubuntu",
        "Cantarell",
        "Adwaita Sans",
        "FreeSans",
        "Arial",
    ];

    private string? FindLinuxSansFamily()
    {
        // Probe in order of preference; return the first family the backend can actually load.
        foreach (var family in s_linuxSansFamilies)
        {
            using var tf = _backend.MatchFamily(family, 400);
            if (tf is not null && tf.FamilyName == family)
                return family;
        }
        return null; // fall through to embedded fonts or the backend default
    }

    /// <summary>
    /// Returns a typeface that contains a glyph for <paramref name="codepoint"/>, or null
    /// if no installed font on the system covers it. Asks the backend once per
    /// (codepoint, weight) and caches the result — including nulls.
    /// </summary>
    private RasterTypeface? GetFallback(int codepoint, int weight)
    {
        long key = ((long)codepoint << 16) | (uint)weight;
        if (_fallbackCache.TryGetValue(key, out var cached)) return cached;

        var tf = _backend.MatchCharacter(codepoint, weight);
        _fallbackCache[key] = tf;
        return tf;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into contiguous runs by typeface coverage.
    /// Each code point is rendered in the base typeface (resolved from
    /// <paramref name="family"/>/<paramref name="weight"/> via <see cref="Resolve"/>) when
    /// supported, otherwise in a system fallback resolved by the backend. A run is a
    /// char range, so a code point is never split across runs.
    /// </summary>
    internal TextRun? SplitRuns(ReadOnlySpan<char> text, string? family, int weight)
    {
        if (text.IsEmpty) return null;
        var key = RunsKey.For(text, family, weight);
        return _runs.TryGet(key, out var runs) ? runs : _runs.Add(key, SplitRunsUncached(text, family, weight));
    }

    // The split itself: one glyph lookup into the backend per code point. That
    // call is the reason the result above is worth caching — it is charged for
    // the whole text on every measure, and the wrap loop measures the same
    // accumulating line once per word.
    private TextRun SplitRunsUncached(ReadOnlySpan<char> text, string? family, int weight)
    {
        var baseTf = Resolve(family, weight);
        TextRun? first = null, last = null;

        RasterTypeface? currentTf = null;
        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            System.Text.Rune.DecodeFromUtf16(text[i..], out var rune, out int len);
            len = Math.Max(1, len);

            // Pick the typeface for this code point:
            //   - If the base typeface has the glyph, always prefer base — even
            //     after a fallback run (otherwise chars after an emoji stick to
            //     the emoji typeface). Space lands here too, so spaces always
            //     render with the text typeface for symmetric spacing.
            //   - Otherwise, resolve a system fallback for the code point.
            RasterTypeface tfForCp = baseTf.HasGlyph(rune.Value)
                ? baseTf
                : (GetFallback(rune.Value, weight) ?? baseTf);

            if (currentTf is not null && !ReferenceEquals(currentTf, tfForCp))
            {
                Add(new TextRun(currentTf, start, i));
                start = i;
            }
            currentTf = tfForCp;
            i += len;
        }
        Add(new TextRun(currentTf!, start, text.Length));
        return first!;

        void Add(TextRun run) => last = first is null ? first = run : last!.Append(run);
    }
}
