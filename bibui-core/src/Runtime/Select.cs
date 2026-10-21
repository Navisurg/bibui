using Bibui.Core;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;
using static Bibui.Core.Runtime.Select;

namespace Bibui.Core.Runtime;

/// <summary>
/// A select: a trigger that opens a dropdown, built between
/// <see cref="Begin"/> and <see cref="SelectScope.End"/>. Whatever the caller
/// builds in between goes into the dropdown — <see cref="Item"/> rows, or
/// rows of its own. The caller keeps what is chosen. The list is a dropdown
/// of the runtime's, named <c>id + "-list"</c>, which the trigger opens and
/// closes with a click; a press a box outside the select takes, or one nothing
/// takes, closes it too. The caller closes it with
/// <see cref="SelectScope.Close"/> when it applies a row's click.
/// </summary>
public static class Select
{
    internal const float PadX = 12f;
    internal const float PadY = 10f;
    internal const float Radius = 6f;
    internal const float Gap = 4f;
    internal const float ListPad = 4f;
    internal const float ItemPadX = 8f;
    internal const float ItemPadY = 8f;
    internal const float ItemRadius = 4f;

    /// <summary>
    /// Builds the trigger named <paramref name="id"/> showing
    /// <paramref name="text"/>, under the parent in scope, and — while its list
    /// is open — begins the list under where the trigger was drawn last frame
    /// and pushes it, so what is built until <see cref="SelectScope.End"/> goes
    /// into it. A click on the trigger opens the list, or closes it.
    /// </summary>
    public static SelectScope Begin(BibuiRuntime runtime, string id, ReadOnlySpan<char> text,
        float width = 220f, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        string listId = id + "-list";
        Box trigger = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        if (runtime.InteractionsFor(trigger).Clicked)
        {
            if (runtime.IsDropDownOpen(listId)) runtime.CloseDropDown(listId);
            else runtime.OpenDropDown(listId, trigger);
        }
        if (disabled) runtime.CloseDropDown(listId);
        bool open = runtime.IsDropDownOpen(listId);
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
        using (runtime.Parent(trigger))
        {
            runtime.Text(text, textColor: disabled ? TextColor.Disabled : TextColor.Primary);
            runtime.Spacer(Size.Fill);
            Icon chevron = runtime.Icon(open ? "chevron-up" : "chevron-down", size: 16f);
            chevron.Color = UiColor.TextSecondary;
        }

        if (runtime.BeginDropDown(listId) is not { } list) return new SelectScope(runtime, trigger, listId, list: null, default);

        // Under the trigger, where it was drawn last frame, and as wide: a
        // container may have stretched it past the width it asked for.
        list.SizeX = SemanticSize.Pixels(trigger.Extent.X > 0f ? trigger.Extent.X : width);
        list.SizeY = SemanticSize.ChildrenSum();
        list.At(trigger.Rect.X, trigger.Rect.Y + trigger.Extent.Y + Gap);
        list.Style.Padding = EdgeInsets.All(ListPad);
        list.Style.CornerRadius = Radius;
        list.Style.BorderWidth = theme.Borders.Width;
        list.Style.Shadow = theme.Elevation.Level(ElevationTokens.Overlay);
        list.Style.BorderColor = UiColor.Border;
        list.Style.BackgroundColor = UiColor.SurfaceRaised;
        return new SelectScope(runtime, trigger, listId, list, runtime.Parent(list));
    }


}

/// <summary>The Select component, called on the runtime that builds it.</summary>
public static class SelectExtensions
{
    /// <summary>
    /// A plain row of the dropdown: <paramref name="label"/>, raised when
    /// <paramref name="selected"/>, taking the press unless
    /// <paramref name="disabled"/>. Built between <see cref="Begin"/> and
    /// <see cref="SelectScope.End"/>.
    /// </summary>
    public static BibuiWidget SelectItem(this BibuiRuntime runtime, string id, ReadOnlySpan<char> label,
        bool selected = false, bool disabled = false)
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
        row.Style.BackgroundColor = selected ? UiColor.SurfaceRaisedHover : (UiColor?)null;
        row.Style.HoverBackgroundColor = disabled ? (UiColor?)null : UiColor.SurfaceRaisedHover;
        using (runtime.Parent(row))
        {
            runtime.Text(label, textColor: disabled ? TextColor.Disabled : TextColor.Primary);
        }
        return new BibuiWidget(row, interaction);
    }
}

/// <summary>A select between <see cref="Select.Begin"/> and <see cref="End"/>.</summary>
public readonly struct SelectScope
{
    internal readonly BibuiRuntime _runtime;
    internal readonly Box _trigger;
    internal readonly string _listId;
    internal readonly Box? _list;
    internal readonly BibuiRuntime.ParentScope _inList;

    internal SelectScope(BibuiRuntime runtime, Box trigger, string listId, Box? list, BibuiRuntime.ParentScope inList)
    {
        _runtime = runtime;
        _trigger = trigger;
        _listId = listId;
        _list = list;
        _inList = inList;
    }

    /// <summary>Whether the list is open, so there is somewhere to build rows into.</summary>
    public bool Open => _list is not null;

    /// <summary>Closes the list: a row was chosen. It is still built this frame.</summary>
    public void Close() => _runtime.CloseDropDown(_listId);

    /// <summary>
    /// Stops building into the list and returns the trigger. The list asks
    /// after its rows, so a press on its padding stops there rather than
    /// reaching what is beneath, and dismisses nothing.
    /// </summary>
    public Box End()
    {
        if (_list is not null)
        {
            _inList.Dispose();
            _list.Claims = InteractionKinds.Press;
            _runtime.InteractionsFor(_list);
        }
        return _trigger;
    }
}
