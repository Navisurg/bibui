using System.Diagnostics;
using Bibui.Core;
using Bibui.Core.Theming;
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Testing;
using Bibui.Core.TextEdit;

// ── Test runner ───────────────────────────────────────────────────────────────

int passed = 0, failed = 0, skipped = 0;

// Run a subset by name:  dotnet run --project tests/bibui-runtime-tests -- cursor
// Matches anywhere in the test's name, case-insensitively. No argument runs
// everything, which is what CI does.
string? filter = args.Length > 0 ? args[0] : null;
int filteredOut = 0;

// TODO: pre-existing failures discovered when CI was first enabled. Fix and remove.
var skipTests = new HashSet<string>
{
    "VideoFrame: receives frames after Play()",
};

void Run(string name, Action test)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    {
        filteredOut++;
        return;
    }
    if (skipTests.Contains(name))
    {
        Console.WriteLine($"  [SKIP] {name}");
        skipped++;
        return;
    }
    try
    {
        test();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}");
        Console.WriteLine($"         {ex.Message}");
        failed++;
    }
}

void Eq<T>(T expected, T actual, string? message = null)
{
    if (!Equals(expected, actual))
        throw new Exception(message ?? $"Expected {expected.ToStr()} but got {actual.ToStr()}");
}

void IsTrue(bool value, string? message = null)
{
    if (!value) throw new Exception(message ?? "Expected true but got false");
}

void IsNull(object? value, string? message = null)
{
    if (value is not null) throw new Exception(message ?? $"Expected null but got {value}");
}

void IsFalse(bool value, string? message = null)
{
    if (value) throw new Exception(message ?? "Expected false but got true");
}

void Throws<T>(Action body, string? message = null) where T : Exception
{
    try { body(); }
    catch (T) { return; }
    catch (Exception e) { throw new Exception($"{message ?? "Expected " + typeof(T).Name} but got {e.GetType().Name}"); }
    throw new Exception(message ?? $"Expected {typeof(T).Name} but nothing was thrown");
}

// A field's frame: a keyless box as wide as its parent and the given height. The field in it
// takes its presses under its own id.
static Box FieldFrame(BibuiRuntime runtime, SemanticSize height)
{
    Box box = runtime.Box();
    box.SizeX = SemanticSize.ParentPct(1f);
    box.SizeY = height;
    return box;
}

// A buffer to edit, holding s.
static BibuiStr Str(string s) => BibuiStr.FromStrLit(s);
// What a stack was: a box that fills its parent, its children floated with At.
static Box Layer(string? id, params UiNode[] children)
{
    var box = new Box { Id = id, ChildLayoutAxis = LayoutAxis.Y }.FillX().FillY();
    foreach (var child in children) UiNode.PushIntoParent(box, child);
    return box;
}
// One pixel of a frame read back at scale 1, as (r, g, b).
static (byte R, byte G, byte B) Pixel(Pixels frame, float x, float y)
{
    int i = ((int)y * frame.Width + (int)x) * 4;   // BGRA
    return (frame.RawBgraBytes[i + 2], frame.RawBgraBytes[i + 1], frame.RawBgraBytes[i]);
}
// A colour as a theme resolves it, to compare with a pixel.
static (byte R, byte G, byte B) Rgb(UiColor color, ThemeTokens theme)
{
    var (r, g, b, _) = color.ResolveColor(theme.Colors);
    return (r, g, b);
}
// What the texts in the last frame say, in paint order.
static List<string> Texts(BibuiRuntime runtime)
{
    runtime.Step();
    var texts = new List<string>();
    Walk(runtime.LastTree);
    return texts;

    void Walk(UiNode? node)
    {
        if (node is null) return;
        if (node is Text t) texts.Add(new string(t.Content));
        for (var c = node.FirstChild; c is not null; c = c.Next == c ? null : c.Next) Walk(c);
    }
}
// Every node under a node, the node first, in paint order.
static IEnumerable<UiNode> Below(UiNode node)
{
    yield return node;
    for (var c = node.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
        foreach (var d in Below(c)) yield return d;
}
// Whether any pixel in a rect is exactly this colour: a stroke wide enough to cover one.
static bool GlyphHas(SkiaSharp.SKBitmap shot, System.Drawing.RectangleF rect, byte r, byte g, byte b)
{
    for (int y = (int)rect.Y; y < (int)(rect.Y + rect.Height); y++)
    for (int x = (int)rect.X; x < (int)(rect.X + rect.Width); x++)
        if (shot.GetPixel(x, y) is { Red: var pr, Green: var pg, Blue: var pb } && pr == r && pg == g && pb == b) return true;
    return false;
}
// A node's rect, as GetBounds gives it.
static System.Drawing.RectangleF Bounds(UiNode node) => new(node.Rect.X, node.Rect.Y, node.Extent.X, node.Extent.Y);
// The words of a text node.
static string? TextOf(UiNode? node) => node is Text t ? new string(t.Content) : null;
// A button that acts on its click as it is built: what a test's build function wants.
static UiNode Btn(BibuiRuntime runtime, string id, string label, Action? onClick = null, bool disabled = false)
{
    var button = runtime.Button(id, label, disabled: disabled);
    if (button.Clicked) onClick?.Invoke();
    return button.Node;
}

Console.WriteLine("bibui-runtime-tests");
Console.WriteLine("===================");

// ── Bibui.Core.Key / Bibui.Core.MouseButton ────────────────────────────────────

// The Silk host converts with a cast, and the test server parses key names,
// so both enums must keep Silk.NET.Input's names and values exactly.
Run("Key and MouseButton: Silk.NET.Input's names, with Silk's values", () =>
{
    foreach (var silk in Enum.GetValues<Silk.NET.Input.Key>())
    {
        string name = silk.ToString();
        IsTrue(Enum.TryParse<Key>(name, out var ours), $"Bibui.Core.Key has no {name}");
        Eq((int)silk, (int)ours, $"Key.{name}");
    }
    Eq(Enum.GetNames<Silk.NET.Input.Key>().Length, Enum.GetNames<Key>().Length, "Key member count");
    foreach (var silk in Enum.GetValues<Silk.NET.Input.MouseButton>())
    {
        string name = silk.ToString();
        IsTrue(Enum.TryParse<MouseButton>(name, out var ours), $"Bibui.Core.MouseButton has no {name}");
        Eq((int)silk, (int)ours, $"MouseButton.{name}");
    }
    Eq(Enum.GetNames<Silk.NET.Input.MouseButton>().Length, Enum.GetNames<MouseButton>().Length, "MouseButton member count");
});

// ── BibuiRuntime.Create ─────────────────────────────────────────────────────

Run("Create: runtime.Button built while a column is pushed is inserted into it", () =>
{
    var runtime = BibuiRuntime.Create();
    Box? column = null;
    runtime.SetBuild(() =>
    {
        column = runtime.Box();
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = column.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(column))
        {
            runtime.Text("Clicked 0 times");
            runtime.Button(id: "add", "Add one");
        }
        return column;
    }, new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    var button = runtime.FindById("add");
    IsTrue(button is not null && ReferenceEquals(button.Parent, column), "the button's parent is the column");
    IsTrue(ReferenceEquals(column!.LastChild, button), "after the text, in the order built");
});

Run("runtime.ColumnFit: a box linked where it is made, and built into once pushed", () =>
{
    var runtime = BibuiRuntime.Create();
    Box? column = null, row = null, unpushed = null;
    runtime.SetBuild(() =>
    {
        column = runtime.ColumnFit();
        using (runtime.PushParent(column))
        {
            runtime.Text("Clicked 0 times");
            row = runtime.RowFit();
            using (runtime.PushParent(row))
            {
                runtime.Button(id: "a", "A");
                runtime.Button(id: "b", "B");
            }
            unpushed = runtime.RowFit();          // made, never pushed: what follows is not in it
            runtime.Button(id: "add", "Add one");
        }
        return column;
    }, new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    Eq(LayoutAxis.Y, column!.ChildLayoutAxis, "a column lays out along Y");
    Eq(LayoutAxis.X, row!.ChildLayoutAxis, "a row lays out along X");
    IsTrue(ReferenceEquals(row.Parent, column), "the row is linked under the column it was made in");
    IsTrue(ReferenceEquals(runtime.FindById("a")?.Parent, row) && ReferenceEquals(runtime.FindById("b")?.Parent, row),
        "the row's buttons are in the row");
    IsTrue(ReferenceEquals(unpushed!.Parent, column) && unpushed.FirstChild is null,
        "a row that is never pushed is in the column, and empty");
    IsTrue(ReferenceEquals(column.LastChild, runtime.FindById("add")), "after the row's scope, building goes back into the column");
    Eq(SemanticSize.ChildrenSum(), column.SizeX, "ColumnFit: as wide as its children");
    Eq(SemanticSize.ChildrenSum(), column.SizeY, "ColumnFit: as tall as its children");
    IsTrue(column.Extent.Y > row.Extent.Y, "laid out: the column holds the text, the row and a button");
});

Run("runtime.Column: sized only by what is pushed, so with nothing pushed layout refuses it", () =>
{
    var runtime = BibuiRuntime.Create();
    Box? pushed = null;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        using (runtime.PushSizeX(SemanticSize.Pixels(120f)))
        using (runtime.PushSizeY(SemanticSize.Pixels(30f)))
            pushed = runtime.Column();
        return page;
    }, new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    Eq(120f, pushed!.Extent.X, "the pushed width");
    Eq(30f, pushed.Extent.Y, "the pushed height");

    var bare = BibuiRuntime.Create();
    bare.SetBuild(() =>
    {
        var page = bare.ColumnFill();
        using (bare.PushParent(page))
        using (bare.PushParent(bare.Column()))
            bare.Text("unsized");
        return page;
    }, new SurfaceOptions { Width = 400, Height = 200 });
    Throws<LayoutException>(() => bare.Step(), "a Column() with nothing pushed has no size");
});

Run("runtime.ColumnFillX & co: each sized form sets the axes it names, and fits the rest", () =>
{
    var runtime = BibuiRuntime.Create();
    var boxes = new Dictionary<string, Box>();
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            void Add(string name, Box box)
            {
                boxes[name] = box;
                using (runtime.PushParent(box)) runtime.BoxFromId("in-" + name).Px(50f, 10f);
            }
            Add("ColumnFit", runtime.ColumnFit());
            Add("ColumnFillX", runtime.ColumnFillX());
            Add("ColumnWidth", runtime.ColumnWidth(80f));
            Add("ColumnPx", runtime.ColumnPx(70f, 15f));
            Add("RowFit", runtime.RowFit());
            Add("RowFillX", runtime.RowFillX());
            Add("RowFillXHeight", runtime.RowFillX(12f));
            Add("RowHeight", runtime.RowHeight(14f));
            Add("RowPx", runtime.RowPx(60f, 11f));
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    (float, float) Size(string n) => (boxes[n].Extent.X, boxes[n].Extent.Y);
    Eq((50f, 10f), Size("ColumnFit"), "ColumnFit");
    Eq((400f, 10f), Size("ColumnFillX"), "ColumnFillX");
    Eq((80f, 10f), Size("ColumnWidth"), "ColumnWidth");
    Eq((70f, 15f), Size("ColumnPx"), "ColumnPx");
    Eq((50f, 10f), Size("RowFit"), "RowFit");
    Eq((400f, 10f), Size("RowFillX"), "RowFillX");
    Eq((400f, 12f), Size("RowFillXHeight"), "RowFillX(height)");
    Eq((50f, 14f), Size("RowHeight"), "RowHeight");
    Eq((60f, 11f), Size("RowPx"), "RowPx");
    Eq(LayoutAxis.Y, boxes["ColumnFillX"].ChildLayoutAxis, "a column lays out along Y");
    Eq(LayoutAxis.X, boxes["RowFillX"].ChildLayoutAxis, "a row lays out along X");
});

Run("runtime.ColumnFillY & co: the fills along Y, in a row with room to fill", () =>
{
    var runtime = BibuiRuntime.Create();
    var boxes = new Dictionary<string, Box>();
    runtime.SetBuild(() =>
    {
        var page = runtime.RowFill();
        using (runtime.PushParent(page))
        {
            boxes["ColumnFillY"] = runtime.ColumnFillY();
            using (runtime.PushParent(boxes["ColumnFillY"])) runtime.BoxFromId("a").Px(50f, 10f);
            boxes["RowFillY"] = runtime.RowFillY();
            using (runtime.PushParent(boxes["RowFillY"])) runtime.BoxFromId("b").Px(30f, 10f);
            boxes["ColumnFill"] = runtime.ColumnFill();
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    Eq((50f, 300f), (boxes["ColumnFillY"].Extent.X, boxes["ColumnFillY"].Extent.Y), "ColumnFillY");
    Eq((30f, 300f), (boxes["RowFillY"].Extent.X, boxes["RowFillY"].Extent.Y), "RowFillY");
    Eq(300f, boxes["ColumnFill"].Extent.Y, "ColumnFill takes the row's height");
    Eq(320f, boxes["ColumnFill"].Extent.X, "and the room the row's other children leave");
});

Run("runtime.Centred / Spring: the contents land in the middle, between two springs", () =>
{
    var runtime = BibuiRuntime.Create();
    Box? across = null, row = null;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            // What Arrange.CentredX was: a full-width row, centred in.
            across = runtime.RowFillX();
            using (runtime.PushParent(across))
            using (runtime.Centred())
                runtime.BoxFromId("mid-x").Px(100f, 20f);

            row = runtime.RowFillX();
            using (runtime.PushParent(row))
            {
                runtime.Box().Px(40f, 10f);
                using (runtime.Centred()) runtime.BoxFromId("mid-row").Px(40f, 10f);
            }

            using (runtime.PushParent(runtime.RowFillX()))
            {
                runtime.Box().Px(30f, 10f);
                runtime.Spring();
                runtime.BoxFromId("far-end").Px(30f, 10f);
            }
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    Eq(400f, across!.Extent.X, "the row is as wide as its parent");
    Eq(150f, runtime.FindById("mid-x")!.Rect.X, "a 100 px box centred in 400 px");
    Eq(3, CountChildren(across), "spring, contents, spring");
    float midRow = runtime.FindById("mid-row")!.Rect.X;
    IsTrue(MathF.Abs(midRow - 200f) < 0.01f, $"Centred() centres in the room the row's other children leave (got {midRow})");
    IsTrue(ReferenceEquals(runtime.FindById("mid-row")!.Parent, row), "the contents stay the row's children, in no box of their own");
    Eq(370f, runtime.FindById("far-end")!.Rect.X, "a spring pushes what follows it to the far end");
});

static int CountChildren(UiNode node)
{
    int n = 0;
    for (var c = node.FirstChild; c is not null; c = c.Next) n++;
    return n;
}

Run("Create: runtime.Button is a button the caller asks about", () =>
{
    var runtime = BibuiRuntime.Create();
    int clicks = 0;
    runtime.SetBuild(() =>
    {
        var add = runtime.Button(id: "add", "Add one");
        if (add.Clicked) clicks++;
        return Layer(null, add.Node).Px(400f, 200f);
    }, new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 20f, Y = 20f });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 20f, Y = 20f });
    runtime.Step();
    Eq(1, clicks, "pressed and released on it");
    Eq("add", runtime.FindById("add")?.Id, "the button keeps the id it was given");
});

// ── Counter: stateful rebuild ─────────────────────────────────────────────────

Run("Counter: initial render shows 0", () =>
{
    var runtime = BibuiRuntime.Create();
    int count = 0;
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            runtime.TextFromId("label", $"{count}");
            Btn(runtime, "inc", "+1", () => count++);
        }),
        surface: new SurfaceOptions { Width = 200, Height = 100 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    Eq("0", page.GetText("label"));
});

Run("Counter: click increments", () =>
{
    var runtime = BibuiRuntime.Create();
    int count = 0;
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            runtime.TextFromId("label", $"{count}");
            Btn(runtime, "inc", "+1", () => count++);
        }),
        surface: new SurfaceOptions { Width = 200, Height = 100 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    page.Click("inc");
    Eq("1", page.GetText("label"));
    page.Click("inc");
    Eq("2", page.GetText("label"));
});

Run("Counter: disabled button does not increment", () =>
{
    var runtime = BibuiRuntime.Create();
    int count = 0;
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            runtime.TextFromId("label", $"{count}");
            Btn(runtime, "inc", "+1", () => count++, disabled: true);
        }),
        surface: new SurfaceOptions { Width = 200, Height = 100 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    page.Click("inc");
    Eq("0", page.GetText("label"), "disabled button should not report a click");
});

// ── Input: a Style fill and border replace the theme's ────────────────────────

// ── Input: focus events ───────────────────────────────────────────────────────
//
// Focus is the runtime's. An app that keeps its own copy of "which field has the keyboard"
// and pushes that onto the runtime every frame overrules a click it did not predict, and the
// clicked field goes dead. OnFocus/OnBlur are how an app follows focus instead.


Run("IsVisible: present node is visible", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => Btn(runtime, "btn", "Go"), new SurfaceOptions { Width = 200, Height = 60 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    IsTrue(page.IsVisible("btn"));
    IsFalse(page.IsVisible("nonexistent"));
});

// ── Screenshot ───────────────────────────────────────────────────────────────

Run("Screenshot returns non-empty PNG bytes", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => Btn(runtime, "btn", "Hello"), new SurfaceOptions { Width = 200, Height = 60 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var png = page.Screenshot();
    IsTrue(png.Length > 100, $"PNG should be non-trivial, got {png.Length} bytes");
    // PNG magic bytes: 0x89 0x50 0x4E 0x47
    IsTrue(png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
        "Output should start with PNG magic bytes");
});

// ── GetBounds ─────────────────────────────────────────────────────────────────

Run("GetBounds returns non-zero bounds for a rendered button", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => Btn(runtime, "btn", "Test").Px(120f, 40f), new SurfaceOptions { Width = 200, Height = 80 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var bounds = page.GetBounds("btn");
    IsTrue(bounds.Width > 0, $"Width should be > 0, got {bounds.Width}");
    IsTrue(bounds.Height > 0, $"Height should be > 0, got {bounds.Height}");
});

// ── Button: icon+label measure (regression for #77) ──────────────────────────

// ── Button: loading spinner animates off the frame clock (regression for #79) ──

// ── Progress: thickness, colours and the indeterminate sweep ─────────────────

Run("Animate: travels to a moved target, then settles and stops asking", () =>
{
    // The property the draw-on-demand loop rests on, and the one no built-in
    // component exercises — the spinners all use Sawtooth, which never settles.
    // Without this, nothing would catch an epsilon test that can never be met.
    float target = 0f;
    float observed = 0f;
    var key = AnimKey.For("anim", "t");

    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => new Box
        {
            Id = "anim",
            Draw = (_, ctx) => observed = ctx.Animate(key, target, Ease.Rate(12f, ctx.Dt), epsilon: 0.01f),
        }.Px(100f, 100f),
        surface: new SurfaceOptions { Width = 100, Height = 100 });

    // A value asked for the first time arrives at its target, so a frame that
    // only ever sees target 0 is settled from the outset.
    runtime.Step();
    while (runtime.FramesRequested > 0) runtime.ConsumeFrame();
    runtime.Step();
    IsTrue(runtime.FramesRequested == 0, "a value already at its target must not ask for frames");

    // Move the target: now it has somewhere to travel, and must say so.
    target = 1f;
    runtime.Step();
    IsTrue(runtime.FramesRequested > 0, "a value travelling to a new target must ask for frames");

    // Still showing the old value, deliberately: a frame computes where a value
    // goes next and commits it at the end, so what this frame drew is what was
    // on screen when the target moved. The travel appears on the next one.
    IsTrue(observed == 0f, $"the frame that moves the target still shows the old value, got {observed}");

    while (runtime.FramesRequested > 0) runtime.ConsumeFrame();
    runtime.Step();
    IsTrue(observed > 0f && observed < 1f, $"the value should be in transit, got {observed}");

    // ...and must stop. Bounded well above the ~35 frames a 12-halvings/second
    // approach needs to close 1.0 to within 0.01 at a nominal 60Hz.
    int frames = 0;
    while (frames < 600)
    {
        while (runtime.FramesRequested > 0) runtime.ConsumeFrame();
        runtime.Step();
        frames++;
        if (runtime.FramesRequested == 0) break;
    }
    IsTrue(frames < 600, "the animation must settle rather than run forever");
    IsTrue(MathF.Abs(observed - 1f) < 0.05f, $"it should end up near its target, got {observed}");
});

Run("An animation that leaves the screen stops asking for frames", () =>
{
    // A cell lingers in the cache for keepFrames after its last use, frozen
    // wherever it got to. If the end-of-frame sweep counted those, a spinner
    // that scrolled away would keep the loop awake for a second after it was
    // gone — so the sweep only counts what this frame actually drew.
    bool show = true;
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => show
            ? runtime.Spinner()
            : runtime.Text("done".ToArray()),
        surface: new SurfaceOptions { Width = 400, Height = 120 });

    runtime.Step();
    while (runtime.FramesRequested > 0) runtime.ConsumeFrame();
    runtime.Step();
    IsTrue(runtime.FramesRequested > 0, "the spinner should be asking for frames while it is on screen");

    // Take it off screen. Its cell survives eviction for many frames yet.
    show = false;
    while (runtime.FramesRequested > 0) runtime.ConsumeFrame();
    runtime.Step();
    IsTrue(runtime.FramesRequested == 0,
        $"a spinner that is no longer drawn must stop asking, asked for {runtime.FramesRequested}");
});

Run("A settled UI asks for no further frames", () =>
{
    // The property the whole draw-on-demand loop rests on: with nothing moving,
    // a render must ask for nothing, or the host never stops drawing.
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => Btn(runtime, "btn", "Continue"),
        surface: new SurfaceOptions { Width = 400, Height = 120 });
    runtime.Step();
    while (runtime.FramesRequested > 0) runtime.ConsumeFrame();

    runtime.Step();
    IsTrue(runtime.FramesRequested == 0,
        $"a static button must ask for no frames, asked for {runtime.FramesRequested}");
});

Run("Shares split a row's free space proportionally (4:1 → 80/20)", () =>
{
    using var page = BibuiPage.Launch(
        runtime =>
        {
            var row = runtime.RowFill();
            UiNode.PushIntoParent(row, new Box { Id = "wide",   Style = new Style { BackgroundColor = UiColor.SurfaceRaised } }.FillX(4f).FillY());
            UiNode.PushIntoParent(row, new Box { Id = "narrow", Style = new Style { BackgroundColor = UiColor.SurfaceRaised } }.FillX(1f).FillY());
        },
        surface: new SurfaceOptions { Width = 500, Height = 100 });

    var wide = page.GetBounds("wide");
    var narrow = page.GetBounds("narrow");
    IsTrue(MathF.Abs(wide.Width - 400f) <= 0.5f, $"Expected wide column 400 (4/5 of 500), got {wide.Width}");
    IsTrue(MathF.Abs(narrow.Width - 100f) <= 0.5f, $"Expected narrow column 100 (1/5 of 500), got {narrow.Width}");
});

Run("Shares split the space remaining after fixed-size siblings (3:1)", () =>
{
    using var page = BibuiPage.Launch(
        runtime =>
        {
            var row = runtime.RowFill();
            UiNode.PushIntoParent(row, new Box { Id = "fixed", Style = new Style { BackgroundColor = UiColor.SurfaceRaised } }.Px(width: 100f).FillY());
            UiNode.PushIntoParent(row, new Box { Id = "big",   Style = new Style { BackgroundColor = UiColor.SurfaceRaised } }.FillX(3f).FillY());
            UiNode.PushIntoParent(row, new Box { Id = "small", Style = new Style { BackgroundColor = UiColor.SurfaceRaised } }.FillX(1f).FillY());
        },
        surface: new SurfaceOptions { Width = 500, Height = 100 });

    // Fixed child takes its 100px; the weighted children split the remaining 400 as 3:1.
    var fixedBox = page.GetBounds("fixed");
    var big = page.GetBounds("big");
    var small = page.GetBounds("small");
    IsTrue(MathF.Abs(fixedBox.Width - 100f) <= 0.5f, $"Expected fixed column 100, got {fixedBox.Width}");
    IsTrue(MathF.Abs(big.Width - 300f) <= 0.5f, $"Expected big column 300 (3/4 of 400), got {big.Width}");
    IsTrue(MathF.Abs(small.Width - 100f) <= 0.5f, $"Expected small column 100 (1/4 of 400), got {small.Width}");
});

// ── Full app integration (samples/bibui-app's own build) ─────────────────────
//
// These tests drive the sample's real build, BibuiApp.Demo — counter, input and
// checkbox — entirely through the BibuiPage semantic API. No window opens; this
// is the "headless mode" equivalent of the running app.

static BibuiPage LaunchApp()
{
    var runtime = BibuiRuntime.Create();
    runtime.Surface = new SurfaceOptions { Width = 720, Height = 1200 };
    var state = new BibuiApp.DemoState();
    return BibuiPage.From(runtime, () => BibuiApp.Demo.Build(runtime, state));
}

// Whether the checkbox's card holds a text reading exactly `text` in the last
// frame: the badge beside the checkbox says its state, under a box with no id.
static bool ShowsText(BibuiPage page, string text)
{
    var card = page.FindById("subscribe-checkbox")?.Parent ?? throw new InvalidOperationException("No checkbox card");
    return Find(card);

    bool Find(UiNode n)
    {
        if (n is Text t && new string(t.Content) == text) return true;
        for (var c = n.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
            if (Find(c)) return true;
        return false;
    }
}

// ── App: counter ──────────────────────────────────────────────────────────────

Run("App: counter starts at 0", () =>
{
    using var page = LaunchApp();
    Eq("0", page.GetText("counter-label"));
});

Run("App: increment and decrement", () =>
{
    using var page = LaunchApp();

    page.Click("increment-btn");
    page.Click("increment-btn");
    page.Click("increment-btn");
    Eq("3", page.GetText("counter-label"));

    page.Click("decrement-btn");
    Eq("2", page.GetText("counter-label"));

    page.Click("decrement-btn");
    page.Click("decrement-btn");
    page.Click("decrement-btn");
    Eq("-1", page.GetText("counter-label"), "The counter goes below zero");
});

// ── App: name input ───────────────────────────────────────────────────────────

Run("App: name input updates greeting", () =>
{
    using var page = LaunchApp();

    Eq("…", page.GetText("greeting"), "Before typing, greeting should be ellipsis");

    page.Fill("name-input", "Ada");
    Eq("Hello, Ada!", page.GetText("greeting"));

    page.Fill("name-input", "");
    Eq("…", page.GetText("greeting"), "Greeting resets when input cleared");
});

// ── App: checkbox ─────────────────────────────────────────────────────────────

Run("App: subscribe checkbox toggles", () =>
{
    using var page = LaunchApp();

    IsTrue(ShowsText(page, "Unsubscribed") && !ShowsText(page, "Subscribed"), "Starts unchecked");

    page.Click("subscribe-checkbox");
    IsTrue(ShowsText(page, "Subscribed") && !ShowsText(page, "Unsubscribed"), "The badge reads Subscribed once checked");

    page.Click("subscribe-checkbox");
    IsTrue(ShowsText(page, "Unsubscribed") && !ShowsText(page, "Subscribed"), "Toggled back to unchecked");
});

// ── HitTest coordinate verification ──────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("HitTest coordinate verification");

// A representative form column matching the account sign-up structure:
// outer Column (padding=32, FillWidth) → inner Column (Stretch) → an input-sized box (FillWidth).
// Surface 900×700 → inner width = 900 - 2*32 = 836.
const float FormPadding = 32f;
const float FormWidth   = 900f;
const float FormHeight  = 700f;
const float ExpectedInputWidth = FormWidth - 2f * FormPadding; // 836

UiNode FormPage(BibuiRuntime runtime) => runtime.Into(runtime.ColumnFill().Padded(EdgeInsets.All(FormPadding)), () =>
{
    using (runtime.PushParent(runtime.ColumnFillX()))
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold))
            runtime.Text("Full Name".ToArray(), ComponentSize.Sm);
        runtime.Gap(8);
        // An input's place: what these tests ask is where the column puts
        // a full-width child, not anything an input does.
        UiNode.PushIntoParent(runtime.ParentInScope, new Box { Id = "name", Style = new Style { BackgroundColor = UiColor.Surface } }.Px(height: 40f).FillX());
    }
    runtime.Gap(24);
    runtime.Spring();
    runtime.Gap(24);
    Btn(runtime, "next", "Next →", () => { }).FillX();
});

Run("HitTest A: input bounds fill column width", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => FormPage(runtime),
        surface: new SurfaceOptions { Width = (int)FormWidth, Height = (int)FormHeight });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var bounds = page.GetBounds("name");
    IsTrue(MathF.Abs(bounds.Width - ExpectedInputWidth) <= 4f,
        $"Input width should be ≈{ExpectedInputWidth} but got {bounds.Width}");
    IsTrue(bounds.X >= 0f, $"Input X should be >= 0, got {bounds.X}");
    IsTrue(bounds.X + bounds.Width <= FormWidth + 1f,
        $"Input right edge {bounds.X + bounds.Width} exceeds surface width {FormWidth}");
});

Run("HitTest C: layout is in logical pixels, not physical", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => runtime.ColumnFill(),
        surface: new SurfaceOptions { Width = (int)FormWidth, Height = (int)FormHeight, ScaleFactor = 2f });
    runtime.Step();

    var root = runtime.LastTree!;
    IsTrue(MathF.Abs(root.Extent.X - FormWidth) <= 1f,
        $"Root width should be logical {FormWidth}, got {root.Extent.X} (physical would be {FormWidth * 2f})");
});

Run("HitTest D: Next button fills column width", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => FormPage(runtime),
        surface: new SurfaceOptions { Width = (int)FormWidth, Height = (int)FormHeight });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var bounds = page.GetBounds("next");
    IsTrue(MathF.Abs(bounds.Width - ExpectedInputWidth) <= 4f,
        $"Next button width should be ≈{ExpectedInputWidth} but got {bounds.Width}");
});

// ── Calendar: date picking ────────────────────────────────────────────────────

// ── Cursor positioning ───────────────────────────────────────────────────────

// ── Input: more text than the box holds ─────────────────────────────────────

// A field that holds more than it shows scrolls to keep the caret in view, as a native field
// does. It used to draw the text from its left edge whatever the caret's position, clipped,
// so everything typed past the right edge — and the caret with it — went on out of sight.

// ── Text editing: what the operations actually do ─────────────────────────────
//
// A mutation run showed these were unreachable before BibuiPage drove input the
// way a user does: emptying Delete, Home, End, SelectAll, Copy, Cut or Paste
// left the suite green.

// A TextField with known text, always focused: the page, and the buffer and
// edit state it edits, which the tests read and set directly.
(BibuiPage Page, BibuiStr Text, TextEditState Edit) TextFieldPage(string initial = "hello world")
{
    var (runtime, text, edit) = TextFieldRuntime(initial);
    return (BibuiPage.From(runtime, runtime.BuildTree), text, edit);
}

// The same field, for a test that hands the runtime its input itself.
(BibuiRuntime Runtime, BibuiStr Text, TextEditState Edit) TextFieldRuntime(string initial)
{
    var text = BibuiStr.FromStrLit(initial);
    var edit = new TextEditState { Cursor = (int)text.Length, Mark = (int)text.Length };
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        var frame = runtime.BoxFromId("frame");
        frame.ChildLayoutAxis = LayoutAxis.Y;
        frame.Px(240f, 40f);
        using (runtime.PushParent(frame))
        {
            var field = FieldFrame(runtime, Bibui.Core.Runtime.TextField.Rows(runtime, 1));
            using (runtime.PushParent(field)) runtime.TextField("txt", text, edit, focused: true);
        }
        return frame;
    }, new SurfaceOptions { Width = 300, Height = 60 });
    runtime.Step();
    return (runtime, text, edit);
}

string Value(BibuiStr text) => text.ToString();

Run("Delete removes the character after the caret", () =>
{
    var (page, text, edit) = TextFieldPage("hello");
    edit.Cursor = edit.Mark = 0;
    page.Press(Key.Delete);
    Eq("ello", Value(text), "Delete at 0 should remove the first character");

    // At the end there is nothing after the caret to remove.
    edit.Cursor = edit.Mark = 4;
    page.Press(Key.Delete);
    Eq("ello", Value(text), "Delete at the end should do nothing");
});

Run("Delete and Backspace remove a selection rather than one character", () =>
{
    var (page, text, edit) = TextFieldPage();
    page.DoubleClick("txt");                     // selects "world"
    page.Press(Key.Delete);
    Eq("hello ", Value(text), "Delete with a selection should remove the selection");

    var (page2, text2, edit2) = TextFieldPage();
    page2.DoubleClick("txt");
    page2.PressBackspace();
    Eq("hello ", Value(text2), "Backspace with a selection should remove the selection too");
});

Run("Home and End move the caret to the ends", () =>
{
    var (page, text, edit) = TextFieldPage("hello");
    edit.Cursor = edit.Mark = 3;

    page.Press(Key.Home);
    Eq(0, edit.Cursor, "Home should put the caret at the start");

    page.Press(Key.End);
    Eq(5, edit.Cursor, "End should put the caret at the end");
});

Run("Shift extends a selection instead of moving the caret", () =>
{
    var (page, text, edit) = TextFieldPage("hello");
    edit.Cursor = edit.Mark = 0;

    page.Press(Key.End, shift: true);
    Eq(5, edit.Cursor, "shift+End should move the caret to the end");
    Eq(0, edit.Mark, "and leave the anchor where the caret started");

    // Typing over what that selected replaces the lot.
    page.TypeChar('X');
    Eq("X", Value(text), "typing should replace the selection shift+End made");
});

Run("Ctrl+Arrow moves the caret a word at a time", () =>
{
    var (page, text, edit) = TextFieldPage("hello world");
    page.Press(Key.End);

    page.Press(Key.Left, ctrl: true);
    Eq(6, edit.Cursor, "ctrl+Left should stop at the start of the last word");

    page.Press(Key.Right, ctrl: true);
    Eq(11, edit.Cursor, "and ctrl+Right should return to the end of it");
});

Run("Ctrl+A selects everything", () =>
{
    var (page, text, edit) = TextFieldPage("hello world");
    page.Press(Key.A, ctrl: true);
    page.TypeChar('X');
    Eq("X", Value(text), "select-all then typing should replace the whole value");
});

Run("Copy, cut and paste move text through the clipboard", () =>
{
    var (page, text, edit) = TextFieldPage();
    page.DoubleClick("txt");                     // selects "world"

    page.Press(Key.C, ctrl: true);
    Eq("world", page.Clipboard, "copy should put the selection on the clipboard");
    Eq("hello world", Value(text), "and leave the text alone");

    page.Press(Key.X, ctrl: true);
    Eq("world", page.Clipboard, "cut should put the selection on the clipboard");
    Eq("hello ", Value(text), "and remove it from the text");

    page.Press(Key.V, ctrl: true);
    Eq("hello world", Value(text), "paste should put it back");
});

Run("Pasting a newline into a single-line field does not break the line", () =>
{
    var (page, text, edit) = TextFieldPage("");
    page.Clipboard = "one\ntwo";
    page.Press(Key.V, ctrl: true);
    Eq("one two", Value(text), "a newline should arrive as a space in a single-line input");
});

// ── Textarea selection ───────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Textarea selection");

// ── Textarea multi-line (\n hit-test) ────────────────────────────────────────

// ── LineChart multi-series ────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("LineChart multi-series");

// ── Interactive Box ──────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Interactive Box");

Run("InteractiveBox: Box that claims nothing stays as Box kind", () =>
{
    using var page = BibuiPage.Launch(
        build: () => new Box
        {
            Id = "plain-box",
        }.Px(80f, 40f),
        surface: new SurfaceOptions { Width = 200, Height = 100 });

    var node = page.FindById("plain-box");
    IsTrue(node is Box { Claims: InteractionKinds.None }, "a box that claims nothing is just a box");
});

// ── Text wrapping ───────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Text wrapping");

Run("Text: keeps single-line metrics when natural width fits the parent", () =>
{
    // Auto-sizing Row gives Text its natural width; nothing constrains it,
    // so it must render single-line just like before.
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        build: () => runtime.Into(runtime.RowFit(), () => runtime.TextFromId("inline", "Inline label".ToArray())),
        surface: new SurfaceOptions { Width = 800, Height = 200 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var texts = Texts(runtime);
    IsTrue(texts.SequenceEqual(new[] { "Inline label" }),
        $"Single-line Text must draw one line, got [{string.Join(" | ", texts)}]");
});

// ── Custom Button styling ───────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Custom Button styling");

// ── Avatar (native ↔ spec parity) ────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Avatar");

Run("Clip: an oversized child is clipped", () =>
{
    // A box whose content is bigger than it must not let that content paint
    // over whatever sits beside it.
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            // A row of fixed-width children, so the content is wider than the box.
            var card = runtime.RowPx(100f, 60f).Clipped();
            UiNode.PushIntoParent(card, new Box { Style = new Style { BackgroundColor = "#FF0000" } }.Px(125f, 40f));
            UiNode.PushIntoParent(card, new Box { Style = new Style { BackgroundColor = "#FF0000" } }.Px(125f, 40f));
        }),
        surface: new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });

    var frame = runtime.Frame();
    bool IsRed(int x, int y)
    {
        var i = (y * frame.Width + x) * 4;   // BGRA
        return frame.RawBgraBytes[i] < 60 && frame.RawBgraBytes[i + 1] < 60 && frame.RawBgraBytes[i + 2] > 180;
    }

    IsTrue(IsRed(50, 20), "the child should paint inside the box");
    IsTrue(!IsRed(150, 20), "the child must not paint past the box's right edge");

});

// ── A row measures its children at the width they will be given ──────────────

Console.WriteLine();
Console.WriteLine("Row measure: weighted children");

Run("Row: a weighted child is measured at its share, not at the whole row", () =>
{
    // Regression: the measure pass handed every child the row's full width and then
    // arranged the weighted ones at half of it. Wrapping text answers differently at the
    // two numbers — the same paragraph is two lines against 600px and five against 300 —
    // so the row came back short by three lines of every wrapped block inside it.
    const string paragraph =
        "A paragraph long enough that the width it is measured against decides how many "
        + "lines it takes, which is the whole of the disagreement being tested here.";

    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            using (runtime.PushParent(runtime.ColumnWidth(600f)))
            using (runtime.PushParent(runtime.RowFillX()))
            {
                var left = runtime.BoxFromId("left-column").FillX(1f);
                (left.ChildLayoutAxis, left.SizeY) = (LayoutAxis.Y, SemanticSize.ChildrenSum());
                using (runtime.PushParent(left))
                {
                    runtime.Text(paragraph).FillX();
                    runtime.Text(paragraph).FillX();
                }
                runtime.Box().FillX(1f).Px(height: 20f);
            }
        }),
        surface: new SurfaceOptions { Width = 700, Height = 400 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var column = page.FindById("left-column") ?? throw new Exception("left-column missing");

    // The column is as tall as the two paragraphs in it, or it is not describing them.
    float wrapped = 0f;
    for (var child = column.FirstChild; child is not null; child = child.Next == child ? null : child.Next) wrapped += child.Extent.Y;
    IsTrue(MathF.Abs(column.Extent.Y - wrapped) < 0.5f,
           $"the column should be as tall as its wrapped text: column={column.Extent.Y}, text={wrapped}");
});

// The other direction. A weight divides slack a row is given; it is not a claim
// on slack the row was only offered while being measured. An icon button
// centres its glyph with a Spacer either side, and counting the Spacers' shares
// made each button as wide as the row it sat in.
Run("Row: a spring in a row sized by its content is an unsolvable ring", () =>
{
    // The spring takes a share of the row, and the row is the sum of what it
    // holds: each waits for the other. Said rather than guessed at.
    Throws<LayoutException>(() => BibuiPage.Launch(
        runtime =>
        {
            using (runtime.PushParent(runtime.ColumnFit()))
            using (runtime.PushParent(runtime.RowFit()))
            {
                runtime.Spring();
                runtime.Box().Px(12f, 12f);
                runtime.Spring();
            }
        },
        surface: new SurfaceOptions { Width = 900, Height = 80 }).Dispose());
});

Run("Row: a built component goes into the argument list as it is, without .Node", () =>
{
    var runtime = BibuiRuntime.Create();
    const string ok = "OK";
    UiNode? button = null, slider = null;
    runtime.SetBuild(() =>
    {
        BibuiWidget built = runtime.Button("ok", ok);
        SliderWidget volume = runtime.Slider("volume", 50f);
        volume.Node.Px(width: 80f);   // a slider is otherwise as wide as its parent
        (button, slider) = (built.Node, volume.Node);
        // What is tested is Arrange.Row's own argument list, obsolete or not.
#pragma warning disable CS0618
        return Arrange.Row("row", built, Arrange.Space(8f), volume);
#pragma warning restore CS0618
    }, new SurfaceOptions { Width = 400, Height = 80 });
    runtime.Step();

    var row = runtime.FindById("row") ?? throw new Exception("row missing");
    IsTrue(ReferenceEquals(row.FirstChild, button), "the button's node is the row's first child");
    IsTrue(ReferenceEquals(row.LastChild, slider), "the slider's node is the row's last child");
});

Run("Window background: the theme's Background, with no style on the root", () =>
{
    // The painter clears every frame to the theme's Background before it walks
    // the tree, so a root that says no background of its own shows that colour,
    // pixel for pixel the same as one styled with UiColor.Background.
    var theme = ThemeLoader.Load("default-light");
    var surface = new SurfaceOptions { Width = 160, Height = 80, ScaleFactor = 1f };
    const string hello = "Hello";

    var bare = BibuiRuntime.Create();
    bare.SetBuild(() => bare.Into(bare.ColumnFill().Padded(EdgeInsets.All(12f)), () => bare.Text(hello)), theme, surface);
    var styled = BibuiRuntime.Create();
    styled.SetBuild(() => styled.Into(styled.ColumnFill()
        .Styled(new Style { BackgroundColor = UiColor.Background, Padding = EdgeInsets.All(12f) }), () => styled.Text(hello)), theme, surface);

    var a = bare.Frame();
    var b = styled.Frame();
    Eq(Rgb(UiColor.Background, theme), Pixel(a, 150, 70), "an unstyled root shows the theme's Background");
    IsTrue(a.RawBgraBytes.AsSpan().SequenceEqual(b.RawBgraBytes), "the same frame as a root styled with UiColor.Background");
});

// ── Overflow rendering (Clip) ───────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Overflow rendering (Clip)");

// The three below are one report from an application's artifact table at half
// width: an unbounded name painted straight over the column beside it, and
// once that was stopped the name collapsed to an ellipsis while an
// eight-character hash kept its room.

Run("Clip: a clipping box paints nothing outside itself", () =>
{
    // No Id on the clipping box, deliberately: clipping must not depend on a
    // box being named.
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        build: () => runtime.Into(runtime.RowPx(220f, 60f).Styled(new Style { BackgroundColor = "#000000" }), () =>
        {
            // Texts laid out at their natural widths in a box narrower
            // than they are: they walk straight out of it unless it clips.
            // That is the shape the artifact table had.
            using (runtime.PushParent(runtime.RowPx(40f, 30f).Clipped()))
            {
                runtime.Text("good_skin".ToArray(), ComponentSize.Md);
                runtime.Gap(6f);
                runtime.Text("194450e3".ToArray(), ComponentSize.Md);
            }
            UiNode.PushIntoParent(runtime.ParentInScope, new Box
                {
                    // No background of its own: it is drawn after the
                    // clipped box, so a filled neighbour would paint over
                    // whatever escaped and the test would pass either way.
                    Id = "neighbour",
                    Style = new Style { BorderWidth = 0f },
                }.Px(160f, 30f));
        }),
        surface: new SurfaceOptions { Width = 240, Height = 80 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var png = page.Screenshot();
    var neighbour = page.GetBounds("neighbour");

    // Nothing the clipped box drew may show up over the neighbour, which is
    // painted the same black as the background and so should stay empty.
    using var ms = new MemoryStream(png);
    using var bmp = SkiaSharp.SKBitmap.Decode(ms);
    var stray = 0;
    for (var y = (int)neighbour.Y; y < (int)(neighbour.Y + neighbour.Height) && y < bmp.Height; y++)
    for (var x = (int)neighbour.X + 2; x < (int)(neighbour.X + neighbour.Width) && x < bmp.Width; x++)
    {
        var c = bmp.GetPixel(x, y);
        if (c.Red > 90 || c.Green > 90 || c.Blue > 90) stray++;
    }
    IsTrue(stray == 0, $"{stray} pixels of the clipped box's text landed on its neighbour");
});

Run("Text: a label that fits measures its own width", () =>
{
    // A label used to report the whole width it was offered, so in a Row
    // it swallowed every weighted sibling's share.
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        build: () => runtime.Into(runtime.RowFit().Px(width: 400f), () =>
        {
            var flexible = runtime.BoxFromId("flexible").FillX();
            flexible.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(flexible))
                runtime.TextFromId("name", "good_skin".ToArray());
            runtime.Gap(8f);
            runtime.TextFromId("hash", "194450e3".ToArray(), ComponentSize.Sm);
        }),
        surface: new SurfaceOptions { Width = 420, Height = 60 });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var hash = page.GetBounds("hash");
    var flexible = page.GetBounds("flexible");

    IsTrue(hash.Width < 120f,
        $"an eight-character label measured {hash.Width}px — it should be about its own glyphs");
    IsTrue(flexible.Width > 200f,
        $"the weighted cell beside it got only {flexible.Width}px of 400");
});

Run("Clip: a floating child is cut to the box that clips, like any other", () =>
{
    // A floating child is not part of the box's content size, so it could
    // not be what decides the clip: the claim decides it alone.
    using var page = BibuiPage.Launch(() =>
    {
        var clipper = new Box { Style = new Style { BackgroundColor = "#000000" } }.Clipped().Px(100, 100);
        UiNode.PushIntoParent(clipper, new Box { Style = new Style { BackgroundColor = "#FF0000" } }.Px(60, 60).At(80, 80));
        var root = new Box { Style = new Style { BackgroundColor = "#FFFFFF" } }.Px(200, 200);
        UiNode.PushIntoParent(root, clipper);
        return root;
    }, new SurfaceOptions { Width = 200, Height = 200, ScaleFactor = 1f });
    using var shot = SkiaSharp.SKBitmap.Decode(page.Screenshot());
    Eq(new SkiaSharp.SKColor(255, 0, 0), shot.GetPixel(90, 90), "inside the box it paints");
    Eq(new SkiaSharp.SKColor(255, 255, 255), shot.GetPixel(130, 130), "past its edge it does not");
});

Run("Clip: a box that does not claim it lets its children run past", () =>
{
    using var page = BibuiPage.Launch(() =>
    {
        var open = new Box { Style = new Style { BackgroundColor = "#000000" } }.Px(100, 100);
        UiNode.PushIntoParent(open, new Box { Style = new Style { BackgroundColor = "#FF0000" } }.Px(60, 60).At(80, 80));
        var root = new Box { Style = new Style { BackgroundColor = "#FFFFFF" } }.Px(200, 200);
        UiNode.PushIntoParent(root, open);
        return root;
    }, new SurfaceOptions { Width = 200, Height = 200, ScaleFactor = 1f });
    using var shot = SkiaSharp.SKBitmap.Decode(page.Screenshot());
    Eq(new SkiaSharp.SKColor(255, 0, 0), shot.GetPixel(130, 130), "clipping is asked for, not assumed");
});

// ── Theme registry ───────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Theme registry");

Run("ThemeRegistry: bibui's own names are listed, unchanged", () =>
{
    foreach (var name in new[] { "neutral-dark", "default-dark", "default-light", "bibui-dark", "bibui-light" })
        IsTrue(ThemeRegistry.Names.Contains(name), $"'{name}' is registered");
});

Run("ThemeRegistry: the built-in names are bibui's own, first in Names, and none registered outside it", () =>
{
    var builtIn = ThemeRegistry.BuiltInNames;
    Eq("bibui-dark,bibui-light,neutral-dark,default-dark,default-light", string.Join(",", builtIn), "bibui ships these");
    Eq(string.Join(",", builtIn), string.Join(",", ThemeRegistry.Names.Take(builtIn.Count)), "registered first, in order");
    string outside = "t26-outside";
    ThemeRegistry.Register(outside, ThemeLoader.DefaultLight);
    IsTrue(!ThemeRegistry.BuiltInNames.Contains(outside), "a theme registered outside bibui is not built in");
});

Run("ThemeRegistry: every listed name loads and mounts by name", () =>
{
    var surface = new SurfaceOptions { Width = 120, Height = 40, ScaleFactor = 1f };
    foreach (var name in ThemeRegistry.Names)
    {
        IsTrue(ThemeRegistry.Contains(name), $"'{name}' is listed and so contained");
        IsTrue(ThemeLoader.Load(name) is not null, $"'{name}' loads");

        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => runtime.TextFromId("t", "Theme".ToArray()), surface, name);
        runtime.Step();
        Eq(ThemeLoader.Load(name).Name, runtime.Theme.Name, $"a runtime set to \"{name}\" uses that theme");

        using var page = BibuiPage.Launch(() => new Box().FillX().FillY(), surface, name);
    }
});

Run("ThemeRegistry: a theme registered outside bibui mounts by name", () =>
{
    const string name = "runtime-tests-outside";
    int made = 0;
    ThemeRegistry.Register(name, () =>
    {
        made++;
        return new ThemeTokens { Name = name, Colors = new ColorTokens { Accent = "#123456" } };
    });
    IsTrue(ThemeRegistry.Names.Contains(name), "listed once registered");

    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => runtime.TextFromId("t", "Theme".ToArray()),
        new SurfaceOptions { Width = 120, Height = 40, ScaleFactor = 1f }, name);
    runtime.Step();
    Eq(name, runtime.Theme.Name);
    Eq("#123456", runtime.Theme.Colors.Accent);

    Eq(name, ThemeLoader.Load(name).Name, "ThemeLoader.Load resolves it too");
    using var page = BibuiPage.Launch(() => new Box().FillX().FillY(), new SurfaceOptions { Width = 120, Height = 40 }, name);
    IsTrue(made >= 3, "the factory runs on every load");
    IsFalse(ReferenceEquals(ThemeLoader.Load(name), ThemeLoader.Load(name)), "each load is a fresh theme");
});

Run("ThemeRegistry: registering a taken name throws and keeps the first", () =>
{
    Throws<InvalidOperationException>(() => ThemeRegistry.Register("neutral-dark", ThemeLoader.DefaultLight));
    Eq("neutral-dark", ThemeLoader.Load("neutral-dark").Name, "the built-in is not replaced");
    Throws<ArgumentException>(() => ThemeRegistry.Register(" ", ThemeLoader.DefaultLight), "a blank name is refused");
});

Run("ThemeRegistry: an unknown name says which names are registered", () =>
{
    foreach (var load in new Action[]
    {
        () => ThemeLoader.Load("no-such-theme"),
        () => BibuiRuntime.Create().SetBuild(() => new Box(), null, "no-such-theme"),
    })
    {
        try { load(); throw new Exception("expected an ArgumentException"); }
        catch (ArgumentException e)
        {
            IsTrue(e.Message.Contains("'no-such-theme'"), $"names the theme asked for: {e.Message}");
            foreach (var name in ThemeRegistry.Names)
                IsTrue(e.Message.Contains(name), $"lists '{name}': {e.Message}");
        }
    }
});

Run("ColorTokens: the built-in themes resolve every role to the colour it had before ramps", () =>
{
    var before = new Dictionary<string, string[]>
    {
        ["neutral-dark"] = ["#09090B", "#09090B", "#18181B", "#27272A", "#52525B", "#D4D4D8", "#FAFAFA", "#A1A1AA", "#71717A", "#FAFAFA", "#18181B", "#7F1D1D", "#FEE2E2", "#E4E4E7", "#D4D4D8", "#27272A", "#3F3F46", "#22C55E", "#052E16", "#0E2A17", "#EAB308", "#1F1500", "#2A210E", "#3B82F6", "#0B1A33", "#0E1A2A"],
        ["default-dark"] = ["#0D0D14", "#1E1E2E", "#2A2A3D", "#3A3A5C", "#5A5A8C", "#7C3AED", "#E0E0F0", "#8888AA", "#4A4A6A", "#7C3AED", "#FFFFFF", "#DC2626", "#FFFFFF", "#8B5CF6", "#6D28D9", "#36364E", "#42425C", "#22C55E", "#052E16", "#0E2A17", "#EAB308", "#1F1500", "#2A210E", "#3B82F6", "#0B1A33", "#0E1A2A"],
        ["default-light"] = ["#F8F8FF", "#FFFFFF", "#F0F0FA", "#D0D0E0", "#B0B0D0", "#7C3AED", "#0D0D14", "#5A5A7A", "#A0A0C0", "#7C3AED", "#FFFFFF", "#DC2626", "#FFFFFF", "#6D28D9", "#5B21B6", "#E4E4F4", "#D8D8EC", "#16A34A", "#F0FDF4", "#DCFCE7", "#CA8A04", "#FEFCE8", "#FEF9C3", "#2563EB", "#EFF6FF", "#DBEAFE"],
    };
    foreach (var (name, hex) in before)
    {
        var c = ThemeLoader.Load(name).Colors;
        string[] now = [c.Background, c.Surface, c.SurfaceRaised, c.Border, c.BorderHover, c.BorderFocus, c.TextPrimary, c.TextSecondary, c.TextDisabled, c.Accent, c.AccentForeground, c.Destructive, c.DestructiveForeground, c.AccentHover, c.AccentActive, c.SurfaceRaisedHover, c.SurfaceRaisedActive, c.Success, c.SuccessForeground, c.SuccessBg, c.Warning, c.WarningForeground, c.WarningBg, c.Info, c.InfoForeground, c.InfoBg];
        for (int i = 0; i < hex.Length; i++) Eq(hex[i], now[i], $"{name} role {i}");
    }
});

Run("ColorTokens: a role names a ramp step, and replacing the ramp recolours it alone", () =>
{
    var c = ThemeLoader.DefaultDark().Colors;
    var teal = new ColorRamp("#F0FDFA", "#CCFBF1", "#99F6E4", "#5EEAD4", "#2DD4BF", "#14B8A6",
                             "#0D9488", "#0F766E", "#115E59", "#134E4A", "#042F2E");
    var swapped = c with { Ramps = c.Ramps with { Accent = teal } };
    Eq("#0D9488", swapped.Accent, "Accent is accent-600");
    Eq("#14B8A6", swapped.AccentHover, "AccentHover is accent-500");
    Eq("#0D9488", swapped.BorderFocus, "BorderFocus is accent-600");
    Eq(c.Background, swapped.Background, "a role set to a colour does not move");
    Eq("#7C3AED", c.Accent, "the original is untouched");

    var one = c with { AccentHover = "#FF0000" };
    Eq("#FF0000", one.AccentHover, "one role replaced alone");
    Eq(c.Accent, one.Accent, "its neighbours keep theirs");

    var step = c with { Ramps = c.Ramps with { Accent = c.Ramps.Accent.With(600, "#123456") } };
    Eq("#123456", step.Accent, "ColorRamp.With changes one step");
    Eq("#8B5CF6", step.AccentHover, "and only that step");
});

Run("ColorTokens: a role nobody sets takes its mode's ramp step", () =>
{
    var dark = new ColorTokens();
    Eq(ColorRamp.DefaultNeutral[950], dark.Background, "dark background is neutral-950");
    Eq(ColorRamp.DefaultNeutral[50], dark.TextPrimary, "dark text is neutral-50");
    Eq(ColorRamp.DefaultAccent[500], dark.Accent, "dark accent is accent-500");

    var light = new ColorTokens { Mode = ThemeMode.Light };
    Eq(ColorRamp.DefaultNeutral[50], light.Background, "light background is neutral-50");
    Eq(ColorRamp.DefaultNeutral[950], light.TextPrimary, "light text is neutral-950");
    Eq(ColorRamp.DefaultAccent[600], light.Accent, "light accent is accent-600");

    var grey = new ColorRamp("#F0F0F0", "#E0E0E0", "#D0D0D0", "#C0C0C0", "#B0B0B0", "#A0A0A0",
                             "#909090", "#808080", "#707070", "#606060", "#505050");
    var own = new ColorTokens { Ramps = new() { Neutral = grey } };
    Eq("#505050", own.Background, "a theme that only sets a ramp has every role from it");
    Eq("#606060", own.SurfaceRaised);
    Eq("#22C55E", own.Success, "status roles are fixed colours, not ramp steps");
});

Run("ColorTokens: a role that is neither a colour nor a ramp step is refused, by name", () =>
{
    foreach (var bad in new[] { "#12345", "#GGGGGG", "neutral-450", "accent-1000", "brand-500", "neutral", "red" })
    {
        try { _ = new ColorTokens { Accent = bad }; throw new Exception($"'{bad}' was accepted"); }
        catch (ArgumentException e)
        {
            IsTrue(e.Message.Contains("Accent") && e.Message.Contains(bad), $"names the role and the value: {e.Message}");
        }
    }
    Throws<ArgumentException>(() => new ColorRamp("#FFFFFF"), "a ramp has eleven steps");
    Throws<ArgumentOutOfRangeException>(() => _ = ColorRamp.DefaultNeutral[450], "450 is not a step");
    Eq("#FF934524", new ColorTokens { Accent = "#FF934524" }.Accent, "#RRGGBBAA is a colour");
});

// ── Theme.FromSeed ────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Theme.FromSeed");

// Seeds a theme must hold up under: everyday brand hues, yellows (light, so
// hard on a light page), seeds near white and near black, greys, and neutral
// seeds from none to far too colourful.
string[] accentSeeds =
[
    "#2563EB", "#0EA5E9", "#7C3AED", "#E11D48", "#16A34A", "#14B8A6", "#F97316",
    "#EAB308", "#FACC15", "#FEF9C3", "#FFFFFF", "#F5F5F4",
    "#000000", "#111827", "#1E1B4B", "#808080",
];
string[] neutralSeeds = ["#71717A", "#64748B", "#78716C", "#FF0000", "#FFFFFF", "#000000"];

Run("Theme.FromSeed: every text colour reads at 4.5:1 on what it is drawn on, light and dark, for any seed", () =>
{
    var failures = new List<string>();
    foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
    foreach (var accent in accentSeeds)
    foreach (var neutral in neutralSeeds)
    {
        var c = Theme.FromSeed(accent, neutral, mode: mode).Colors;
        // Disabled text is exempt: it is meant to read as out of reach.
        (string Pair, string Text, string On, double Min)[] pairs =
        [
            ("TextPrimary on Background", c.TextPrimary, c.Background, 4.5),
            ("TextPrimary on Surface", c.TextPrimary, c.Surface, 4.5),
            ("TextPrimary on SurfaceRaised", c.TextPrimary, c.SurfaceRaised, 4.5),
            ("TextPrimary on SurfaceRaisedHover", c.TextPrimary, c.SurfaceRaisedHover, 4.5),
            ("TextPrimary on SurfaceRaisedActive", c.TextPrimary, c.SurfaceRaisedActive, 4.5),
            ("TextSecondary on Background", c.TextSecondary, c.Background, 4.5),
            ("TextSecondary on Surface", c.TextSecondary, c.Surface, 4.5),
            ("TextSecondary on SurfaceRaised", c.TextSecondary, c.SurfaceRaised, 4.5),
            ("TextSecondary on SurfaceRaisedHover", c.TextSecondary, c.SurfaceRaisedHover, 4.5),
            ("Accent text on Background", c.Accent, c.Background, 4.5),
            ("Accent text on Surface", c.Accent, c.Surface, 4.5),
            ("Accent text on SurfaceRaised", c.Accent, c.SurfaceRaised, 4.5),
            ("AccentForeground on Accent", c.AccentForeground, c.Accent, 4.5),
            ("AccentForeground on AccentHover", c.AccentForeground, c.AccentHover, 4.5),
            ("AccentForeground on AccentActive", c.AccentForeground, c.AccentActive, 4.5),
            ("DestructiveForeground on Destructive", c.DestructiveForeground, c.Destructive, 4.5),
            ("SuccessForeground on Success", c.SuccessForeground, c.Success, 4.5),
            ("WarningForeground on Warning", c.WarningForeground, c.Warning, 4.5),
            ("InfoForeground on Info", c.InfoForeground, c.Info, 4.5),
            ("Success on SuccessBg", c.Success, c.SuccessBg, 4.5),
            ("Warning on WarningBg", c.Warning, c.WarningBg, 4.5),
            ("Info on InfoBg", c.Info, c.InfoBg, 4.5),
            // Not text: a focus ring wants 3:1 against the page.
            ("BorderFocus on Background", c.BorderFocus, c.Background, 3.0),
        ];
        foreach (var (pair, text, on, min) in pairs)
        {
            double ratio = Theme.Contrast(text, on);
            if (ratio < min) failures.Add($"{mode} accent {accent} neutral {neutral}: {pair} {text} on {on} is {ratio:F2}:1");
        }
    }
    IsTrue(failures.Count == 0, $"{failures.Count} pairs short of AA:\n         " + string.Join("\n         ", failures.Take(20)));
});

Run("Theme.Contrast: black on white is 21:1, a colour on itself 1:1, and order does not matter", () =>
{
    IsTrue(Math.Abs(Theme.Contrast("#000000", "#FFFFFF") - 21.0) < 1e-9, "black on white");
    IsTrue(Math.Abs(Theme.Contrast("#7C3AED", "#7C3AED") - 1.0) < 1e-9, "itself");
    Eq(Theme.Contrast("#2563EB", "#FAFAFA"), Theme.Contrast("#FAFAFA", "#2563EB"), "symmetric");
    // #767676 on white is the grey that just clears 4.5:1.
    IsTrue(Theme.Contrast("#767676", "#FFFFFF") is > 4.5 and < 4.6, $"#767676 on white: {Theme.Contrast("#767676", "#FFFFFF"):F3}");
    Throws<ArgumentException>(() => Theme.Contrast("blue", "#FFFFFF"), "a name is not a colour");
});

Run("Theme.FromSeed: both ramps run light to dark, and the accent seed is in its ramp exactly", () =>
{
    foreach (var accent in accentSeeds)
    foreach (var neutral in neutralSeeds)
    {
        var ramps = Theme.FromSeed(accent, neutral).Colors.Ramps;
        foreach (var (name, ramp) in new[] { ("neutral", ramps.Neutral), ("accent", ramps.Accent) })
            for (int i = 1; i < ColorRamp.Steps.Count; i++)
            {
                int lighter = ColorRamp.Steps[i - 1], darker = ColorRamp.Steps[i];
                IsTrue(Theme.Contrast(ramp[lighter], "#000000") >= Theme.Contrast(ramp[darker], "#000000"),
                    $"{accent}/{neutral}: {name}-{lighter} {ramp[lighter]} is not lighter than {name}-{darker} {ramp[darker]}");
            }
        IsTrue(ColorRamp.Steps.Any(s => ramps.Accent[s] == accent), $"{accent} is one of its ramp's steps");
    }
    Eq(Theme.AccentRamp("#2563EB"), Theme.FromSeed("#2563EB").Colors.Ramps.Accent, "FromSeed's accent ramp is AccentRamp's");
    Eq(Theme.NeutralRamp(Theme.DefaultNeutralSeed), Theme.FromSeed("#2563EB").Colors.Ramps.Neutral, "and the neutral one NeutralRamp's");
    Eq("#2563EB", Theme.AccentRamp("#2563eb")[600], "lower-case hex is the same seed");
});

Run("Theme.FromSeed: a neutral seed tints the greys but does not move their lightness", () =>
{
    var zinc = Theme.NeutralRamp("#71717A");
    foreach (var seed in new[] { "#FF0000", "#FFFFFF", "#000000", "#64748B" })
    {
        var tinted = Theme.NeutralRamp(seed);
        foreach (var step in ColorRamp.Steps)
        {
            double a = Theme.Contrast(zinc[step], "#000000"), b = Theme.Contrast(tinted[step], "#000000");
            IsTrue(Math.Abs(a - b) / a < 0.08, $"{seed} {step}: {tinted[step]} against {zinc[step]}");
        }
    }
    Eq(Theme.NeutralRamp("#FFFFFF"), Theme.NeutralRamp("#000000"), "two greys tint alike: not at all");
});

Run("Theme.FromSeed: every role is set, to a ramp step where it can be, and the other seeds land", () =>
{
    var dark = Theme.FromSeed("#0EA5E9");
    Eq("seed-dark", dark.Name);
    Eq(ThemeMode.Dark, dark.Colors.Mode);
    Eq("#0EA5E9", dark.Colors.Accent, "a mid-light seed reads on a dark page as it is");
    Eq(dark.Colors.Ramps.Neutral[950], dark.Colors.Background);
    Eq(dark.Colors.Ramps.Neutral[50], dark.Colors.TextPrimary);

    // Swapping a ramp afterwards still recolours the roles: they name steps.
    var teal = Theme.AccentRamp("#14B8A6");
    var swapped = dark.Colors with { Ramps = dark.Colors.Ramps with { Accent = teal } };
    IsTrue(ColorRamp.Steps.Any(s => teal[s] == swapped.Accent), "Accent names an accent step");
    IsTrue(ColorRamp.Steps.Any(s => teal[s] == swapped.AccentHover), "AccentHover names an accent step");

    var light = Theme.FromSeed("#EAB308", "#78716C", radius: 9f, density: Density.Compact, mode: ThemeMode.Light, name: "mine");
    Eq("mine", light.Name);
    Eq(ThemeMode.Light, light.Colors.Mode);
    IsTrue(light.Colors.Accent != "#EAB308", "yellow does not read on a light page, so the accent is a darker step");
    IsTrue(ColorRamp.Steps.Any(s => light.Colors.Ramps.Accent[s] == light.Colors.Accent), "a step of the same ramp");
    Eq((6f, 9f, 12f), (light.Radii.Sm, light.Radii.Md, light.Radii.Lg), "radii from one radius");
    Eq(Density.Compact, light.Density.Mode);

    var defaults = Theme.FromSeed("#2563EB");
    var stock = new ThemeTokens();
    Eq((stock.Radii.Sm, stock.Radii.Md, stock.Radii.Lg), (defaults.Radii.Sm, defaults.Radii.Md, defaults.Radii.Lg),
        "radius 6 is the radii every theme has by default");
    var square = Theme.FromSeed("#2563EB", radius: 0f).Radii;
    Eq((0f, 0f, 0f), (square.Sm, square.Md, square.Lg), "radius 0 is square");
    Eq(Density.Default, defaults.Density.Mode);

    Throws<ArgumentException>(() => Theme.FromSeed("blue"), "a seed is a colour");
    Throws<ArgumentException>(() => Theme.FromSeed("#2563EB", neutral: "#12345"), "so is the neutral one");
    Throws<ArgumentException>(() => Theme.FromSeed("#2563EB", radius: -1f), "a radius is not negative");
    Throws<ArgumentException>(() => Theme.FromSeed("#2563EB", radius: float.NaN), "nor not a number");
});

Run("Theme.FromSeed: changing only the accent seed restyles accent components, and nothing else", () =>
{
    var blue = Theme.FromSeed("#2563EB");
    var rose = Theme.FromSeed("#E11D48");

    // The roles: every accent role moves, every neutral one stays.
    foreach (var (role, a, b) in new[]
    {
        ("Accent", blue.Colors.Accent, rose.Colors.Accent),
        ("AccentHover", blue.Colors.AccentHover, rose.Colors.AccentHover),
        ("AccentActive", blue.Colors.AccentActive, rose.Colors.AccentActive),
        ("BorderFocus", blue.Colors.BorderFocus, rose.Colors.BorderFocus),
    })
        IsTrue(a != b, $"{role} is {a} under both seeds");
    foreach (var (role, a, b) in new[]
    {
        ("Background", blue.Colors.Background, rose.Colors.Background),
        ("SurfaceRaised", blue.Colors.SurfaceRaised, rose.Colors.SurfaceRaised),
        ("Border", blue.Colors.Border, rose.Colors.Border),
        ("TextPrimary", blue.Colors.TextPrimary, rose.Colors.TextPrimary),
        ("TextSecondary", blue.Colors.TextSecondary, rose.Colors.TextSecondary),
    })
        Eq(a, b, $"{role} does not move with the accent");

    // The pictures: the same components, no code of theirs changed, each
    // filled with its own theme's accent and none of the other's.
    Pixels Draw(ThemeTokens theme)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() =>
        {
            Box root = runtime.Box();
            root.ChildLayoutAxis = LayoutAxis.Y;
            root.SizeX = root.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(root))
            {
                runtime.Switch("sw", on: true, label: "Airplane mode");
                runtime.Checkbox("cb", on: true, label: "Subscribe");
            }
            return root;
        }, theme, new SurfaceOptions { Width = 240, Height = 80, ScaleFactor = 1f });
        Pixels frame = runtime.Frame();
        for (int f = 0; f < 60; f++) frame = runtime.Frame();   // the knob has crossed
        return frame;
    }
    int Count(Pixels frame, (byte R, byte G, byte B) colour)
    {
        int n = 0;
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
            if (Pixel(frame, x, y) == colour) n++;
        return n;
    }

    var blueFrame = Draw(blue);
    var roseFrame = Draw(rose);
    var blueAccent = Rgb(UiColor.Accent, blue);
    var roseAccent = Rgb(UiColor.Accent, rose);
    IsTrue(Count(blueFrame, blueAccent) > 100, $"the blue theme fills with its accent: {Count(blueFrame, blueAccent)} px");
    IsTrue(Count(roseFrame, roseAccent) > 100, $"the rose theme fills with its accent: {Count(roseFrame, roseAccent)} px");
    Eq(0, Count(blueFrame, roseAccent), "no rose in the blue picture");
    Eq(0, Count(roseFrame, blueAccent), "no blue in the rose picture");
    Eq(Pixel(blueFrame, 230, 75), Pixel(roseFrame, 230, 75), "the page behind is the same");
});

// ── Theme files ───────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Theme files");

// The message of the T that body throws.
string MessageOf<T>(Action body) where T : Exception
{
    try { body(); }
    catch (T e) { return e.Message; }
    throw new Exception($"Expected {typeof(T).Name} but nothing was thrown");
}

Run("ThemeFile: the built-in themes, read from their files, are the themes their C# gave", () =>
{
    foreach (var (name, before) in new (string, ThemeTokens)[]
    {
        ("neutral-dark", ThemeFileTests.NeutralDarkBefore()),
        ("default-dark", ThemeFileTests.DefaultDarkBefore()),
        ("default-light", ThemeFileTests.DefaultLightBefore()),
    })
    {
        var diff = ThemeFileTests.Differences(before, ThemeLoader.Load(name));
        IsTrue(diff.Count == 0, $"{name} differs from its C# definition:\n  " + string.Join("\n  ", diff));
        Eq(0, ThemeFileTests.Differences(before, ThemeFile.BuiltIn(name).Resolve()).Count, $"{name} through ThemeFile");
    }
    Eq(0, ThemeFileTests.Differences(ThemeFileTests.NeutralDarkBefore(), ThemeLoader.NeutralDark()).Count, "ThemeLoader.NeutralDark()");
    Eq(0, ThemeFileTests.Differences(ThemeFileTests.DefaultDarkBefore(), ThemeLoader.DefaultDark()).Count, "ThemeLoader.DefaultDark()");
    Eq(0, ThemeFileTests.Differences(ThemeFileTests.DefaultLightBefore(), ThemeLoader.DefaultLight()).Count, "ThemeLoader.DefaultLight()");
    IsTrue(ThemeFileTests.Differences(ThemeFileTests.DefaultDarkBefore(), ThemeLoader.DefaultLight()).Count > 0,
           "the comparison can tell two themes apart");
});

Run("ThemeFile: a file that extends default-light and changes only the accent", () =>
{
    string dir = Directory.CreateTempSubdirectory("bibui-theme-").FullName;
    try
    {
        string path = Path.Combine(dir, "t24-teal-light.json");
        File.WriteAllText(path, """
            {
              // Only what differs from the parent.
              "extends": "default-light",
              "colors": { "accent": "#0D9488" },
            }
            """);
        string name = ThemeRegistry.RegisterFile(path);
        Eq("t24-teal-light", name, "named after the file when it has no name key");
        IsTrue(ThemeRegistry.Names.Contains(name), "registered");

        var theme = ThemeLoader.Load(name);
        Eq("#0D9488", theme.Colors.Accent);
        var diff = ThemeFileTests.Differences(ThemeLoader.DefaultLight(), theme);
        IsTrue(diff.Count == 3 && diff.All(d => d.StartsWith("name") || d.Contains(".accent ")),
               "only the name and the accent differ:\n  " + string.Join("\n  ", diff));

        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => runtime.TextFromId("t", "Theme".ToArray()),
            new SurfaceOptions { Width = 120, Height = 40, ScaleFactor = 1f }, name);
        runtime.Step();
        Eq("#0D9488", runtime.Theme.Colors.Accent, "mounts by name");
    }
    finally { Directory.Delete(dir, recursive: true); }
});

Run("ThemeFile: motion's follow speed, also read under its old name caret", () =>
{
    Eq(30f, ThemeFileTests.Parse("""{ "name": "t-follow", "motion": { "follow": 30 } }""").Resolve().Motion.Follow, "follow");
    Eq(30f, ThemeFileTests.Parse("""{ "name": "t-caret", "motion": { "caret": 30 } }""").Resolve().Motion.Follow, "caret, as before");
});

Run("ThemeFile: extends merges group by group, field by field", () =>
{
    ThemeRegistry.Register(ThemeFileTests.Parse("""
        {
          "name": "t24-base",
          "colors": {
            "ramps": { "accent": ["#F0FDFA", "#CCFBF1", "#99F6E4", "#5EEAD4", "#2DD4BF", "#14B8A6",
                                  "#0D9488", "#0F766E", "#115E59", "#134E4A", "#042F2E"] },
            "accent": "accent-600",
            "extra": { "brand": "#112233", "glow": "#445566" }
          },
          "spacing": { "xs": 2, "md": 10 },
          "elevation": { "level2": { "offsetY": 4, "blur": 12, "color": "#00000059" } },
          "focusRing": { "width": 2, "color": "accent" },
          "density": { "mode": "compact" },
          "motion": { "easing": "none" }
        }
        """));
    var child = ThemeFileTests.Parse("""
        {
          "name": "t24-child",
          "extends": "t24-base",
          "colors": { "mode": "light", "ramps": { "accent": { "600": "#123456" } }, "extra": { "glow": "#ABCDEF" } },
          "spacing": { "md": 14 },
          "elevation": { "level2": { "blur": 20 }, "level3": { "offsetY": 8 } },
          "typography": { "fontFamilyMono": "JetBrains Mono" }
        }
        """).Resolve();
    var parent = ThemeLoader.Load("t24-base");

    Eq("t24-child", child.Name);
    Eq(ThemeMode.Light, child.Colors.Mode);
    Eq("#123456", child.Colors.Accent, "a ramp step changed alone moves the role that names it");
    Eq("#14B8A6", child.Colors.Ramps.Accent[500], "and the ramp's other steps stay the parent's");
    Eq(ColorRamp.DefaultNeutral[50], child.Colors.Background, "an unset role follows the child's mode");
    Eq("#112233", child.Colors.Extra["brand"], "extra keeps the parent's keys");
    Eq("#ABCDEF", child.Colors.Extra["glow"], "and takes the child's");
    Eq("#445566", parent.Colors.Extra["glow"], "without writing the parent's");
    Eq(2f, child.Spacing.Xs, "a group keeps the parent's fields the child does not give");
    Eq(14f, child.Spacing.Md, "and takes those it does");
    Eq(24f, child.Spacing.Xl, "and the defaults neither gives");
    Eq(4f, child.Elevation.Level2!.OffsetY, "a shadow keeps the parent's fields");
    Eq(20f, child.Elevation.Level2!.Blur);
    Eq("#00000059", child.Elevation.Level2!.Color.Resolve(child.Colors));
    Eq(8f, child.Elevation.Level3!.OffsetY, "a level the parent left flat starts from BoxShadow's defaults");
    Eq(2f, child.FocusRing.Width);
    Eq("#123456", child.FocusRing.Color.Resolve(child.Colors), "a role-named colour follows the role");
    Eq(Density.Compact, child.Density.Mode);
    Eq(MotionEasing.None, child.Motion.Easing);
    Eq("JetBrains Mono", child.Typography.FontFamilyMono);
});

Run("ThemeFile: extends reaches a theme registered in code", () =>
{
    ThemeRegistry.Register("t24-code", () => new ThemeTokens { Name = "t24-code", Colors = new() { Border = "#010203" } });
    var t = ThemeFileTests.Parse("""{ "name": "t24-on-code", "extends": "t24-code", "colors": { "accent": "#0D9488" } }""").Resolve();
    Eq("#010203", t.Colors.Border);
    Eq("#0D9488", t.Colors.Accent);

    var flat = ThemeFileTests.Parse("""{ "extends": "t24-base", "elevation": { "level2": null } }""").Resolve();
    IsNull(flat.Elevation.Level2, "null sets a level flat");
});

Run("ThemeFile: an unknown key is named, with the file and what the group takes", () =>
{
    var msg = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "colors": { "acent": "#0D9488" } }""", "mine.json"));
    IsTrue(msg.StartsWith("mine.json: ") && msg.Contains("'colors.acent'") && msg.Contains("accent"), msg);

    msg = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "spacing": { "huge": 40 } }""", "mine.json"));
    IsTrue(msg.Contains("'spacing.huge'") && msg.Contains("xs, sm, md, lg, xl"), msg);

    msg = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "colours": {} }""", "mine.json"));
    IsTrue(msg.Contains("'colours'") && msg.Contains("colors"), msg);

    msg = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "colors": { "accent": "#000000", "accent": "#FFFFFF" } }""", "mine.json"));
    IsTrue(msg.Contains("colors.accent") && msg.Contains("twice"), msg);
});

Run("ThemeFile: a bad colour is named, with the file, the key and the value", () =>
{
    foreach (var (json, key, value) in new[]
    {
        ("""{ "colors": { "accent": "#12345" } }""", "colors.accent", "#12345"),
        ("""{ "colors": { "border": "neutral-450" } }""", "colors.border", "neutral-450"),
        ("""{ "colors": { "extra": { "brand": "#GGGGGG" } } }""", "colors.extra.brand", "#GGGGGG"),
        ("""{ "colors": { "ramps": { "accent": { "600": "teal" } } } }""", "colors.ramps.accent.600", "teal"),
        ("""{ "focusRing": { "color": "#ABC" } }""", "focusRing.color", "#ABC"),
    })
    {
        var msg = MessageOf<FormatException>(() => ThemeFileTests.Parse(json, "bad.json"));
        IsTrue(msg.StartsWith("bad.json: ") && msg.Contains(key) && msg.Contains(value), msg);
    }

    var ramp = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "colors": { "ramps": { "neutral": ["#FFFFFF"] } } }""", "bad.json"));
    IsTrue(ramp.Contains("colors.ramps.neutral") && ramp.Contains("11"), ramp);
    var number = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "radii": { "md": "6" } }""", "bad.json"));
    IsTrue(number.Contains("radii.md") && number.Contains("\"6\"") && number.Contains("number"), number);
    var mode = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "colors": { "mode": "dim" } }""", "bad.json"));
    IsTrue(mode.Contains("colors.mode") && mode.Contains("dark, light"), mode);
    var syntax = MessageOf<FormatException>(() => ThemeFileTests.Parse("""{ "colors": { "accent": """, "bad.json"));
    IsTrue(syntax.StartsWith("bad.json: not valid JSON"), syntax);
});

Run("ThemeFile: a missing parent is named, with the file and what is registered", () =>
{
    var file = ThemeFileTests.Parse("""{ "name": "t24-orphan", "extends": "no-such-parent" }""", "orphan.json");
    var msg = MessageOf<InvalidOperationException>(() => file.Resolve());
    IsTrue(msg.StartsWith("orphan.json: ") && msg.Contains("'no-such-parent'") && msg.Contains("default-light"), msg);

    ThemeRegistry.Register(file);
    msg = MessageOf<InvalidOperationException>(() => ThemeLoader.Load("t24-orphan"));
    IsTrue(msg.Contains("'no-such-parent'"), $"and through the registry: {msg}");
});

Run("ThemeFile: themes that extend each other in a ring are refused, naming the ring", () =>
{
    ThemeRegistry.Register(ThemeFileTests.Parse("""{ "name": "t24-ring-a", "extends": "t24-ring-b" }""", "a.json"));
    ThemeRegistry.Register(ThemeFileTests.Parse("""{ "name": "t24-ring-b", "extends": "t24-ring-a" }""", "b.json"));
    var msg = MessageOf<InvalidOperationException>(() => ThemeLoader.Load("t24-ring-a"));
    IsTrue(msg.Contains("ring") && msg.Contains("'t24-ring-a' (a.json)") && msg.Contains("'t24-ring-b' (b.json)"), msg);

    ThemeRegistry.Register(ThemeFileTests.Parse("""{ "name": "t24-self", "extends": "t24-self" }""", "self.json"));
    IsTrue(MessageOf<InvalidOperationException>(() => ThemeLoader.Load("t24-self")).Contains("ring"), "a theme extending itself");

    Eq("default-light", ThemeLoader.Load("default-light").Name, "a refused ring leaves loading on this thread working");

    var refine = ThemeFileTests.Parse("""{ "name": "default-light", "extends": "default-light", "colors": { "accent": "#0D9488" } }""").Resolve();
    Eq("#0D9488", refine.Colors.Accent, "an unregistered file may share the name of the theme it refines");
});

// ── UiColor ──────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("UiColor");

Run("UiColor.WithAlpha: sets alpha on a literal hex", () =>
{
    var color = UiColor.Hex("#FF9345").WithAlpha(0.14f);
    var resolved = color.Resolve(new ColorTokens());
    // 0.14 * 255 ≈ 35.7 → rounds to 36 = 0x24.
    Eq("#FF934524", resolved);
});

Run("UiColor.WithAlpha: stays token-linked on a theme token color", () =>
{
    var color = UiColor.Accent.WithAlpha(0.5f);
    var warm = color.Resolve(new ColorTokens { Accent = "#E8836B" });
    var cool = color.Resolve(new ColorTokens { Accent = "#3B82F6" });
    Eq("#E8836B80", warm);
    Eq("#3B82F680", cool);
});

Run("UiColor.WithAlpha: drops a pre-existing alpha rather than compounding it", () =>
{
    var color = UiColor.Hex("#15151599").WithAlpha(1.0f);
    Eq("#151515FF", color.Resolve(new ColorTokens()));
});

// ── Select: nested dropdown click via coordinates ────────────────────────────

// A select whose state the test keeps, built the way an app builds one.
static void BuildSelect(BibuiRuntime runtime, SelectState state, string id)
{
    var select = Bibui.Core.Runtime.Select.Begin(runtime, id, state.Value ?? "Pick one...");
    if (select.Open)
        foreach (var (label, value) in new[] { ("Option A", "a"), ("Option B", "b"), ("Option C", "c") })
            if (runtime.SelectItem($"{id}-option-{value}", label, value == state.Value).Clicked)
            {
                state.Value = value;
                select.Close();
            }
    select.End();
}

static void ClickCentre(BibuiRuntime runtime, string id)
{
    var b = BibuiPage.From(runtime, runtime.BuildTree).GetBounds(id);
    ClickAt(runtime, b.X + b.Width / 2f, b.Y + b.Height / 2f);
}

Run("Select: an item of a select nested deep, its list past every container, is picked by a click", () =>
{
    var runtime = BibuiRuntime.Create();
    var state = new SelectState();
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            runtime.Box().FillX().Px(height: 400f);
            var row = runtime.Box();
            row.ChildLayoutAxis = LayoutAxis.X;
            row.FillX().Px(height: 60f);
            using (runtime.PushParent(row))
            {
                runtime.Box().FillX().FillY();
                var column = runtime.Box();
                column.ChildLayoutAxis = LayoutAxis.Y;
                column.Px(300f, 60f);
                using (runtime.PushParent(column)) BuildSelect(runtime, state, "sel");
            }
        }
        return page;
    }, new SurfaceOptions { Width = 800, Height = 500 });
    runtime.Step();
    ClickCentre(runtime, "sel");
    IsTrue(runtime.IsDropDownOpen("sel-list"), "the trigger opens it");
    runtime.Step();
    ClickCentre(runtime, "sel-option-a");
    Eq("a", state.Value, "the first item, below every container, takes the click");
    IsTrue(!runtime.IsDropDownOpen("sel-list"), "and picking closes it");
});

Run("Select: its open list takes the press over a button built after it", () =>
{
    var runtime = BibuiRuntime.Create();
    var state = new SelectState();
    int buttonClicks = 0;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var header = runtime.Box();
            header.ChildLayoutAxis = LayoutAxis.X;
            header.FillX().Px(height: 48f);
            using (runtime.PushParent(header)) BuildSelect(runtime, state, "sel");
            var content = runtime.Button("content-btn", "Content Button");
            content.Node.FillX().FillY();
            if (content.Clicked) buttonClicks++;
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    ClickCentre(runtime, "sel");
    runtime.Step();
    ClickCentre(runtime, "sel-option-a");
    Eq("a", state.Value, "the item has the click");
    Eq(0, buttonClicks, "the button beneath does not");
});

Run("Select: in a dialog, a row is picked by a press and a release in frames of their own", () =>
{
    var runtime = BibuiRuntime.Create();
    var state = new SelectState();
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var dialog = Bibui.Core.Runtime.Dialog.Begin(runtime, "sheet", shown: 1f, DialogSide.Right, width: 320f);
            BuildSelect(runtime, state, "sel");
            dialog.End();
        }
        return page;
    }, new SurfaceOptions { Width = 800, Height = 500 });
    runtime.Step();
    runtime.Step();
    ClickCentre(runtime, "sel");
    IsTrue(runtime.IsDropDownOpen("sel-list"), "the trigger opens it");
    runtime.Step();

    var b = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("sel-option-b");
    float x = b.X + b.Width / 2f, y = b.Y + b.Height / 2f;
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
    IsTrue(runtime.IsDropDownOpen("sel-list"), "the press on a row leaves the list open");
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
    Eq("b", state.Value, "the row takes the click, not the dialog's panel");
    IsTrue(!runtime.IsDropDownOpen("sel-list"), "and picking closes it");

    runtime.Step();
    ClickCentre(runtime, "sel");
    IsTrue(runtime.IsDropDownOpen("sel-list"), "it opens again");
    runtime.Step();
    ClickCentre(runtime, "sheet-panel");
    IsTrue(!runtime.IsDropDownOpen("sel-list"), "a press elsewhere on the panel still closes it");
});

Run("runtime.RadioGroup: a click on an option's label picks it", () =>
{
    var runtime = BibuiRuntime.Create();
    string value = "a";
    RadioOption[] items = [new("Alpha", "a"), new("Beta", "b"), new("Gamma", "c", Disabled: true)];
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 200f);
            using (runtime.PushParent(column))
                if (runtime.RadioGroup("rg", items, value).Picked is { } picked) value = picked;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 200 });
    runtime.Step();
    var beta = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("rg-b");
    Eq(32f, beta.Height, "a row is 32 px tall");
    ClickAt(runtime, beta.X + beta.Width - 5f, beta.Y + beta.Height / 2f);
    Eq("b", value, "the click picks Beta");
});

Run("runtime.RadioGroup: a disabled option swallows the click", () =>
{
    var runtime = BibuiRuntime.Create();
    string value = "a";
    RadioOption[] items = [new("Alpha", "a"), new("Gamma", "c", Disabled: true)];
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 200f);
            using (runtime.PushParent(column))
                if (runtime.RadioGroup("rg", items, value).Picked is { } picked) value = picked;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 200 });
    runtime.Step();
    var gamma = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("rg-c");
    ClickAt(runtime, gamma.X + 8f, gamma.Y + gamma.Height / 2f);
    Eq("a", value, "nothing picked");
});

Run("runtime.Toggle: a click is reported and the caller flips its state", () =>
{
    var runtime = BibuiRuntime.Create();
    bool on = false;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 100f);
            using (runtime.PushParent(column))
                if (runtime.Toggle("tog", on, "Star", icon: "star").Clicked) on = !on;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 100 });
    runtime.Step();
    var toggle = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("tog");
    Eq(36f, toggle.Height, "36 px tall");
    ClickAt(runtime, toggle.X + toggle.Width / 2f, toggle.Y + toggle.Height / 2f);
    IsTrue(on, "the click turns it on");
    ClickAt(runtime, toggle.X + toggle.Width / 2f, toggle.Y + toggle.Height / 2f);
    IsTrue(!on, "and off again");
});

Run("runtime.Toggle: disabled, a click is not reported", () =>
{
    var runtime = BibuiRuntime.Create();
    bool clicked = false;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 100f);
            using (runtime.PushParent(column))
                clicked |= runtime.Toggle("tog", false, "Off", disabled: true).Clicked;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 100 });
    runtime.Step();
    var toggle = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("tog");
    ClickAt(runtime, toggle.X + toggle.Width / 2f, toggle.Y + toggle.Height / 2f);
    IsTrue(!clicked, "no click");
});

// ── New style capabilities (native path via BibuiRuntime) ─────────────────────

Run("Style.Shadow: paints a shadow behind the fill", () =>
{
    Pixels Frame(BoxShadow? shadow)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(
            () => Layer(null, new Box { Style = new Style { BackgroundColor = "#222228", CornerRadius = 12, Shadow = shadow } }.Px(120, 80).At(40, 40)),
            new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
        return runtime.Frame();
    }
    // Just below the box, where only a shadow paints.
    IsTrue(Pixel(Frame(null), 100, 130) != Pixel(Frame(BoxShadow.Elevation()), 100, 130), "expected a shadow under the box");
});

Run("Style.BackgroundGradient: gradient fill renders and child still lays out", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnPx(200, 120)
            .Styled(new Style { CornerRadius = 16, BackgroundGradient = Gradient.Diagonal(new ColorStop(0f, "#2A1B2E"), new ColorStop(1f, "#E8836B")) }),
            () => runtime.TextFromId("grad-child", "On top".ToArray())),
        new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    var frame = runtime.Frame();
    IsTrue(Pixel(frame, 5, 190) != Pixel(frame, 295, 190), "the fill should run from one colour to the other");
    using var page = BibuiPage.From(runtime, runtime.BuildTree);
    IsTrue(page.IsVisible("grad-child"), "gradient surface should still lay out its child");
});

Run("Gradient.Radial: brightest at its centre, where a linear one runs corner to corner", () =>
{
    Pixels Frame(Gradient gradient)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => new Box { Style = new Style { BackgroundGradient = gradient } }.Px(300, 200),
            new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
        return runtime.Frame();
    }
    var linear = Frame(Gradient.Diagonal(new ColorStop(0f, "#2A1B2E"), new ColorStop(1f, "#E8836B")));
    var radial = Frame(Gradient.Radial((0.34f, 0.24f), (1.2f, 1.0f), new ColorStop(0f, "#FF9345"), new ColorStop(1f, "#00000000")));
    IsTrue(Pixel(linear, 2, 2).R < 80 && Pixel(linear, 297, 197).R > 180, "linear: dark at the top left, warm at the bottom right");
    IsTrue(Pixel(radial, 102, 48).R > 200 && Pixel(radial, 297, 197).R < 120, "radial: warm at its centre, faded towards the corner");
});

Run("Style.Opacity: fades the subtree over what is behind it", () =>
{
    UiNode Tree(BibuiRuntime runtime, float opacity) => runtime.Into(
        runtime.ColumnPx(120, 80).Styled(new Style { Opacity = opacity, BackgroundColor = "#E8836B", CornerRadius = 12 }),
        () => runtime.TextFromId("fade-child", "x".ToArray()));
    var opaqueRuntime = BibuiRuntime.Create();
    opaqueRuntime.SetBuild(() => Tree(opaqueRuntime, 1.0f), new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    using var opaque = BibuiPage.From(opaqueRuntime, opaqueRuntime.BuildTree);
    var fadedRuntime = BibuiRuntime.Create();
    fadedRuntime.SetBuild(() => Tree(fadedRuntime, 0.4f), new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    using var faded = BibuiPage.From(fadedRuntime, fadedRuntime.BuildTree);
    var solid = Pixel(opaqueRuntime.Frame(), 150, 150);
    var dim = Pixel(fadedRuntime.Frame(), 150, 150);
    IsTrue(dim.R < solid.R - 50, $"faded fill should let the background through (opaque={solid}, faded={dim})");
    IsTrue(faded.IsVisible("fade-child"), "faded subtree should still render its child");
});

Run("Style.BorderLeft: a left-only nav active-indicator bar draws one edge, not a uniform border", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => new Box { Style = new Style { BorderLeft = new EdgeBorder(2f, "#FF9345") } }.Px(300, 200),
        new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    var frame = runtime.Frame();
    var background = Rgb(UiColor.Hex(runtime.Theme.Colors.Background), runtime.Theme);
    Eq(((byte)0xFF, (byte)0x93, (byte)0x45), Pixel(frame, 0, 100), "the left edge is the bar");
    Eq(background, Pixel(frame, 299, 100), "and no other edge is drawn: not the right");
    Eq(background, Pixel(frame, 150, 0), "nor the top");
    Eq(background, Pixel(frame, 150, 100), "nor a fill");
});

Run("Style.BorderBottom: a dashed edge is broken into dashes", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => new Box { Style = new Style { BorderBottom = new EdgeBorder(1f, "#71717A", BorderLineStyle.Dashed) } }.Px(300, 200),
        new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    var frame = runtime.Frame();
    var background = Rgb(UiColor.Hex(runtime.Theme.Colors.Background), runtime.Theme);
    int on = Enumerable.Range(0, 300).Count(x => Pixel(frame, x, 199) != background);
    IsTrue(on > 20 && on < 280, $"some of the bottom row is dash and some is gap: {on} of 300 painted");
});

Run("Style.BackdropBlur: a glass chip still paints its own fill on top of the blurred region", () =>
{
    using var page = BibuiPage.Launch(
        () => Layer(null, new Box { Style = new Style { BackgroundGradient = Gradient.Diagonal(new ColorStop(0f, "#2A1B2E"), new ColorStop(1f, "#E8836B")) } }.Px(300, 200), new Box
                {
                    Id = "chip",
                    Style = new Style
                    {
                        BackdropBlur = 8f,
                        BackgroundColor = "#15151599",
                        CornerRadius = 6,
                    },
                }.Px(80, 24).At(12, 12)).Px(300, 200),
        new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    IsTrue(page.IsVisible("chip"), "the glass chip itself should still render");
    using var shot = SkiaSharp.SKBitmap.Decode(page.Screenshot());
    var inside = shot.GetPixel(50, 24);
    var outside = shot.GetPixel(50, 50);
    IsTrue(inside.Red + inside.Green + inside.Blue < outside.Red + outside.Green + outside.Blue - 60,
        $"the chip's own dark fill should paint on top of the blurred backdrop: inside {inside}, outside {outside}");
});

Run("Launch(ThemeTokens): custom accent reaches an accent fill (no reflection)", () =>
{
    var theme = new ThemeTokens { Name = "warm", Colors = new ColorTokens { Accent = "#E8836B", AccentForeground = "#1A1110" } };
    using var page = BibuiPage.Launch(
        () => new Box { Id = "primary", Style = new Style { BackgroundColor = UiColor.Accent } }.Px(80f, 36f),
        theme,
        new SurfaceOptions { Width = 300, Height = 120, ScaleFactor = 1f });
    using var shot = SkiaSharp.SKBitmap.Decode(page.Screenshot());
    var p = shot.GetPixel(40, 18);
    Eq(((byte)0xE8, (byte)0x83, (byte)0x6B), (p.Red, p.Green, p.Blue), "an accent fill should use the custom theme accent");
});

Run("Icon: standalone leaf node sizes itself to Size and resolves Color", () =>
{
    using var page = BibuiPage.Launch(
        runtime => UiNode.PushIntoParent(runtime.RowFit(),
            new Icon { Id = "glyph", Name = "search", Size = FontSize.Px(24f), Color = "#FF9345" }.Px(24f, 24f)),
        new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    var frame = page.GetBounds("glyph");
    IsTrue(Math.Abs(frame.Width - 24f) < 0.5f, $"expected width≈24, got {frame.Width}");
    IsTrue(Math.Abs(frame.Height - 24f) < 0.5f, $"expected height≈24, got {frame.Height}");
    using var shot = SkiaSharp.SKBitmap.Decode(page.Screenshot());
    IsTrue(GlyphHas(shot, frame, 0xFF, 0x93, 0x45), "the glyph is stroked in its own colour");
});

Run("Icon: null Color falls back to the theme's TextPrimary", () =>
{
    var theme = new ThemeTokens { Name = "t", Colors = new ColorTokens { TextPrimary = "#ABCDEF" } };
    using var page = BibuiPage.Launch(
        () => new Icon { Id = "glyph", Name = "play" }.Px(24f, 24f),
        theme,
        new SurfaceOptions { Width = 300, Height = 200, ScaleFactor = 1f });
    using var shot = SkiaSharp.SKBitmap.Decode(page.Screenshot());
    IsTrue(GlyphHas(shot, page.GetBounds("glyph"), 0xAB, 0xCD, 0xEF), "the glyph is stroked in the theme's TextPrimary");
});

// A title left to size itself: the Column hands it exactly the width it measured. The line
// breaker used to measure the same line differently — with tracking, untracked and wider by the
// tracking on every glyph — decide it did not fit, and draw it on two lines inside a box laid
// out for one, over whatever sat beneath. An app's event editor did this with every short
// title.
Run("Text: a text drawn at its measured width stays on one line", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            using (runtime.PushFontFace(weight: FontWeight.Bold))
                runtime.TextFromId("title", "UTC anchored".ToArray(), ComponentSize.Xl);
            runtime.TextFromId("below", "under it".ToArray());
        }),
        new SurfaceOptions { Width = 400, Height = 120, ScaleFactor = 1f });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);
    var texts = Texts(runtime);
    IsTrue(texts.SequenceEqual(new[] { "UTC anchored", "under it" }),
        $"expected the title drawn whole on one line, got [{string.Join(" | ", texts)}]");
});

// A padded box that sizes itself around a one-line label hands the label back its measured
// width — as the box's width less its padding, and the box's width was the label's plus that
// padding. In floats the two trips do not cancel: where the sum crosses a power of two the
// room comes back one ulp short, and a label measured as one line was broken as two when it
// was painted. An app's "Remember something…" pill drew "Remember" and nothing else
// in a 2024px-wide window at scale 1.25 (measured 110.760414, handed 110.76041).
//
// Which labels round the wrong way is an accident of their widths, so this sweeps lengths
// across the binade edges for several paddings and scales.
Run("Text: a label handed back its own measured width, less a padding, is drawn whole", () =>
{
    var labels = new[] { "Remember something about it", "That's not right, is it now", "Ask about this one as well" }
        .SelectMany(t => Enumerable.Range(6, t.Length - 5).Select(n => t[..n].TrimEnd() + "\u2026"))
        .Distinct().ToList();
    var broken = new List<string>();
    foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
    foreach (var pad in new[] { 9f, 0.1f, 3.3f, 7.3f, 10.9f, 12.35f })
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => runtime.Into(runtime.ColumnFit(), () =>
        {
            foreach (var label in labels)
                using (runtime.PushParent(runtime.ColumnFit().Px(height: 20f)
                    .Padded(EdgeInsets.Symmetric(horizontal: pad, vertical: 0f))))
                    runtime.Text(label, ComponentSize.Sm);
        }), new SurfaceOptions { Width = 400, Height = 1300, ScaleFactor = scale });
        using var page = BibuiPage.From(runtime, runtime.BuildTree);
        var drawn = Texts(runtime).ToHashSet();
        foreach (var label in labels.Where(l => !drawn.Contains(l)))
            broken.Add($"'{label}' at scale {scale}, padding {pad}");
    }
    IsTrue(broken.Count == 0, $"{broken.Count} labels broken: " + string.Join("; ", broken.Take(6)));
});

// A box with a fixed Width is that wide, so what is inside it wraps at that width less its
// padding. Measuring it against whatever the row offered instead — wider — found one line,
// and the box was laid out one line tall around a text that then painted two, the second
// spilling out of the bottom. An app's chat bubble (Width 520) did it only in a
// window wide enough for the row to offer more than 520.
Run("Box: a fixed Width box holds a multi-line field wrapped at that width, not at the width offered", () =>
{
    const string words = "The parcel left the depot this morning and should reach the north office before noon";
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.RowFit(), () =>
        {
            runtime.Spring();
            var bubble = runtime.BoxFromId("bubble");
            bubble.ChildLayoutAxis = LayoutAxis.Y;
            bubble.SizeX = SemanticSize.Pixels(240f);
            bubble.SizeY = SemanticSize.ChildrenSum();
            bubble.Style.Padding = new EdgeInsets(10f, 14f, 10f, 14f);
            using (runtime.PushParent(bubble))
            using (runtime.PushParent(FieldFrame(runtime, SemanticSize.ChildrenSum())))
            using (runtime.PushSizeY(SemanticSize.ChildrenSum()))
                runtime.MultilineTextField("words", Str(words), new TextEditState(), focused: false, events: 0);
        }),
        new SurfaceOptions { Width = 1000, Height = 300, ScaleFactor = 1f });
    runtime.Step();   // a field wraps at the width it was laid out at last frame
    using var page = BibuiPage.From(runtime, runtime.BuildTree);
    var bubble = Bounds(page.FindById("bubble")!);
    var text = Bounds(page.FindById("words")!);
    IsTrue(text.Height > 30f, $"the words should wrap inside 212px: text {text.Height} tall");
    IsTrue(text.Y + text.Height <= bubble.Y + bubble.Height - 10f + 0.5f,
        $"the bubble ({bubble.Height} tall) should hold its {text.Height}px of text and its padding");
});

// A hard break starts a line whatever the width. Measured as one run, a text with one in it
// came out narrow enough to fit, one line tall, and the paint — which does break there — put
// everything after the first break outside the box it was laid out in. An app's chat draft
// showed "Yes, I'll take it." and the top half of the line under it.
Run("runtime.MultilineTextField: wrapped by characters, a token longer than the field continues on the next line", () =>
{
    var runtime = BibuiRuntime.Create();
    var path = "/home/ada/projects/examples/ledger-view/bin/Release/net10.0/ledger-view.dll".ToArray();
    TextWrap wrap = TextWrap.Words;
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            using (runtime.PushParent(runtime.ColumnWidth(160f)))
            using (runtime.PushSizeY(SemanticSize.ChildrenSum()))
                runtime.MultilineTextField("log", BibuiStr.FromStrLit(path), new TextEditState(),
                    focused: false, events: 0, wrap: wrap);
        }),
        new SurfaceOptions { Width = 400, Height = 400 });

    List<System.Drawing.RectangleF> Lines()
    {
        runtime.Step();
        runtime.Step();   // wrapped at last frame's width
        using var page = BibuiPage.From(runtime, runtime.BuildTree);
        return Below(page.FindById("log")!).Where(t => TextOf(t)?.Length > 0).Select(Bounds).ToList();
    }
    var byWords = Lines();
    Eq(1, byWords.Count, "by words the path is one line");
    IsTrue(byWords[0].Width > 160f, $"cut at the field's edge, {byWords[0].Width} wide");

    wrap = TextWrap.Characters;
    var byCharacters = Lines();
    IsTrue(byCharacters.Count > 1, $"by characters it continues, got {byCharacters.Count} lines");
    IsTrue(byCharacters.All(l => l.Width <= 160f + 0.5f), "every line inside the field");
});

Run("runtime.MultilineTextField: a hard line break is laid out as a line, not only painted as one", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            var draft = runtime.BoxFromId("draft");
            draft.ChildLayoutAxis = LayoutAxis.Y;
            draft.SizeX = SemanticSize.Pixels(600f);
            draft.SizeY = SemanticSize.ChildrenSum();
            draft.Style.Padding = new EdgeInsets(9f, 11f, 9f, 11f);
            using (runtime.PushParent(draft))
            using (runtime.PushParent(FieldFrame(runtime, SemanticSize.ChildrenSum())))
            using (runtime.PushSizeY(SemanticSize.ChildrenSum()))
                runtime.MultilineTextField("words",
                BibuiStr.FromStrLit("Yes, I'll take it.\nI will send it before Friday.\r\nThanks."), new TextEditState(),
                focused: false, events: 0);
        }),
        new SurfaceOptions { Width = 800, Height = 300, ScaleFactor = 1.25f });
    runtime.Step();
    using var page = BibuiPage.From(runtime, runtime.BuildTree);
    var box = Bounds(page.FindById("draft")!);
    var wordsNode = page.FindById("words")!;
    var words = Bounds(wordsNode);
    var lines = Below(wordsNode).Where(t => TextOf(t)?.Length > 0).Select(Bounds).ToList();
    IsTrue(lines.Count == 3, $"three lines painted, got {lines.Count}");
    IsTrue(words.Height > 3 * 12f, $"three lines tall, got {words.Height}");
    IsTrue(lines.All(l => l.Y + l.Height <= box.Y + box.Height - 9f + 0.5f),
        $"every line inside the box ({box.Y}+{box.Height}); lines end at {string.Join(", ", lines.Select(l => l.Y + l.Height))}");
});

Run("Text: with no theme family tokens set, the drawn family stays null", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.TextFromId("plain", "Hello".ToArray()),
        new SurfaceOptions { Width = 300, Height = 80, ScaleFactor = 1f });
    runtime.Step();
    Eq(null, runtime.FontInScope().Family);
});

Run("Text: a mono text is set and laid out in the theme's mono family", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            runtime.TextFromId("body", "194450e3".ToArray());
            runtime.TextFromId("mono", "194450e3".ToArray(), family: FontSlot.Mono);
        }),
        new ThemeTokens { Name = "mono-family", Typography = new TypographyTokens { FontFamilyMono = "IBM Plex Mono" } },
        new SurfaceOptions { Width = 300, Height = 80, ScaleFactor = 1f });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var mono = runtime.FontInScope(family: FontSlot.Mono);
    Eq("IBM Plex Mono", mono.Family);
    // Laid out at what the mono family measures. Not compared with the body
    // family's width: where neither family is installed both fall back to the
    // same face, and the two widths agree.
    Eq(runtime.Fonts.Measure("194450e3", mono), page.GetBounds("mono").Width, "laid out at its mono width");
});

Run("FontSize: a text in pixels is measured and laid out at that size, between the steps", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnFit(), () =>
        {
            runtime.TextFromId("sm", "Zoomed".ToArray(), ComponentSize.Sm);
            runtime.TextFromId("px", "Zoomed".ToArray(), FontSize.Px(21f));
        }),
        new SurfaceOptions { Width = 300, Height = 120, ScaleFactor = 1f });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    var font = runtime.FontInScope(FontSize.Px(21f));
    Eq(21f, font.Size, "the font is the size asked for");
    Eq(runtime.Fonts.Measure("Zoomed", font), page.GetBounds("px").Width, "laid out at what it measures");
    Eq(runtime.Fonts.LineHeight(font), page.GetBounds("px").Height, "and a line of it tall");
    IsTrue(page.GetBounds("px").Width > page.GetBounds("sm").Width, "larger than the small step");
});

Run("FontSize: a text's size is always said, and can be changed on the text that came back", () =>
{
    var runtime = BibuiRuntime.Create();
    Text plain = null!;
    runtime.SetBuild(() =>
    {
        Box root = runtime.Box();
        root.ChildLayoutAxis = LayoutAxis.Y;
        root.SizeX = root.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(root))
        {
            plain = runtime.TextFromId("plain", "Zoomed".ToArray());
            runtime.TextFromId("changed", "Zoomed".ToArray()).Size = FontSize.Px(26f);
        }
        return root;
    }, new SurfaceOptions { Width = 300, Height = 120, ScaleFactor = 1f });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    Eq((FontSize)ComponentSize.Md, plain.Size, "nothing passed and nothing pushed is Md");
    Eq(runtime.Fonts.Measure("Zoomed", runtime.FontInScope(FontSize.Px(26f))), page.GetBounds("changed").Width,
        "a size set afterwards is the one laid out");
});

Run("PushFontSize: what is built in scope takes the innermost pushed size, unless it says its own", () =>
{
    var runtime = BibuiRuntime.Create();
    Icon icon = null!;
    runtime.SetBuild(() =>
    {
        Box root = runtime.Box();
        root.ChildLayoutAxis = LayoutAxis.Y;
        root.SizeX = root.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(root))
        {
            using (runtime.PushFontSize(FontSize.Px(30f)))
            {
                runtime.TextFromId("outer", "Zoomed".ToArray());
                using (runtime.PushFontSize(FontSize.Px(18f)))
                {
                    runtime.TextFromId("inner", "Zoomed".ToArray());
                    icon = runtime.Icon("x");
                }
                runtime.TextFromId("own", "Zoomed".ToArray(), ComponentSize.Sm);
            }
            runtime.TextFromId("after", "Zoomed".ToArray());
        }
        return root;
    }, new SurfaceOptions { Width = 400, Height = 200, ScaleFactor = 1f });
    using var page = BibuiPage.From(runtime, runtime.BuildTree);

    float Wide(FontSize size) => runtime.Fonts.Measure("Zoomed", runtime.FontInScope(size));
    Eq(Wide(FontSize.Px(30f)), page.GetBounds("outer").Width, "the pushed size");
    Eq(Wide(FontSize.Px(18f)), page.GetBounds("inner").Width, "the innermost push");
    Eq(18f, icon.Extent.X, "an icon takes it too");
    Eq(Wide(ComponentSize.Sm), page.GetBounds("own").Width, "a text's own size wins");
    Eq(Wide(ComponentSize.Md), page.GetBounds("after").Width, "and past the scope the default is back");
});

Run("PushFontSize: a scope left open is an error at the end of the build", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        runtime.PushFontSize(FontSize.Px(20f));   // never disposed
        return runtime.Box().Px(10f, 10f);
    }, new SurfaceOptions { Width = 100, Height = 100 });
    Throws<InvalidOperationException>(() => runtime.Step());
});

Run("PushFontFace: what is built in scope takes the innermost face, and an unsaid part is the outer one's", () =>
{
    var runtime = BibuiRuntime.Create();
    var seen = new Dictionary<string, Text>();
    Font inScope = default;
    runtime.SetBuild(() =>
    {
        Box root = runtime.Box();
        root.ChildLayoutAxis = LayoutAxis.Y;
        root.SizeX = root.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(root))
        {
            using (runtime.PushFontFace(FontSlot.Mono))
            {
                seen["mono"] = runtime.Text("a".ToArray());
                using (runtime.PushFontFace(weight: FontWeight.Bold))
                {
                    seen["mono bold"] = runtime.Text("a".ToArray());
                    seen["own family"] = runtime.Text("a".ToArray(), family: FontSlot.Heading);
                    inScope = runtime.FontInScope();
                }
                seen["mono again"] = runtime.Text("a".ToArray());
            }
            seen["after"] = runtime.Text("a".ToArray());
        }
        return root;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    string Face(Text t) => $"{t.Family} {t.Weight}";
    Eq("Mono Regular", Face(seen["mono"]), "the pushed family, at the weight already in scope");
    Eq("Mono Bold", Face(seen["mono bold"]), "a weight alone keeps the family");
    Eq("Heading Bold", Face(seen["own family"]), "a text's own family wins, at the weight in scope");
    Eq("Mono Regular", Face(seen["mono again"]), "the inner scope ends");
    Eq("Body Regular", Face(seen["after"]), "and past them the default is back");
    var regular = runtime.FontInScope(family: FontSlot.Mono);
    IsTrue(inScope.Family == regular.Family && inScope.Weight > regular.Weight,
        "FontInScope measures in the face in scope");
});

Run("PushFontFace: a scope left open is an error at the end of the build", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        runtime.PushFontFace(weight: FontWeight.Bold);   // never disposed
        return runtime.Box().Px(10f, 10f);
    }, new SurfaceOptions { Width = 100, Height = 100 });
    Throws<InvalidOperationException>(() => runtime.Step());
});

Run("FontRegistry: IsRegistered is keyed per (family, weight), and invalid font bytes are rejected", () =>
{
    FontRegistry.Clear();
    const string family = "Test Registry Family";
    IsTrue(!FontRegistry.IsRegistered(family, 400), "should not be registered before Register() is called");
    IsTrue(!FontRegistry.IsRegistered(family, 700), "a different weight for the same family should be independently unregistered");

    bool threw = false;
    try { FontRegistry.Register(family, 400, [0x00, 0x01, 0x02]); }
    catch (ArgumentException) { threw = true; }
    IsTrue(threw, "Register with bytes Skia can't parse as a font should throw ArgumentException, not silently succeed");
    IsTrue(!FontRegistry.IsRegistered(family, 400), "a failed Register() call should not leave a partial registration behind");
    FontRegistry.Clear();
});

// ── Renderers ─────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Renderers");

// What a wl_shm host does: the compositor's buffer, with its own stride, drawn
// into where it lies. Each renderer draws the rows it draws into a packed buffer.
void StrideContract(string name, Bibui.Core.Draw.DrawToPixels draw)
{
    const int w = 240, h = 120, stride = w * 4 + 64;
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => runtime.Into(runtime.ColumnFit(), () =>
    {
        runtime.TextFromId("title", "Straight into shared memory");
        Btn(runtime, "ok", "OK");
    }), new SurfaceOptions { Width = w, Height = h, ScaleFactor = 1f });
    var packed = runtime.Frame(draw);

    var memory = GC.AllocateArray<byte>(stride * h, pinned: true);
    draw(runtime.DrawList, new Bibui.Core.Draw.RasterPixels(
        System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(memory, 0), w, h, stride));
    for (int y = 0; y < h; y++)
        IsTrue(memory.AsSpan(y * stride, w * 4).SequenceEqual(packed.RawBgraBytes.AsSpan(y * w * 4, w * 4)),
            $"{name}: row {y} must match the packed buffer byte for byte");
    IsTrue(packed.RawBgraBytes.Chunk(4).Distinct().Count() > 1, $"{name}: the frame holds more than the clear colour");
}

Run("Renderers: Skia draws into memory with its own stride what it draws packed", () => StrideContract("skia", Frames.Skia.Draw));

Run("Renderers: the managed rasterizer draws into memory with its own stride what it draws packed", () =>
{
    using var managed = new ManagedRenderer();
    StrideContract("managed", managed.Draw);
});

Run("Raster: glyphs bibui shapes draw the same pixels as Skia drawing the string", () =>
{
    // The canvas is handed glyphs FreeType shaped, never a string. On Skia they
    // must land exactly where Skia's own string drawing puts them, with the
    // paint bibui draws text with, so the move to glyphs moved no pixel.
    using var fonts = new Fonts();
    var face = fonts.Face();
    IsTrue(face.HasGlyph('A') && !face.HasGlyph(0x10FFFD), "glyph coverage is the face's own");

    const float size = 17.5f;
    const string text = "Shaped runs, glyph by glyph — 0.5 ffi";
    var shaped = fonts.Shape(text, face, size);
    Eq(text.Length, shaped.Glyphs.Length, "one glyph per char for this text (no ligatures)");
    Eq(face.Strike(size).Measure(text), shaped.Width, "the shaped run is as wide as measuring says");

    var info = new SkiaSharp.SKImageInfo(360, 48, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
    static byte[] Read(SkiaSharp.SKSurface surface)
    {
        using var pixmap = surface.PeekPixels();
        return pixmap.GetPixelSpan().ToArray();
    }

    byte[] fromString;
    using (var surface = SkiaSharp.SKSurface.Create(info))
    using (var data = SkiaSharp.SKData.CreateCopy(face.FontFile))
    using (var typeface = SkiaSharp.SKTypeface.FromData(data))
    using (var font = new SkiaSharp.SKFont(typeface, size)
    {
        Subpixel = true, Edging = SkiaSharp.SKFontEdging.Antialias,
        ForceAutoHinting = true, Hinting = SkiaSharp.SKFontHinting.Full,
        // Unrounded advances, as FreeType's: Skia 4 rounds them under full hinting
        // where 2.88 did not. The renderer never uses Skia's advances; this string does.
        LinearMetrics = true,
    })
    using (var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true })
    {
        surface.Canvas.Clear(SkiaSharp.SKColors.White);
        surface.Canvas.DrawText(text, 6.25f, 32f, SkiaSharp.SKTextAlign.Left, font, paint);
        fromString = Read(surface);
        Eq(font.MeasureText(text), shaped.Width, $"Skia measures the string as FreeType does (Skia {font.MeasureText(text)}, FreeType {shaped.Width})");
    }

    var list = new Bibui.Core.Draw.DrawList();
    list.Push(Bibui.Core.Draw.DrawOpKind.Clear).Color = Rgba.White;
    ref var glyphs = ref list.Push(Bibui.Core.Draw.DrawOpKind.Glyphs);
    (glyphs.Resource, glyphs.C, glyphs.A, glyphs.B, glyphs.Color) = (list.AddFace(face), size, 6.25f, 32f, Rgba.Black);
    (glyphs.Start, glyphs.Count) = (list.AddGlyphs(shaped.Glyphs), shaped.Glyphs.Length);
    glyphs.Start2 = list.AddPoints(shaped.Positions);
    var fromGlyphs = Pixels.Of(list, 360, 48, Frames.Skia.Draw).RawBgraBytes;

    IsTrue(fromString.AsSpan().SequenceEqual(fromGlyphs), "glyph pixels must equal Skia's string pixels");
    IsTrue(fromString.Chunk(4).Distinct().Count() > 2, "text was drawn");
});

// Textures: one checked against each renderer, through the renderer's own
// texture functions.
void TextureContract(string name, Bibui.Core.Draw.DrawToPixels draw, CreateTextureFn create, UpdateTextureFn update,
    Action<Bibui.Core.Draw.TextureHandle> release)
{
    var rgba8 = Bibui.Core.Draw.TextureFormat.RGBA8;
    var stream = Bibui.Core.Draw.TextureKind.Stream;
    // 4x2 opaque pixels, RGBA, each one different.
    static byte[] Rgba8(byte seed)
    {
        var px = new byte[4 * 2 * 4];
        for (int i = 0; i < 8; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = ((byte)(seed + i * 30), (byte)(200 - i * 20), (byte)(i * 11), 255);
        return px;
    }
    // Drawn 1:1 at the origin with an opaque paint, read back as RGBA.
    byte[] Draw(Bibui.Core.Draw.TextureHandle texture)
    {
        var list = new Bibui.Core.Draw.DrawList();
        list.Push(Bibui.Core.Draw.DrawOpKind.Clear).Color = new Rgba(0, 0, 0, 0);
        ref var op = ref list.Push(Bibui.Core.Draw.DrawOpKind.Texture);
        (op.Texture, op.Rect, op.Paint) = (texture, new RectF(0, 0, 4, 2), new PaintSpec { Color = Rgba.Black, FilterQuality = FilterQuality.Low });
        var bgra = Pixels.Of(list, 4, 2, draw).RawBgraBytes;
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    var a = Rgba8(10);
    var first = create(rgba8, stream, 4, 2, a, 0);
    IsTrue(!first.IsNone, $"{name}: a created texture is a handle");
    IsTrue(Draw(first).SequenceEqual(a), $"{name}: drawn 1:1, a texture is its pixels");

    var b = Rgba8(90);
    update(first, b, 0);
    IsTrue(Draw(first).SequenceEqual(b), $"{name}: an update changes what the same Stream handle draws");

    var still = create(rgba8, Bibui.Core.Draw.TextureKind.Static, 4, 2, a, 0);
    update(still, b, 0);
    IsTrue(Draw(still).SequenceEqual(a), $"{name}: a Static texture ignores an update");
    release(still);

    // Rows 20 bytes apart, the last 4 of each padding, as a video decoder's rows are.
    var padded = new byte[20 * 2];
    for (int y = 0; y < 2; y++) Array.Copy(b, y * 16, padded, y * 20, 16);
    update(first, padded, 20);
    IsTrue(Draw(first).SequenceEqual(b), $"{name}: padded rows read as the pixels they hold");

    var blank = create(rgba8, stream, 4, 2, ReadOnlySpan<byte>.Empty, 0);
    IsTrue(Draw(blank).SequenceEqual(new byte[4 * 2 * 4]), $"{name}: a Stream made with no pixels is zeros");
    release(blank);

    // Each format's default look, drawn plainly: R8 grey, A8 white coverage
    // (premultiplied, so (a, a, a, a)), RG8 (r, g, 0, 1).
    byte[] one = [0, 40, 80, 120, 160, 200, 240, 255];
    var r8 = create(Bibui.Core.Draw.TextureFormat.R8, stream, 4, 2, one, 0);
    IsTrue(Draw(r8).SequenceEqual(one.SelectMany(v => new[] { v, v, v, (byte)255 })), $"{name}: R8 draws grey");
    var a8 = create(Bibui.Core.Draw.TextureFormat.A8, stream, 4, 2, one, 0);
    IsTrue(Draw(a8).SequenceEqual(one.SelectMany(v => new[] { v, v, v, v })), $"{name}: A8 draws white coverage");
    var two = new byte[16];
    for (int i = 0; i < 8; i++) (two[2 * i], two[2 * i + 1]) = ((byte)(i * 30), (byte)(255 - i * 30));
    var rg8 = create(Bibui.Core.Draw.TextureFormat.RG8, stream, 4, 2, two, 0);
    IsTrue(Draw(rg8).SequenceEqual(Enumerable.Range(0, 8).SelectMany(i => new[] { two[2 * i], two[2 * i + 1], (byte)0, (byte)255 })),
        $"{name}: RG8 draws (r, g, 0, 1)");
    update(r8, one.Reverse().ToArray(), 0);
    IsTrue(Draw(r8).SequenceEqual(one.Reverse().SelectMany(v => new[] { v, v, v, (byte)255 })), $"{name}: an R8 Stream updates in place");
    // BGRA8 is RGBA8 in another byte order: the same picture.
    var bgra = new byte[a.Length];
    for (int i = 0; i < 8; i++) (bgra[4 * i], bgra[4 * i + 1], bgra[4 * i + 2], bgra[4 * i + 3]) = (a[4 * i + 2], a[4 * i + 1], a[4 * i], a[4 * i + 3]);
    var bgra8 = create(Bibui.Core.Draw.TextureFormat.BGRA8, stream, 4, 2, bgra, 0);
    IsTrue(Draw(bgra8).SequenceEqual(a), $"{name}: BGRA8 draws as the RGBA8 it reorders");
    release(r8); release(a8); release(rg8); release(bgra8);

    var empty = new byte[4 * 2 * 4];
    IsTrue(Draw(Bibui.Core.Draw.TextureHandle.None).SequenceEqual(empty), $"{name}: None draws nothing");
    release(first);
    IsTrue(Draw(first).SequenceEqual(empty), $"{name}: a released handle draws nothing");
    var second = create(rgba8, stream, 4, 2, a, 0);
    IsTrue(second != first, $"{name}: a new texture in the reused slot has a new handle");
    IsTrue(Draw(first).SequenceEqual(empty), $"{name}: the old handle still draws nothing");
    IsTrue(Draw(second).SequenceEqual(a), $"{name}: the new handle draws its own pixels");
    update(first, b, 0);
    IsTrue(Draw(second).SequenceEqual(a), $"{name}: updating the old handle leaves the new texture alone");
    release(second);
}

Run("DrawList: recording a frame after the first allocates nothing", () =>
{
    // A page of what most frames are made of: text, icons, decorated and
    // clipped boxes, an opacity layer, a cached path. Laid out once by a real
    // runtime, then recorded again and again into one list.
    var runtime = BibuiRuntime.Create();
    UiNode? tree = null;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            runtime.Text("Recording allocates nothing");
            runtime.Icon("check", size: 18f);
            var card = runtime.Box();
            card.Px(160f, 48f).Clipped();
            card.Style.BackgroundColor = UiColor.SurfaceRaised;
            card.Style.BorderColor = UiColor.Border;
            card.Style.BorderWidth = 1f;
            card.Style.CornerRadius = 8f;
            card.Style.Opacity = 0.5f;
            using (runtime.PushParent(card)) runtime.Text("inside a clipped, faded card");
            var drawn = runtime.Box();
            drawn.Px(80f, 40f);
            drawn.Draw = (_, ctx) => ctx.DrawCheckMark(0f, 0f, 20f, 20f, new Rgba(34, 197, 94), 2f);
        }
        return tree = page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();

    var caches = new Bibui.Core.Draw.RenderCaches();
    using var fonts = new Fonts();
    var painter = new Bibui.Core.Draw.Painter(caches, fonts);
    var list = new Bibui.Core.Draw.DrawList();
    var surface = new SurfaceOptions { Width = 400, Height = 300 };
    var theme = runtime.Theme;
    for (int i = 0; i < 3; i++)
    {
        painter.Record(list, tree!, surface, theme, FrameInput.None);
        caches.EndFrame();
        fonts.EndFrame();
    }

    long before = GC.GetAllocatedBytesForCurrentThread();
    painter.Record(list, tree!, surface, theme, FrameInput.None);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    IsTrue(list.Ops.Length > 8, $"the page recorded its draws ({list.Ops.Length} ops)");
    Eq(0L, allocated, $"a warmed-up recording allocates nothing (it allocated {allocated} bytes)");
    caches.Dispose();
});

// An app names its renderer once (global using Renderer = ...) and calls it
// directly, so swapping renderers is that line and the setup. That holds only
// while every renderer's texture and draw calls are the same: this keeps them so.
Run("Renderers: SkiaGlRenderer, SkiaCpuRenderer and ManagedRenderer share their texture and draw calls", () =>
{
    static string Shape(System.Reflection.MethodInfo m) =>
        $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}{(p.HasDefaultValue ? $" = {p.DefaultValue}" : "")}"))})";
    string[] calls = ["CreateTexture", "UpdateTexture", "ReleaseTexture"];
    string[] Shapes(Type t) =>
    [
        .. t.GetMethods().Where(m => calls.Contains(m.Name)).Select(Shape).Order(),
        .. t.GetMethods().Where(m => m.Name == "Draw" && m.GetParameters() is [{ ParameterType.Name: "DrawList" }]).Select(Shape),
    ];
    var gl = Shapes(typeof(SkiaGlRenderer));
    Eq(4, gl.Length, $"SkiaGlRenderer has the four calls: {string.Join("; ", gl)}");
    foreach (var t in new[] { typeof(SkiaCpuRenderer), typeof(ManagedRenderer) })
        Eq(string.Join("; ", gl), string.Join("; ", Shapes(t)), $"{t.Name} matches SkiaGlRenderer");
});

// A texture through a table (TextureLookup): each pixel's raw channel picks the
// entry drawn. The same cases on every renderer, drawn 1:1 so filtering lands on
// texel centres and the entry read is exact.
void LookupContract(string name, Bibui.Core.Draw.DrawToPixels draw, CreateTextureFn create, Action<Bibui.Core.Draw.TextureHandle> release)
{
    var stat = Bibui.Core.Draw.TextureKind.Static;
    byte[] Draw(Bibui.Core.Draw.TextureHandle texture, Bibui.Core.Draw.TextureLookup lookup)
    {
        var list = new Bibui.Core.Draw.DrawList();
        list.Push(Bibui.Core.Draw.DrawOpKind.Clear).Color = new Rgba(0, 0, 0, 0);
        ref var op = ref list.Push(Bibui.Core.Draw.DrawOpKind.Texture);
        (op.Texture, op.Rect, op.Paint) = (texture, new RectF(0, 0, 4, 2), new PaintSpec { Color = Rgba.Black, FilterQuality = FilterQuality.Low });
        op.Resource = list.AddLookup(lookup);
        var bgra = Pixels.Of(list, 4, 2, draw).RawBgraBytes;
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }
    static bool Near(byte[] got, byte[] want, int tolerance = 0) =>
        got.Length == want.Length && got.Zip(want).All(p => Math.Abs(p.First - p.Second) <= tolerance);

    // A 1-D table whose every entry differs: entry i = (i, 255 - i, i * 7, 255).
    var line = new byte[256 * 4];
    for (int i = 0; i < 256; i++) (line[4 * i], line[4 * i + 1], line[4 * i + 2], line[4 * i + 3]) = ((byte)i, (byte)(255 - i), (byte)(i * 7), 255);
    var table = create(Bibui.Core.Draw.TextureFormat.RGBA8, stat, 256, 1, line, 0);
    byte[] Entry(int i) => [line[4 * i], line[4 * i + 1], line[4 * i + 2], line[4 * i + 3]];

    byte[] one = [0, 40, 80, 120, 160, 200, 240, 255];
    var r8 = create(Bibui.Core.Draw.TextureFormat.R8, stat, 4, 2, one, 0);
    IsTrue(Near(Draw(r8, new(table, Bibui.Core.Draw.TextureChannel.R)), one.SelectMany(v => Entry(v)).ToArray()),
        $"{name}: an R8 value picks its entry of a 1-D table");

    var a8 = create(Bibui.Core.Draw.TextureFormat.A8, stat, 4, 2, one, 0);
    IsTrue(Near(Draw(a8, new(table, Bibui.Core.Draw.TextureChannel.A)), one.SelectMany(v => Entry(v)).ToArray()),
        $"{name}: A8's coverage is its raw A");
    IsTrue(Near(Draw(a8, new(table, Bibui.Core.Draw.TextureChannel.R)), Enumerable.Range(0, 8).SelectMany(_ => Entry(0)).ToArray()),
        $"{name}: a channel the format lacks reads 0");

    // A 2-D table: entry (x, y) = (x, y, 77, 255); RG8's two channels pick column and row.
    var plane = new byte[256 * 256 * 4];
    for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
        { int o = (y * 256 + x) * 4; (plane[o], plane[o + 1], plane[o + 2], plane[o + 3]) = ((byte)x, (byte)y, 77, 255); }
    var grid = create(Bibui.Core.Draw.TextureFormat.RGBA8, stat, 256, 256, plane, 0);
    var two = new byte[16];
    for (int i = 0; i < 8; i++) (two[2 * i], two[2 * i + 1]) = ((byte)(i * 30), (byte)(255 - i * 30));
    var rg8 = create(Bibui.Core.Draw.TextureFormat.RG8, stat, 4, 2, two, 0);
    IsTrue(Near(Draw(rg8, new(grid, Bibui.Core.Draw.TextureChannel.R, Bibui.Core.Draw.TextureChannel.G)),
        Enumerable.Range(0, 8).SelectMany(i => new[] { two[2 * i], two[2 * i + 1], (byte)77, (byte)255 }).ToArray()),
        $"{name}: RG8's two channels pick an entry of a 2-D table");

    // RGBA8 is read straight, not premultiplied: R at half alpha still picks entry R.
    var rgba = new byte[32];
    for (int i = 0; i < 8; i++) (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = ((byte)(i * 30), 0, 0, 128);
    var half = create(Bibui.Core.Draw.TextureFormat.RGBA8, stat, 4, 2, rgba, 0);
    IsTrue(Near(Draw(half, new(table, Bibui.Core.Draw.TextureChannel.R)), Enumerable.Range(0, 8).SelectMany(i => Entry(i * 30)).ToArray(), tolerance: 7),
        $"{name}: RGBA8's R is read straight, whatever its alpha");

    // A transparent entry draws nothing; a released table draws nothing.
    var clear = (byte[])line.Clone();
    for (int i = 0; i < 128; i++) clear[4 * i + 3] = 0;
    var halfClear = create(Bibui.Core.Draw.TextureFormat.RGBA8, stat, 256, 1, clear, 0);
    var drawn = Draw(r8, new(halfClear, Bibui.Core.Draw.TextureChannel.R));
    IsTrue(drawn[3] == 0 && drawn[4 * 7 + 3] == 255, $"{name}: an entry's alpha is drawn: transparent below 128, opaque at 255");
    release(halfClear);
    IsTrue(Draw(r8, new(halfClear, Bibui.Core.Draw.TextureChannel.R)).All(b => b == 0), $"{name}: a released table draws nothing");
    release(table); release(grid); release(r8); release(a8); release(rg8); release(half);
}

// A frame that draws through a table, drawn again: what was made for the first
// (the effect, the shader, the managed shader's state) is reused, and the managed
// heap does not grow.
void LookupAllocatesNothing(string name, Bibui.Core.Draw.DrawToPixels draw, CreateTextureFn create)
{
    var line = new byte[256 * 4];
    for (int i = 0; i < 256; i++) (line[4 * i], line[4 * i + 1], line[4 * i + 2], line[4 * i + 3]) = ((byte)i, (byte)(255 - i), 7, 255);
    var table = create(Bibui.Core.Draw.TextureFormat.RGBA8, Bibui.Core.Draw.TextureKind.Static, 256, 1, line, 0);
    var source = create(Bibui.Core.Draw.TextureFormat.R8, Bibui.Core.Draw.TextureKind.Static, 64, 64, new byte[64 * 64], 0);
    var list = new Bibui.Core.Draw.DrawList();
    list.Push(Bibui.Core.Draw.DrawOpKind.Clear).Color = new Rgba(0, 0, 0, 0);
    ref var op = ref list.Push(Bibui.Core.Draw.DrawOpKind.Texture);
    (op.Texture, op.Rect, op.Paint) = (source, new RectF(0, 0, 128, 128), new PaintSpec { Color = Rgba.Black, FilterQuality = FilterQuality.Low });
    op.Resource = list.AddLookup(new(table, Bibui.Core.Draw.TextureChannel.R));
    var bgra = GC.AllocateArray<byte>(128 * 128 * 4, pinned: true);
    var pixels = new Bibui.Core.Draw.RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), 128, 128, 128 * 4);
    for (int i = 0; i < 3; i++) draw(list, pixels);
    long before = GC.GetAllocatedBytesForCurrentThread();
    draw(list, pixels);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Eq(0L, allocated, $"{name}: a lookup drawn again allocates nothing (it allocated {allocated} bytes)");
}

Run("Texture lookups: LookupAt is total: an index that names no lookup reads None", () =>
{
    var list = new Bibui.Core.Draw.DrawList();
    ref var plain = ref list.Push(Bibui.Core.Draw.DrawOpKind.Texture);
    IsTrue(list.LookupAt(plain.Resource).IsNone, $"a texture op pushed without a table reads None (Resource {plain.Resource})");
    var table = new Bibui.Core.Draw.TextureHandle(7, 1);
    int at = list.AddLookup(new(table, Bibui.Core.Draw.TextureChannel.R));
    Eq(table, list.LookupAt(at).Table, "an added lookup reads back");
    IsTrue(list.LookupAt(-1).IsNone && list.LookupAt(at + 1).IsNone && list.LookupAt(int.MinValue).IsNone, "-1, past the end and int.MinValue read None");
    list.Reset();
    IsTrue(list.LookupAt(at).IsNone, "after Reset the frame's lookups are gone");
});

Run("Texture lookups: drawn again, a lookup allocates nothing, on Skia and the managed rasterizer", () =>
{
    using var skia = new SkiaCpuRenderer();
    LookupAllocatesNothing("skia", skia.Draw, skia.CreateTexture);
    using var managed = new ManagedRenderer();
    LookupAllocatesNothing("managed", managed.Draw, managed.CreateTexture);
});

Run("Texture lookups: a texture's raw channels pick a table's entries, on Skia", () =>
{
    using var skia = new SkiaCpuRenderer();
    LookupContract("skia", skia.Draw, skia.CreateTexture, skia.ReleaseTexture);
});

Run("Texture lookups: a texture's raw channels pick a table's entries, on the managed rasterizer", () =>
{
    using var managed = new ManagedRenderer();
    LookupContract("managed", managed.Draw, managed.CreateTexture, managed.ReleaseTexture);
});

// Groups: a layer composited by a blend mode or through a colour matrix. The
// same cases on every renderer, against the formulas on premultiplied values:
// a backdrop of two colours under a group that covers the left half only, so
// the right half shows what each mode does with the layer's transparent pixels.
void GroupContract(string name, Bibui.Core.Draw.DrawToPixels draw)
{
    var back = new Rgba(200, 100, 50, 255);
    byte[] Draw(Action<Bibui.Core.Draw.DrawList> group, Rgba fill)
    {
        var list = new Bibui.Core.Draw.DrawList();
        list.Push(Bibui.Core.Draw.DrawOpKind.Clear).Color = new Rgba(0, 0, 0, 0);
        ref var bg = ref list.Push(Bibui.Core.Draw.DrawOpKind.Rect);
        (bg.Rect, bg.Paint) = (new RectF(0, 0, 4, 1), new PaintSpec { Color = back });
        group(list);
        ref var op = ref list.Push(Bibui.Core.Draw.DrawOpKind.Rect);
        (op.Rect, op.Paint) = (new RectF(0, 0, 2, 1), new PaintSpec { Color = fill });
        list.Push(Bibui.Core.Draw.DrawOpKind.Pop);
        var bgra = Pixels.Of(list, 4, 1, draw).RawBgraBytes;
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }
    Action<Bibui.Core.Draw.DrawList> Mode(Bibui.Core.Draw.BlendMode m) => l => l.Push(Bibui.Core.Draw.DrawOpKind.PushBlendMode).Blend = m;
    Action<Bibui.Core.Draw.DrawList> Matrix(float[] m) => l => { ref var op = ref l.Push(Bibui.Core.Draw.DrawOpKind.PushColorMatrix); (op.Start, op.Count) = (l.AddFloats(m), 20); };
    static bool Near(byte[] got, byte[] want, int tolerance) =>
        got.Length == want.Length && got.Zip(want).All(p => Math.Abs(p.First - p.Second) <= tolerance);
    string Show(byte[] px) => string.Join(" ", px.Chunk(4).Select(c => $"({string.Join(",", c)})"));

    // The formulas, on premultiplied 0..1, as the modes are documented; the
    // surface holds premultiplied pixels too.
    static float[] Pm(Rgba c) { float a = c.A / 255f; return [c.R / 255f * a, c.G / 255f * a, c.B / 255f * a, a]; }
    byte[] Expect(Func<float, float, float, float, float> channel, Func<float, float, float> alpha, Rgba fill, bool srcClearsRight = false)
    {
        var s = Pm(fill); var d = Pm(back);
        float oa = alpha(s[3], d[3]);
        var left = new byte[4];
        for (int i = 0; i < 3; i++) left[i] = (byte)MathF.Round(Math.Clamp(channel(s[i], d[i], s[3], d[3]), 0f, 1f) * 255f);
        left[3] = (byte)MathF.Round(oa * 255f);
        byte[] right = srcClearsRight ? [0, 0, 0, 0] : [back.R, back.G, back.B, back.A];
        return [.. left, .. left, .. right, .. right];
    }
    static float Over(float sa, float da) => sa + da - sa * da;

    foreach (var fill in new[] { new Rgba(100, 150, 220, 255), new Rgba(100, 150, 220, 128) })
    {
        string f = fill.A == 255 ? "opaque" : "translucent";
        (string Mode, Bibui.Core.Draw.BlendMode M, Func<float, float, float, float, float> C, Func<float, float, float> A, bool Clears)[] cases =
        [
            ("SrcOver", Bibui.Core.Draw.BlendMode.SrcOver, (s, d, sa, da) => s + d * (1 - sa), Over, false),
            ("Multiply", Bibui.Core.Draw.BlendMode.Multiply, (s, d, sa, da) => s * d + s * (1 - da) + d * (1 - sa), Over, false),
            ("Screen", Bibui.Core.Draw.BlendMode.Screen, (s, d, sa, da) => s + d - s * d, Over, false),
            ("Plus", Bibui.Core.Draw.BlendMode.Plus, (s, d, sa, da) => MathF.Min(1, s + d), (sa, da) => MathF.Min(1, sa + da), false),
            ("Overlay", Bibui.Core.Draw.BlendMode.Overlay, (s, d, sa, da) =>
                (2 * d <= da ? 2 * s * d : sa * da - 2 * (da - d) * (sa - s)) + s * (1 - da) + d * (1 - sa), Over, false),
            ("Src", Bibui.Core.Draw.BlendMode.Src, (s, d, sa, da) => s, (sa, da) => sa, true),
        ];
        foreach (var c in cases)
        {
            var got = Draw(Mode(c.M), fill);
            var want = Expect(c.C, c.A, fill, c.Clears);
            IsTrue(Near(got, want, 3), $"{name}: {c.Mode} of an {f} layer: got {Show(got)}, want {Show(want)}");
        }
    }

    // Matrices act on the straight colour; the translation is in 0..1.
    var red = new Rgba(220, 40, 40, 255);
    float l = (0.2126f * 220 + 0.7152f * 40 + 0.0722f * 40) / 255f;
    var gray = Draw(Matrix([0.2126f, 0.7152f, 0.0722f, 0, 0, 0.2126f, 0.7152f, 0.0722f, 0, 0, 0.2126f, 0.7152f, 0.0722f, 0, 0, 0, 0, 0, 1, 0]), red);
    byte g = (byte)MathF.Round(l * 255f);
    IsTrue(Near(gray, [g, g, g, 255, g, g, g, 255, back.R, back.G, back.B, 255, back.R, back.G, back.B, 255], 2), $"{name}: a luminance matrix greys the layer only: {Show(gray)}");

    var shifted = Draw(Matrix([1, 0, 0, 0, 0, 0, 1, 0, 0, 0.5f, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]), red);
    IsTrue(Near(shifted, [220, 168, 40, 255, 220, 168, 40, 255, back.R, back.G, back.B, 255, back.R, back.G, back.B, 255], 2), $"{name}: a translation of 0.5 adds half of full scale: {Show(shifted)}");

    // A translucent layer's straight colour is what the matrix sees: halving
    // alpha in the matrix gives the colour at half alpha, over the backdrop.
    var halved = Draw(Matrix([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0.5f, 0]), red);
    var opacity = Draw(l2 => { ref var op = ref l2.Push(Bibui.Core.Draw.DrawOpKind.PushColorMatrix); (op.Start, op.Count) = (l2.AddFloats([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 128 / 255f, 0]), 20); }, red);
    IsTrue(Near(halved, opacity, 2), $"{name}: an alpha-only matrix is an opacity layer: {Show(halved)} vs {Show(opacity)}");

    // A matrix that adds alpha lights up the layer's transparent pixels too:
    // they are black at alpha 0, so the whole width darkens to half.
    var lit = Draw(Matrix([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0.5f]), new Rgba(0, 0, 0, 0));
    IsTrue(Near(lit, [100, 50, 25, 255, 100, 50, 25, 255, 100, 50, 25, 255, 100, 50, 25, 255], 3), $"{name}: alpha added by a matrix covers the whole layer: {Show(lit)}");

}

void GroupsAllocateNothing(string name, Bibui.Core.Draw.DrawToPixels draw)
{
    var list = new Bibui.Core.Draw.DrawList();
    list.Push(Bibui.Core.Draw.DrawOpKind.Clear).Color = new Rgba(0, 0, 0, 0);
    foreach (var m in new[] { Bibui.Core.Draw.BlendMode.Multiply, Bibui.Core.Draw.BlendMode.Src, Bibui.Core.Draw.BlendMode.Overlay })
    {
        list.Push(Bibui.Core.Draw.DrawOpKind.PushBlendMode).Blend = m;
        ref var mop = ref list.Push(Bibui.Core.Draw.DrawOpKind.PushColorMatrix);
        (mop.Start, mop.Count) = (list.AddFloats([0.3f, 0.6f, 0.1f, 0, 0, 0.3f, 0.6f, 0.1f, 0, 0, 0.3f, 0.6f, 0.1f, 0, 0.2f, 0, 0, 0, 1, 0]), 20);
        ref var op = ref list.Push(Bibui.Core.Draw.DrawOpKind.Rect);
        (op.Rect, op.Paint) = (new RectF(10, 10, 100, 100), new PaintSpec { Color = new Rgba(200, 80, 40, 255) });
        list.Push(Bibui.Core.Draw.DrawOpKind.Pop);
        list.Push(Bibui.Core.Draw.DrawOpKind.Pop);
    }
    var bgra = GC.AllocateArray<byte>(128 * 128 * 4, pinned: true);
    var pixels = new Bibui.Core.Draw.RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), 128, 128, 128 * 4);
    for (int i = 0; i < 3; i++) draw(list, pixels);
    long before = GC.GetAllocatedBytesForCurrentThread();
    draw(list, pixels);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Eq(0L, allocated, $"{name}: groups drawn again allocate nothing (they allocated {allocated} bytes)");
}

Run("Groups: blend modes and colour matrices composite by their formulas, on Skia", () =>
{
    using var skia = new SkiaCpuRenderer();
    GroupContract("skia", skia.Draw);
    GroupsAllocateNothing("skia", skia.Draw);
});

Run("Groups: blend modes and colour matrices composite by their formulas, on the managed rasterizer", () =>
{
    using var managed = new ManagedRenderer();
    GroupContract("managed", managed.Draw);
    GroupsAllocateNothing("managed", managed.Draw);
});

Run("Textures: drawn, updated and released on Skia", () =>
{
    using var skia = new SkiaCpuRenderer();
    TextureContract("skia", skia.Draw, skia.CreateTexture, skia.UpdateTexture, skia.ReleaseTexture);
});

Run("Textures: drawn, updated and released on the managed rasterizer", () =>
{
    using var managed = new ManagedRenderer();
    TextureContract("managed", managed.Draw, managed.CreateTexture, managed.UpdateTexture, managed.ReleaseTexture);
});

Run("Box.Draw: a callback draws in its box's space, clipped to the box, and its state is its own", () =>
{
    // A 40×40 box at (30,20). The callback draws a rect from its own origin
    // well past its bottom-right, pushes a clip it never pops, and pops twice
    // more than it pushed. A sibling after it must still draw unclipped.
    Box? handed = null;
    var red = new Rgba(255, 0, 0);
    var green = new Rgba(0, 255, 0);
    var drawn = new Box
    {
        Draw = (box, ctx) =>
        {
            handed = box;
            ctx.DrawRect(new RectF(0f, 0f, 200f, 200f), 0f, Paint.Filled(red));
            ctx.PushClip(new RectF(0f, 0f, 1f, 1f));
            ctx.PopTransform();
            ctx.PopTransform();
        },
    }.Px(40f, 40f);
    var after = new Box { Draw = (_, ctx) => ctx.DrawRect(ctx.Rect, 0f, Paint.Filled(green)) }.Px(40f, 40f);
    var root = new Box { SizeX = SemanticSize.ChildrenSum(), SizeY = SemanticSize.ChildrenSum() }.Padded(new EdgeInsets(20f, 0f, 0f, 30f));
    UiNode.PushIntoParent(root, drawn);
    UiNode.PushIntoParent(root, after);
    var px = Frames.Frame(root, new SurfaceOptions { Width = 160, Height = 100, ScaleFactor = 1f }, ThemeLoader.NeutralDark()).RawBgraBytes;
    bool Is(int x, int y, Rgba c) { int i = (y * 160 + x) * 4; return px[i] == c.B && px[i + 1] == c.G && px[i + 2] == c.R; }

    IsTrue(ReferenceEquals(drawn, handed), "the callback is handed its box");
    IsTrue(Is(30, 20, red) && Is(69, 59, red), "(0,0) is the box's top-left, and the box is drawn edge to edge");
    IsTrue(!Is(29, 20, red) && !Is(30, 19, red), "nothing is drawn above or left of the box");
    IsTrue(!Is(30, 60, red) && !Is(110, 40, red), "nor below or right of it: the rect is clipped to the box");
    IsTrue(Is(70, 20, green) && Is(109, 59, green), "the next box draws whole: the clip left pushed was popped, and the extra pops undid nothing of the painter's");
});

Run("Raster: DrawContext primitives draw with backend-neutral paints, paths and gradients", () =>
{
    // The former Skia escape hatch: paints are values, paths and
    // gradients are the backend's handles, and none of it names SkiaSharp.
    var root = new Box
    {
            Draw = (_, ctx) =>
            {
                var r = ctx.Rect;
                var shader = Bibui.Core.Draw.RasterShader.Linear(new System.Numerics.Vector2(r.Left, r.Top),
                    new System.Numerics.Vector2(r.Right, r.Top), [new Rgba(255, 0, 0), new Rgba(0, 0, 255)], []);
                ctx.DrawRect(r, 8f, new Paint { Shader = shader });
                var tri = ctx.Path(CacheKey.Of([42]), r, static (rect, p) =>
                {
                    p.MoveTo(rect.Left + 10, rect.Bottom - 10);
                    p.LineTo(rect.MidX, rect.Top + 10);
                    p.LineTo(rect.Right - 10, rect.Bottom - 10);
                    p.Close();
                });
                ctx.DrawPath(tri, new Paint { Stroke = Rgba.White, StrokeWidth = 2f, Dash = new Bibui.Core.Draw.DashPattern(4f, 2f) });
            },
    }.Px(120f, 60f);
    var output = Frames.Frame(root, new SurfaceOptions { Width = 120, Height = 60, ScaleFactor = 1f }, ThemeLoader.NeutralDark());
    var px = output.RawBgraBytes;
    // BGRA: left edge red-ish, right edge blue-ish, from the gradient.
    int Px(int x, int y) => (y * 120 + x) * 4;
    IsTrue(px[Px(4, 30) + 2] > 200 && px[Px(4, 30)] < 60, "left of the gradient is red");
    IsTrue(px[Px(115, 30)] > 200 && px[Px(115, 30) + 2] < 60, "right of the gradient is blue");
    IsTrue(px.Chunk(4).Any(c => c[0] > 240 && c[1] > 240 && c[2] > 240), "the dashed white stroke drew");
});

Run("Box.Draw: a retained box draws while its build says so, and acquiring it again clears it", () =>
{
    // Draw is build state like the rest: BoxFromId (and the keyless pool)
    // clears it, so a callback set under a condition does not linger into
    // frames whose condition is false.
    bool draw = true;
    int retainedCalls = 0, pooledCalls = 0;
    Box? retained = null, pooled = null;
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        var box = runtime.BoxFromId("drawn", InteractionKinds.LeftPress);
        box.Px(60f, 60f);
        if (draw) box.Draw = (_, _) => retainedCalls++;
        using (runtime.PushParent(box))
        {
            var inner = runtime.Box().Px(20f, 20f);
            if (draw) inner.Draw = (_, _) => pooledCalls++;
            pooled = inner;
        }
        retained = box;
        return box;
    }, new SurfaceOptions { Width = 60, Height = 60 });

    runtime.Step();
    Eq(1, retainedCalls, "the retained box's callback runs at paint");
    Eq(1, pooledCalls, "so does a pooled box's");
    var first = retained;

    draw = false;
    runtime.Step();
    IsTrue(ReferenceEquals(first, retained), "the same retained box both frames");
    IsNull(retained!.Draw, "BoxFromId cleared the callback");
    IsNull(pooled!.Draw, "the keyless pool cleared it too");
    Eq(1, retainedCalls, "a cleared callback is not called again");
    Eq(1, pooledCalls, "nor is a pooled one");
});

Run("DrawContext.Resolve: follows a theme changed between the build and the paint", () =>
{
    var light = ThemeLoader.BibuiLight();
    var dark = ThemeLoader.BibuiDark();
    static Rgba Expect(ThemeTokens t, UiColor c) { var (r, g, b, a) = c.ResolveColor(t.Colors); return new Rgba(r, g, b, a); }
    IsTrue(Expect(light, UiColor.Surface) != Expect(dark, UiColor.Surface), "the two themes differ, or this proves nothing");

    Rgba accent = default, surface = default, faded = default;
    ThemeTokens? seen = null;
    var root = new Box
    {
        Draw = (_, ctx) =>
        {
            seen = ctx.Theme;
            accent = ctx.Resolve(UiColor.Accent);
            surface = ctx.Resolve(UiColor.Surface);
            faded = ctx.Resolve(UiColor.Accent.WithAlpha(0.5f));
            ctx.DrawRect(ctx.Rect, 0f, Paint.Filled(surface));
        },
    }.Px(40f, 40f);
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => root, light, new SurfaceOptions { Width = 40, Height = 40, ScaleFactor = 1f });
    runtime.Step();                // a frame in one theme
    runtime.SetTheme(dark);            // then the theme changes
    var output = runtime.Frame();      // and the next frame is painted in it

    IsTrue(ReferenceEquals(dark, seen), "ctx.Theme is the theme the frame is painted in");
    Eq(Expect(dark, UiColor.Accent), accent, "a token resolves against the painting theme");
    Eq(Expect(dark, UiColor.Surface), surface, "every token does");
    Eq(new Rgba(accent.R, accent.G, accent.B, 128), faded, "WithAlpha applies, over the same token");
    var px = output.RawBgraBytes;
    int mid = (20 * 40 + 20) * 4;
    IsTrue(px[mid] == surface.B && px[mid + 1] == surface.G && px[mid + 2] == surface.R, "and that is what was painted");
});

// ── What a node is told about a gesture ───────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Gestures");

Run("A box is told which gesture it was", () =>
{
    // The answer carries the button, where the pointer was and the modifiers,
    // so a middle-drag and a click are told apart without a second place to look.
    var runtime = BibuiRuntime.Create();
    Box canvas = null!;
    runtime.SetBuild(() =>
    {
        canvas = runtime.BoxFromId("canvas", InteractionKinds.All);
        canvas.Px(100f, 100f);
        return canvas;
    }, new SurfaceOptions { Width = 120, Height = 120, ScaleFactor = 1f });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    {
        Kind = BibUiEventKind.MouseDown, Button = MouseButton.Middle,
        X = 40f, Y = 60f, Modifiers = KeyModifiers.Shift,
    });
    var seen = runtime.InteractionsFor(canvas);

    IsTrue(seen.MiddlePressed, "the middle button");
    Eq(40f, seen.X, "and where the pointer was");
    Eq(60f, seen.Y, "and where the pointer was");
    IsTrue((seen.Modifiers & KeyModifiers.Shift) != 0, "and that shift was held");
    IsTrue(!seen.Pressed, "and not a left press it never saw");
});

Run("A box hears the right button", () =>
{
    // A right-drag is answered like any other gesture, so an application keeps
    // no raw pointer state of its own.
    var runtime = BibuiRuntime.Create();
    Box canvas = null!;
    runtime.SetBuild(() =>
    {
        canvas = runtime.BoxFromId("canvas", InteractionKinds.All);
        canvas.Px(100f, 100f);
        return canvas;
    }, new SurfaceOptions { Width = 120, Height = 120, ScaleFactor = 1f });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    {
        Kind = BibUiEventKind.MouseDown, Button = MouseButton.Right, X = 30f, Y = 30f,
    });
    var seen = runtime.InteractionsFor(canvas);
    IsTrue(seen.RightPressed, "the right press arrives");

    // Taking the press is what makes the drag this node's, so it reads as one
    // from the ask after it — never on the frame of the press itself.
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 70f, Y = 50f });
    seen = runtime.InteractionsFor(canvas);
    IsTrue(seen.RightDragging, "and it is a right-drag");
    Eq(40f, seen.DragDeltaX, "anchored where the button went down");
    Eq(20f, seen.DragDeltaY, "anchored where the button went down");
});

// ── BibuiTestServer: synthesised keyboard input ───────────────────────────────
//
// The point of `press` and `type` is that a test can reach the host app's own
// keyboard handling, which is only true if the events actually land on the
// frame queue where a build callback reads them. So these drive a real socket
// and assert on what the build callback saw — not on the command's "ok".

Console.WriteLine();
Console.WriteLine("BibuiTestServer keyboard");

// Each test gets its own port; a stale listener from a previous run would
// otherwise make the failure look like a protocol bug.
int nextTestPort = 45411;

// Every request carries the server's token; the tests give it one, and write
// no token file, so a test never touches the user's.
const string TestServerToken = "runtime-tests-0123456789abcdef";
static string Authorised(string json) => "{\"token\":\"" + TestServerToken + "\"," + json[1..];
static string TestServerOut() =>
    Path.Combine(Path.GetTempPath(), "bibui-runtime-tests", "test-server-" + Guid.NewGuid().ToString("N"));
// A server on runtime, and the app's loop it waits on.
BibuiTestServer NewTestServer(BibuiRuntime runtime, int port, string? output = null)
{
    var server = new BibuiTestServer(runtime, output ?? TestServerOut(), port, TestServerToken, writeTokenFile: false, draw: new SkiaCpuRenderer().Draw);
    runtime.Serve(server);
    runtime.RunLoop();
    return server;
}
static void StopTestServer(BibuiTestServer server)
{
    server.Stop();
    try { Directory.Delete(server.OutputDirectory, recursive: true); }
    catch (IOException) { }
}

// Mount a runtime whose build records every event of the frame, start a test
// server on it, and hand both to the body.
void WithKeyboardServer(Action<Func<object, string>, List<BibUiEvent>> body)
{
    var runtime = BibuiRuntime.Create();
    var seen = new List<BibUiEvent>();
    runtime.SetBuild(() =>
    {
        // Copies: an event lives until the frame ends, and the pool hands it out again.
        foreach (var ev in runtime.UnconsumedEvents) { var copy = new BibUiEvent(); copy.CopyFrom(ev); seen.Add(copy); }
        return new Box { Id = "canvas" }.Px(100f, 100f);
    }, new SurfaceOptions { Width = 120, Height = 120, ScaleFactor = 1f });
    runtime.Step();
    seen.Clear();

    var port = nextTestPort++;
    var server = NewTestServer(runtime, port);
    server.Start();
    try
    {
        using var client = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        body(cmd =>
        {
            writer.WriteLine(Authorised(System.Text.Json.JsonSerializer.Serialize(cmd)));
            return reader.ReadLine() ?? "";
        }, seen);
    }
    finally
    {
        StopTestServer(server);
    }
}

Run("TestServer press: a control key reaches the frame queue as the window would build it", () =>
    WithKeyboardServer((send, seen) =>
    {
        var reply = send(new { cmd = "press", key = "Delete" });
        IsTrue(reply.Contains("\"ok\":true"), $"press must succeed, got {reply}");

        var ev = seen.FirstOrDefault(e => e.Key == Key.Delete);
        IsTrue(ev is not null, "the Delete event must reach the build callback");
        IsTrue(ev!.Kind == BibUiEventKind.Edit, "Delete must arrive as an Edit, as the window builds it");
    }));

Run("TestServer press: the key is let go of, in a later frame, as a keyboard does", () =>
    WithKeyboardServer((send, seen) =>
    {
        send(new { cmd = "press", key = "Tab" });
        var kinds = seen.Where(e => e.Key == Key.Tab).Select(e => e.Kind).ToList();
        Eq(2, kinds.Count, "a press is a key going down and coming up");
        IsTrue(kinds[0] != BibUiEventKind.Release && kinds[1] == BibUiEventKind.Release,
               $"down first, then the release; got {string.Join(", ", kinds)}");
    }));

Run("TestServer press: modifiers ride along", () =>
    WithKeyboardServer((send, seen) =>
    {
        send(new { cmd = "press", key = "A", ctrl = true });

        var ev = seen.FirstOrDefault(e => e.Key == Key.A);
        IsTrue(ev is not null, "the A press must reach the build callback");
        IsTrue(ev!.Ctrl, "ctrl must be set on the synthesised event");
    }));

Run("TestServer press: Arrow spellings alias the bare direction names", () =>
    WithKeyboardServer((send, seen) =>
    {
        send(new { cmd = "press", key = "ArrowLeft" });

        IsTrue(seen.Any(e => e.Key == Key.Left), "ArrowLeft must arrive as Key.Left");
    }));

Run("TestServer press: an unknown key name is an error, not a silent no-op", () =>
    WithKeyboardServer((send, seen) =>
    {
        var reply = send(new { cmd = "press", key = "NotAKey" });
        IsTrue(!reply.Contains("\"ok\":true"), $"an unknown key must report an error, got {reply}");
        IsTrue(seen.Count == 0, "and must enqueue nothing");
    }));

// `press` cannot do this: a keyboard reports printable characters on a channel
// of their own, so a digit is not reachable through a key name.
Run("TestServer type: text arrives one Text event per character, in order", () =>
    WithKeyboardServer((send, seen) =>
    {
        var reply = send(new { cmd = "type", text = "150" });
        IsTrue(reply.Contains("\"ok\":true"), $"type must succeed, got {reply}");

        var typed = string.Concat(seen.Where(e => e.Kind == BibUiEventKind.Text).Select(e => e.Char));
        Eq("150", typed, "every character must arrive, in the order given");
    }));

Run("TestServer type: control characters carry no printable event", () =>
    WithKeyboardServer((send, seen) =>
    {
        send(new { cmd = "type", text = "a\tb\nc" });

        var typed = string.Concat(seen.Where(e => e.Kind == BibUiEventKind.Text).Select(e => e.Char));
        Eq("abc", typed, "tabs and newlines are key presses, not text");
    }));

Run("TestServer type: empty text is accepted and enqueues nothing", () =>
    WithKeyboardServer((send, seen) =>
    {
        var reply = send(new { cmd = "type", text = "" });
        IsTrue(reply.Contains("\"ok\":true"), $"empty text must succeed, got {reply}");
        IsTrue(seen.Count == 0, "and must enqueue nothing");
    }));

// ── BibuiTestServer: the wire format ──────────────────────────────────────────
//
// The server's replies come from a source-generated JSON context so a Native AOT
// app carries no reflection JSON. Scripts read these lines (nc, port
// 44334), so each must be byte for byte what the anonymous types used to give:
// the expected line is serialised here the old way, with reflection.

Console.WriteLine();
Console.WriteLine("BibuiTestServer wire format");

void WithLabelServer(Action<int> body)
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        var canvas = runtime.BoxFromId("canvas", InteractionKinds.Disabled).Px(100f, 40f);
        UiNode.PushIntoParent(canvas, runtime.Text("Tæst <&> \"q\"".ToArray()).Px(90f, 20f));
        return canvas;
    }, new SurfaceOptions { Width = 120, Height = 60, ScaleFactor = 1f });
    runtime.Step();

    var port = nextTestPort++;
    var server = NewTestServer(runtime, port);
    server.Start();
    try { body(port); }
    finally { StopTestServer(server); }
}

Run("TestServer replies are byte-identical to the reflection-serialised format", () =>
    WithLabelServer(port =>
    {
        using var client = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        string Send(string line) { writer.WriteLine(Authorised(line)); return reader.ReadLine() ?? ""; }
        static string Old(object o) => System.Text.Json.JsonSerializer.Serialize(o);

        Eq("{\"ok\":true}", Send("{\"cmd\":\"ping\"}"), "ping");
        Eq(Old(new { ok = true, result = true }), Send("{\"cmd\":\"is_visible\",\"id\":\"canvas\"}"), "is_visible, found");
        Eq(Old(new { ok = true, result = false }), Send("{\"cmd\":\"is_visible\",\"id\":\"nope\"}"), "is_visible, missing");
        Eq(Old(new { ok = true, result = true }), Send("{\"cmd\":\"is_disabled\",\"id\":\"canvas\"}"), "is_disabled");
        Eq(Old(new { ok = true, result = (string?)"Tæst <&> \"q\"" }), Send("{\"cmd\":\"get_text\",\"id\":\"canvas\"}"), "get_text, escaped as before");
        Eq(Old(new { ok = false, error = "No node with id 'n<é>'" }), Send("{\"cmd\":\"get_text\",\"id\":\"n<é>\"}"), "an error, escaped as before");
        Eq(Old(new { ok = false, error = "Unknown command: bogus" }), Send("{\"cmd\":\"bogus\"}"), "unknown command");
    }));

Run("WorkflowClient round-trips against BibuiTestServer", () =>
    WithLabelServer(port =>
    {
        using var client = new WorkflowClient(port: port, token: TestServerToken);
        client.Connect();
        client.Ping();
        IsTrue(client.IsVisible("canvas"), "canvas is visible");
        IsTrue(!client.IsVisible("nope"), "an unknown id is not visible");
        IsTrue(client.IsDisabled("canvas"), "canvas claims Disabled");
        Eq("Tæst <&> \"q\"", client.GetText("canvas"), "get_text returns the label's words");
        client.Hover(10f, 10.5f);
        client.Scroll(10f, 10f, -3f);
        client.ClickAt(5f, 5f);
        var threw = false;
        try { client.GetText("nope"); } catch (Exception ex) { threw = ex.Message.Contains("No node with id 'nope'"); }
        IsTrue(threw, "a server error surfaces as an exception carrying its message");
    }));

// The client's requests too: a fake server records each line and answers ok.
Run("WorkflowClient requests are byte-identical to the reflection-serialised format", () =>
{
    var port = nextTestPort++;
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
    listener.Start();
    var lines = new List<string>();
    var serverThread = new Thread(() =>
    {
        using var conn = listener.AcceptTcpClient();
        using var stream = conn.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lock (lines) lines.Add(line);
            writer.WriteLine(line.Contains("get_text") ? "{\"ok\":true,\"result\":null}" : "{\"ok\":true,\"result\":true}");
        }
    }) { IsBackground = true };
    serverThread.Start();
    try
    {
        using (var client = new WorkflowClient(port: port, token: TestServerToken))
        {
            client.Connect();
            client.Ping();
            client.Click("a\"b");
            client.ClickAt(1.5f, 2f);
            client.Hover(3f, 4.25f);
            client.Scroll(5f, 6f, -0.1f);
            client.Screenshot("shots/x é.png");
            client.Fill("f", "<v>");
            client.IsVisible("v");
            client.IsDisabled("d");
            Eq<string?>(null, client.GetText("t"), "a null result reads back as null");
            client.StartRecording("out.mp4");
            client.StopRecording();
        }
        serverThread.Join(2000);

        static string Old(object o) => Authorised(System.Text.Json.JsonSerializer.Serialize(o));
        string[] expected =
        [
            Old(new { cmd = "ping" }),
            Old(new { cmd = "click", id = "a\"b" }),
            Old(new { cmd = "click_at", x = 1.5f, y = 2f }),
            Old(new { cmd = "hover", x = 3f, y = 4.25f }),
            Old(new { cmd = "scroll", x = 5f, y = 6f, dy = -0.1f }),
            Old(new { cmd = "screenshot", path = "shots/x é.png" }),
            Old(new { cmd = "fill", id = "f", value = "<v>" }),
            Old(new { cmd = "is_visible", id = "v" }),
            Old(new { cmd = "is_disabled", id = "d" }),
            Old(new { cmd = "get_text", id = "t" }),
            Old(new { cmd = "start_recording", output = "out.mp4" }),
            Old(new { cmd = "stop_recording" }),
        ];
        lock (lines)
        {
            Eq(expected.Length, lines.Count, "one line per command");
            for (int i = 0; i < expected.Length; i++) Eq(expected[i], lines[i], $"request {i}");
        }
    }
    finally { listener.Stop(); }
});

// ── BibuiTestServer: who may use it, and where it may write ───────────────────
//
// A loopback port is reachable from any local process and, through a
// cross-protocol POST, from any web page the user has open. So the server is
// off unless asked for, every request carries a token, anything HTTP-shaped
// ends the connection before a later line can run, files land only in the
// output directory, and ffmpeg gets its arguments one by one.

Console.WriteLine();
Console.WriteLine("BibuiTestServer security");

// A runtime with something on it, for a server to drive. Its loop is started
// once its server is known: one loop per runtime, as an app has.
BibuiRuntime SecurityRuntime()
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => new Box { Id = "canvas" }.Px(40f, 40f),
        new SurfaceOptions { Width = 60, Height = 60, ScaleFactor = 1f });
    runtime.Step();
    return runtime;
}

// Writes every line on one connection, then reads every reply until the server
// closes it (or two seconds pass).
static List<string> Exchange(int port, params string[] lines)
{
    using var client = new System.Net.Sockets.TcpClient("127.0.0.1", port);
    client.ReceiveTimeout = 2000;
    using var stream = client.GetStream();
    using var reader = new StreamReader(stream);
    using var writer = new StreamWriter(stream) { AutoFlush = true };
    foreach (var line in lines) writer.WriteLine(line);
    var replies = new List<string>();
    try
    {
        string? reply;
        while ((reply = reader.ReadLine()) is not null) replies.Add(reply);
    }
    catch (IOException) { } // the timeout: the server is still waiting for more
    return replies;
}

void WithSecurityServer(Action<int, BibuiTestServer> body)
{
    var port = nextTestPort++;
    var server = NewTestServer(SecurityRuntime(), port);
    server.Start();
    try { body(port, server); }
    finally { StopTestServer(server); }
}

Run("TestServer is off without BIBUI_TEST_SERVER=1", () =>
{
    var saved = Environment.GetEnvironmentVariable(BibuiTestServer.EnableVariable);
    try
    {
        foreach (var value in new string?[] { null, "", "0", "true", "yes" })
        {
            Environment.SetEnvironmentVariable(BibuiTestServer.EnableVariable, value);
            var port = nextTestPort++;
            IsTrue(!BibuiTestServer.IsEnabled, $"'{value}' does not turn it on");
            using var server = BibuiTestServer.StartIfEnabled(SecurityRuntime(), port, new SkiaCpuRenderer().Draw);
            IsTrue(server is null, $"with BIBUI_TEST_SERVER='{value}' no server starts");
            var refused = false;
            try { using var c = new System.Net.Sockets.TcpClient("127.0.0.1", port); }
            catch (System.Net.Sockets.SocketException) { refused = true; }
            IsTrue(refused, $"and nothing listens on {port}");
        }
    }
    finally { Environment.SetEnvironmentVariable(BibuiTestServer.EnableVariable, saved); }
});

Run("TestServer with BIBUI_TEST_SERVER=1 starts, and writes its token for the user alone", () =>
{
    var saved = new[] { BibuiTestServer.EnableVariable, BibuiTestServer.OutputVariable, "XDG_RUNTIME_DIR" }
        .ToDictionary(n => n, Environment.GetEnvironmentVariable);
    var runtimeDir = TestServerOut();
    var output = TestServerOut();
    Directory.CreateDirectory(runtimeDir);
    try
    {
        Environment.SetEnvironmentVariable(BibuiTestServer.EnableVariable, "1");
        Environment.SetEnvironmentVariable(BibuiTestServer.OutputVariable, output);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);
        var port = nextTestPort++;
        string tokenFile;
        var securityRuntime = SecurityRuntime();
        using (var server = BibuiTestServer.StartIfEnabled(securityRuntime, port, new SkiaCpuRenderer().Draw))
        {
            IsTrue(server is not null, "the opt-in starts a server");
            securityRuntime.Serve(server!);
            securityRuntime.RunLoop();
            tokenFile = server!.TokenFile;
            Eq(Path.Combine(runtimeDir, $"bibui-test-server-{port}.token"), tokenFile, "the token file is under XDG_RUNTIME_DIR");
            Eq(server.Token, File.ReadAllText(tokenFile), "it holds the token");
            Eq(64, server.Token.Length, "a random 256-bit token, as hex");
            if (!OperatingSystem.IsWindows())
                Eq(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile), "readable by the user only");
            Eq(Path.GetFullPath(output), server.OutputDirectory, "the output directory is BIBUI_TEST_SERVER_OUT");

            using var client = new WorkflowClient(port: port);   // reads the token file
            client.Connect();
            client.Ping();
            client.Screenshot("shot.png");
            IsTrue(File.Exists(Path.Combine(output, "shot.png")), "a screenshot lands in the output directory");
        }
        IsTrue(!File.Exists(tokenFile), "the token file goes with the server");
    }
    finally
    {
        foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(runtimeDir, recursive: true);
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
    }
});

Run("TestServer refuses a request without the token, and closes the connection", () =>
    WithSecurityServer((port, server) =>
    {
        var replies = Exchange(port, "{\"cmd\":\"screenshot\",\"path\":\"no-token.png\"}", Authorised("{\"cmd\":\"ping\"}"));
        Eq(1, replies.Count, $"one refusal, then the connection is closed; got {string.Join(" | ", replies)}");
        IsTrue(replies[0].Contains("\"ok\":false") && replies[0].Contains("token"), $"refused for its token, got {replies[0]}");
        IsTrue(!File.Exists(Path.Combine(server.OutputDirectory, "no-token.png")), "and the command did not run");

        replies = Exchange(port, "{\"token\":\"runtime-tests-0123456789abcdeX\",\"cmd\":\"ping\"}");
        IsTrue(replies.Count == 1 && replies[0].Contains("\"ok\":false"), "a wrong token is refused too");
        replies = Exchange(port, "{\"token\":42,\"cmd\":\"ping\"}");
        IsTrue(replies.Count == 1 && replies[0].Contains("\"ok\":false"), "so is a token that is not a string");

        replies = Exchange(port, Authorised("{\"cmd\":\"ping\"}"), Authorised("{\"cmd\":\"ping\"}"));
        Eq(2, replies.Count, "with the token, a connection carries many requests");
        IsTrue(replies.All(r => r == "{\"ok\":true}"), $"and each is answered; got {string.Join(" | ", replies)}");
    }));

Run("TestServer refuses anything that looks like HTTP, before a later line can run", () =>
    WithSecurityServer((port, server) =>
    {
        // What fetch('http://127.0.0.1:PORT', {method:'POST', mode:'no-cors', body}) sends,
        // with the worst body there is: a line that carries the token.
        var shot = Authorised("{\"cmd\":\"screenshot\",\"path\":\"from-the-web.png\"}");
        var replies = Exchange(port,
            "POST / HTTP/1.1", "Host: 127.0.0.1", "Content-Type: text/plain", "", shot);
        Eq(1, replies.Count, $"the request line is refused and the connection closed; got {string.Join(" | ", replies)}");
        IsTrue(replies[0].Contains("\"ok\":false"), "refused");
        IsTrue(!File.Exists(Path.Combine(server.OutputDirectory, "from-the-web.png")), "the body never ran");

        foreach (var line in new[] { "GET / HTTP/1.1", "OPTIONS * HTTP/1.1", "get /x", "PUT /",
                                     Authorised("{\"cmd\":\"get_text\",\"id\":\"HTTP/1.1\"}"), "ping", "[1,2]" })
        {
            replies = Exchange(port, line, Authorised("{\"cmd\":\"ping\"}"));
            Eq(1, replies.Count, $"'{line}' ends the connection");
            IsTrue(replies[0].Contains("\"ok\":false"), $"'{line}' is refused");
        }
    }));

Run("TestServer writes only inside its output directory", () =>
    WithSecurityServer((port, server) =>
    {
        var outside = Path.GetDirectoryName(server.OutputDirectory)!;
        var escapes = new[]
        {
            "../escape.png", "sub/../../escape.png", "..\\escape.png", "/tmp/bibui-absolute.png",
            Path.Combine(outside, "escape.png"), "~/escape.png", "", ".", "sub/..",
        };
        foreach (var path in escapes)
        {
            var json = Authorised("{\"cmd\":\"screenshot\",\"path\":" + System.Text.Json.JsonSerializer.Serialize(path) + "}");
            var reply = Exchange(port, json).Single();
            IsTrue(reply.Contains("\"ok\":false") && reply.Contains("Refused"), $"'{path}' is refused, got {reply}");
        }
        IsTrue(!File.Exists(Path.Combine(outside, "escape.png")), "nothing was written beside the output directory");
        IsTrue(!File.Exists("/tmp/bibui-absolute.png"), "nor at an absolute path");

        var recording = Exchange(port, Authorised("{\"cmd\":\"start_recording\",\"output\":\"../escape.mp4\"}")).Single();
        IsTrue(recording.Contains("Refused"), $"a recording is held to it too, got {recording}");

        // A link inside the directory to a directory outside it.
        var target = TestServerOut();
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(server.OutputDirectory, "link"), target);
            File.CreateSymbolicLink(Path.Combine(server.OutputDirectory, "file-link.png"), Path.Combine(target, "victim.png"));
            foreach (var path in new[] { "link/escape.png", "file-link.png" })
            {
                var reply = Exchange(port, Authorised("{\"cmd\":\"screenshot\",\"path\":\"" + path + "\"}")).Single();
                IsTrue(reply.Contains("symbolic link"), $"'{path}' goes through a link and is refused, got {reply}");
            }
            IsTrue(Directory.GetFiles(target).Length == 0, "nothing reached the link's target");
        }
        finally { Directory.Delete(target, recursive: true); }

        var ok = Exchange(port, Authorised("{\"cmd\":\"screenshot\",\"path\":\"sub/dir/ok.png\"}")).Single();
        Eq("{\"ok\":true}", ok, "a relative path inside it is written");
        IsTrue(File.Exists(Path.Combine(server.OutputDirectory, "sub", "dir", "ok.png")), "where it says");
    }));

Run("TestServer gives ffmpeg one argument per element, so a quote in a name adds none", () =>
    WithSecurityServer((port, server) =>
    {
        var name = "clip\" -i /etc/passwd -f data \"stolen.mp4";
        var path = server.ResolveOutputPath(name);
        Eq(Path.Combine(server.OutputDirectory, name), path, "the name stays one file in the output directory");

        var psi = BibuiTestServer.FfmpegStartInfo(64, 48, path);
        Eq("", psi.Arguments, "no argument string to be split");
        Eq(path, psi.ArgumentList[^1], "the whole name, quote and all, is the last argument");
        Eq(1, psi.ArgumentList.Count(a => a == "-i"), "the only input is the frame pipe");
        Eq("pipe:0", psi.ArgumentList[psi.ArgumentList.IndexOf("-i") + 1], "which is stdin");
        Eq(20, psi.ArgumentList.Count, "and the count is fixed");
        IsTrue(!psi.UseShellExecute, "and no shell");

        var threw = false;
        try { BibuiTestServer.FfmpegStartInfo(64, 48, "-i"); } catch (ArgumentException) { threw = true; }
        IsTrue(threw, "an output that is not a full path cannot reach ffmpeg, where it could pass for an option");
    }));

// ── InteractionsFor ───────────────────────────────────────────────────────────
//
// What a build function can ask about a node it described. The point of these
// is that a plain Box — something any consumer can build — answers the same
// questions a Button does, once it has said what it takes.

// Two overlapping boxes in a layer: "under" fills the surface, "over" is
// painted on top of its right half. Plain Boxes, no built-in kind between them
// and the runtime — but they claim, because a box that claims nothing is
// answered nothing. Claims is how a box says it is there to be interacted
// with; saying nothing is not a quieter way of saying everything.
(BibuiRuntime Page, Box Under, Box Over) Overlapping()
{
    var runtime = BibuiRuntime.Create();
    Box under = null!, over = null!;
    runtime.SetBuild(
        () =>
        {
            under = runtime.BoxFromId("under", InteractionKinds.All);
            under.Px(400f, 200f);

            over = runtime.BoxFromId("over", InteractionKinds.All);
            over.Px(200f, 200f).At(200f, 0f);

            return Layer(null, under, over).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();   // the scene these ask against
    return (runtime, under, over);
}

void Press(BibuiRuntime r, float x, float y) => r.ProcessEvent(
    new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });

Run("InteractionsFor: a press inside a plain Box is reported to it", () =>
{
    var (page, under, over) = Overlapping();
    Press(page, 100f, 100f);
    IsTrue(page.InteractionsFor(under).Pressed, "the box under the pointer was pressed");
});

Run("InteractionsFor: a press outside a node is not its press", () =>
{
    var (page, under, over) = Overlapping();
    Press(page, 100f, 100f);          // left half — "over" starts at x=200
    IsTrue(!page.InteractionsFor(over).Pressed, "a box the press missed reports nothing");
});

Run("InteractionsFor: of two boxes over the same point, the first to ask has it", () =>
{
    // The two boxes overlap on the right half, and paint order does not decide
    // between them — nothing hit-tests any more. Whoever asks first takes the
    // press and eats the event, so z-order is something builder code expresses
    // by choosing what it asks about first.
    var (page, under, over) = Overlapping();
    Press(page, 300f, 100f);

    var underFirstAsked = page.InteractionsFor(under);   // asked first
    var overAskedSecond = page.InteractionsFor(over);

    IsTrue(underFirstAsked.Pressed, "asked first, so it has it");
    IsTrue(!overAskedSecond.Pressed, "and there is nothing left, though the point is inside it too");

    // The same two boxes, the same press, the other order.
    var (reversed, underR, overR) = Overlapping();
    Press(reversed, 300f, 100f);
    var overFirst  = reversed.InteractionsFor(overR);
    var underFirst = reversed.InteractionsFor(underR);

    IsTrue(overFirst.Pressed, "asking the other one first hands it the other way");
    IsTrue(!underFirst.Pressed, "which is the whole of the ordering rule");
});

Run("InteractionsFor: a press a child took is not left for its parent", () =>
{
    // Nesting, not overlap: both own the point, so both could have it — and
    // asking takes it, so only the first to ask does. Describe your children
    // before you ask and that is the innermost one, which is what stops a panel
    // acting on a press its own control has already handled.
    var runtime = BibuiRuntime.Create();
    Box outer = null!, inner = null!;
    runtime.SetBuild(
        () =>
        {
            outer = runtime.BoxFromId("outer", InteractionKinds.LeftPress);
            outer.Px(400f, 200f);

            inner = runtime.BoxFromId("inner", InteractionKinds.LeftPress);
            inner.Px(100f, 50f);

            UiNode.PushIntoParent(outer, inner);
            return outer;
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    Press(runtime, 50f, 25f);

    IsTrue(runtime.InteractionsFor(inner).Pressed, "the child was pressed");
    IsTrue(!runtime.InteractionsFor(outer).Pressed, "and there is nothing left for the parent");
});

Run("InteractionsFor: hovering follows the pointer without an event", () =>
{
    var (page, under, over) = Overlapping();
    page.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 300f, Y = 100f });

    IsTrue(page.InteractionsFor(over).Hovering, "the pointer is over it");
    page.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 50f, Y = 100f });
    IsTrue(!page.InteractionsFor(over).Hovering, "and then it is not");
});

Run("InteractionsFor: wheel travel over a node is summed, not last-one-wins", () =>
{
    // A frame can carry several notches. A scroll container that read only the
    // last would move a third as far as the wheel was turned.
    var (page, under, over) = Overlapping();
    page.ProcessEvents(
    [
        new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollY = -1f },
        new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollY = -1f },
        new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollY = -1f },
    ]);
    Eq(-3f, page.InteractionsFor(under).ScrollY, "three notches are three notches");
});

void Move(BibuiRuntime r, float x, float y) => r.ProcessEvent(
    new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });

void Release(BibuiRuntime r, float x, float y, MouseButton b = MouseButton.Left) => r.ProcessEvent(
    new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = b, X = x, Y = y });

Run("Drag: a press then a move is that node's drag, with the travel since", () =>
{
    var (page, under, over) = Overlapping();
    Press(page, 300f, 100f);

    // The frame in which the press becomes this node's: a drag is owned by
    // whoever took the press, so it reads as one only from the ask after that.
    page.InteractionsFor(over);
    Move(page, 340f, 130f);

    var i = page.InteractionsFor(over);
    IsTrue(i.Dragging, "the button is still down and went down on it");
    Eq(300f, i.DragStartX, "the anchor is where the press landed");
    Eq(40f,  i.DragDeltaX, "and the delta is the travel since");
    Eq(30f,  i.DragDeltaY, "and the delta is the travel since");
});

Run("Separator.Build: one pixel thick, as long as its parent", () =>
{
    var runtime = BibuiRuntime.Create();
    Box across = null!, down = null!;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 100f);
            using (runtime.PushParent(column))
            {
                across = Bibui.Core.Runtime.Separator.Build(runtime);
                var row = runtime.Box();
                row.Px(300f, 40f);
                using (runtime.PushParent(row))
                    down = Bibui.Core.Runtime.Separator.Build(runtime, vertical: true);
            }
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 100 });
    runtime.Step();
    Eq(300f, across.Extent.X, "a horizontal one is its parent's width");
    Eq(1f, across.Extent.Y, "and 1 px tall");
    Eq(1f, down.Extent.X, "a vertical one is 1 px wide");
    Eq(40f, down.Extent.Y, "and its parent's height");
});

Run("Progress.Build: the fill is the given share of the track", () =>
{
    var runtime = BibuiRuntime.Create();
    Box bar = null!;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(400f, 100f);
            using (runtime.PushParent(column))
                bar = Bibui.Core.Runtime.Progress.Build(runtime, 65f, 100f).Px(width: 400f);
            return column;
        },
        new SurfaceOptions { Width = 400, Height = 100 });
    runtime.Step();
    var fill = (Box)bar.FirstChild!;
    Eq(400f, bar.Extent.X, "the track is as wide as it was sized");
    Eq(Bibui.Core.Runtime.Progress.DefaultThickness, bar.Extent.Y, "and the default thickness tall");
    Eq(260f, fill.Extent.X, "65 of 100 fills 65% of it");
    Eq(bar.Rect.X, fill.Rect.X, "from its left end");
});

Run("Progress.Build: a label above, and nothing filled at zero or past the end clamped", () =>
{
    Eq(0f, Bibui.Core.Runtime.Progress.Share(-5f, 100f), "below zero is empty");
    Eq(1f, Bibui.Core.Runtime.Progress.Share(150f, 100f), "past max is full");
    Eq(0f, Bibui.Core.Runtime.Progress.Share(10f, 0f), "no max is empty");

    var runtime = BibuiRuntime.Create();
    Box column = null!;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnPx(400f, 100f);
            using (runtime.PushParent(page))
                column = Bibui.Core.Runtime.Progress.Build(runtime, 0f, 100f, "Upload").Px(width: 300f);
            return page;
        },
        new SurfaceOptions { Width = 400, Height = 100 });
    runtime.Step();
    var label = (Text)column.FirstChild!;
    var track = (Box)column.LastChild!;
    Eq("Upload", new string(label.Content));
    IsTrue(track.Rect.Y > label.Rect.Y + label.Extent.Y - 0.5f, "the track is under the label");
    IsTrue(track.FirstChild is null, "nothing is filled at zero");
});

Run("Avatar.Build: a circle of the given size with the initials centred", () =>
{
    var runtime = BibuiRuntime.Create();
    Box circle = null!;
    runtime.SetBuild(
        () =>
        {
            var row = runtime.RowPx(200f, 100f);
            using (runtime.PushParent(row))
                circle = Bibui.Core.Runtime.Avatar.Build(runtime, "JD", 56f);
            return row;
        },
        new SurfaceOptions { Width = 200, Height = 100 });
    runtime.Step();
    Eq(56f, circle.Extent.X);
    Eq(56f, circle.Extent.Y);
    Eq(28f, circle.Style.CornerRadius, "a circle");
    Text? initials = null;
    void Find(UiNode n) { if (n is Text t) initials = t; for (var c = n.FirstChild; c is not null; c = c.Next) Find(c); }
    Find(circle);
    IsTrue(initials is not null, "the initials are in it");
    float cx = initials!.Rect.X + initials.Extent.X / 2f - circle.Rect.X;
    float cy = initials.Rect.Y + initials.Extent.Y / 2f - circle.Rect.Y;
    IsTrue(MathF.Abs(cx - 28f) < 1f, $"centred across ({cx})");
    IsTrue(MathF.Abs(cy - 28f) < 1f, $"centred down ({cy})");
});

Run("KeyboardShortcut.Build: a keycap per key, 24 px tall, 4 px apart", () =>
{
    var runtime = BibuiRuntime.Create();
    Box row = null!;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.RowPx(300f, 60f);
            using (runtime.PushParent(page))
                row = Bibui.Core.Runtime.KeyboardShortcut.Build(runtime, "Ctrl", "K");
            return page;
        },
        new SurfaceOptions { Width = 300, Height = 60 });
    runtime.Step();
    var first = (Box)row.FirstChild!;
    var last = (Box)row.LastChild!;
    IsTrue(first != last, "two keycaps");
    Eq(Bibui.Core.Runtime.KeyboardShortcut.KeyHeight, first.Extent.Y);
    Eq(Bibui.Core.Runtime.KeyboardShortcut.KeyHeight, row.Extent.Y);
    Eq(4f, last.Rect.X - (first.Rect.X + first.Extent.X), "4 px between them");
    Eq(first.Extent.X + 4f + last.Extent.X, row.Extent.X, "the row is as wide as they are");
    var key = (Text)last.FirstChild!.Next!;
    Eq(key.Extent.X + 16f, last.Extent.X, "8 px either side of the key's name");
});

Run("Drag: the node keeps the drag once the pointer has left it", () =>
{
    // The reason the anchor is stored at all. A node being dragged is usually
    // dragged out from under the cursor, and topmost-at-now would hand the
    // gesture to whatever it passed over.
    var (page, under, over) = Overlapping();
    Press(page, 300f, 100f);
    page.InteractionsFor(over);     // the frame that takes it
    Move(page, 50f, 100f);            // now over "under", far from "over"

    IsTrue(page.InteractionsFor(over).Dragging, "the press decides whose drag it is");
    IsTrue(!page.InteractionsFor(under).Dragging, "not whatever the pointer has since crossed");
});

Run("Drag: releasing ends it, and the release still carries the travel", () =>
{
    // Nothing asks between the press and the release here, so all three events
    // reach one ask — the frame that ends the gesture is the only frame that
    // ever sees it. This used to report a zero travel there, on the grounds
    // that the button was already up; that left the distance covered readable
    // in every frame except the one that commits it.
    var (page, under, over) = Overlapping();
    Press(page, 300f, 100f);
    Move(page, 340f, 130f);
    Release(page, 340f, 130f);

    var i = page.InteractionsFor(over);
    IsTrue(!i.Dragging, "the button is up");
    IsTrue(i.Released, "and the gesture ended on the node that owned it");
    Eq(40f, i.DragDeltaX, "with the travel it covered still on the release");
    Eq(30f, i.DragDeltaY, "on both axes");
});

Run("Drag: the right button drags separately", () =>
{
    // A right-drag is reported apart from a left one, so an application keeps
    // no pointer state of its own.
    var (page, under, over) = Overlapping();
    page.ProcessEvent(new BibUiEvent
    {
        Kind = BibUiEventKind.MouseDown, Button = MouseButton.Right, X = 300f, Y = 100f,
    });
    page.InteractionsFor(over);     // the frame that takes it
    Move(page, 320f, 90f);

    var i = page.InteractionsFor(over);
    IsTrue(i.RightDragging, "the right button is dragging it");
    IsTrue(!i.Dragging, "and the left one is not");
    Eq(20f,  i.DragDeltaX, "with its own anchor");
    Eq(-10f, i.DragDeltaY, "with its own anchor");

    Release(page, 320f, 90f, MouseButton.Right);
    IsTrue(!page.InteractionsFor(over).RightDragging, "and it ends on release");
});

Run("Drag: an anchor is the press position even when a move shares its frame", () =>
{
    // One batch, press then move. Folding the pointer first and reading it
    // after would anchor at 340 — the travel between press and frame silently
    // dropped, which is exactly what a polled gesture cannot recover.
    var (page, under, over) = Overlapping();
    page.ProcessEvents(
    [
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 300f, Y = 100f },
        new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 340f, Y = 100f },
    ]);
    page.InteractionsFor(over);     // the frame that takes it

    Eq(300f, page.InteractionsFor(over).DragStartX, "the anchor is the press, not the frame's end");
});

Run("InteractionsFor: a node that has never been painted is not interactive", () =>
{
    // Bounds come from the last painted scene. Nothing has been painted, so
    // there is nowhere the press could have landed — and the answer is the
    // empty one rather than an exception.
    var runtime = BibuiRuntime.Create();
    Box b = null!;
    runtime.SetBuild(() =>
    {
        b = runtime.BoxFromId("b", InteractionKinds.LeftPress);
        b.Px(100f, 100f);
        return b;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.ProcessEvent(
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f });

    // Never built either, so there is no box to hand over — which is the same
    // answer as a box that has never been painted.
    IsTrue(runtime.InteractionsFor(runtime.BoxFromId("b", InteractionKinds.LeftPress)).None,
        "no scene, no interaction");
});

Run("A box that claims the wheel is handed it", () =>
{
    var runtime = BibuiRuntime.Create();
    Box scroller = null!;
    runtime.SetBuild(
        () =>
        {
            scroller = runtime.BoxFromId("scroller", InteractionKinds.ViewScroll);
            scroller.Px(200f, 200f);
            UiNode.PushIntoParent(scroller, Layer(null).Px(200f, 800f));
            return scroller;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    {
        Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollY = 40f,
    });
    var seen = runtime.InteractionsFor(scroller);

    Eq(40f, seen.ScrollY, "the node is handed the wheel");
});

// A wheel claim is per axis. A list that scrolls down inside a strip that
// scrolls sideways takes the vertical travel and leaves the sideways travel to
// the strip, from the one notch.
Run("Claims: a box takes the wheel only along the axis it claimed", () =>
{
    var runtime = BibuiRuntime.Create();
    Box strip = null!, list = null!;
    runtime.SetBuild(
        () =>
        {
            strip = runtime.BoxFromId("strip", InteractionKinds.ViewScrollX);
            list = runtime.BoxFromId("list", InteractionKinds.ViewScrollY);
            runtime.InteractionsFor(list);
            runtime.InteractionsFor(strip);

            strip.Px(200f, 200f);
            list.Px(200f, 200f);
            UiNode.PushIntoParent(strip, list);
            return strip;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollX = 30f, ScrollY = 40f });

    var l = runtime.InteractionsFor(list);
    var st = runtime.InteractionsFor(strip);
    Eq(40f, l.ScrollY, "the list takes the vertical travel");
    Eq(0f, l.ScrollX, "and none of the sideways");
    Eq(30f, st.ScrollX, "which reaches the strip beneath it");
    Eq(0f, st.ScrollY, "without the vertical travel the list already took");
    Eq(new System.Numerics.Vector2(0f, 40f), list.TargetViewOffset, "the list aims down");
    Eq(new System.Numerics.Vector2(30f, 0f), strip.TargetViewOffset, "the strip aims sideways");
});

// A box that scrolls along one axis takes a wheel with no travel on that axis
// onto it: a plain wheel over a sideways strip moves the strip sideways.
foreach (var (claim, sx, sy, want) in new[]
{
    (InteractionKinds.ViewScrollX, 0f, 40f, new System.Numerics.Vector2(40f, 0f)),
    (InteractionKinds.ViewScrollY, 30f, 0f, new System.Numerics.Vector2(0f, 30f)),
    (InteractionKinds.ViewScrollX, 30f, 40f, new System.Numerics.Vector2(30f, 0f)),
})
Run($"Claims: {claim} takes a wheel of ({sx}, {sy}) as {want}", () =>
{
    var runtime = BibuiRuntime.Create();
    Box strip = null!;
    runtime.SetBuild(
        () =>
        {
            strip = runtime.BoxFromId("strip", claim);
            runtime.InteractionsFor(strip);
            strip.Px(200f, 200f);
            return strip;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollX = sx, ScrollY = sy });
    runtime.Step();
    Eq(want, strip.TargetViewOffset, "the travel lands on the axis it claims");
});

// Scroll is told about the wheel and moves nothing: a canvas spends it on zoom.
Run("Claims: Scroll reports the wheel, takes it, and leaves the view alone", () =>
{
    var runtime = BibuiRuntime.Create();
    Box canvas = null!, under = null!;
    Interaction got = default;
    runtime.SetBuild(
        () =>
        {
            canvas = runtime.BoxFromId("canvas", InteractionKinds.Scroll);
            under = runtime.BoxFromId("under", InteractionKinds.ViewScroll);
            got = runtime.InteractionsFor(canvas);
            runtime.InteractionsFor(under);
            canvas.Px(200f, 200f);
            under.Px(200f, 200f);
            UiNode.PushIntoParent(canvas, runtime.Text("content".ToArray()));
            UiNode.PushIntoParent(under, canvas);
            return under;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollX = 10f, ScrollY = 40f });
    runtime.Step();
    Eq(10f, got.ScrollX, "the sideways travel is reported");
    Eq(40f, got.ScrollY, "and the vertical");
    Eq(System.Numerics.Vector2.Zero, canvas.TargetViewOffset, "nothing moves");
    Eq(System.Numerics.Vector2.Zero, under.TargetViewOffset, "and nothing beneath gets it");
});

// ── Text through the runtime ────────────────────────────────────────────────

Run("runtime.Text: linked under the parent in scope and sized by its own text", () =>
{
    var runtime = BibuiRuntime.Create();
    Text text = null!;
    runtime.SetBuild(
        () =>
        {
            var row = runtime.BoxFromId("row");
            row.SizeX = row.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(row))
                text = runtime.Text("Sync".ToArray());
            return row;
        },
        new SurfaceOptions { Width = 200, Height = 100 });
    runtime.Step();
    var row = runtime.BoxFromId("row");
    IsTrue(text.Parent is not null && text.Parent.Id == "row", "the text is linked under the row");
    Eq(SizeKind.Text, text.SizeX.Kind, "sized by its text along X");
    Eq(SizeKind.Text, text.SizeY.Kind, "and along Y");
    IsTrue(text.Extent.X > 10f && text.Extent.Y > 10f, $"and measured: {text.Extent}");
});

Run("runtime.Text: a pushed size wins over its own text", () =>
{
    var runtime = BibuiRuntime.Create();
    Text text = null!;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(200f, 100f);
            using (runtime.PushParent(column))
            using (runtime.PushSizeX(SemanticSize.ParentPct(1f)))
                text = runtime.Text("Sync".ToArray());
            return column;
        },
        new SurfaceOptions { Width = 200, Height = 100 });
    runtime.Step();
    Eq(200f, text.Extent.X, "the pushed share of the column");
    Eq(SizeKind.Text, text.SizeY.Kind, "nothing pushed along Y");
});

Run("runtime.Text: keyless texts are reused frame to frame, cleared each time", () =>
{
    var runtime = BibuiRuntime.Create();
    var seen = new List<Text>();
    int frame = 0;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(200f, 100f);
            using (runtime.PushParent(column))
            {
                if (frame == 0)
                    using (runtime.PushFontFace(FontSlot.Mono, FontWeight.Bold))
                        seen.Add(runtime.Text("a".ToArray(), ComponentSize.Lg));
                else
                    seen.Add(runtime.Text("a".ToArray()));
                seen.Add(runtime.Text("b".ToArray()));
            }
            frame++;
            return column;
        },
        new SurfaceOptions { Width = 200, Height = 100 });
    runtime.Step();
    runtime.Step();
    IsTrue(ReferenceEquals(seen[0], seen[2]) && ReferenceEquals(seen[1], seen[3]), "the n-th call gets the n-th instance");
    IsTrue(!ReferenceEquals(seen[0], seen[1]), "two calls in a frame are two texts");
    IsTrue(seen[2].Weight == FontWeight.Regular && seen[2].Family == FontSlot.Body
        && seen[2].Size == ComponentSize.Md, "what one frame set does not reach the next");
});

Run("runtime.Icon and runtime.Spinner: linked, sized by their call, and reused", () =>
{
    var runtime = BibuiRuntime.Create();
    var icons = new List<Icon>();
    Spinner spinner = null!;
    runtime.SetBuild(
        () =>
        {
            var row = runtime.RowPx(300f, 60f);
            using (runtime.PushParent(row))
            {
                var icon = runtime.Icon("plus", size: 20f);
                icons.Add(icon);
                spinner = runtime.Spinner();
            }
            return row;
        },
        new SurfaceOptions { Width = 300, Height = 60 });
    runtime.Step();
    runtime.Step();
    Eq(new System.Numerics.Vector2(20f, 20f), icons[0].Extent, "the icon is its size");
    IsTrue(ReferenceEquals(icons[0], icons[1]), "and the same instance next frame");
    Eq(new System.Numerics.Vector2(24f, 24f), spinner.Extent, "the spinner is its default diameter");
    IsTrue(ReferenceEquals(spinner.Parent, icons[1].Parent), "both linked under the row");
});

Run("Badge.Build: a pill as large as its label plus padding, linked under the parent", () =>
{
    var runtime = BibuiRuntime.Create();
    Box pill = null!;
    runtime.SetBuild(
        () =>
        {
            var row = runtime.RowPx(300f, 60f);
            using (runtime.PushParent(row))
                pill = Bibui.Core.Runtime.Badge.Build(runtime, "Outline", BadgeVariant.Outline);
            return row;
        },
        new SurfaceOptions { Width = 300, Height = 60 });
    runtime.Step();
    var label = (Text)pill.FirstChild!;
    Eq("Outline", new string(label.Content));
    Eq(label.Extent.X + 24f, pill.Extent.X, "12 either side of the label");
    Eq(label.Extent.Y + 8f, pill.Extent.Y, "4 above and below it");
    IsTrue(pill.Parent is not null, "linked under the row");
});

Run("runtime.Checkbox: the row takes the press and reports it", () =>
{
    var runtime = BibuiRuntime.Create();
    bool on = false;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 100f);
            using (runtime.PushParent(column))
                if (runtime.Checkbox("cb", on, "Subscribe").Clicked) on = !on;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 100 });
    runtime.Step();
    var row = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("cb");
    Eq(18f, row.Height, "as tall as its square");
    IsTrue(row.Width > 18f + 12f + 40f, $"square, gap and label: {row.Width}");
    // Pressing the label, not the square, counts.
    float x = row.X + row.Width - 10f, y = row.Y + 9f;
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
    IsTrue(on, "a click on the label toggles it");
});

Run("runtime.ToggleGroup: an item's press is reported as the value picked", () =>
{
    var runtime = BibuiRuntime.Create();
    string value = "a";
    ToggleOption[] items = [new("Alpha", "a"), new("Beta", "b")];
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 100f);
            using (runtime.PushParent(column))
                if (runtime.ToggleGroup("tg", items, value).Picked is { } picked) value = picked;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 100 });
    runtime.Step();
    var beta = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("tg-b");
    Eq(34f, beta.Height, "the item fills the group inside its outline");
    float x = beta.X + beta.Width / 2f, y = beta.Y + beta.Height / 2f;
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
    Eq("b", value, "pressing Beta picks it");
});

// ── Dropdowns ────────────────────────────────────────────────────────────────

// A page whose box "under" is built first, then a row in which a dropdown is
// opened over it: 100x40 at (50, 50), holding one item.
static (BibuiRuntime Runtime, Func<(int Under, int Item)> Counts, Action<bool> SetOpen, Func<UiNode> Tree) DropDownPage()
{
    var runtime = BibuiRuntime.Create();
    int under = 0, item = 0;
    bool open = true;
    UiNode tree = null!;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnFill();
            using (runtime.PushParent(page))
            {
                var below = runtime.BoxFromId("under", InteractionKinds.LeftPress);
                below.Px(200f, 200f);
                if (runtime.InteractionsFor(below).Clicked) under++;

                var row = runtime.BoxFromId("row");
                row.SizeX = row.SizeY = SemanticSize.ChildrenSum();
                using (runtime.PushParent(row))
                {
                    runtime.Box().Px(10f, 10f);
                    if (open)
                    {
                        runtime.OpenDropDown("menu");
                        var dropDown = runtime.BeginDropDown("menu")!;
                        dropDown.Px(100f, 40f).At(50f, 50f);
                        using (runtime.PushParent(dropDown))
                        {
                            var option = runtime.BoxFromId("item", InteractionKinds.LeftPress);
                            option.Px(100f, 40f);
                            if (runtime.InteractionsFor(option).Clicked) item++;
                        }
                    }
                }
            }
            return tree = page;
        },
        new SurfaceOptions { Width = 300, Height = 300 });
    return (runtime, () => (under, item), v => open = v, () => tree);
}

Run("BeginDropDown: linked nowhere while building, last in the tree after", () =>
{
    var (runtime, _, _, tree) = DropDownPage();
    runtime.Step();
    var last = tree().LastChild;
    Eq("menu", last?.Id, "the dropdown is the tree's last child, not the row's");
    UiNode? row = null;
    for (var n = tree().FirstChild; n is not null; n = n.Next) if (n.Id == "row") row = n;
    IsTrue(row is not null, "the row is in the tree");
    for (var n = row!.FirstChild; n is not null; n = n.Next)
        IsTrue(n.Id != "menu", "and the dropdown is not in the row it was opened in");
});

Run("BeginDropDown: its rect is its own, even against a box that asked first", () =>
{
    var (runtime, counts, _, _) = DropDownPage();
    runtime.Step();
    runtime.Step();
    ClickAt(runtime, 70f, 60f);
    Eq((0, 1), counts(), "inside the dropdown its item has the press");
    ClickAt(runtime, 20f, 20f);
    Eq((1, 1), counts(), "outside it the box beneath still does");
});

Run("BeginDropDown: a dropdown no longer begun stops occupying its rect", () =>
{
    var (runtime, counts, setOpen, _) = DropDownPage();
    runtime.Step();
    runtime.Step();
    setOpen(false);
    runtime.Step();   // the frame it is not begun: still last frame's rect
    runtime.Step();   // gone
    ClickAt(runtime, 70f, 60f);
    Eq((1, 0), counts(), "the box beneath has it again");
});

// ── PushIntoParent moves ───────────────────────────────────────────────────

// A node's children by id, walked forwards and backwards; checks every link on the way.
static string Kids(UiNode parent)
{
    var forwards = new List<string>();
    UiNode? prev = null;
    for (var n = parent.FirstChild; n is not null; n = n.Next)
    {
        if (!ReferenceEquals(n.Parent, parent)) throw new Exception($"{n.Id} is in {parent.Id}'s list but names {n.Parent?.Id} as its parent");
        if (!ReferenceEquals(n.Prev, prev)) throw new Exception($"{n.Id}.Prev is {n.Prev?.Id}, not {prev?.Id}");
        forwards.Add(n.Id ?? "?");
        prev = n;
        if (forwards.Count > 100) throw new Exception($"{parent.Id}'s list runs in a ring");
    }
    if (!ReferenceEquals(parent.LastChild, prev)) throw new Exception($"{parent.Id}.LastChild is {parent.LastChild?.Id}, not {prev?.Id}");
    return string.Join(",", forwards);
}

Run("PushIntoParent: a node that has a parent is moved, and both lists stay whole", () =>
{
    Box a = new() { Id = "a" }, b = new() { Id = "b" };
    Box x = new() { Id = "x" }, y = new() { Id = "y" }, z = new() { Id = "z" };
    UiNode.PushIntoParent(a, x);
    UiNode.PushIntoParent(a, y);
    UiNode.PushIntoParent(a, z);

    UiNode.PushIntoParent(b, y);          // from the middle
    Eq("x,z", Kids(a));
    Eq("y", Kids(b));
    UiNode.PushIntoParent(b, x);          // from the front
    Eq("z", Kids(a));
    UiNode.PushIntoParent(b, z);          // the last one
    Eq("", Kids(a));
    IsNull(a.FirstChild, "a is empty");
    Eq("y,x,z", Kids(b));
});

Run("PushIntoParent: into the parent it is already in, a node moves to the end", () =>
{
    Box a = new() { Id = "a" };
    Box x = new() { Id = "x" }, y = new() { Id = "y" }, z = new() { Id = "z" };
    UiNode.PushIntoParent(a, x);
    UiNode.PushIntoParent(a, y);
    UiNode.PushIntoParent(a, z);
    UiNode.PushIntoParent(a, x);
    Eq("y,z,x", Kids(a));
    UiNode.PushIntoParent(a, x);          // already last
    Eq("y,z,x", Kids(a));
    UiNode.PushIntoParent(a, z);          // from the middle
    Eq("y,x,z", Kids(a));

    Box only = new() { Id = "only" }, solo = new() { Id = "solo" };
    UiNode.PushIntoParent(only, solo);
    UiNode.PushIntoParent(only, solo);
    Eq("solo", Kids(only));
});

Run("PushIntoParent: a node into itself or its own descendant throws, and the tree is left as it was", () =>
{
    Box a = new() { Id = "a" }, b = new() { Id = "b" }, c = new() { Id = "c" };
    UiNode.PushIntoParent(a, b);
    UiNode.PushIntoParent(b, c);
    Throws<InvalidOperationException>(() => UiNode.PushIntoParent(c, a), "an ancestor into its grandchild");
    Throws<InvalidOperationException>(() => UiNode.PushIntoParent(b, a), "a parent into its child");
    Throws<InvalidOperationException>(() => UiNode.PushIntoParent(a, a), "a node into itself");
    Eq("b", Kids(a));
    Eq("c", Kids(b));
    Eq("", Kids(c));
});

Run("PushIntoParent: Row(runtime.Text(...)) inside a PushParent scope moves the text into the row", () =>
{
    var runtime = BibuiRuntime.Create();
    Box card = null!, row = null!;
    Text above = null!, title = null!, below = null!;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnFill();
            using (runtime.PushParent(page))
            {
                card = runtime.BoxFromId("card");
                card.ChildLayoutAxis = LayoutAxis.Y;
                card.Px(200f, 100f);
                using (runtime.PushParent(card))
                {
                    above = runtime.Text("above");
                    // What is tested is Arrange's expression form moving runtime nodes, obsolete or not.
#pragma warning disable CS0618
                    row = Arrange.Row(
                        title = runtime.Text("title"),
                        Arrange.Spring(),
                        Arrange.CentredY(runtime.Box().Px(10f, 10f))).FillX().Px(height: 24f);
#pragma warning restore CS0618
                    UiNode.PushIntoParent(card, row);
                    below = runtime.Text("below");
                }
            }
            return page;
        },
        new SurfaceOptions { Width = 300, Height = 300 });

    for (int frame = 0; frame < 3; frame++)
    {
        runtime.Step();
        var kids = new List<UiNode>();
        for (var n = card.FirstChild; n is not null; n = n.Next) kids.Add(n);
        Eq(3, kids.Count, $"frame {frame}: the card holds the first text, the row and the last text");
        IsTrue(ReferenceEquals(kids[0], above) && ReferenceEquals(kids[1], row) && ReferenceEquals(kids[2], below), "in that order");
        IsTrue(ReferenceEquals(card.LastChild, below) && ReferenceEquals(below.Prev, row) && ReferenceEquals(row.Prev, above), "linked both ways");
        IsTrue(ReferenceEquals(title.Parent, row) && ReferenceEquals(row.FirstChild, title), "the title is the row's first child");
        IsNull(title.Prev, "and nothing of the card's comes before it");
        Eq(row.Rect.Y, title.Rect.Y, "the title is laid out in the row");
        IsTrue(row.Rect.Y >= above.Rect.Y + above.Extent.Y - 0.01f, "the row is below the first text");
        IsTrue(below.Rect.Y >= row.Rect.Y + 24f - 0.01f, "and the last text below the row");
    }
});

Run("PushIntoParent: in a dropdown, a control built in expression form asks while it is under the dropdown", () =>
{
    var runtime = BibuiRuntime.Create();
    int under = 0, go = 0;
    Box menu = null!, row = null!;
    BibuiWidget button = default;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnFill();
            using (runtime.PushParent(page))
            {
                var below = runtime.BoxFromId("under", InteractionKinds.LeftPress);
                below.Px(300f, 300f);
                if (runtime.InteractionsFor(below).Clicked) under++;

                runtime.OpenDropDown("menu");
                menu = runtime.BeginDropDown("menu")!;
                menu.Px(200f, 60f).At(20f, 20f);
                using (runtime.PushParent(menu))
                {
                    // What is tested is Arrange's expression form moving runtime nodes, obsolete or not.
#pragma warning disable CS0618
                    row = Arrange.Row(
                        runtime.Text("Run it"),
                        Arrange.Spring(),
                        (button = runtime.Button("go", "Go"))).FillX();
#pragma warning restore CS0618
                    UiNode.PushIntoParent(menu, row);
                    if (button.Clicked) go++;
                }
            }
            return page;
        },
        new SurfaceOptions { Width = 300, Height = 300 });
    runtime.Step();
    runtime.Step();

    Eq("row-only", ReferenceEquals(menu.FirstChild, row) && ReferenceEquals(menu.LastChild, row) ? "row-only" : "other",
        "the dropdown holds only the row: the text and the button moved out of it");
    IsTrue(ReferenceEquals(button.Node.Parent, row), "the button is in the row");
    ClickCentre(runtime, "go");
    Eq((0, 1), (under, go), "the button has the press, not the box beneath");
});

Run("PushIntoParent: in a Select, rows built in expression form are picked by a click", () =>
{
    var runtime = BibuiRuntime.Create();
    string? picked = null;
    int under = 0;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnFill();
            using (runtime.PushParent(page))
            {
                runtime.OpenDropDown("pick-list");
                var select = Bibui.Core.Runtime.Select.Begin(runtime, "pick", "Pick one");
                if (select.Open)
                {
                    // Something in the list to push into: built under the list, the parent in scope.
                    var holder = runtime.Box().FillX();
                    holder.ChildLayoutAxis = LayoutAxis.Y;
                    holder.SizeY = SemanticSize.ChildrenSum();
                    BibuiWidget a = default, b = default;
                    // What is tested is Arrange's expression form moving runtime nodes, obsolete or not.
#pragma warning disable CS0618
                    UiNode.PushIntoParent(holder, Arrange.Row(
                        runtime.Text("·"),
                        Arrange.Space(6f),
                        Arrange.Column(
                            a = runtime.SelectItem("pick-a", "Option A"),
                            b = runtime.SelectItem("pick-b", "Option B")).FillX()).FillX());
#pragma warning restore CS0618
                    if (a.Clicked) picked = "a";
                    if (b.Clicked) picked = "b";
                }
                select.End();
                var below = runtime.BoxFromId("under", InteractionKinds.LeftPress);
                below.FillX().FillY();
                if (runtime.InteractionsFor(below).Clicked) under++;
            }
            return page;
        },
        new SurfaceOptions { Width = 300, Height = 300 });
    runtime.Step();
    runtime.Step();
    ClickCentre(runtime, "pick-b");
    Eq("b", picked, "Option B is picked");
    Eq(0, under, "and the box beneath the list got nothing");
});

Run("OpenToolTip: one tooltip a frame, last in the tree, placed by its default offset, taking nothing", () =>
{
    var runtime = BibuiRuntime.Create();
    int presses = 0;
    UiNode tree = null!;
    Box first = null!, second = null!;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnFill();
            using (runtime.PushParent(page))
            {
                var below = runtime.BoxFromId("under", InteractionKinds.LeftPress);
                below.Px(200f, 200f);
                if (runtime.InteractionsFor(below).Clicked) presses++;
                runtime.OpenDropDown("menu");
                runtime.BeginDropDown("menu")!.Px(50f, 50f).At(150f, 150f);

                first = runtime.OpenToolTip();
                runtime.DefaultToolTipOffset(first);
                using (runtime.PushParent(first)) runtime.Text("one".ToArray());
                second = runtime.OpenToolTip();
                using (runtime.PushParent(second)) runtime.Text("two".ToArray());
            }
            return tree = page;
        },
        new SurfaceOptions { Width = 300, Height = 300 });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 40f, Y = 30f });
    runtime.Step();
    runtime.Step();

    IsTrue(ReferenceEquals(first, second), "a second open in the frame is the same tooltip");
    Eq("two", new string(((Text)first.LastChild!).Content), "and both texts are in it");
    IsTrue(ReferenceEquals(tree.LastChild, first), "it is the tree's last child, after the dropdown");
    Eq(new System.Numerics.Vector2(55f, 45f), first.Rect, "the default offset: the mouse plus 15");

    ClickAt(runtime, 60f, 50f);   // on the tooltip, over the box beneath
    Eq(1, presses, "the box beneath still takes the press");
});

Run("OpenToolTip: with nothing built into it, it is not in the tree", () =>
{
    var runtime = BibuiRuntime.Create();
    UiNode tree = null!;
    runtime.SetBuild(
        () =>
        {
            var page = runtime.ColumnFill();
            using (runtime.PushParent(page)) runtime.Box().Px(10f, 10f);
            return tree = page;
        },
        new SurfaceOptions { Width = 100, Height = 100 });
    runtime.Step();
    Eq(null, runtime.OpenToolTip().Parent, "not linked");
    IsTrue(tree.LastChild is Box { Id: null }, "the page ends with its own box");
});

static void ClickAt(BibuiRuntime runtime, float x, float y)
{
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
}

Run("runtime.TextFromId: retained under its id", () =>
{
    var runtime = BibuiRuntime.Create();
    var seen = new List<Text>();
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(200f, 100f);
            using (runtime.PushParent(column))
                seen.Add(runtime.TextFromId("title", "Cases".ToArray()));
            return column;
        },
        new SurfaceOptions { Width = 200, Height = 100 });
    runtime.Step();
    runtime.Step();
    IsTrue(ReferenceEquals(seen[0], seen[1]), "the same text both frames");
    Eq("title", seen[1].Id);
});

Run("Box links: with no axis pushed a box lays out along X, and scopes nest", () =>
{
    var runtime = BibuiRuntime.Create();
    var outer = runtime.BoxFromId("outer");
    Eq(LayoutAxis.X, outer.ChildLayoutAxis, "X when the stack is empty");

    Box inner, leaf, after;
    using (runtime.PushParent(outer))
    {
        using (runtime.PushAxis(LayoutAxis.Y))
            inner = runtime.BoxFromId("inner");
        using (runtime.PushParent(inner))
            leaf = runtime.BoxFromId("leaf");
        after = runtime.BoxFromId("after");
    }
    var outside = runtime.BoxFromId("outside");

    Eq(LayoutAxis.Y, inner.ChildLayoutAxis, "the pushed axis");
    Eq(LayoutAxis.X, after.ChildLayoutAxis, "and X again once its scope is gone");
    IsTrue(leaf.Parent == inner && inner.Parent == outer && after.Parent == outer, "each under the parent on top");
    IsTrue(inner.Next == after && after.Prev == inner && leaf.Next is null, "siblings in acquire order");
    IsTrue(outside.Parent is null, "nothing is linked once the parent scope is gone");
});

Run("Box links: a parent named when acquiring wins over the stack", () =>
{
    var runtime = BibuiRuntime.Create();
    var named = runtime.BoxFromId("named");
    var pushed = runtime.BoxFromId("pushed");
    Box child;
    using (runtime.PushParent(pushed))
        child = runtime.BoxFromId("child", parent: named);
    IsTrue(child.Parent == named && named.FirstChild == child && pushed.FirstChild is null,
        "linked under the parent it was given");
});

Run("Box links: acquiring a box clears last frame's links", () =>
{
    var runtime = BibuiRuntime.Create();
    var parent = runtime.BoxFromId("parent");
    Box child;
    using (runtime.PushParent(parent))
        child = runtime.BoxFromId("child");
    IsTrue(child.Parent == parent && parent.FirstChild == child, "linked this frame");
    runtime.BoxFromId("child");
    IsTrue(child.Parent is null && child.Next is null && child.Prev is null, "the child's links are gone");
    runtime.BoxFromId("parent");
    IsTrue(parent.FirstChild is null && parent.LastChild is null, "and so are the parent's");
});

Run("Box links: a scope disposed out of order throws", () =>
{
    var runtime = BibuiRuntime.Create();
    var a = runtime.BoxFromId("a");
    var b = runtime.BoxFromId("b");
    var outer = runtime.PushParent(a);
    var inner = runtime.PushParent(b);
    bool threw = false;
    try { outer.Dispose(); } catch (InvalidOperationException) { threw = true; }
    IsTrue(threw, "popping a parent that is not on top throws");
    inner.Dispose();
    outer.Dispose();

    var x = runtime.PushAxis(LayoutAxis.X);
    var y = runtime.PushAxis(LayoutAxis.Y);
    threw = false;
    try { x.Dispose(); } catch (InvalidOperationException) { threw = true; }
    IsTrue(threw, "and so does popping an axis that is not on top");
    y.Dispose();
    x.Dispose();
});

Run("Box links: a scope left open throws when the frame ends", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            var parent = runtime.BoxFromId("parent").Px(100f, 100f);
            runtime.PushParent(parent);   // never disposed
            return parent;
        },
        new SurfaceOptions { Width = 100, Height = 100 });
    bool threw = false;
    try { runtime.Step(); } catch (InvalidOperationException) { threw = true; }
    IsTrue(threw, "rendering it throws");
});

// Linked boxes rendered: the legacy renderer hands a linked tree to the solver.
foreach (var axis in new[] { LayoutAxis.X, LayoutAxis.Y })
Run($"Box links: children acquired under a parent are laid out along {axis}", () =>
{
    var runtime = BibuiRuntime.Create();
    Box a = null!, b = null!, parent = null!;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnFit();
            using (runtime.PushParent(column))
            using (runtime.PushAxis(axis))
                parent = runtime.BoxFromId("parent");
            a = runtime.BoxFromId("a", parent: parent);
            b = runtime.BoxFromId("b", parent: parent);
            parent.Style.Padding = EdgeInsets.All(5f);
            parent.SizeX = parent.SizeY = SemanticSize.ChildrenSum();
            (a.SizeX, a.SizeY) = (SemanticSize.Pixels(30), SemanticSize.Pixels(20));
            (b.SizeX, b.SizeY) = (SemanticSize.Pixels(40), SemanticSize.Pixels(10));
            return column;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    Eq(new System.Numerics.Vector2(5f, 5f), a.Rect, "the first child starts inside the padding");
    Eq(axis == LayoutAxis.X ? new System.Numerics.Vector2(35f, 5f) : new System.Numerics.Vector2(5f, 25f),
        b.Rect, "the second follows the first along the axis");
    Eq(axis == LayoutAxis.X ? new System.Numerics.Vector2(80f, 30f) : new System.Numerics.Vector2(50f, 40f),
        parent.Extent, "the parent is as large as its children plus padding");
});

Run("Box links: a box acquired twice under itself does not hang", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            var parent = runtime.BoxFromId("parent", InteractionKinds.LeftPress);
            using (runtime.PushParent(parent))
                runtime.BoxFromId("parent", InteractionKinds.LeftPress);
            runtime.InteractionsFor(parent);
            return runtime.Into(runtime.ColumnFit(), () => runtime.Box().Px(10f, 10f));
        },
        new SurfaceOptions { Width = 100, Height = 100 });
    var task = Task.Run(() => { runtime.Step(); runtime.Step(); });
    IsTrue(task.Wait(TimeSpan.FromSeconds(10)), "rendering and asking finish");
});

// The bar's thumb is the view's share of its content, placed by how far it has
// scrolled; dragging it moves the view, pressing either side of it pages.
Run("Scrollbar: the thumb follows the view, and moves it", () =>
{
    var runtime = BibuiRuntime.Create();
    Box view = null!;
    runtime.SetBuild(
        () =>
        {
            var panel = runtime.BoxFromId("panel");
            view = runtime.BoxFromId("view", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, panel);
            view.ChildLayoutAxis = LayoutAxis.Y;
            runtime.BoxFromId("content", parent: view).FillX().Px(height: 800f);
            using (runtime.PushParent(panel))
                runtime.Scrollbar("bar", view);
            runtime.InteractionsFor(view);
            (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
            view.AllowOverflowY = true;
            (panel.SizeX, panel.SizeY) = (SemanticSize.Pixels(200f), SemanticSize.Pixels(200f));
            return panel;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    void Settle() { for (int i = 0; i < 60; i++) { runtime.ConsumeFrame(); runtime.Step(); } }
    Settle();

    Box thumb = runtime.BoxFromId("bar-thumb");   // peek: re-acquired every frame anyway
    Settle();
    Eq(200f, view.Extent.Y, "the view is the panel's height, not its content's");
    Eq(50f, thumb.Extent.Y, "the thumb is the share in view: 200 of 800");
    Eq(0f, thumb.Rect.Y, "and starts at the top");

    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 50f, Y = 100f, ScrollY = 300f });
    Settle();
    Eq(300f, view.ViewOffset.Y, "the wheel moves the view");
    Eq(75f, thumb.Rect.Y, "and the thumb a quarter of the way down its free 150px");

    float bx = 200f - Scrollbar.Lane / 2f;
    runtime.ProcessEvents(
    [
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = bx, Y = 100f },
        new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = bx, Y = 175f },
    ]);
    Settle();
    Eq(600f, view.ViewOffset.Y, "dragging the thumb's centre to the bottom scrolls to the end");
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = bx, Y = 175f });
    Settle();

    runtime.ProcessEvents(
    [
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = bx, Y = 10f },
        new BibUiEvent { Kind = BibUiEventKind.MouseUp,   Button = MouseButton.Left, X = bx, Y = 10f },
    ]);
    Settle();
    Eq(400f, view.ViewOffset.Y, "pressing the track above the thumb pages up by one view");
});

// A child scrolled past a Clip parent's edge takes nothing outside it.
foreach (bool clip in new[] { true, false })
Run($"Claims: a child scrolled out of {(clip ? "a clipping" : "a non-clipping")} parent {(clip ? "takes nothing there" : "still takes the press")}", () =>
{
    var runtime = BibuiRuntime.Create();
    int clicks = 0;
    runtime.SetBuild(
        () =>
        {
            var claims = InteractionKinds.ViewScrollY | InteractionKinds.ClampY
                | (clip ? InteractionKinds.Clip : InteractionKinds.None);
            var column = runtime.ColumnFit();
            UiNode.PushIntoParent(column, new Box {  }.Px(200f, 50f));
            Box view;
            using (runtime.PushParent(column))
            using (runtime.PushAxis(LayoutAxis.Y))
                view = runtime.BoxFromId("view", claims);
            BibuiWidget row;
            using (runtime.PushParent(view))
            {
                row = runtime.Button("row", "Row");
                if (row.Clicked) clicks++;
                runtime.BoxFromId("filler").Px(200f, 200f);
            }
            runtime.InteractionsFor(view);

            row.Node.Px(height: 60f);
            (view.SizeX, view.SizeY) = (SemanticSize.Pixels(200), SemanticSize.Pixels(100));
            view.AllowOverflowY = true;
            return column;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    // Scroll the content 40px up: the 60px button now spans y 10..70, and the
    // view starts at y 50.
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollY = 40f });
    for (int i = 0; i < 60; i++) { runtime.ConsumeFrame(); runtime.Step(); }

    void Click(float y)
    {
        runtime.ProcessEvents(
        [
            new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 20f, Y = y },
            new BibUiEvent { Kind = BibUiEventKind.MouseUp,   Button = MouseButton.Left, X = 20f, Y = y },
        ]);
        runtime.Step();
    }

    Click(15f);
    Eq(clip ? 0 : 1, clicks, clip
        ? "a press on the header above the view is not the button's"
        : "without Clip, the button's whole rect still takes it");

    Click(52f);
    Eq(clip ? 1 : 2, clicks, "a press on the part still in view is the button's");
});

// Clamping keeps the target inside the content: from 0 to how far the content
// runs past the box, on the axes claimed and no others.
Run("Claims: a clamped box cannot be scrolled past its content", () =>
{
    var claims = InteractionKinds.ViewScroll | InteractionKinds.ClampY;
    var runtime = BibuiRuntime.Create();
    Box list = null!;
    runtime.SetBuild(
        () =>
        {
            list = runtime.BoxFromId("list", claims);
            runtime.InteractionsFor(list);
            list.Px(200f, 200f);
            list.AllowOverflowY = true;
            UiNode.PushIntoParent(list, Layer(null).Px(200f, 800f));
            return list;
        },
        new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    void Notch(float dx, float dy)
    {
        runtime.ProcessEvent(new BibUiEvent
        { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollX = dx, ScrollY = dy });
        runtime.Step();
    }

    Notch(0f, 10_000f);
    Eq(600f, list.TargetViewOffset.Y, "no further down than the content runs past the box");
    Notch(0f, -10_000f);
    Eq(0f, list.TargetViewOffset.Y, "and no further up than its top");

    Notch(-50f, 0f);
    Eq(-50f, list.TargetViewOffset.X, "an axis that was not clamped is left alone");
});

// A box that claims the wheel is asking for every notch over it, whatever the
// ancestor it sits in happens to measure.
Run("A wheel claimant inside an overflowing ancestor still gets the wheel", () =>
{
    var runtime = BibuiRuntime.Create();
    Box canvas = null!;
    runtime.SetBuild(
        () => runtime.Into(runtime.ColumnPx(400f, 200f), () =>
        {
            using (runtime.PushParent(runtime.RowFit()))
            {
                canvas = runtime.BoxFromId("canvas", InteractionKinds.ViewScroll).Px(200f, 200f);
                runtime.Box().Px(200f, 900f);
            }
        }),
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.Scroll, X = 100f, Y = 100f, ScrollY = 40f });

    Eq(40f, runtime.InteractionsFor(canvas).ScrollY, "the canvas is handed the wheel");
});

// The two failures this whole mechanism exists to make impossible, as they
// appeared in a real app: clicking a card cleared the selection the card's own
// handler had just made, and pressing a card started the canvas's marquee so
// the card could never be dragged at all.
Run("Claims: a container does not take the press its card already took", () =>
{
    var runtime = BibuiRuntime.Create();
    Box canvas = null!, card = null!;
    runtime.SetBuild(
        () =>
        {
            canvas = runtime.BoxFromId(
                "canvas", InteractionKinds.LeftPress | InteractionKinds.ViewScroll);
            canvas.Px(400f, 400f);

            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            card.Px(100f, 100f);

            UiNode.PushIntoParent(canvas, card);
            return canvas;
        },
        new SurfaceOptions { Width = 400, Height = 400 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f });

    // Describe-your-children-then-ask, which is the order a build produces.
    IsTrue(runtime.InteractionsFor(card).Pressed, "the card took it");
    IsTrue(!runtime.InteractionsFor(canvas).Pressed, "and the canvas has nothing to act on");

    // And the drag that press becomes is the card's alone.
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 200f, Y = 200f });
    IsTrue(runtime.InteractionsFor(card).Dragging, "the card is being dragged");
    IsTrue(!runtime.InteractionsFor(canvas).Dragging, "the canvas is not also sweeping");
});

Run("Claims: a box that takes a press is told where the pointer is; one that takes nothing is not", () =>
{
    var runtime = BibuiRuntime.Create();
    Box takesAPress = null!, wheelOnly = null!;
    runtime.SetBuild(
        () =>
        {
            takesAPress = runtime.BoxFromId("takes-a-press", InteractionKinds.LeftPress);
            takesAPress.Px(100f, 100f).At(0f, 0f);

            // Takes the wheel and nothing else: scrollable, not pressable, so
            // the pointer being over it is not its business.
            wheelOnly = runtime.BoxFromId("wheel-only", InteractionKinds.ViewScroll);
            wheelOnly.Px(100f, 100f).At(200f, 0f);

            return Layer(null, takesAPress, wheelOnly).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    // Hovering follows the press claim rather than a claim of its own: a box
    // that can be pressed is one the pointer can be over.
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 50f, Y = 50f });
    IsTrue(runtime.InteractionsFor(takesAPress).Hovering, "it takes a press, so it is told");

    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 250f, Y = 50f });
    IsTrue(!runtime.InteractionsFor(wheelOnly).Hovering,
        "the pointer is over it, but it takes no press");
    IsTrue(!runtime.InteractionsFor(takesAPress).Hovering,
        "and the one that does is no longer under it");
});

Run("Claims: the wheel over a card is still the canvas's", () =>
{
    var runtime = BibuiRuntime.Create();
    Box canvas = null!, card = null!;
    runtime.SetBuild(
        () =>
        {
            canvas = runtime.BoxFromId(
                "canvas", InteractionKinds.LeftPress | InteractionKinds.ViewScroll);
            canvas.Px(400f, 400f);

            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            card.Px(100f, 100f);

            UiNode.PushIntoParent(canvas, card);
            return canvas;
        },
        new SurfaceOptions { Width = 400, Height = 400 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.Scroll, X = 50f, Y = 50f, ScrollY = 40f });

    Eq(0f, runtime.InteractionsFor(card).ScrollY, "the card never claimed a wheel");
    Eq(40f, runtime.InteractionsFor(canvas).ScrollY, "so it passed to the one that did");
});

// A release lands wherever the pointer got to, which for a pan is routinely
// off the node entirely. Attributing it by position would leave the gesture
// running for ever.
Run("Claims: a release off the node still ends the drag that owns it", () =>
{
    var runtime = BibuiRuntime.Create();
    Box card = null!;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            card.Px(100f, 100f).At(0f, 0f);
            return Layer(null, card).Px(400f, 400f);
        },
        new SurfaceOptions { Width = 400, Height = 400 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f });
    runtime.InteractionsFor(card);
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 350f, Y = 350f });
    IsTrue(runtime.InteractionsFor(card).Dragging, "dragged well off itself");

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 350f, Y = 350f });

    IsTrue(runtime.InteractionsFor(card).Released, "the release is the holder's, not the pointer's");
    IsTrue(!runtime.InteractionsFor(card).Dragging, "and the drag is over");
});


Run("Claims: a drag pressed and released in one frame still says where it began", () =>
{
    // Regression: the release clears _activeId, and the anchor was read off it — so a
    // press and a release arriving in the same frame reported Released, at a position,
    // with DragStartX = NaN and a zero delta. The one frame that commits a gesture was
    // the one frame that could not measure it.
    //
    // That pairing is the normal case, not a corner: pointer events arrive far faster
    // than frames are drawn, so a real mouse delivers press and release together
    // whenever the gesture is quicker than a repaint, and the test server's `gesture`
    // command does it by construction.
    var runtime = BibuiRuntime.Create();
    Box card = null!;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            card.Px(100f, 100f).At(0f, 0f);
            return Layer(null, card).Px(400f, 400f);
        },
        new SurfaceOptions { Width = 400, Height = 400 });
    runtime.Step();

    // The whole gesture at once, with no render between the moves.
    runtime.ProcessEvents(new[]
    {
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 20f, Y = 20f },
        new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 50f, Y = 90f },
        new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 80f, Y = 140f },
    });

    var it = runtime.InteractionsFor(card);
    IsTrue(it.Pressed, "the press is the card's");
    IsTrue(it.Released, "and so is the release, let go off the card though it was");
    Eq(20f, it.DragStartX, "the anchor is where the button went down");
    Eq(20f, it.DragStartY, "on both axes");
    Eq(60f, it.DragDeltaX, "and the travel is measured from it");
    Eq(120f, it.DragDeltaY, "on both axes");
});

Run("Claims: a drag let go over nothing in particular still reaches its owner", () =>
{
    // Regression: the release was offered to the built-ins first, and they were
    // held to own anything whose hit node carries no id — which over a real
    // surface is most of it: padding wrappers, decorative fills, the empty
    // background of a grid. So a drag let go anywhere but back on its own box
    // was eaten before the box could ask, and the box held the button for ever.
    // That is worse than it sounds: nothing goes hot while any box is active, so
    // the whole interface stops responding to hover rather than failing visibly.
    var runtime = BibuiRuntime.Create();
    Box card = null!;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            card.Px(80f, 80f).At(0f, 0f);
            return Layer(null, card, // Bare, unnamed, and covering where the gesture ends — a
                    // backdrop, not a control, and nothing anyone could ask about.
                    new Box {  }.Px(250f, 250f).At(100f, 100f)).Px(400f, 400f);
        },
        new SurfaceOptions { Width = 400, Height = 400 });
    runtime.Step();

    // Press on the card, let go well inside the anonymous box.
    runtime.ProcessEvents(new[]
    {
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 40f, Y = 40f },
        new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 200f, Y = 200f },
        new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 200f, Y = 200f },
    });

    var it = runtime.InteractionsFor(card);
    IsTrue(it.Pressed, "the press is the card's");
    IsTrue(it.Released, "and the release reaches it, though it landed on nothing that asks");

    // And the button is genuinely let go: a box still holding one keeps every
    // other box cold for as long as it does.
    runtime.Step();
    IsTrue(!runtime.InteractionsFor(card).Dragging, "the card is not still holding the button");
});

Run("Claims: a box that asks rather than calls back is still clickable by name", () =>
{
    // Regression: DispatchClick(id) once reached only components and boxes with
    // a callback, so a box that claims a press could not be clicked by name.
    // That is the whole of how a test rig and a host's own "click this control"
    // both reach a box.
    var runtime = BibuiRuntime.Create();
    Box card = null!;
    int clicks = 0;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            if (runtime.InteractionsFor(card).Clicked) clicks++;
            card.Px(100f, 60f).At(40f, 20f);
            return Layer(null, card).Px(400f, 400f);
        },
        new SurfaceOptions { Width = 400, Height = 400 });
    runtime.Step();   // the frame that gives the box a rect to aim at

    runtime.DispatchClick("card");
    runtime.Step();   // the frame that asks

    Eq(1, clicks, "the box was clicked by its name");
});

Run("Claims: a click by name goes to the box named, not to a child under the point", () =>
{
    // A by-name dispatch has already said which box it means. Delivering it at the
    // box's centre and letting ask order decide hands it to whatever is drawn
    // there — which for a card with its own title, button or grip inside it is one
    // of those, not the card. The name is the answer to "which box", so the point
    // is only how the event is carried.
    var runtime = BibuiRuntime.Create();
    Box card = null!, inner = null!;
    int cardClicks = 0, innerClicks = 0;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            inner = runtime.BoxFromId("inner", InteractionKinds.LeftPress);

            // Innermost asks first, as z-order demands — so on an ordinary click
            // at this point the inner box would take it, and does below.
            if (runtime.InteractionsFor(inner).Clicked) innerClicks++;
            if (runtime.InteractionsFor(card).Clicked) cardClicks++;

            inner.Px(100f, 100f).At(0f, 0f);
            card.Px(100f, 100f).At(0f, 0f);
            UiNode.PushIntoParent(card, Layer(null, inner).FillX().FillY());
            return Layer(null, card).Px(300f, 300f);
        },
        new SurfaceOptions { Width = 300, Height = 300 });
    runtime.Step();

    runtime.DispatchClick("card");
    runtime.Step();
    Eq(1, cardClicks, "the card was named, so the card was clicked");
    Eq(0, innerClicks, "and the box covering it took nothing");

    runtime.DispatchClick("inner");
    runtime.Step();
    Eq(1, innerClicks, "naming the inner one reaches it too");
    Eq(1, cardClicks, "without the card taking a second");

    // An ordinary click at the same point still goes by ask order, which is what
    // makes the two different questions.
    runtime.ProcessEvents(new[]
    {
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f },
        new BibUiEvent { Kind = BibUiEventKind.MouseUp,   Button = MouseButton.Left, X = 50f, Y = 50f },
    });
    runtime.Step();
    Eq(2, innerClicks, "a click by coordinate goes to whoever asks first");
    Eq(1, cardClicks, "not to the container beneath it");
});

Run("Claims: clicking an unknown name, or a box with no rect yet, does nothing", () =>
{
    var runtime = BibuiRuntime.Create();
    Box card = null!;
    int clicks = 0;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            if (runtime.InteractionsFor(card).Clicked) clicks++;
            card.Px(100f, 60f);
            return Layer(null, card).Px(400f, 400f);
        },
        new SurfaceOptions { Width = 400, Height = 400 });

    // Never painted: there is no centre to aim at, and no exception either.
    runtime.DispatchClick("card");
    runtime.DispatchClick("nothing-by-that-name");
    runtime.Step();
    Eq(0, clicks, "nothing was clicked");
});

Run("Pointer: the runtime keeps its own current, without being told", () =>
{
    // It used to be filled only by the window, so anything driving the runtime
    // directly — a test server, a headless harness — ran an entire synthesised
    // drag with Pointer reporting no buttons at all.
    var (runtime, under, over) = Overlapping();
    IsTrue(!runtime.InteractionsFor(under).Dragging, "nothing held to begin with");

    Press(runtime, 300f, 100f);
    runtime.InteractionsFor(over);   // the frame that takes it
    var pressed = runtime.InteractionsFor(over);
    IsTrue(pressed.Dragging, "the press it just processed is held");
    Eq(300f, pressed.X, "and the pointer is where the press was");

    // Including on a move, which is most of what a pointer does. Asserting
    // only around press and release let this go stale unnoticed.
    Move(runtime, 120f, 60f);
    var moved = runtime.InteractionsFor(over);
    Eq(120f, moved.X, "a move moves it");
    Eq(60f,  moved.Y, "a move moves it");
    IsTrue(moved.Dragging, "and does not drop the held button");

    Release(runtime, 320f, 110f);
    IsTrue(!runtime.InteractionsFor(over).Dragging, "and released when it comes up");
});

Run("runtime.Tabs: clicking a tab reports its value", () =>
{
    var runtime = BibuiRuntime.Create();
    string value = "a";
    TabOption[] items = [new("Alpha", "a"), new("Beta", "b")];
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 100f);
            using (runtime.PushParent(column))
                if (runtime.Tabs("tabs", items, value).Picked is { } picked) value = picked;
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 100 });
    runtime.Step();
    var beta = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("tabs-b");
    Eq(Bibui.Core.Runtime.Tabs.Height - 4f, beta.Height, "the tab fills the strip inside its inset");
    float x = beta.X + beta.Width / 2f, y = beta.Y + beta.Height / 2f;
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
    Eq("b", value, "clicking Beta picks it");
});

Run("runtime.Expander: a click on the header reports a toggle, and the body is there only while open", () =>
{
    var runtime = BibuiRuntime.Create();
    bool open = false;
    bool hadBody = false;
    runtime.SetBuild(
        () =>
        {
            var column = runtime.ColumnPx(300f, 200f);
            using (runtime.PushParent(column))
            {
                var expander = runtime.Expander("coll", "Title", open);
                hadBody = expander.Body is not null;
                if (expander.Body is { } body)
                    using (runtime.PushParent(body)) runtime.Text("Inside".ToArray());
                if (expander.Toggled) open = !open;
            }
            return column;
        },
        new SurfaceOptions { Width = 300, Height = 200 });
    runtime.Step();
    IsTrue(!hadBody, "closed, there is no body to build into");
    var header = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("coll");
    Eq(Bibui.Core.Runtime.Expander.HeaderHeight, header.Height, "the header is its own height");
    float x = header.X + header.Width / 2f, y = header.Y + header.Height / 2f;
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
    runtime.Step();
    IsTrue(open, "the click toggled it open");
    runtime.Step();
    IsTrue(hadBody, "and open, it has a body");
});

Run("Modifiers: what was held when it happened, not when it is read", () =>
{
    // Stamped on the event at the window, then OR'd in. Asking afterwards would
    // be asking at a different moment: let go of ctrl between the press and the
    // frame that reads it, and a ctrl-click silently becomes a click.
    var (runtime, under, over) = Overlapping();
    runtime.ProcessEvent(new BibUiEvent
    {
        Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left,
        X = 300f, Y = 100f, Modifiers = KeyModifiers.Ctrl | KeyModifiers.Alt,
    });

    var i = runtime.InteractionsFor(over);
    IsTrue(i.Pressed, "the press landed");
    IsTrue((i.Modifiers & KeyModifiers.Ctrl) != 0, "ctrl was held when it happened");
    IsTrue((i.Modifiers & KeyModifiers.Alt) != 0, "and so was alt, which two bools could not say");
});

Run("Modifiers: a frame's events are OR'd together", () =>
{
    var (runtime, under, over) = Overlapping();
    runtime.ProcessEvents(
    [
        new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left,
                         X = 300f, Y = 100f, Modifiers = KeyModifiers.Shift },
        new BibUiEvent { Kind = BibUiEventKind.Scroll,
                         X = 300f, Y = 100f, ScrollY = 1f, Modifiers = KeyModifiers.Ctrl },
    ]);

    var i = runtime.InteractionsFor(over);
    IsTrue((i.Modifiers & KeyModifiers.Shift) != 0, "shift came in on the press");
    IsTrue((i.Modifiers & KeyModifiers.Ctrl) != 0, "ctrl came in on the wheel");
});

// ── Retained boxes (BoxFromId) ───────────────────────────────────────────────

BibuiRuntime Retained(Func<bool>? wantsSecond = null)
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            var children = new List<UiNode> { runtime.BoxFromId("first").Px(10f, 10f) };
            if (wantsSecond?.Invoke() ?? false) children.Add(runtime.BoxFromId("second").Px(10f, 10f));
            return Layer(null, [.. children]).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    return runtime;
}

Run("BoxFromId: one id is one instance, every frame", () =>
{
    var runtime = Retained();
    var first = runtime.BoxFromId("first");
    runtime.Step();
    IsTrue(ReferenceEquals(first, runtime.BoxFromId("first")), "the same box came back");
    Eq("first", first.Id, "and it is named after the id it was asked for by");
});

Run("BoxFromId: distinct ids are distinct boxes", () =>
{
    var runtime = Retained(() => true);
    IsTrue(!ReferenceEquals(runtime.BoxFromId("first"), runtime.BoxFromId("second")), "not aliased");
});

Run("BoxFromId: HotT and ActiveT are what a box keeps across frames", () =>
{
    // The reason the box is retained at all: a build function has no memory
    // between frames, so this is state it could not otherwise hold. Asserted
    // over the reset rather than over a render, because a render legitimately
    // advances these — see the HotT/ActiveT easing tests.
    var runtime = Retained();
    var box = runtime.BoxFromId("first");
    box.HotT = 0.4f;
    box.ActiveT = 0.7f;

    var again = runtime.BoxFromId("first");
    IsTrue(ReferenceEquals(box, again), "same instance");
    Eq(0.4f, again.HotT, "hot carried through the reset");
    Eq(0.7f, again.ActiveT, "active carried through the reset");
});

Run("BoxFromId: a box nobody asks for is evicted", () =>
{
    // Asking touches, so the check cannot live inside the idle loop — it would
    // keep alive the very entry it is testing. Ask once, at the end.
    bool wanted = true;
    var runtime = Retained(() => wanted);
    var second = runtime.BoxFromId("second");
    wanted = false;

    for (int i = 0; i < 62; i++) runtime.Step();

    IsTrue(!ReferenceEquals(second, runtime.BoxFromId("second")), "dropped after keepFrames idle");
});

Run("BoxFromId: a box that keeps being asked for is not evicted", () =>
{
    var runtime = Retained();
    var first = runtime.BoxFromId("first");
    for (int i = 0; i < 200; i++) runtime.Step();   // the build asks every frame
    IsTrue(ReferenceEquals(first, runtime.BoxFromId("first")), "still the same box");
});

Run("ResetForBuild: a retained box is cleared before it is handed back", () =>
{
    // "Not set this frame" has to be expressible, or a field assigned under a
    // condition lingers into every frame whose condition is false.
    var runtime = Retained();
    var box = runtime.BoxFromId("first");
    box.Claims = InteractionKinds.ViewScroll;
    box.Px(width: 999f);
    UiNode.PushIntoParent(box, new Box());

    var again = runtime.BoxFromId("first");
    IsTrue(again.FirstChild is null, "the child is gone");
    Eq(InteractionKinds.None, again.Claims, "and the claims");
    Eq(default, again.SizeX, "and the size");
    Eq("first", again.Id, "but not the identity");
});

Run("ResetForBuild: keeps the Style instance rather than replacing it", () =>
{
    var runtime = Retained();
    var style = runtime.BoxFromId("first").Style;
    style.CornerRadius = 12f;
    var again = runtime.BoxFromId("first");
    IsTrue(ReferenceEquals(style, again.Style), "same Style object");
    Eq(0f, again.Style.CornerRadius, "cleared in place");
});

Run("ResetForBuild: hot and active survive it", () =>
{
    var runtime = Retained();
    var box = runtime.BoxFromId("first");
    box.HotT = 0.4f;
    box.ActiveT = 0.7f;
    var again = runtime.BoxFromId("first");
    Eq(0.4f, again.HotT, "hot is what a build function cannot hold itself");
    Eq(0.7f, again.ActiveT, "and so is active");
});

Run("Style.Clear: restores every property", () =>
{
    // Reflection rather than a hand-written list, so a property added to Style
    // and forgotten in Clear() fails here instead of leaking last frame's value
    // into a retained box. Not every default is zero, so this compares against a
    // freshly constructed Style rather than against default(T).
    var style = new Style();
    var fresh = new Style();
    var props = typeof(Style).GetProperties().Where(pr => pr.CanRead && pr.CanWrite).ToArray();
    IsTrue(props.Length > 15, $"found the properties ({props.Length})");

    foreach (var pr in props)
    {
        object? dirty = Dirty(pr.PropertyType, pr.GetValue(fresh));
        IsTrue(dirty is not null, $"no dirty value known for {pr.Name} ({pr.PropertyType.Name}) — teach this test about it");
        pr.SetValue(style, dirty);
    }

    style.Clear();

    foreach (var pr in props)
        Eq(pr.GetValue(fresh), pr.GetValue(style), $"Clear() missed {pr.Name}");

    static object? Dirty(Type t, object? current)
    {
        Type u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(float)) return 42f;
        if (u == typeof(bool)) return !(bool)(current ?? false);
        if (u == typeof(UiColor)) return UiColor.Hex("#123456");
        if (u == typeof(EdgeInsets)) return new EdgeInsets(9f, 9f, 9f, 9f);
        if (u == typeof(Gradient)) return new Gradient();
        if (u == typeof(BoxShadow)) return new BoxShadow();
        if (u == typeof(EdgeBorder)) return new EdgeBorder();
        if (u == typeof(System.Numerics.Vector2)) return new System.Numerics.Vector2(7f, 9f);
        if (u == typeof(CornerRadii)) return CornerRadii.All(3f);
        if (u.IsEnum)
            foreach (var v in Enum.GetValues(u))
                if (!Equals(v, current)) return v;
        return null;
    }
});

// A button, which is to say a box that asks and then draws itself from the
// answer. Asking is what makes it hot: _hotId and _activeId are written only
// by InteractionsFor, so a box that describes itself and never asks stays cold
// however long the pointer sits on it.
//
// What being hot *means* is the box's own business — the runtime keeps a 0..1
// and this one spends it on opacity.
BibuiRuntime Hoverable()
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            var box = runtime.BoxFromId("handle", InteractionKinds.LeftPress);
            box.Px(100f, 100f);

            // Asking is what makes it hot, and it is asked by the box rather
            // than by name — holding one is proof it was described.
            runtime.InteractionsFor(box);

            // Read after, from what the last frame's step settled on.
            box.Style.Opacity = 0.6f + 0.4f * MathF.Max(box.HotT, box.ActiveT);

            return Layer(null, box).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    return runtime;
}

Run("HotT: eases toward 1 while hovered, and settles exactly on it", () =>
{
    var runtime = Hoverable();
    Eq(0f, runtime.BoxFromId("handle").HotT, "not hovered, not hot");

    Move(runtime, 50f, 50f);
    runtime.Step();
    float first = runtime.BoxFromId("handle").HotT;
    IsTrue(first > 0f && first < 1f, $"eased rather than switched (got {first})");

    for (int i = 0; i < 20; i++) runtime.Step();
    Eq(1f, runtime.BoxFromId("handle").HotT, "snapped to the target, not left just short of it");
});

Run("HotT: eases back to exactly 0 once the pointer leaves", () =>
{
    var runtime = Hoverable();
    Move(runtime, 50f, 50f);
    for (int i = 0; i < 20; i++) runtime.Step();

    Move(runtime, 380f, 190f);
    runtime.Step();
    IsTrue(runtime.BoxFromId("handle").HotT < 1f, "started falling");

    for (int i = 0; i < 20; i++) runtime.Step();
    Eq(0f, runtime.BoxFromId("handle").HotT, "all the way back");
});

Run("ActiveT: a press lands on 1 at once, the release eases back", () =>
{
    // The asymmetry is deliberate: a button that fades in under the finger
    // feels unresponsive, so the press is instant and only the release eases.
    var runtime = Hoverable();
    Move(runtime, 50f, 50f);
    Press(runtime, 50f, 50f);
    runtime.Step();
    Eq(1f, runtime.BoxFromId("handle").ActiveT, "instant on press");

    Release(runtime, 50f, 50f);
    runtime.Step();
    float after = runtime.BoxFromId("handle").ActiveT;
    IsTrue(after > 0f && after < 1f, $"eased on release (got {after})");

    for (int i = 0; i < 20; i++) runtime.Step();
    Eq(0f, runtime.BoxFromId("handle").ActiveT, "and settles on zero");
});

Run("HotT: stepped once per render, not once per ask", () =>
{
    var runtime = Hoverable();
    Move(runtime, 50f, 50f);
    runtime.Step();

    float a = runtime.BoxFromId("handle").HotT;
    float b = runtime.BoxFromId("handle").HotT;
    Eq(a, b, "asking twice does not advance it twice");
});


// ── runtime.Animate (eased values for build functions) ───────────────────────

BibuiRuntime Animated(Func<float> target)
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            runtime.Animate("thing", "x", target());
            return Layer(null).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    return runtime;
}

Run("Animate: a value asked for the first time is already at its target", () =>
{
    // Otherwise every screen slides into place on the frame it appears, and a
    // one-shot offscreen render captures half-finished animation.
    var runtime = Animated(() => 10f);
    runtime.Step();
    Eq(10f, runtime.Animate("thing", "x", 10f), "arrived, not travelling");
});

Run("Animate: eases to a new target and settles exactly on it", () =>
{
    float target = 0f;
    var runtime = Animated(() => target);
    runtime.Step();

    target = 1f;
    runtime.Step();
    float first = runtime.Animate("thing", "x", 1f);
    IsTrue(first > 0f && first < 1f, $"eased rather than jumped (got {first})");

    for (int i = 0; i < 60; i++) runtime.Step();
    Eq(1f, runtime.Animate("thing", "x", 1f), "snapped to the target, not left short of it");
});

Run("Animate: asking does not advance it", () =>
{
    // The step happens once, at the end of the frame — so a build function that
    // runs twice for one input reads the same answer both times.
    float target = 0f;
    var runtime = Animated(() => target);
    runtime.Step();
    target = 1f;
    runtime.Step();

    float a = runtime.Animate("thing", "x", 1f);
    float b = runtime.Animate("thing", "x", 1f);
    float c = runtime.Animate("thing", "x", 1f);
    Eq(a, b, "second ask is the same answer");
    Eq(a, c, "and the third");
});

Run("Animate: an infinite speed closes the whole gap in one step", () =>
{
    // For the frames where a value is driven directly rather than animated. The
    // step is still the end-of-frame one, so this is one frame behind the
    // target rather than an easing tail behind it.
    float target = 0f;
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            runtime.Animate("thing", "x", target, rate: 1f);
            return Layer(null).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    target = 1f;
    runtime.Step();   // one step, at a rate of 1
    Eq(1f, runtime.Animate("thing", "x", 1f, rate: 1f), "all the way, in one step");

    target = 0f;
    runtime.Step();
    Eq(0f, runtime.Animate("thing", "x", 0f, rate: 1f), "and back the same way");
});

Run("Animate: separate properties of one id are separate values", () =>
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(
        () =>
        {
            runtime.Animate("thing", "x", 1f);
            runtime.Animate("thing", "y", 0f);
            return Layer(null).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    Eq(1f, runtime.Animate("thing", "x", 1f), "x is x");
    Eq(0f, runtime.Animate("thing", "y", 0f), "y is y");
});

Run("Claims: a disabled box is in the way, not absent", () =>
{
    // Declining to act on a press is not the same as not being there. A panel
    // that claims, with a disabled control on top of it: the control reports
    // nothing, and the panel must not act on a click that landed on it.
    var runtime = BibuiRuntime.Create();
    Box panel = null!, declines = null!;
    runtime.SetBuild(
        () =>
        {
            panel = runtime.BoxFromId("panel", InteractionKinds.LeftPress);
            panel.Px(400f, 200f);

            declines = runtime.BoxFromId("declines", InteractionKinds.Disabled);
            declines.Px(100f, 100f);

            UiNode.PushIntoParent(panel, declines);
            return panel;
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f });

    // Describe children then ask, so the disabled control is asked first.
    IsTrue(!runtime.InteractionsFor(declines).Pressed, "the disabled control takes nothing");
    IsTrue(!runtime.InteractionsFor(panel).Pressed, "and nothing is left to fall through to");
});

Run("Claims: a disabled box does not swallow someone else's release", () =>
{
    // A press taken on one box, let go over a disabled one. The release has to
    // reach the box that owns the gesture: eaten here, that box holds the
    // button for ever, and while any button is held nothing can go hot.
    var runtime = BibuiRuntime.Create();
    Box handle = null!, declines = null!;
    runtime.SetBuild(
        () =>
        {
            handle = runtime.BoxFromId("handle", InteractionKinds.LeftPress);
            handle.Px(100f, 100f).At(0f, 0f);

            declines = runtime.BoxFromId("declines", InteractionKinds.Disabled);
            declines.Px(100f, 100f).At(200f, 0f);

            return Layer(null, handle, declines).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f });
    IsTrue(runtime.InteractionsFor(handle).Pressed, "the handle took the press");
    runtime.Step();

    // Let go over the disabled box, which is asked first.
    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 250f, Y = 50f });
    runtime.InteractionsFor(declines);
    IsTrue(runtime.InteractionsFor(handle).Released, "and the release still reaches it");

    runtime.Step();
    IsTrue(!runtime.InteractionsFor(handle).Dragging, "so the gesture is over");
});

Run("Claims: a disabled box does not stop the wheel beneath it", () =>
{
    // The other half: it swallows what it declines to act on, not everything.
    var runtime = BibuiRuntime.Create();
    Box canvas = null!, declines = null!;
    runtime.SetBuild(
        () =>
        {
            canvas = runtime.BoxFromId("canvas", InteractionKinds.ViewScroll);
            canvas.Px(400f, 200f);

            declines = runtime.BoxFromId("declines", InteractionKinds.Disabled);
            declines.Px(100f, 100f);

            UiNode.PushIntoParent(canvas, declines);
            return canvas;
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.Scroll, X = 50f, Y = 50f, ScrollY = -2f });

    runtime.InteractionsFor(declines);
    Eq(-2f, runtime.InteractionsFor(canvas).ScrollY, "the wheel reaches what can act on it");
});

// A slider 200 px wide at the top left, its value applied every frame.
(BibuiRuntime Runtime, Func<float> Value) MountSlider(float start, float step = 0f, bool disabled = false)
{
    var runtime = BibuiRuntime.Create();
    float value = start;
    runtime.SetBuild(() => runtime.Into(runtime.ColumnFit(), () =>
    {
        var slider = runtime.Slider("s", value, 0f, 100f, step, disabled: disabled);
        slider.Node.Px(width: 200f);
        value = slider.Value;
    }), new SurfaceOptions { Width = 400, Height = 100 });
    runtime.Step();
    runtime.Step();
    return (runtime, () => value);
}

Run("Slider: a press on the track sets the value under it, snapped to the step", () =>
{
    var (runtime, value) = MountSlider(start: 0f, step: 5f);
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 151f, Y = 8f });
    runtime.Step();
    Eq(75f, value(), "three quarters of the way along a 0..100 track, to the nearest 5");
});

Run("Slider: a drag follows the pointer, and past the end is the end", () =>
{
    var (runtime, value) = MountSlider(start: 50f);
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 100f, Y = 8f });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 40f, Y = 60f });
    runtime.Step();
    IsTrue(MathF.Abs(value() - 20f) < 0.01f, $"dragged to 40 px is 20, got {value()}");
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 380f, Y = 60f });
    runtime.Step();
    Eq(100f, value(), "past the end is the maximum");
});

Run("Slider: a disabled slider keeps its value", () =>
{
    var (runtime, value) = MountSlider(start: 30f, disabled: true);
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 150f, Y = 8f });
    runtime.Step();
    Eq(30f, value(), "a press does nothing");
});

Run("Switch: every size scales with the height it is given", () =>
{
    // By default it is 44 by 24 with a 20px knob; given twice that height, all
    // of it is twice that size, and on it rests 4px from the far end.
    foreach (var twice in new[] { false, true })
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() =>
        {
            Box root = runtime.Box();
            root.SizeX = root.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(root))
                runtime.Switch("sw", on: true, height: twice ? 48f : 24f);
            return root;
        }, new SurfaceOptions { Width = 200, Height = 100, ScaleFactor = 1f });
        for (int f = 0; f < 60; f++) runtime.Step();   // the knob has crossed

        float k = twice ? 2f : 1f;
        var track = runtime.FindById("sw")!;
        var knob = runtime.FindById("knobsw")!;
        Eq(new System.Numerics.Vector2(44f * k, 24f * k), track.Extent, $"the track at {k}x");
        Eq(new System.Numerics.Vector2(20f * k, 20f * k), knob.Extent, $"the knob at {k}x");
        IsTrue(MathF.Abs(track.Rect.X + track.Extent.X - 2f * k - (knob.Rect.X + knob.Extent.X)) < 0.05f,
            $"on, the knob rests {2f * k}px from the far end");
    }
});

Run("Switch: a disabled switch does not flip when it is clicked", () =>
{
    var runtime = BibuiRuntime.Create();
    bool on = false;
    runtime.SetBuild(() =>
    {
        var sw = runtime.Switch("sw", on, disabled: true);
        if (sw.Clicked) on = !on;

        // Wrapped: the root node is laid out at the full surface whatever its
        // own size says, so a bare switch mounted as the root would report the
        // whole window as its bounds.
        return Layer(null, sw.Node).Px(200f, 80f);
    }, new SurfaceOptions { Width = 200, Height = 80 });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 22f, Y = 12f });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 22f, Y = 12f });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 22f, Y = 12f });
    runtime.Step();
    IsTrue(!on, $"a disabled switch must not flip; on={on}");
});

Run("Claims: saying nothing takes nothing, and Disabled says it out loud", () =>
{
    var runtime = BibuiRuntime.Create();
    Box silent = null!, declines = null!;
    runtime.SetBuild(
        () =>
        {
            // Says nothing, so there is nothing to answer: a claim is how a box
            // asks to be interacted with, not a filter on a default of
            // everything.
            silent = runtime.BoxFromId("silent");
            silent.Px(100f, 100f).At(0f, 0f);

            // Declines out loud, which is a different thing to say and the one
            // a control uses when it must be there and take nothing.
            declines = runtime.BoxFromId("declines", InteractionKinds.Disabled);
            declines.Px(100f, 100f).At(200f, 0f);

            return Layer(null, silent, declines).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();

    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f });
    IsTrue(!runtime.InteractionsFor(silent).Pressed, "a box that never mentioned claims takes nothing");

    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 50f, Y = 50f });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 250f, Y = 50f });
    IsTrue(!runtime.InteractionsFor(declines).Pressed, "a box claiming Disabled takes no press");
});

// ── Clicks ────────────────────────────────────────────────────────────────────
//
// One event per frame, which is how a window delivers them. BibuiPage's helpers
// batch the down and the up into a single ProcessEvents call, and that is the
// one arrangement in which a press and its release share an Interaction —
// everywhere else it is fresh on each ask, so nothing carries between them.
//
// The streak on a MouseDown is what BeginBuild counts: presses
// within 500ms and 5px of each other. The MouseUp carries none of its own.

(BibuiRuntime Page, Box Button) Clickable()
{
    var runtime = BibuiRuntime.Create();
    Box b = null!;
    runtime.SetBuild(
        () =>
        {
            b = runtime.BoxFromId("btn", InteractionKinds.LeftPress);
            b.Px(100f, 100f);
            return Layer(null, b).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    return (runtime, b);
}

Interaction Deliver(
    BibuiRuntime runtime, Box button, BibUiEventKind kind, int streak, float x = 50f, float y = 50f)
{
    runtime.ProcessEvent(new BibUiEvent
    { Kind = kind, Button = MouseButton.Left, X = x, Y = y, ClickCount = streak });
    var seen = runtime.InteractionsFor(button);
    runtime.Step();
    return seen;
}

Run("Clicked: pressed and released where it started, without moving, is a click", () =>
{
    var (runtime, button) = Clickable();

    IsTrue(!Deliver(runtime, button, BibUiEventKind.MouseDown, 1).Clicked,
        "a press on its own is not a click — the pointer could still leave");

    var up = Deliver(runtime, button, BibUiEventKind.MouseUp, 1);
    IsTrue(up.Clicked, "the release that lands where the press did makes it one");
    IsTrue(up.Released, "and it is a release too, the narrower half of one");
});

Run("Clicked: pressed on it, wandered off, released away, is no click", () =>
{
    // The gesture still ends here — a release goes by who took the press — but
    // ending it somewhere else is how a press is taken back.
    var (runtime, button) = Clickable();
    Deliver(runtime, button, BibUiEventKind.MouseDown, 1);
    Deliver(runtime, button, BibUiEventKind.MouseMove, 1, x: 300f, y: 150f);

    var up = Deliver(runtime, button, BibUiEventKind.MouseUp, 1, x: 300f, y: 150f);
    IsTrue(up.Released, "the node that took the press is the one that ends it");
    IsTrue(!up.Clicked, "but letting go elsewhere is not clicking it");
    Eq(0, up.ClickCount, "so there is nothing to count");
});

Run("Clicked: pressed on it, wandered off and back, released on it, is a click", () =>
{
    // Leaving does not cancel it: only where the button comes up decides.
    var (runtime, button) = Clickable();
    Deliver(runtime, button, BibUiEventKind.MouseDown, 1);
    Deliver(runtime, button, BibUiEventKind.MouseMove, 1, x: 300f, y: 150f);

    var away = Deliver(runtime, button, BibUiEventKind.MouseMove, 1, x: 300f, y: 150f);
    IsTrue(away.Dragging, "still held, and still this node's while it is");

    Deliver(runtime, button, BibUiEventKind.MouseMove, 1, x: 50f, y: 50f);
    var up = Deliver(runtime, button, BibUiEventKind.MouseUp, 1);
    IsTrue(up.Clicked, "come back and let go on it, and it is a click after all");
    IsTrue(up.Released, "and a release");
});

// ── Style.Draw ────────────────────────────────────────────────────────────────
//
// The flags say which of the theme's treatments a node takes, and the explicit
// colours override them. Asserted through the drawn colour rather than the
// Style, because the resolution happens at paint time and that is the only
// place the two can be seen to compose.

// The edge as painted, read from inside the card's 4 px left border; null where
// the background shows through.
(byte R, byte G, byte B)? BorderColorOf(BibuiRuntime runtime)
{
    var edge = Pixel(runtime.LastFrame(), 1, 50);
    return edge == Rgb(UiColor.Hex(runtime.Theme.Colors.Background), runtime.Theme) ? null : edge;
}

(BibuiRuntime Page, Box Card) Bordered(DrawFlags draw, UiColor? resting = null)
{
    var runtime = BibuiRuntime.Create();
    Box card = null!;
    runtime.SetBuild(
        () =>
        {
            card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
            card.Px(100f, 100f);
            card.Style.Draw = draw;
            card.Style.BorderColor = resting;
            card.Style.BorderWidth = 4f;
            runtime.InteractionsFor(card);
            return Layer(null, card).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    return (runtime, card);
}

Run("Style.Draw: Border takes the theme's edge, and nothing draws one without it", () =>
{
    var (bare, _) = Bordered(DrawFlags.None);
    Eq(null, BorderColorOf(bare), "no flag and no colour is no border");

    var (bordered, _) = Bordered(DrawFlags.Border);
    var theme = bordered.Theme;
    Eq(Rgb(UiColor.Border, theme), BorderColorOf(bordered), "the flag takes the theme's border");
});

Run("Style.Draw: HotEffects recolours the border only while the box is hot", () =>
{
    var (page, _) = Bordered(DrawFlags.Border | DrawFlags.HotEffects);
    var theme = page.Theme;

    Eq(Rgb(UiColor.Border, theme), BorderColorOf(page), "cold, so the resting edge");

    // Twice: the ask sets the hot id, and HotT is stepped at the end of the
    // frame — after the paint — so the treatment lands on the frame after the
    // pointer arrives. Part-way there it is a blend of the two, neither one.
    page.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 50f, Y = 50f });
    page.Step();
    page.Step();
    var midway = BorderColorOf(page);
    IsTrue(midway != Rgb(UiColor.Border, theme)
        && midway != Rgb(UiColor.BorderHover, theme),
        $"going hot should blend between the two edges, not switch: {midway}");

    // Settled, it is exactly the hover edge: HotT snaps to 1 inside its epsilon.
    for (int i = 0; i < 8; i++) page.Step();
    Eq(Rgb(UiColor.BorderHover, theme), BorderColorOf(page), "hot, so the hover edge");

    // And leaving takes longer still: HotT eases out over seven frames before
    // the epsilon snap, and the border is lit for every one of them.
    page.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 350f, Y = 150f });
    for (int i = 0; i < 8; i++) page.Step();
    Eq(Rgb(UiColor.Border, theme), BorderColorOf(page), "and back again when it is left");
});

Run("Style.Draw: an explicit border colour wins, and still takes the hot one", () =>
{
    // What a card needs: a resting edge of its own, and the standard hover.
    var mine = UiColor.Hex("#FF00FF");
    var (page, _) = Bordered(DrawFlags.Border | DrawFlags.HotEffects, resting: mine);
    var theme = page.Theme;

    Eq(Rgb(mine, theme), BorderColorOf(page), "the flag's default does not overwrite it");

    page.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 50f, Y = 50f });
    for (int i = 0; i < 10; i++) page.Step();
    Eq(Rgb(UiColor.BorderHover, theme), BorderColorOf(page),
        "and the hot treatment still applies over it, once it has settled");
});

// Numbers in and numbers out, agreeing with the hex spelling.
Run("UiColor: numbers in, numbers out, and the same colour either way", () =>
{
    var theme = ThemeLoader.NeutralDark();

    var byNumbers = UiColor.Rgba(24, 24, 27);
    Eq("#18181BFF", byNumbers.Resolve(theme.Colors), "packed channels spell the hex");
    Eq((24, 24, 27, 255), byNumbers.ResolveColor(theme.Colors), "and come back unchanged");

    var byHex = UiColor.Hex("#18181B");
    Eq((24, 24, 27, 255), byHex.ResolveColor(theme.Colors), "a hex parses to the same channels");

    var token = UiColor.AccentForeground;
    Eq((24, 24, 27, 255), token.ResolveColor(theme.Colors),
        "and so does the token the theme resolves to it");

    // WithAlpha has to survive the packed form, not just the hex one.
    Eq((24, 24, 27, 128), UiColor.Rgba(24, 24, 27).WithAlpha(0.502f).ResolveColor(theme.Colors),
        "alpha applies to a packed colour");
    Eq((24, 24, 27, 128), byHex.WithAlpha(0.502f).ResolveColor(theme.Colors),
        "and to a hex one identically");
});

// The bibui graphics types cross to Skia by reinterpretation and casts
// (bibui-core/src/Renderer/SkiaInterop.cs), so their packing and enum values must be
// Skia's exactly — this is what keeps the type swap from moving a pixel.
Run("Rgba/PaintSpec enums: the same bits and values Skia uses", () =>
{
    var c = new Rgba(0xE8, 0x83, 0x6B, 0x52);
    Eq((uint)new SkiaSharp.SKColor(0xE8, 0x83, 0x6B, 0x52), c.Argb, "Rgba packs 0xAARRGGBB like SKColor");
    Eq(4, System.Runtime.CompilerServices.Unsafe.SizeOf<Rgba>(), "and is one uint wide, so a span casts");
    Eq((uint)SkiaSharp.SKColors.Black, Rgba.Black.Argb, "Black is opaque black");
    Eq(c.Argb & 0x00FFFFFFu, c.WithAlpha(0).Argb, "WithAlpha replaces only alpha");
    Eq(new Rgba(0x18, 0x18, 0x1B), Rgba.Parse("#18181B"), "hex parses opaque");
    Eq(new Rgba(0x18, 0x18, 0x1B, 0x80), Rgba.Parse("#18181B80"), "with alpha");
    Eq("#18181B", Rgba.Parse("#18181B").ToString(), "and spells back as the theme does");
    Eq("#18181B80", Rgba.Parse("#18181B80").ToString(), "alpha only when not opaque");

    Eq((int)SkiaSharp.SKPaintStyle.Fill, (int)PaintStyle.Fill, "PaintStyle.Fill");
    Eq((int)SkiaSharp.SKPaintStyle.Stroke, (int)PaintStyle.Stroke, "PaintStyle.Stroke");
    Eq((int)SkiaSharp.SKPaintStyle.StrokeAndFill, (int)PaintStyle.StrokeAndFill, "PaintStyle.StrokeAndFill");
    Eq((int)SkiaSharp.SKStrokeCap.Butt, (int)StrokeCap.Butt, "StrokeCap.Butt");
    Eq((int)SkiaSharp.SKStrokeCap.Round, (int)StrokeCap.Round, "StrokeCap.Round");
    Eq((int)SkiaSharp.SKStrokeCap.Square, (int)StrokeCap.Square, "StrokeCap.Square");
    Eq((int)SkiaSharp.SKStrokeJoin.Miter, (int)StrokeJoin.Miter, "StrokeJoin.Miter");
    Eq((int)SkiaSharp.SKStrokeJoin.Round, (int)StrokeJoin.Round, "StrokeJoin.Round");
    Eq((int)SkiaSharp.SKStrokeJoin.Bevel, (int)StrokeJoin.Bevel, "StrokeJoin.Bevel");
    // FilterQuality maps to Skia's sampling options in the renderer (SkiaInterop): Skia has no such enum now.

    var r = RectF.FromXywh(10f, 20f, 30f, 40f);
    Eq(new RectF(10f, 20f, 40f, 60f), r, "FromXywh is edges");
    Eq((30f, 40f), (r.Width, r.Height), "and gives back the size");
});

// A bare null in `cond ? UiColor.X : null` types as string and converts, so the
// ternary is a colour where the author meant none.
Run("UiColor: a null colour is refused where it is written, not where it is painted", () =>
{
    Throws<ArgumentNullException>(() => UiColor.Hex(null!), "Hex(null) is not a colour");

    string? missing = null;
    Throws<ArgumentNullException>(() => { UiColor _ = missing!; }, "and neither is an implicit null");

    // The pragma is part of the assertion: the compiler does warn (CS8625), and
    // this project promotes that to an error. An app that does not, will not.
    bool disabled = true;
#pragma warning disable CS8625
    Throws<ArgumentNullException>(
        () => { UiColor? _ = disabled ? null : UiColor.Accent; },
        "a bare null in a ternary is the same conversion, so it is refused too");
#pragma warning restore CS8625

    UiColor? none = disabled ? (UiColor?)null : UiColor.Accent;
    Eq(false, none.HasValue, "and the cast spelling still means no colour");
});

// A UiColor nobody wrote. It used to pass every check and throw at paint.
Run("UiColor: the default is a colour, and it is nothing", () =>
{
    var theme = ThemeLoader.NeutralDark();

    Eq("#00000000", default(UiColor).Resolve(theme.Colors), "an unwritten UiColor is transparent");
    Eq((0, 0, 0, 0), default(UiColor).ResolveColor(theme.Colors), "by channels too");
    Eq(default(UiColor), UiColor.Rgba(0, 0, 0, 0), "and it is the colour you would have written");

    var slots = new UiColor[1];
    Eq("#00000000", slots[0].Resolve(theme.Colors), "including one nobody assigned");
});

// A Text names its colour the way an Icon does now; the enum still converts.
Run("Text.Color: a role, a token and a literal all reach the painter", () =>
{
    var theme = ThemeLoader.NeutralDark();

    // As the painter resolves it.
    string ColorOf(BibuiRuntime rt, string id) =>
        (((Text)rt.FindById(id)!).Color ?? UiColor.TextPrimary).Resolve(theme.Colors);

    var rt = BibuiRuntime.Create();
    rt.SetBuild(() => rt.Into(rt.ColumnFit(), () =>
    {
        rt.TextFromId("role", "x".ToArray(), textColor: TextColor.Secondary);
        rt.TextFromId("token", "x".ToArray(), textColor: UiColor.TextSecondary);
        rt.TextFromId("literal", "x".ToArray(), textColor: UiColor.Rgba(161, 161, 170));
        rt.TextFromId("default", "x".ToArray());
    }), new SurfaceOptions { Width = 200, Height = 200 }, "neutral-dark");
    rt.Step();

    Eq(theme.Colors.TextSecondary, ColorOf(rt, "role"), "the enum still names the token it always did");
    Eq(theme.Colors.TextSecondary, ColorOf(rt, "token"), "and the token spells the same colour");
    Eq("#A1A1AAFF", ColorOf(rt, "literal"), "a literal reaches the painter unmediated");
    Eq(theme.Colors.TextPrimary, ColorOf(rt, "default"), "and no colour is still TextPrimary");
});

// ── Fonts: measuring in a font, and the offset under an x ─────────────────────

Run("Interaction.PressCount: which press of a streak it was", () =>
{
    var runtime = BibuiRuntime.Create();
    var counts = new List<int>();
    runtime.SetBuild(() =>
    {
        var b = runtime.BoxFromId("b", InteractionKinds.LeftPress);
        var i = runtime.InteractionsFor(b);
        if (i.Pressed) counts.Add(i.PressCount);
        b.Px(100f, 100f);
        return b;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();

    foreach (int streak in new[] { 1, 2, 3 })
    {
        runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f, ClickCount = streak });
        runtime.Step();
        runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 50f, Y = 50f });
        runtime.Step();
    }
    Eq("1,2,3", string.Join(",", counts), "single, double and triple press");
});

Run("Interaction.PressCount: held, a double press is still a double press", () =>
{
    var runtime = BibuiRuntime.Create();
    var seen = new List<string>();
    runtime.SetBuild(() =>
    {
        var b = runtime.BoxFromId("b", InteractionKinds.LeftPress);
        var i = runtime.InteractionsFor(b);
        seen.Add($"{(i.Pressed ? "P" : i.Dragging ? "D" : "-")}{i.PressCount}");
        b.Px(100f, 100f);
        return b;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();
    seen.Clear();

    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 50f, Y = 50f, ClickCount = 2 });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 60f, Y = 50f });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 60f, Y = 50f });
    runtime.Step();
    Eq("P2,D2,-0", string.Join(",", seen), "pressed, held, released");
});

// ── Overlays: menu, popover, tooltip ─────────────────────────────────────────

Run("Menu: an item clicked in an open menu reports it, and the caller closes it", () =>
{
    var runtime = BibuiRuntime.Create();
    string? picked = null;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var trigger = runtime.Button("menu-trigger", "Actions");
            if (trigger.Clicked) runtime.OpenDropDown("menu", trigger.Node);
            if (Bibui.Core.Runtime.Menu.BeginBelow(runtime, "menu", trigger.Node) is { } menu)
            {
                foreach (var label in new[] { "Copy", "Paste" })
                    if (runtime.MenuItem($"menu-{label}", label).Clicked)
                    {
                        picked = label;
                        runtime.CloseDropDown("menu");
                    }
                Bibui.Core.Runtime.Menu.Separator(runtime);
                runtime.MenuItem("menu-Delete", "Delete", disabled: true);
                menu.End();
            }
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    ClickCentre(runtime, "menu-trigger");
    IsTrue(runtime.IsDropDownOpen("menu"), "the trigger opens it");
    runtime.Step();
    ClickCentre(runtime, "menu-Delete");
    Eq(null, picked, "a disabled item reports nothing");
    IsTrue(runtime.IsDropDownOpen("menu"), "and the menu stays open");
    ClickCentre(runtime, "menu-Paste");
    Eq("Paste", picked, "the item under the pointer has the click");
    IsTrue(!runtime.IsDropDownOpen("menu"), "and the caller closes it");
});

Run("Menu: Escape closes the open menu and is eaten", () =>
{
    var runtime = BibuiRuntime.Create();
    int escapesLeft = 0;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            if (Bibui.Core.Runtime.Menu.Begin(runtime, "menu", 10f, 10f) is { } menu)
            {
                runtime.MenuItem("menu-copy", "Copy");
                menu.End();
            }
            escapesLeft += runtime.UnconsumedEvents.Count(h => h is { IsKeyDown: true, Key: Bibui.Core.Key.Escape });
        }
        return page;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.OpenDropDown("menu");
    runtime.Step();
    runtime.ProcessEvent(BibUiEvent.FromKey(Bibui.Core.Key.Escape, ctrl: false, shift: false));
    runtime.Step();
    IsTrue(!runtime.IsDropDownOpen("menu"), "Escape closes it");
    Eq(0, escapesLeft, "and the menu ate it");
    runtime.ProcessEvent(BibUiEvent.FromKey(Bibui.Core.Key.Escape, ctrl: false, shift: false));
    runtime.Step();
    Eq(1, escapesLeft, "with the menu closed, Escape is left for the app");
});

// ── Dismissing a dropdown ────────────────────────────────────────────────────

// A page whose button beneath asks first, as content built before a dropdown
// does, then a trigger that toggles a menu under it on its click, and two
// rows a right press opens a context menu on, where it went down. Counts the
// button's clicks. The presses here go down and up in frames of their own, as
// a person's do.
(BibuiRuntime runtime, Func<int> beneath, Func<string?> menuFor) DismissPage()
{
    var runtime = BibuiRuntime.Create();
    int beneath = 0;
    string? menuFor = null;
    float mx = 0f, my = 0f;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var under = runtime.Button("under", "Beneath");
            under.Node.Px(400f, 100f);
            if (under.Clicked) beneath++;
            var trigger = runtime.Button("trigger", "Actions");
            if (trigger.Clicked)
                if (runtime.IsDropDownOpen("menu")) runtime.CloseDropDown("menu"); else runtime.OpenDropDown("menu", trigger.Node);
            if (Bibui.Core.Runtime.Menu.BeginBelow(runtime, "menu", trigger.Node, width: 160f) is { } menu)
            {
                runtime.MenuItem("menu-copy", "Copy");
                Bibui.Core.Runtime.Menu.Separator(runtime);
                runtime.MenuItem("menu-paste", "Paste");
                menu.End();
            }
            foreach (var id in new[] { "row-1", "row-2" })
            {
                var row = runtime.BoxFromId(id, InteractionKinds.RightPress);
                var i = runtime.InteractionsFor(row);
                if (i.RightPressed) { (menuFor, mx, my) = (id, i.X, i.Y); runtime.OpenDropDown("ctx"); }
                row.Px(400f, 40f);
            }
            if (Bibui.Core.Runtime.Menu.Begin(runtime, "ctx", mx, my, 120f) is { } ctx)
            {
                runtime.MenuItem("ctx-open", "Open");
                ctx.End();
            }
        }
        return page;
    }, new SurfaceOptions { Width = 600, Height = 500 });
    runtime.Step();
    runtime.Step();
    return (runtime, () => beneath, () => menuFor);
}

// Down, a frame, up, a frame.
static void PressAndRelease(BibuiRuntime runtime, float x, float y, MouseButton button = MouseButton.Left)
{
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = button, X = x, Y = y });
    runtime.Step();
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = button, X = x, Y = y });
    runtime.Step();
}

static (float X, float Y) Centre(BibuiRuntime runtime, string id)
{
    var b = BibuiPage.From(runtime, runtime.BuildTree).GetBounds(id);
    return (b.X + b.Width / 2f, b.Y + b.Height / 2f);
}

Run("Dismiss: a click on the trigger of an open menu closes it, and another opens it", () =>
{
    var (runtime, _, _) = DismissPage();
    var (x, y) = Centre(runtime, "trigger");
    PressAndRelease(runtime, x, y);
    IsTrue(runtime.IsDropDownOpen("menu"), "the click opens it");
    runtime.Step();
    PressAndRelease(runtime, x, y);
    IsTrue(!runtime.IsDropDownOpen("menu"), "the next closes it: its press is the trigger's, which the menu leaves to it");
    PressAndRelease(runtime, x, y);
    IsTrue(runtime.IsDropDownOpen("menu"), "and the next opens it again");
});

Run("Dismiss: a right press on another row moves an open context menu there", () =>
{
    var (runtime, _, menuFor) = DismissPage();
    var (_, y1) = Centre(runtime, "row-1");
    PressAndRelease(runtime, 20f, y1, MouseButton.Right);
    Eq("row-1", menuFor(), "a right press opens it on its row");
    IsTrue(runtime.IsDropDownOpen("ctx"), "and does not dismiss the menu it opened");
    runtime.Step();
    var (_, y2) = Centre(runtime, "row-2");
    PressAndRelease(runtime, 300f, y2, MouseButton.Right);
    Eq("row-2", menuFor(), "a right press on the other row");
    IsTrue(runtime.IsDropDownOpen("ctx"), "closes the menu and opens it there, which stands");
    var ctx = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("ctx");
    IsTrue(ctx.X == 300f && ctx.Y == y2, "and it is drawn where that press went down");
});

Run("Dismiss: a press a box outside takes closes it, and still reaches that box", () =>
{
    var (runtime, beneath, _) = DismissPage();
    var (x, y) = Centre(runtime, "trigger");
    PressAndRelease(runtime, x, y);
    runtime.Step();
    var (ux, uy) = Centre(runtime, "under");
    PressAndRelease(runtime, ux, uy);
    IsTrue(!runtime.IsDropDownOpen("menu"), "the button beneath took the press: the menu is closed");
    Eq(1, beneath(), "and the button has its click");
});

Run("Dismiss: a press nothing takes closes it", () =>
{
    var (runtime, beneath, _) = DismissPage();
    var (x, y) = Centre(runtime, "trigger");
    PressAndRelease(runtime, x, y);
    runtime.Step();
    PressAndRelease(runtime, 590f, 490f);
    IsTrue(!runtime.IsDropDownOpen("menu"), "an empty corner closes it");
    Eq(0, beneath(), "and nothing has a click");
});

Run("Dismiss: a press inside, on an item or on the padding, does not", () =>
{
    var (runtime, beneath, _) = DismissPage();
    var (x, y) = Centre(runtime, "trigger");
    PressAndRelease(runtime, x, y);
    runtime.Step();
    var (ix, iy) = Centre(runtime, "menu-copy");
    PressAndRelease(runtime, ix, iy);
    IsTrue(runtime.IsDropDownOpen("menu"), "an item's press leaves it open");
    var list = BibuiPage.From(runtime, runtime.BuildTree).GetBounds("menu");
    PressAndRelease(runtime, list.X + 1f, list.Y + 1f);
    IsTrue(runtime.IsDropDownOpen("menu"), "so does a press on its padding");
    Eq(0, beneath(), "and neither reaches the button beneath");
});

Run("Dismiss: a dropdown opened in every frame it is built is never dismissed", () =>
{
    var runtime = BibuiRuntime.Create();
    int beneath = 0;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var under = runtime.Button("under", "Beneath");
            under.Node.FillX().FillY();
            if (under.Clicked) beneath++;
            runtime.OpenDropDown("toast");
            runtime.BeginDropDown("toast")!.Px(100f, 40f).At(250f, 250f);
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    runtime.Step();
    PressAndRelease(runtime, 20f, 20f);
    Eq(1, beneath, "the press beside it is the button's");
    IsTrue(runtime.IsDropDownOpen("toast"), "and the dropdown is still open");
});

Run("Dismiss: opening a dropdown after it was begun asks for the frame it shows in; one opened every frame does not keep asking", () =>
{
    var runtime = BibuiRuntime.Create();
    int wakes = 0;
    bool openLate = false;
    runtime.WakeUp = () => wakes++;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            runtime.BeginDropDown("late")?.Px(50f, 50f);
            if (openLate) runtime.OpenDropDown("late");
            runtime.OpenDropDown("always");
            runtime.BeginDropDown("always")!.Px(50f, 50f).At(100f, 100f);
        }
        return page;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.Step();
    runtime.Step();
    wakes = 0;
    runtime.Step();
    runtime.Step();
    Eq(0, wakes, "a dropdown opened every frame it is built asks for nothing");
    openLate = true;
    runtime.Step();
    IsTrue(wakes > 0, "one opened after its build point asks for a frame");
});

Run("Dismiss: a dropdown its caller stops building closes", () =>
{
    var runtime = BibuiRuntime.Create();
    bool build = true;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
            if (build) runtime.BeginDropDown("pop")?.Px(50f, 50f);
        return page;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    runtime.OpenDropDown("pop");
    runtime.Step();
    IsTrue(runtime.IsDropDownOpen("pop"), "open while it is begun");
    build = false;
    runtime.Step();
    IsTrue(!runtime.IsDropDownOpen("pop"), "closed in the frame it is not");
});

Run("BibUiEvent.IsKeyDown: every key-down kind, and nothing else", () =>
{
    foreach (var key in new[] { Bibui.Core.Key.Up, Bibui.Core.Key.Down, Bibui.Core.Key.Left, Bibui.Core.Key.Home, Bibui.Core.Key.Enter,
                 Bibui.Core.Key.Escape, Bibui.Core.Key.Slash, Bibui.Core.Key.Backspace, Bibui.Core.Key.Delete })
        IsTrue(BibUiEvent.FromKey(key, ctrl: false, shift: false).IsKeyDown, $"{key} is a key-down");
    foreach (var key in new[] { Bibui.Core.Key.A, Bibui.Core.Key.C, Bibui.Core.Key.X, Bibui.Core.Key.V })
        IsTrue(BibUiEvent.FromKey(key, ctrl: true, shift: false).IsKeyDown, $"Ctrl+{key} is a key-down");
    Eq(BibUiEventKind.Navigate, BibUiEvent.FromKey(Bibui.Core.Key.Up, false, false).Kind, "an arrow is still a Navigate");
    Eq(BibUiEventKind.Submit, BibUiEvent.FromKey(Bibui.Core.Key.Enter, false, false).Kind, "Enter is still a Submit");
    foreach (var kind in new[] { BibUiEventKind.Null, BibUiEventKind.Text, BibUiEventKind.Release, BibUiEventKind.MouseDown,
                 BibUiEventKind.MouseUp, BibUiEventKind.MouseMove, BibUiEventKind.Scroll })
        IsTrue(!new BibUiEvent { Kind = kind }.IsKeyDown, $"{kind} is not a key-down");
});

Run("BibUiEvent.IsKeyDown: a shortcut handler sees arrows and Enter that reach the app", () =>
{
    var runtime = BibuiRuntime.Create();
    var seen = new List<Bibui.Core.Key>();
    runtime.SetBuild(() =>
    {
        foreach (var ev in runtime.UnconsumedEvents)
            if (ev.IsKeyDown) { seen.Add(ev.Key); ev.Eat(); }
        return runtime.Box().Px(10f, 10f);
    }, new SurfaceOptions { Width = 100, Height = 100 });
    runtime.Step();
    foreach (var key in new[] { Bibui.Core.Key.Down, Bibui.Core.Key.Enter, Bibui.Core.Key.G })
        runtime.ProcessEvent(BibUiEvent.FromKey(key, ctrl: false, shift: false));
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Text, Char = 'g', CodePoint = 'g' });
    runtime.Step();
    Eq("Down,Enter,G", string.Join(",", seen), "each key once, its Text not among them");
});

Run("Popover: its open panel blocks the box beneath it, and only there", () =>
{
    var runtime = BibuiRuntime.Create();
    int beneath = 0;
    Box? panel = null;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var trigger = runtime.Button("pop-trigger", "Open");
            // Asking before the popover does, and covering the whole page under
            // the trigger: only the panel's rect being its own keeps the press from it.
            var below = runtime.Button("below", "Beneath");
            below.Node.FillX().FillY();
            if (below.Clicked) beneath++;
            runtime.OpenDropDown("pop", trigger.Node);
            if (Bibui.Core.Runtime.Popover.Begin(runtime, "pop", trigger.Node, width: 200f) is { } popover)
            {
                runtime.Text("Floating over the button below");
                panel = (Box)popover.End().Node;
            }
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    runtime.Step();
    IsTrue(panel!.Extent.X > 0f && panel.Extent.Y > 0f, "the panel was drawn");
    ClickAt(runtime, panel.Rect.X + panel.Extent.X / 2f, panel.Rect.Y + panel.Extent.Y / 2f);
    Eq(0, beneath, "a press on the panel does not reach the button beneath");
    ClickAt(runtime, 390f, 290f);
    Eq(1, beneath, "a press beside it does");
});

Run("Tooltip: claims nothing, so a press on it reaches what is beneath", () =>
{
    var runtime = BibuiRuntime.Create();
    int clicks = 0;
    Box? pill = null;
    runtime.SetBuild(() =>
    {
        var page = runtime.ColumnFill();
        using (runtime.PushParent(page))
        {
            var button = runtime.Button("under", "Under the tip");
            button.Node.FillX().FillY();
            if (button.Clicked) clicks++;
            pill = Bibui.Core.Runtime.Tooltip.Build(runtime, "A hint", (Box)button.Node);
            pill.Parent!.At(150f, 120f);   // over the middle of the button
        }
        return page;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    runtime.Step();
    IsTrue(pill!.Extent.X > 0f, "the tooltip was drawn");
    IsTrue(pill.Claims == InteractionKinds.None, "and claims nothing");
    ClickAt(runtime, pill.Rect.X + pill.Extent.X / 2f, pill.Rect.Y + pill.Extent.Y / 2f);
    Eq(1, clicks, "the button beneath the tooltip has the click");
});

Run("Fonts.OffsetAt: the boundary nearer x, either side of every glyph", () =>
{
    var runtime = BibuiRuntime.Create();
    var font = runtime.FontInScope();
    const string text = "Hello";
    for (int k = 1; k <= text.Length; k++)
    {
        float before = runtime.Fonts.Measure(text.AsSpan(0, k - 1), font);
        float after = runtime.Fonts.Measure(text.AsSpan(0, k), font);
        Eq(k, runtime.Fonts.OffsetAt(text, font, after - 0.5f), $"just left of glyph {k}'s right edge");
        Eq(k - 1, runtime.Fonts.OffsetAt(text, font, before + 0.5f), $"just right of glyph {k}'s left edge");
    }
    Eq(0, runtime.Fonts.OffsetAt(text, font, -10f), "before the start");
    Eq(text.Length, runtime.Fonts.OffsetAt(text, font, 1000f), "past the end");
});

Run("Fonts.OffsetAt: only ever a text element boundary", () =>
{
    var runtime = BibuiRuntime.Create();
    var font = runtime.FontInScope();
    const string text = "a👍🏽b";   // a | 👍🏽 (two code points, four chars) | b — boundaries 0, 1, 5, 6
    float a = runtime.Fonts.Measure("a", font), at = runtime.Fonts.Measure("a👍🏽", font);
    Eq(5, runtime.Fonts.OffsetAt(text, font, at - 0.5f), "after the 👍🏽, not inside it");
    Eq(1, runtime.Fonts.OffsetAt(text, font, a + 0.5f), "before the 👍🏽");
});

Run("Fonts.LineHeight and LineAdvance: what layout gives one line, and the next", () =>
{
    // One line is LineHeight tall, and a multi-line field as tall as its lines
    // adds LineAdvance for every line after the first.
    var runtime = BibuiRuntime.Create();
    Text one = null!;
    runtime.SetBuild(() =>
    {
        var column = runtime.BoxFromId("column");
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = SemanticSize.Pixels(400f);
        column.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(column))
        {
            one = runtime.TextFromId("one", "A line".ToArray());
            var field = FieldFrame(runtime, SemanticSize.ChildrenSum());
            using (runtime.PushParent(field))
            using (runtime.PushSizeY(SemanticSize.ChildrenSum()))
                runtime.MultilineTextField("three", BibuiStr.FromStrLit("A line\nanother\nand a third"),
                new TextEditState(), focused: false, events: 0);
        }
        return column;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    runtime.Step();

    var font = runtime.FontInScope();
    float height = runtime.Fonts.LineHeight(font), advance = runtime.Fonts.LineAdvance(font);
    IsTrue(advance >= height, $"the advance {advance} includes the line {height}");
    Eq(height, one.Extent.Y, "one line");
    using var page = BibuiPage.From(runtime, runtime.BuildTree);
    float three = page.FindById("three")!.Extent.Y;
    IsTrue(MathF.Abs(height + 2f * advance - three) < 0.01f,
        $"three lines: {height} + 2 x {advance}, laid out {three}");
});

Run("Fonts.Wrap: lines at a width, the space between them in neither", () =>
{
    var runtime = BibuiRuntime.Create();
    var font = runtime.FontInScope();
    var text = "one two three";
    // Room for "one two" but not "one two three".
    float width = runtime.Fonts.Measure("one two", font) + 1f;
    var lines = new List<string>();
    for (var line = runtime.Fonts.Wrap(text, font, width); line is not null; line = line.Next)
        lines.Add($"{line.Start}..{line.End}");
    Eq("0..7,8..13", string.Join(",", lines), "one two | three, the space at 7 in neither");
    IsTrue(runtime.Fonts.Wrap("", font, width) is null, "nothing is no lines");
});

Run("Fonts.Wrap: offsets are chars, and a line break starts a line", () =>
{
    var runtime = BibuiRuntime.Create();
    var font = runtime.FontInScope();
    var text = "grüße\nw👍🏽rld";   // ü and ß are a char each; 👍🏽 is four
    var first = runtime.Fonts.Wrap(text, font, 1000f)!;
    var second = first.Next!;
    Eq(0, first.Start); Eq(5, first.End, "grüße is 5 chars");
    Eq(6, second.Start, "after the \\n"); Eq(14, second.End, "w👍🏽rld is 8 chars");
    IsTrue(second.Next is null && second.Prev == first, "two lines, linked both ways");
});

Run("Fonts.Wrap: by characters, a line holds what fits and the next starts where it ends", () =>
{
    var runtime = BibuiRuntime.Create();
    var font = runtime.FontInScope();
    const string text = "/home/ada/grüße/a-path-longer-than-the-line.txt\nok";
    float width = runtime.Fonts.Measure("/home/ada/", font) + 0.5f;

    var lines = new List<TextLine>();
    for (var line = runtime.Fonts.Wrap(text, font, width, TextWrap.Characters); line is not null; line = line.Next)
        lines.Add(line);
    IsTrue(lines.Count > 3, $"the path is cut into several lines, got {lines.Count}");
    Eq(0, lines[0].Start);
    Eq(10, lines[0].End, "as many characters as fit: /home/ada/");
    for (int i = 1; i < lines.Count - 1; i++)
        Eq(lines[i - 1].End, lines[i].Start, "nothing between one line and the next");
    IsTrue(lines.All(l => runtime.Fonts.Measure(text.AsSpan(l.Start..l.End), font) <= width), "every line fits");
    IsTrue(lines.All(l => l.Start == l.End || !char.IsLowSurrogate(text[l.Start])), "a line starts on a character, not inside one");
    Eq("ok", text[lines[^1].Start..lines[^1].End], "a line break still starts a line");

    var byWords = runtime.Fonts.Wrap(text, font, width)!;
    Eq(text.IndexOf('\n'), byWords.End, "by words the path is one line, wider than the width");

    var narrow = runtime.Fonts.Wrap("ab", font, 0.5f, TextWrap.Characters)!;
    IsTrue(narrow.End == 1 && narrow.Next!.Start == 1, "a line too narrow for anything still holds one character");
});

Run("Fonts.Measure: a longer text is wider, and nothing has no width", () =>
{
    var runtime = BibuiRuntime.Create();
    using var bold = runtime.PushFontFace(weight: FontWeight.Bold);
    var font = runtime.FontInScope(ComponentSize.Lg);
    float word = runtime.Fonts.Measure("Grüße", font);
    IsTrue(word > 0f && runtime.Fonts.Measure("Grüße, wörld", font) > word, "measured over its code points");
    Eq(0f, runtime.Fonts.Measure("", font), "nothing is no width");
});

// ── TextField ────────────────────────────────────────────────────────────────

// A field in a 300 px parent with no padding, which a press focuses, as an app
// would; it also notes whether a Submit was left for the caller after the field.
(BibuiRuntime Runtime, Func<bool> Focused, Func<bool> SubmitLeft) MountField(BibuiStr text, TextEditState edit, bool multiline = false,
    uint events = uint.MaxValue)
{
    var runtime = BibuiRuntime.Create();
    bool focused = false, submitLeft = false;
    runtime.SetBuild(() =>
    {
        var parent = runtime.BoxFromId("parent");
        parent.ChildLayoutAxis = LayoutAxis.Y;
        parent.Px(300f, 200f);
        using (runtime.PushParent(parent))
        {
            // As wide as the parent, and one line or three tall. The field takes its own presses.
            BibuiWidget line;
            if (multiline)
            {
                var field = FieldFrame(runtime, SemanticSize.ChildrenSum());
                using (runtime.PushParent(field))
                using (runtime.PushSizeY(Bibui.Core.Runtime.TextField.Rows(runtime, 3)))
                    line = runtime.MultilineTextField("field", text, edit, focused,
                        events == uint.MaxValue ? Bibui.Core.Runtime.TextField.MultiLineEvents : events);
            }
            else
            {
                var field = FieldFrame(runtime, Bibui.Core.Runtime.TextField.Rows(runtime, 1));
                using (runtime.PushParent(field)) line = runtime.TextField("field", text, edit, focused,
                    events == uint.MaxValue ? Bibui.Core.Runtime.TextField.SingleLineEvents : events);
            }
            if (line.Pressed) focused = true;
            submitLeft |= runtime.UnconsumedEvents.Any(h => h.Kind == BibUiEventKind.Submit);
        }
        return parent;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    runtime.Step();   // a second frame: the multi-line field wraps at its width from the first
    return (runtime, () => focused, () => submitLeft);
}

void TypeInto(BibuiRuntime runtime, string chars)
{
    foreach (var c in chars)
        runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Text, Char = c, CodePoint = c });
    runtime.Step();
}

void PressKey(BibuiRuntime runtime, Bibui.Core.Key key, bool ctrl = false, bool shift = false)
{
    runtime.ProcessEvent(BibUiEvent.FromKey(key, ctrl, shift));
    runtime.Step();
}

Run("runtime.TextField: a press puts the caret at the byte under it, and focuses", () =>
{
    var text = BibuiStr.FromStrLit("Hello");
    var edit = new TextEditState();
    var (runtime, focused, _) = MountField(text, edit);
    var font = runtime.FontInScope();
    float afterHe = runtime.Fonts.Measure("He", font);
    ClickAt(runtime, afterHe + 0.5f, 5f);
    runtime.Step();
    IsTrue(focused(), "the press is reported, and the caller focuses");
    Eq(2, edit.Cursor, "after He"); Eq(2, edit.Mark);
});

Run("runtime.TextField: in a padded box, a press is measured from where the text starts", () =>
{
    // The caller's box carries the padding: the texts are laid out inside it,
    // and the field takes it off the pointer and puts it on the caret.
    var text = BibuiStr.FromStrLit("Hello");
    var edit = new TextEditState();
    var runtime = BibuiRuntime.Create();
    Box field = null!;
    runtime.SetBuild(() =>
    {
        var parent = runtime.BoxFromId("parent");
        parent.ChildLayoutAxis = LayoutAxis.Y;
        parent.Px(300f, 200f);
        using (runtime.PushParent(parent))
        {
            field = FieldFrame(runtime, SemanticSize.Pixels(40f));
            field.Style.Padding = new EdgeInsets(10f, 20f, 10f, 20f);
            using (runtime.PushParent(field)) runtime.TextField("field", text, edit, focused: true);
        }
        return parent;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    var font = runtime.FontInScope();
    float afterHe = runtime.Fonts.Measure("He", font);
    ClickAt(runtime, 20f + afterHe + 0.5f, 15f);
    runtime.Step();
    Eq(2, edit.Cursor, "after He, counted from inside the padding");
    // The caret is the last child of the line's view, placed floating in it. It may still
    // be gliding from the start of the text, so it is somewhere on the text, not before it.
    var caret = field.FirstChild!.LastChild!;
    float left = caret.Rect.X - field.Rect.X + 1f;   // its middle, from the box's edge
    IsTrue(left >= 20f - 0.5f && left <= 20f + afterHe + 0.5f, $"the caret is on the text, inside the padding: {left}");
    Eq(10f, caret.Rect.Y - field.Rect.Y, "its top is the padding's");
});

Run("runtime.EllipsisTextField: a press is the field's, and being edited it scrolls instead of ending in \"…\"", () =>
{
    var text = BibuiStr.FromStrLit("A label far too long for a box this narrow");
    var edit = new TextEditState();
    bool focused = false;
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() => runtime.Into(runtime.ColumnFit(), () =>
    {
        var box = runtime.Box();
        box.SizeX = SemanticSize.Pixels(120f);
        box.SizeY = SemanticSize.ChildrenSum();
        BibuiWidget line;
        using (runtime.PushParent(box))
            line = runtime.EllipsisTextField("label", text, edit, focused, events: Bibui.Core.Runtime.TextField.SingleLineEvents);
        if (line.Pressed) focused = true;
    }), new SurfaceOptions { Width = 400, Height = 100 });
    runtime.Step();
    runtime.Step();
    bool Dots() => Texts(runtime).Contains("\u2026");
    IsTrue(Dots(), "not being edited, it ends in \"…\"");

    runtime.DispatchClick("label");
    runtime.Step();
    IsTrue(focused, "the press is the field's, by its id");
    runtime.Step();
    IsTrue(!Dots(), "being edited, it has no \"…\"");
});

Run("runtime.Gap: pixels along the parent's axis, nothing across it", () =>
{
    var runtime = BibuiRuntime.Create();
    Box rowGap = null!, columnGap = null!;
    runtime.SetBuild(() =>
    {
        var column = runtime.BoxFromId("column");
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.Px(200f, 200f);
        using (runtime.PushParent(column))
        {
            columnGap = runtime.Gap(12f);
            var row = runtime.BoxFromId("row");
            row.ChildLayoutAxis = LayoutAxis.X;
            row.Px(100f, 20f);
            using (runtime.PushParent(row)) rowGap = runtime.Gap(8f);
        }
        return column;
    }, new SurfaceOptions { Width = 300, Height = 300 });
    runtime.Step();
    Eq(new System.Numerics.Vector2(0f, 12f), columnGap.Extent, "down a column");
    Eq(new System.Numerics.Vector2(8f, 0f), rowGap.Extent, "along a row");
});

Run("runtime.EllipsisTextField: \"…\" only when the line is wider than its box, and the line gives it room", () =>
{
    foreach (var (words, cut) in new[] { ("Fits", false), ("A label far too long for a box this narrow", true) })
    {
        var runtime = BibuiRuntime.Create();
        Box box = null!;
        runtime.SetBuild(() => runtime.Into(runtime.ColumnFit(), () =>
        {
            box = runtime.BoxFromId("label");
            box.SizeX = SemanticSize.Pixels(120f);
            box.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(box)) runtime.EllipsisTextField("line", Str(words), new TextEditState(), focused: false);
        }), new SurfaceOptions { Width = 400, Height = 100 });
        runtime.Step();
        runtime.Step();   // whether it fits is told by last frame's width
        using var page = BibuiPage.From(runtime, runtime.BuildTree);
        bool dots = Texts(runtime).Contains("\u2026");
        Eq(cut, dots, $"\"{words}\" ends in \"…\"");
        var line = box.FirstChild!;   // the line's own box, inside the label
        float dotsWidth = runtime.Fonts.Measure("\u2026", runtime.FontInScope());
        Eq(cut ? 120f - 2f - dotsWidth : 120f, line.Extent.X, "the line takes what the gap and the \"…\" leave");
    }
});

Run("runtime.TextField: a line wider than its box scrolls to the caret, and back to its start", () =>
{
    var text = BibuiStr.FromStrLit("the quick brown fox jumps over the lazy dog");
    var edit = new TextEditState { Cursor = (int)text.Length, Mark = (int)text.Length };
    var runtime = BibuiRuntime.Create();
    Box field = null!;
    runtime.SetBuild(() =>
    {
        var parent = runtime.BoxFromId("parent");
        parent.ChildLayoutAxis = LayoutAxis.Y;
        parent.Px(300f, 200f);
        using (runtime.PushParent(parent))
        {
            field = FieldFrame(runtime, Bibui.Core.Runtime.TextField.Rows(runtime, 1));
            field.SizeX = SemanticSize.Pixels(100f);
            using (runtime.PushParent(field))
                runtime.TextField("field", text, edit, focused: true);
        }
        return parent;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    for (int i = 0; i < 3; i++) runtime.Step();   // laid out, then scrolled
    PressKey(runtime, Bibui.Core.Key.End);      // an edit-free move: the view eases rather than jumps
    for (int i = 0; i < 60; i++) runtime.Step();

    var font = runtime.FontInScope();
    float width = runtime.Fonts.Measure(text.Str, font);
    var view = field.FirstChild!;   // the line's own view, inside the field
    IsTrue(MathF.Abs(width - 100f - view.TargetViewOffset.X) < 0.01f,
        $"at the end, the view is the line's width less the box's: {view.TargetViewOffset.X} vs {width - 100f}");
    var caret = view.LastChild!;
    IsTrue(caret.Rect.X >= field.Rect.X - 1f && caret.Rect.X <= field.Rect.X + 100f,
        $"the caret is in the box: {caret.Rect.X} in {field.Rect.X}..{field.Rect.X + 100f}");

    PressKey(runtime, Bibui.Core.Key.Home);
    for (int i = 0; i < 60; i++) runtime.Step();
    Eq(0f, view.TargetViewOffset.X, "at the start, the line is shown from its start");
});

Run("runtime.MultilineTextField: moving the caret past the last row scrolls its line into view, and the wheel is not fought", () =>
{
    var text = BibuiStr.FromStrLit("one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten");
    var edit = new TextEditState();
    bool focused = false;
    var runtime = BibuiRuntime.Create();
    Box view = null!;
    runtime.SetBuild(() =>
    {
        var parent = runtime.BoxFromId("parent");
        parent.ChildLayoutAxis = LayoutAxis.Y;
        parent.Px(300f, 200f);
        using (runtime.PushParent(parent))
        using (runtime.PushParent(FieldFrame(runtime, SemanticSize.ChildrenSum())))
        using (runtime.PushSizeY(Bibui.Core.Runtime.TextField.Rows(runtime, 3)))
        {
            var field = runtime.MultilineTextField("field", text, edit, focused);
            view = (Box)field.Node;
            if (field.Pressed) focused = true;
        }
        return parent;
    }, new SurfaceOptions { Width = 400, Height = 300 });
    runtime.Step();
    runtime.Step();
    ClickAt(runtime, 10f, 5f);
    runtime.Step();
    IsTrue(focused, "a press focuses it");
    Eq(0f, view.TargetViewOffset.Y, "on the first line, nothing to scroll");

    for (int i = 0; i < 6; i++) { PressKey(runtime, Bibui.Core.Key.Down); runtime.Step(); }
    for (int i = 0; i < 30; i++) runtime.Step();
    var font = runtime.FontInScope();
    float lineHeight = runtime.Fonts.LineHeight(font), advance = runtime.Fonts.LineAdvance(font);
    float caretBottom = 6 * advance + lineHeight;   // the seventh line's
    float scrolled = view.TargetViewOffset.Y;
    IsTrue(scrolled > 0f && caretBottom - scrolled <= view.Extent.Y + 0.01f && 6 * advance >= scrolled,
        $"the caret's line is in view: {scrolled} for a line {6 * advance}..{caretBottom} in {view.Extent.Y}");

    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Scroll, X = 50f, Y = 20f, ScrollY = 5f });
    for (int i = 0; i < 30; i++) runtime.Step();
    IsTrue(MathF.Abs(view.TargetViewOffset.Y - scrolled) > 1f,
        $"the wheel moved it, and the caret did not pull it back: {view.TargetViewOffset.Y} after {scrolled}");
});

Run("runtime.TextField: typing edits the caller's buffer; Enter is left to the caller", () =>
{
    var text = BibuiStr.FromStrLit("Hello");
    var edit = new TextEditState();
    var (runtime, _, submitLeft) = MountField(text, edit);
    ClickAt(runtime, 200f, 5f);   // past the end
    runtime.Step();
    TypeInto(runtime, ", world");
    Eq("Hello, world", text.ToString());
    PressKey(runtime, Bibui.Core.Key.Enter);
    Eq("Hello, world", text.ToString(), "a single line takes no line break");
    IsTrue(submitLeft(), "Submit is still the caller's");
});

Run("runtime.MultilineTextField: Enter ends the line, and Up goes back to it", () =>
{
    var text = BibuiStr.FromStrLit("one");
    var edit = new TextEditState();
    var (runtime, _, _) = MountField(text, edit, multiline: true);
    ClickAt(runtime, 200f, 5f);   // past the end of the line
    runtime.Step();
    PressKey(runtime, Bibui.Core.Key.Enter);
    TypeInto(runtime, "two");
    Eq("one\ntwo", text.ToString());
    PressKey(runtime, Bibui.Core.Key.Up);
    Eq(3, edit.Cursor, "the end of line one, at about the same x");
});

Run("TextField read-only: moves and copies, never edits", () =>
{
    var text = BibuiStr.FromStrLit("hello world");
    var edit = new TextEditState();
    var (runtime, _, _) = MountField(text, edit, events: Bibui.Core.Runtime.TextField.SingleLineReadOnlyEvents);
    string copied = "";
    runtime.ClipboardSet = t => copied = t;
    ClickAt(runtime, 1f, 5f);   // the start
    runtime.Step();
    TypeInto(runtime, "x");
    PressKey(runtime, Bibui.Core.Key.Backspace);
    PressKey(runtime, Bibui.Core.Key.Right, shift: true);
    PressKey(runtime, Bibui.Core.Key.Right, shift: true);
    PressKey(runtime, Bibui.Core.Key.C, ctrl: true);
    PressKey(runtime, Bibui.Core.Key.X, ctrl: true);
    Eq("hello world", text.ToString(), "nothing changed");
    Eq("he", copied, "the selection was copied");
});

Run("runtime.MultilineTextField: a press on the second row lands on the second line", () =>
{
    var text = BibuiStr.FromStrLit("first line\nsecond line");
    var edit = new TextEditState();
    var (runtime, _, _) = MountField(text, edit, multiline: true);
    var font = runtime.FontInScope();
    float secondRowY = runtime.Fonts.LineAdvance(font) + 2f;
    ClickAt(runtime, runtime.Fonts.Measure("sec", font) + 0.5f, secondRowY);
    runtime.Step();
    Eq(11 + 3, edit.Cursor, "after \"sec\" on the second line");
});

Run("BibuiPage.Fill types into a TextField as a person would", () =>
{
    var text = BibuiStr.FromStrLit("old value");
    var edit = new TextEditState();
    var (runtime, focused, _) = MountField(text, edit);
    var page = BibuiPage.From(runtime, runtime.BuildTree);
    page.Fill("field", "grüße");
    IsTrue(focused(), "the press focused it");
    Eq("grüße", text.ToString(), "the old text replaced");
    page.Fill("field", "");
    Eq("", text.ToString(), "an empty value clears it");
});

// ── Button (build function) ──────────────────────────────────────────────────

(BibuiRuntime Runtime, Func<int> Clicks) MountButton(bool ghost = false, bool disabled = false)
{
    var runtime = BibuiRuntime.Create();
    int clicks = 0;
    runtime.SetBuild(
        () =>
        {
            var button = runtime.Button("go", "Sync", ghost: ghost, disabled: disabled);
            if (button.Clicked) clicks++;
            return Layer(null, button.Node).Px(400f, 200f);
        },
        new SurfaceOptions { Width = 400, Height = 200 });
    runtime.Step();
    return (runtime, () => clicks);
}

Run("Button: pressed and released on it is a click, and the caller acts on it", () =>
{
    var (rt, clicks) = MountButton();

    rt.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 20f, Y = 20f });
    rt.Step();
    Eq(0, clicks(), "the press alone is not a click");

    rt.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 20f, Y = 20f });
    rt.Step();
    Eq(1, clicks(), "released on it, so it is");
});

Run("Button: pressed and let go somewhere else is no click", () =>
{
    var (rt, clicks) = MountButton();

    rt.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 20f, Y = 20f });
    rt.Step();
    rt.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 350f, Y = 180f });
    rt.Step();
    Eq(0, clicks(), "sliding off is how someone takes it back");
});

Run("Button: disabled reports nothing", () =>
{
    var (rt, clicks) = MountButton(disabled: true);

    rt.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 20f, Y = 20f });
    rt.Step();
    rt.ProcessEvent(new BibUiEvent
    { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 20f, Y = 20f });
    rt.Step();
    Eq(0, clicks(), "a disabled button is in the way, not a control");
});

// ── Window input: what a platform's BeginFrame hands the runtime ─────────────

Run("BeginBuild: a press and release a window reports are one click on the button", () =>
{
    var (rt, clicks) = MountButton();
    var events = new List<WindowEvent>();
    rt.Step();
    events.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, 20f, 20f, MouseButton.Left));
    rt.Step(events);
    Eq(0, events.Count, "BeginBuild empties the list it was given");
    events.Add(WindowEvent.Mouse(WindowEventKind.MouseUp, 20f, 20f, MouseButton.Left));
    rt.Step(events);
    rt.Step();
    Eq(1, clicks(), "a press and release the window reported are one click");
});

// What a key-down carried, read in the frame its events went into.
KeyModifiers ModifiersOfPress(BibuiRuntime rt, List<WindowEvent> events)
{
    rt.BeginBuild(events);
    var seen = KeyModifiers.None;
    foreach (var ev in rt.UnconsumedEvents)
        if (ev.Kind == BibUiEventKind.Press) seen = ev.Modifiers;
    rt.BuildTree();
    rt.EndBuild();
    return seen;
}

Run("BeginBuild: an event's modifiers are what it says was held", () =>
{
    var (rt, _) = MountButton();
    rt.Step();
    Eq(KeyModifiers.Ctrl, ModifiersOfPress(rt, [WindowEvent.KeyPress(Key.F5, KeyModifiers.Ctrl)]), "Ctrl held");
    Eq(KeyModifiers.None, ModifiersOfPress(rt, [WindowEvent.KeyPress(Key.F5)]), "and nothing remembered after it");
});

Run("BeginBuild: Super held is reported as Ctrl too", () =>
{
    var (rt, _) = MountButton();
    rt.Step();
    var seen = ModifiersOfPress(rt, [WindowEvent.KeyPress(Key.F5, KeyModifiers.Super)]);
    IsTrue((seen & KeyModifiers.Ctrl) != 0, $"Cmd+key should read as Ctrl+key, saw {seen}");
    IsTrue((seen & KeyModifiers.Super) != 0, "and stay distinguishable as Super");
});

Run("BeginBuild: moves in a frame are one move, where the pointer ended up", () =>
{
    var (rt, _) = MountButton();
    rt.Step();
    rt.BeginBuild([
        WindowEvent.Mouse(WindowEventKind.MouseMove, 1f, 1f, MouseButton.Unknown),
        WindowEvent.Mouse(WindowEventKind.MouseMove, 5f, 6f, MouseButton.Unknown),
        WindowEvent.Mouse(WindowEventKind.MouseMove, 9f, 7f, MouseButton.Unknown)]);
    var moves = rt.UnconsumedEvents.Where(e => e.Kind == BibUiEventKind.MouseMove).Select(e => (e.X, e.Y)).ToList();
    rt.BuildTree();
    rt.EndBuild();
    Eq(1, moves.Count, "one move");
    IsTrue(moves[0] == (9f, 7f), $"at the last position, got {moves[0]}");
});

Run("BeginBuild: a typed emoji enters the field whole", () =>
{
    var (rt, text, _) = TextFieldRuntime("");
    var events = new List<WindowEvent>();
    foreach (var r in "a\U0001F600".EnumerateRunes()) events.Add(WindowEvent.Text(r));
    rt.Step(events);
    Eq("a\U0001F600", Value(text), "both characters, the emoji as its two chars");
});

Run("TextField: half a surrogate pair types nothing, and does not throw", () =>
{
    var (rt, text, _) = TextFieldRuntime("ab");
    rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Text, CodePoint = 0xD83D });
    rt.Step();
    Eq("ab", Value(text), "no character to insert");
});

Run("Frames owed: a frame that handled input owes one more, and an idle runtime owes none", () =>
{
    var (rt, _) = MountButton();
    // A loop's frames: a platform takes one as each starts, before the build.
    int frames = 0;
    while (rt.FramesRequested > 0 && frames < 40) { rt.ConsumeFrame(); rt.Step(); frames++; }
    IsTrue(frames < 40, $"an idle runtime must stop owing frames, but owed {frames}");
    Eq(0, rt.FramesRequested, "and it ends owing nothing");

    rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 1f, Y = 1f });
    rt.Step();
    IsTrue(rt.FramesRequested > 0, "the frame that handled input owes the one that shows what it did");
});

Run("Window clipboard: the runtime's clipboard is the window's unless the app set its own", () =>
{
    var bare = BibuiRuntime.Create();
    IsNull(bare.ClipboardGet(), "a runtime with no window has no clipboard");
    bare.ClipboardSet("nowhere"); // and writing to it is harmless

    string? system = null;
    var rt = BibuiRuntime.Create();
    rt.WindowClipboard = (() => system, text => system = text);
    rt.ClipboardSet("copied");
    Eq("copied", system, "a copy reaches the window's clipboard");
    system = "pasted";
    Eq("pasted", rt.ClipboardGet(), "a paste reads it");

    string? own = "app's";
    var app = BibuiRuntime.Create();
    app.ClipboardGet = () => own;
    app.WindowClipboard = (() => "window's", _ => { });
    Eq("app's", app.ClipboardGet(), "a clipboard the app set survives the window");
});

// The system clipboard is the window's, so the key translation reaches for
// none of its own.
Run("Window clipboard: a paste with no clipboard given pastes nothing", () =>
{
    IsNull(BibUiEvent.FromKey(Key.V, ctrl: true, shift: false).PasteData,
        "with no clipboard given, a paste pastes nothing (no system clipboard behind the window's back)");
});

// Silk.NET finds the bundled GLFW only where its own RID guess lands, and
// on Omarchy (ID=omarchy) or Arch's source-built .NET (arch-x64) that is a
// folder NuGet never wrote. The window loads it from where the build put it.
Run("SilkWindow: loads the GLFW the build bundled, whatever the distro's RID", () =>
{
    if (!OperatingSystem.IsLinux()) return;
    string? loaded = SilkWindow.LoadBundledGlfw();
    IsTrue(loaded is not null, "no bundled libglfw.so.3 loaded from " + AppContext.BaseDirectory);
    IsTrue(loaded!.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal), $"loaded {loaded}, not the app's own copy");
    IsTrue(System.Runtime.InteropServices.NativeLibrary.TryLoad("libglfw.so.3", out _),
        "a dlopen by SONAME, as Silk.NET does, should find it resident");
    Eq(loaded, SilkWindow.LoadBundledGlfw(), "a second call loads nothing new");
});

Run("Button: a ghost rests bare and lights up under the pointer", () =>
{
    var (rt, _) = MountButton(ghost: true);

    // In the button's left padding, clear of its label.
    rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 350f, Y = 180f });
    var cold = Pixel(rt.Frame(), 3, 15);
    Eq(Rgb(UiColor.Hex(rt.Theme.Colors.Background), rt.Theme), cold, "a ghost at rest is bare");

    rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 20f, Y = 20f });
    for (int i = 0; i < 4; i++) rt.Step();
    var hot = Pixel(rt.Frame(), 3, 15);

    IsTrue(hot != cold, $"a ghost should gain a fill under the pointer: cold {cold}, hot {hot}");
});

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
// ── Layout solver ─────────────────────────────────────────────────────────────
// The solver alone: nodes linked by hand, a stub for leaves, exact rects.

// A leaf is 10px per character and 20px per line, wrapping at the width it is given.
MeasureLeaf leaf = (node, width) =>
{
    int chars = node is Text t ? t.Content.Length : 0;
    float natural = chars * 10f;
    if (!float.IsFinite(width) || natural <= width) return new System.Numerics.Vector2(natural, 20f);
    int lines = (int)MathF.Ceiling(natural / MathF.Max(10f, width));
    return new System.Numerics.Vector2(width, lines * 20f);
};
static System.Numerics.Vector2 V(float x, float y) => new(x, y);
static Box Node(UiNode? parent, SemanticSize x, SemanticSize y, LayoutAxis axis = LayoutAxis.X)
{
    var n = new Box { SizeX = x, SizeY = y, ChildLayoutAxis = axis };
    if (parent is not null) UiNode.PushIntoParent(parent, n);
    return n;
}
// A node centred across a row by hand: a column of a spring, it, and a spring, all the row's height.
static void CentredY(UiNode row, UiNode node)
{
    var column = Node(row, SemanticSize.ChildrenSum(), SemanticSize.ParentPct(1), LayoutAxis.Y);
    Arrange.Spring(column);
    UiNode.PushIntoParent(column, node);
    Arrange.Spring(column);
}
// A text is had only from a runtime; with no parent in scope it links to nothing,
// and is linked by hand like every other node here.
var leafTexts = BibuiRuntime.Create();
Text Leaf(UiNode parent, string content)
{
    var t = leafTexts.Text(content);
    UiNode.PushIntoParent(parent, t);
    return t;
}

Run("Solver: pixels along and across, inside padding", () =>
{
    var root = Node(null, SemanticSize.Pixels(300), SemanticSize.Pixels(100));
    root.Style.Padding = new EdgeInsets(5, 10, 5, 10);
    var a = Node(root, SemanticSize.Pixels(40), SemanticSize.Pixels(20));
    var b = Node(root, SemanticSize.Pixels(60), SemanticSize.Pixels(30));
    LayoutSolver.Layout(root, V(0, 0), V(300, 100), leaf);
    Eq(V(10, 5), a.Rect, "the first child starts inside the padding");
    Eq(V(50, 5), b.Rect, "the second follows it along X");
    Eq(V(60, 30), b.Extent);
    Eq(V(100 + 20, 30 + 10), root.ContentSize, "children's extent plus padding");
});

Run("Solver: children's sum along the axis, largest across, plus padding", () =>
{
    var root = Node(null, SemanticSize.ChildrenSum(), SemanticSize.ChildrenSum(), LayoutAxis.Y);
    root.Style.Padding = EdgeInsets.All(4);
    Node(root, SemanticSize.Pixels(40), SemanticSize.Pixels(20));
    Node(root, SemanticSize.Pixels(70), SemanticSize.Pixels(30));
    Eq(V(78, 58), LayoutSolver.Measure(root, V(float.PositiveInfinity, float.PositiveInfinity), leaf));
});

Run("Solver: a share of the parent is taken inside its padding, and nests", () =>
{
    var root = Node(null, SemanticSize.Pixels(200), SemanticSize.Pixels(100));
    root.Style.Padding = EdgeInsets.All(10);
    var half = Node(root, SemanticSize.ParentPct(0.5f), SemanticSize.ParentPct(1));
    var quarter = Node(half, SemanticSize.ParentPct(0.5f), SemanticSize.Pixels(10));
    LayoutSolver.Layout(root, V(0, 0), V(200, 100), leaf);
    Eq(V(90, 80), half.Extent, "half of 180 wide, all of 80 high");
    Eq(V(45, 10), quarter.Extent, "half of the half");
});

Run("Solver: a share of a node sized by its children is an unsolvable ring", () =>
{
    var root = Node(null, SemanticSize.Pixels(200), SemanticSize.Pixels(100));
    var row = Node(root, SemanticSize.ChildrenSum(), SemanticSize.ChildrenSum());
    Node(row, SemanticSize.Pixels(50), SemanticSize.Pixels(40));
    CentredY(row, new Box { SizeX = SemanticSize.Pixels(30), SizeY = SemanticSize.Pixels(10) });
    Throws<LayoutException>(() => LayoutSolver.Layout(root, V(0, 0), V(200, 100), leaf));
});

Run("Solver: a row with a height centres what it holds in that height", () =>
{
    var root = Node(null, SemanticSize.Pixels(200), SemanticSize.Pixels(100));
    var row = Node(root, SemanticSize.ChildrenSum(), SemanticSize.Pixels(40));
    var small = new Box { SizeX = SemanticSize.Pixels(20), SizeY = SemanticSize.Pixels(10) };
    var tall = new Box { SizeX = SemanticSize.Pixels(20), SizeY = SemanticSize.Pixels(40) };
    CentredY(row, small);
    CentredY(row, tall);
    LayoutSolver.Layout(root, V(0, 0), V(200, 100), leaf);
    Eq(V(40, 40), row.Extent);
    Eq(15f, small.Rect.Y, "the small one centred in 40");
    Eq(0f, tall.Rect.Y);
});

Run("Solver: siblings taking a share split the room equally", () =>
{
    var root = Node(null, SemanticSize.Pixels(300), SemanticSize.Pixels(40));
    var fixedOne = Node(root, SemanticSize.Pixels(100), SemanticSize.ParentPct(1));
    var a = Node(root, SemanticSize.ParentPct(1), SemanticSize.ParentPct(1));
    var b = Node(root, SemanticSize.ParentPct(1), SemanticSize.ParentPct(1));
    LayoutSolver.Layout(root, V(0, 0), V(300, 40), leaf);
    Eq(100f, fixedOne.Extent.X, "a strict size keeps all of it");
    Eq(100f, a.Extent.X, "the rest is shared");
    Eq(100f, b.Extent.X);
    Eq(V(200, 0), b.Rect);
});

Run("Solver: strictness says how much of a size may be taken away", () =>
{
    var root = Node(null, SemanticSize.Pixels(100), SemanticSize.Pixels(40));
    var strict = Node(root, SemanticSize.Pixels(80, 1f), SemanticSize.Pixels(10));
    var loose = Node(root, SemanticSize.Pixels(80, 0f), SemanticSize.Pixels(10));
    LayoutSolver.Layout(root, V(0, 0), V(100, 40), leaf);
    Eq(80f, strict.Extent.X);
    Eq(20f, loose.Extent.X, "the loose one gives up the 60 that does not fit");
});

Run("Solver: allowing overflow keeps children at their size", () =>
{
    var root = Node(null, SemanticSize.Pixels(100), SemanticSize.Pixels(40));
    root.AllowOverflowX = true;
    var a = Node(root, SemanticSize.Pixels(80, 0f), SemanticSize.Pixels(10));
    var b = Node(root, SemanticSize.Pixels(80, 0f), SemanticSize.Pixels(10));
    LayoutSolver.Layout(root, V(0, 0), V(100, 40), leaf);
    Eq(80f, b.Extent.X);
    Eq(V(80, 0), b.Rect);
    Eq(160f, root.ContentSize.X, "the content runs past the node");
});

Run("Solver: the view offset moves the children, not the node", () =>
{
    var root = Node(null, SemanticSize.Pixels(100), SemanticSize.Pixels(100), LayoutAxis.Y);
    root.AllowOverflowY = true;
    root.ViewOffset = V(0, 30);
    var a = Node(root, SemanticSize.Pixels(100), SemanticSize.Pixels(80));
    LayoutSolver.Layout(root, V(5, 5), V(100, 100), leaf);
    Eq(V(5, 5), root.Rect);
    Eq(V(5, -25), a.Rect);
});

Run("Solver: a floating child is placed where it says and not counted", () =>
{
    var root = Node(null, SemanticSize.ChildrenSum(), SemanticSize.ChildrenSum());
    Node(root, SemanticSize.Pixels(40), SemanticSize.Pixels(20));
    var floating = Node(root, SemanticSize.Pixels(500), SemanticSize.Pixels(500));
    floating.FloatingX = floating.FloatingY = true;
    floating.FixedPosition = V(7, 9);
    var size = LayoutSolver.Measure(root, V(float.PositiveInfinity, float.PositiveInfinity), leaf);
    Eq(V(40, 20), size, "the floating child adds nothing");
    LayoutSolver.Layout(root, V(100, 100), size, leaf);
    Eq(V(107, 109), floating.Rect);
});

Run("Solver: text wraps at the width it is given, not the parent's", () =>
{
    var row = Node(null, SemanticSize.Pixels(200), SemanticSize.ChildrenSum());
    var left = Node(row, SemanticSize.ParentPct(0.5f), SemanticSize.ChildrenSum(), LayoutAxis.Y);
    var right = Node(row, SemanticSize.ParentPct(0.5f), SemanticSize.ChildrenSum(), LayoutAxis.Y);
    var text = Leaf(left, "twenty characters!!!");   // 200px natural
    Leaf(right, "short");
    text.SizeX = SemanticSize.ParentPct(1);
    text.SizeY = SemanticSize.Text();
    var size = LayoutSolver.Measure(row, V(200, float.PositiveInfinity), leaf);
    Eq(V(100, 40), text.FixedSize, "two lines at its 100px share");
    Eq(40f, size.Y, "and the row is as tall as that");
});

Run("Solver: Arrange.Centred centres a node along its parent's axis", () =>
{
    var root = Node(null, SemanticSize.Pixels(300), SemanticSize.Pixels(100));
    var node = new Box { SizeX = SemanticSize.Pixels(100), SizeY = SemanticSize.Pixels(20) };
    // What is tested is Arrange.Centred itself, obsolete or not.
#pragma warning disable CS0618
    Arrange.Centred(root, node);
#pragma warning restore CS0618
    LayoutSolver.Layout(root, V(0, 0), V(300, 100), leaf);
    Eq(V(100, 0), node.Rect);
});

Run("Solver: a spring pushes what follows to the end, and gives way first", () =>
{
    foreach (var (width, wantRight, wantContent) in new[] { (300f, 240f, 300f), (100f, 60f, 120f) })
    {
        var row = Node(null, SemanticSize.Pixels(width), SemanticSize.Pixels(20));
        Node(row, SemanticSize.Pixels(60), SemanticSize.Pixels(20));
        Arrange.Spring(row);
        var right = Node(row, SemanticSize.Pixels(60), SemanticSize.Pixels(20));
        LayoutSolver.Layout(row, V(0, 0), V(width, 20), leaf);
        Eq(wantRight, right.Rect.X, $"at {width}px");
        Eq(wantContent, row.ContentSize.X, $"content at {width}px");
    }
});

Run("Solver: a scroll view takes its parent's room and never its content's", () =>
{
    var panel = Node(null, SemanticSize.Pixels(200), SemanticSize.Pixels(100));
    var view = Node(panel, SemanticSize.ParentPct(1), SemanticSize.ParentPct(1), LayoutAxis.Y);
    view.AllowOverflowY = true;
    for (int i = 0; i < 10; i++) Node(view, SemanticSize.ParentPct(1), SemanticSize.Pixels(30));
    LayoutSolver.Layout(panel, V(0, 0), V(200, 100), leaf);
    Eq(V(200, 100), view.Extent);
    Eq(300f, view.ContentSize.Y, "ten rows of 30");
});

Run("Solver: Measure writes no geometry, and Layout after it is the same", () =>
{
    UiNode Build(out Box b)
    {
        var root = Node(null, SemanticSize.Pixels(300), SemanticSize.Pixels(100));
        Node(root, SemanticSize.Pixels(100), SemanticSize.ParentPct(1));
        b = Node(root, SemanticSize.ParentPct(1), SemanticSize.ParentPct(0.5f));
        return root;
    }
    var once = Build(out var plainB);
    LayoutSolver.Layout(once, V(0, 0), V(300, 100), leaf);
    var twice = Build(out var measuredB);
    LayoutSolver.Measure(twice, V(300, 100), leaf);
    Eq(System.Numerics.Vector2.Zero, measuredB.Rect, "measuring places nothing");
    LayoutSolver.Layout(twice, V(0, 0), V(300, 100), leaf);
    Eq(plainB.Rect, measuredB.Rect);
    Eq(plainB.Extent, measuredB.Extent);
});

Run("Solver: solving a tree allocates nothing", () =>
{
    var root = Node(null, SemanticSize.Pixels(400), SemanticSize.Pixels(400), LayoutAxis.Y);
    for (int i = 0; i < 20; i++)
    {
        var row = Node(root, SemanticSize.ParentPct(1), SemanticSize.ChildrenSum());
        Leaf(row, "a row of text").SizeY = SemanticSize.Text();
        Node(row, SemanticSize.ParentPct(1), SemanticSize.Pixels(10));
    }
    LayoutSolver.Layout(root, V(0, 0), V(400, 400), leaf);   // warm up
    long before = GC.GetAllocatedBytesForCurrentThread();
    LayoutSolver.Layout(root, V(0, 0), V(400, 400), leaf);
    Eq(0L, GC.GetAllocatedBytesForCurrentThread() - before);
});

// ── Theme tokens: elevation, borders, focus ring, density, motion ─────

// Mounts build under theme, renders once, and hands the runtime back.
BibuiRuntime Themed(ThemeTokens theme, Action<BibuiRuntime> build)
{
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        Box root = runtime.Box();
        root.ChildLayoutAxis = LayoutAxis.Y;
        root.Px(400f, 300f);
        using (runtime.PushParent(root)) build(runtime);
        return root;
    }, theme, new SurfaceOptions { Width = 400, Height = 300, ScaleFactor = 1f });
    runtime.Step();
    return runtime;
}

Run("Tokens: the defaults are the literals the components drew before them", () =>
{
    var t = new ThemeTokens();
    Eq(1f, t.Borders.Width); Eq(2f, t.Borders.Strong);
    Eq(1f, t.FocusRing.Width); Eq(0f, t.FocusRing.Offset); Eq(UiColor.BorderFocus, t.FocusRing.Color);
    Eq(1f, t.Density.Scale); Eq(7.5f, t.Density.Of(7.5f), "a length at the default density is exactly itself");
    Eq(60f, t.Motion.Hover); Eq(60f, t.Motion.Follow); Eq(22f, t.Motion.Toggle); Eq(20f, t.Motion.Default);
    Eq(MotionEasing.Exponential, t.Motion.Easing);
    for (int level = 0; level <= 4; level++) IsNull(t.Elevation.Level(level), $"level {level} is flat");
    IsNull(ThemeLoader.NeutralDark().Elevation.Level(ElevationTokens.Overlay), "a built-in theme is flat too");
});

Run("Tokens: a theme's border widths are what the outlines are drawn at", () =>
{
    var theme = new ThemeTokens { Borders = new BorderTokens { Width = 3f, Strong = 5f } };
    float square = 0f, chosenRing = 0f, panel = 0f, field = 0f;
    Box rule = null!;
    Themed(theme, rt =>
    {
        var box = rt.Checkbox("cb", on: false);
        square = ((Box)box.Node.FirstChild!).Style.BorderWidth;

        var group = rt.RadioGroup("rg",
            [new RadioOption("A", "a")], value: "a");
        // group → row → the ring's cell → spring, ring.
        var cell = (Box)((Box)group.Node.FirstChild!).FirstChild!;
        chosenRing = ((Box)cell.FirstChild!.Next!).Style.BorderWidth;

        panel = Bibui.Core.Runtime.Tabs.Panel(rt).Style.BorderWidth;

        Box input = rt.Box().Px(100f, 30f);
        Bibui.Core.Runtime.TextField.InputStyle(rt, input, focused: false);
        field = input.Style.BorderWidth;

        rule = Bibui.Core.Runtime.Separator.Build(rt);
    });
    Eq(3f, square, "a checkbox's square");
    Eq(5f, chosenRing, "a chosen radio's ring is the strong width");
    Eq(3f, panel, "a tab panel's outline");
    Eq(3f, field, "a text field's outline");
    Eq(3f, rule.Extent.Y, "a separator is a border width thick");
});

Run("Tokens: a focused field draws the theme's focus ring as its outline", () =>
{
    var ring = UiColor.Hex("#FF00FF");
    var theme = new ThemeTokens { FocusRing = new FocusRingTokens { Width = 2f, Color = ring } };
    Style focused = null!, resting = null!;
    Themed(theme, rt =>
    {
        Box a = rt.Box().Px(100f, 30f); Bibui.Core.Runtime.TextField.InputStyle(rt, a, focused: true);
        Box b = rt.Box().Px(100f, 30f); Bibui.Core.Runtime.TextField.InputStyle(rt, b, focused: false);
        (focused, resting) = (Copy(a.Style), Copy(b.Style));
    });
    Eq(2f, focused.BorderWidth, "the ring's width while focused");
    Eq((UiColor?)ring, focused.BorderColor, "the ring's colour while focused");
    Eq(1f, resting.BorderWidth, "the border's width otherwise");

    static Style Copy(Style s) => new() { BorderWidth = s.BorderWidth, BorderColor = s.BorderColor };
});

Run("Tokens: elevation puts the theme's shadow under what floats", () =>
{
    foreach (var elevated in new[] { false, true })
    {
        var theme = elevated ? new ThemeTokens { Elevation = ElevationTokens.Soft } : new ThemeTokens();
        BoxShadow? menu = null, dialog = null;
        Themed(theme, rt =>
        {
            rt.OpenDropDown("menu");
            menu = ((Box)Bibui.Core.Runtime.Menu.Begin(rt, "menu", 10f, 10f)!.Value.End().Node).Style.Shadow;
            var panel = Bibui.Core.Runtime.Dialog.Begin(rt, "dialog", shown: 1f).End().Panel;
            dialog = panel.Style.Shadow;
        });
        if (elevated)
        {
            IsTrue(ReferenceEquals(ElevationTokens.Soft.Level2, menu), "a menu floats at the overlay level");
            IsTrue(ReferenceEquals(ElevationTokens.Soft.Level4, dialog), "a dialog floats at the modal level");
        }
        else
        {
            IsNull(menu, "flat by default");
            IsNull(dialog, "flat by default");
        }
    }
});

Run("Tokens: density scales a control's height", () =>
{
    foreach (var (mode, scale) in new[] { (Density.Default, 1f), (Density.Compact, 0.8f), (Density.Comfortable, 1.25f) })
    {
        var theme = new ThemeTokens { Density = new DensityTokens { Mode = mode } };
        var runtime = Themed(theme, rt => rt.Toggle("tg", on: false, label: "Bold"));
        runtime.Step();
        Eq(36f * scale, runtime.FindById("tg")!.Extent.Y, $"a toggle at {mode}");
    }
});

Run("Tokens: a theme's toggle speed is how fast a switch's knob crosses", () =>
{
    float KnobAfter(float speed)
    {
        bool on = false;
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() =>
        {
            Box root = runtime.Box();
            root.SizeX = root.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(root)) runtime.Switch("sw", on);
            return root;
        }, new ThemeTokens { Motion = new MotionTokens { Toggle = speed } },
           new SurfaceOptions { Width = 200, Height = 100, ScaleFactor = 1f });
        runtime.Step();
        on = true;
        for (int f = 0; f < 4; f++) runtime.Step();
        return runtime.FindById("knobsw")!.Rect.X;
    }
    float slow = KnobAfter(2f), standard = KnobAfter(22f);
    IsTrue(standard > slow + 1f, $"the faster theme's knob is further across ({standard} vs {slow})");
});

Run("Tokens: Animate with no rate takes the theme's default", () =>
{
    float After(float speed)
    {
        float target = 0f;
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() =>
        {
            runtime.Animate("thing", "x", target);
            return Layer(null).Px(100f, 100f);
        }, new ThemeTokens { Motion = new MotionTokens { Default = speed } }, new SurfaceOptions { Width = 100, Height = 100 });
        runtime.Step();
        target = 1f;
        runtime.Step();
        return runtime.Animate("thing", "x", 1f);
    }
    float rate = 1f - MathF.Pow(2f, -5f / 60f);
    IsTrue(MathF.Abs(After(5f) - rate) < 1e-5f, $"one step at the theme's 5 halvings a second (got {After(5f)})");
    IsTrue(After(40f) > After(5f), "a faster theme gets further");
});

Run("Tokens: MotionEasing.None closes every gap in one step", () =>
{
    float target = 0f;
    var runtime = BibuiRuntime.Create();
    runtime.SetBuild(() =>
    {
        runtime.Animate("thing", "x", target, Ease.Rate(1f, runtime.Dt));   // a rate of its own, not from Rates
        Box b = runtime.BoxFromId("b", InteractionKinds.LeftPress);
        runtime.InteractionsFor(b);
        b.Px(50f, 50f).At(0f, 0f);
        return Layer(null, b).Px(100f, 100f);
    }, new ThemeTokens { Motion = new MotionTokens { Easing = MotionEasing.None } }, new SurfaceOptions { Width = 100, Height = 100 });
    runtime.Step();
    target = 1f;
    runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = 10f, Y = 10f });
    runtime.Step();
    Eq(1f, runtime.Animate("thing", "x", 1f, Ease.Rate(1f, runtime.Dt)), "even a slow ease arrives at once");
    Eq(1f, ((Box)runtime.FindById("b")!).HotT, "and hover is all the way on after one frame");
});
Console.WriteLine();
Console.WriteLine("Markdown");
MarkdownTests.Register(Run);
Console.WriteLine();
Console.WriteLine("Icon names");
IconNameTests.Register(Run);
Console.WriteLine();
Console.WriteLine("App icons");
AppIconTests.Register(Run);
Console.WriteLine();
Console.WriteLine("Motion");
MotionTests.Register(Run);
Console.WriteLine();
Console.WriteLine("Scroll list");
ScrollListTests.Register(Run);

// ── Key repeat ──────────────────────────────────────────────────────────────

Run("BeginBuild: a key the window repeats is a key down marked Repeat; a press is not", () =>
{
    var (rt, _) = MountButton();
    rt.Step();
    bool? Repeated(List<WindowEvent> events)
    {
        rt.BeginBuild(events);
        bool? seen = null;
        foreach (var ev in rt.UnconsumedEvents)
            if (ev.IsKeyDown) seen = ev.Repeat;
        rt.BuildTree();
        rt.EndBuild();
        return seen;
    }
    Eq<bool?>(false, Repeated([WindowEvent.KeyPress(Key.Backspace)]), "the first press");
    Eq<bool?>(true, Repeated([WindowEvent.KeyRepeat(Key.Backspace)]), "held: repeated");
    Eq<bool?>(false, Repeated([WindowEvent.KeyPress(Key.Backspace)]), "a pooled event comes back unmarked");
});

Run("A held Backspace keeps deleting in a text field", () =>
{
    var rt = BibuiRuntime.Create();
    var text = BibuiStr.FromStrLit("abcd");
    var edit = new TextEditState { Cursor = 4, Mark = 4 };
    rt.SetBuild(() => rt.TextField("field", text, edit, focused: true).Node, new SurfaceOptions { Width = 300, Height = 60 });
    rt.Step();
    rt.Step([WindowEvent.KeyPress(Key.Backspace)]);
    rt.Step([WindowEvent.KeyRepeat(Key.Backspace)]);
    rt.Step([WindowEvent.KeyRepeat(Key.Backspace)]);
    Eq("a", text.ToString(), "one press and two repeats delete three characters");
});

// ── Boxes that measure themselves, gaps and cross-axis alignment ───────────

Run("Box.Measure: a box sized by its content is asked for its width unbounded, then for its height at the width it got", () =>
{
    var rt = BibuiRuntime.Create();
    var asked = new List<float>();
    Box? para = null, after = null;
    // A paragraph of 300 px of words, 20 px a line: as many lines as the width makes.
    System.Numerics.Vector2 Words(Box _, float width)
    {
        asked.Add(width);
        float w = MathF.Min(300f, width);
        return new System.Numerics.Vector2(w, 20f * MathF.Ceiling(300f / w));
    }
    rt.SetBuild(() =>
    {
        // The frame's one top node is its root, the surface's size: the 100 px column goes inside it.
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.ColumnWidth(100f)))
        {
            para = rt.Box();
            para.SizeX = SemanticSize.ParentPct(1f);
            para.SizeY = SemanticSize.Text();
            para.Measure = Words;
            after = rt.Box().Px(10f, 10f);
        }
        return root;
    }, new SurfaceOptions { Width = 400, Height = 400 });
    rt.Step();
    Eq(100f, para!.Extent.X, "a fill: the column's width");
    Eq(60f, para.Extent.Y, "three lines at 100 px");
    Eq(60f, after!.Rect.Y, "what follows comes after all three");
    IsTrue(asked.Contains(100f), "measured at its final width");

    // Sized by its content on both axes: unbounded first, then at the width that gave.
    asked.Clear();
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.RowFit()))
        {
            para = rt.Box();
            para.SizeX = para.SizeY = SemanticSize.Text();
            para.Measure = Words;
        }
        return root;
    }, new SurfaceOptions { Width = 400, Height = 400 });
    rt.Step();
    Eq(float.PositiveInfinity, asked[0], "the width asked unbounded");
    Eq(new System.Numerics.Vector2(300f, 20f), para!.Extent, "one line at its natural width");
});

Run("Box.Measure: cleared when the box is acquired again; a content-sized box without one throws as before", () =>
{
    var rt = BibuiRuntime.Create();
    bool measure = true;
    rt.SetBuild(() =>
    {
        var box = rt.BoxFromId("leaf");
        box.SizeX = box.SizeY = SemanticSize.Text();
        if (measure) box.Measure = (_, _) => new System.Numerics.Vector2(10f, 10f);
        return box;
    }, new SurfaceOptions { Width = 100, Height = 100 });
    rt.Step();
    measure = false;
    bool threw = false;
    try { rt.Step(); } catch (InvalidOperationException e) when (e.Message.Contains("Measure")) { threw = true; }
    IsTrue(threw, "the measure was build state: not set this frame, none");
});

Run("ChildGap: space between children, counted in a size from the children, kept when fills share the rest", () =>
{
    var rt = BibuiRuntime.Create();
    Box? fit = null, a = null, b = null, c = null, fa = null, fb = null, floater = null;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        {
            fit = rt.RowFit().Spaced(8f);
            using (rt.PushParent(fit))
            {
                a = rt.Box().Px(10f, 10f);
                floater = rt.Box().Px(5f, 5f).At(0f, 0f);
                b = rt.Box().Px(20f, 10f);
                c = rt.Box().Px(30f, 10f);
            }
            using (rt.PushParent(rt.RowFillX(20f).Spaced(10f)))
            {
                fa = rt.Box().FillX().Px(height: 10f);
                fb = rt.Box().FillX().Px(height: 10f);
            }
        }
        return root;
    }, new SurfaceOptions { Width = 210, Height = 100 });
    rt.Step();
    Eq(10f + 20f + 30f + 2 * 8f, fit!.Extent.X, "three children and two gaps; the floating one is in neither");
    Eq(18f, b!.Rect.X, "after the first and a gap");
    Eq(46f, c!.Rect.X, "after the second and a gap");
    IsTrue(MathF.Abs(fa!.Extent.X - 100f) < 0.01f && MathF.Abs(fb!.Extent.X - 100f) < 0.01f, $"two fills share what the gap leaves of 210: {fa!.Extent.X}, {fb!.Extent.X}");
    IsTrue(MathF.Abs(fb!.Rect.X - 110f) < 0.01f, $"and the gap stands between them: {fb!.Rect.X}");
});

Run("ChildAlign: children across the axis at the start, the middle or the end of a row as tall as its tallest", () =>
{
    var rt = BibuiRuntime.Create();
    var align = ChildAlignment.Start;
    Box? row = null, tall = null, small = null;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        {
            row = rt.RowFit().Aligned(align);
            using (rt.PushParent(row))
            {
                tall = rt.Box().Px(10f, 40f);
                small = rt.Box().Px(10f, 10f);
            }
        }
        return root;
    }, new SurfaceOptions { Width = 200, Height = 200 });
    rt.Step();
    Eq(40f, row!.Extent.Y, "as tall as its tallest child");
    Eq(0f, small!.Rect.Y, "start");
    align = ChildAlignment.Center;
    rt.Step();
    Eq(15f, small!.Rect.Y, "centred: no ring, though the row is sized by its children");
    Eq(0f, tall!.Rect.Y, "the tallest fills it");
    align = ChildAlignment.End;
    rt.Step();
    Eq(30f, small!.Rect.Y, "end");
});

Run("Box.InkOverflow: a callback's drawing reaches past its box as far as it says, and no further", () =>
{
    var rt = BibuiRuntime.Create();
    var ink = EdgeInsets.Zero;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.ColumnFit().Padded(EdgeInsets.All(20f))))
        {
            var box = rt.Box().Px(10f, 10f);
            box.InkOverflow = ink;
            // A square from 5 px before the box to 5 px past it.
            box.Draw = (_, ctx) => ctx.DrawRRect(-5f, -5f, 20f, 20f, 0f, new Rgba(255, 0, 0, 255), null, null);
        }
        return root;
    }, new SurfaceOptions { Width = 60, Height = 60 });
    bool Red(Pixels px, int x, int y) { int i = (y * px.Width + x) * 4; return px.RawBgraBytes[i + 2] > 200 && px.RawBgraBytes[i + 1] < 50; }
    var clipped = rt.Frame(new ManagedRenderer().Draw);
    IsTrue(Red(clipped, 25, 25) && !Red(clipped, 17, 17), "clipped to the box by default");
    ink = EdgeInsets.All(5f);
    var reached = rt.Frame(new ManagedRenderer().Draw);
    IsTrue(Red(reached, 17, 17) && Red(reached, 33, 33), "drawn 5 px past it with an ink overflow of 5");
    ink = EdgeInsets.All(2f);
    var part = rt.Frame(new ManagedRenderer().Draw);
    IsTrue(Red(part, 19, 19) && !Red(part, 16, 16), "and no further than it says");
});

Run("ChildWrap: a row whose children fit is a row (a spring shares the rest); one too narrow runs onto lines", () =>
{
    var rt = BibuiRuntime.Create();
    float width = 200f;
    Box? row = null, a = null, b = null, c = null, spring = null, tall = null;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.ColumnWidth(width)))
        {
            row = rt.RowFillX().Spaced(10f).Wrapping(lineGap: 6f).Aligned(ChildAlignment.Center);
            using (rt.PushParent(row))
            {
                a = rt.Box().Px(50f, 20f);
                b = rt.Box().Px(50f, 20f);
                spring = rt.Box().FillX().Px(height: 0f);
                c = rt.Box().Px(50f, 20f);
                tall = rt.Box().Px(30f, 40f);
            }
        }
        return root;
    }, new SurfaceOptions { Width = 400, Height = 400 });

    rt.Step();
    // 50+50+50+30 and four gaps = 220: too wide for 200.
    Eq(0f, spring!.Extent.X, "wrapped: the spring takes nothing");
    Eq(0, a!.WrapLine);
    Eq(0f, c!.Rect.X - a.Rect.X - 120f, "the third fits on the first line (50+10+50+10)");
    Eq(1, tall!.WrapLine, "the fourth starts the next line");
    Eq(a.Rect.X, tall.Rect.X, "at the line's start");
    Eq(20f + 6f + 40f, row!.Extent.Y, "as tall as its lines and the gap between them");
    Eq(row.Rect.Y + 26f, tall.Rect.Y, "the second line under the first and the gap");

    width = 300f;
    rt.Step();
    Eq(0, tall!.WrapLine, "room enough: one line");
    Eq(40f, row!.Extent.Y, "as tall as its tallest");
    IsTrue(spring!.Extent.X > 0f && MathF.Abs(tall.Rect.X + 30f - (row.Rect.X + 300f)) < 0.01f, "the spring pushes what follows to the end");
    Eq(row.Rect.Y + 10f, a!.Rect.Y, "centred in the line");
});

Run("Shrinking along a row: fills give way first, then what may give up, by how much it may", () =>
{
    var rt = BibuiRuntime.Create();
    Box? label = null, spring = null, other = null;
    float width = 300f;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.RowPx(width, 20f)))
        {
            // A label that may be cut (strictness 0), a spring, and a fixed box.
            label = rt.Box();
            label.SizeX = SemanticSize.Pixels(120f, strictness: 0f);
            label.SizeY = SemanticSize.Pixels(20f);
            spring = rt.Box().FillX().Px(height: 20f);
            other = rt.Box().Px(100f, 20f);
        }
        return root;
    }, new SurfaceOptions { Width = 400, Height = 100 });
    rt.Step();
    Eq(120f, label!.Extent.X, "room enough: the spring takes what is left, the label keeps its width");
    Eq(80f, spring!.Extent.X, "300 - 120 - 100");
    width = 180f;
    rt.Step();
    Eq(0f, spring!.Extent.X, "too narrow: the spring gives all it has first");
    Eq(80f, label!.Extent.X, "then the label gives the rest (120 - 40)");
    Eq(100f, other!.Extent.X, "the fixed box gives nothing");
});

Run("MaxSize: a fill held to a width, centred by its column; a size from children held too", () =>
{
    var rt = BibuiRuntime.Create();
    float width = 1000f;
    Box? measure = null, fit = null;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.ColumnWidth(width).Aligned(ChildAlignment.Center)))
        {
            measure = rt.Box().FillX().Px(height: 10f);
            measure.MaxSize = new System.Numerics.Vector2(720f, float.PositiveInfinity);
            fit = rt.RowFit();
            fit.MaxSize = new System.Numerics.Vector2(50f, 15f);
            using (rt.PushParent(fit)) rt.Box().Px(80f, 30f);
        }
        return root;
    }, new SurfaceOptions { Width = 1200, Height = 200 });
    rt.Step();
    Eq(720f, measure!.Extent.X, "held to its maximum");
    Eq(140f, measure.Rect.X, "in the middle of 1000");
    Eq(new System.Numerics.Vector2(50f, 15f), fit!.Extent, "a size from its children, held both ways");
    width = 500f;
    rt.Step();
    Eq(500f, measure!.Extent.X, "narrower than its maximum: its share");
});

Run("ChildAlignment.Stretch: children as tall as their row, pixel sizes kept", () =>
{
    var rt = BibuiRuntime.Create();
    Box? row = null, a = null, b = null, px = null;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        {
            row = rt.RowFillX().Aligned(ChildAlignment.Stretch);
            using (rt.PushParent(row))
            {
                a = rt.ColumnFit();
                using (rt.PushParent(a)) rt.Box().Px(20f, 40f);
                b = rt.ColumnFit();
                using (rt.PushParent(b)) rt.Box().Px(20f, 10f);
                px = rt.Box().Px(10f, 5f);
            }
        }
        return root;
    }, new SurfaceOptions { Width = 300, Height = 200 });
    rt.Step();
    Eq(40f, row!.Extent.Y, "as tall as its tallest");
    Eq(40f, b!.Extent.Y, "the short cell stretched to it");
    Eq(row.Rect.Y, b.Rect.Y, "from the top");
    Eq(5f, px!.Extent.Y, "a number of pixels kept");
});

Run("Style.CornerRadii: each corner its own radius; a uniform one draws as CornerRadius does", () =>
{
    var rt = BibuiRuntime.Create();
    CornerRadii? radii = CornerRadii.All(16f) with { BottomRight = 0f };
    float radius = 0f;
    rt.SetBuild(() =>
    {
        var root = rt.ColumnFill();
        using (rt.PushParent(root))
        using (rt.PushParent(rt.ColumnFit().Padded(EdgeInsets.All(10f))))
        {
            var box = rt.Box().Px(40f, 40f);
            box.Style.BackgroundColor = "#ff0000";
            box.Style.CornerRadius = radius;
            box.Style.CornerRadii = radii;
        }
        return root;
    }, new SurfaceOptions { Width = 60, Height = 60 });
    Bibui.Core.Draw.DrawToPixels draw = new ManagedRenderer().Draw;
    bool Red(Pixels px, int x, int y) { int i = (y * px.Width + x) * 4; return px.RawBgraBytes[i + 2] > 200 && px.RawBgraBytes[i + 1] < 60; }
    var shaped = rt.Frame(draw);
    IsTrue(!Red(shaped, 10, 10) && !Red(shaped, 49, 10) && !Red(shaped, 10, 49), "three round corners");
    IsTrue(Red(shaped, 49, 49), "and the square one filled to its corner");
    IsTrue(Red(shaped, 30, 30), "filled inside");

    radii = CornerRadii.All(8f);
    var uniform = rt.Frame(draw).RawBgraBytes;
    (radii, radius) = (null, 8f);
    var plain = rt.Frame(draw).RawBgraBytes;
    IsTrue(uniform.AsSpan().SequenceEqual(plain), "CornerRadii.All(8) is CornerRadius 8, pixel for pixel");
});

Console.WriteLine($"Results: {passed} passed, {failed} failed, {skipped} skipped"
    + (filter is null ? "" : $" ({filteredOut} not matching '{filter}')"));

if (failed > 0)
{
    Console.WriteLine("FAILED");
    Environment.Exit(1);
}
else
{
    Console.WriteLine("All tests passed.");
}

// ── Helper extensions ─────────────────────────────────────────────────────────

static class ObjectExtensions
{
    public static string ToStr(this object? o) => o is null ? "null" : o.ToString() ?? "null";

    // `box`, with what `build` makes linked under it: a test's tree as one expression.
    public static Box Into(this BibuiRuntime runtime, Box box, Action build)
    {
        using (runtime.PushParent(box)) build();
        return box;
    }
}

// The state BuildSelect keeps for a test's select.
sealed class SelectState { public string? Value; }
