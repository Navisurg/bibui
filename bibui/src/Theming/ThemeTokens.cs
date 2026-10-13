namespace Bibui.Theming;

public sealed record ThemeTokens
{
    public string Name { get; init; } = "";
    public ColorTokens Colors { get; init; } = new();
    public SpacingTokens Spacing { get; init; } = new();
    public TypographyTokens Typography { get; init; } = new();
    public RadiiTokens Radii { get; init; } = new();

    // The groups below are read by the runtime's components as they build —
    // runtime.Theme, the theme the frame is painted with — rather than at paint
    // time, because what they set (a Style's line width, a fixed height, an
    // ease's speed) is a number the painter and the solver take as given. Every
    // default reproduces what the components drew before the group existed.

    /// <summary>Shadows by level, for what floats over the page.</summary>
    public ElevationTokens Elevation { get; init; } = new();

    /// <summary>Outline and rule widths.</summary>
    public BorderTokens Borders { get; init; } = new();

    /// <summary>What a control that has the keyboard draws around itself.</summary>
    public FocusRingTokens FocusRing { get; init; } = new();

    /// <summary>How tightly controls are packed.</summary>
    public DensityTokens Density { get; init; } = new();

    /// <summary>How fast eased values travel, and how.</summary>
    public MotionTokens Motion { get; init; } = new();
}

/// <summary>Whether a theme is light text on dark or dark text on light.</summary>
public enum ThemeMode { Dark, Light }

/// <summary>
/// A theme's colours, in two layers: primitive palettes (<see cref="Ramps"/>,
/// a neutral and an accent ramp of steps 50 to 950) and the semantic roles
/// components paint with (<see cref="Background"/>, <see cref="Accent"/>, …).
///
/// <para>A role is set to either a colour, <c>"#RRGGBB"</c> or
/// <c>"#RRGGBBAA"</c>, or a ramp step, <c>"neutral-900"</c> or
/// <c>"accent-500"</c>. Reading it always gives the colour. A role nobody sets
/// takes the step its <see cref="Mode"/> gives it, so a theme that only sets
/// its ramps has every role; status colours (success, warning, info,
/// destructive) are not drawn from a ramp and default to fixed colours.</para>
///
/// <para>Replace one layer alone with <c>with</c>:
/// <c>colors with { Ramps = colors.Ramps with { Accent = teal } }</c> recolours
/// every role that names an accent step; <c>colors with { Accent = "#0EA5E9" }</c>
/// changes that role and nothing else.</para>
/// </summary>
public sealed record ColorTokens
{
    /// <summary>Which steps an unset role takes: dark surfaces and light text, or the reverse.</summary>
    public ThemeMode Mode { get; init; } = ThemeMode.Dark;

    /// <summary>The ramps a role names by step.</summary>
    public ColorRamps Ramps { get; init; } = new();

    /// <summary>The page behind everything.</summary>
    public string Background { get => Role(_background, "neutral-950", "neutral-50"); init => _background = CheckRole(value); }
    private readonly string? _background;

    /// <summary>A panel on the page.</summary>
    public string Surface { get => Role(_surface, "neutral-950", "neutral-50"); init => _surface = CheckRole(value); }
    private readonly string? _surface;

    /// <summary>A card, a field, a menu: what stands on a surface.</summary>
    public string SurfaceRaised { get => Role(_surfaceRaised, "neutral-900", "neutral-100"); init => _surfaceRaised = CheckRole(value); }
    private readonly string? _surfaceRaised;

    /// <summary>An outline or a rule.</summary>
    public string Border { get => Role(_border, "neutral-800", "neutral-200"); init => _border = CheckRole(value); }
    private readonly string? _border;

    /// <summary>An outline under the pointer.</summary>
    public string BorderHover { get => Role(_borderHover, "neutral-600", "neutral-300"); init => _borderHover = CheckRole(value); }
    private readonly string? _borderHover;

    /// <summary>An outline with the keyboard.</summary>
    public string BorderFocus { get => Role(_borderFocus, "accent-400", "accent-500"); init => _borderFocus = CheckRole(value); }
    private readonly string? _borderFocus;

    /// <summary>Reading text.</summary>
    public string TextPrimary { get => Role(_textPrimary, "neutral-50", "neutral-950"); init => _textPrimary = CheckRole(value); }
    private readonly string? _textPrimary;

    /// <summary>Supporting text: a description, a placeholder.</summary>
    public string TextSecondary { get => Role(_textSecondary, "neutral-400", "neutral-600"); init => _textSecondary = CheckRole(value); }
    private readonly string? _textSecondary;

    /// <summary>Text on a control that takes nothing.</summary>
    public string TextDisabled { get => Role(_textDisabled, "neutral-500", "neutral-400"); init => _textDisabled = CheckRole(value); }
    private readonly string? _textDisabled;

    /// <summary>The filled control, the selection.</summary>
    public string Accent { get => Role(_accent, "accent-500", "accent-600"); init => _accent = CheckRole(value); }
    private readonly string? _accent;

    /// <summary>Text and icons on <see cref="Accent"/>.</summary>
    public string AccentForeground { get => Role(_accentForeground, "neutral-50", "neutral-50"); init => _accentForeground = CheckRole(value); }
    private readonly string? _accentForeground;

    public string Destructive { get => Role(_destructive, "#DC2626", "#DC2626"); init => _destructive = CheckRole(value); }
    private readonly string? _destructive;

    public string DestructiveForeground { get => Role(_destructiveForeground, "#FFFFFF", "#FFFFFF"); init => _destructiveForeground = CheckRole(value); }
    private readonly string? _destructiveForeground;

    /// <summary><see cref="Accent"/> under the pointer.</summary>
    public string AccentHover { get => Role(_accentHover, "accent-400", "accent-700"); init => _accentHover = CheckRole(value); }
    private readonly string? _accentHover;

    /// <summary><see cref="Accent"/> pressed.</summary>
    public string AccentActive { get => Role(_accentActive, "accent-600", "accent-800"); init => _accentActive = CheckRole(value); }
    private readonly string? _accentActive;

    /// <summary><see cref="SurfaceRaised"/> under the pointer.</summary>
    public string SurfaceRaisedHover { get => Role(_surfaceRaisedHover, "neutral-800", "neutral-200"); init => _surfaceRaisedHover = CheckRole(value); }
    private readonly string? _surfaceRaisedHover;

    /// <summary><see cref="SurfaceRaised"/> pressed.</summary>
    public string SurfaceRaisedActive { get => Role(_surfaceRaisedActive, "neutral-700", "neutral-300"); init => _surfaceRaisedActive = CheckRole(value); }
    private readonly string? _surfaceRaisedActive;

    public string Success { get => Role(_success, "#22C55E", "#16A34A"); init => _success = CheckRole(value); }
    private readonly string? _success;

    public string SuccessForeground { get => Role(_successForeground, "#052E16", "#F0FDF4"); init => _successForeground = CheckRole(value); }
    private readonly string? _successForeground;

    public string SuccessBg { get => Role(_successBg, "#0E2A17", "#DCFCE7"); init => _successBg = CheckRole(value); }
    private readonly string? _successBg;

    public string Warning { get => Role(_warning, "#EAB308", "#CA8A04"); init => _warning = CheckRole(value); }
    private readonly string? _warning;

    public string WarningForeground { get => Role(_warningForeground, "#1F1500", "#FEFCE8"); init => _warningForeground = CheckRole(value); }
    private readonly string? _warningForeground;

    public string WarningBg { get => Role(_warningBg, "#2A210E", "#FEF9C3"); init => _warningBg = CheckRole(value); }
    private readonly string? _warningBg;

    public string Info { get => Role(_info, "#3B82F6", "#2563EB"); init => _info = CheckRole(value); }
    private readonly string? _info;

    public string InfoForeground { get => Role(_infoForeground, "#0B1A33", "#EFF6FF"); init => _infoForeground = CheckRole(value); }
    private readonly string? _infoForeground;

    public string InfoBg { get => Role(_infoBg, "#0E1A2A", "#DBEAFE"); init => _infoBg = CheckRole(value); }
    private readonly string? _infoBg;

    /// <summary>Colours a theme names beyond its roles, keyed by its own token names.</summary>
    public Dictionary<string, string> Extra { get; init; } = new();

    private string Role(string? set, string dark, string light) =>
        Resolve(set ?? (Mode == ThemeMode.Light ? light : dark));

    /// <summary>
    /// <paramref name="value"/> as a colour: itself when it is one, the step it
    /// names when it names one. Used when a role is read, so it allocates nothing.
    /// </summary>
    private string Resolve(string value)
    {
        if (value[0] == '#') return value;
        int dash = value.IndexOf('-');
        var step = int.Parse(value.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture);
        return RampNamed(value.AsSpan(0, dash))[step];
    }

    private ColorRamp RampNamed(ReadOnlySpan<char> name) =>
        name.SequenceEqual("neutral") ? Ramps.Neutral : Ramps.Accent;

    private static string CheckRole(string value, [System.Runtime.CompilerServices.CallerMemberName] string role = "")
    {
        ArgumentNullException.ThrowIfNull(value, role);
        if (value.StartsWith('#'))
        {
            ColorRamp.CheckHex(value, role);
            return value;
        }
        int dash = value.IndexOf('-');
        if (dash > 0
            && value.AsSpan(0, dash) is var ramp && (ramp.SequenceEqual("neutral") || ramp.SequenceEqual("accent"))
            && int.TryParse(value.AsSpan(dash + 1), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int step)
            && ColorRamp.IsStep(step))
            return value;
        throw new ArgumentException(
            $"{role} = '{value}' is neither a colour (#RRGGBB, #RRGGBBAA) nor a ramp step (neutral-900, accent-500; "
            + "steps are 50, 100, 200 … 900, 950).", role);
    }
}

public sealed class SpacingTokens
{
    public float Xs { get; init; } = 4f;
    public float Sm { get; init; } = 8f;
    public float Md { get; init; } = 12f;
    public float Lg { get; init; } = 16f;
    public float Xl { get; init; } = 24f;
}

public sealed class TypographyTokens
{
    public float SizeSm { get; init; } = 12f;
    public float SizeMd { get; init; } = 14f;
    public float SizeLg { get; init; } = 16f;
    public float SizeXl { get; init; } = 20f;
    public float Size2Xl { get; init; } = 24f;
    public int WeightNormal { get; init; } = 400;
    public int WeightSemiBold { get; init; } = 600;

    /// <summary>
    /// Default reading-text font family. Null (default) reproduces bibui's historical
    /// single-family behavior: a platform system-font probe, falling back to the built-in
    /// embedded Inter resource, falling back to <c>SKTypeface.Default</c>. Non-null resolves
    /// through <c>FontRegistry</c> first (a host app's own registered bytes), then a
    /// system-installed font matching that exact name, then the same fallback chain.
    /// </summary>
    public string? FontFamilyBody { get; init; }

    /// <summary>Display/hero-title face. Falls back to <see cref="FontFamilyBody"/> when null.</summary>
    public string? FontFamilyDisplay { get; init; }

    /// <summary>Section/card-heading face. Falls back to <see cref="FontFamilyBody"/> when null.</summary>
    public string? FontFamilyHeading { get; init; }

    /// <summary>Technical/monospace readout face. Falls back to <see cref="FontFamilyBody"/> when null.</summary>
    public string? FontFamilyMono { get; init; }
}

public sealed class RadiiTokens
{
    public float Sm { get; init; } = 4f;
    public float Md { get; init; } = 6f;
    public float Lg { get; init; } = 8f;
    public float Full { get; init; } = 9999f;
}

/// <summary>
/// How far a surface stands off what is beneath it, as the shadow it casts:
/// <see cref="Level0"/> flat, up to <see cref="Level4"/> for what floats
/// highest. A component picks a level by its role rather than a shadow:
/// a dropdown list, menu or popover floats at <see cref="Overlay"/>, a tooltip
/// at <see cref="Tooltip"/>, a dialog at <see cref="Modal"/>.
///
/// Every level defaults to flat — what the built-in themes have always drawn.
/// A theme that wants depth sets the levels, or takes <see cref="Soft"/>.
/// </summary>
public sealed class ElevationTokens
{
    /// <summary>Flat: on the surface it sits on. Always null.</summary>
    public BoxShadow? Level0 => null;
    public BoxShadow? Level1 { get; init; }
    public BoxShadow? Level2 { get; init; }
    public BoxShadow? Level3 { get; init; }
    public BoxShadow? Level4 { get; init; }

    /// <summary>The level a dropdown list, menu or popover floats at.</summary>
    public const int Overlay = 2;

    /// <summary>The level a tooltip floats at.</summary>
    public const int Tooltip = 3;

    /// <summary>The level a dialog's panel floats at.</summary>
    public const int Modal = 4;

    /// <summary>The shadow at <paramref name="level"/>, clamped to 0..4; null is flat.</summary>
    public BoxShadow? Level(int level) => level switch
    {
        <= 0 => Level0,
        1 => Level1,
        2 => Level2,
        3 => Level3,
        _ => Level4,
    };

    /// <summary>A soft, neutral scale, for a theme that wants depth.</summary>
    public static ElevationTokens Soft { get; } = new()
    {
        Level1 = new BoxShadow { OffsetY = 1f, Blur = 3f, Color = "#00000040" },
        Level2 = new BoxShadow { OffsetY = 4f, Blur = 12f, Color = "#00000059" },
        Level3 = new BoxShadow { OffsetY = 8f, Blur = 24f, Color = "#00000066" },
        Level4 = new BoxShadow { OffsetY = 16f, Blur = 40f, Color = "#00000073" },
    };
}

/// <summary>
/// How thick the lines a component draws are, in logical pixels: its outline,
/// and the rules and dividers inside it.
/// </summary>
public sealed class BorderTokens
{
    /// <summary>An outline, a rule, a divider.</summary>
    public float Width { get; init; } = 1f;

    /// <summary>An outline that marks the one chosen: a picked radio's ring.</summary>
    public float Strong { get; init; } = 2f;
}

/// <summary>
/// What a control draws around itself while it has the keyboard.
///
/// <see cref="Offset"/> is how far outside the control's edge the ring stands.
/// At 0 the ring <em>is</em> the control's outline, drawn <see cref="Width"/>
/// thick in <see cref="Color"/> — what a focused text field has always done.
/// A ring standing clear of the edge is not drawn yet: until it is, a
/// component draws its ring as its outline whatever the offset says.
/// </summary>
public sealed class FocusRingTokens
{
    public float Width { get; init; } = 1f;
    public float Offset { get; init; }

    /// <summary>A colour role, resolved against the theme's colours at paint time.</summary>
    public UiColor Color { get; init; } = UiColor.BorderFocus;
}

/// <summary>How tightly controls are packed: see <see cref="DensityTokens"/>.</summary>
public enum Density { Compact, Default, Comfortable }

/// <summary>
/// How tightly controls are packed, as one number.
///
/// The model: density is vertical rhythm. <see cref="Scale"/> multiplies a
/// control's fixed height (a toggle's 36 px, a radio row's 32) and the padding
/// above and below its content (a button's, a field's, a menu row's). It
/// leaves text size, horizontal padding, gaps, radii and line widths alone, so
/// a compact form keeps its type and its outlines and only loses air.
///
/// A component passes each such length through <see cref="Of"/>. At
/// <see cref="Density.Default"/> the scale is exactly 1, so every length is
/// exactly what it was.
/// </summary>
public sealed class DensityTokens
{
    public Density Mode { get; init; } = Density.Default;

    /// <summary>What <see cref="Density.Compact"/> scales by.</summary>
    public float CompactScale { get; init; } = 0.8f;

    /// <summary>What <see cref="Density.Comfortable"/> scales by.</summary>
    public float ComfortableScale { get; init; } = 1.25f;

    /// <summary>What a fixed height or a vertical padding is multiplied by.</summary>
    public float Scale => Mode switch
    {
        Density.Compact => CompactScale,
        Density.Comfortable => ComfortableScale,
        _ => 1f,
    };

    /// <summary><paramref name="pixels"/> at this density.</summary>
    public float Of(float pixels) => pixels * Scale;
}

/// <summary>How eased values travel: see <see cref="MotionTokens"/>.</summary>
public enum MotionEasing
{
    /// <summary>
    /// A fixed fraction of the remaining gap per unit of time: quick at first,
    /// settling softly, the same curve at any frame rate.
    /// </summary>
    Exponential,

    /// <summary>
    /// No travel: an eased value is at its target the frame after it is set —
    /// for a reduced-motion preference.
    /// </summary>
    None,
}

/// <summary>
/// How fast things move. A speed is halvings per second: an eased value
/// closes half of what is left of its gap every 1/speed seconds, so it comes
/// within <c>e</c> of a unit gap in about <c>log2(1/e) / speed</c> seconds —
/// at 20, within a hundredth in a third of a second.
/// </summary>
public sealed class MotionTokens
{
    /// <summary>Hover and press: how fast a box goes hot, and comes back.</summary>
    public float Hover { get; init; } = 60f;

    /// <summary>A text caret gliding to where the cursor went.</summary>
    public float Caret { get; init; } = 60f;

    /// <summary>A control crossing between two states: a switch's knob.</summary>
    public float Toggle { get; init; } = 22f;

    /// <summary><c>runtime.Animate</c> when it is given no speed of its own.</summary>
    public float Default { get; init; } = 20f;

    /// <summary>The curve every eased value travels along.</summary>
    public MotionEasing Easing { get; init; } = MotionEasing.Exponential;
}
