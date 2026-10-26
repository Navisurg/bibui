using System.Runtime.CompilerServices;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;

/// <summary>
/// A test's app: its build and window, kept per runtime, and the frames it runs.
/// An app owns its loop and runs BeginBuild, its build, EndBuild; a test does
/// the same here, a frame at a time.
/// </summary>
internal static class TestBuild
{
    private sealed record App(Func<UiNode> Build, WindowFrame Window);

    private static readonly ConditionalWeakTable<BibuiRuntime, App> s_apps = new();

    /// <summary>Keeps <paramref name="build"/> as the test's, on <paramref name="window"/> in a theme.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, ThemeTokens theme, WindowFrame window)
    {
        s_apps.AddOrUpdate(runtime, new App(build, window));
        runtime.SetTheme(theme);
    }

    /// <inheritdoc cref="SetBuild(BibuiRuntime, Func{UiNode}, ThemeTokens, WindowFrame)"/>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, WindowFrame window)
        => runtime.SetBuild(build, ThemeLoader.BibuiDark(), window);

    /// <summary>One frame on the test's window: BeginBuild, the test's build (what it returns linked under the root), EndBuild.</summary>
    public static void Step(this BibuiRuntime runtime)
    {
        var app = s_apps.TryGetValue(runtime, out var a) ? a
            : throw new InvalidOperationException("This runtime has no test build: SetBuild first.");
        runtime.BeginBuild(app.Window);
        var tree = app.Build();
        if (tree.Parent is null) UiNode.PushIntoParent(runtime.ParentInScope, tree);
        runtime.EndBuild();
    }
}
