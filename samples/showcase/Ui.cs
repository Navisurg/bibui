using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

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
        return rt.Text(s, size, color, weight: FontWeight.Bold);
    }

    /// <summary>A small capitalised label above a group: tracking is not a thing yet, so it is plain small caps by hand.</summary>
    public static Text Eyebrow(BibuiRuntime rt, string s)
    {
        return rt.Text(s.ToUpperInvariant(), FontSize.Px(11f), UiColor.TextSecondary, weight: FontWeight.Bold);
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
    /// What is built until <see cref="EndBottomY"/> sits at the foot of a
    /// full-height column across the row in scope, <paramref name="lift"/> px
    /// up: a small text set by eye on the baseline of a large one beside it.
    /// Hands the column back for the End.
    /// </summary>
    public static Box BeginBottomY(BibuiRuntime rt)
    {
        var column = rt.BeginParent(rt.Column().Sized(y: Size.Fill));
        rt.Spacer(Size.Fill);
        return column;
    }

    /// <summary>Ends <see cref="BeginBottomY"/>: the lift under what was built, and the column.</summary>
    public static void EndBottomY(BibuiRuntime rt, Box column, float lift)
    {
        rt.Gap(lift);
        rt.EndParent(column);
    }

    /// <summary>
    /// A card's heading row: icon, title, an optional caption, and a spring.
    /// What is built until <see cref="EndCardHeader"/> goes at the far end.
    /// </summary>
    public static Box BeginCardHeader(BibuiRuntime rt, string icon, string title, string? caption)
    {
        var row = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(32f)));
        rt.BeginCenterAcross();
        Icon(rt, icon, 16f, UiColor.Accent);
        rt.EndCenterAcross();
        rt.Gap(10f);
        rt.BeginCenterAcross();
        Bold(rt, title, ComponentSize.Md, UiColor.TextPrimary);
        rt.EndCenterAcross();
        if (caption is not null)
        {
            rt.Gap(10f);
            rt.BeginCenterAcross();
            T(rt, caption, ComponentSize.Sm, UiColor.TextSecondary);
            rt.EndCenterAcross();
        }
        rt.Spacer(Size.Fill);
        return row;
    }

    /// <summary>Ends <see cref="BeginCardHeader"/>.</summary>
    public static void EndCardHeader(BibuiRuntime rt, Box row) => rt.EndParent(row);

    /// <summary>A card's heading row with nothing at the far end.</summary>
    public static Box CardHeader(BibuiRuntime rt, string icon, string title, string? caption)
    {
        var row = BeginCardHeader(rt, icon, title, caption);
        EndCardHeader(rt, row);
        return row;
    }

    /// <summary>A rounded pill of tinted colour with a label: a status, a delta, a tag.</summary>
    public static Box Pill(BibuiRuntime rt, string label, UiColor tint, bool dot = false, float height = 22f)
    {
        var pill = rt.BeginParent(rt.Row().Sized(y: Size.Px(height)));
        pill.Style = new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 9f, vertical: 0f),
            CornerRadius = height / 2f,
            BackgroundColor = tint.WithAlpha(0.16f),
        };
        if (dot)
        {
            rt.BeginCenterAcross();
            var dotBox = rt.Box().Sized(Size.Px(6f), Size.Px(6f));
            dotBox.Style = new Style { CornerRadius = 3f, BackgroundColor = tint };
            rt.EndCenterAcross();
            rt.Gap(6f);
        }
        rt.BeginCenterAcross();
        Bold(rt, label, FontSize.Px(11.5f), tint);
        rt.EndCenterAcross();
        rt.EndParent(pill);
        return pill;
    }

    /// <summary>A hairline across the parent.</summary>
    public static Box Rule(BibuiRuntime rt)
    {
        var rule = rt.Box().Sized(Size.Fill, Size.Px(1f));
        rule.Style = new Style { BackgroundColor = UiColor.Border };
        return rule;
    }
}
