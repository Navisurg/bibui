namespace Bibui.Core.Runtime.Platform;

/// <summary>What the window is created with.</summary>
/// <param name="Title">Window title.</param>
/// <param name="Width">Requested width in logical pixels (a tiling compositor may decide otherwise).</param>
/// <param name="Height">Requested height in logical pixels.</param>
public readonly record struct PlatformWindowOptions(string Title, int Width, int Height);

/// <summary>Pointer shapes a window can show. A platform maps each to the nearest it has.</summary>
public enum CursorShape
{
    Default,
    Pointer,
    Text,
    Crosshair,
    ResizeHorizontal,
    ResizeVertical,
    ResizeAll,
    NotAllowed,
    Wait,
}
