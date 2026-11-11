using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

namespace ComponentGallery;

/// <summary>
/// The StyledTextField page: Markdown source in the field, its markers dimmed,
/// strong text bold and code in the accent colour. The ranges are found again
/// from the source each frame by the MarkdownEditor's highlighter, into a
/// buffer the page keeps, which grows to the most ranges found.
/// </summary>
internal static class DemoStyledTextField
{
    private const string Id = "demo-styled-source";
    private const float FieldWidth = 420f;
    private const int FieldRows = 10;

    private static readonly BibuiStr Source = BibuiStr.FromStrLit(
        "# Release notes\n"
        + "\n"
        + "The field is **bold** where the source says so,\n"
        + "and `code` is drawn in the accent colour.\n"
        + "\n"
        + "## Try it\n"
        + "\n"
        + "Type between **the stars** and the text is bold;\n"
        + "take a star away and it is plain again.\n");

    private static readonly StyledTextState State = new();
    private static TextStyleRange[] Ranges = [];
    private static int Count;

    internal static void Page(BibuiRuntime runtime)
    {
        Count = MarkdownHighlight.RangesFromSource(Source.Str, ref Ranges);
        Box frame = runtime.Box();
        TextField.InputStyle(runtime, frame, runtime.IsFocused(Id));
        frame.SizeX = SemanticSize.Pixels(FieldWidth);
        frame.SizeY = SemanticSize.ChildrenSum();
        runtime.BeginParent(frame);
        runtime.BeginSizeY(TextField.Rows(runtime, FieldRows));
        runtime.StyledTextField(Id, Source, State, Ranges.AsSpan(0, Count));
        runtime.EndSizeY();
        runtime.EndParent(frame);
    }
}
