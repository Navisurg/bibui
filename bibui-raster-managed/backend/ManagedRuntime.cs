using Bibui.Runtime;

namespace Bibui.Raster.Managed.Backend;

/// <summary>Choosing the managed rasterizer for a runtime made with <see cref="BibuiRuntime.Create"/>.</summary>
public static class ManagedRuntime
{
    /// <summary>
    /// Draws <paramref name="runtime"/> with a new <see cref="ManagedRasterBackend"/>:
    /// <c>BibuiRuntime.Create().WithManagedRasterizer()</c>. No SkiaSharp, and
    /// Native AOT clean.
    /// </summary>
    public static BibuiRuntime WithManagedRasterizer(this BibuiRuntime runtime,
        Antialiasing antialiasing = Antialiasing.BaselineCompatible)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.WithRaster(new ManagedRasterBackend(antialiasing));
    }
}
