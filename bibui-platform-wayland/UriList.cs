using System.Text;

namespace Bibui.Platform.Wayland;

/// <summary>
/// A <c>text/uri-list</c> (RFC 2483), as a file manager offers the files it
/// drags, turned into local paths. One URI a line, lines ending in CRLF (a
/// bare LF is taken too), <c>#</c> starting a comment. Only <c>file:</c> URIs
/// name local files; anything else in the list is left out.
/// </summary>
internal static class UriList
{
    /// <summary>The mime type a drag carrying files offers.</summary>
    public const string Mime = "text/uri-list";

    /// <summary>
    /// The local paths <paramref name="bytes"/> lists, in its order, each once.
    /// A URI that is not <c>file:</c>, names another host, or does not decode
    /// to a path (a NUL, bad UTF-8) is skipped.
    /// </summary>
    public static List<string> Paths(ReadOnlySpan<byte> bytes, string? hostName = null)
    {
        var paths = new List<string>();
        // A list is ASCII by the RFC; what is not is decoded leniently and
        // then judged by the path it gives.
        foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (PathOf(line, hostName ?? Environment.MachineName) is { } path && !paths.Contains(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// The local path a <c>file:</c> URI names, or null. <c>file:///p</c>,
    /// <c>file://localhost/p</c> and <c>file://<paramref name="hostName"/>/p</c>
    /// (as some file managers write it) are local; <c>file:/p</c> too. The path
    /// is percent-decoded as UTF-8; a query or fragment is not part of it.
    /// </summary>
    public static string? PathOf(string uri, string hostName)
    {
        const string scheme = "file:";
        if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri.AsSpan(scheme.Length);
        if (rest.StartsWith("//"))
        {
            rest = rest[2..];
            int slash = rest.IndexOf('/');
            if (slash < 0) return null;
            var host = rest[..slash];
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.Equals(hostName, StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest[slash..];
        }
        if (rest.Length == 0 || rest[0] != '/') return null;
        int end = rest.IndexOfAny('?', '#');
        if (end >= 0) rest = rest[..end];
        return PercentDecode(rest) is { } path && !path.Contains('\0') ? path : null;
    }

    /// <summary>
    /// <paramref name="s"/> with every <c>%XX</c> made the byte it stands for,
    /// the bytes read as UTF-8. Null when they are not UTF-8: a name in another
    /// encoding has no string to stand for it. A <c>%</c> not followed by two
    /// hex digits stands for itself.
    /// </summary>
    public static string? PercentDecode(ReadOnlySpan<char> s)
    {
        if (s.IndexOf('%') < 0) return s.ToString();
        var bytes = new List<byte>(s.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            {
                bytes.Add((byte)(Hex(s[i + 1]) << 4 | Hex(s[i + 2])));
                i += 2;
                continue;
            }
            // A character as it is: its own UTF-8.
            int n = char.IsHighSurrogate(s[i]) && i + 1 < s.Length
                ? Encoding.UTF8.GetBytes(s.Slice(i++, 2), utf8)
                : Encoding.UTF8.GetBytes(s.Slice(i, 1), utf8);
            for (int k = 0; k < n; k++) bytes.Add(utf8[k]);
        }
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int Hex(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
