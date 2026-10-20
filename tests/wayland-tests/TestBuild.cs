using System.Runtime.CompilerServices;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Theming;

/// <summary>
/// A test's app: its build, kept per runtime, and the frames it runs. An app
/// owns its loop and runs BeginBuild, its build, EndBuild; a test does the same
/// here, a frame at a time.
/// </summary>
internal static class TestBuild
{
    private static readonly ConditionalWeakTable<BibuiRuntime, Func<UiNode>> s_builds = new();

    /// <summary>Keeps <paramref name="build"/> as the test's, on a surface in a theme.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, ThemeTokens theme, SurfaceOptions surface)
    {
        s_builds.AddOrUpdate(runtime, build);
        runtime.Surface = surface;
        runtime.SetTheme(theme);
    }

    /// <inheritdoc cref="SetBuild(BibuiRuntime, Func{UiNode}, ThemeTokens, SurfaceOptions)"/>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, SurfaceOptions surface)
        => runtime.SetBuild(build, ThemeLoader.BibuiDark(), surface);

    /// <summary>One frame: BeginBuild, the test's build (what it returns linked under the root), EndBuild.</summary>
    public static void Step(this BibuiRuntime runtime)
    {
        runtime.BeginBuild();
        var tree = s_builds.TryGetValue(runtime, out var build) ? build()
            : throw new InvalidOperationException("This runtime has no test build: SetBuild first.");
        if (tree.Parent is null) UiNode.PushIntoParent(runtime.ParentInScope, tree);
        runtime.EndBuild();
    }
}
