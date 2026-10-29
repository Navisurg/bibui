using System.Text;


namespace Bibui.Core.Runtime.Platform;

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
/// One thing the OS told us, and only that. Produced by a platform's event
/// pump (or anything else that speaks for a keyboard and mouse, as a test
/// server does); <see cref="BibuiRuntime.BeginBuild"/> decides what it means.
///
/// It says what happened, and the modifiers held as it happened, stamped by the
/// platform from the OS's own state at that moment (<see cref="Modifiers"/>), so
/// nothing downstream keeps a fold of the key stream that a key released in
/// another window could leave stuck. No button mask: the runtime keeps that.
/// </summary>
public readonly struct WindowEvent
{
    public WindowEventKind Kind { get; private init; }

    /// <summary>Cursor position when the event happened, in logical pixels.</summary>
    public float X { get; private init; }
    public float Y { get; private init; }

    public MouseButton Button { get; private init; }

    /// <summary>
    /// How far a <see cref="WindowEventKind.Scroll"/> went, in GLFW's sign
    /// (positive Y is up, positive X is left): wheel notches, 32 logical px
    /// each, or for a <see cref="Continuous"/> scroll, logical px, which the
    /// runtime does not scale.
    /// </summary>
    public float ScrollX { get; private init; }

    /// <inheritdoc cref="ScrollX"/>
    public float ScrollY { get; private init; }

    /// <summary>
    /// For a <see cref="WindowEventKind.Scroll"/> only: from a source with no
    /// notches, as a touchpad is, its <see cref="ScrollX"/> and
    /// <see cref="ScrollY"/> in logical px, scrolled as they are. A platform
    /// that knows a source has none says so (<see cref="ContinuousScroll"/>),
    /// and turns its own units into px. False for a wheel.
    /// </summary>
    public bool Continuous { get; private init; }

    public Key Key { get; private init; }
    /// <summary>
    /// What a <see cref="WindowEventKind.KeyChar"/> typed: one whole character,
    /// never half of a UTF-16 surrogate pair. A <see cref="Rune"/> cannot hold an
    /// invalid scalar, so a platform cannot hand the runtime one.
    /// </summary>
    public Rune CodePoint { get; private init; }

    /// <summary>The modifiers held when it happened, as the OS reported them then.</summary>
    public KeyModifiers Modifiers { get; private init; }

    /// <summary>
    /// A <see cref="WindowEventKind.KeyDown"/> the platform sent again because the
    /// key is held (<see cref="KeyRepeat"/>), at the rate the OS repeats keys:
    /// the key down a held Backspace or arrow is made of after the first. A held
    /// printable key also types its text again, as a <see cref="WindowEventKind.KeyChar"/>.
    /// </summary>
    public bool Repeat { get; private init; }

    public static WindowEvent Mouse(WindowEventKind kind, float x, float y, MouseButton button, KeyModifiers modifiers = KeyModifiers.None)
        => new() { Kind = kind, X = x, Y = y, Button = button, Modifiers = modifiers };

    /// <summary>A wheel turned: <paramref name="scrollX"/> and <paramref name="scrollY"/> notches, a fraction from a high-resolution wheel.</summary>
    public static WindowEvent Wheel(float x, float y, float scrollX, float scrollY, KeyModifiers modifiers = KeyModifiers.None)
        => new() { Kind = WindowEventKind.Scroll, X = x, Y = y, ScrollX = scrollX, ScrollY = scrollY, Modifiers = modifiers };

    /// <summary>
    /// A scroll from a source with no notches, as a touchpad is (<see cref="Continuous"/>):
    /// <paramref name="pixelsX"/> and <paramref name="pixelsY"/> logical px, positive up and left.
    /// </summary>
    public static WindowEvent ContinuousScroll(float x, float y, float pixelsX, float pixelsY, KeyModifiers modifiers = KeyModifiers.None)
        => new() { Kind = WindowEventKind.Scroll, X = x, Y = y, ScrollX = pixelsX, ScrollY = pixelsY, Modifiers = modifiers, Continuous = true };

    public static WindowEvent KeyPress(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Kind = WindowEventKind.KeyDown, Key = key, Modifiers = modifiers };
    /// <summary>A key held down, reported again: a <see cref="WindowEventKind.KeyDown"/> with <see cref="Repeat"/> set.</summary>
    public static WindowEvent KeyRepeat(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Kind = WindowEventKind.KeyDown, Key = key, Modifiers = modifiers, Repeat = true };
    public static WindowEvent KeyRelease(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Kind = WindowEventKind.KeyUp, Key = key, Modifiers = modifiers };
    public static WindowEvent Text(Rune codePoint) => new() { Kind = WindowEventKind.KeyChar, CodePoint = codePoint };
    public static WindowEvent FocusLost() => new() { Kind = WindowEventKind.FocusLost };
}
