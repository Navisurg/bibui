global using static ComponentGallery.Faces;

using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

internal static class Faces
{
    // A text at the bold weight, where a PushFontFace scope cannot go: inside an expression.
    internal static Text Bold(BibuiRuntime runtime, ReadOnlyMemory<byte> text, FontSize? fontSize = null, UiColor? textColor = null)
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold))
            return runtime.Text(text, fontSize, textColor);
    }
}
