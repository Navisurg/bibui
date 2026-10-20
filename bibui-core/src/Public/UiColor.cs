using Bibui.Core.Theming;

namespace Bibui.Core;

/// <summary>
/// A color value for <see cref="Style.BackgroundColor"/>, <see cref="Style.BorderColor"/>,
/// and scrim colors. Can hold either a literal hex string (e.g. "#18181B") or a
/// theme-token selector that resolves against the active <see cref="ColorTokens"/>
/// at render time.
///
/// Constructed implicitly from strings for backwards compatibility:
///   <c>BackgroundColor = "#18181B"</c>
/// or from theme tokens:
///   <c>BackgroundColor = UiColor.SurfaceRaised</c>
///   <c>BorderColor     = UiColor.Token(c =&gt; c.Border)</c>
/// </summary>
public readonly struct UiColor : IEquatable<UiColor>
{
    private readonly string? _hex;
    private readonly Func<ColorTokens, string>? _selector;

    // 0xRRGGBBAA, and the answer whenever neither of the above is set. Not
    // nullable, so the struct has a valid zero: a UiColor nobody assigned is
    // transparent black rather than a value that throws at paint.
    private readonly uint _rgba;
    private readonly float? _alpha;

    private UiColor(string? hex, Func<ColorTokens, string>? selector, uint rgba = 0, float? alpha = null)
    {
        _hex = hex;
        _selector = selector;
        _rgba = rgba;
        _alpha = alpha;
    }

    public static UiColor Hex(string hex) => new(Checked(hex), null);
    public static UiColor Token(Func<ColorTokens, string> selector) => new(null, selector);

    /// <summary>Numbers in, numbers out: <see cref="ResolveColor"/> hands these
    /// channels straight back, with no string in between.</summary>
    public static UiColor Rgba(byte r, byte g, byte b, byte a = 255)
        => new(null, null, ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a);

    /// <summary>Opaque, for the common case.</summary>
    public static UiColor Rgb(byte r, byte g, byte b) => Rgba(r, g, b);

    public static implicit operator UiColor(string hex) => new(Checked(hex), null);

    /// <summary>
    /// The conversion above makes a null easy to write by accident:
    /// <c>cond ? UiColor.Accent : null</c> against a <c>UiColor?</c> target
    /// types the null as <c>string</c> and converts, so the ternary is a colour
    /// where the author meant none. A blank you meant is <c>(UiColor?)null</c>;
    /// a blank you did not should say so rather than paint nothing quietly.
    /// </summary>
    private static string Checked(string hex) => hex ?? throw new ArgumentNullException(
        nameof(hex),
        "A UiColor needs a colour. For \"no colour\", write (UiColor?)null — a bare null in a "
        + "ternary converts through the string operator and becomes a colour you did not mean.");

    /// <summary>A text role names a colour, so it is one — and every existing
    /// <c>Color = TextColor.Secondary</c> keeps compiling.</summary>
    public static implicit operator UiColor(TextColor role) => role switch
    {
        TextColor.Secondary => TextSecondary,
        TextColor.Disabled => TextDisabled,
        TextColor.Accent => Accent,
        TextColor.AccentForeground => AccentForeground,
        _ => TextPrimary,
    };

    /// <summary>
    /// Returns a copy of this color with its alpha channel set to <paramref name="a"/> (0..1) at
    /// resolve time — works for both literal hex and theme-token colors, so e.g.
    /// <c>UiColor.Accent.WithAlpha(0.14f)</c> stays token-linked instead of degrading to a
    /// hand-picked literal hex that can drift from the theme's actual accent.
    /// </summary>
    public UiColor WithAlpha(float a) => new(_hex, _selector, _rgba, Math.Clamp(a, 0f, 1f));

    /// <summary>Resolves this color to a hex string against the given theme tokens.</summary>
    public string Resolve(ColorTokens colors)
    {
        string? hex = _selector is not null ? _selector(colors) : _hex;
        if (hex is null)
        {
            byte a8 = _alpha is { } al ? (byte)Math.Clamp((int)MathF.Round(al * 255f), 0, 255) : (byte)(_rgba & 0xFF);
            return $"#{(byte)(_rgba >> 24):X2}{(byte)(_rgba >> 16):X2}{(byte)(_rgba >> 8):X2}{a8:X2}";
        }

        return _alpha is { } a ? ApplyAlpha(hex, a) : hex;
    }

    /// <summary>The channels, without a string in the middle. A token or hex is
    /// parsed from the span the theme holds, which allocates nothing.</summary>
    public (byte R, byte G, byte B, byte A) ResolveColor(ColorTokens colors)
    {
        string? source = _selector is not null ? _selector(colors) : _hex;
        if (source is null)
        {
            byte a8 = _alpha is { } al ? (byte)Math.Clamp((int)MathF.Round(al * 255f), 0, 255) : (byte)(_rgba & 0xFF);
            return ((byte)(_rgba >> 24), (byte)(_rgba >> 16), (byte)(_rgba >> 8), a8);
        }

        var (r, g, b, a) = ParseHex(source);
        if (_alpha is { } alpha) a = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        return (r, g, b, a);
    }

    /// <summary>The one hex parser — DrawingContext.ParseColor wraps it rather
    /// than keeping a second copy. A malformed digit gives black.</summary>
    internal static (byte R, byte G, byte B, byte A) ParseHex(ReadOnlySpan<char> hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length is not (6 or 8)) return (0, 0, 0, 255);
        byte a = hex.Length == 8 && Channel(hex[6..8], out byte parsedA) ? parsedA : (byte)255;
        return Channel(hex[0..2], out byte r) && Channel(hex[2..4], out byte g) && Channel(hex[4..6], out byte b)
            ? (r, g, b, a)
            : ((byte)0, (byte)0, (byte)0, (byte)255);

        static bool Channel(ReadOnlySpan<char> pair, out byte value) =>
            byte.TryParse(pair, System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string ApplyAlpha(string hex, float alpha)
    {
        // Take the RGB from a 6- or 8-digit hex, dropping any pre-existing alpha —
        // WithAlpha sets the alpha explicitly rather than multiplying one that's already there.
        string trimmed = hex.TrimStart('#');
        string rgb = trimmed.Length >= 6 ? trimmed[..6] : trimmed.PadLeft(6, '0');
        byte a = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        return $"#{rgb}{a:X2}";
    }

    public bool Equals(UiColor other) =>
        _hex == other._hex && ReferenceEquals(_selector, other._selector)
        && _rgba == other._rgba && _alpha == other._alpha;
    public override bool Equals(object? obj) => obj is UiColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(_hex, _selector, _rgba, _alpha);

    public static UiColor Background            => Token(c => c.Background);
    public static UiColor Surface               => Token(c => c.Surface);
    public static UiColor SurfaceRaised         => Token(c => c.SurfaceRaised);
    public static UiColor SurfaceRaisedHover    => Token(c => c.SurfaceRaisedHover);
    public static UiColor SurfaceRaisedActive   => Token(c => c.SurfaceRaisedActive);
    public static UiColor Border                => Token(c => c.Border);
    public static UiColor BorderHover           => Token(c => c.BorderHover);
    public static UiColor BorderFocus           => Token(c => c.BorderFocus);
    public static UiColor TextPrimary           => Token(c => c.TextPrimary);
    public static UiColor TextSecondary         => Token(c => c.TextSecondary);
    public static UiColor TextDisabled          => Token(c => c.TextDisabled);
    public static UiColor Accent                => Token(c => c.Accent);
    public static UiColor AccentForeground      => Token(c => c.AccentForeground);
    public static UiColor AccentHover           => Token(c => c.AccentHover);
    public static UiColor AccentActive          => Token(c => c.AccentActive);
    public static UiColor Destructive           => Token(c => c.Destructive);
    public static UiColor DestructiveForeground => Token(c => c.DestructiveForeground);
    public static UiColor Success               => Token(c => c.Success);
    public static UiColor SuccessForeground     => Token(c => c.SuccessForeground);
    public static UiColor SuccessBg             => Token(c => c.SuccessBg);
}
