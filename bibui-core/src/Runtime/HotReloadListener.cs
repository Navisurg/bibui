using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Bibui.Core.Runtime.HotReloadListener))]

namespace Bibui.Core.Runtime;

/// <summary>
/// Integrates with dotnet-watch hot reload. Call Register() once after
/// creating a BibuiRuntime to receive live updates when source files change.
///
/// Usage:
///   HotReloadListener.Register(runtime.RequestFrame);
///   dotnet watch run --project samples/my-app/my-app.csproj
/// </summary>
public static class HotReloadListener
{
    private static readonly List<Action> _callbacks = new();

    public static void Register(Action onUpdate)
    {
        lock (_callbacks) _callbacks.Add(onUpdate);
    }

    // Called by the dotnet watch runtime before patching
    internal static void ClearCache(Type[]? types) { }

    // Called by the dotnet watch runtime after patching
    internal static void UpdateApplication(Type[]? types)
    {
        lock (_callbacks)
            foreach (var cb in _callbacks)
                cb();
    }
}
