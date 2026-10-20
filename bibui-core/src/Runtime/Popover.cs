using Bibui.Core;
using Bibui.Core.Theming;

namespace Bibui.Core.Runtime;

/// <summary>
/// A popover: a raised panel under a box the caller holds, built between
/// <see cref="Begin"/> and <see cref="PopoverScope.End"/>. Whatever the caller
/// builds in between goes into it. It has no trigger of its own — the caller
/// builds one, a button or anything else it can ask about, which opens it with
/// <c>runtime.OpenDropDown(id, trigger)</c>.
///
/// <para>It is a dropdown of the runtime's, so it is drawn over everything and
/// the rect it was drawn at last frame is its own: nothing beneath it gets a
/// press there. It is open until <see cref="BibuiRuntime.CloseDropDown"/> or a
/// press that dismisses it: one a box outside it and its trigger takes, or one
/// nothing takes. A caller that keeps it open while the pointer is over it
/// opens it every frame it is.</para>
/// </summary>
public static class Popover
{
    private const float Pad = 12f;
    private const float Radius = 8f;
    private const float Gap = 8f;

    /// <summary>
    /// Begins the popover named <paramref name="id"/>, when it is open,
    /// <paramref name="width"/> px wide, centred under <paramref name="anchor"/>
    /// where it was drawn last frame, and pushes it, so what is built until
    /// <see cref="PopoverScope.End"/> goes into it. As tall as what is built.
    /// Null when it is not open.
    /// </summary>
    public static PopoverScope? Begin(BibuiRuntime runtime, string id, UiNode anchor, float width = 280f)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(anchor);

        if (runtime.BeginDropDown(id) is not { } panel) return null;
        panel.SizeX = SemanticSize.Pixels(width);
        panel.SizeY = SemanticSize.ChildrenSum();
        panel.At(anchor.Rect.X + (anchor.Extent.X - width) / 2f, anchor.Rect.Y + anchor.Extent.Y + Gap);
        var theme = runtime.Theme;
        Style style = panel.Style;
        style.Padding = EdgeInsets.All(Pad);
        style.CornerRadius = Radius;
        style.BorderWidth = theme.Borders.Width;
        style.Shadow = theme.Elevation.Level(ElevationTokens.Overlay);
        style.BorderColor = UiColor.Border;
        style.BackgroundColor = UiColor.SurfaceRaised;
        return new PopoverScope(runtime, panel, runtime.PushParent(panel));
    }
}

/// <summary>A popover between <see cref="Popover.Begin"/> and <see cref="End"/>.</summary>
public readonly struct PopoverScope
{
    private readonly BibuiRuntime _runtime;
    private readonly Box _panel;
    private readonly BibuiRuntime.ParentScope _inPanel;

    internal PopoverScope(BibuiRuntime runtime, Box panel, BibuiRuntime.ParentScope inPanel)
    {
        _runtime = runtime;
        _panel = panel;
        _inPanel = inPanel;
    }

    /// <summary>
    /// Stops building into the popover and returns its panel and what happened
    /// to it. The panel asks after what is in it, so a press on its padding
    /// stops there rather than reaching what is beneath; and it reports the
    /// pointer over it, for a caller that keeps it open while it is.
    /// </summary>
    public BibuiWidget End()
    {
        _inPanel.Dispose();
        _panel.Claims = InteractionKinds.Press;
        return new BibuiWidget(_panel, _runtime.InteractionsFor(_panel));
    }
}
