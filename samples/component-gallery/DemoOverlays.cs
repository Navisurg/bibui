using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;

namespace ComponentGallery;

/// <summary>What the Overlays pages hold between frames.</summary>
internal sealed record OverlaysState
{
    public bool DialogOpen { get; init; }
    public bool AlertDialogOpen { get; init; }
    public bool DrawerRightOpen { get; init; }
    public bool DrawerBottomOpen { get; init; }
    public bool TooltipOpen { get; init; }
    public bool CommandPaletteOpen { get; init; }
    public string? DropdownLastSelected { get; init; }
    public string? ContextLastSelected { get; init; }

    /// <summary>The page's open state, for a page that has one.</summary>
    internal OverlaysState? Opened(string route) => route switch
    {
        "Dialog" => this with { DialogOpen = true },
        "AlertDialog" => this with { AlertDialogOpen = true },
        "Drawer" => this with { DrawerRightOpen = true },
        "Tooltip" => this with { TooltipOpen = true },
        "CommandPalette" => this with { CommandPaletteOpen = true },
        _ => null,
    };
}

internal static class DemoOverlays
{
    internal static UiNode Build(BibuiRuntime runtime, string route, OverlaysState state, Action<OverlaysState> set) => route switch
    {
    "Dialog" => DialogPage(runtime, state, set),

    "AlertDialog" => AlertDialogPage(runtime, state, set),

    "Popover" => PopoverPage(runtime, state, set),

    "HoverPreview" => HoverPreviewPage(runtime, state, set),

    "Tooltip" => TooltipPage(runtime, state, set),

    "Drawer" => DrawerPage(runtime, state, set),

    "DropdownMenu" => DropdownMenuPage(runtime, state, set),

    "ContextMenu" => ContextMenuPage(runtime, state, set),

    "CommandPalette" => CommandPalettePage(runtime, state, set),

        _ => runtime.Text("Select a component from the sidebar.", textColor: TextColor.Secondary),
    };

    private static readonly TextEditState NoEdit = new();

    private static void Heading(BibuiRuntime runtime, string text)
    {
        Text heading = Bold(runtime, text, ComponentSize.Sm, TextColor.Secondary);
        Arrange.Gap((UiNode)heading.Parent!, 16f);
    }

    // A fixed area of the page, laying its children out down it, at the size
    // the reference picture shows.
    private static Box Area(BibuiRuntime runtime, float width, float height)
    {
        Box area = runtime.Box();
        area.ChildLayoutAxis = LayoutAxis.Y;
        area.Px(width, height);
        return area;
    }

    // A row across the parent in scope, whose children are centred along it.
    private static Box CentredRow(BibuiRuntime runtime)
    {
        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ParentPct(1f);
        row.SizeY = SemanticSize.ChildrenSum();
        return row;
    }

    // Wrapped text in the parent in scope, as wide as it: small and quiet.
    private static void Paragraph(BibuiRuntime runtime, string id, string text)
    {
        using (runtime.PushFontSize(ComponentSize.Sm))
        using (runtime.PushSizeY(SemanticSize.ChildrenSum()))
            runtime.MultilineTextField(id, GalleryHelpers.Held(id, text), NoEdit, focused: false, events: 0,
                textColor: TextColor.Secondary);
    }

    // ── Popover ──────────────────────────────────────────────────────────────

    // The trigger is an ordinary button: it opens the popover under it and
    // closes it again, and a press anywhere else closes it too.
    private static UiNode PopoverPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        const string id = "demo-popover-content";
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Its button opens and closes it, and a press elsewhere closes it");
            Box area = Area(runtime, 340f, 140f);
            using (runtime.PushParent(area))
            {
                runtime.Gap(16f);
                Box row = CentredRow(runtime);
                using (runtime.PushParent(row))
                {
                    Arrange.Spring(row);
                    var trigger = runtime.Button("demo-popover", "Open popover");
                    if (trigger.Clicked)
                        if (runtime.IsDropDownOpen(id)) runtime.CloseDropDown(id); else runtime.OpenDropDown(id, trigger.Node);
                    Arrange.Spring(row);

                    if (Popover.Begin(runtime, id, trigger.Node, width: 280f) is { } popover)
                    {
                        Paragraph(runtime, "demo-popover-text", "Anything built between Popover.Begin and End shows here, under its trigger.");
                        popover.End();
                    }
                }
            }

            Arrange.Gap(page, 16f);
            runtime.TextFromId("demo-popover-state", $"Popover: {(runtime.IsDropDownOpen(id) ? "open" : "closed")}", ComponentSize.Sm, TextColor.Secondary);
        }
        return page;
    }

    // ── HoverPreview: a popover the pointer opens ───────────────────────────

    // Whether the card is open because the pointer opened it, so leaving
    // closes it; one a click opened stays until closed the same way.
    private static bool HoverPreviewByHover;

    private static UiNode HoverPreviewPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        const string id = "demo-hover-preview-content";
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Point at @fernway, or click it to pin the preview open");
            Box trigger;
            bool clicked, hovering;
            Box area = Area(runtime, 340f, 120f);
            using (runtime.PushParent(area))
            {
                runtime.Gap(16f);
                Box row = CentredRow(runtime);
                using (runtime.PushParent(row))
                {
                    Arrange.Spring(row);
                    // A name, not a button: a box that takes the press so the
                    // pointer can be over it.
                    trigger = runtime.BoxFromId("demo-hover-preview", InteractionKinds.LeftPress);
                    Interaction over = runtime.InteractionsFor(trigger);
                    clicked = over.Clicked;
                    hovering = over.Hovering;
                    trigger.ChildLayoutAxis = LayoutAxis.X;
                    trigger.SizeX = trigger.SizeY = SemanticSize.ChildrenSum();
                    using (runtime.PushParent(trigger))
                        using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text("@fernway", textColor: TextColor.Accent);
                    Arrange.Spring(row);

                    if (Popover.Begin(runtime, id, trigger, width: 280f) is { } card)
                    {
                        Paragraph(runtime, "demo-hover-preview-text", "The team behind fernway/ui. Building design systems that ship.");
                        hovering |= card.End().Hovering;
                    }
                }
            }

            bool open = runtime.IsDropDownOpen(id);
            Arrange.Gap(page, 16f);
            runtime.TextFromId("demo-hover-preview-state", open ? "Preview: open" : "Preview: closed", ComponentSize.Sm, TextColor.Secondary);

            if (clicked)
            {
                HoverPreviewByHover = false;
                if (open) runtime.CloseDropDown(id); else runtime.OpenDropDown(id, trigger);
            }
            else if (!open && hovering)
            {
                HoverPreviewByHover = true;
                runtime.OpenDropDown(id, trigger);
            }
            else if (open && HoverPreviewByHover && !hovering)
            {
                HoverPreviewByHover = false;
                runtime.CloseDropDown(id);
            }
        }
        return page;
    }

    // ── Tooltip ─────────────────────────────────────────────────────────────

    private static UiNode TooltipPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Point at a button, or pin the first hint open");
            if (runtime.Button("demo-tooltip-toggle", state.TooltipOpen ? "Hide tooltip" : "Show tooltip").Clicked)
                set(state with { TooltipOpen = !state.TooltipOpen });
            Arrange.Gap(page, 16f);

            // One tooltip at a time: the one under the pointer, or failing that
            // the one the button pins open.
            BibuiWidget main = TooltipTrigger(runtime, page, "demo-tooltip", "Hover me", 260f, 100f);
            Arrange.Gap(page, 16f);
            bool shown = state.TooltipOpen || main.Hovering;
            runtime.TextFromId("demo-tooltip-state", shown ? "Tooltip: open" : "Tooltip: closed", ComponentSize.Sm, TextColor.Secondary);
            Arrange.Gap(page, 16f);

            Box row = runtime.Box();
            row.ChildLayoutAxis = LayoutAxis.X;
            row.SizeX = row.SizeY = SemanticSize.ChildrenSum();
            BibuiWidget copy, share;
            using (runtime.PushParent(row))
            {
                copy = TooltipTrigger(runtime, row, "demo-tooltip-copy", "Copy", 180f, 80f);
                runtime.Gap(12f);
                share = TooltipTrigger(runtime, row, "demo-tooltip-share", "Share", 180f, 80f);
            }

            if (copy.Hovering) Tooltip.Build(runtime, "Copy to clipboard", (Box)copy.Node);
            else if (share.Hovering) Tooltip.Build(runtime, "Share a link to this page", (Box)share.Node);
            else if (shown) Tooltip.Build(runtime, "Tooltip.Build, anchored to this button", (Box)main.Node);
        }
        return page;
    }

    // A button centred in an area of the given size, as the reference tooltip
    // sat in the size it was given.
    private static BibuiWidget TooltipTrigger(BibuiRuntime runtime, UiNode parent, string id, string label, float width, float height)
    {
        Box area = Area(runtime, width, height);
        using (runtime.PushParent(area))
        {
            Arrange.Spring(area);
            Box row = CentredRow(runtime);
            BibuiWidget button;
            using (runtime.PushParent(row))
            {
                Arrange.Spring(row);
                button = runtime.Button(id, label);
                Arrange.Spring(row);
            }
            Arrange.Spring(area);
            return button;
        }
    }

    // ── DropdownMenu and ContextMenu: one menu, opened two ways ─────────────

    private static readonly (string Label, string Icon, string? Shortcut)[] AccountItems =
        [("Profile", "user", null), ("Settings", "settings", "⌘S"), ("", "", null), ("Log out", "log-out", null)];

    private static UiNode DropdownMenuPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        const string id = "demo-dropdown-menu-list";
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "A menu that opens under its button");
            string? picked = null;
            Box area = Area(runtime, 280f, 200f);
            using (runtime.PushParent(area))
            {
                runtime.Gap(2f);
                Box row = CentredRow(runtime);
                using (runtime.PushParent(row))
                {
                    Arrange.Spring(row);
                    Box trigger = runtime.BoxFromId("demo-dropdown-menu", InteractionKinds.LeftPress);
                    if (runtime.InteractionsFor(trigger).Clicked)
                        if (runtime.IsDropDownOpen(id)) runtime.CloseDropDown(id); else runtime.OpenDropDown(id, trigger);
                    trigger.ChildLayoutAxis = LayoutAxis.X;
                    trigger.SizeX = trigger.SizeY = SemanticSize.ChildrenSum();
                    trigger.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: 8f);
                    trigger.Style.CornerRadius = 8f;
                    trigger.Style.Draw = DrawFlags.Border | DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects;
                    using (runtime.PushParent(trigger))
                    {
                        runtime.Text("My Account");
                        runtime.Gap(8f);
                        Box cell = runtime.Box();
                        cell.ChildLayoutAxis = LayoutAxis.Y;
                        cell.SizeX = cell.SizeY = SemanticSize.ChildrenSum();
                        using (runtime.PushParent(cell))
                        {
                            runtime.Gap(3f);
                            runtime.Icon(runtime.IsDropDownOpen(id) ? "chevron-up" : "chevron-down", 14f).Color = UiColor.TextSecondary;
                        }
                    }
                    Arrange.Spring(row);

                    if (Menu.BeginBelow(runtime, id, trigger) is { } menu)
                    {
                        for (int i = 0; i < AccountItems.Length; i++)
                        {
                            var (label, icon, shortcut) = AccountItems[i];
                            if (label.Length == 0) { Menu.Separator(runtime); continue; }
                            if (runtime.MenuItem($"demo-dropdown-menu-item-{i}", label, icon,
                                    shortcut is null ? null : shortcut).Clicked)
                                picked = label;
                        }
                        menu.End();
                    }
                }
            }

            Arrange.Gap(page, 16f);
            runtime.TextFromId("demo-dropdown-state", runtime.IsDropDownOpen(id) ? "Dropdown: open" : $"Dropdown: closed — selected: {state.DropdownLastSelected ?? "none"}", ComponentSize.Sm, TextColor.Secondary);

            if (picked is not null)
            {
                runtime.CloseDropDown(id);
                set(state with { DropdownLastSelected = picked });
            }
        }
        return page;
    }

    private static readonly (string Label, string Icon, bool Disabled)[] FileItems =
        [("Open", "folder", false), ("Copy link", "link", false), ("", "", false), ("Rename", "pencil", false), ("Delete", "trash", true)];

    // Where the right button last went down in the area, for the menu to open
    // there; null opens it in the area's corner, as the open page's picture does.
    private static System.Numerics.Vector2? ContextAt;

    private static UiNode ContextMenuPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        const string id = "demo-context-menu-list";
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Right-click in the box: the menu opens where you pressed");

            // The area a right press opens the menu in. An open menu's rect is
            // its own, so a press on the menu never reaches the area; a right
            // press elsewhere in it closes the menu and opens it there.
            Box area = runtime.BoxFromId("demo-context-menu", InteractionKinds.RightPress);
            Interaction onArea = runtime.InteractionsFor(area);
            if (onArea.RightPressed)
            {
                ContextAt = new System.Numerics.Vector2(onArea.X, onArea.Y);
                runtime.OpenDropDown(id);
            }
            area.ChildLayoutAxis = LayoutAxis.Y;
            area.Px(280f, 200f);

            string? picked = null;
            var at = ContextAt ?? area.Rect + new System.Numerics.Vector2(16f, 16f);
            if (Menu.Begin(runtime, id, at.X, at.Y) is { } menu)
            {
                for (int i = 0; i < FileItems.Length; i++)
                {
                    var (label, icon, disabled) = FileItems[i];
                    if (label.Length == 0) { Menu.Separator(runtime); continue; }
                    if (runtime.MenuItem($"demo-context-menu-item-{i}", label, icon, disabled: disabled).Clicked)
                        picked = label;
                }
                menu.End();
            }

            Arrange.Gap(page, 16f);
            runtime.TextFromId("demo-context-menu-state", runtime.IsDropDownOpen(id) ? "Context menu: open" : $"Context menu: closed — selected: {state.ContextLastSelected ?? "none"}", ComponentSize.Sm, TextColor.Secondary);

            if (picked is not null)
            {
                runtime.CloseDropDown(id);
                set(state with { ContextLastSelected = picked });
            }
        }
        return page;
    }

    // ── CommandPalette: a search field over a filtered list ─────────────────

    private static readonly (string Label, string Icon, string Group)[] Commands =
    [
        ("Open file…", "folder", "Files"),
        ("New file", "plus", "Files"),
        ("Save all", "save", "Files"),
        ("Theme", "sliders", "Settings"),
        ("Keyboard shortcuts", "settings", "Settings"),
        ("Account", "user", "Settings"),
    ];

    private static readonly BibuiStr CommandQuery = new(64);
    private static readonly TextEditState CommandEdit = new();
    private static string? CommandRan;

    // Open is the search field being edited: the button or a press on the
    // field opens it, the button or Escape closes it.
    private static UiNode CommandPalettePage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Press the field or the button, then type; Escape closes it");
            bool toggled = PrimaryButton(runtime, "demo-command-toggle", state.CommandPaletteOpen ? "Close command palette" : "Open command palette").Clicked;
            Arrange.Gap(page, 16f);

            // In the 480 by 360 the reference palette was given, so the page
            // below it stays put however few commands match.
            Box area = Area(runtime, 480f, 360f);
            var inArea = runtime.PushParent(area);
            Box palette = runtime.Box();
            palette.ChildLayoutAxis = LayoutAxis.Y;
            palette.SizeX = SemanticSize.Pixels(480f);
            palette.SizeY = SemanticSize.ChildrenSum();
            palette.Style.CornerRadius = 8f;
            palette.Style.BorderColor = UiColor.Border;
            palette.Style.BackgroundColor = UiColor.SurfaceRaised;

            bool focus = false;
            string? ran = null;
            using (runtime.PushParent(palette))
            {
                Box search = runtime.Box();
                search.ChildLayoutAxis = LayoutAxis.X;
                search.SizeX = SemanticSize.ParentPct(1f);
                search.SizeY = SemanticSize.ChildrenSum();
                search.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: 12f);
                using (runtime.PushParent(search))
                {
                    Box cell = runtime.Box();
                    cell.ChildLayoutAxis = LayoutAxis.Y;
                    cell.SizeX = cell.SizeY = SemanticSize.ChildrenSum();
                    using (runtime.PushParent(cell))
                    {
                        runtime.Gap(2f);
                        runtime.Icon("search", 16f).Color = UiColor.TextSecondary;
                    }
                    runtime.Gap(12f);
                    Box field = runtime.Box();
                    field.ChildLayoutAxis = LayoutAxis.X;
                    field.SizeX = SemanticSize.ParentPct(1f);
                    field.SizeY = SemanticSize.ChildrenSum();
                    using (runtime.PushParent(field))
                        focus = runtime.TextField("demo-command-search", CommandQuery, CommandEdit, state.CommandPaletteOpen,
                            textColor: TextColor.Primary, placeholder: "Type a command…").Pressed;
                }
                Box rule = runtime.Box();
                rule.SizeX = SemanticSize.ParentPct(1f);
                rule.SizeY = SemanticSize.Pixels(1f);
                rule.Style.BackgroundColor = UiColor.Border;

                Box list = runtime.Box();
                list.ChildLayoutAxis = LayoutAxis.Y;
                list.SizeX = SemanticSize.ParentPct(1f);
                list.SizeY = SemanticSize.ChildrenSum();
                list.Style.Padding = EdgeInsets.All(4f);
                using (runtime.PushParent(list))
                {
                    string query = CommandQuery.ToString();
                    string? group = null;
                    bool any = false;
                    for (int i = 0; i < Commands.Length; i++)
                    {
                        var (label, icon, itemGroup) = Commands[i];
                        if (!label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        if (itemGroup != group)
                        {
                            Menu.Label(runtime, itemGroup);
                            group = itemGroup;
                        }
                        if (runtime.MenuItem($"demo-command-item-{i}", label, icon).Clicked) ran = label;
                        any = true;
                    }
                    if (!any)
                    {
                        Box empty = CentredRow(runtime);
                        empty.Style.Padding = EdgeInsets.Symmetric(horizontal: 0f, vertical: 24f);
                        using (runtime.PushParent(empty))
                        {
                            Arrange.Spring(empty);
                            runtime.Text("No command matches.", ComponentSize.Sm, TextColor.Secondary);
                            Arrange.Spring(empty);
                        }
                    }
                }
            }

            inArea.Dispose();
            Arrange.Gap(page, 16f);
            runtime.TextFromId("demo-command-state", (state.CommandPaletteOpen ? "Command palette: open" : "Command palette: closed")
                + (CommandRan is null ? "" : $" — ran: {CommandRan}"), ComponentSize.Sm, TextColor.Secondary);

            // Escape ends the search, as it ends editing a field elsewhere.
            bool escape = false;
            if (state.CommandPaletteOpen)
                foreach (var ev in runtime.UnconsumedEvents)
                    if (ev is { IsKeyDown: true, Key: Bibui.Key.Escape })
                    {
                        escape = true;
                        ev.Eat();
                    }

            if (ran is not null)
            {
                CommandRan = ran;
                CommandQuery.ReplaceRange((0, (int)CommandQuery.Length), default);
                CommandEdit.Cursor = CommandEdit.Mark = 0;
                set(state with { CommandPaletteOpen = false });
            }
            else if (toggled || escape) set(state with { CommandPaletteOpen = !state.CommandPaletteOpen });
            else if (focus && !state.CommandPaletteOpen) set(state with { CommandPaletteOpen = true });
        }
        return page;
    }

    // ── Dialogs ──────────────────────────────────────────────────────────────

    // A page whose button opens a modal, and says whether it is open.
    private static Box ModalTrigger(BibuiRuntime runtime, string heading, string id, string name, bool open, Action toggle)
    {
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, heading);
            if (PrimaryButton(runtime, id, open ? $"Close {name}" : $"Open {name}").Clicked) toggle();
            Arrange.Gap(page, 16f);
            runtime.Text(open ? $"{Capital(name)}: open" : $"{Capital(name)}: closed", ComponentSize.Sm, TextColor.Secondary);
        }
        return page;

        static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];
    }

    // How far open a modal is, eased: asked every frame, open or not, so it
    // travels both ways.
    private static float Shown(BibuiRuntime runtime, string id, bool open)
        => runtime.Animate(id, "open", open ? 1f : 0f, runtime.Rate(18f));

    // A dialog's title and the words under it.
    private static void DialogHeader(BibuiRuntime runtime, string id, string title, string description)
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(title, ComponentSize.Lg);
        runtime.Gap(6f);
        Paragraph(runtime, id + "-description", description);
    }

    // The action row at a dialog's foot, its buttons to the right.
    private static Box Actions(BibuiRuntime runtime)
    {
        runtime.Gap(24f);
        var row = CentredRow(runtime);
        Arrange.Spring(row);
        return row;
    }

    private static UiNode DialogPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        var page = ModalTrigger(runtime, "Cancel, Rename or a press beside it closes it", "demo-dialog-toggle", "dialog", state.DialogOpen,
            () => set(state with { DialogOpen = !state.DialogOpen }));

        float shown = Shown(runtime, "demo-dialog", state.DialogOpen);
        if (shown > 0f)
        {
            var dialog = Dialog.Begin(runtime, "demo-dialog", shown, width: 460f);
            DialogHeader(runtime, "demo-dialog", "Rename workspace", "The new name shows in the sidebar and in links you share.");
            bool close;
            using (runtime.PushParent(Actions(runtime)))
            {
                close = runtime.Button("demo-dialog-cancel", "Cancel").Clicked;
                runtime.Gap(8f);
                close |= PrimaryButton(runtime, "demo-dialog-save", "Rename").Clicked;
            }
            if (dialog.End().Dismissed | close && state.DialogOpen) set(state with { DialogOpen = false });
        }
        return page;
    }

    private static UiNode AlertDialogPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        var page = ModalTrigger(runtime, "Only Cancel or Delete workspace closes it", "demo-alert-dialog-toggle", "alert dialog",
            state.AlertDialogOpen, () => set(state with { AlertDialogOpen = !state.AlertDialogOpen }));

        float shown = Shown(runtime, "demo-alert-dialog", state.AlertDialogOpen);
        if (shown > 0f)
        {
            var dialog = Dialog.Begin(runtime, "demo-alert-dialog", shown, width: 480f);
            DialogHeader(runtime, "demo-alert-dialog", "Delete this workspace?",
                "Its files and settings are deleted with it, for everyone in it. This cannot be undone.");
            bool close;
            using (runtime.PushParent(Actions(runtime)))
            {
                close = runtime.Button("demo-alert-dialog-cancel", "Cancel").Clicked;
                runtime.Gap(8f);
                close |= DestructiveButton(runtime, "demo-alert-dialog-confirm", "Delete workspace").Clicked;
            }
            // Blocking: a press beside it is not an answer, so only its buttons close it.
            dialog.End();
            if (close && state.AlertDialogOpen) set(state with { AlertDialogOpen = false });
        }
        return page;
    }

    // A panel that slides in from an edge: from the right, or from the bottom
    // with a grab handle. Each has its own button and its own state.
    private static UiNode DrawerPage(BibuiRuntime runtime, OverlaysState state, Action<OverlaysState> set)
    {
        var page = runtime.ColumnFit();
        using (runtime.PushParent(page))
        {
            Heading(runtime, "Open either one; the cross or a press beside it closes it");
            using (runtime.PushParent(DemoNavigation.Row(runtime)))
            {
                if (PrimaryButton(runtime, "demo-drawer-right-toggle", state.DrawerRightOpen ? "Close right drawer" : "Open from the right").Clicked)
                    set(state with { DrawerRightOpen = !state.DrawerRightOpen });
                runtime.Gap(8f);
                if (PrimaryButton(runtime, "demo-drawer-bottom-toggle", state.DrawerBottomOpen ? "Close bottom drawer" : "Open from the bottom").Clicked)
                    set(state with { DrawerBottomOpen = !state.DrawerBottomOpen });
            }
            Arrange.Gap(page, 16f);
            runtime.Text($"Right: {(state.DrawerRightOpen ? "open" : "closed")}    Bottom: {(state.DrawerBottomOpen ? "open" : "closed")}",
                ComponentSize.Sm, TextColor.Secondary);
        }

        float right = Shown(runtime, "demo-drawer-right", state.DrawerRightOpen);
        if (right > 0f)
        {
            var drawer = Dialog.Begin(runtime, "demo-drawer-right", right, DialogSide.Right, width: 380f);
            bool close = CloseMark(runtime, "demo-drawer-right-close");
            DialogHeader(runtime, "demo-drawer-right", "Workspace details", "Who owns it, who can see it, and where its files are kept.");
            runtime.Gap(16f);
            runtime.Text("Owner, members, storage…", ComponentSize.Sm, TextColor.Secondary);
            if (drawer.End().Dismissed | close && state.DrawerRightOpen) set(state with { DrawerRightOpen = false });
        }

        float bottom = Shown(runtime, "demo-drawer-bottom", state.DrawerBottomOpen);
        if (bottom > 0f)
        {
            var drawer = Dialog.Begin(runtime, "demo-drawer-bottom", bottom, DialogSide.Bottom);
            // The grab handle, centred along the top.
            var handle = CentredRow(runtime);
            using (runtime.PushParent(handle))
            {
                Arrange.Spring(handle);
                var bar = runtime.Box().Px(48f, 4f);
                bar.Style.BackgroundColor = UiColor.Border;
                bar.Style.CornerRadius = 2f;
                Arrange.Spring(handle);
            }
            runtime.Gap(12f);
            bool close = CloseMark(runtime, "demo-drawer-bottom-close");
            DialogHeader(runtime, "demo-drawer-bottom", "Export", "Choose a format, and where to save the file.");
            runtime.Gap(16f);
            runtime.Text("PNG, twice the size, to ~/Pictures/exports", ComponentSize.Sm);
            if (drawer.End().Dismissed | close && state.DrawerBottomOpen) set(state with { DrawerBottomOpen = false });
        }
        return page;
    }

    // A small "x" floated in the panel's top right corner; true when clicked.
    private static bool CloseMark(BibuiRuntime runtime, string id)
    {
        var panel = runtime.ParentInScope;
        Box mark = runtime.BoxFromId(id, InteractionKinds.LeftPress);
        bool clicked = runtime.InteractionsFor(mark).Clicked;
        mark.Px(24f, 24f).At(panel.Extent.X - 24f - 12f, 12f);
        mark.Style.CornerRadius = 4f;
        mark.Style.HoverBackgroundColor = UiColor.SurfaceRaised;
        mark.Style.Padding = EdgeInsets.All(4f);
        using (runtime.PushParent(mark)) runtime.Icon("x", 16f);
        return clicked;
    }

    // The accent button's shape in the destructive colour.
    private static BibuiWidget DestructiveButton(BibuiRuntime runtime, string id, string label)
    {
        Box box = runtime.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(box);
        box.ChildLayoutAxis = LayoutAxis.X;
        box.SizeX = box.SizeY = SemanticSize.ChildrenSum();
        box.Style.Padding = EdgeInsets.Symmetric(horizontal: 16f, vertical: 8f);
        box.Style.CornerRadius = 8f;
        box.Style.BackgroundColor = UiColor.Destructive;
        using (runtime.PushParent(box))
            runtime.Text(label);
        return new BibuiWidget(box, interaction);
    }

    // A button in the accent colour: the look of the reference primary button.
    private static BibuiWidget PrimaryButton(BibuiRuntime runtime, string id, string label)
    {
        Box box = runtime.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(box);
        box.ChildLayoutAxis = LayoutAxis.X;
        box.SizeX = box.SizeY = SemanticSize.ChildrenSum();
        box.Style.Padding = EdgeInsets.Symmetric(horizontal: 16f, vertical: 8f);
        box.Style.CornerRadius = 8f;
        box.Style.BackgroundColor = UiColor.Accent;
        box.Style.HoverBackgroundColor = UiColor.AccentHover;
        using (runtime.PushParent(box))
            runtime.Text(label, textColor: TextColor.AccentForeground);
        return new BibuiWidget(box, interaction);
    }
}
