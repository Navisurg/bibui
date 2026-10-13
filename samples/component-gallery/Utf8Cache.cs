global using static ComponentGallery.Utf8Cache;

using System.Text;
using Bibui;

namespace ComponentGallery;

// Sample-side only: the library takes bytes. Literals are encoded once and kept;
// a formatted string is encoded each time it changes.
internal static class Utf8Cache
{
    private static readonly Dictionary<string, byte[]> Bytes = new();
    private static readonly Dictionary<string, BibuiStr> Strs = new();

    internal static ReadOnlyMemory<byte> U8(string s)
    {
        if (!Bytes.TryGetValue(s, out var bytes))
            Bytes[s] = bytes = Encoding.UTF8.GetBytes(s);
        return bytes;
    }

    internal static BibuiStr Str(string s)
    {
        if (!Strs.TryGetValue(s, out var str))
            Strs[s] = str = BibuiStr.FromStrLit(Encoding.UTF8.GetBytes(s));
        return str;
    }
}
