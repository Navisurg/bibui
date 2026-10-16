using Bibui;
using Bibui.Theming;

namespace Bibui;

/// <summary>
/// Family and weight resolution for a font slot, from the theme.
/// </summary>
internal static class FontResolution
{
    internal static string? ResolveFamily(FontSlot slot, ThemeTokens theme) => slot switch
    {
        FontSlot.Display => theme.Typography.FontFamilyDisplay ?? theme.Typography.FontFamilyBody,
        FontSlot.Heading => theme.Typography.FontFamilyHeading ?? theme.Typography.FontFamilyBody,
        FontSlot.Mono => theme.Typography.FontFamilyMono ?? theme.Typography.FontFamilyBody,
        _ => theme.Typography.FontFamilyBody,
    };

    internal static int ResolveWeight(FontWeight weight, ThemeTokens theme) =>
        weight == FontWeight.Bold ? theme.Typography.WeightSemiBold : theme.Typography.WeightNormal;
}
