// The docs' examples in the browser: each canvas the page mounts builds one
// example (DocsExamples.Registry, generated from docs/components/*.md),
// centred in a column that fills the canvas, 24 px in from its edges.
using Bibui;
using Bibui.Browser;
using Bibui.Runtime;

if (OperatingSystem.IsBrowser())
{
    BrowserHost.Ids = () => DocsExamples.Registry.Ids;
    BrowserHost.Factory = id => DocsExamples.Registry.Create(id) is { } build ? rt => Stage(rt, build) : null;
}

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
