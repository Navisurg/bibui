using Bibui.Core;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Platform.Browser;

/// <summary>
/// The canvas's state and input since the last frame, as bibui-browser.js
/// hands them over: one array of numbers per poll, so a frame costs one call
/// into JavaScript however much happened. A header, then a record of
/// <see cref="Stride"/> numbers per event; text (a key's code and name, a
/// paste) is passed beside it in an array of strings that records index.
/// </summary>
/// <remarks>
/// Header: visible (0 or 1), the canvas's width and height in device pixels,
/// devicePixelRatio, and the number of records. Records, by kind:
/// <list type="table">
/// <item><term>1 move, 2 down, 3 up</term><description>x, y (CSS pixels), DOM button, modifier bits</description></item>
/// <item><term>4 wheel</term><description>x, y, deltaX, deltaY, deltaMode, modifier bits</description></item>
/// <item><term>5 key down</term><description>code (string index), key (string index), modifier bits, AltGraph (0 or 1)</description></item>
/// <item><term>6 key up</term><description>code (string index), modifier bits</description></item>
/// <item><term>7 focus lost</term><description>nothing</description></item>
/// <item><term>8 paste</term><description>the text (string index)</description></item>
/// </list>
/// </remarks>
internal static class BrowserInput
{
    public const int HeaderLength = 5;
    public const int Stride = 7;

    public const int Move = 1, Down = 2, Up = 3, Wheel = 4, KeyDown = 5, KeyUp = 6, FocusLost = 7, Paste = 8;

    /// <summary>The canvas, as the header says it is.</summary>
    public readonly record struct CanvasState(bool Visible, int DeviceWidth, int DeviceHeight, float Ratio, int Count);

    public static CanvasState ReadHeader(ReadOnlySpan<double> data) => data.Length < HeaderLength
        ? default
        : new(data[0] != 0, (int)data[1], (int)data[2], (float)data[3], (int)data[4]);

    /// <summary>
    /// Appends the events the records describe to <paramref name="into"/>, as
    /// the desktop hosts report them: a key-down, then the character it typed;
    /// a wheel in notches. A paste sets <paramref name="pasted"/>, the text the
    /// runtime's clipboard then reads, and is reported as the Ctrl+V that asked
    /// for it.
    /// </summary>
    public static void Decode(ReadOnlySpan<double> data, IReadOnlyList<string> strings, List<WindowEvent> into, ref string? pasted)
    {
        int count = ReadHeader(data).Count;
        for (int i = 0; i < count; i++)
        {
            int at = HeaderLength + i * Stride;
            if (at + Stride > data.Length) break;
            var r = data.Slice(at, Stride);
            switch ((int)r[0])
            {
                case Move:
                    into.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, (float)r[1], (float)r[2], MouseButton.Unknown,
                        BrowserKeys.FromDomModifiers((int)r[4])));
                    break;
                case Down:
                case Up:
                    into.Add(WindowEvent.Mouse((int)r[0] == Down ? WindowEventKind.MouseDown : WindowEventKind.MouseUp,
                        (float)r[1], (float)r[2], BrowserKeys.FromDomButton((int)r[3]), BrowserKeys.FromDomModifiers((int)r[4])));
                    break;
                case Wheel:
                {
                    var (dx, dy) = Notches(r[3], r[4], (int)r[5]);
                    into.Add(WindowEvent.Wheel((float)r[1], (float)r[2], dx, dy, BrowserKeys.FromDomModifiers((int)r[6])));
                    break;
                }
                case KeyDown:
                {
                    string code = At(strings, r[1]), key = At(strings, r[2]);
                    bool altGraph = r[4] != 0;
                    var held = BrowserKeys.HeldForKey((int)r[3], altGraph);
                    into.Add(WindowEvent.KeyPress(BrowserKeys.FromCode(code), held));
                    if (BrowserKeys.TextOf(key, held, altGraph) is { } typed) into.Add(WindowEvent.Text(typed));
                    break;
                }
                case KeyUp:
                    into.Add(WindowEvent.KeyRelease(BrowserKeys.FromCode(At(strings, r[1])), BrowserKeys.FromDomModifiers((int)r[2])));
                    break;
                case FocusLost:
                    into.Add(WindowEvent.FocusLost());
                    break;
                case Paste:
                    pasted = At(strings, r[1]);
                    into.Add(WindowEvent.KeyPress(Key.V, KeyModifiers.Ctrl));
                    into.Add(WindowEvent.KeyRelease(Key.V, KeyModifiers.Ctrl));
                    break;
            }
        }
    }

    /// <summary>
    /// A wheel's deltas as notches, positive up and left, as GLFW reports a
    /// wheel: a browser reports pixels (deltaMode 0, about 100 to a notch),
    /// lines (1, three to a notch) or pages (2), positive down and right.
    /// </summary>
    public static (float X, float Y) Notches(double deltaX, double deltaY, int deltaMode)
    {
        double perNotch = deltaMode switch { 1 => 3.0, 2 => 1.0 / 3.0, _ => 100.0 };
        return ((float)(-deltaX / perNotch), (float)(-deltaY / perNotch));
    }

    private static string At(IReadOnlyList<string> strings, double index)
    {
        int i = (int)index;
        return i >= 0 && i < strings.Count ? strings[i] : "";
    }
}
