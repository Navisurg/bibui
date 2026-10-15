namespace Bibui.Runtime.Platform;

/// <summary>Choosing the Silk host for a window made with <see cref="BibuiWindow.Create"/>.</summary>
public static class SilkWindow
{
    /// <summary>
    /// Opens <paramref name="window"/> on a new <see cref="SilkPlatformHost"/>
    /// (GLFW + OpenGL, cross-platform) in place of the Wayland host it would
    /// default to: <c>BibuiWindow.Create(runtime).WithSilk()</c>.
    /// </summary>
    public static BibuiWindow WithSilk(this BibuiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.WithHost(new SilkPlatformHost());
    }
}
