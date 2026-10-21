global using static ComponentGallery.Faces;

using Bibui.Core;
using Bibui.Core.Runtime;

namespace ComponentGallery;

internal static class Faces
{
    // A text at the bold weight, in one call.
    internal static Text Bold(BibuiRuntime runtime, ReadOnlySpan<char> text, FontSize? fontSize = null, UiColor? textColor = null)
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold))
            return runtime.Text(text, fontSize, textColor);
    }
}
