using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Bibui.Raster;

/// <summary>
/// The raster backend the parameterless <c>new BibuiRenderer()</c> and
/// <c>new BibuiRuntime()</c> draw with.
///
/// <para>bibui names no backend: Skia lives in Bibui.Raster.Skia, which
/// references this assembly and so cannot be constructed from it. The package
/// that provides the default registers it — Bibui.Raster.Skia does so from its
/// module initializer — so an app that references the Skia package keeps
/// writing <c>new BibuiRuntime()</c>, and one that does not (an app on the
/// managed rasterizer, which passes <c>new ManagedRasterBackend()</c>) carries no Skia.</para>
///
/// <para>A module initializer runs when its assembly is first used, and under
/// the JIT an app that only writes <c>new BibuiRuntime()</c> never uses the Skia
/// assembly. So when nothing has registered, <see cref="CreateDefault"/> loads
/// <see cref="DefaultProvider"/> by name — an assembly the app already ships,
/// or nothing — and runs its module initializer. No type is looked up and no
/// member is reflected on.</para>
///
/// <para>Loading an assembly by name is what trimming cannot follow: under
/// Native AOT or a trimmed publish, Bibui.Raster.Skia is kept only if the app
/// uses one of its types, so the default may be gone at run time. That
/// is why the parameterless constructors carry
/// <see cref="RequiresUnreferencedCodeAttribute"/>: an AOT or trimmed publish
/// that calls them warns (IL2026) at publish time, naming the fix, instead of
/// throwing at start. An app that publishes AOT passes the backend:
/// <c>new BibuiRuntime(new ManagedRasterBackend())</c> on the Wayland + managed
/// route, the one that promises AOT.</para>
/// </summary>
public static class RasterBackends
{
    /// <summary>The assembly that registers the default backend when it is loaded.</summary>
    public const string DefaultProvider = "Bibui.Raster.Skia";

    /// <summary>
    /// The assembly that registers the managed rasterizer when it is loaded:
    /// what <c>BibuiRuntime.Create()</c> draws with unless the app picks another.
    /// </summary>
    public const string ManagedProvider = "Bibui.Raster.Managed.Backend";

    private static Func<IRasterBackend>? s_default;
    private static Func<IRasterBackend>? s_managed;

    /// <summary>
    /// Makes <paramref name="factory"/> the backend the parameterless
    /// constructors draw with. The last call wins.
    /// </summary>
    public static void SetDefault(Func<IRasterBackend> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        s_default = factory;
    }

    /// <summary>A new default backend, or an exception that says what to reference.</summary>
    [RequiresUnreferencedCode(TrimMessage)]
    public static IRasterBackend CreateDefault()
    {
        if (s_default is null) RunProvider(DefaultProvider);
        return s_default?.Invoke() ?? throw new InvalidOperationException(
            RuntimeFeature.IsDynamicCodeSupported
                ? "No default raster backend: reference the Bibui.Raster.Skia package (it registers SkiaRasterBackend), " +
                  "or pass one: new BibuiRuntime(backend) / new BibuiRenderer(backend)."
                : "No default raster backend: under Native AOT, Bibui.Raster.Skia is compiled in only when the app names " +
                  "one of its types, so the parameterless constructor cannot find it. Pass the backend: " +
                  "new BibuiRuntime(new ManagedRasterBackend()) or new BibuiRuntime(new SkiaRasterBackend()).");
    }

    /// <summary>
    /// Makes <paramref name="factory"/> the managed rasterizer that
    /// <see cref="CreateManaged"/> returns. Bibui.Raster.Managed.Backend calls
    /// it from its module initializer.
    /// </summary>
    public static void SetManaged(Func<IRasterBackend> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        s_managed = factory;
    }

    /// <summary>
    /// A new managed rasterizer — <c>BibuiRuntime.Create()</c>'s backend when the
    /// app chooses none — or an exception that says what to reference. Loaded
    /// by name, like <see cref="CreateDefault"/>, and for the same reason.
    /// </summary>
    [RequiresUnreferencedCode(ManagedTrimMessage)]
    public static IRasterBackend CreateManaged()
    {
        if (s_managed is null) RunProvider(ManagedProvider);
        return s_managed?.Invoke() ?? throw new InvalidOperationException(
            "No raster backend: BibuiRuntime.Create() draws with the managed rasterizer unless told otherwise. " +
            "Reference the Bibui.Raster.Managed.Backend package, or choose one: " +
            "BibuiRuntime.Create().WithSkia() (Bibui.Raster.Skia) / .WithManagedRasterizer().");
    }

    /// <summary>
    /// What a trimmed or Native AOT publish says where a parameterless
    /// constructor is called (IL2026).
    /// </summary>
    internal const string TrimMessage =
        "The default raster backend is loaded by name (Bibui.Raster.Skia), which trimming and Native AOT do not keep " +
        "unless the app names one of its types. Pass the backend instead: " +
        "new BibuiRuntime(new SkiaRasterBackend()) / new BibuiRenderer(new SkiaRasterBackend()).";

    /// <summary>What a trimmed or Native AOT publish says where <c>BibuiRuntime.Create()</c> is called (IL2026).</summary>
    internal const string ManagedTrimMessage =
        "BibuiRuntime.Create() loads the managed rasterizer by name (Bibui.Raster.Managed.Backend) unless a backend is " +
        "chosen, which trimming and Native AOT do not keep unless the app names one of its types. Pass the backend " +
        "instead: new BibuiRuntime(new ManagedRasterBackend()).";

    /// <summary>
    /// Loads <paramref name="name"/>, if the app ships it, and runs its module
    /// initializer, which registers what it provides. No type is looked up.
    /// </summary>
    internal static void RunProvider(string name)
    {
        Assembly provider;
        try { provider = Assembly.Load(new AssemblyName(name)); }
        catch (FileNotFoundException) { return; }
        catch (FileLoadException) { return; }
        RuntimeHelpers.RunModuleConstructor(provider.ManifestModule.ModuleHandle);
    }
}
