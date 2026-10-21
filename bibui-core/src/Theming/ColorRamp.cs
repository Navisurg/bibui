namespace Bibui.Core.Theming;

/// <summary>
/// One hue from light to dark in eleven steps — 50, 100, 200 … 900, 950 —
/// that a theme's colour roles name by step: <c>"neutral-900"</c>,
/// <c>"accent-500"</c>. See <see cref="ColorRamps"/>.
/// </summary>
/// <remarks>Immutable: <see cref="With"/> gives a copy with one step changed.</remarks>
public sealed class ColorRamp : IEquatable<ColorRamp>
{
    /// <summary>The steps a ramp has, lightest first.</summary>
    public static IReadOnlyList<int> Steps { get; } = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950];

    private readonly string[] _hex;

    /// <summary>A ramp from its eleven colours, step 50 first and 950 last.</summary>
    /// <exception cref="ArgumentException">Not eleven colours, or one is not <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</exception>
    public ColorRamp(params string[] hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 11)
            throw new ArgumentException($"A ramp has 11 steps (50 to 950); {hex.Length} colours were given.", nameof(hex));
        foreach (var h in hex) CheckHex(h, nameof(hex));
        _hex = (string[])hex.Clone();
    }

    /// <summary>The colour at <paramref name="step"/> (50, 100, 200 … 900, 950).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of <see cref="Steps"/>.</exception>
    public string this[int step] => _hex[IndexOf(step)];

    /// <summary>A copy with <paramref name="step"/> set to <paramref name="hex"/>.</summary>
    public ColorRamp With(int step, string hex)
    {
        CheckHex(hex, nameof(hex));
        var copy = (string[])_hex.Clone();
        copy[IndexOf(step)] = hex;
        return new ColorRamp(copy);
    }

    /// <summary>Zinc-like greys, the neutral ramp a theme has unless it sets one.</summary>
    public static ColorRamp DefaultNeutral { get; } = new(
        "#FAFAFA", "#F4F4F5", "#E4E4E7", "#D4D4D8", "#A1A1AA", "#71717A",
        "#52525B", "#3F3F46", "#27272A", "#18181B", "#09090B");

    /// <summary>Violet, the accent ramp a theme has unless it sets one.</summary>
    public static ColorRamp DefaultAccent { get; } = new(
        "#F5F3FF", "#EDE9FE", "#DDD6FE", "#C4B5FD", "#A78BFA", "#8B5CF6",
        "#7C3AED", "#6D28D9", "#5B21B6", "#4C1D95", "#2E1065");

    internal static int IndexOf(int step) => step switch
    {
        50 => 0, 100 => 1, 200 => 2, 300 => 3, 400 => 4, 500 => 5,
        600 => 6, 700 => 7, 800 => 8, 900 => 9, 950 => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "A ramp step is 50, 100, 200 … 900 or 950."),
    };

    internal static bool IsStep(int step) => step is 50 or 950 || (step is >= 100 and <= 900 && step % 100 == 0);

    internal static void CheckHex(string hex, string paramName)
    {
        if (!IsHex(hex))
            throw new ArgumentException($"'{hex}' is not a colour: write #RRGGBB or #RRGGBBAA.", paramName);
    }

    internal static bool IsHex(string? hex)
    {
        if (hex is null || hex.Length is not (7 or 9) || hex[0] != '#') return false;
        for (int i = 1; i < hex.Length; i++)
            if (!char.IsAsciiHexDigit(hex[i])) return false;
        return true;
    }

    public bool Equals(ColorRamp? other) => other is not null && _hex.AsSpan().SequenceEqual(other._hex);
    public override bool Equals(object? obj) => Equals(obj as ColorRamp);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var s in _hex) h.Add(s);
        return h.ToHashCode();
    }
}

/// <summary>
/// The primitive palettes a theme's colour roles are drawn from. A role names a
/// step of one of them — <c>"neutral-900"</c>, <c>"accent-500"</c> — so
/// replacing a ramp recolours every role that names it, and nothing else.
/// </summary>
public sealed record ColorRamps
{
    /// <summary>Greys: backgrounds, surfaces, outlines, text.</summary>
    public ColorRamp Neutral { get; init; } = ColorRamp.DefaultNeutral;

    /// <summary>The brand hue: filled controls, focus, selection.</summary>
    public ColorRamp Accent { get; init; } = ColorRamp.DefaultAccent;
}
