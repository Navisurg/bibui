using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Bibui.Runtime;

namespace Bibui.Raster.Managed.Backend;

/// <summary>Choosing the managed rasterizer for a runtime made with <see cref="BibuiRuntime.Create"/>.</summary>
public static class ManagedRuntime
{
    /// <summary>
    /// Draws <paramref name="runtime"/> with a new <see cref="ManagedRasterBackend"/>:
    /// <c>BibuiRuntime.Create().WithManagedRasterizer()</c>. It is what
    /// <c>Create()</c> draws with anyway; naming it links this package, so the
    /// default need not be loaded by name, and lets the antialiasing be chosen.
    /// </summary>
    public static BibuiRuntime WithManagedRasterizer(this BibuiRuntime runtime,
        Antialiasing antialiasing = Antialiasing.BaselineCompatible)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.WithRaster(new ManagedRasterBackend(antialiasing));
    }
}

/// <summary>
/// Referencing this package makes the managed rasterizer what
/// <c>BibuiRuntime.Create()</c> draws with (<see cref="RasterBackends"/>).
/// </summary>
internal static class DefaultBackend
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification =
        "Registering the managed backend is the point: it is what BibuiRuntime.Create() draws with for an app that references this package.")]
    internal static void Register() => RasterBackends.SetManaged(static () => new ManagedRasterBackend());
}
