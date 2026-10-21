namespace Bibui.Core.Theming;

/// <summary>
/// Small registry of named colormaps used for gradient previews (e.g. colormap
/// selectors). Each entry is a list of (stop, hex-color) pairs sampled from the
/// canonical colormap; 5 stops is enough for a smooth horizontal preview at
/// typical UI sizes. Viridis, inferno, magma and plasma are matplotlib's (CC0);
/// Turbo is Google's (Apache-2.0). See NOTICE.
/// </summary>
public static class Colormaps
{
    public static readonly IReadOnlyList<(float Stop, string Color)> Viridis =
    [
        (0.00f, "#440154"),
        (0.25f, "#3B528B"),
        (0.50f, "#21918C"),
        (0.75f, "#5EC962"),
        (1.00f, "#FDE725"),
    ];

    public static readonly IReadOnlyList<(float Stop, string Color)> Inferno =
    [
        (0.00f, "#000004"),
        (0.25f, "#420A68"),
        (0.50f, "#932667"),
        (0.75f, "#DD513A"),
        (1.00f, "#FCFFA4"),
    ];

    public static readonly IReadOnlyList<(float Stop, string Color)> Magma =
    [
        (0.00f, "#000004"),
        (0.25f, "#3B0F70"),
        (0.50f, "#8C2981"),
        (0.75f, "#DE4968"),
        (1.00f, "#FCFDBF"),
    ];

    public static readonly IReadOnlyList<(float Stop, string Color)> Plasma =
    [
        (0.00f, "#0D0887"),
        (0.25f, "#6A00A8"),
        (0.50f, "#B12A90"),
        (0.75f, "#E16462"),
        (1.00f, "#FCA636"),
    ];

    public static readonly IReadOnlyList<(float Stop, string Color)> Turbo =
    [
        (0.00f, "#30123B"),
        (0.25f, "#4662D8"),
        (0.50f, "#36AAB7"),
        (0.75f, "#FAB72E"),
        (1.00f, "#7A0403"),
    ];

    public static IReadOnlyList<(float Stop, string Color)>? Get(string? name) => name?.ToLowerInvariant() switch
    {
        "viridis" => Viridis,
        "inferno" => Inferno,
        "magma"   => Magma,
        "plasma"  => Plasma,
        "turbo"   => Turbo,
        _ => null,
    };

    public static IReadOnlyList<string> Names { get; } =
    [
        "viridis", "inferno", "magma", "plasma", "turbo",
    ];
}
