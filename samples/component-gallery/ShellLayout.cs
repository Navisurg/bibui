using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

/// <summary>
/// How the gallery's shell is laid out at a window width: the sidebar's width
/// (none when it is folded into a menu) and the content panel's padding. Read
/// from the surface every frame, so a resized window lays out again at once.
/// </summary>
/// <param name="SidebarWidth">The sidebar's width, or 0 when it is folded away behind the menu button.</param>
/// <param name="Padding">The content panel's padding.</param>
internal readonly record struct ShellLayout(float SidebarWidth, EdgeInsets Padding)
{
    /// <summary>
    /// Below this width the sidebar folds into a menu: here the narrower
    /// sidebar still leaves a page 596 px, as much as the widest demo row
    /// (Button's interactive row, a 560 px table) needs.
    /// </summary>
    internal const float Narrow = 840f;

    /// <summary>
    /// The smallest window every page is laid out whole in (checked by
    /// <c>check</c>): with the sidebar folded, a page 592 px wide.
    /// </summary>
    internal const int MinWidth = 640, MinHeight = 480;

    /// <summary>Below this width, and from <see cref="Narrow"/>, the sidebar is narrower.</summary>
    internal const float Medium = 1000f;

    /// <summary>The height of the bar that carries the menu button when the sidebar is folded.</summary>
    internal const float BarHeight = 52f;

    /// <summary>Whether the sidebar is folded into a menu.</summary>
    internal bool Folded => SidebarWidth == 0f;

    internal static ShellLayout For(float width) => width switch
    {
        < Narrow => new(0f, new EdgeInsets(20, 16, 24, 20)),
        < Medium => new(184f, new EdgeInsets(28, 20, 28, 28)),
        _ => new(220f, new EdgeInsets(32, 28, 32, 40)),
    };

    /// <summary>
    /// The width the content panel lays a page out in: the window, less the
    /// sidebar, the panel's padding and its scrollbar's lane.
    /// </summary>
    internal float ContentWidth(float windowWidth, float scrollbarLane) =>
        MathF.Max(0f, windowWidth - SidebarWidth - Padding.Left - Padding.Right - scrollbarLane);

    /// <summary>
    /// The width a page has in this frame's window: for a demo that lays out
    /// differently when it would not fit side by side.
    /// </summary>
    internal static float PageWidth(BibuiRuntime runtime) =>
        For(runtime.SurfaceWidth).ContentWidth(runtime.SurfaceWidth, Scrollbar.Lane);
}
