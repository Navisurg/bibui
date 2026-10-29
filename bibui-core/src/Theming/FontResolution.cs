using Bibui.Core.Theming;

namespace Bibui.Core;

/// <summary>
/// Which face and size a theme means for a text: a slot (Body, Heading, …) and
/// a weight to a face, a size step to pixels. The theme's half of setting text;
/// the text layer (<see cref="TextLayer"/>) is handed the face and the size, and
/// knows nothing of themes.
///
/// <para>The faces are a table of the runtime's, <c>[slot * FontWeight.Count + weight]</c>,
/// filled when the theme's typography changes (<see cref="SlotFacesFill"/>, in
/// BeginBuild), so a text asking for its face reads one entry. Build, layout and
/// paint all see the frame's faces.</para>
/// </summary>
internal static class FontResolution
{
    /// <summary>The faces a slot table holds: every slot in every weight.</summary>
    internal const int SlotFaceCount = (int)FontSlot.Count * (int)FontWeight.Count;

    // A face's place in the table: a row per slot, a column per weight.
    internal static int SlotFace(FontSlot slot, FontWeight weight) => (int)slot * (int)FontWeight.Count + (int)weight;

    internal static string? ResolveFamily(FontSlot slot, TypographyTokens type) => slot switch
    {
        FontSlot.Display => type.FontFamilyDisplay ?? type.FontFamilyBody,
        FontSlot.Heading => type.FontFamilyHeading ?? type.FontFamilyBody,
        FontSlot.Mono => type.FontFamilyMono ?? type.FontFamilyBody,
        _ => type.FontFamilyBody,
    };

    internal static int ResolveWeight(FontWeight weight, TypographyTokens type) =>
        ((int)weight & (int)FontWeight.Bold) != 0 ? type.WeightSemiBold : type.WeightNormal;

    /// <summary>
    /// Fills <paramref name="faces"/> with the face each slot resolves to at each
    /// weight under <paramref name="type"/>, from <paramref name="fonts"/>. The
    /// faces come from FontProvider's cache, which keeps what a (family, weight)
    /// first resolved to: a family registered after it was first asked for still
    /// gets the face it got then. The italics are cleared, for
    /// <see cref="FaceFromSlot"/> to resolve under this typography.
    /// </summary>
    internal static void SlotFacesFill(FontFace?[] faces, Fonts fonts, TypographyTokens type)
    {
        int regular = ResolveWeight(FontWeight.Regular, type);
        int bold = ResolveWeight(FontWeight.Bold, type);
        for (var slot = (FontSlot)0; slot < FontSlot.Count; slot++)
        {
            string? family = ResolveFamily(slot, type);
            faces[SlotFace(slot, FontWeight.Regular)] = fonts.Provider.Resolve(family, regular);
            faces[SlotFace(slot, FontWeight.Bold)] = fonts.Provider.Resolve(family, bold);
            faces[SlotFace(slot, FontWeight.Italic)] = faces[SlotFace(slot, FontWeight.BoldItalic)] = null;
        }
    }

    // The uprights are filled by SlotFacesFill; an italic on its first ask, so an
    // app that sets nothing in italic opens no italic face.
    /// <summary>The face <paramref name="slot"/> is set in at <paramref name="weight"/>.</summary>
    internal static FontFace FaceFromSlot(Fonts fonts, FontFace?[] faces, FontSlot slot, FontWeight weight, ThemeTokens theme)
        => faces[SlotFace(slot, weight)] ??= fonts.Provider.Resolve(ResolveFamily(slot, theme.Typography), ResolveWeight(weight, theme.Typography), italic: true);

    /// <summary>The font <paramref name="text"/> is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontFromText(Fonts fonts, FontFace?[] faces, Text text, ThemeTokens theme, float scale)
        => FontFromSize(fonts, faces, text.Size, text.Family, text.Weight, theme, scale);

    /// <summary>The font a text with these settings is set in, measured at <paramref name="scale"/>.</summary>
    internal static Font FontFromSize(Fonts fonts, FontFace?[] faces, FontSize size, FontSlot family, FontWeight weight, ThemeTokens theme, float scale)
        => new(size.In(theme), FaceFromSlot(fonts, faces, family, weight, theme), scale);

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
