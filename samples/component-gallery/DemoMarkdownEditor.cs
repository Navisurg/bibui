using Bibui.Core;
using Bibui.Core.Runtime;

namespace ComponentGallery;

/// <summary>
/// The MarkdownEditor page: a document rendered, its paragraph open as source
/// to start with, coloured as it is typed. A press on another block edits that
/// one; Escape renders them all. The page holds the source and the editor's
/// state.
/// </summary>
internal static class DemoMarkdownEditor
{
    private const string Id = "demo-markdown-editor";
    private const float EditorWidth = 560f;
    private const float EditorHeight = 560f;
    private const float Pad = 16f;

    private const string Opened = "Version **2.0**";

    private static readonly BibuiStr Source = BibuiStr.FromStrLit(
        "# Release notes\n"
        + "\n"
        + Opened + " is *out*: `runtime.MarkdownEditor` edits a document in place, ***both*** styles"
        + " at once, and [a link](https://commonmark.org) too.\n"
        + "\n"
        + "## What changed\n"
        + "\n"
        + "- Press a block to edit its source\n"
        + "- Arrow keys cross into the next block\n"
        + "  1. Backspace at a block's start\n"
        + "  2. joins it to the one before\n"
        + "\n"
        + "> The block being edited is coloured: markers dimmed, **strong** in bold, _emphasis_ in italic.\n"
        + "\n"
        + "```csharp\n"
        + "var editor = runtime.MarkdownEditor(\"doc\", source, state);\n"
        + "if (editor.Edited) Save(source);\n"
        + "```\n"
        + "\n"
        + "## To do\n"
        + "\n"
        + "- [x] Parse in the caller's state\n"
        + "- [ ] Draw images in place\n"
        + "\n"
        + "More at [CommonMark](https://commonmark.org).\n");

    private static readonly MarkdownEditState State = Open(Source.Str.IndexOf(Opened));
    private static string? _lastLink;

    internal static void Page(BibuiRuntime runtime)
    {
        runtime.Text("Press a block to edit its source, coloured as you type. Escape renders it again.",
            ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(Pad);

        Box surface = runtime.Column().Sized(Size.Px(Math.Min(EditorWidth, ShellLayout.PageWidth(runtime))), Size.Px(EditorHeight));
        surface.Style.Padding = EdgeInsets.All(Pad);
        surface.Style.BorderColor = UiColor.Border;
        surface.Style.BorderWidth = 1f;
        surface.Style.CornerRadius = 8f;
        runtime.BeginParent(surface);
        var editor = runtime.MarkdownEditor(Id, Source, State);
        if (editor.ClickedLink is { } link) _lastLink = link;
        runtime.EndParent(surface);

        runtime.Gap(Pad);
        runtime.Text(_lastLink is null ? "Click a link to see where it goes." : $"Clicked: {_lastLink}",
            ComponentSize.Sm, TextColor.Secondary);
    }

    // The block holding `at` open, the caret at `at`.
    private static MarkdownEditState Open(int at)
    {
        var state = new MarkdownEditState { Active = at };
        state.Edit.Cursor = state.Edit.Mark = at;
        return state;
    }
}
