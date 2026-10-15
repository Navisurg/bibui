using Bibui.Runtime;
using Bibui.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>Choosing the Wayland host for a window made with <see cref="BibuiWindow.Create"/>.</summary>
public static class WaylandWindow
{
    /// <summary>
    /// Opens <paramref name="window"/> on a new <see cref="WaylandPlatformHost"/>:
    /// <c>BibuiWindow.Create(runtime).WithWayland()</c>.
    /// </summary>
    public static BibuiWindow WithWayland(this BibuiWindow window, WaylandHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.WithHost(new WaylandPlatformHost(options));
    }
}
