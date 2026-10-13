using Bibui;
using Bibui.TextEdit;
using Bibui.Theming;

namespace Bibui.Runtime;

/// <summary>
/// A select: a trigger that opens a dropdown, built between
/// <see cref="Begin"/> and <see cref="SelectScope.End"/>. Whatever the caller
/// builds in between goes into the dropdown — <see cref="Item"/> rows, or
/// rows of its own. The select owns nothing: the caller keeps whether it is
/// open and what is chosen, and applies what <see cref="SelectScope.End"/>
/// reports. It closes when the caller says: on a press of its trigger, or of
/// an item.
/// </summary>
public static class Select
{
    private const float PadX = 12f;
    private const float PadY = 10f;
    private const float Radius = 6f;
    private const float Gap = 4f;
    private const float ListPad = 4f;
    private const float ItemPadX = 8f;
    private const float ItemPadY = 8f;
    private const float ItemRadius = 4f;

    /// <summary>
    /// Builds the trigger named <paramref name="id"/> showing
    /// <paramref name="text"/>, under the parent in scope, and — when
    /// <paramref name="open"/> — opens its dropdown under where the trigger was
    /// drawn last frame and pushes it, so what is built until
    /// <see cref="SelectScope.End"/> goes into it.
    /// </summary>
    public static SelectScope Begin(BibuiRuntime runtime, string id, BibuiStr text, bool open,
        float width = 220f, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(text);

        Box trigger = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        bool toggled = runtime.InteractionsFor(trigger).Clicked;
        trigger.ChildLayoutAxis = LayoutAxis.X;
        trigger.SizeX = SemanticSize.Pixels(width);
        trigger.SizeY = SemanticSize.ChildrenSum();
        var theme = runtime.Theme;
        Style style = trigger.Style;
        style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: theme.Density.Of(PadY));
        style.CornerRadius = Radius;
        style.BorderWidth = theme.Borders.Width;
        style.BorderColor = UiColor.Border;
        style.BackgroundColor = disabled ? UiColor.SurfaceRaised : UiColor.Surface;
        using (runtime.PushParent(trigger))
        {
            runtime.Text(text.AsMemory(), textColor: disabled ? TextColor.Disabled : TextColor.Primary);
            Arrange.Spring(trigger);
            Icon chevron = runtime.Icon(open ? "chevron-up" : "chevron-down", size: 16f);
            chevron.Color = UiColor.TextSecondary;
        }

        if (!open || disabled) return new SelectScope(runtime, trigger, list: null, default, toggled);

        // Under the trigger, where it was drawn last frame, and as wide: a
        // container may have stretched it past the width it asked for.
        Box list = runtime.OpenDropDown(id + "-list");
        list.SizeX = SemanticSize.Pixels(trigger.Extent.X > 0f ? trigger.Extent.X : width);
        list.SizeY = SemanticSize.ChildrenSum();
        list.At(trigger.Rect.X, trigger.Rect.Y + trigger.Extent.Y + Gap);
        list.Style.Padding = EdgeInsets.All(ListPad);
        list.Style.CornerRadius = Radius;
        list.Style.BorderWidth = theme.Borders.Width;
        list.Style.Shadow = theme.Elevation.Level(ElevationTokens.Overlay);
        list.Style.BorderColor = UiColor.Border;
        list.Style.BackgroundColor = UiColor.SurfaceRaised;
        return new SelectScope(runtime, trigger, list, runtime.PushParent(list), toggled);
    }

    /// <summary>
    /// A plain row of the dropdown: <paramref name="label"/>, raised when
    /// <paramref name="selected"/>, taking the press unless
    /// <paramref name="disabled"/>. Built between <see cref="Begin"/> and
    /// <see cref="SelectScope.End"/>.
    /// </summary>
    public static BibuiWidget Item(BibuiRuntime runtime, string id, BibuiStr label,
        bool selected = false, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(label);

        Box row = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(row);
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ParentPct(1f);
        row.SizeY = SemanticSize.ChildrenSum();
        row.Style.Padding = EdgeInsets.Symmetric(horizontal: ItemPadX, vertical: runtime.Theme.Density.Of(ItemPadY));
        row.Style.CornerRadius = ItemRadius;
        row.Style.BackgroundColor = selected ? UiColor.SurfaceRaisedHover : (UiColor?)null;
        row.Style.HoverBackgroundColor = disabled ? (UiColor?)null : UiColor.SurfaceRaisedHover;
        using (runtime.PushParent(row))
        {
            runtime.Text(label.AsMemory(), textColor: disabled ? TextColor.Disabled : TextColor.Primary);
        }
        return new BibuiWidget(row, interaction);
    }
}

/// <summary>What <see cref="SelectScope.End"/> reports: the trigger, and whether it was pressed.</summary>
// TODO: a press outside the open list does not close it. The runtime could:
// when the build ends, a press nobody consumed closes the open dropdowns, and
// the select reports that to its caller next frame.
public readonly record struct SelectResult(Box Node, bool Toggled);

/// <summary>A select between <see cref="Select.Begin"/> and <see cref="End"/>.</summary>
public readonly struct SelectScope
{
    private readonly BibuiRuntime _runtime;
    private readonly Box _trigger;
    private readonly Box? _list;
    private readonly BibuiRuntime.ParentScope _inList;
    private readonly bool _toggled;

    internal SelectScope(BibuiRuntime runtime, Box trigger, Box? list, BibuiRuntime.ParentScope inList, bool toggled)
    {
        _runtime = runtime;
        _trigger = trigger;
        _list = list;
        _inList = inList;
        _toggled = toggled;
    }

    /// <summary>Whether the dropdown is open, so there is somewhere to build rows into.</summary>
    public bool Open => _list is not null;

    /// <summary>
    /// Stops building into the dropdown. The list asks after its rows, so a
    /// press on its padding stops there rather than reaching what is beneath.
    /// </summary>
    public SelectResult End()
    {
        if (_list is not null)
        {
            _inList.Dispose();
            _list.Claims = InteractionKinds.Press;
            _runtime.InteractionsFor(_list);
        }
        return new SelectResult(_trigger, _toggled);
    }
}
