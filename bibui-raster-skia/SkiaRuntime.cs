using Bibui.Runtime;

namespace Bibui.Raster.Skia;

/// <summary>Choosing Skia for a runtime made with <see cref="BibuiRuntime.Create"/>.</summary>
public static class SkiaRuntime
{
    /// <summary>
    /// Draws <paramref name="runtime"/> with a new <see cref="SkiaRasterBackend"/>
    /// <c>BibuiRuntime.Create().WithSkia()</c>.
    /// </summary>
    public static BibuiRuntime WithSkia(this BibuiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.WithRaster(new SkiaRasterBackend());
    }
}
