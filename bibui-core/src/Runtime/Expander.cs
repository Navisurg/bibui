using Bibui.Core;
using Bibui.Core.TextEdit;
using static Bibui.Core.Runtime.Expander;

namespace Bibui.Core.Runtime;

/// <summary>
/// What <see cref="ExpanderExtensions.Expander"/> built: the outlined frame, the body to
/// build the content into — null while closed — and whether the header was
/// clicked this frame.
/// </summary>
public readonly record struct ExpanderWidget(Box Node, Box? Body, Interaction Interaction)
{
    /// <summary>The header was clicked: the caller flips whether it is open.</summary>
    public bool Toggled => Interaction.Clicked;

    public static implicit operator UiNode(ExpanderWidget widget) => widget.Node;

    /// <summary>So it goes straight into <c>Row(...)</c> or <c>Column(...)</c>.</summary>
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

    internal const float PadX = 12f;     // Spacing.Md
    internal const float BodyTop = 8f;   // Spacing.Sm
    internal const float Radius = 6f;    // Radii.Md
    internal const float ChevronSize = 16f;
}

/// <summary>The Expander component, called on the runtime that builds it.</summary>
public static class ExpanderExtensions
{
    /// <summary>
    /// Builds the expander named <paramref name="id"/> under the parent in
    /// scope, as wide as all of it, titled <paramref name="title"/>. While
    /// <paramref name="open"/>, <see cref="ExpanderWidget.Body"/> is a column
    /// as wide as the frame and as tall as what is built into it: push it to
    /// build the content, and size it to fix the height.
    /// </summary>
    public static ExpanderWidget Expander(this BibuiRuntime runtime, string id, ReadOnlySpan<char> title, bool open)
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
        using (runtime.Parent(frame))
        {
            Box header = runtime.BoxFromId(id, InteractionKinds.LeftPress);
            interaction = runtime.InteractionsFor(header);
            header.ChildLayoutAxis = LayoutAxis.X;
            header.SizeX = SemanticSize.ParentPct(1f);
            header.SizeY = SemanticSize.Pixels(theme.Density.Of(HeaderHeight));
            header.Style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: 0f);
            header.Style.CornerRadius = Radius;
            using (runtime.Parent(header))
            {
                runtime.BeginCenterAcross();
                runtime.Text(title, textColor: TextColor.Primary, weight: FontWeight.Bold);
                runtime.EndCenterAcross();
                runtime.Spacer(Size.Fill);
                runtime.BeginCenterAcross();
                Icon chevron = runtime.Icon(open ? "chevron-up" : "chevron-down", ChevronSize);
                chevron.Color = UiColor.TextSecondary;
                runtime.EndCenterAcross();
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
}
