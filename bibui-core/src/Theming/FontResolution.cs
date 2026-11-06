using Bibui.Core.Theming;

namespace Bibui.Core;

/// <summary>
/// Which face and size a theme means for a text: a slot (Body, Heading, …) and
/// a weight to a face, a size step to pixels. The theme's half of setting text;
/// the text layer (<see cref="TextLayer"/>) is handed the face and the size, and
/// knows nothing of themes.
///
/// <para>The faces are a table of the runtime's, <c>[slot * 2 + bold]</c>, filled
/// once a frame from the frame's theme (<see cref="SlotFacesFill"/>, in
/// BeginBuild), so a text asking for its face reads one entry. Build, layout and
/// paint all see the frame's faces.</para>
/// </summary>
internal static class FontResolution
{
    /// <summary>The faces a slot table holds: four slots, each regular and bold.</summary>
    internal const int SlotFaceCount = 8;

    internal static string? ResolveFamily(FontSlot slot, ThemeTokens theme) => slot switch
    {
        FontSlot.Display => theme.Typography.FontFamilyDisplay ?? theme.Typography.FontFamilyBody,
        FontSlot.Heading => theme.Typography.FontFamilyHeading ?? theme.Typography.FontFamilyBody,
        FontSlot.Mono => theme.Typography.FontFamilyMono ?? theme.Typography.FontFamilyBody,
        _ => theme.Typography.FontFamilyBody,
    };

    internal static int ResolveWeight(FontWeight weight, ThemeTokens theme) =>
        weight == FontWeight.Bold ? theme.Typography.WeightSemiBold : theme.Typography.WeightNormal;

    /// <summary>
    /// Fills <paramref name="faces"/> with the face each slot resolves to at each
    /// weight under <paramref name="theme"/>, from <paramref name="fonts"/>. The
    /// faces come from FontProvider's cache, which keeps what a (family, weight)
    /// first resolved to: a family registered after it was first asked for still
    /// gets the face it got then.
    /// </summary>
    internal static void SlotFacesFill(FontFace[] faces, Fonts fonts, ThemeTokens theme)
    {
        int regular = ResolveWeight(FontWeight.Regular, theme);
        int bold = ResolveWeight(FontWeight.Bold, theme);
        for (int s = 0; s < SlotFaceCount / 2; s++)
        {
            string? family = ResolveFamily((FontSlot)s, theme);
            faces[s * 2] = fonts.Provider.Resolve(family, regular);
            faces[s * 2 + 1] = fonts.Provider.Resolve(family, bold);
        }
    }

    /// <summary>The face <paramref name="slot"/> is set in at <paramref name="weight"/>.</summary>
    internal static FontFace FaceFromSlot(FontFace[] faces, FontSlot slot, FontWeight weight)
        => faces[(int)slot * 2 + (weight == FontWeight.Bold ? 1 : 0)];

    /// <summary>The font <paramref name="text"/> is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontFromText(FontFace[] faces, Text text, ThemeTokens theme, float scale)
        => FontFromSize(faces, text.Size, text.Family, text.Weight, theme, scale);

    /// <summary>The font a text with these settings is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontFromSize(FontFace[] faces, FontSize size, FontSlot family, FontWeight weight, ThemeTokens theme, float scale)
        => new(size.In(theme), FaceFromSlot(faces, family, weight), scale);

    /// <summary>The theme's font size for a size step, in logical px.</summary>
    internal static float SizeFromStep(ComponentSize step, ThemeTokens theme) => step switch
    {
        ComponentSize.Sm => theme.Typography.SizeSm,
        ComponentSize.Lg => theme.Typography.SizeLg,
        ComponentSize.Xl => theme.Typography.SizeXl,
        ComponentSize.Xxl => theme.Typography.Size2Xl,
        _ => theme.Typography.SizeMd,
    };
}
