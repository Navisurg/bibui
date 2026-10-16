using Bibui.Runtime;
using Bibui.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>Choosing the Wayland host for a window made with <see cref="BibuiWindow.Create"/>.</summary>
public static class WaylandWindow
{
    /// <summary>
    /// Opens <paramref name="window"/> on a new <see cref="WaylandPlatformHost"/>
    /// that draws frames with <paramref name="draw"/>, the app's renderer:
    /// <c>BibuiWindow.Create(runtime).WithWayland(managed.Draw)</c>.
    /// </summary>
    public static BibuiWindow WithWayland(this BibuiWindow window, Bibui.Draw.DrawToPixels draw, WaylandHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.WithHost(new WaylandPlatformHost(draw, options));
    }
}
