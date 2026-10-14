using Bibui;
using Bibui.TextEdit;
using Bibui.Theming;

namespace Bibui.Runtime;

/// <summary>
/// A menu: a raised list of actions in a dropdown of the runtime's, built
/// between <see cref="Begin"/> and <see cref="MenuScope.End"/> out of
/// <see cref="Item"/>, <see cref="Separator"/> and <see cref="Label"/> rows,
/// or rows of the caller's own. One menu serves a dropdown menu — opened under
/// a trigger — and a context menu — opened where the right button went down:
/// where it opens is the caller's to say.
///
/// <para>It is a dropdown of the runtime's, open from
/// <see cref="BibuiRuntime.OpenDropDown"/> until
/// <see cref="BibuiRuntime.CloseDropDown"/> or a press that dismisses it: a
/// press a box outside it takes, or one nothing takes. A dropdown menu's
/// trigger opens it with itself as the anchor, so the trigger's own press
/// leaves it to the trigger; a context menu opens on the press that wants it
/// there, and so moves rather than closes. The caller applies what the items
/// report: an item clicked is typically its cue to act and close the menu.</para>
/// </summary>
public static class Menu
{
    private const float Radius = 8f;
    private const float ListPad = 4f;
    private const float ItemPadX = 8f;
    private const float ItemPadY = 7.5f;   // a 32 px row around a body line
    private const float ItemRadius = 4f;
    private const float LabelPadY = 4f;
    private const float IconSize = 14f;
    private const float IconGap = 8f;
    private const float RuleInset = 4f;
    private const float RuleGap = 2f;

    /// <summary>
    /// Begins the menu named <paramref name="id"/>, when it is open, at
    /// (<paramref name="x"/>, <paramref name="y"/>) in window coordinates,
    /// <paramref name="width"/> px wide and as tall as its rows, and pushes it,
    /// so what is built until <see cref="MenuScope.End"/> goes into it. Null
    /// when it is not open.
    /// </summary>
    public static MenuScope? Begin(BibuiRuntime runtime, string id, float x, float y, float width = 220f)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        if (runtime.BeginDropDown(id) is not { } list) return null;
        list.SizeX = SemanticSize.Pixels(width);
        list.SizeY = SemanticSize.ChildrenSum();
        list.At(x, y);
        var theme = runtime.Theme;
        Style style = list.Style;
        style.Padding = EdgeInsets.All(ListPad);
        style.CornerRadius = Radius;
        style.BorderWidth = theme.Borders.Width;
        style.Shadow = theme.Elevation.Level(ElevationTokens.Overlay);
        style.BorderColor = UiColor.Border;
        style.BackgroundColor = UiColor.SurfaceRaised;
        return new MenuScope(runtime, list, runtime.PushParent(list));
    }

    /// <summary>
    /// Begins the menu named <paramref name="id"/>, when it is open, under
    /// <paramref name="anchor"/>, where it was drawn last frame: left edges
    /// aligned, <paramref name="gap"/> px below it. What a dropdown menu does
    /// with its trigger, which opens it with
    /// <c>runtime.OpenDropDown(id, trigger)</c>. Null when it is not open.
    /// </summary>
    public static MenuScope? BeginBelow(BibuiRuntime runtime, string id, UiNode anchor, float width = 220f, float gap = 4f)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return Begin(runtime, id, anchor.Rect.X, anchor.Rect.Y + anchor.Extent.Y + gap, width);
    }

    /// <summary>
    /// A row of the menu: an optional <paramref name="icon"/>, the
    /// <paramref name="label"/>, and an optional <paramref name="shortcut"/>
    /// at the far end. Lit under the pointer and taking the press, unless
    /// <paramref name="disabled"/> — which still takes the press, and reports
    /// nothing, so it does not fall through to what is beneath.
    /// </summary>
    public static BibuiWidget Item(BibuiRuntime runtime, string id, ReadOnlySpan<char> label,
        string? icon = null, ReadOnlySpan<char> shortcut = default, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        Box row = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(row);
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ParentPct(1f);
        row.SizeY = SemanticSize.ChildrenSum();
        row.Style.Padding = EdgeInsets.Symmetric(horizontal: ItemPadX, vertical: runtime.Theme.Density.Of(ItemPadY));
        row.Style.CornerRadius = ItemRadius;
        row.Style.HoverBackgroundColor = disabled ? (UiColor?)null : UiColor.SurfaceRaisedHover;

        UiColor ink = disabled ? UiColor.TextDisabled : UiColor.TextPrimary;
        using (runtime.PushParent(row))
        {
            if (icon is not null)
            {
                // Down a little, about level with the label's glyphs.
                Box cell = runtime.Box();
                cell.ChildLayoutAxis = LayoutAxis.Y;
                cell.SizeX = cell.SizeY = SemanticSize.ChildrenSum();
                using (runtime.PushParent(cell))
                {
                    runtime.Gap(3f);
                    runtime.Icon(icon, IconSize).Color = ink;
                }
                runtime.Gap(IconGap);
            }
            runtime.Text(label, textColor: ink);
            if (!shortcut.IsEmpty)
            {
                Arrange.Spring(row);
                runtime.Text(shortcut, ComponentSize.Sm, UiColor.TextDisabled);
            }
        }
        return new BibuiWidget(row, interaction);
    }

    /// <summary>A rule across the menu, between groups of rows.</summary>
    public static Box Separator(BibuiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Box band = runtime.Box();
        band.ChildLayoutAxis = LayoutAxis.X;
        band.SizeX = SemanticSize.ParentPct(1f);
        band.SizeY = SemanticSize.ChildrenSum();
        band.Style.Padding = EdgeInsets.Symmetric(horizontal: RuleInset, vertical: RuleGap);
        using (runtime.PushParent(band))
        {
            Box rule = runtime.Box();
            rule.SizeX = SemanticSize.ParentPct(1f);
            rule.SizeY = SemanticSize.Pixels(runtime.Theme.Borders.Width);
            rule.Style.BackgroundColor = UiColor.Border;
        }
        return band;
    }

    /// <summary>A caption over a group of rows: small, bold and quiet. Takes nothing.</summary>
    public static Box Label(BibuiRuntime runtime, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Box band = runtime.Box();
        band.ChildLayoutAxis = LayoutAxis.X;
        band.SizeX = SemanticSize.ParentPct(1f);
        band.SizeY = SemanticSize.ChildrenSum();
        band.Style.Padding = EdgeInsets.Symmetric(horizontal: ItemPadX, vertical: LabelPadY);
        using (runtime.PushParent(band))
            using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(text, ComponentSize.Sm, UiColor.TextSecondary);
        return band;
    }
}

/// <summary>A menu between <see cref="Menu.Begin"/> and <see cref="End"/>.</summary>
public readonly struct MenuScope
{
    private readonly BibuiRuntime _runtime;
    private readonly Box _list;
    private readonly BibuiRuntime.ParentScope _inList;

    internal MenuScope(BibuiRuntime runtime, Box list, BibuiRuntime.ParentScope inList)
    {
        _runtime = runtime;
        _list = list;
        _inList = inList;
    }

    /// <summary>
    /// Stops building into the menu and returns it and what happened to it.
    /// The list asks after its rows, so a press on its padding or a rule stops
    /// there rather than reaching what is beneath, and dismisses nothing. An
    /// Escape no one has eaten closes the menu, and is eaten, so it does not
    /// also close what is under it.
    /// </summary>
    public BibuiWidget End()
    {
        _inList.Dispose();
        _list.Claims = InteractionKinds.Press;
        var widget = new BibuiWidget(_list, _runtime.InteractionsFor(_list));
        foreach (var handle in _runtime.UnconsumedEvents)
            if (handle.Event is { IsKeyDown: true, Key: Key.Escape })
            {
                handle.Eat();
                _runtime.CloseDropDown(_list.Id!);
            }
        return widget;
    }
}
