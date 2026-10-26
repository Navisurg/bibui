using System.Runtime.CompilerServices;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;

/// <summary>
/// A test's app: its build and its window, kept per runtime, and the frames it
/// runs. An app owns its loop and runs BeginBuild on its window, its build,
/// EndBuild; a test does the same here, a frame at a time.
/// </summary>
internal static class TestBuild
{
    private sealed class App(Func<UiNode> build, WindowFrame window)
    {
        public readonly Func<UiNode> Build = build;
        public WindowFrame Window = window;
    }

    private static readonly ConditionalWeakTable<BibuiRuntime, App> s_apps = new();

    /// <summary>
    /// A window <paramref name="logicalWidth"/> × <paramref name="logicalHeight"/>
    /// logical px at <paramref name="scale"/>, 60 Hz: its framebuffer is the
    /// physical size a window would give it (<see cref="WindowFrame.PhysicalSize"/>).
    /// </summary>
    public static WindowFrame Window(int logicalWidth, int logicalHeight, float scale = 1f) =>
        new(WindowFrame.PhysicalSize(logicalWidth, scale), WindowFrame.PhysicalSize(logicalHeight, scale), scale, 60f);

    /// <summary>Keeps <paramref name="build"/> as the test's, on <paramref name="window"/> in a theme.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, ThemeTokens theme, WindowFrame window)
    {
        s_apps.AddOrUpdate(runtime, new App(build, window));
        runtime.SetTheme(theme);
    }

    /// <inheritdoc cref="SetBuild(BibuiRuntime, Func{UiNode}, ThemeTokens, WindowFrame)"/>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, WindowFrame window)
        => runtime.SetBuild(build, ThemeLoader.BibuiDark(), window);

    /// <summary>Moves the test's app to <paramref name="window"/>: a resize, from the next frame on.</summary>
    public static void SetWindow(this BibuiRuntime runtime, WindowFrame window) => AppOf(runtime).Window = window;

    /// <summary>The window the test's frames are built on.</summary>
    public static WindowFrame WindowOf(this BibuiRuntime runtime) => AppOf(runtime).Window;

    private static App AppOf(BibuiRuntime runtime) => s_apps.TryGetValue(runtime, out var app) ? app
        : throw new InvalidOperationException("This runtime has no test build: SetBuild first.");

    /// <summary>One frame on the test's window: BeginBuild, the test's build (what it returns linked under the root), EndBuild.</summary>
    public static void Step(this BibuiRuntime runtime)
    {
        var app = AppOf(runtime);
        runtime.BeginBuild(app.Window);
        var tree = app.Build();
        if (tree.Parent is null) UiNode.PushIntoParent(runtime.ParentInScope, tree);
        runtime.EndBuild();
    }
}
