using System.Text;

namespace Bibui.Platform.Portal;

/// <summary>The <c>file://</c> URIs a portal answers with, as local paths.</summary>
internal static class FileUris
{
    /// <summary>
    /// The local path <paramref name="uri"/> names, percent-decoded as UTF-8:
    /// <c>file:///home/a/My%20Notes.md</c> is <c>/home/a/My Notes.md</c>. Null
    /// for anything that is not a file on this machine: another scheme, a
    /// host other than <c>localhost</c>, an escape that does not decode, a
    /// NUL, or bytes that are not UTF-8. A <c>?</c> or <c>#</c> in a file name
    /// arrives escaped, so a bare one ends the path, as in any URI.
    /// </summary>
    public static string? ToLocalPath(string uri)
    {
        const string scheme = "file://";
        if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri.AsSpan(scheme.Length);
        int slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var host = rest[..slash];
        if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        var path = rest[slash..];
        int end = path.IndexOfAny('?', '#');
        if (end >= 0) path = path[..end];

        var bytes = new byte[Encoding.UTF8.GetMaxByteCount(path.Length)];
        int n = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] != '%')
            {
                n += Encoding.UTF8.GetBytes(path.Slice(i, char.IsHighSurrogate(path[i]) && i + 1 < path.Length ? 2 : 1), bytes.AsSpan(n));
                if (char.IsHighSurrogate(path[i]) && i + 1 < path.Length) i++;
                continue;
            }
            if (i + 2 >= path.Length || !IsHex(path[i + 1]) || !IsHex(path[i + 2])) return null;
            bytes[n++] = (byte)(Hex(path[i + 1]) * 16 + Hex(path[i + 2]));
            i += 2;
        }
        var decoded = bytes.AsSpan(0, n);
        if (decoded.Contains((byte)0)) return null;
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(decoded); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    private static int Hex(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
