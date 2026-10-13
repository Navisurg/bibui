namespace ComponentGallery;

/// <summary>
/// The gallery shell's state: which page is shown and which sidebar group is
/// open. Each group of demo pages owns its own state record, kept here in a
/// slot so a page's values survive while another page is shown; the shell only
/// hands each slot to its pages and writes back what they return.
/// </summary>
record GalleryState(string Route, string? OpenSidebarGroup)
{
    /// <summary>Whether the sidebar is shown in place of the page, in a window too narrow to keep it beside it.</summary>
    public bool MenuOpen { get; init; }

    public InputsState Inputs { get; init; } = new();
    public DisplayState Display { get; init; } = new();
    public OverlaysState Overlays { get; init; } = new();
    public NavigationState Navigation { get; init; } = new();
    public LayoutState Layout { get; init; } = new();
}
