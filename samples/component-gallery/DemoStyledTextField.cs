using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

namespace ComponentGallery;

/// <summary>
/// The StyledTextField page: Markdown source in the field, its markers dimmed,
/// strong text bold and code in the accent colour. The ranges are found again
/// from the source each frame, as an editor's highlighter would, into a buffer
/// the page keeps, which grows to the most ranges found.
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
        Highlight(Source.Str);
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

    // A heading's hashes dimmed and its text bold; **strong** with its stars
    // dimmed and its text bold; `code` in the accent colour. Found left to right,
    // each after the one before it ends: sorted, none overlapping, as the field takes them.
    private static void Highlight(ReadOnlySpan<char> source)
    {
        Count = 0;
        for (int start = 0; start < source.Length; )
        {
            int length = source[start..].IndexOf('\n');
            int end = length < 0 ? source.Length : start + length;
            var line = source[start..end];
            int hashes = line.IndexOfAnyExcept('#');
            if (hashes > 0 && line[hashes] == ' ')
            {
                Add(new TextStyleRange(start, hashes + 1, UiColor.TextSecondary));
                Add(new TextStyleRange(start + hashes + 1, line.Length - hashes - 1, Weight: FontWeight.Bold));
            }
            else Inline(line, start);
            start = end + 1;
        }
    }

    private static void Inline(ReadOnlySpan<char> line, int offset)
    {
        const string strong = "**";
        for (int at = 0; at < line.Length; at++)
        {
            if (line[at..].StartsWith(strong) && line[(at + strong.Length)..].IndexOf(strong) is var inner and > 0)
            {
                Add(new TextStyleRange(offset + at, strong.Length, UiColor.TextSecondary));
                Add(new TextStyleRange(offset + at + strong.Length, inner, Weight: FontWeight.Bold));
                Add(new TextStyleRange(offset + at + strong.Length + inner, strong.Length, UiColor.TextSecondary));
                at += 2 * strong.Length + inner - 1;
            }
            else if (line[at] == '`' && line[(at + 1)..].IndexOf('`') is var code and >= 0)
            {
                Add(new TextStyleRange(offset + at, code + 2, UiColor.Accent));
                at += code + 1;
            }
        }
    }

    private static void Add(TextStyleRange range)
    {
        if (Count == Ranges.Length) Array.Resize(ref Ranges, (int)BitOperations.RoundUpToPowerOf2((uint)Count + 1));
        Ranges[Count++] = range;
    }
}
