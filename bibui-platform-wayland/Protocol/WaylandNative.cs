using System.Runtime.InteropServices;

namespace Bibui.Platform.Wayland.Protocol;

/// <summary>
/// libwayland-client's entry points. Blittable signatures only, and the
/// assembly disables runtime marshalling, so nothing here needs a marshalling
/// stub or reflection: it compiles the same under Native AOT.
///
/// <para>Requests go through <c>wl_proxy_marshal_array_flags</c> (libwayland
/// 1.20 and later) rather than the variadic <c>wl_proxy_marshal_flags</c> that
/// wayland-scanner's inline helpers call: .NET cannot call a C variadic
/// portably, and the array form is what the variadic one forwards to.</para>
/// </summary>
internal static unsafe partial class WaylandNative
{
    public const string Library = "libwayland-client.so.0";

    /// <summary>Destroy the proxy after marshalling: the flag every destructor request carries.</summary>
    public const uint WL_MARSHAL_FLAG_DESTROY = 1;

    [DllImport(Library, ExactSpelling = true)] public static extern nint wl_display_connect(byte* name);
    [DllImport(Library, ExactSpelling = true)] public static extern void wl_display_disconnect(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_get_fd(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_roundtrip(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_dispatch(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_dispatch_pending(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_prepare_read(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_read_events(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern void wl_display_cancel_read(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_flush(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern int wl_display_get_error(nint display);

    [DllImport(Library, ExactSpelling = true)]
    public static extern nint wl_proxy_marshal_array_flags(
        nint proxy, uint opcode, WlInterface* iface, uint version, uint flags, WlArgument* args);

    [DllImport(Library, ExactSpelling = true)] public static extern int wl_proxy_add_listener(nint proxy, void* implementation, void* data);
    [DllImport(Library, ExactSpelling = true)] public static extern void wl_proxy_destroy(nint proxy);
    [DllImport(Library, ExactSpelling = true)] public static extern uint wl_proxy_get_version(nint proxy);
    [DllImport(Library, ExactSpelling = true)] public static extern void* wl_proxy_get_user_data(nint proxy);
    [DllImport(Library, ExactSpelling = true)] public static extern void wl_proxy_set_user_data(nint proxy, void* data);

    private static nint _library;

    /// <summary>True when libwayland-client can be loaded on this machine.</summary>
    public static bool IsAvailable => NativeLibrary.TryLoad(Library, out _);

    /// <summary>
    /// One of the core protocol's interface tables, which libwayland-client
    /// exports (<c>wl_surface_interface</c> and the rest) — the same tables
    /// wayland-scanner generated for libwayland itself, so ours cannot drift
    /// from the library we talk through.
    ///
    /// <para>An interface newer than the installed libwayland (wl_fixes is 1.24)
    /// becomes a name-only stub instead of failing the whole table set. A stub
    /// has no messages, so it must not be bound; it still type-checks as an
    /// argument, which is all the other tables need of it.</para>
    /// </summary>
    public static WlInterface* CoreInterface(string name)
    {
        if (_library == 0) _library = NativeLibrary.Load(Library);
        return NativeLibrary.TryGetExport(_library, name + "_interface", out var table)
            ? (WlInterface*)table
            : WlTables.Stub(name);
    }

    /// <summary>The exception a request sent above its proxy's version throws, rather than a protocol error.</summary>
    public static InvalidOperationException TooOld(string request, uint since, uint version) =>
        new($"{request} needs version {since}, but the proxy is version {version}");
}
