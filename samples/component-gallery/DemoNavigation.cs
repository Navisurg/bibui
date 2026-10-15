using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

/// <summary>What the Navigation pages hold between frames.</summary>
internal sealed record NavigationState
{
    public string ActiveTab { get; init; } = "account";
    public string? AccordionOpen { get; init; } = "item-1";
    public bool ExpanderOpen { get; init; } = true;
    public int PaginationPage { get; init; } = 3;
    public string NavBarActiveItem { get; init; } = "Overview";
    public string SidebarActiveItem { get; init; } = "Dashboard";

}

internal static class DemoNavigation
{
    internal static UiNode Build(BibuiRuntime runtime, string route, NavigationState state, Action<NavigationState> set) => route switch
    {
    "Tabs" => TabsPage(runtime, state, set),

    "Accordion" => AccordionPage(runtime, state, set),

    "Expander" => ExpanderPage(runtime, state, set),

    "NavBar" => NavBarPage(runtime, state, set),

    "Sidebar" => SidebarPage(runtime, state, set),

    "Breadcrumb" => BreadcrumbPage(runtime),

    "Pagination" => PaginationPage(runtime, state, set),

    "MenuBar" => MenuBarPage(runtime, state, set),

        _ => runtime.Text("Select a component from the sidebar.", textColor: TextColor.Secondary),
    };

    // ── Shared page pieces ───────────────────────────────────────────────────

    /// <summary>The bold caption over a demo, and the space under it.</summary>
    internal static void Heading(BibuiRuntime runtime, string text)
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(text, ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);
    }

    /// <summary>
    /// A cell as tall as the row in scope and as wide as what is built into it,
    /// with a spring above and below: push it, build one thing, and that thing
    /// is centred down the row. The row needs a height of its own.
    /// </summary>
    internal static Box MidCell(BibuiRuntime runtime)
    {
        Box cell = runtime.Box();
        cell.ChildLayoutAxis = LayoutAxis.Y;
        cell.SizeX = SemanticSize.ChildrenSum();
        cell.SizeY = SemanticSize.ParentPct(1f);
        Arrange.Spring(cell);
        return cell;
    }

    /// <summary>A text centred down the row in scope.</summary>
    internal static Text MidText(BibuiRuntime runtime, string text, ComponentSize? size = null,
        UiColor? color = null, FontWeight weight = FontWeight.Regular)
    {
        Box cell = MidCell(runtime);
        Text label;
        using (runtime.PushParent(cell))
        using (runtime.PushFontFace(weight: weight))
        {
            label = runtime.Text(text, size, color);
            Arrange.Spring(cell);
        }
        return label;
    }

    /// <summary>An icon centred down the row in scope.</summary>
    internal static Icon MidIcon(BibuiRuntime runtime, string name, float size, UiColor color)
    {
        Box cell = MidCell(runtime);
        Icon icon;
        using (runtime.PushParent(cell))
        {
            icon = runtime.Icon(name, size);
            icon.Color = color;
            Arrange.Spring(cell);
        }
        return icon;
    }

    /// <summary>
    /// A button <paramref name="width"/> by <paramref name="height"/> px with
    /// its label in the middle: the runtime button, its padding widened to
    /// centre what it reads.
    /// </summary>
    internal static BibuiWidget WideButton(BibuiRuntime runtime, string id, string label, float width,
        float height = 40f, bool ghost = false, bool disabled = false)
    {
        var button = runtime.Button(id, label, ghost, disabled);
        Font font = runtime.FontInScope();
        float padX = MathF.Max(0f, (width - runtime.Fonts.Measure(label, font)) / 2f);
        float padY = MathF.Max(0f, (height - runtime.Fonts.LineHeight(font)) / 2f);
        button.Node.Style.Padding = EdgeInsets.Symmetric(horizontal: padX, vertical: padY);
        return button;
    }

    /// <summary>A row under the parent in scope, as large as what is built into it.</summary>
    internal static Box Row(BibuiRuntime runtime)
    {
        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = row.SizeY = SemanticSize.ChildrenSum();
        return row;
    }

    /// <summary>A rule 1 px tall across the parent in scope.</summary>
    internal static Box Rule(BibuiRuntime runtime)
    {
        Box rule = runtime.Box();
        rule.SizeX = SemanticSize.ParentPct(1f);
        rule.SizeY = SemanticSize.Pixels(1f);
        rule.Style.BackgroundColor = UiColor.Border;
        return rule;
    }

    /// <summary>A column <paramref name="width"/> px wide, as tall as its children, under the parent in scope.</summary>
    private static Box FixedColumn(BibuiRuntime runtime, float width)
    {
        Box column = runtime.Box();
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = SemanticSize.Pixels(width);
        column.SizeY = SemanticSize.ChildrenSum();
        return column;
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private static readonly TabOption[] TabItems =
    [
        new("Account", "account"),
        new("Password", "password"),
        new("Notifications", "notifications"),
    ];

    private static UiNode TabsPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "One panel at a time, chosen by its tab");
            using (runtime.PushParent(FixedColumn(runtime, 480f)))
            {
                var tabs = runtime.Tabs("demo-tabs", TabItems, state.ActiveTab);
                if (tabs.Picked is { } picked && picked != state.ActiveTab) set(state with { ActiveTab = picked });

                runtime.Gap(Tabs.PanelGap);
                Box panel = Tabs.Panel(runtime);
                panel.SizeY = SemanticSize.Pixels(76f);
                using (runtime.PushParent(panel))
                    runtime.Text(state.ActiveTab switch
                    {
                        "account" => "Your name, your email, and the workspaces you are in.",
                        "password" => "Change the password, or sign out everywhere else.",
                        "notifications" => "Which events reach you, and how.",
                        _ => "",
                    }, textColor: TextColor.Secondary);
            }
        }
        return page;
    }

    // ── Accordion: three expanders, at most one open ─────────────────────────

    private static readonly (string Title, string Content, string Value)[] AccordionItems =
    [
        ("Who holds which one is open?", "The page: it says which is open, and reads back a toggle.", "item-1"),
        ("Why only one at a time?", "This page closes the others when one opens.", "item-2"),
        ("What is a section made of?", "runtime.Expander, as on the Expander page.", "item-3"),
    ];

    private static UiNode AccordionPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Sections that open one at a time");
            using (runtime.PushParent(FixedColumn(runtime, 480f)))
            {
                for (int i = 0; i < AccordionItems.Length; i++)
                {
                    var (title, content, value) = AccordionItems[i];
                    if (i > 0) runtime.Gap(4f);
                    bool open = state.AccordionOpen == value;
                    var item = runtime.Expander($"demo-accordion-{value}", title, open);
                    if (item.Toggled) set(state with { AccordionOpen = open ? null : value });
                    if (item.Body is { } body)
                    {
                        body.SizeY = SemanticSize.Pixels(59f);
                        using (runtime.PushParent(body))
                            runtime.Text(content, ComponentSize.Sm, TextColor.Secondary);
                    }
                }
            }
            runtime.Gap(16f);
            runtime.Text($"Open: {(string.IsNullOrEmpty(state.AccordionOpen) ? "none" : state.AccordionOpen)}",
                ComponentSize.Sm, TextColor.Secondary);
        }
        return page;
    }

    // ── Expander ─────────────────────────────────────────────────────────────

    private static readonly string[] StarredRepositories = ["@fernway/primitives", "@fernway/palette", "@fernway/styled"];

    private static UiNode ExpanderPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Press the header, or the button, to open and close it");
            bool toggled;
            using (runtime.PushParent(FixedColumn(runtime, 360f)))
            {
                var expander = runtime.Expander("demo-expander", "Starred repositories", state.ExpanderOpen);
                toggled = expander.Toggled;
                if (expander.Body is { } body)
                    using (runtime.PushParent(body))
                        for (int i = 0; i < StarredRepositories.Length; i++)
                        {
                            if (i > 0) runtime.Gap(4f);
                            runtime.Text(StarredRepositories[i], ComponentSize.Sm, TextColor.Secondary);
                        }
            }
            runtime.Gap(16f);
            toggled |= WideButton(runtime, "demo-expander-toggle", state.ExpanderOpen ? "Collapse" : "Expand", 120f).Clicked;
            if (toggled) set(state with { ExpanderOpen = !state.ExpanderOpen });
        }
        return page;
    }

    // ── NavBar ───────────────────────────────────────────────────────────────

    private static readonly string[] NavItems = ["Overview", "Files", "Activity", "Settings"];

    private static UiNode NavBarPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        string? picked = null;
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Press a link, or a button below it, to make it current");

            // The bar: links 36 px tall, 16 px in, centred down 60.
            Box bar = runtime.Box();
            bar.ChildLayoutAxis = LayoutAxis.X;
            bar.Px(560f, 60f);
            bar.Style.Padding = new EdgeInsets(12f, 16f, 12f, 16f);
            using (runtime.PushParent(bar))
                for (int i = 0; i < NavItems.Length; i++)
                {
                    if (i > 0) runtime.Gap(4f);
                    string label = NavItems[i];
                    bool active = label == state.NavBarActiveItem;
                    Box link = runtime.BoxFromId($"demo-navbar-{GalleryHelpers.Slug(label)}", InteractionKinds.LeftPress);
                    if (runtime.InteractionsFor(link).Clicked) picked = label;
                    link.ChildLayoutAxis = LayoutAxis.X;
                    link.SizeX = SemanticSize.ChildrenSum();
                    link.SizeY = SemanticSize.ParentPct(1f);
                    link.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: 0f);
                    link.Style.CornerRadius = 4f;
                    link.Style.BackgroundColor = active ? UiColor.SurfaceRaised : (UiColor?)null;
                    link.Style.HoverBackgroundColor = UiColor.SurfaceRaised;
                    using (runtime.PushParent(link))
                        MidText(runtime, label, ComponentSize.Sm, active ? TextColor.Primary : TextColor.Secondary);
                }

            runtime.Gap(16f);
            using (runtime.PushParent(Row(runtime)))
            {
                (string Label, float Width)[] shortcuts = [("Overview", 120f), ("Files", 120f), ("Activity", 120f)];
                for (int i = 0; i < shortcuts.Length; i++)
                {
                    if (i > 0) runtime.Gap(8f);
                    var (label, width) = shortcuts[i];
                    if (WideButton(runtime, $"demo-navbar-go-{GalleryHelpers.Slug(label)}", label, width, ghost: true).Clicked)
                        picked = label;
                }
            }

            runtime.Gap(16f);
            runtime.Text($"Active: {state.NavBarActiveItem}", ComponentSize.Sm, TextColor.Secondary);
        }
        if (picked is not null && picked != state.NavBarActiveItem) set(state with { NavBarActiveItem = picked });
        return page;
    }

    // ── Sidebar ──────────────────────────────────────────────────────────────

    // An empty label is a separator.
    private static readonly (string Label, string Icon)[] SidebarItems =
    [
        ("Dashboard", "grid"),
        ("Analytics", "sliders"),
        ("", ""),
        ("Settings", "settings"),
        ("Team", "users"),
        ("Billing", "file-text"),
    ];

    private static UiNode SidebarPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        string? picked = null;
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Press an entry to make it current");

            Box panel = runtime.Box();
            panel.ChildLayoutAxis = LayoutAxis.Y;
            panel.Px(220f, 320f);
            panel.Style.BackgroundColor = UiColor.Surface;
            panel.Style.BorderColor = UiColor.Border;
            panel.Style.BorderWidth = 1f;
            panel.Style.Padding = EdgeInsets.All(12f);
            using (runtime.PushParent(panel))
                foreach (var (label, icon) in SidebarItems)
                {
                    if (label.Length == 0)
                    {
                        runtime.Gap(4f);
                        Rule(runtime);
                        runtime.Gap(4f);
                        continue;
                    }

                    bool active = label == state.SidebarActiveItem;
                    Box item = runtime.BoxFromId($"demo-sidebar-{GalleryHelpers.Slug(label)}", InteractionKinds.LeftPress);
                    if (runtime.InteractionsFor(item).Clicked) picked = label;
                    item.ChildLayoutAxis = LayoutAxis.X;
                    item.SizeX = SemanticSize.ParentPct(1f);
                    item.SizeY = SemanticSize.Pixels(40f);
                    item.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: 0f);
                    item.Style.CornerRadius = 6f;
                    item.Style.BackgroundColor = active ? UiColor.SurfaceRaised : (UiColor?)null;
                    item.Style.HoverBackgroundColor = UiColor.SurfaceRaised;
                    UiColor ink = active ? UiColor.TextPrimary : UiColor.TextSecondary;
                    using (runtime.PushParent(item))
                    {
                        MidIcon(runtime, icon, 18f, ink);
                        runtime.Gap(8f);
                        MidText(runtime, label, ComponentSize.Sm, ink);
                    }
                }

            runtime.Gap(16f);
            runtime.Text($"Active: {state.SidebarActiveItem}", ComponentSize.Sm, TextColor.Secondary);
        }
        if (picked is not null && picked != state.SidebarActiveItem) set(state with { SidebarActiveItem = picked });
        return page;
    }

    // ── Breadcrumb ───────────────────────────────────────────────────────────

    private static UiNode BreadcrumbPage(BibuiRuntime runtime)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Where you are, and the places above it");
            Breadcrumb(runtime, "Workspaces", "fernway-design", "Settings");
            runtime.Gap(20f);
            Breadcrumb(runtime, "Files", "exports", "2026", "summary.png");
        }
        return page;
    }

    // A trail: the ancestors in the secondary colour, the current page last and
    // bold, a small chevron between each.
    private static void Breadcrumb(BibuiRuntime runtime, params string[] trail)
    {
        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ChildrenSum();
        row.SizeY = SemanticSize.Pixels(20f);
        using (runtime.PushParent(row))
            for (int i = 0; i < trail.Length; i++)
            {
                bool current = i == trail.Length - 1;
                if (i > 0)
                {
                    runtime.Gap(6f);
                    MidIcon(runtime, "chevron-right", 12f, UiColor.TextDisabled);
                    runtime.Gap(6f);
                }
                MidText(runtime, trail[i], ComponentSize.Sm, current ? TextColor.Primary : TextColor.Secondary, current ? FontWeight.Bold : FontWeight.Regular);
            }
    }

    // ── Pagination ───────────────────────────────────────────────────────────

    private const int PageCount = 10;

    private static UiNode PaginationPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        int current = state.PaginationPage;
        int? go = null;
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Step through numbered pages");

            Box row = runtime.Box();
            row.ChildLayoutAxis = LayoutAxis.X;
            row.SizeX = SemanticSize.ChildrenSum();
            row.SizeY = SemanticSize.Pixels(40f);
            using (runtime.PushParent(row))
            {
                // Five pages about the current one, kept inside 1..PageCount.
                int end = Math.Min(PageCount, Math.Max(1, current - 2) + 4);
                int start = Math.Max(1, end - 4);

                if (PageButton(runtime, "demo-pagination-prev", 80f, current > 1, raised: true, accent: false, box =>
                    {
                        MidIcon(runtime, "chevron-left", 14f, current > 1 ? UiColor.TextPrimary : UiColor.TextDisabled);
                        runtime.Gap(5f);
                        MidText(runtime, "Previous", ComponentSize.Sm, current > 1 ? TextColor.Primary : TextColor.Disabled);
                    }, padLeft: 5f))
                    go = current - 1;

                for (int p = start; p <= end; p++)
                {
                    runtime.Gap(4f);
                    bool active = p == current;
                    int number = p;
                    if (PageButton(runtime, $"demo-pagination-{p}", 36f, true, raised: false, accent: active, box =>
                        {
                            Arrange.Spring(box);
                            MidText(runtime, number.ToString(), ComponentSize.Sm, active ? TextColor.AccentForeground : TextColor.Primary);
                            Arrange.Spring(box);
                        }))
                        go = p;
                }

                runtime.Gap(4f);
                if (PageButton(runtime, "demo-pagination-next", 80f, current < PageCount, raised: true, accent: false, box =>
                    {
                        MidText(runtime, "Next", ComponentSize.Sm, current < PageCount ? TextColor.Primary : TextColor.Disabled);
                        Arrange.Spring(box);
                        MidIcon(runtime, "chevron-right", 14f, current < PageCount ? UiColor.TextPrimary : UiColor.TextDisabled);
                    }, padLeft: 4f, padRight: 9f))
                    go = current + 1;
            }

            runtime.Gap(16f);
            runtime.Text($"Page {current} of {PageCount}", ComponentSize.Sm, TextColor.Accent);
        }
        if (go is { } target && target != current) set(state with { PaginationPage = target });
        return page;
    }

    // One cell of the pagination row: outlined unless it is the current page,
    // which is filled with the accent; it takes the press unless it is not
    // enabled, and reports a click.
    private static bool PageButton(BibuiRuntime runtime, string id, float width, bool enabled, bool raised, bool accent,
        Action<Box> content, float padLeft = 0f, float padRight = 0f)
    {
        Box box = runtime.BoxFromId(id, enabled ? InteractionKinds.LeftPress : InteractionKinds.Disabled);
        bool clicked = runtime.InteractionsFor(box).Clicked;
        box.ChildLayoutAxis = LayoutAxis.X;
        box.SizeX = SemanticSize.Pixels(width);
        box.SizeY = SemanticSize.ParentPct(1f);
        box.Style.CornerRadius = 6f;
        box.Style.Padding = new EdgeInsets(0f, padRight, 0f, padLeft);
        box.Style.BackgroundColor = accent ? UiColor.Accent : raised && enabled ? UiColor.SurfaceRaised : UiColor.Surface;
        box.Style.HoverBackgroundColor = accent || !enabled ? (UiColor?)null : UiColor.SurfaceRaisedHover;
        if (!accent)
        {
            box.Style.BorderColor = UiColor.Border;
            box.Style.BorderWidth = 1f;
        }
        using (runtime.PushParent(box)) content(box);
        return clicked;
    }

    // ── MenuBar ──────────────────────────────────────────────────────────────

    // An empty label is a separator.
    private static readonly (string Menu, (string Label, string? Icon, string Shortcut)[] Items)[] Menus =
    [
        ("File", [("New File", "plus", "Ctrl+N"), ("Open…", "folder", "Ctrl+O"), ("", null, ""),
                  ("Save", "save", "Ctrl+S"), ("Save As…", null, "Ctrl+Shift+S")]),
        ("Edit", [("Undo", "rotate-ccw", "Ctrl+Z"), ("Redo", null, "Ctrl+Shift+Z"), ("", null, ""),
                  ("Cut", null, "Ctrl+X"), ("Copy", "copy", "Ctrl+C"), ("Paste", null, "Ctrl+V")]),
        ("View", [("Zoom In", "plus", "Ctrl++"), ("Zoom Out", "minus", "Ctrl+-"), ("Reset Zoom", null, "Ctrl+0")]),
    ];

    private static UiNode MenuBarPage(BibuiRuntime runtime, NavigationState state, Action<NavigationState> set)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "File, Edit, View: menus along the top of a window");

            Box bar = runtime.Box();
            bar.ChildLayoutAxis = LayoutAxis.X;
            bar.Px(560f, 36f);
            bar.Style.BackgroundColor = UiColor.Surface;
            bar.Style.BorderColor = UiColor.Border;
            bar.Style.BorderWidth = 1f;
            bar.Style.CornerRadius = 4f;
            bar.Style.Padding = new EdgeInsets(2f, 8f, 2f, 8f);
            // The open menu's dropdown, if one is.
            string? openList = null;
            foreach (var (menu, _) in Menus)
            {
                string list = $"demo-menu-bar-{menu.ToLowerInvariant()}-menu";
                if (runtime.IsDropDownOpen(list)) openList = list;
            }
            using (runtime.PushParent(bar))
                foreach (var (menu, items) in Menus)
                {
                    string id = $"demo-menu-bar-{menu.ToLowerInvariant()}", list = id + "-menu";
                    Box trigger = runtime.BoxFromId(id, InteractionKinds.LeftPress);
                    var interaction = runtime.InteractionsFor(trigger);
                    // A press on another trigger has closed the open menu already.
                    if (interaction.Pressed)
                        if (openList == list) runtime.CloseDropDown(list); else runtime.OpenDropDown(list, trigger);
                    // With a menu open, moving onto another trigger opens that one instead.
                    else if (openList is not null && openList != list && interaction.Hovering)
                    {
                        runtime.CloseDropDown(openList);
                        runtime.OpenDropDown(list, trigger);
                        openList = list;
                    }
                    bool isOpen = runtime.IsDropDownOpen(list);
                    trigger.ChildLayoutAxis = LayoutAxis.X;
                    trigger.SizeX = SemanticSize.ChildrenSum();
                    trigger.SizeY = SemanticSize.ParentPct(1f);
                    trigger.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: 0f);
                    trigger.Style.CornerRadius = 4f;
                    trigger.Style.BackgroundColor = isOpen ? UiColor.SurfaceRaised : (UiColor?)null;
                    trigger.Style.HoverBackgroundColor = UiColor.SurfaceRaised;
                    using (runtime.PushParent(trigger)) MidText(runtime, menu);

                    if (MenuDropDown(runtime, list, items, trigger)) runtime.CloseDropDown(list);
                }
        }
        return page;
    }

    // The menu named id, when it is open: a dropdown under its trigger, where
    // the trigger was drawn last frame. Reports whether an item was clicked,
    // which closes it.
    private static bool MenuDropDown(BibuiRuntime runtime, string id, (string Label, string? Icon, string Shortcut)[] items, Box trigger)
    {
        if (runtime.BeginDropDown(id) is not { } list) return false;
        list.SizeX = SemanticSize.Pixels(220f);
        list.SizeY = SemanticSize.ChildrenSum();
        list.At(trigger.Rect.X, trigger.Rect.Y + trigger.Extent.Y + 6f);
        list.Style.Padding = EdgeInsets.All(4f);
        list.Style.CornerRadius = 6f;
        list.Style.BorderColor = UiColor.Border;
        list.Style.BorderWidth = 1f;
        list.Style.BackgroundColor = UiColor.SurfaceRaised;

        bool clicked = false;
        using (runtime.PushParent(list))
            foreach (var (label, icon, shortcut) in items)
            {
                if (label.Length == 0)
                {
                    runtime.Gap(2f);
                    Rule(runtime);
                    runtime.Gap(2f);
                    continue;
                }
                Box row = runtime.BoxFromId($"{id}-{GalleryHelpers.Slug(label)}", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(row).Clicked) clicked = true;
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.ParentPct(1f);
                row.SizeY = SemanticSize.Pixels(32f);
                row.Style.Padding = EdgeInsets.Symmetric(horizontal: 8f, vertical: 0f);
                row.Style.CornerRadius = 4f;
                row.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
                using (runtime.PushParent(row))
                {
                    if (icon is not null) MidIcon(runtime, icon, 14f, UiColor.TextSecondary);
                    else runtime.Gap(14f);
                    runtime.Gap(8f);
                    MidText(runtime, label);
                    Arrange.Spring(row);
                    MidText(runtime, shortcut, ComponentSize.Sm, TextColor.Disabled);
                }
            }

        // After its rows, so a press on its padding stops here rather than
        // reaching what is beneath.
        list.Claims = InteractionKinds.Press;
        runtime.InteractionsFor(list);
        return clicked;
    }
}
