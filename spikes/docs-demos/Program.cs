// The docs' examples in the browser: each canvas the page mounts builds one
// example (DocsExamples.Registry, generated from docs/components/*.md),
// centred in a column that fills the canvas, 24 px in from its edges. The
// demos in Demos/ are written for the docs site rather than lifted from the
// markdown: the Markdown editor and the Animations page.
using Bibui;
using Bibui.Browser;
using Bibui.Runtime;
using DocsDemos;

if (OperatingSystem.IsBrowser())
{
    BrowserHost.Ids = () => [.. DocsExamples.Registry.Ids, .. Extra.Keys];
    BrowserHost.Factory = id => id == "markdown-editor"
        ? new MarkdownEditor().Build           // fills its canvas rather than sitting centred in it
        : Extra.TryGetValue(id, out var make)
            ? Centre(make())
            : DocsExamples.Registry.Create(id) is { } build ? Centre(build) : null;
}

static Action<BibuiRuntime> Centre(Action<BibuiRuntime> build) => rt => Stage(rt, build);

// The runtime's default theme is bibui-dark, whose Background (#0B0907) is the
// docs page's preview stage: the canvas meets the page without a seam.
static void Stage(BibuiRuntime runtime, Action<BibuiRuntime> build)
{
    using (runtime.PushParent(runtime.ColumnFill().Padded(EdgeInsets.All(24f))))
    using (runtime.Centred())
    using (runtime.PushParent(runtime.RowFillX()))
    using (runtime.Centred())
        build(runtime);
}

partial class Program
{
    // A fresh instance per mount, so two canvases showing one demo keep their own state.
    static readonly Dictionary<string, Func<Action<BibuiRuntime>>> Extra = new()
    {
        ["markdown-editor"] = () => new MarkdownEditor().Build,
        ["anim-hover"] = () => new AnimHover().Build,
        ["anim-panel"] = () => new AnimPanel().Build,
        ["anim-transitions"] = () => new AnimTransitions().Build,
        ["anim-presence"] = () => new AnimPresence().Build,
        ["anim-theme"] = () => new AnimTheme().Build,
    };
}
