using Bibui;
using Bibui.Theming;

/// <summary>
/// Theme files: bibui's own themes as the C# that defined them before
/// they became files, and a theme written out as one line per value, so two
/// themes compare value by value.
/// </summary>
static class ThemeFileTests
{
    // ThemeLoader's definitions as they were before theme files, verbatim.

    private static readonly ColorRamp Zinc = new(
        "#FAFAFA", "#F4F4F5", "#E4E4E7", "#D4D4D8", "#A1A1AA", "#71717A",
        "#52525B", "#3F3F46", "#27272A", "#18181B", "#09090B");

    private static readonly ColorRamp Violet = new(
        "#F5F3FF", "#EDE9FE", "#DDD6FE", "#C4B5FD", "#A78BFA", "#8B5CF6",
        "#7C3AED", "#6D28D9", "#5B21B6", "#4C1D95", "#2E1065");

    public static ThemeTokens NeutralDarkBefore() => new()
    {
        Name = "neutral-dark",
        Colors = new()
        {
            Ramps = new() { Neutral = Zinc },
            Background = "neutral-950",
            Surface = "neutral-950",
            SurfaceRaised = "neutral-900",
            Border = "neutral-800",
            BorderHover = "neutral-600",
            BorderFocus = "neutral-300",
            TextPrimary = "neutral-50",
            TextSecondary = "neutral-400",
            TextDisabled = "neutral-500",
            Accent = "neutral-50",
            AccentForeground = "neutral-900",
            Destructive = "#7F1D1D",
            DestructiveForeground = "#FEE2E2",
            AccentHover = "neutral-200",
            AccentActive = "neutral-300",
            SurfaceRaisedHover = "neutral-800",
            SurfaceRaisedActive = "neutral-700",
            Success = "#22C55E",
            SuccessForeground = "#052E16",
            SuccessBg = "#0E2A17",
            Warning = "#EAB308",
            WarningForeground = "#1F1500",
            WarningBg = "#2A210E",
            Info = "#3B82F6",
            InfoForeground = "#0B1A33",
            InfoBg = "#0E1A2A",
        },
        Spacing = new() { Xs = 4, Sm = 8, Md = 12, Lg = 16, Xl = 24 },
        Typography = new() { SizeSm = 12, SizeMd = 14, SizeLg = 16, SizeXl = 20, Size2Xl = 24, WeightNormal = 400 },
        Radii = new() { Sm = 4, Md = 6, Lg = 8, Full = 9999 },
    };

    public static ThemeTokens DefaultDarkBefore() => new()
    {
        Name = "default-dark",
        Colors = new()
        {
            Ramps = new() { Accent = Violet },
            Background = "#0D0D14",
            Surface = "#1E1E2E",
            SurfaceRaised = "#2A2A3D",
            Border = "#3A3A5C",
            BorderHover = "#5A5A8C",
            BorderFocus = "accent-600",
            TextPrimary = "#E0E0F0",
            TextSecondary = "#8888AA",
            TextDisabled = "#4A4A6A",
            Accent = "accent-600",
            AccentForeground = "#FFFFFF",
            Destructive = "#DC2626",
            DestructiveForeground = "#FFFFFF",
            AccentHover = "accent-500",
            AccentActive = "accent-700",
            SurfaceRaisedHover = "#36364E",
            SurfaceRaisedActive = "#42425C",
            Success = "#22C55E",
            SuccessForeground = "#052E16",
            SuccessBg = "#0E2A17",
            Warning = "#EAB308",
            WarningForeground = "#1F1500",
            WarningBg = "#2A210E",
            Info = "#3B82F6",
            InfoForeground = "#0B1A33",
            InfoBg = "#0E1A2A",
        },
        Spacing = new() { Xs = 4, Sm = 8, Md = 12, Lg = 16, Xl = 24 },
        Typography = new() { SizeSm = 12, SizeMd = 14, SizeLg = 16, SizeXl = 20, Size2Xl = 24, WeightNormal = 400 },
        Radii = new() { Sm = 4, Md = 6, Lg = 8, Full = 9999 },
    };

    public static ThemeTokens DefaultLightBefore() => new()
    {
        Name = "default-light",
        Colors = new()
        {
            Mode = ThemeMode.Light,
            Ramps = new() { Accent = Violet },
            Background = "#F8F8FF",
            Surface = "#FFFFFF",
            SurfaceRaised = "#F0F0FA",
            Border = "#D0D0E0",
            BorderHover = "#B0B0D0",
            BorderFocus = "accent-600",
            TextPrimary = "#0D0D14",
            TextSecondary = "#5A5A7A",
            TextDisabled = "#A0A0C0",
            Accent = "accent-600",
            AccentForeground = "#FFFFFF",
            Destructive = "#DC2626",
            DestructiveForeground = "#FFFFFF",
            AccentHover = "accent-700",
            AccentActive = "accent-800",
            SurfaceRaisedHover = "#E4E4F4",
            SurfaceRaisedActive = "#D8D8EC",
            Success = "#16A34A",
            SuccessForeground = "#F0FDF4",
            SuccessBg = "#DCFCE7",
            Warning = "#CA8A04",
            WarningForeground = "#FEFCE8",
            WarningBg = "#FEF9C3",
            Info = "#2563EB",
            InfoForeground = "#EFF6FF",
            InfoBg = "#DBEAFE",
        },
        Spacing = new() { Xs = 4, Sm = 8, Md = 12, Lg = 16, Xl = 24 },
        Typography = new() { SizeSm = 12, SizeMd = 14, SizeLg = 16, SizeXl = 20, Size2Xl = 24, WeightNormal = 400 },
        Radii = new() { Sm = 4, Md = 6, Lg = 8, Full = 9999 },
    };

    // Ramps whose every step is a colour no theme uses: a role resolved against
    // them shows which step it names, not only the colour it gives.
    private static readonly ColorRamp ProbeNeutral = new(
        "#000050", "#000100", "#000200", "#000300", "#000400", "#000500",
        "#000600", "#000700", "#000800", "#000900", "#000950");
    private static readonly ColorRamp ProbeAccent = new(
        "#AC0050", "#AC0100", "#AC0200", "#AC0300", "#AC0400", "#AC0500",
        "#AC0600", "#AC0700", "#AC0800", "#AC0900", "#AC0950");

    /// <summary><paramref name="t"/>, one line per value, in a fixed order.</summary>
    public static List<string> Dump(ThemeTokens t)
    {
        var lines = new List<string> { $"name {t.Name}" };
        var c = t.Colors;
        lines.Add($"colors.mode {c.Mode}");
        foreach (int step in ColorRamp.Steps)
            lines.Add($"colors.ramps.neutral.{step} {c.Ramps.Neutral[step]}  accent.{step} {c.Ramps.Accent[step]}");
        Roles(lines, "colors", c);
        Roles(lines, "colors (probe ramps)", c with { Ramps = new() { Neutral = ProbeNeutral, Accent = ProbeAccent } });
        foreach (var (k, v) in c.Extra.OrderBy(e => e.Key, StringComparer.Ordinal))
            lines.Add($"colors.extra.{k} {v}");

        var s = t.Spacing;
        lines.Add($"spacing {s.Xs} {s.Sm} {s.Md} {s.Lg} {s.Xl}");
        var y = t.Typography;
        lines.Add($"typography {y.SizeSm} {y.SizeMd} {y.SizeLg} {y.SizeXl} {y.Size2Xl} {y.WeightNormal} {y.WeightSemiBold} "
                  + $"[{y.FontFamilyBody}] [{y.FontFamilyDisplay}] [{y.FontFamilyHeading}] [{y.FontFamilyMono}]");
        var r = t.Radii;
        lines.Add($"radii {r.Sm} {r.Md} {r.Lg} {r.Full}");
        for (int level = 0; level <= 4; level++)
            lines.Add($"elevation.level{level} " + (t.Elevation.Level(level) is { } b
                ? $"{b.OffsetX} {b.OffsetY} {b.Blur} {b.Spread} {b.Color.Resolve(c)}"
                : "flat"));
        lines.Add($"borders {t.Borders.Width} {t.Borders.Strong}");
        lines.Add($"focusRing {t.FocusRing.Width} {t.FocusRing.Offset} {t.FocusRing.Color.Resolve(c)}");
        lines.Add($"density {t.Density.Mode} {t.Density.CompactScale} {t.Density.ComfortableScale}");
        var m = t.Motion;
        lines.Add($"motion {m.Hover} {m.Follow} {m.Toggle} {m.Default} {m.Easing}");
        return lines;
    }

    private static void Roles(List<string> lines, string prefix, ColorTokens c)
    {
        (string, string)[] roles =
        [
            ("background", c.Background), ("surface", c.Surface), ("surfaceRaised", c.SurfaceRaised),
            ("border", c.Border), ("borderHover", c.BorderHover), ("borderFocus", c.BorderFocus),
            ("textPrimary", c.TextPrimary), ("textSecondary", c.TextSecondary), ("textDisabled", c.TextDisabled),
            ("accent", c.Accent), ("accentForeground", c.AccentForeground),
            ("destructive", c.Destructive), ("destructiveForeground", c.DestructiveForeground),
            ("accentHover", c.AccentHover), ("accentActive", c.AccentActive),
            ("surfaceRaisedHover", c.SurfaceRaisedHover), ("surfaceRaisedActive", c.SurfaceRaisedActive),
            ("success", c.Success), ("successForeground", c.SuccessForeground), ("successBg", c.SuccessBg),
            ("warning", c.Warning), ("warningForeground", c.WarningForeground), ("warningBg", c.WarningBg),
            ("info", c.Info), ("infoForeground", c.InfoForeground), ("infoBg", c.InfoBg),
        ];
        foreach (var (key, value) in roles) lines.Add($"{prefix}.{key} {value}");
    }

    /// <summary>The lines of <paramref name="a"/> and <paramref name="b"/> that differ; empty when they are the same theme.</summary>
    public static List<string> Differences(ThemeTokens a, ThemeTokens b)
    {
        var x = Dump(a);
        var y = Dump(b);
        var diff = new List<string>();
        for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            string l = i < x.Count ? x[i] : "(none)", r = i < y.Count ? y[i] : "(none)";
            if (l != r) diff.Add($"{l}  ≠  {r}");
        }
        return diff;
    }

    /// <summary>A theme file's text, parsed under <paramref name="source"/>.</summary>
    public static ThemeFile Parse(string json, string source = "test.json") =>
        ThemeFile.Parse(System.Text.Encoding.UTF8.GetBytes(json), source);
}
