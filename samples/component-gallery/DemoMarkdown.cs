using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

namespace ComponentGallery;

/// <summary>
/// The Markdown page: the source in a field on the left, edited when pressed,
/// and the viewer on the right drawing it as it is typed. A link clicked in the
/// viewer is reported under it; a task box clicked ticks the item in the source.
/// </summary>
internal static class DemoMarkdown
{
    private const float FieldWidth = 320f;
    private const float ViewerWidth = 420f;

    private static readonly BibuiStr Source = BibuiStr.FromStrLit(
        "# Markdown\n"
        + "\n"
        + "A **native** viewer: `inline code`, ~~struck text~~ and [links](https://commonmark.org) wrap as one paragraph.\n"
        + "\n"
        + "## Lists\n"
        + "\n"
        + "- Bullets, and\n"
        + "  1. numbered items\n"
        + "  2. that nest with hanging indents\n"
        + "- [x] task items: click a box\n"
        + "- [ ] to tick it in the source\n"
        + "\n"
        + "> Quotes get a rule on their left.\n"
        + "\n"
        + "| Option | Cost | Notes |\n"
        + "|:--|--:|:-:|\n"
        + "| **Fold** | 57 files | one package |\n"
        + "| **Keep split** | none | two packages to version |\n"
        + "\n"
        + "```\n"
        + "runtime.Markdown(\"doc\", source);\n"
        + "```\n");

    private static readonly TextEditState Edit = new();
    private static bool _focused;
    private static string? _lastLink;

    internal static void Page(BibuiRuntime runtime)
    {
        Bold(runtime, "Edit the source on the left; the viewer draws it as you type. Escape stops editing.",
            ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);

        // Side by side where they fit, the viewer under the source where they do not.
        bool sideBySide = ShellLayout.PageWidth(runtime) >= FieldWidth + 24f + ViewerWidth;
        using (runtime.PushParent(sideBySide ? runtime.RowFit() : runtime.ColumnFit()))
        {
            Box frame = runtime.Box();
            TextField.InputStyle(runtime, frame, _focused);
            frame.SizeX = SemanticSize.Pixels(FieldWidth);
            frame.SizeY = SemanticSize.ChildrenSum();
            BibuiWidget field;
            using (runtime.PushParent(frame))
            using (runtime.PushSizeY(TextField.Rows(runtime, 26)))
            using (runtime.PushFontSize(ComponentSize.Sm))
                field = runtime.MultilineTextField("demo-markdown-source", Source, Edit, _focused);
            if (field.Pressed) _focused = true;

            runtime.Gap(24f);

            Box surface = runtime.ColumnWidth(ViewerWidth);
            surface.Style.Padding = EdgeInsets.All(16f);
            surface.Style.BorderColor = UiColor.Border;
            surface.Style.BorderWidth = 1f;
            surface.Style.CornerRadius = 8f;
            using (runtime.PushParent(surface))
            {
                var viewer = runtime.Markdown("demo-markdown", Source, width: ViewerWidth - 32f);
                if (viewer.LinkClicked) _lastLink = viewer.ClickedLink;
                if (viewer.TaskClicked) Markdown.ToggleTask(Source, viewer.ToggledTask);
            }
        }

        runtime.Gap(16f);
        runtime.Text(_lastLink is null ? "Click a link in the viewer." : $"Clicked: {_lastLink}",
            ComponentSize.Sm, TextColor.Secondary);

        if (_focused)
            foreach (var ev in runtime.UnconsumedEvents)
                if (ev is { IsKeyDown: true, Key: Bibui.Core.Key.Escape })
                {
                    _focused = false;
                    ev.Eat();
                }
    }
}
