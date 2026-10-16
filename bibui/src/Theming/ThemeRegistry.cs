namespace Bibui.Theming;

/// <summary>
/// The one table of theme names. Every API that takes a theme by name —
/// <see cref="ThemeLoader.Load"/>, <c>BibuiRuntime.SetTheme</c>,
/// <c>BibuiPage.Launch</c>, a fixture's <c>Theme</c> — resolves it here, so a
/// name that works in one works in all of them.
/// </summary>
/// <remarks>
/// <para>bibui's own themes are registered when the type is first touched. Code
/// outside bibui adds its own with <see cref="Register"/>, typically once at
/// start-up, before anything mounts by that name.</para>
/// <para>A name is registered once. Registering a name that is already taken —
/// a built-in one included — throws rather than replacing it: a replacement would
/// make a name mean whichever theme registered last, and code that loaded it
/// before the replacement would quietly disagree with code that loaded it after.
/// Names compare ordinally (case-sensitive).</para>
/// <para>All members are thread-safe. The factory runs on every load, outside
/// the registry's lock, so each caller gets its own <see cref="ThemeTokens"/>.</para>
/// </remarks>
public static class ThemeRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<ThemeTokens>> Factories = new(StringComparer.Ordinal);
    private static readonly List<string> Order = new();
    private static readonly string[] BuiltIn = ["bibui-dark", "bibui-light", "neutral-dark", "default-dark", "default-light"];

    // bibui's own themes are theme files embedded in the assembly, read through
    // the same ThemeFile as anyone else's — each the first time it is loaded,
    // so starting up reads none that is not used.
    static ThemeRegistry()
    {
        foreach (string name in BuiltIn)
        {
            var file = new Lazy<ThemeFile>(() => ThemeFile.BuiltIn(name));
            Register(name, () => file.Value.Resolve());
        }
    }

    /// <summary>
    /// Adds a theme under <paramref name="name"/>; <paramref name="factory"/> is
    /// called each time the name is loaded.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    public static void Register(string name, Func<ThemeTokens> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            if (!Factories.TryAdd(name, factory))
                throw new InvalidOperationException($"A theme named '{name}' is already registered.");
            Order.Add(name);
        }
    }

    /// <summary>
    /// Adds the theme <paramref name="file"/> describes under its
    /// <see cref="ThemeFile.Name"/>. Each load resolves it afresh, so the theme
    /// it extends may be registered after it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    public static void Register(ThemeFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Register(file.Name, file.Resolve);
    }

    /// <summary>
    /// Reads the theme file at <paramref name="path"/> (see <see cref="ThemeFile"/>)
    /// and adds it under its name, which it returns.
    /// </summary>
    /// <exception cref="FormatException">The file is not a theme; the message names the key and value.</exception>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    public static string RegisterFile(string path)
    {
        var file = ThemeFile.Load(path);
        Register(file);
        return file.Name;
    }

    /// <summary>The registered names, in the order they were registered (bibui's own first).</summary>
    public static IReadOnlyList<string> Names
    {
        get { lock (Gate) return Order.ToArray(); }
    }

    /// <summary>
    /// The themes bibui ships, in the order they are registered: the first
    /// <see cref="Names"/>, without any registered from outside bibui.
    /// </summary>
    public static IReadOnlyList<string> BuiltInNames => BuiltIn.AsReadOnly();

    /// <summary>Whether <paramref name="name"/> is registered.</summary>
    public static bool Contains(string name)
    {
        lock (Gate) return Factories.ContainsKey(name);
    }

    /// <summary>The theme registered as <paramref name="name"/>, freshly made.</summary>
    /// <exception cref="ArgumentException">No theme is registered under that name; the message lists those that are.</exception>
    public static ThemeTokens Load(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Func<ThemeTokens>? factory;
        string[]? names = null;
        lock (Gate)
        {
            if (!Factories.TryGetValue(name, out factory))
                names = Order.ToArray();
        }
        if (factory is null)
            throw new ArgumentException($"Unknown theme '{name}'. Registered: {string.Join(", ", names!)}.", nameof(name));
        return factory() ?? throw new InvalidOperationException($"The factory registered for theme '{name}' returned null.");
    }
}
