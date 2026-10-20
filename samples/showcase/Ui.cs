using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;

namespace Showcase;

/// <summary>
/// Small pieces the screens are built from. Each is a plain function that
/// composes runtime boxes and returns the node, as bibui's own components do.
/// </summary>
internal static class Ui
{
    // ── Text ─────────────────────────────────────────────────────────────────

    public static Text T(BibuiRuntime rt, string s, FontSize? size = null, UiColor? color = null)
        => rt.Text(s, size, color);

    public static Text Bold(BibuiRuntime rt, string s, FontSize? size = null, UiColor? color = null)
    {
        using (rt.PushFontFace(weight: FontWeight.Bold))
            return rt.Text(s, size, color);
    }

    /// <summary>A small capitalised label above a group: tracking is not a thing yet, so it is plain small caps by hand.</summary>
    public static Text Eyebrow(BibuiRuntime rt, string s)
    {
        using (rt.PushFontFace(weight: FontWeight.Bold))
            return rt.Text(s.ToUpperInvariant(), FontSize.Px(11f), UiColor.TextSecondary);
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

    // ── Placing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="node"/>, built already, centred across the row in scope:
    /// a full-height column of a spring, it, and a spring.
    /// </summary>
    public static Box CentredY(BibuiRuntime rt, UiNode node)
    {
        var column = rt.ColumnFillY();
        using (rt.PushParent(column))
        using (rt.Centred())
            UiNode.PushIntoParent(column, node);
        return column;
    }

    /// <summary>The same as <see cref="CentredY"/>, across the column in scope: a full-width row.</summary>
    public static Box CentredX(BibuiRuntime rt, UiNode node)
    {
        var row = rt.RowFillX();
        using (rt.PushParent(row))
        using (rt.Centred())
            UiNode.PushIntoParent(row, node);
        return row;
    }

    /// <summary>
    /// <paramref name="node"/>, built already, at the foot of a full-height column
    /// across the row in scope, <paramref name="lift"/> px up: a small text set
    /// by eye on the baseline of a large one beside it.
    /// </summary>
    public static Box BottomY(BibuiRuntime rt, UiNode node, float lift)
    {
        var column = rt.ColumnFillY();
        using (rt.PushParent(column))
        {
            rt.Spring();
            Place(rt, node);
            rt.Gap(lift);
        }
        return column;
    }

    /// <summary><paramref name="node"/>, built already, linked last under the parent in scope.</summary>
    public static void Place(BibuiRuntime rt, UiNode node) => UiNode.PushIntoParent(rt.ParentInScope, node);

    /// <summary>A card's heading row: icon, title, an optional caption, and whatever goes at the far end.</summary>
    public static Box CardHeader(BibuiRuntime rt, string icon, string title, string? caption, params UiNode[] trailing)
    {
        var row = rt.RowFillX(32f);
        using (rt.PushParent(row))
        {
            CentredY(rt, Icon(rt, icon, 16f, UiColor.Accent));
            rt.Gap(10f);
            CentredY(rt, Bold(rt, title, ComponentSize.Md, UiColor.TextPrimary));
            if (caption is not null)
            {
                rt.Gap(10f);
                CentredY(rt, T(rt, caption, ComponentSize.Sm, UiColor.TextSecondary));
            }
            rt.Spring();
            foreach (var node in trailing) Place(rt, node);
        }
        return row;
    }

    /// <summary>A rounded pill of tinted colour with a label: a status, a delta, a tag.</summary>
    public static Box Pill(BibuiRuntime rt, string label, UiColor tint, bool dot = false, float height = 22f)
    {
        var pill = rt.RowHeight(height).Styled(new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 9f, vertical: 0f),
            CornerRadius = height / 2f,
            BackgroundColor = tint.WithAlpha(0.16f),
        });
        using (rt.PushParent(pill))
        {
            if (dot)
            {
                CentredY(rt, rt.Box().Px(6f, 6f).Styled(new Style { CornerRadius = 3f, BackgroundColor = tint }));
                rt.Gap(6f);
            }
            CentredY(rt, Bold(rt, label, FontSize.Px(11.5f), tint));
        }
        return pill;
    }

    /// <summary>A hairline across the parent.</summary>
    public static Box Rule(BibuiRuntime rt) =>
        rt.Box().FillX().Px(height: 1f).Styled(new Style { BackgroundColor = UiColor.Border });
}
