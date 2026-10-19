using System.Reflection;
using System.Runtime.InteropServices;
using Foundation;

namespace Bibui.Runtime.Platform;

/// <summary>
/// Points bibui's text imports at the FreeType and HarfBuzz this package ships.
/// bibui imports them by their Linux names (libfreetype.so.6,
/// libharfbuzz.so.0), the system's there; iOS has no system copy, so the app
/// carries them as frameworks (native/build-text-natives.sh) and this resolves
/// those names to them. Registered once, before anything in bibui calls
/// FreeType: <see cref="SilkIosWindow.RunApp"/> does it first.
/// </summary>
internal static class TextLibraries
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(BibuiRuntime).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) => name switch
    {
        "libfreetype.so.6" => Load("freetype"),
        "libharfbuzz.so.0" => Load("harfbuzz"),
        _ => 0,
    };

    // Embedded frameworks are in the bundle's Frameworks folder.
    private static nint Load(string framework) =>
        NativeLibrary.Load(Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath!, $"{framework}.framework", framework));
}
