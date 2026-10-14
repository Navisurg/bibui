using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// What <see cref="Expander.Build"/> built: the outlined frame, the body to
/// build the content into — null while closed — and whether the header was
/// clicked this frame.
/// </summary>
public readonly record struct ExpanderWidget(Box Node, Box? Body, Interaction Interaction)
{
    /// <summary>The header was clicked: the caller flips whether it is open.</summary>
    public bool Toggled => Interaction.Clicked;

    public static implicit operator UiNode(ExpanderWidget widget) => widget.Node;

    /// <summary>So it goes straight into <c>Row(...)</c> or <c>Column(...)</c>.</summary>
    public static implicit operator Arrange.Item(ExpanderWidget widget) => widget.Node;
}

/// <summary>
/// An expander: an outlined frame whose header — a bold title and a chevron —
/// takes the press, and under it, while open, a rule and a body the caller
/// builds the content into. It owns no state: it is told whether it is open and
/// reports a click on the header; the caller flips its own flag. An accordion
/// is several of these, the caller keeping which one is open.
/// </summary>
public static class Expander
{
    /// <summary>The header's height, at the theme's default density.</summary>
    public const float HeaderHeight = 44f;

    private const float PadX = 12f;     // Spacing.Md
    private const float BodyTop = 8f;   // Spacing.Sm
    private const float Radius = 6f;    // Radii.Md
    private const float ChevronSize = 16f;

    /// <summary>
    /// Builds the expander named <paramref name="id"/> under the parent in
    /// scope, as wide as all of it, titled <paramref name="title"/>. While
    /// <paramref name="open"/>, <see cref="ExpanderWidget.Body"/> is a column
    /// as wide as the frame and as tall as what is built into it: push it to
    /// build the content, and size it to fix the height.
    /// </summary>
    public static ExpanderWidget Build(BibuiRuntime runtime, string id, ReadOnlySpan<char> title, bool open)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        var theme = runtime.Theme;
        Box frame = runtime.Box();
        frame.ChildLayoutAxis = LayoutAxis.Y;
        frame.SizeX = SemanticSize.ParentPct(1f);
        frame.SizeY = SemanticSize.ChildrenSum();
        frame.Style.BorderColor = UiColor.Border;
        frame.Style.BorderWidth = theme.Borders.Width;
        frame.Style.CornerRadius = Radius;

        Box? body = null;
        Interaction interaction;
        using (runtime.PushParent(frame))
        {
            Box header = runtime.BoxFromId(id, InteractionKinds.LeftPress);
            interaction = runtime.InteractionsFor(header);
            header.ChildLayoutAxis = LayoutAxis.X;
            header.SizeX = SemanticSize.ParentPct(1f);
            header.SizeY = SemanticSize.Pixels(theme.Density.Of(HeaderHeight));
            header.Style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: 0f);
            header.Style.CornerRadius = Radius;
            using (runtime.PushParent(header))
            {
                Box titleCell = Cell(runtime);
                using (runtime.PushParent(titleCell))
                {
                    Arrange.Spring(titleCell);
                    using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(title, textColor: TextColor.Primary);
                    Arrange.Spring(titleCell);
                }
                Arrange.Spring(header);
                Box chevronCell = Cell(runtime);
                using (runtime.PushParent(chevronCell))
                {
                    Arrange.Spring(chevronCell);
                    Icon chevron = runtime.Icon(open ? "chevron-up" : "chevron-down", ChevronSize);
                    chevron.Color = UiColor.TextSecondary;
                    Arrange.Spring(chevronCell);
                }
            }

            if (open)
            {
                Box rule = runtime.Box();
                rule.SizeX = SemanticSize.ParentPct(1f);
                rule.SizeY = SemanticSize.Pixels(theme.Borders.Width);
                rule.Style.BackgroundColor = UiColor.Border;

                body = runtime.Box();
                body.ChildLayoutAxis = LayoutAxis.Y;
                body.SizeX = SemanticSize.ParentPct(1f);
                body.SizeY = SemanticSize.ChildrenSum();
                body.Style.Padding = new EdgeInsets(BodyTop, PadX, PadX, PadX);
            }
        }
        return new ExpanderWidget(frame, body, interaction);
    }

    // A cell the header's height, under the parent in scope: what is built
    // into it between two springs is centred down the header.
    private static Box Cell(BibuiRuntime runtime)
    {
        Box cell = runtime.Box();
        cell.ChildLayoutAxis = LayoutAxis.Y;
        cell.SizeX = SemanticSize.ChildrenSum();
        cell.SizeY = SemanticSize.ParentPct(1f);
        return cell;
    }
}
