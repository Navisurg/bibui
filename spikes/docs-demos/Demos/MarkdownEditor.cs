// A demo the docs pages have no Example for: a Markdown editor that fills its
// canvas. The source is a multi-line field on the left and the viewer draws
// it on the right as it is typed; a task box ticked in the viewer ticks it in
// the source. Registered as "markdown-editor" (Program.cs).
using System.Runtime.InteropServices.JavaScript;
using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;

namespace DocsDemos;

internal sealed partial class MarkdownEditor
{
    // A link clicked in the viewer opens in a new tab. It is called in the
    // frame the click is handled, within the click's user activation, so the
    // popup blocker lets it through.
    [JSImport("globalThis.open")]
    private static partial JSObject? Open(string url, string target, string features);

    private const string Initial =
        "# Hello, bibui\n"
        + "\n"
        + "Type on the **left**; this side is drawn by `runtime.Markdown`, in C#, in your browser.\n"
        + "\n"
        + "## Lists\n"
        + "\n"
        + "- Bullets, and\n"
        + "  1. numbered items\n"
        + "  2. that nest\n"
        + "- [x] task items: click a box\n"
        + "- [ ] to tick it in the source\n"
        + "\n"
        + "> Quotes get a rule on their left.\n"
        + "\n"
        + "| Route | AOT size |\n"
        + "|:--|--:|\n"
        + "| Wayland + managed | 5.8 MB |\n"
        + "| Silk + Skia | 15.1 MB |\n"
        + "\n"
        + "```\n"
        + "runtime.Markdown(\"doc\", source);\n"
        + "```\n"
        + "\n"
        + "Links work too: [CommonMark](https://commonmark.org).\n";

    private readonly BibuiStr _source = BibuiStr.FromStrLit(Initial);
    private readonly TextEditState _edit = new();
    private bool _focused = true;

    public void Build(BibuiRuntime runtime)
    {
        var row = runtime.RowFill().Padded(EdgeInsets.All(16f));
        using (runtime.PushParent(row))
        {
            // The source: an input frame filling the left half, the field filling the frame.
            Box frame = runtime.Box().FillX().FillY();
            TextField.InputStyle(runtime, frame, _focused);
            BibuiWidget field;
            using (runtime.PushParent(frame))
            using (runtime.PushSizeY(SemanticSize.ParentPct(1f)))
            using (runtime.PushFontSize(ComponentSize.Sm))
                field = runtime.MultilineTextField("md-source", _source, _edit, _focused);
            if (field.Pressed) _focused = true;

            runtime.Gap(16f);

            // The viewer: the right half, scrolled by the wheel, cut at its edge.
            // It asks after the document inside it: ask order is z-order, and a
            // container that asked first would take the presses meant for the
            // task boxes and links.
            Box view = runtime.BoxFromId("md-view", InteractionKinds.ViewScrollY | InteractionKinds.ClampY
                | InteractionKinds.Clip | InteractionKinds.LeftPress);
            view.ChildLayoutAxis = LayoutAxis.Y;
            view.FillX().FillY();
            view.AllowOverflowY = true;
            view.Style.Padding = EdgeInsets.All(16f);
            view.Style.BorderColor = UiColor.Border;
            view.Style.BorderWidth = runtime.Theme.Borders.Width;
            view.Style.CornerRadius = runtime.Theme.Radii.Lg;
            using (runtime.PushParent(view))
            {
                var doc = runtime.Markdown("md-doc", _source);
                if (doc.TaskClicked) Markdown.ToggleTask(_source, doc.ToggledTask);
                if (doc.LinkClicked && IsWebLink(doc.ClickedLink!) && OperatingSystem.IsBrowser())
                    Open(doc.ClickedLink!, "_blank", "noopener")?.Dispose();
            }
            if (runtime.InteractionsFor(view).Pressed) _focused = false;
        }

        if (_focused)
            foreach (var ev in runtime.UnconsumedEvents)
                if (ev is { IsKeyDown: true, Key: Key.Escape })
                {
                    _focused = false;
                    ev.Eat();
                }
    }

    private static bool IsWebLink(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
