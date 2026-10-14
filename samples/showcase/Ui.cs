using System.Text;
using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;
using static Bibui.Arrange;

namespace Showcase;

/// <summary>
/// Small pieces the screens are built from. Each is a plain function that
/// composes runtime boxes and returns the node, as bibui's own components do.
/// </summary>
internal static class Ui
{
    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>A text holds UTF-8 bytes; formatted per frame, encoded per frame.</summary>
    public static ReadOnlyMemory<byte> U8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A component label.</summary>
    public static BibuiStr S(string s) => BibuiStr.FromStrLit(Encoding.UTF8.GetBytes(s));

    public static Text T(BibuiRuntime rt, string s, FontSize? size = null, UiColor? color = null)
        => rt.Text(U8(s), size, color);

    public static Text Bold(BibuiRuntime rt, string s, FontSize? size = null, UiColor? color = null)
    {
        using (rt.PushFontFace(weight: FontWeight.Bold))
            return rt.Text(U8(s), size, color);
    }

    /// <summary>A small capitalised label above a group: tracking is not a thing yet, so it is plain small caps by hand.</summary>
    public static Text Eyebrow(BibuiRuntime rt, string s)
    {
        using (rt.PushFontFace(weight: FontWeight.Bold))
            return rt.Text(U8(s.ToUpperInvariant()), FontSize.Px(11f), UiColor.TextSecondary);
    }

    public static Icon Icon(BibuiRuntime rt, string name, float size, UiColor color)
    {
        var icon = rt.Icon(name, size);
        icon.Color = color;
        return icon;
    }

    // ── Surfaces ─────────────────────────────────────────────────────────────

    /// <summary>A card: the raised surface every panel of the console sits on.</summary>
    public static Style CardStyle(BibuiRuntime rt, float pad = 18f) => new()
    {
        Padding = EdgeInsets.All(pad),
        CornerRadius = rt.Theme.Radii.Lg + 4f,
        BorderWidth = rt.Theme.Borders.Width,
        BorderColor = UiColor.Border,
        BackgroundColor = UiColor.Surface,
        Shadow = rt.Theme.Elevation.Level(1),
    };

    /// <summary>A card's heading row: icon, title, an optional caption, and whatever goes at the far end.</summary>
    public static Box CardHeader(BibuiRuntime rt, string icon, string title, string? caption, params UiNode[] trailing)
    {
        var row = Row(
            CentredY(Icon(rt, icon, 16f, UiColor.Accent)),
            Space(10f),
            CentredY(Bold(rt, title, ComponentSize.Md, UiColor.TextPrimary)));
        if (caption is not null)
        {
            Gap(row, 10f);
            UiNode.PushIntoParent(row, CentredY(T(rt, caption, ComponentSize.Sm, UiColor.TextSecondary)));
        }
        Spring(row);
        foreach (var node in trailing) UiNode.PushIntoParent(row, node);
        return row.FillX().Px(height: 32f);
    }

    /// <summary>A rounded pill of tinted colour with a label: a status, a delta, a tag.</summary>
    public static Box Pill(BibuiRuntime rt, string label, UiColor tint, bool dot = false, float height = 22f)
    {
        var pill = Row().Px(height: height);
        pill.SizeX = SemanticSize.ChildrenSum();
        pill.Style = new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 9f, vertical: 0f),
            CornerRadius = height / 2f,
            BackgroundColor = tint.WithAlpha(0.16f),
        };
        if (dot)
        {
            UiNode.PushIntoParent(pill, CentredY(rt.Box().Px(6f, 6f).Styled(new Style { CornerRadius = 3f, BackgroundColor = tint })));
            Gap(pill, 6f);
        }
        UiNode.PushIntoParent(pill, CentredY(Bold(rt, label, FontSize.Px(11.5f), tint)));
        return pill;
    }

    /// <summary>A hairline across the parent.</summary>
    public static Box Rule(BibuiRuntime rt) =>
        rt.Box().FillX().Px(height: 1f).Styled(new Style { BackgroundColor = UiColor.Border });
}
