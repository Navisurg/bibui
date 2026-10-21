namespace Bibui.Core.Theming;

/// <summary>
/// Themes made from a few seeds rather than written out role by role:
/// <c>Theme.FromSeed("#0EA5E9")</c> is a whole theme — two ramps, every colour
/// role, radii and density — so an application has its own look in one line.
/// </summary>
public static class Theme
{
    /// <summary>The neutral seed a theme has unless it names one: a cool grey.</summary>
    public const string DefaultNeutralSeed = "#71717A";

    /// <summary>What text must reach against what it stands on: 4.5:1, for body text.</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>
    /// A theme generated from seeds.
    ///
    /// <para><paramref name="accent"/> is the brand colour. It is placed in the
    /// accent ramp exactly, at the step nearest its lightness, and the other
    /// steps run from it to near-white and near-black in its hue (see
    /// <see cref="AccentRamp"/>). The <see cref="ColorTokens.Accent"/> role is
    /// that step when text in the seed's colour reads on the page, and
    /// otherwise the nearest step that does — lighter in dark mode, darker in
    /// light — so a pale yellow stays yellow on a dark page and turns ochre on
    /// a light one.</para>
    ///
    /// <para><paramref name="neutral"/> tints the greys: its hue and a little of
    /// its chroma, never its lightness (see <see cref="NeutralRamp"/>), so the
    /// surfaces and text keep the contrast they are built for whichever grey is
    /// given.</para>
    ///
    /// <para>Every role is set, to a ramp step where it is drawn from one, so
    /// replacing a ramp afterwards still recolours it. Each text colour reaches
    /// <see cref="MinTextContrast"/> on the backgrounds it is drawn on, for any
    /// seed; disabled text is exempt and is only quieter.</para>
    /// </summary>
    /// <param name="accent">The brand colour, <c>#RRGGBB</c>.</param>
    /// <param name="neutral">The grey's tint, <c>#RRGGBB</c>; <see cref="DefaultNeutralSeed"/> when null.</param>
    /// <param name="radius">The corner of a control, in logical pixels: <see cref="RadiiTokens.Md"/>.
    /// <see cref="RadiiTokens.Sm"/> is two thirds of it and <see cref="RadiiTokens.Lg"/> four thirds,
    /// so 6 gives the 4 / 6 / 8 every theme has by default, and 0 gives square corners.</param>
    /// <param name="density">How tightly controls are packed.</param>
    /// <param name="mode">Light text on dark, or dark text on light.</param>
    /// <param name="name">The theme's name; <c>"seed-dark"</c> or <c>"seed-light"</c> when null.</param>
    /// <exception cref="ArgumentException">A seed is not a colour, or the radius is negative or not a number.</exception>
    public static ThemeTokens FromSeed(
        string accent,
        string? neutral = null,
        float radius = 6f,
        Density density = Density.Default,
        ThemeMode mode = ThemeMode.Dark,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(accent);
        if (!(radius >= 0f) || float.IsInfinity(radius))
            throw new ArgumentException($"A radius is a length of 0 or more; {radius} was given.", nameof(radius));

        var ramps = new ColorRamps { Neutral = NeutralRamp(neutral ?? DefaultNeutralSeed), Accent = AccentRamp(accent) };
        return new ThemeTokens
        {
            Name = name ?? (mode == ThemeMode.Light ? "seed-light" : "seed-dark"),
            Colors = Colors(ramps, StepNearest(accent), mode),
            Radii = new RadiiTokens { Sm = radius * 2f / 3f, Md = radius, Lg = radius * 4f / 3f },
            Density = new DensityTokens { Mode = density },
        };
    }

    /// <summary>
    /// The contrast ratio of two colours, from 1 (the same) to 21 (black on
    /// white); text wants <see cref="MinTextContrast"/> against what it is
    /// drawn on. Alpha is ignored: both are taken as opaque.
    /// </summary>
    public static double Contrast(string foreground, string background) => Oklch.Contrast(foreground, background);

    // The lightness each step stands at, 50 to 950. An accent ramp bends these
    // to meet its seed; a neutral ramp stands at them exactly, so its text and
    // surfaces always contrast as the role steps below assume.
    private static readonly double[] AccentLightness = [0.971, 0.936, 0.885, 0.808, 0.704, 0.620, 0.546, 0.488, 0.424, 0.379, 0.282];
    private static readonly double[] NeutralLightness = [0.985, 0.967, 0.920, 0.871, 0.705, 0.552, 0.442, 0.370, 0.274, 0.210, 0.141];

    // How much of a neutral seed's chroma its greys keep, at most: enough to
    // tint them, not enough to make the page a colour.
    private const double MaxNeutralChroma = 0.04;

    /// <summary>
    /// A ramp with <paramref name="seed"/> at the step nearest its lightness,
    /// exactly. The steps lighter than it run evenly up to near-white and the
    /// darker ones down to near-black, all in the seed's hue; chroma falls off
    /// towards both ends, faster towards white, and a step sRGB cannot show
    /// keeps its lightness and loses chroma.
    /// </summary>
    public static ColorRamp AccentRamp(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var s = Oklch.FromHex(seed);
        int k = StepNearest(s.L);
        var T = AccentLightness;
        var hex = new string[11];
        for (int i = 0; i < 11; i++)
        {
            if (i == k) { hex[i] = seed[..7].ToUpperInvariant(); continue; }
            double L, C;
            if (i < k)
            {
                // [T[k], T[0]] onto [seed, T[0]]: the light end stays where it is.
                double top = Math.Max(T[0], s.L);
                L = top - (T[0] - T[i]) / (T[0] - T[k]) * (top - s.L);
                C = s.C * (1 - 0.85 * (k - i) / (double)k);
            }
            else
            {
                double bottom = Math.Min(T[10], s.L);
                L = bottom + (T[i] - T[10]) / (T[k] - T[10]) * (s.L - bottom);
                C = s.C * (1 - 0.55 * (i - k) / (double)(10 - k));
            }
            hex[i] = new Oklch(L, C, s.H).ToHex();
        }
        return new ColorRamp(hex);
    }

    /// <summary>
    /// Greys in <paramref name="seed"/>'s hue, with at most a little of its
    /// chroma, at fixed lightnesses from near-white to near-black. The seed's
    /// own lightness is not used.
    /// </summary>
    public static ColorRamp NeutralRamp(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var s = Oklch.FromHex(seed);
        double chroma = Math.Min(s.C, MaxNeutralChroma);
        var hex = new string[11];
        for (int i = 0; i < 11; i++)
        {
            double L = NeutralLightness[i];
            // Most tint mid-ramp, least at white and black, where it would show as a cast.
            double C = chroma * (1 - 0.7 * Math.Abs(L - 0.55) / 0.45);
            hex[i] = new Oklch(L, C, s.H).ToHex();
        }
        return new ColorRamp(hex);
    }

    private static int StepNearest(string hex) => StepNearest(Oklch.FromHex(hex).L);

    private static int StepNearest(double lightness)
    {
        int best = 0;
        for (int i = 1; i < 11; i++)
            if (Math.Abs(AccentLightness[i] - lightness) < Math.Abs(AccentLightness[best] - lightness)) best = i;
        return best;
    }

    private static string Step(string ramp, int index) => $"{ramp}-{ColorRamp.Steps[index]}";

    private static ColorTokens Colors(ColorRamps ramps, int seedStep, ThemeMode mode)
    {
        bool dark = mode == ThemeMode.Dark;
        string N(int dk, int lt) => "neutral-" + (dark ? dk : lt);

        // The neutral roles stand at fixed steps of a ramp whose lightness is
        // fixed, so each text colour clears the bar on each surface it sits on.
        var colors = new ColorTokens
        {
            Mode = mode,
            Ramps = ramps,
            Background = N(950, 50),
            Surface = N(950, 50),
            SurfaceRaised = N(900, 100),
            SurfaceRaisedHover = N(800, 200),
            SurfaceRaisedActive = N(700, 300),
            Border = N(800, 200),
            BorderHover = N(600, 300),
            TextPrimary = N(50, 950),
            TextSecondary = N(400, 600),
            TextDisabled = N(500, 400),
        };

        // Status colours are not drawn from the seeds. Dark mode's defaults
        // already read under their foregrounds and on their tints; light mode's
        // success, warning and info fills are too pale for their near-white
        // labels, so they are taken a step darker here.
        if (!dark)
            colors = colors with { Success = "#15803D", Warning = "#A16207", Info = "#1D4ED8" };

        // The accent is the seed's own step where accent text reads on every
        // surface it may be drawn on, and otherwise the nearest step that does,
        // stepping away from the page: lighter on a dark one, darker on a light one.
        string[] surfaces = [colors.Background, colors.Surface, colors.SurfaceRaised];
        int away = dark ? -1 : 1;
        int a = seedStep;
        while (a + away is >= 0 and <= 10 && !Reads(ramps.Accent[ColorRamp.Steps[a]], surfaces)) a += away;

        // On the accent: whichever end of the neutral ramp contrasts more, or
        // pure white or black in its place where that end falls just short.
        string fill = ramps.Accent[ColorRamp.Steps[a]];
        bool lightForeground = Contrast(ramps.Neutral[50], fill) >= Contrast(ramps.Neutral[950], fill);
        string end = lightForeground ? "neutral-50" : "neutral-950";
        string foreground = Contrast(ramps.Neutral[lightForeground ? 50 : 950], fill) >= MinTextContrast ? end
            : lightForeground ? "#FFFFFF" : "#000000";

        // Hover and press move away from the foreground, one step and two, so
        // the label on them only gains contrast; at the ramp's end they turn back.
        int fromForeground = lightForeground ? 1 : -1;
        int hover = StepFrom(a, fromForeground, 1), active = StepFrom(a, fromForeground, 2);

        return colors with
        {
            Accent = Step("accent", a),
            AccentForeground = foreground,
            AccentHover = Step("accent", hover),
            AccentActive = Step("accent", active),
            BorderFocus = Step("accent", a),
        };
    }

    private static int StepFrom(int step, int direction, int by)
    {
        int to = step + direction * by;
        return to is >= 0 and <= 10 ? to : Math.Clamp(step - direction * by, 0, 10);
    }

    private static bool Reads(string foreground, string[] backgrounds)
    {
        foreach (var b in backgrounds)
            if (Contrast(foreground, b) < MinTextContrast) return false;
        return true;
    }
}
