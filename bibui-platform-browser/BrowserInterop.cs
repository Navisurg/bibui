using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Bibui.Platform.Browser;

/// <summary>
/// The calls into bibui-browser.js, the package's module. It is a static web
/// asset of the package, served at <c>_content/Bibui.Platform.Browser/</c>
/// beside the app's <c>_framework/</c>, and imported once, before the first
/// window opens.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserInterop
{
    public const string ModuleName = "Bibui.Platform.Browser";

    // Resolved by the runtime against _framework/, where dotnet.runtime.js is.
    private const string ModuleUrl = "../_content/Bibui.Platform.Browser/bibui-browser.js";

    private static Task? _loading;

    /// <summary>Imports the module, once; every call returns the same task.</summary>
    public static Task LoadAsync() => _loading ??= JSHost.ImportAsync(ModuleName, ModuleUrl);

    [JSImport("attachById", ModuleName)]
    public static partial int AttachById(string id);

    [JSImport("attach", ModuleName)]
    public static partial int Attach(JSObject canvas);

    [JSImport("detach", ModuleName)]
    public static partial void Detach(int handle);

    [JSImport("poll", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static partial double[] Poll(int handle);

    [JSImport("strings", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.String>>]
    public static partial string[] Strings(int handle);

    [JSImport("present", ModuleName)]
    public static partial void Present(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height);

    [JSImport("setCursor", ModuleName)]
    public static partial void SetCursor(int handle, string css);

    [JSImport("writeClipboard", ModuleName)]
    public static partial void WriteClipboard(string text);

    [JSImport("start", ModuleName)]
    public static partial void Start([JSMarshalAs<JSType.Function<JSType.Number, JSType.Boolean>>] Func<double, bool> tick);

    [JSImport("wake", ModuleName)]
    public static partial void Wake();
}
