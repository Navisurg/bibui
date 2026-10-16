using Bibui.Draw;

namespace Bibui.Renderer;

/// <summary>
/// Parses SVG path data into path verbs. Supports M m L l H h V v C c S s
/// Q q T t A a Z z, including reflected control points (S/T) and elliptical arcs
/// (A).
///
/// The result is bibui's own <see cref="VectorPath"/>, not a backend path: the parse is
/// process-wide while native paths belong to a renderer, which builds its own
/// from the verbs. Parsed paths are
/// cached by their raw path string for the lifetime of the process — icon paths
/// are immutable and the registry is small (~90 entries).
/// </summary>
internal static class SvgPathParser
{
    private static readonly Dictionary<string, VectorPath> _cache = new(StringComparer.Ordinal);
    private static readonly object _cacheLock = new();

    public static VectorPath? Parse(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return null;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(svg, out var cached)) return cached;
        }

        var path = new VectorPath();
        try
        {
            BuildPath(svg, path);
        }
        catch
        {
            return null;
        }

        lock (_cacheLock)
        {
            _cache[svg] = path;
        }
        return path;
    }

    /// <summary>
    /// Parses <paramref name="svg"/> and throws <see cref="FormatException"/>
    /// on anything a draw would quietly drop: a parse error, a path that does
    /// not begin with a move, a number that is not finite, or no segment to
    /// stroke. What <c>Icons.Register</c> checks an app's path with.
    /// </summary>
    public static VectorPath ParseStrict(string svg)
    {
        string trimmed = svg.TrimStart(' ', ',', '\t', '\n', '\r');
        if (trimmed.Length == 0) throw new FormatException("The path data is empty.");
        if (trimmed[0] is not ('M' or 'm')) throw new FormatException($"The path data must begin with a move (M or m), not '{trimmed[0]}'.");
        var path = new VectorPath();
        BuildPath(svg, path);
        if (!path.AllFinite) throw new FormatException("The path data holds a number too large to be finite.");
        if (!path.HasSegment) throw new FormatException("The path data draws nothing: it has no line, curve or arc.");
        lock (_cacheLock)
        {
            _cache[svg] = path;
        }
        return path;
    }

    private static void BuildPath(string s, VectorPath path)
    {
        int i = 0;
        int len = s.Length;
        float cx = 0f, cy = 0f;          // current point
        float startX = 0f, startY = 0f;  // last MoveTo (for Z)
        float lastC2x = 0f, lastC2y = 0f; // last cubic control point 2 (for S)
        float lastQcx = 0f, lastQcy = 0f; // last quad control (for T)
        char lastCmd = '\0';
        char prevCmd = '\0'; // for tracking reflected control validity

        while (i < len)
        {
            SkipWhitespaceAndCommas(s, ref i);
            if (i >= len) break;

            char cmd;
            if (IsCommand(s[i]))
            {
                cmd = s[i++];
            }
            else
            {
                // Implicit command repetition: M -> L, m -> l, others same as last
                cmd = lastCmd switch
                {
                    'M' => 'L',
                    'm' => 'l',
                    '\0' => throw new FormatException("Path must start with a command"),
                    _ => lastCmd,
                };
            }

            switch (cmd)
            {
                case 'M':
                    cx = ReadNumber(s, ref i);
                    cy = ReadNumber(s, ref i);
                    path.MoveTo(cx, cy);
                    startX = cx; startY = cy;
                    break;
                case 'm':
                    cx += ReadNumber(s, ref i);
                    cy += ReadNumber(s, ref i);
                    path.MoveTo(cx, cy);
                    startX = cx; startY = cy;
                    break;

                case 'L':
                    cx = ReadNumber(s, ref i);
                    cy = ReadNumber(s, ref i);
                    path.LineTo(cx, cy);
                    break;
                case 'l':
                    cx += ReadNumber(s, ref i);
                    cy += ReadNumber(s, ref i);
                    path.LineTo(cx, cy);
                    break;

                case 'H':
                    cx = ReadNumber(s, ref i);
                    path.LineTo(cx, cy);
                    break;
                case 'h':
                    cx += ReadNumber(s, ref i);
                    path.LineTo(cx, cy);
                    break;

                case 'V':
                    cy = ReadNumber(s, ref i);
                    path.LineTo(cx, cy);
                    break;
                case 'v':
                    cy += ReadNumber(s, ref i);
                    path.LineTo(cx, cy);
                    break;

                case 'C':
                {
                    float c1x = ReadNumber(s, ref i);
                    float c1y = ReadNumber(s, ref i);
                    float c2x = ReadNumber(s, ref i);
                    float c2y = ReadNumber(s, ref i);
                    float ex = ReadNumber(s, ref i);
                    float ey = ReadNumber(s, ref i);
                    path.CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                    lastC2x = c2x; lastC2y = c2y;
                    cx = ex; cy = ey;
                    break;
                }
                case 'c':
                {
                    float c1x = cx + ReadNumber(s, ref i);
                    float c1y = cy + ReadNumber(s, ref i);
                    float c2x = cx + ReadNumber(s, ref i);
                    float c2y = cy + ReadNumber(s, ref i);
                    float ex  = cx + ReadNumber(s, ref i);
                    float ey  = cy + ReadNumber(s, ref i);
                    path.CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                    lastC2x = c2x; lastC2y = c2y;
                    cx = ex; cy = ey;
                    break;
                }

                case 'S':
                case 's':
                {
                    float c1x, c1y;
                    if (prevCmd is 'C' or 'c' or 'S' or 's')
                    {
                        // Reflect previous control 2 through current point
                        c1x = 2f * cx - lastC2x;
                        c1y = 2f * cy - lastC2y;
                    }
                    else
                    {
                        c1x = cx; c1y = cy;
                    }
                    float c2x = ReadNumber(s, ref i);
                    float c2y = ReadNumber(s, ref i);
                    float ex  = ReadNumber(s, ref i);
                    float ey  = ReadNumber(s, ref i);
                    if (cmd == 's')
                    {
                        c2x += cx; c2y += cy; ex += cx; ey += cy;
                    }
                    path.CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                    lastC2x = c2x; lastC2y = c2y;
                    cx = ex; cy = ey;
                    break;
                }

                case 'Q':
                {
                    float qcx = ReadNumber(s, ref i);
                    float qcy = ReadNumber(s, ref i);
                    float ex  = ReadNumber(s, ref i);
                    float ey  = ReadNumber(s, ref i);
                    path.QuadTo(qcx, qcy, ex, ey);
                    lastQcx = qcx; lastQcy = qcy;
                    cx = ex; cy = ey;
                    break;
                }
                case 'q':
                {
                    float qcx = cx + ReadNumber(s, ref i);
                    float qcy = cy + ReadNumber(s, ref i);
                    float ex  = cx + ReadNumber(s, ref i);
                    float ey  = cy + ReadNumber(s, ref i);
                    path.QuadTo(qcx, qcy, ex, ey);
                    lastQcx = qcx; lastQcy = qcy;
                    cx = ex; cy = ey;
                    break;
                }

                case 'T':
                case 't':
                {
                    float qcx, qcy;
                    if (prevCmd is 'Q' or 'q' or 'T' or 't')
                    {
                        qcx = 2f * cx - lastQcx;
                        qcy = 2f * cy - lastQcy;
                    }
                    else
                    {
                        qcx = cx; qcy = cy;
                    }
                    float ex = ReadNumber(s, ref i);
                    float ey = ReadNumber(s, ref i);
                    if (cmd == 't') { ex += cx; ey += cy; }
                    path.QuadTo(qcx, qcy, ex, ey);
                    lastQcx = qcx; lastQcy = qcy;
                    cx = ex; cy = ey;
                    break;
                }

                case 'A':
                case 'a':
                {
                    float rx = ReadNumber(s, ref i);
                    float ry = ReadNumber(s, ref i);
                    float xRot = ReadNumber(s, ref i);
                    bool largeArc = ReadFlag(s, ref i);
                    bool sweep = ReadFlag(s, ref i);
                    float ex = ReadNumber(s, ref i);
                    float ey = ReadNumber(s, ref i);
                    if (cmd == 'a') { ex += cx; ey += cy; }
                    path.ArcTo(rx, ry, xRot, largeArc, sweep, ex, ey);
                    cx = ex; cy = ey;
                    break;
                }

                case 'Z':
                case 'z':
                    path.Close();
                    cx = startX; cy = startY;
                    break;

                default:
                    throw new FormatException($"Unsupported SVG path command '{cmd}'");
            }

            prevCmd = cmd;
            lastCmd = cmd;
        }
    }

    private static bool IsCommand(char c) =>
        c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v'
          or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't'
          or 'A' or 'a' or 'Z' or 'z';

    private static void SkipWhitespaceAndCommas(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
            i++;
    }

    private static float ReadNumber(string s, ref int i)
    {
        SkipWhitespaceAndCommas(s, ref i);
        int start = i;
        int len = s.Length;

        if (i < len && (s[i] == '+' || s[i] == '-')) i++;
        bool sawDigit = false;
        while (i < len && s[i] >= '0' && s[i] <= '9') { i++; sawDigit = true; }
        if (i < len && s[i] == '.')
        {
            i++;
            while (i < len && s[i] >= '0' && s[i] <= '9') { i++; sawDigit = true; }
        }
        if (i < len && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < len && (s[i] == '+' || s[i] == '-')) i++;
            while (i < len && s[i] >= '0' && s[i] <= '9') i++;
        }

        if (!sawDigit) throw new FormatException($"Expected number at offset {start} in path");
        return float.Parse(s.AsSpan(start, i - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ReadFlag(string s, ref int i)
    {
        SkipWhitespaceAndCommas(s, ref i);
        if (i >= s.Length) throw new FormatException("Expected arc flag (0 or 1)");
        char c = s[i++];
        if (c == '0') return false;
        if (c == '1') return true;
        throw new FormatException($"Expected arc flag (0 or 1), got '{c}'");
    }
}
