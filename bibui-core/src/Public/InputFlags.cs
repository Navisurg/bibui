namespace Bibui.Core;

/// <summary>Modifier keys currently held.</summary>
[Flags]
public enum KeyModifiers
{
    None  = 0,
    Shift = 1 << 0,
    Ctrl  = 1 << 1,
    Alt   = 1 << 2,

    /// <summary>Cmd on macOS, Super elsewhere. Folded into <see cref="Ctrl"/> by the window.</summary>
    Super = 1 << 3,
}
