
namespace Bibui.Runtime.Platform;

/// <summary>What a <see cref="WindowEvent"/> reports.</summary>
public enum WindowEventKind
{
    MouseDown,
    MouseUp,
    MouseMove,
    Scroll,
    KeyDown,
    KeyUp,
    KeyChar,

    /// <summary>Focus left the window. Whatever was held is not held any more.</summary>
    FocusLost,
}

/// <summary>
/// One thing the OS told us, and only that. Produced by an
/// <see cref="IPlatformHost"/>'s event pump; <see cref="BibuiWindow"/> decides what it means.
///
/// It says what happened, never what is now true: no button mask, no modifier
/// set, nothing accumulated. Those are a fold of the whole stream and belong to
/// whoever keeps that fold — the window keeps the modifiers, the runtime the rest.
/// Carrying a snapshot on each event would mean every event answering a question
/// it was not asked, and a key press pretending to know about the mouse.
/// </summary>
public readonly struct WindowEvent
{
    public WindowEventKind Kind { get; private init; }

    /// <summary>Cursor position when the event happened, in logical pixels.</summary>
    public float X { get; private init; }
    public float Y { get; private init; }

    public MouseButton Button { get; private init; }
    public float ScrollX { get; private init; }
    public float ScrollY { get; private init; }

    public Key Key { get; private init; }
    public char Char { get; private init; }

    public static WindowEvent Mouse(WindowEventKind kind, float x, float y, MouseButton button)
        => new() { Kind = kind, X = x, Y = y, Button = button };

    public static WindowEvent Wheel(float x, float y, float scrollX, float scrollY)
        => new() { Kind = WindowEventKind.Scroll, X = x, Y = y, ScrollX = scrollX, ScrollY = scrollY };

    public static WindowEvent KeyPress(Key key) => new() { Kind = WindowEventKind.KeyDown, Key = key };
    public static WindowEvent KeyRelease(Key key) => new() { Kind = WindowEventKind.KeyUp, Key = key };
    public static WindowEvent Text(char ch) => new() { Kind = WindowEventKind.KeyChar, Char = ch };
    public static WindowEvent FocusLost() => new() { Kind = WindowEventKind.FocusLost };
}
