using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Bibui.Raster;

namespace Bibui.Runtime.Platform;

/// <summary>
/// The host <c>BibuiWindow.Create(runtime)</c> opens its window on when the app
/// chooses none: the raw Wayland one, in Bibui.Platform.Wayland.
///
/// <para>bibui references no host, so the package that provides the default
/// registers it from its module initializer, and when nothing has, the
/// assembly is loaded by name and its initializer run, as
/// <see cref="RasterBackends"/> does for the default backend. Trimming cannot
/// follow a load by name, which is why <c>BibuiWindow.Create</c> warns under
/// a trimmed or Native AOT publish (IL2026); such an app passes the host:
/// <c>new BibuiWindow(runtime, new WaylandPlatformHost())</c>.</para>
/// </summary>
public static class PlatformHosts
{
    /// <summary>The assembly that registers the default host when it is loaded.</summary>
    public const string DefaultProvider = "Bibui.Platform.Wayland";

    private static Func<IPlatformHost>? s_default;

    /// <summary>
    /// Makes <paramref name="factory"/> the host <c>BibuiWindow.Create</c> opens
    /// on when none is chosen. The last call wins.
    /// </summary>
    public static void SetDefault(Func<IPlatformHost> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        s_default = factory;
    }

    /// <summary>A new default host, or an exception that says what to reference.</summary>
    [RequiresUnreferencedCode(TrimMessage)]
    public static IPlatformHost CreateDefault()
    {
        if (s_default is null) RasterBackends.RunProvider(DefaultProvider);
        return s_default?.Invoke() ?? throw new InvalidOperationException(
            RuntimeFeature.IsDynamicCodeSupported
                ? "No platform host: BibuiWindow.Create(runtime) opens on Wayland unless told otherwise. " +
                  "Reference the Bibui.Platform.Wayland package, or choose one: " +
                  "BibuiWindow.Create(runtime).WithSilk() (Bibui.Platform.Silk) / .WithWayland()."
                : "No platform host: under Native AOT, Bibui.Platform.Wayland is compiled in only when the app names " +
                  "one of its types. Pass the host: new BibuiWindow(runtime, new WaylandPlatformHost()).");
    }

    /// <summary>What a trimmed or Native AOT publish says where <c>BibuiWindow.Create</c> is called (IL2026).</summary>
    internal const string TrimMessage =
        "BibuiWindow.Create loads the default platform host by name (Bibui.Platform.Wayland) unless a host is chosen, " +
        "which trimming and Native AOT do not keep unless the app names one of its types. Pass the host instead: " +
        "new BibuiWindow(runtime, new WaylandPlatformHost()).";
}
