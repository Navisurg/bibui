using Bibui.Core.Runtime.Platform;

namespace Bibui.Platform.Browser;

/// <summary>A <see cref="CursorShape"/> as the CSS <c>cursor</c> the canvas takes.</summary>
internal static class CanvasCursor
{
    public static string Css(CursorShape shape) => shape switch
    {
        CursorShape.Pointer => "pointer",
        CursorShape.Text => "text",
        CursorShape.Crosshair => "crosshair",
        CursorShape.ResizeHorizontal => "ew-resize",
        CursorShape.ResizeVertical => "ns-resize",
        CursorShape.ResizeAll => "move",
        CursorShape.NotAllowed => "not-allowed",
        CursorShape.Wait => "wait",
        _ => "",   // the page's own, which for a canvas is the default arrow
    };
}
