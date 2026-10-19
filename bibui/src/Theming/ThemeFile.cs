using System.Globalization;
using System.Text.Json;

namespace Bibui.Theming;

/// <summary>
/// A theme written as a JSON file, which lists only what it changes from the
/// theme it <c>extends</c>:
/// <code>
/// {
///   "name": "violet-teal",
///   "extends": "default-light",
///   "colors": { "accent": "#0D9488" }
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>The keys are the <see cref="ThemeTokens"/> properties in camelCase:
/// <c>colors</c> (<c>mode</c>, <c>ramps</c>, <c>extra</c> and the roles —
/// <c>background</c>, <c>surfaceRaised</c>, <c>accentForeground</c>, …),
/// <c>spacing</c>, <c>typography</c>, <c>radii</c>, <c>elevation</c>,
/// <c>borders</c>, <c>focusRing</c>, <c>density</c> and <c>motion</c>. A role is
/// a colour or a ramp step, as in code. A ramp is its eleven colours, or an
/// object of the steps it changes (<c>{ "600": "#0D9488" }</c>). An elevation
/// level is an object of <see cref="BoxShadow"/>'s fields, or <c>null</c> for
/// flat; its <c>color</c>, like <c>focusRing.color</c>, is a colour or a role
/// name (<c>"borderFocus"</c>). Enums are their names in lower case
/// (<c>"light"</c>, <c>"compact"</c>). Comments and trailing commas are allowed.</para>
/// <para><c>extends</c> names a registered theme (<see cref="ThemeRegistry"/>),
/// a file's or one made in code. What the file gives replaces the parent's
/// value one key at a time: a group the file mentions keeps every field it does
/// not, and so do <c>ramps</c>, <c>extra</c> and an elevation level. Without
/// <c>extends</c> the file starts from <c>new ThemeTokens()</c>.</para>
/// <para>A file is checked whole when it is read: an unknown key, a value of the
/// wrong kind or a colour that is not one throws a <see cref="FormatException"/>
/// naming the file, the key and the value. Its parent is looked up when it is
/// <see cref="Resolve">resolved</see>, so it may be registered after the file is
/// read; a missing parent or a ring of themes extending each other throws an
/// <see cref="InvalidOperationException"/> then.</para>
/// </remarks>
public sealed class ThemeFile
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // The files being resolved on this thread, outermost first: a parent that
    // is already among them is a ring.
    [ThreadStatic] private static List<ThemeFile>? _resolving;

    private readonly JsonElement _root;

    /// <summary>The theme's name: its <c>name</c> key, or the file's name without its extension.</summary>
    public string Name { get; }

    /// <summary>The registered theme this one starts from, or null to start from the defaults.</summary>
    public string? Extends { get; }

    /// <summary>Where the file came from, as errors name it: a path, or what <see cref="Parse"/> was told.</summary>
    public string Source { get; }

    private ThemeFile(JsonElement root, string name, string? extends, string source)
    {
        _root = root;
        Name = name;
        Extends = extends;
        Source = source;
    }

    /// <summary>Reads and checks the theme file at <paramref name="path"/>.</summary>
    /// <exception cref="FormatException">The file is not JSON, or not a theme; the message names the key and value.</exception>
    public static ThemeFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>
    /// Checks a theme file's UTF-8 text. <paramref name="sourceName"/> is what
    /// errors call it, and, without a <c>name</c> key, gives the theme its name.
    /// </summary>
    /// <exception cref="FormatException">The text is not JSON, or not a theme; the message names the key and value.</exception>
    public static ThemeFile Parse(ReadOnlySpan<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), Options);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new FormatException($"{sourceName}: not valid JSON: {e.Message}", e);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{sourceName}: a theme file is a JSON object, not {Show(root)}.");

        string? name = null, extends = null;
        if (root.TryGetProperty("name", out var n))
            name = n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
                ? s : throw new FormatException($"{sourceName}: name = {Show(n)} is not a name.");
        if (root.TryGetProperty("extends", out var x))
            extends = x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
                ? s : throw new FormatException($"{sourceName}: extends = {Show(x)} is not a theme name.");
        name ??= Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"{sourceName}: the theme has no name; give it a \"name\" key.");

        var file = new ThemeFile(root, name, extends, sourceName);
        // Every key and value is checked now, against the defaults, rather than
        // when the theme is first mounted: what a value means does not depend
        // on the parent it lands on.
        file.Apply(new ThemeTokens());
        return file;
    }

    /// <summary>
    /// The theme: its parent, loaded by name from <see cref="ThemeRegistry"/>,
    /// with this file's values over it. Each call makes a fresh one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The parent is not registered, or the themes extend each other in a ring.</exception>
    public ThemeTokens Resolve()
    {
        var resolving = _resolving ??= new();
        int at = resolving.IndexOf(this);
        if (at >= 0)
        {
            var ring = resolving.Skip(at).Append(this).Select(f => $"'{f.Name}' ({f.Source})");
            throw new InvalidOperationException($"Themes extend each other in a ring: {string.Join(" extends ", ring)}.");
        }
        resolving.Add(this);
        try
        {
            if (Extends is null) return Apply(new ThemeTokens());
            if (!ThemeRegistry.Contains(Extends))
                throw new InvalidOperationException(
                    $"{Source}: extends '{Extends}', which is not a registered theme. "
                    + $"Registered: {string.Join(", ", ThemeRegistry.Names)}.");
            return Apply(ThemeRegistry.Load(Extends));
        }
        finally
        {
            resolving.RemoveAt(resolving.Count - 1);
        }
    }

    /// <summary>One of bibui's own themes, from the file embedded in the assembly.</summary>
    internal static ThemeFile BuiltIn(string name)
    {
        string resource = $"Bibui.Assets.Themes.{name}.json";
        using var stream = typeof(ThemeFile).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"bibui's theme file '{resource}' is missing from the assembly.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var file = Parse(bytes, $"{name}.json (built in)");
        return file.Name == name ? file
            : throw new InvalidOperationException($"bibui's {name}.json names itself '{file.Name}'.");
    }

    // ── applying ────────────────────────────────────────────────────────────

    private ThemeTokens Apply(ThemeTokens t)
    {
        // Extra is the one mutable thing a theme holds; the child gets its own.
        t = t with { Name = Name, Colors = t.Colors with { Extra = new(t.Colors.Extra) } };
        foreach (var p in Members(_root, ""))
        {
            t = p.Name switch
            {
                "name" or "extends" => t,
                "colors" => t with { Colors = Colors(t.Colors, p.Value) },
                "spacing" => t with { Spacing = Spacing(t.Spacing, p.Value) },
                "typography" => t with { Typography = Typography(t.Typography, p.Value) },
                "radii" => t with { Radii = Radii(t.Radii, p.Value) },
                "elevation" => t with { Elevation = Elevation(t.Elevation, p.Value) },
                "borders" => t with { Borders = Borders(t.Borders, p.Value) },
                "focusRing" => t with { FocusRing = FocusRing(t.FocusRing, p.Value) },
                "density" => t with { Density = Density(t.Density, p.Value) },
                "motion" => t with { Motion = Motion(t.Motion, p.Value) },
                _ => throw Unknown("", p.Name,
                    "name, extends, colors, spacing, typography, radii, elevation, borders, focusRing, density, motion"),
            };
        }
        return t;
    }

    // The roles by their key, in the order ColorTokens declares them.
    private static readonly (string Key, Func<ColorTokens, string, ColorTokens> Set, Func<ColorTokens, string> Get)[] Roles =
    [
        ("background", (c, v) => c with { Background = v }, c => c.Background),
        ("surface", (c, v) => c with { Surface = v }, c => c.Surface),
        ("surfaceRaised", (c, v) => c with { SurfaceRaised = v }, c => c.SurfaceRaised),
        ("border", (c, v) => c with { Border = v }, c => c.Border),
        ("borderHover", (c, v) => c with { BorderHover = v }, c => c.BorderHover),
        ("borderFocus", (c, v) => c with { BorderFocus = v }, c => c.BorderFocus),
        ("textPrimary", (c, v) => c with { TextPrimary = v }, c => c.TextPrimary),
        ("textSecondary", (c, v) => c with { TextSecondary = v }, c => c.TextSecondary),
        ("textDisabled", (c, v) => c with { TextDisabled = v }, c => c.TextDisabled),
        ("accent", (c, v) => c with { Accent = v }, c => c.Accent),
        ("accentForeground", (c, v) => c with { AccentForeground = v }, c => c.AccentForeground),
        ("destructive", (c, v) => c with { Destructive = v }, c => c.Destructive),
        ("destructiveForeground", (c, v) => c with { DestructiveForeground = v }, c => c.DestructiveForeground),
        ("accentHover", (c, v) => c with { AccentHover = v }, c => c.AccentHover),
        ("accentActive", (c, v) => c with { AccentActive = v }, c => c.AccentActive),
        ("surfaceRaisedHover", (c, v) => c with { SurfaceRaisedHover = v }, c => c.SurfaceRaisedHover),
        ("surfaceRaisedActive", (c, v) => c with { SurfaceRaisedActive = v }, c => c.SurfaceRaisedActive),
        ("success", (c, v) => c with { Success = v }, c => c.Success),
        ("successForeground", (c, v) => c with { SuccessForeground = v }, c => c.SuccessForeground),
        ("successBg", (c, v) => c with { SuccessBg = v }, c => c.SuccessBg),
        ("warning", (c, v) => c with { Warning = v }, c => c.Warning),
        ("warningForeground", (c, v) => c with { WarningForeground = v }, c => c.WarningForeground),
        ("warningBg", (c, v) => c with { WarningBg = v }, c => c.WarningBg),
        ("info", (c, v) => c with { Info = v }, c => c.Info),
        ("infoForeground", (c, v) => c with { InfoForeground = v }, c => c.InfoForeground),
        ("infoBg", (c, v) => c with { InfoBg = v }, c => c.InfoBg),
    ];

    private static string RoleKeys => string.Join(", ", Roles.Select(r => r.Key));

    private ColorTokens Colors(ColorTokens c, JsonElement e)
    {
        foreach (var p in Members(e, "colors"))
        {
            string key = "colors." + p.Name;
            switch (p.Name)
            {
                case "mode": c = c with { Mode = Choice<ThemeMode>(key, p.Value) }; break;
                case "ramps": c = c with { Ramps = Ramps(c.Ramps, p.Value) }; break;
                case "extra":
                    foreach (var x in Members(p.Value, key))
                        c.Extra[x.Name] = Hex($"{key}.{x.Name}", x.Value);
                    break;
                default:
                    int role = Array.FindIndex(Roles, r => r.Key == p.Name);
                    if (role < 0) throw Unknown("colors", p.Name, "mode, ramps, extra, " + RoleKeys);
                    string value = String(key, p.Value);
                    try { c = Roles[role].Set(c, value); }
                    catch (ArgumentException ex)
                    {
                        throw new FormatException(
                            $"{Source}: {key} = {Show(p.Value)} is neither a colour (#RRGGBB, #RRGGBBAA) nor a ramp step "
                            + "(neutral-900, accent-500; steps are 50, 100, 200 … 900, 950).", ex);
                    }
                    break;
            }
        }
        return c;
    }

    private ColorRamps Ramps(ColorRamps r, JsonElement e)
    {
        foreach (var p in Members(e, "colors.ramps"))
        {
            string key = "colors.ramps." + p.Name;
            r = p.Name switch
            {
                "neutral" => r with { Neutral = Ramp(key, r.Neutral, p.Value) },
                "accent" => r with { Accent = Ramp(key, r.Accent, p.Value) },
                _ => throw Unknown("colors.ramps", p.Name, "neutral, accent"),
            };
        }
        return r;
    }

    // Eleven colours replace the ramp; an object of steps changes those steps.
    private ColorRamp Ramp(string key, ColorRamp ramp, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            if (e.GetArrayLength() != 11)
                throw Error($"{key} has {e.GetArrayLength()} colours; a ramp has 11, steps 50 to 950.");
            var hex = new string[11];
            int i = 0;
            foreach (var step in e.EnumerateArray())
            {
                hex[i] = Hex($"{key}[{i}]", step);
                i++;
            }
            return new ColorRamp(hex);
        }
        if (e.ValueKind != JsonValueKind.Object)
            throw Error($"{key} = {Show(e)} is neither eleven colours nor an object of steps.");
        foreach (var p in Members(e, key))
        {
            if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int step) || !ColorRamp.IsStep(step))
                throw Error($"{key}.{p.Name} is not a ramp step: steps are 50, 100, 200 … 900, 950.");
            ramp = ramp.With(step, Hex($"{key}.{p.Name}", p.Value));
        }
        return ramp;
    }

    private SpacingTokens Spacing(SpacingTokens s, JsonElement e)
    {
        float xs = s.Xs, sm = s.Sm, md = s.Md, lg = s.Lg, xl = s.Xl;
        foreach (var p in Members(e, "spacing"))
        {
            string key = "spacing." + p.Name;
            switch (p.Name)
            {
                case "xs": xs = Number(key, p.Value); break;
                case "sm": sm = Number(key, p.Value); break;
                case "md": md = Number(key, p.Value); break;
                case "lg": lg = Number(key, p.Value); break;
                case "xl": xl = Number(key, p.Value); break;
                default: throw Unknown("spacing", p.Name, "xs, sm, md, lg, xl");
            }
        }
        return new() { Xs = xs, Sm = sm, Md = md, Lg = lg, Xl = xl };
    }

    private TypographyTokens Typography(TypographyTokens t, JsonElement e)
    {
        float sizeSm = t.SizeSm, sizeMd = t.SizeMd, sizeLg = t.SizeLg, sizeXl = t.SizeXl, size2Xl = t.Size2Xl;
        int weightNormal = t.WeightNormal, weightSemiBold = t.WeightSemiBold;
        string? body = t.FontFamilyBody, display = t.FontFamilyDisplay, heading = t.FontFamilyHeading, mono = t.FontFamilyMono;
        foreach (var p in Members(e, "typography"))
        {
            string key = "typography." + p.Name;
            switch (p.Name)
            {
                case "sizeSm": sizeSm = Number(key, p.Value); break;
                case "sizeMd": sizeMd = Number(key, p.Value); break;
                case "sizeLg": sizeLg = Number(key, p.Value); break;
                case "sizeXl": sizeXl = Number(key, p.Value); break;
                case "size2Xl": size2Xl = Number(key, p.Value); break;
                case "weightNormal": weightNormal = Integer(key, p.Value); break;
                case "weightSemiBold": weightSemiBold = Integer(key, p.Value); break;
                case "fontFamilyBody": body = StringOrNull(key, p.Value); break;
                case "fontFamilyDisplay": display = StringOrNull(key, p.Value); break;
                case "fontFamilyHeading": heading = StringOrNull(key, p.Value); break;
                case "fontFamilyMono": mono = StringOrNull(key, p.Value); break;
                default:
                    throw Unknown("typography", p.Name, "sizeSm, sizeMd, sizeLg, sizeXl, size2Xl, weightNormal, "
                        + "weightSemiBold, fontFamilyBody, fontFamilyDisplay, fontFamilyHeading, fontFamilyMono");
            }
        }
        return new()
        {
            SizeSm = sizeSm, SizeMd = sizeMd, SizeLg = sizeLg, SizeXl = sizeXl, Size2Xl = size2Xl,
            WeightNormal = weightNormal, WeightSemiBold = weightSemiBold,
            FontFamilyBody = body, FontFamilyDisplay = display, FontFamilyHeading = heading, FontFamilyMono = mono,
        };
    }

    private RadiiTokens Radii(RadiiTokens r, JsonElement e)
    {
        float sm = r.Sm, md = r.Md, lg = r.Lg, full = r.Full;
        foreach (var p in Members(e, "radii"))
        {
            string key = "radii." + p.Name;
            switch (p.Name)
            {
                case "sm": sm = Number(key, p.Value); break;
                case "md": md = Number(key, p.Value); break;
                case "lg": lg = Number(key, p.Value); break;
                case "full": full = Number(key, p.Value); break;
                default: throw Unknown("radii", p.Name, "sm, md, lg, full");
            }
        }
        return new() { Sm = sm, Md = md, Lg = lg, Full = full };
    }

    private ElevationTokens Elevation(ElevationTokens t, JsonElement e)
    {
        BoxShadow? l1 = t.Level1, l2 = t.Level2, l3 = t.Level3, l4 = t.Level4;
        foreach (var p in Members(e, "elevation"))
        {
            string key = "elevation." + p.Name;
            switch (p.Name)
            {
                case "level1": l1 = Shadow(key, l1, p.Value); break;
                case "level2": l2 = Shadow(key, l2, p.Value); break;
                case "level3": l3 = Shadow(key, l3, p.Value); break;
                case "level4": l4 = Shadow(key, l4, p.Value); break;
                default: throw Unknown("elevation", p.Name, "level1, level2, level3, level4");
            }
        }
        return new() { Level1 = l1, Level2 = l2, Level3 = l3, Level4 = l4 };
    }

    // null is flat; an object changes the parent's shadow at that level, or a
    // new BoxShadow's defaults where the parent is flat.
    private BoxShadow? Shadow(string key, BoxShadow? s, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;
        s ??= new BoxShadow();
        float offsetX = s.OffsetX, offsetY = s.OffsetY, blur = s.Blur, spread = s.Spread;
        UiColor color = s.Color;
        foreach (var p in Members(e, key))
        {
            string k = key + "." + p.Name;
            switch (p.Name)
            {
                case "offsetX": offsetX = Number(k, p.Value); break;
                case "offsetY": offsetY = Number(k, p.Value); break;
                case "blur": blur = Number(k, p.Value); break;
                case "spread": spread = Number(k, p.Value); break;
                case "color": color = Color(k, p.Value); break;
                default: throw Unknown(key, p.Name, "offsetX, offsetY, blur, spread, color");
            }
        }
        return new() { OffsetX = offsetX, OffsetY = offsetY, Blur = blur, Spread = spread, Color = color };
    }

    private BorderTokens Borders(BorderTokens b, JsonElement e)
    {
        float width = b.Width, strong = b.Strong;
        foreach (var p in Members(e, "borders"))
        {
            string key = "borders." + p.Name;
            switch (p.Name)
            {
                case "width": width = Number(key, p.Value); break;
                case "strong": strong = Number(key, p.Value); break;
                default: throw Unknown("borders", p.Name, "width, strong");
            }
        }
        return new() { Width = width, Strong = strong };
    }

    private FocusRingTokens FocusRing(FocusRingTokens f, JsonElement e)
    {
        float width = f.Width, offset = f.Offset;
        UiColor color = f.Color;
        foreach (var p in Members(e, "focusRing"))
        {
            string key = "focusRing." + p.Name;
            switch (p.Name)
            {
                case "width": width = Number(key, p.Value); break;
                case "offset": offset = Number(key, p.Value); break;
                case "color": color = Color(key, p.Value); break;
                default: throw Unknown("focusRing", p.Name, "width, offset, color");
            }
        }
        return new() { Width = width, Offset = offset, Color = color };
    }

    private DensityTokens Density(DensityTokens d, JsonElement e)
    {
        var mode = d.Mode;
        float compact = d.CompactScale, comfortable = d.ComfortableScale;
        foreach (var p in Members(e, "density"))
        {
            string key = "density." + p.Name;
            switch (p.Name)
            {
                case "mode": mode = Choice<Density>(key, p.Value); break;
                case "compactScale": compact = Number(key, p.Value); break;
                case "comfortableScale": comfortable = Number(key, p.Value); break;
                default: throw Unknown("density", p.Name, "mode, compactScale, comfortableScale");
            }
        }
        return new() { Mode = mode, CompactScale = compact, ComfortableScale = comfortable };
    }

    private MotionTokens Motion(MotionTokens m, JsonElement e)
    {
        float hover = m.Hover, follow = m.Follow, toggle = m.Toggle, dflt = m.Default;
        var easing = m.Easing;
        foreach (var p in Members(e, "motion"))
        {
            string key = "motion." + p.Name;
            switch (p.Name)
            {
                case "hover": hover = Number(key, p.Value); break;
                case "follow" or "caret": follow = Number(key, p.Value); break;   // caret: its name before it was shared
                case "toggle": toggle = Number(key, p.Value); break;
                case "default": dflt = Number(key, p.Value); break;
                case "easing": easing = Choice<MotionEasing>(key, p.Value); break;
                default: throw Unknown("motion", p.Name, "hover, follow, toggle, default, easing");
            }
        }
        return new() { Hover = hover, Follow = follow, Toggle = toggle, Default = dflt, Easing = easing };
    }

    // ── values ──────────────────────────────────────────────────────────────

    // An object's members, refusing one given twice: JSON allows it, and the
    // second would quietly win.
    private IEnumerable<JsonProperty> Members(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw Error($"{path} = {Show(e)} is not an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name))
                throw Error($"{Key(path, p.Name)} is given twice.");
            yield return p;
        }
    }

    private float Number(string key, JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out float v) && float.IsFinite(v)
            ? v : throw Error($"{key} = {Show(e)} is not a number.");

    private int Integer(string key, JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v)
            ? v : throw Error($"{key} = {Show(e)} is not a whole number.");

    private string String(string key, JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw Error($"{key} = {Show(e)} is not a string.");

    private string? StringOrNull(string key, JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : String(key, e);

    private string Hex(string key, JsonElement e)
    {
        string v = String(key, e);
        return ColorRamp.IsHex(v) ? v : throw Error($"{key} = {Show(e)} is not a colour: write #RRGGBB or #RRGGBBAA.");
    }

    // A colour outside the roles — a shadow's, the focus ring's — is a hex, or
    // a role it follows at paint time.
    private UiColor Color(string key, JsonElement e)
    {
        string v = String(key, e);
        if (ColorRamp.IsHex(v)) return UiColor.Hex(v);
        foreach (var r in Roles)
            if (r.Key == v) return UiColor.Token(r.Get);
        throw Error($"{key} = {Show(e)} is neither a colour (#RRGGBB, #RRGGBBAA) nor a role ({RoleKeys}).");
    }

    private T Choice<T>(string key, JsonElement e) where T : struct, Enum
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            string v = e.GetString()!;
            foreach (var value in Enum.GetValues<T>())
                if (string.Equals(value.ToString(), v, StringComparison.OrdinalIgnoreCase)) return value;
        }
        throw Error($"{key} = {Show(e)} is not one of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
    }

    private FormatException Error(string message) => new($"{Source}: {message}");

    private FormatException Unknown(string path, string name, string known) =>
        Error($"unknown key '{Key(path, name)}'. {(path.Length == 0 ? "A theme" : path)} takes: {known}.");

    private static string Key(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static string Show(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        _ => e.GetRawText(),
    };
}
