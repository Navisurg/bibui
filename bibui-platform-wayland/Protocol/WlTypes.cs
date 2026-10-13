using System.Runtime.InteropServices;
using System.Text;

namespace Bibui.Platform.Wayland.Protocol;

/// <summary>struct wl_message: one request or event, as libwayland marshals it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlMessage
{
    public byte* Name;
    public byte* Signature;
    public WlInterface** Types;
}

/// <summary>struct wl_interface: what libwayland reads to marshal and demarshal an object's messages.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlInterface
{
    public byte* Name;
    public int Version;
    public int MethodCount;
    public WlMessage* Methods;
    public int EventCount;
    public WlMessage* Events;
}

/// <summary>union wl_argument — 8 bytes on LP64.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct WlArgument
{
    [FieldOffset(0)] public int I;
    [FieldOffset(0)] public uint U;
    [FieldOffset(0)] public int F;    // wl_fixed_t
    [FieldOffset(0)] public nint S;   // const char*
    [FieldOffset(0)] public nint O;   // struct wl_object* (a proxy), or NULL for a new_id
    [FieldOffset(0)] public nint A;   // struct wl_array*
    [FieldOffset(0)] public int H;    // fd
}

/// <summary>struct wl_array.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlArray
{
    public nuint Size;
    public nuint Alloc;
    public void* Data;

    /// <summary>The array's contents read as <typeparamref name="T"/>s (the XML says which).</summary>
    public readonly ReadOnlySpan<T> As<T>() where T : unmanaged =>
        Data == null ? default : new ReadOnlySpan<T>(Data, (int)(Size / (nuint)sizeof(T)));
}

/// <summary>wl_fixed_t: a signed 24.8 fixed-point number.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct WlFixed(int raw)
{
    public readonly int Raw = raw;
    public double ToDouble() => Raw / 256.0;
    public static WlFixed FromDouble(double d) => new((int)Math.Round(d * 256.0));
    public override string ToString() => ToDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A typed proxy: one generated struct per protocol interface, holding the
/// <c>wl_proxy*</c>. The static members let generic code (binding a global,
/// for one) reach the interface table without reflection.
/// </summary>
internal unsafe interface IWlProxy<TSelf> where TSelf : struct, IWlProxy<TSelf>
{
    /// <summary>The interface table libwayland marshals this object's messages with.</summary>
    static abstract WlInterface* Interface { get; }

    /// <summary>The highest version of the interface these bindings speak — the version in the XML.</summary>
    static abstract uint MaxVersion { get; }

    static abstract TSelf FromHandle(nint handle);

    nint Handle { get; }
}

/// <summary>UTF-8, NUL-terminated, in native memory: what a request's string argument is.</summary>
internal static unsafe class WlUtf8
{
    public static byte* Alloc(string? s)
    {
        if (s is null) return null;
        int n = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, n));
        p[n] = 0;
        return p;
    }

    public static void Free(byte* p)
    {
        if (p != null) NativeMemory.Free(p);
    }

    /// <summary>An event's string argument, or null.</summary>
    public static string? Read(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);
}

/// <summary>
/// Builds the interface tables for protocols libwayland does not export.
/// Tables refer to each other in cycles (xdg_surface.get_popup takes an
/// xdg_surface), so each is allocated first and filled afterwards.
/// </summary>
internal static unsafe class WlTables
{
    public static WlInterface* New() => (WlInterface*)NativeMemory.AllocZeroed((nuint)sizeof(WlInterface));

    /// <summary>
    /// An interface that is only ever an argument's type, from a protocol we do
    /// not bind. libwayland compares interfaces by name, so a name is enough.
    /// </summary>
    public static WlInterface* Stub(string name)
    {
        var i = New();
        i->Name = WlUtf8.Alloc(name);
        return i;
    }

    public static void Define(WlInterface* i, string name, int version, WlMessage* methods, int methodCount,
                              WlMessage* events, int eventCount)
    {
        i->Name = WlUtf8.Alloc(name);
        i->Version = version;
        i->MethodCount = methodCount;
        i->Methods = methods;
        i->EventCount = eventCount;
        i->Events = events;
    }

    public static WlMessage* Messages(int count) =>
        count == 0 ? null : (WlMessage*)NativeMemory.AllocZeroed((nuint)(sizeof(WlMessage) * count));

    public static void Message(WlMessage* m, string name, string signature, params ReadOnlySpan<nint> types)
    {
        m->Name = WlUtf8.Alloc(name);
        m->Signature = WlUtf8.Alloc(signature);
        var t = (WlInterface**)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * Math.Max(types.Length, 1)));
        for (int k = 0; k < types.Length; k++) t[k] = (WlInterface*)types[k];
        m->Types = t;
    }

    /// <summary>
    /// One line per message, the format tools/wayland-scanner/check_tables.py
    /// derives from the XML: name, version, counts, then each message's
    /// signature and argument types.
    /// </summary>
    public static List<string> Dump(WlInterface* i)
    {
        var lines = new List<string> { $"interface {WlUtf8.Read(i->Name)} v{i->Version} methods={i->MethodCount} events={i->EventCount}" };
        for (int m = 0; m < i->MethodCount; m++) lines.Add("  r " + DumpMessage(&i->Methods[m]));
        for (int e = 0; e < i->EventCount; e++) lines.Add("  e " + DumpMessage(&i->Events[e]));
        return lines;
    }

    private static string DumpMessage(WlMessage* m)
    {
        var sig = WlUtf8.Read(m->Signature)!;
        int argc = 0;
        foreach (var c in sig) if (c != '?' && !char.IsAsciiDigit(c)) argc++;
        var types = new List<string>();
        for (int t = 0; t < argc; t++)
            types.Add(m->Types == null || m->Types[t] == null ? "-" : WlUtf8.Read(m->Types[t]->Name)!);
        return $"{WlUtf8.Read(m->Name)} '{sig}' [{string.Join(",", types)}]";
    }
}
