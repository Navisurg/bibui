using Bibui.Core;
using Bibui.Core.Theming;

namespace Bibui.Core.Runtime;

/// <summary>Where a dialog's panel sits: centred, or docked to an edge as a sheet or drawer.</summary>
public enum DialogSide { Center, Left, Right, Top, Bottom }

/// <summary>
/// A dialog and what happened to it: its panel, and whether a press landed on
/// the scrim outside the panel — which the caller may treat as dismissing it.
/// </summary>
public readonly record struct DialogWidget(Box Panel, bool Dismissed)
{
    public static implicit operator UiNode(DialogWidget widget) => widget.Panel;

    /// <summary>So it goes straight into <c>Row(...)</c> or <c>Column(...)</c>.</summary>
}

/// <summary>
/// A modal dialog, built between <see cref="Begin"/> and <see cref="DialogScope.End"/>:
/// a scrim over the whole window and a panel on it. It is one of the runtime's
/// dropdown roots, so it is drawn over everything and nothing beneath it gets a
/// press or a hover while it is up. It owns nothing: the caller keeps whether it
/// is open and passes how far open it is, eased 0..1 —
/// <c>runtime.Animate(id, "open", open ? 1 : 0)</c>, asked every frame, open or
/// not, so opening and closing both travel — and builds it while that is above 0.
/// </summary>
public static class Dialog
{
    private const float Pad = 24f;
    private const float Radius = 12f;
    private const float Travel = 24f;
    private static readonly UiColor Scrim = UiColor.Hex("#07070B");
    private const float ScrimAlpha = 0.7f;

    /// <summary>
    /// Opens the dialog named <paramref name="id"/> and pushes its panel, so what
    /// is built until <see cref="DialogScope.End"/> goes into it. A centred panel
    /// is <paramref name="width"/> wide and as tall as what is built; a left or
    /// right one is <paramref name="width"/> wide and the window's height; a top
    /// or bottom one is the window's width and as tall as what is built.
    /// <paramref name="shown"/> 0..1 fades the scrim and slides the panel in.
    /// </summary>
    public static DialogScope Begin(BibuiRuntime runtime, string id, float shown,
        DialogSide side = DialogSide.Center, float width = 460f)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        shown = Math.Clamp(shown, 0f, 1f);

        // Opened in every frame it is built, so no press dismisses it.
        runtime.OpenDropDown(id);
        Box root = runtime.BeginDropDown(id)!;
        root.Sized(Size.Px(runtime.SurfaceWidth), Size.Px(runtime.SurfaceHeight)).At(0f, 0f);
        root.Style.BackgroundColor = Scrim.WithAlpha(ScrimAlpha * shown);
        bool across = side is DialogSide.Left or DialogSide.Right;
        root.ChildLayoutAxis = across ? LayoutAxis.X : LayoutAxis.Y;

        // Acquired under the node it sits in: what the runtime builds is linked
        // under the parent in scope, so it is never linked a second time.
        float hidden = 1f - shown;
        Box panel;
        runtime.BeginParent(root);
        switch (side)
        {
            case DialogSide.Center:
                // Centred both ways in the screen.
                runtime.BeginCenter();
                runtime.BeginCenterAcross();
                panel = Panel(runtime, id, shown, SemanticSize.Pixels(width), SemanticSize.ChildrenSum());
                panel.Style.CornerRadius = Radius;
                runtime.EndCenterAcross();
                runtime.EndCenter();
                // Slid down into place from a little above.
                root.ViewOffset = new System.Numerics.Vector2(0f, Travel * hidden);
                break;
            case DialogSide.Left or DialogSide.Right:
                if (side == DialogSide.Right) runtime.Spacer(Size.Fill);
                panel = Panel(runtime, id, shown, SemanticSize.Pixels(width), SemanticSize.ParentPct(1f));
                if (side == DialogSide.Left) runtime.Spacer(Size.Fill);
                // Slid in from its edge, its whole width.
                root.ViewOffset = new System.Numerics.Vector2((side == DialogSide.Right ? -1f : 1f) * width * hidden, 0f);
                break;
            default:
                if (side == DialogSide.Bottom) runtime.Spacer(Size.Fill);
                panel = Panel(runtime, id, shown, SemanticSize.ParentPct(1f), SemanticSize.ChildrenSum());
                if (side == DialogSide.Top) runtime.Spacer(Size.Fill);
                // As tall as it was drawn last frame; a first frame slides from just off its edge.
                float height = MathF.Max(panel.Extent.Y, Travel);
                root.ViewOffset = new System.Numerics.Vector2(0f, (side == DialogSide.Bottom ? -1f : 1f) * height * hidden);
                break;
        }
        runtime.EndParent(root);
        runtime.BeginParent(panel);
        return new DialogScope(runtime, root, panel);
    }

    // The raised panel: retained, so it can be asked about and remembers its height.
    private static Box Panel(BibuiRuntime runtime, string id, float shown, SemanticSize x, SemanticSize y)
    {
        var panel = runtime.BoxFromId(id + "-panel", InteractionKinds.Press);
        panel.ChildLayoutAxis = LayoutAxis.Y;
        (panel.SizeX, panel.SizeY) = (x, y);
        Style style = panel.Style;
        style.Padding = EdgeInsets.All(Pad);
        style.BackgroundColor = UiColor.Surface;
        style.BorderColor = UiColor.Border;
        var theme = runtime.Theme;
        style.BorderWidth = theme.Borders.Width;
        style.Shadow = theme.Elevation.Level(ElevationTokens.Modal);
        style.Opacity = shown;
        return panel;
    }
}

/// <summary>A dialog between <see cref="Dialog.Begin"/> and <see cref="End"/>.</summary>
public readonly struct DialogScope
{
    private readonly BibuiRuntime _runtime;
    private readonly Box _root;
    private readonly Box _panel;   // begun as the parent until End

    internal DialogScope(BibuiRuntime runtime, Box root, Box panel)
    {
        _runtime = runtime;
        _root = root;
        _panel = panel;
    }

    /// <summary>
    /// Stops building into the panel and returns it. The panel asks after what is
    /// in it, so a press on its padding stops there; the scrim asks last, so a
    /// press anywhere else on the window is the scrim's: <see cref="DialogWidget.Dismissed"/>.
    /// </summary>
    public DialogWidget End()
    {
        _runtime.EndParent(_panel);
        _runtime.InteractionsFor(_panel);
        _root.Claims = InteractionKinds.LeftPress;
        return new DialogWidget(_panel, _runtime.InteractionsFor(_root).Clicked);
    }
}
