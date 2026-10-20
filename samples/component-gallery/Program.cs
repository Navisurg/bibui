using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;
using ComponentGallery;
using Bibui.Draw;
using Bibui.Raster.Skia;
using Bibui.Runtime.Platform;
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;

// ── Component Gallery ─────────────────────────────────────────────────────────
// A page for each of bibui's components, and for the patterns an application
// composes from them. The sidebar lists the pages by group; the panel to its
// right shows the chosen one, working: every demo is built from runtime pieces
// the way an application builds it.
// The window is resizable: the sidebar narrows, then folds behind a menu
// bar (ShellLayout), and the page scrolls.
//
// Usage (GalleryOptions.Usage has the whole list):
//   dotnet run             → opens interactive window (Silk host + Skia)
//   dotnet run -- reference → renders every page, and its open state, to reference/
//   dotnet run -- check    → lays every page out at several window sizes and
//                            reports anything cut, spilling, or off the window
//   dotnet run -- --host wayland --raster managed
//                          → raw Wayland host + managed rasterizer with FreeType/HarfBuzz text
//   dotnet run -- --accent "#E11D48" --mode light
//                          → the same gallery in a theme generated from one seed
//   dotnet run -- --first-frame
//                          → renders the first frame headless and exits (CI's start-up check)
//   dotnet run -- --host wayland --raster managed --dry-run
//                          → prints the choice and exits, without opening a window

if (GalleryOptions.Parse(args, out string? error) is not { } options)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(GalleryOptions.Usage);
    Environment.Exit(2);
    return;
}
foreach (var notice in options.Notices)
    Console.Error.WriteLine(notice);

if (options.Help)
{
    Console.WriteLine(GalleryOptions.Usage);
    return;
}

if (options.DryRun)
{
    Console.WriteLine(options.Describe());
    return;
}

if (options.Reference)
{
    WriteReferences(options);
    return;
}

if (options.Check)
{
    Environment.Exit(CheckLayouts(options));
    return;
}

var runtime = BibuiRuntime.Create();
runtime.SetTheme(options.Theme());
runtime.Surface = Surface();
HotReloadListener.Register(runtime.RequestFrame);
var gallery = new Gallery { State = InitialState() };

// CI's start-up check: one frame headless, then exit; a throw fails it.
if (options.FirstFrame)
{
    using var cpu = new SkiaCpuRenderer();
    DemoTexture.SkiaCpu = cpu;
    runtime.BeginBuild();
    BuildGallery(runtime, gallery);
    runtime.EndBuild();
    FramePixels.Bgra(runtime, cpu.Draw, out _, out _);
    return;
}

// Off unless asked for: BIBUI_TEST_SERVER=1 (test_all_pages.sh says how); the
// loop steps it. Its screenshots draw with a CPU renderer of their own: the
// window's draws on the GPU, or keeps damage state for the window's buffers.
using var testServer = BibuiTestServer.StartIfEnabled(runtime, options.Port,
    options.Raster == GalleryRaster.Managed ? new ManagedRenderer().Draw : new SkiaCpuRenderer().Draw);

var size = new PlatformWindowOptions("Component Gallery", Surface().Width, Surface().Height);
if (options.Host == GalleryHost.Wayland)
{
    using var window = WaylandWindow.Open(runtime, size, new WaylandHostOptions
    {
        AppId = "bibui-component-gallery",
        MinWidth = ShellLayout.MinWidth,
        MinHeight = ShellLayout.MinHeight,
    });
    // The renderer --raster names, bound to the window's memory; the texture page makes its textures on it.
    SkiaCpuRenderer? skia = options.Raster == GalleryRaster.Skia ? new SkiaCpuRenderer(window.Cpu) : null;
    ManagedRenderer? managed = skia is null ? new ManagedRenderer(window.Cpu) : null;
    (DemoTexture.SkiaCpu, DemoTexture.Managed) = (skia, managed);
    while (WaylandWindow.BeginFrame(window, runtime))
    {
        // While a test-server command runs, the server is the keyboard and mouse.
        runtime.BeginBuild(testServer?.Step(window.Events) ?? window.Events);
        BuildGallery(runtime, gallery);
        runtime.EndBuild();
        WaylandWindow.BeginDraw(window);
        if (skia is not null) skia.Draw(runtime.DrawList);
        else managed!.Draw(runtime.DrawList);
        WaylandWindow.EndDraw(window);
        WaylandWindow.EndFrame(window, runtime);
    }
    skia?.Dispose();
    managed?.Dispose();
}
else
{
    using var window = SilkWindow.Open(runtime, size);
    using var skia = new SkiaGlRenderer(window.Gl);
    DemoTexture.SkiaGl = skia;   // the texture page's textures, on the renderer that draws them
    window.OnRefresh = Frame;  // keeps drawing during a resize on Windows and macOS
    while (SilkWindow.BeginFrame(window, runtime)) Frame();

    void Frame()
    {
        // While a test-server command runs, the server is the keyboard and mouse.
        runtime.BeginBuild(testServer?.Step(window.Events) ?? window.Events);
        BuildGallery(runtime, gallery);
        runtime.EndBuild();
        SilkWindow.BeginDraw(window);
        skia.Draw(runtime.DrawList);
        SilkWindow.EndDraw(window);
        SilkWindow.EndFrame(window, runtime);
    }
}

// ── Sidebar ───────────────────────────────────────────────────────────────────

static (string Group, string[] Routes)[] NavGroups() => new[]
    {
        ("Inputs & Forms", new[] { "Button", "Input", "Textarea", "TextField", "Checkbox", "Switch", "RadioGroup",
            "Select", "Slider", "Toggle",
            "ToggleGroup", "DatePicker", "Calendar", "TimePicker", "ColorPicker" }),
        ("Display", new[] { "Text", "Badge", "Avatar", "Card", "Alert", "Separator", "Label",
            "Progress", "Skeleton", "Spinner", "RatioBox", "KeyboardShortcut", "Table", "Markdown", "Texture",
            "PagedTable", "FormField", "ListItem", "EmptyState", "Toast" }),
        ("Overlays", new[] { "Dialog", "AlertDialog", "Popover", "HoverPreview", "Tooltip",
            "Drawer", "DropdownMenu", "ContextMenu", "CommandPalette" }),
        ("Navigation", new[] { "Tabs", "Accordion", "Expander", "NavBar", "Sidebar",
            "Breadcrumb", "Pagination", "MenuBar" }),
        ("Layout", new[] { "SplitPane", "Carousel", "ButtonStrip", "Stack", "InteractiveBox", "Section", "Scrollbar", "ScrollList" }),
    };

// Beside the page at its width, or, folded (width 0), the whole window under
// the menu bar, which carries the title instead.
static void BuildSidebar(BibuiRuntime runtime, GalleryState state, Gallery gallery, float width)
{
    var groups = NavGroups();
    bool folded = width == 0f;

    var sidebar = runtime.BoxFromId("sidebar");
    sidebar.ChildLayoutAxis = LayoutAxis.Y;
    if (folded) sidebar.FillX();
    else sidebar.Px(width: width);
    sidebar.Styled(new Style
    {
        BackgroundColor = UiColor.Background,
        BorderColor = UiColor.Border,
        BorderWidth = 1f,
    }).FillY();

    using (runtime.PushParent(sidebar))
    {
        if (!folded)
        {
            // Logo / title
            using (runtime.PushParent(runtime.ColumnFillX().Padded(new EdgeInsets(16, 20, 12, 20))))
                Bold(runtime, "Gallery", ComponentSize.Lg, TextColor.Primary);
            Separator.Build(runtime).FillX();
        }

        // The list and its bar side by side. Entries are acquired under the list,
        // so they lay out along its axis and are clipped to it; the list asks
        // after them.
        var nav = runtime.BoxFromId("sidebar-nav");
        var list = runtime.BoxFromId("sidebar-scroll",
            InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, nav);
        list.ChildLayoutAxis = LayoutAxis.Y;

        bool Entry(string id, string label, float height, bool raised)
        {
            var entry = runtime.Button(id, label, ghost: true);
            (entry.Node.SizeX, entry.Node.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.Pixels(height));
            var style = entry.Node.Style;
            if (raised) style.Draw = DrawFlags.Background;
            style.CornerRadius = 6f;
            float padY = (height - runtime.Fonts.LineHeight(runtime.FontInScope())) / 2f;
            style.Padding = new EdgeInsets(padY, 8, padY, 8);
            return entry.Clicked;
        }

        using (runtime.PushParent(list))
        {
            foreach (var (groupLabel, components) in groups)
            {
                bool groupOpen = state.OpenSidebarGroup == groupLabel;
                if (Entry($"nav-group-{GalleryHelpers.Slug(groupLabel)}",
                        groupOpen ? $"{groupLabel} -" : $"{groupLabel} +",
                        36f, groupOpen))
                    gallery.State = state with { OpenSidebarGroup = groupOpen ? null : groupLabel };

                if (!groupOpen) continue;
                foreach (var comp in components)
                {
                    if (Entry($"nav-{comp}", comp, 32f, state.Route == comp))
                        gallery.State = state with { Route = comp, OpenSidebarGroup = groupLabel, MenuOpen = false };
                }
            }
        }

        using (runtime.PushParent(nav))
            runtime.Scrollbar("sidebar-bar", list);

        runtime.InteractionsFor(list);
        (list.SizeX, list.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        list.AllowOverflowY = true;
        list.Style.Padding = folded ? new EdgeInsets(8, 0, 12, 8) : new EdgeInsets(0, 0, 12, 12);
        nav.FillX().FillY();
    }
}

// The bar across the top of a window too narrow for the sidebar: the title, and
// the button that shows the sidebar in place of the page, or the page again.
static void BuildMenuBar(BibuiRuntime runtime, GalleryState state, Gallery gallery)
{
    var bar = runtime.BoxFromId("menu-bar");
    bar.ChildLayoutAxis = LayoutAxis.X;
    bar.Styled(new Style
    {
        Padding = new EdgeInsets(10, 12, 10, 16),
        BackgroundColor = UiColor.Background,
        BorderColor = UiColor.Border,
        BorderWidth = 1f,
    }).FillX().Px(height: ShellLayout.BarHeight);

    using (runtime.PushParent(bar))
    {
        using (runtime.PushParent(runtime.ColumnFillY()))
        using (runtime.Centred())
            Bold(runtime, "Gallery", ComponentSize.Lg, TextColor.Primary);
        runtime.Spring();
        using (runtime.PushParent(runtime.ColumnFillY()))
        using (runtime.Centred())
        {
            var toggle = runtime.Button("nav-menu",
                state.MenuOpen ? "Close" : "Components", ghost: true, size: ComponentSize.Sm);
            if (toggle.Clicked) gallery.State = state with { MenuOpen = !state.MenuOpen };
        }
    }
}

// ── Content panel ─────────────────────────────────────────────────────────────
// The page scrolls in a view with its bar beside it, so a page taller than the
// window is all there; its text wraps at the width the window leaves it.

static void BuildContentPanel(BibuiRuntime runtime, GalleryState state, Gallery gallery, ShellLayout shell)
{
    var panel = runtime.BoxFromId("content");
    var view = runtime.BoxFromId("content-scroll",
        InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, panel);

    float width = ShellLayout.PageWidth(runtime);

    using (runtime.PushParent(view))
    using (runtime.PushParent(runtime.ColumnFillX()))
    {
        // Page title
        var title = runtime.BoxFromId("page-title");
        title.ChildLayoutAxis = LayoutAxis.X;
        (title.SizeX, title.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ChildrenSum());
        using (runtime.PushParent(title))
            Bold(runtime, state.Route, ComponentSize.Xxl, TextColor.Primary);
        runtime.Gap(24f);
        Wrapped(runtime, GalleryHelpers.ComponentDescription(state.Route), width, ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(24f);
        Separator.Build(runtime);
        runtime.Gap(24f);
        // Demo content: each page builds into a column the panel's width
        using (runtime.PushParent(runtime.ColumnFillX()))
            BuildDemo(runtime, state, gallery);
    }

    using (runtime.PushParent(panel))
        runtime.Scrollbar("content-bar", view);
    runtime.InteractionsFor(view);

    view.ChildLayoutAxis = LayoutAxis.Y;
    (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
    view.AllowOverflowY = true;
    var p = shell.Padding;
    view.Style.Padding = new EdgeInsets(p.Top, p.Right, p.Bottom, p.Left);
    panel.Style.BackgroundColor = UiColor.Background;
    panel.FillX().FillY();
}

// Text in lines no wider than width, broken between words.
static void Wrapped(BibuiRuntime runtime, string text, float width, ComponentSize size, TextColor color)
{
    using (runtime.PushParent(runtime.ColumnFillX()))
    {
        var line = runtime.Fonts.Wrap(text, runtime.FontInScope(size), width);
        if (line is null)   // nothing to say still takes its line
            runtime.Text(text, size, color);
        for (; line is not null; line = line.Next)
            runtime.Text(text.AsSpan(line.Start, line.End - line.Start), size, color);
    }
}

// ── Per-component demos (delegated to per-category classes) ──────────────────

static void BuildDemo(BibuiRuntime runtime, GalleryState state, Gallery gallery)
{
    switch (state.Route)
    {
        case "Button" or "Input" or "Textarea" or "TextField" or "Checkbox" or "Switch" or "RadioGroup"
            or "Select" or "Slider" or "Toggle" or "ToggleGroup" or "DatePicker" or "Calendar"
            or "TimePicker" or "ColorPicker":
            DemoInputs.Build(runtime, state.Route, state.Inputs, s => gallery.State = state with { Inputs = s }); break;
        case "Text" or "Badge" or "Avatar" or "Card" or "Alert" or "Separator" or "Label"
            or "Progress" or "Skeleton" or "Spinner" or "RatioBox"
            or "KeyboardShortcut" or "Table" or "Markdown" or "Texture" or "PagedTable" or "FormField" or "ListItem" or "EmptyState"
            or "Toast":
            DemoDisplay.Build(runtime, state.Route, state.Display, s => gallery.State = state with { Display = s }); break;
        case "Dialog" or "AlertDialog" or "Popover" or "HoverPreview" or "Tooltip"
            or "Drawer" or "DropdownMenu" or "ContextMenu" or "CommandPalette":
            DemoOverlays.Build(runtime, state.Route, state.Overlays, s => gallery.State = state with { Overlays = s }); break;
        case "Tabs" or "Accordion" or "Expander" or "NavBar" or "Sidebar"
            or "Breadcrumb" or "Pagination" or "MenuBar":
            DemoNavigation.Build(runtime, state.Route, state.Navigation, s => gallery.State = state with { Navigation = s }); break;
        case "Scrollbar": DemoScrollbar.Build(runtime); break;
        case "ScrollList": DemoScrollList.Build(runtime); break;
        case "SplitPane" or "Carousel" or "ButtonStrip" or "Stack" or "InteractiveBox" or "Section":
            DemoLayout.Build(runtime, state.Route, state.Layout, s => gallery.State = state with { Layout = s }); break;
        default: runtime.Text("Select a component from the sidebar.", textColor: TextColor.Secondary); break;
    }
}

// ── The whole window ──────────────────────────────────────────────────────────

// The size the window opens at. Only that: the window is resizable, and the
// shell lays itself out for whatever size it is given (ShellLayout).
static SurfaceOptions Surface() => new() { Width = 1100, Height = 820 };

// Wide, the sidebar sits beside the page; narrow, it folds behind a menu bar and
// takes the page's place while it is open.
static void BuildGallery(BibuiRuntime runtime, Gallery gallery)
{
    var state = gallery.State;
    var shell = ShellLayout.For(runtime.SurfaceWidth);
    // Under the frame's root: the sidebar beside the page, or the bar over one
    // of them.
    var page = runtime.BoxFromId("gallery");
    page.ChildLayoutAxis = shell.Folded ? LayoutAxis.Y : LayoutAxis.X;
    page.FillX().FillY().Styled(new Style { BackgroundColor = UiColor.Background });
    using (runtime.PushParent(page))
    {
        if (!shell.Folded)
        {
            BuildSidebar(runtime, state, gallery, shell.SidebarWidth);
            BuildContentPanel(runtime, state, gallery, shell);
        }
        else
        {
            BuildMenuBar(runtime, state, gallery);
            if (state.MenuOpen) BuildSidebar(runtime, state, gallery, 0f);
            else BuildContentPanel(runtime, state, gallery, shell);
        }
    }
}

static GalleryState InitialState() => new(Route: "Button", OpenSidebarGroup: "Inputs & Forms");

// ── Reference pictures ────────────────────────────────────────────────────────
// Every page as the window shows it, rendered headlessly — and, where the page
// has an open state, opened too — to reference/: what the rebuilt gallery is
// compared with.

static void WriteReferences(GalleryOptions options)
{
    // A generated theme's pictures go beside the checked-in ones, not over them.
    var outDir = Path.GetFullPath(options.OutDir ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        options.Accent is null ? "reference" : "reference-seed"));
    var theme = options.Theme();
    var sizes = options.Sizes.Count > 0 ? options.Sizes : [(Surface().Width, Surface().Height)];
    using var skia = new SkiaCpuRenderer();
    DemoTexture.SkiaCpu = skia;   // the texture page's textures, on the renderer that draws it
    foreach (var (width, height) in sizes)
    {
        // One size writes straight into the directory; several, a folder each.
        var dir = sizes.Count == 1 ? outDir : Path.Combine(outDir, $"{width}x{height}");
        Directory.CreateDirectory(dir);
        foreach (var (name, state, dropDown) in Pages())
            WriteReference(dir, name, state, dropDown, theme, new SurfaceOptions { Width = width, Height = height }, skia);
    }
    Console.WriteLine($"Done — references in {outDir}");
}

// Every page, and open where it opens, as the sidebar opens it: its open
// state, or the dropdown to open in its runtime.
static IEnumerable<(string Name, GalleryState State, string? DropDown)> Pages()
{
    foreach (var (group, routes) in NavGroups())
        foreach (var route in routes)
        {
            var state = InitialState() with { Route = route, OpenSidebarGroup = group };
            yield return (route, state, null);
            if (Opened(state) is { } open) yield return (route + "-open", open, null);
            else if (OpenedDropDown(route) is { } dropDown) yield return (route + "-open", state, dropDown);
        }
}

// A page that cannot be rendered — a layout ring, say — leaves what it threw
// instead of a picture, and the rest go on.
static void WriteReference(string outDir, string name, GalleryState state, string? dropDown,
    Bibui.Theming.ThemeTokens theme, SurfaceOptions surface, SkiaCpuRenderer skia)
{
    var runtime = BibuiRuntime.Create();
    runtime.SetTheme(theme);
    runtime.Surface = surface;
    if (dropDown is not null) runtime.OpenDropDown(dropDown);
    try
    {
        File.WriteAllBytes(Path.Combine(outDir, $"{name}.png"), RenderSettled(runtime, new Gallery { State = state }, skia));
        Console.WriteLine($"  wrote {name}.png");
    }
    catch (Exception e)
    {
        File.WriteAllText(Path.Combine(outDir, $"{name}.error.txt"), $"{e.GetType().Name}: {e.Message}\n");
        Console.WriteLine($"  {name}: {e.GetType().Name}: {e.Message}");
    }
}

// Three frames: laid out, and wrapped at that width; the last drawn with Skia.
static byte[] RenderSettled(BibuiRuntime runtime, Gallery gallery, SkiaCpuRenderer skia)
{
    for (int i = 0; i < 3; i++)
    {
        runtime.BeginBuild();
        BuildGallery(runtime, gallery);
        runtime.EndBuild();
    }
    return FramePixels.Png(runtime, skia.Draw);
}

// ── Layout check ──────────────────────────────────────────────────────────────
// Every page at every size, headless: what is cut, spills out of its box, or is
// off the surface (LayoutCheck). The exit code is the number of pages at fault,
// capped at 1.

static int CheckLayouts(GalleryOptions options)
{
    var sizes = options.Sizes.Count > 0
        ? options.Sizes
        : [(ShellLayout.MinWidth, ShellLayout.MinHeight), (840, 600), (900, 700), (1100, 820), (1600, 1000)];
    int pages = 0, faulty = 0;
    using var skia = new SkiaCpuRenderer();
    DemoTexture.SkiaCpu = skia;   // the texture page's textures, on the renderer that draws it
    foreach (var (width, height) in sizes)
    {
        string? dir = options.OutDir is { } o ? Path.Combine(Path.GetFullPath(o), $"{width}x{height}") : null;
        if (dir is not null) Directory.CreateDirectory(dir);
        // And the sidebar on its own, as the menu shows it in a narrow window.
        var menu = ("menu-open", InitialState() with { MenuOpen = true }, (string?)null);
        foreach (var (name, state, dropDown) in Pages().Append(menu))
        {
            pages++;
            var runtime = BibuiRuntime.Create();
            runtime.SetTheme(options.Theme());
            runtime.Surface = new SurfaceOptions { Width = width, Height = height };
            if (dropDown is not null) runtime.OpenDropDown(dropDown);
            List<LayoutCheck.Fault> faults;
            try
            {
                byte[] png = RenderSettled(runtime, new Gallery { State = state }, skia);
                if (dir is not null) File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), png);
                faults = LayoutCheck.Check(runtime, runtime.FindById("gallery")!, width, height);
            }
            catch (Exception e)
            {
                faults = [new("throws", name, $"{e.GetType().Name}: {e.Message}")];
            }
            if (faults.Count == 0) continue;
            faulty++;
            Console.WriteLine($"{width}x{height} {name}: {faults.Count} fault(s)");
            foreach (var f in faults.Take(8)) Console.WriteLine($"    {f}");
        }
    }
    Console.WriteLine($"{pages - faulty}/{pages} pages laid out whole at {string.Join(", ", sizes.Select(s => $"{s.Width}x{s.Height}"))}");
    return faulty == 0 ? 0 : 1;
}

// The page's open state, for a page that has one.
static GalleryState? Opened(GalleryState state) =>
    state.Display.Opened(state.Route) is { } display ? state with { Display = display }
    : state.Overlays.Opened(state.Route) is { } overlays ? state with { Overlays = overlays }
    : null;

// The dropdown a page opens, for a page whose open state is one.
static string? OpenedDropDown(string route) => route switch
{
    "Select" => "demo-select-list",
    "DatePicker" => "demo-datepicker-popup",
    "TimePicker" => "demo-timepicker-popup",
    "ColorPicker" => "demo-colorpicker-popup",
    "Popover" => "demo-popover-content",
    "HoverPreview" => "demo-hover-preview-content",
    "DropdownMenu" => "demo-dropdown-menu-list",
    "ContextMenu" => "demo-context-menu-list",
    "MenuBar" => "demo-menu-bar-file-menu",
    "Section" => "demo-colormap-list",
    _ => null,
};

/// <summary>The gallery's state: one record, replaced as the gallery's controls change it.</summary>
sealed class Gallery
{
    public GalleryState State = null!;
}
