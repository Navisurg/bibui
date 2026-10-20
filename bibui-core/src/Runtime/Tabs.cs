using Bibui.Core;
using Bibui.Core.TextEdit;
using static Bibui.Core.Runtime.Tabs;

namespace Bibui.Core.Runtime;

/// <summary>One tab of a <see cref="Tabs"/> strip: what it reads, and the value it stands for.</summary>
public readonly record struct TabOption(string Label, string Value);

/// <summary>What <see cref="TabsExtensions.Tabs"/> built: the strip, and the value of the tab clicked this frame, if any.</summary>
public readonly record struct TabsWidget(Box Node, string? Picked)
{
    public static implicit operator UiNode(TabsWidget widget) => widget.Node;

    /// <summary>So it goes straight into <c>Row(...)</c> or <c>Column(...)</c>.</summary>
    public static implicit operator Arrange.Item(TabsWidget widget) => widget.Node;
}

/// <summary>
/// Tabs: a strip of tab headers on a raised track, the active one cut out of
/// it in the background colour, and — separately — the panel the content goes
/// in. Each tab is a retained box that takes its own press; the strip reports
/// which was clicked and the caller applies it, and decides what the panel
/// holds. The component owns no state.
/// </summary>
public static class Tabs
{
    /// <summary>The strip's height, at the theme's default density.</summary>
    public const float Height = 36f;

    /// <summary>The space a caller puts between the strip and its <see cref="Panel"/>.</summary>
    public const float PanelGap = 8f;

    internal const float Inset = 2f;     // the active tab sits this far inside the track, above and below
    internal const float PadX = 12f;     // Spacing.Md, either side of a label
    internal const float TabGap = 4f;
    internal const float RadiusSm = 4f;  // Radii.Sm
    internal const float RadiusMd = 6f;  // Radii.Md
    internal const float PanelPad = 12f; // Spacing.Md


    /// <summary>
    /// The panel under a strip, under the parent in scope and as wide as it: a
    /// bordered surface, padded, laid out down. Push it to build the active
    /// tab's content into it. It is as tall as its content; set its size to
    /// change that.
    /// </summary>
    public static Box Panel(BibuiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Box panel = runtime.Box();
        panel.ChildLayoutAxis = LayoutAxis.Y;
        panel.SizeX = SemanticSize.ParentPct(1f);
        panel.SizeY = SemanticSize.ChildrenSum();
        panel.Style.BackgroundColor = UiColor.Surface;
        panel.Style.BorderColor = UiColor.Border;
        panel.Style.BorderWidth = runtime.Theme.Borders.Width;
        panel.Style.CornerRadius = RadiusMd;
        panel.Style.Padding = EdgeInsets.All(PanelPad);
        return panel;
    }
}

/// <summary>The Tabs component, called on the runtime that builds it.</summary>
public static class TabsExtensions
{
    /// <summary>
    /// Builds the strip named <paramref name="id"/> under the parent in scope,
    /// as wide as all of it, showing <paramref name="items"/> with the one whose
    /// value is <paramref name="value"/> active. The tabs are named
    /// <c>{id}-{value}</c>.
    /// </summary>
    public static TabsWidget Tabs(this BibuiRuntime runtime, string id, IReadOnlyList<TabOption> items, string? value)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(items);

        Box strip = runtime.Box();
        strip.ChildLayoutAxis = LayoutAxis.X;
        strip.SizeX = SemanticSize.ParentPct(1f);
        strip.SizeY = SemanticSize.Pixels(runtime.Theme.Density.Of(Height));
        strip.Style.BackgroundColor = UiColor.SurfaceRaised;
        strip.Style.CornerRadius = RadiusSm;
        strip.Style.Padding = new EdgeInsets(Inset, 4f, Inset, 4f);

        string? picked = null;
        using (runtime.PushParent(strip))
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) runtime.Gap(TabGap);

                bool active = items[i].Value == value;
                Box tab = runtime.BoxFromId($"{id}-{items[i].Value}", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(tab).Clicked) picked = items[i].Value;
                tab.ChildLayoutAxis = LayoutAxis.Y;
                tab.SizeX = SemanticSize.ChildrenSum();
                tab.SizeY = SemanticSize.ParentPct(1f);
                tab.Style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: 0f);
                tab.Style.CornerRadius = RadiusSm;
                tab.Style.BackgroundColor = active ? UiColor.Background : (UiColor?)null;
                tab.Style.HoverBackgroundColor = active ? (UiColor?)null : UiColor.SurfaceRaisedHover;

                // Centred down the tab, which has the strip's height.
                using (runtime.PushParent(tab))
                {
                    Arrange.Spring(tab);
                    runtime.Text(items[i].Label, textColor: active ? TextColor.Primary : TextColor.Secondary);
                    Arrange.Spring(tab);
                }
            }
        }
        return new TabsWidget(strip, picked);
    }
}
