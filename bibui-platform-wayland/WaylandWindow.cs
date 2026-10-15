using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>Choosing the Wayland host for a window made with <see cref="BibuiWindow.Create"/>.</summary>
public static class WaylandWindow
{
    /// <summary>
    /// Opens <paramref name="window"/> on a new <see cref="WaylandPlatformHost"/>:
    /// <c>BibuiWindow.Create(runtime).WithWayland()</c>. It is what
    /// <c>Create</c> opens on anyway; naming it links this package, so the
    /// default need not be loaded by name, and lets the options be given.
    /// </summary>
    public static BibuiWindow WithWayland(this BibuiWindow window, WaylandHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.WithHost(new WaylandPlatformHost(options));
    }
}

/// <summary>
/// Referencing this package makes Wayland the host <c>BibuiWindow.Create</c>
/// opens on (<see cref="PlatformHosts"/>).
/// </summary>
internal static class DefaultHost
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification =
        "Registering the default host is the point: it is what BibuiWindow.Create opens on for an app that references this package.")]
    internal static void Register() => PlatformHosts.SetDefault(static () => new WaylandPlatformHost());
}
