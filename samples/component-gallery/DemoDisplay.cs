using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

/// <summary>What the Display pages hold between frames.</summary>
internal sealed record DisplayState
{
    public float ProgressValue { get; init; } = 65f;
    public int PagedTablePage { get; init; } = 1;
    public string? ActiveItem { get; init; } = "Settings";
    public bool ToastVisible { get; init; }

    /// <summary>The page's open state, for a page that has one.</summary>
    internal DisplayState? Opened(string route) => route switch
    {
        "Toast" => this with { ToastVisible = true },
        _ => null,
    };
}

internal static class DemoDisplay
{
    // Nothing edits a label, so every one shares a state no one reads.
    private static readonly Bibui.TextEdit.TextEditState NoEdit = new();

    private static readonly string LongFiller = "When content is genuinely long, the box stops at six lines and scrolls. "
        + string.Join(' ', Enumerable.Repeat("Filler sentence to fill many wrapped lines.", 30));

    // A paragraph in a dark box of the given width, around its lines: as tall
    // as they are, or the given rows tall and scrolling with the wheel to the
    // lines past them. Nothing edits it.
    private static Box Paragraph(BibuiRuntime runtime, string id, string text, float width, int? rows = null)
    {
        Box box = runtime.Box();
        box.SizeX = SemanticSize.Pixels(width);
        box.SizeY = SemanticSize.ChildrenSum();
        box.Style.BackgroundColor = "#18181B";
        box.Style.CornerRadius = 6f;
        box.Style.Padding = EdgeInsets.All(8);
        using (runtime.PushParent(box))
        using (runtime.PushSizeY(rows is { } r ? TextField.Rows(runtime, r) : SemanticSize.ChildrenSum()))
            runtime.MultilineTextField(id, GalleryHelpers.Held(id, text), NoEdit, focused: false, events: 0);
        return box;
    }

    // The editable ellipsis: what an application would hold for it.
    private static readonly BibuiStr EllipsisText = BibuiStr.FromStrLit(
        "This is a very long label that gets truncated with an ellipsis character");
    private static readonly Bibui.TextEdit.TextEditState EllipsisEdit = new();
    private static bool EllipsisFocused;

    // A line in a dark box of the given width, ending in "…" when it is wider
    // than the box. A press edits it, and then it scrolls rather than ending
    // in "…"; Enter or Escape stops editing.
    private static Box EllipsisField(BibuiRuntime runtime, string id, float width)
    {
        Box box = runtime.Box();
        box.SizeX = SemanticSize.Pixels(width);
        box.SizeY = SemanticSize.ChildrenSum();
        box.Style.BackgroundColor = "#18181B";
        box.Style.CornerRadius = 6f;
        box.Style.Padding = EdgeInsets.All(8);
        box.Style.BorderWidth = 1f;
        box.Style.BorderColor = EllipsisFocused ? UiColor.BorderFocus : UiColor.Border;
        BibuiWidget line;
        using (runtime.PushParent(box))
            line = runtime.EllipsisTextField(id, EllipsisText, EllipsisEdit, EllipsisFocused, events: TextField.SingleLineEvents);
        if (line.Pressed) EllipsisFocused = true;
        if (EllipsisFocused)
            foreach (var ev in runtime.UnconsumedEvents)
                if (ev is { IsKeyDown: true, Key: Bibui.Key.Escape } or { Kind: Bibui.TextEdit.BibUiEventKind.Submit })
                {
                    EllipsisFocused = false;
                    ev.Eat();
                }
        return box;
    }

    internal static UiNode Build(BibuiRuntime runtime, string route, DisplayState state, Action<DisplayState> set) => route switch
    {
    "Text" => Arrange.Column(
        Bold(runtime, "Heading (Xxl, Bold)", ComponentSize.Xxl),
        Arrange.Space(10f),
        runtime.Text("Large text (Xl)", ComponentSize.Xl),
        Arrange.Space(10f),
        runtime.Text("Body text (Md) — the default", ComponentSize.Md),
        Arrange.Space(10f),
        runtime.Text("Small text (Sm)", ComponentSize.Sm),
        Arrange.Space(10f),
        runtime.Text("Secondary color", textColor: TextColor.Secondary),
        Arrange.Space(10f),
        runtime.Text("Accent / brand color", textColor: TextColor.Accent),
        Arrange.Space(10f),
        runtime.Text("Disabled / muted", textColor: TextColor.Disabled),
        Arrange.Space(18f),
        Bold(runtime, "Ellipsis overflow: press to edit, Enter or Escape to stop", ComponentSize.Sm, TextColor.Secondary),
        Arrange.Space(10f),
        EllipsisField(runtime, "demo-text-ellipsis", 220f),
        Arrange.Space(10f),
        Bold(runtime, "Two lines tall, scrolling to the rest", ComponentSize.Sm, TextColor.Secondary),
        Arrange.Space(10f),
        Paragraph(runtime, "demo-text-two-rows", "This paragraph wraps to multiple lines. The box is two lines tall, and the lines past the second are below its edge: scroll the wheel over it to bring them into view.", 280f, rows: 2),
        Arrange.Space(22f),
        Bold(runtime, "Hugging its text, or scrolling past a height", ComponentSize.Sm, TextColor.Secondary),
        Arrange.Space(10f),
        Paragraph(runtime, "demo-text-hug-short", "Hi!", 320f),
        Arrange.Space(10f),
        Paragraph(runtime, "demo-text-hug-medium", "A medium-length message that wraps to a few lines. Notice the box hugs the text: it is as tall as its lines.", 320f),
        Arrange.Space(10f),
        Paragraph(runtime, "demo-text-six-rows", LongFiller, 320f, rows: 6)),

    "Badge" => Arrange.Column(
        Bold(runtime, "Status indicators", ComponentSize.Sm, TextColor.Secondary),
        Arrange.Space(16f),
        Arrange.Row(
            Badge.Build(runtime, "Default"),
            Arrange.Space(8f),
            Badge.Build(runtime, "Primary", BadgeVariant.Primary),
            Arrange.Space(8f),
            Badge.Build(runtime, "Success", BadgeVariant.Success),
            Arrange.Space(8f),
            Badge.Build(runtime, "Destructive", BadgeVariant.Destructive))),

    "Avatar" => AvatarPage(runtime),

    "Card" => CardPage(runtime),

    "Alert" => AlertPage(runtime),

    "Separator" => SeparatorPage(runtime),
    "Texture" => DemoTexture.Build(runtime),

    "Label" => Arrange.Column(
        Bold(runtime, "Form labels paired with inputs", ComponentSize.Sm, TextColor.Secondary),
        Arrange.Space(12f),
        DemoInputs.FieldLabel(runtime, "Email address"),
        Arrange.Space(12f),
        DemoInputs.StaticInput(runtime, "demo-label-email", "you@example.com", 280f),
        Arrange.Space(12f),
        DemoInputs.FieldLabel(runtime, "Password"),
        Arrange.Space(12f),
        DemoInputs.StaticInput(runtime, "demo-label-password", "••••••••", 280f)),

    "Progress" => ProgressPage(runtime, state, set),

    "Skeleton" => SkeletonPage(runtime),

    "Spinner" => Arrange.Column(
        Bold(runtime, "Loading spinner", ComponentSize.Sm, TextColor.Secondary),
        Arrange.Space(20f),
        Arrange.Row(
            Arrange.CentredY(new Spinner { Size = ComponentSize.Sm }.Px(16f, 16f)),
            Arrange.Space(20f),
            Arrange.CentredY(new Spinner { Size = ComponentSize.Md }.Px(24f, 24f)),
            Arrange.Space(20f),
            Arrange.CentredY(new Spinner { Size = ComponentSize.Lg }.Px(32f, 32f))).Px(height: 40f),
        Arrange.Space(20f),
        Arrange.Row(
            Arrange.CentredY(new Spinner { Size = ComponentSize.Sm }.Px(16f, 16f)),
            Arrange.Space(8f),
            Arrange.CentredY(runtime.Text("Loading data…", ComponentSize.Sm, TextColor.Secondary))).Px(height: 40f)),

    "RatioBox" => RatioBoxPage(runtime),

    "KeyboardShortcut" => KeyboardShortcutPage(runtime),

    "Table" => TablePage(runtime),

    "Markdown" => DemoMarkdown.Page(runtime),

    "PagedTable" => PagedTablePage(runtime, state, set),

    "FormField" => FormFieldPage(runtime),

    "ListItem" => ListItemPage(runtime, state, set),

    "EmptyState" => EmptyStatePage(runtime),

    "Toast" => ToastPage(runtime, state, set),

        _ => runtime.Text("Select a component from the sidebar.", textColor: TextColor.Secondary),
    };

    // ── Shared by the pages below ───────────────────────────────────────────
    // Each page is a layout expression with nothing pushed, so every node it
    // makes is unlinked until the expression places it. A helper that builds
    // into a box of its own pushes that box, and hands it back unlinked.

    private static readonly UiColor Info = UiColor.Token(c => c.Info);
    private static readonly UiColor InfoBg = UiColor.Token(c => c.InfoBg);
    private static readonly UiColor Warning = UiColor.Token(c => c.Warning);
    private static readonly UiColor WarningBg = UiColor.Token(c => c.WarningBg);

    // A small bold caption above a demo.
    private static Text Caption(BibuiRuntime runtime, string text)
        => Bold(runtime, text, ComponentSize.Sm, TextColor.Secondary);

    private static float LineHeight(BibuiRuntime runtime, ComponentSize size, FontWeight weight = FontWeight.Regular)
    {
        using (runtime.PushFontFace(weight: weight))
            return runtime.Fonts.LineHeight(runtime.FontInScope(size));
    }

    // A gap that puts the next line `advance` below the top of a line set in
    // `size`: what the reference components spaced their lines by.
    private static void Advance(BibuiRuntime runtime, float advance, ComponentSize size, FontWeight weight = FontWeight.Regular)
        => runtime.Gap(MathF.Max(0f, advance - LineHeight(runtime, size, weight)));

    /// <summary>
    /// A button in the reference look: solid in the accent, or raised with an
    /// edge, or — disabled — the page's surface with an edge and a faint
    /// label. Its label is centred in a box <paramref name="height"/> tall
    /// and <paramref name="width"/> wide, or as wide as the label and its
    /// padding. runtime.Button draws only the raised and ghost looks, so the
    /// page draws the solid one itself.
    /// </summary>
    private static BibuiWidget PageButton(BibuiRuntime runtime, string id, string label, bool primary,
        float? width = null, float height = 40f, ComponentSize size = ComponentSize.Md, float radius = 8f,
        float padX = 16f, bool disabled = false)
    {
        Box box = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(box);
        box.ChildLayoutAxis = LayoutAxis.Y;
        box.SizeX = width is { } w ? SemanticSize.Pixels(w) : SemanticSize.ChildrenSum();
        box.SizeY = SemanticSize.Pixels(height);
        Style style = box.Style;
        style.CornerRadius = radius;
        style.Padding = EdgeInsets.Symmetric(horizontal: width is null ? padX : 0f, vertical: 0f);
        if (disabled)
        {
            style.BackgroundColor = UiColor.Surface;
            style.BorderColor = UiColor.Border;
        }
        else if (primary)
        {
            style.BackgroundColor = UiColor.Accent;
            style.HoverBackgroundColor = UiColor.AccentHover;
        }
        else
        {
            style.BackgroundColor = UiColor.SurfaceRaised;
            style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
            style.BorderColor = UiColor.Border;
        }

        UiColor ink = disabled ? TextColor.Disabled : primary ? TextColor.AccentForeground : TextColor.Primary;
        using (runtime.PushParent(box))
        {
            Arrange.Spring(box);
            if (width is null) runtime.Text(label, size, ink);
            else
            {
                Box row = runtime.Box();
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.ParentPct(1f);
                row.SizeY = SemanticSize.ChildrenSum();
                using (runtime.PushParent(row))
                {
                    Arrange.Spring(row);
                    runtime.Text(label, size, ink);
                    Arrange.Spring(row);
                }
            }
            Arrange.Spring(box);
        }
        return new BibuiWidget(box, interaction);
    }

    // A label centred both ways in a box the caller has sized.
    private static void CentredLabel(BibuiRuntime runtime, Box box, string label, ComponentSize size, UiColor color)
    {
        box.ChildLayoutAxis = LayoutAxis.X;
        using (runtime.PushParent(box))
        {
            Arrange.Spring(box);
            Box column = runtime.Box();
            column.ChildLayoutAxis = LayoutAxis.Y;
            column.SizeX = SemanticSize.ChildrenSum();
            column.SizeY = SemanticSize.ParentPct(1f);
            using (runtime.PushParent(column))
            {
                Arrange.Spring(column);
                runtime.Text(label, size, color);
                Arrange.Spring(column);
            }
            Arrange.Spring(box);
        }
    }

    // A column, under the parent in scope, as wide as what is in it and as
    // tall as the parent: what centres an icon or a label down a row.
    private static Box DownTheMiddle(BibuiRuntime runtime)
    {
        Box column = runtime.Box();
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = SemanticSize.ChildrenSum();
        column.SizeY = SemanticSize.ParentPct(1f);
        return column;
    }

    // ── Avatar ──────────────────────────────────────────────────────────────

    private static UiNode AvatarPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "User avatars with initials fallback"),
        Arrange.Space(20f),
        Arrange.Row(
            Avatar.Build(runtime, "JD", 56f),
            Arrange.Space(12f),
            Avatar.Build(runtime, "AB", 56f),
            Arrange.Space(12f),
            Avatar.Build(runtime, "MK", 56f),
            Arrange.Space(12f),
            Avatar.Build(runtime, "TC", 56f)));

    // ── Card ────────────────────────────────────────────────────────────────

    // A raised, bordered box: a bold title, a muted description, and a body
    // that wraps, each spaced as the reference card spaced them.
    private static Box Card(BibuiRuntime runtime, string id, string title, string description, string? body, float width)
    {
        Box card = runtime.Box();
        card.ChildLayoutAxis = LayoutAxis.Y;
        card.SizeX = SemanticSize.Pixels(width);
        card.SizeY = SemanticSize.ChildrenSum();
        card.Style.BackgroundColor = UiColor.SurfaceRaised;
        card.Style.BorderColor = UiColor.Border;
        card.Style.BorderWidth = 1f;
        card.Style.CornerRadius = 8f;
        card.Style.Padding = EdgeInsets.All(16f);
        using (runtime.PushParent(card))
        {
            using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(title, ComponentSize.Lg);
            Advance(runtime, 16f * 1.8f, ComponentSize.Lg, FontWeight.Bold);
            runtime.Text(description, ComponentSize.Md, TextColor.Secondary);
            if (body is not null)
            {
                Advance(runtime, 14f * 1.8f + 18f - 14f * 0.4f, ComponentSize.Md);
                Box lines = runtime.Box();
                lines.SizeX = SemanticSize.ParentPct(1f);
                lines.SizeY = SemanticSize.ChildrenSum();
                using (runtime.PushParent(lines))
                using (runtime.PushSizeY(SemanticSize.ChildrenSum()))
                    runtime.MultilineTextField(id, GalleryHelpers.Held(id, body), NoEdit, focused: false, events: 0);
            }
        }
        return card;
    }

    private static UiNode CardPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "Content container with title and description"),
        Arrange.Space(16f),
        Card(runtime, "demo-card-workspace", "New workspace", "A place for one team's files and settings.",
            "Give it a name and choose who can see it. Both can be changed later.", 420f),
        Arrange.Space(16f),
        Card(runtime, "demo-card-inbox", "Inbox", "3 messages since you last looked.", null, 420f));

    // ── Alert ───────────────────────────────────────────────────────────────

    // A banner 80 px tall across its parent: an icon down the middle of the
    // left edge in the variant's colour, and a bold title over a description.
    private static Box Alert(BibuiRuntime runtime, string title, string description, UiColor accent, UiColor fill, string icon)
    {
        Box alert = runtime.Box();
        alert.ChildLayoutAxis = LayoutAxis.X;
        alert.SizeX = SemanticSize.ParentPct(1f);
        alert.SizeY = SemanticSize.Pixels(80f);
        alert.Style.BackgroundColor = fill;
        alert.Style.BorderColor = accent;
        alert.Style.BorderWidth = 1f;
        alert.Style.CornerRadius = 6f;
        alert.Style.Padding = new EdgeInsets(0f, 12f, 0f, 12f);
        using (runtime.PushParent(alert))
        {
            Box middle = DownTheMiddle(runtime);
            using (runtime.PushParent(middle))
            {
                Arrange.Spring(middle);
                runtime.Icon(icon, 20f).Color = accent;
                Arrange.Spring(middle);
            }
            runtime.Gap(8f);
            Box text = runtime.Box();
            text.ChildLayoutAxis = LayoutAxis.Y;
            text.SizeX = text.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(text))
            {
                runtime.Gap(12f);
                using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(title, ComponentSize.Md);
                Advance(runtime, 14f * 1.4f + 4f, ComponentSize.Md, FontWeight.Bold);
                runtime.Text(description, ComponentSize.Sm, TextColor.Secondary);
            }
        }
        return alert;
    }

    private static UiNode AlertPage(BibuiRuntime runtime) => Arrange.Column(
        Alert(runtime, "Sync paused", "Files will sync again when the connection is back.", UiColor.Border, UiColor.Surface, "info"),
        Arrange.Space(12f),
        Alert(runtime, "New version", "Restart to finish updating.", Info, InfoBg, "info"),
        Arrange.Space(12f),
        Alert(runtime, "Storage almost full", "92% of the workspace's space is in use.", Warning, WarningBg, "alert-circle"),
        Arrange.Space(12f),
        Alert(runtime, "Upload failed", "summary.png could not be sent. Try again.", UiColor.Destructive, UiColor.Surface, "alert-circle")).FillX();

    // ── Separator ───────────────────────────────────────────────────────────

    private static UiNode SeparatorPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "Horizontal separator"),
        Arrange.Space(16f),
        runtime.Text("Content above"),
        Arrange.Space(16f),
        Separator.Build(runtime),
        Arrange.Space(16f),
        runtime.Text("Content below"),
        Arrange.Space(16f),
        Caption(runtime, "Vertical separator (inline)"),
        Arrange.Space(16f),
        Arrange.Row(
            Arrange.CentredY(runtime.Text("Left")),
            Arrange.Space(16f),
            Arrange.CentredY(Separator.Build(runtime, vertical: true).Px(height: 20f)),
            Arrange.Space(16f),
            Arrange.CentredY(runtime.Text("Right"))).Px(height: 24f)).FillX();

    // ── Progress ────────────────────────────────────────────────────────────

    private static UiNode ProgressPage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        var minus = PageButton(runtime, "demo-progress-minus", "-10", primary: false, width: 60f);
        var plus = PageButton(runtime, "demo-progress-plus", "+10", primary: true, width: 60f);
        if (minus.Clicked) set(state with { ProgressValue = Math.Max(0f, state.ProgressValue - 10f) });
        if (plus.Clicked) set(state with { ProgressValue = Math.Min(100f, state.ProgressValue + 10f) });

        return Arrange.Column(
            Caption(runtime, "Task completion indicator"),
            Arrange.Space(16f),
            Progress.Build(runtime, state.ProgressValue, 100f, "Upload progress").Px(width: 400f),
            Arrange.Space(16f),
            runtime.Text($"{state.ProgressValue:0}% complete", ComponentSize.Sm, TextColor.Accent),
            Arrange.Space(16f),
            Arrange.Row(minus.Node, Arrange.Space(8f), plus.Node),
            Arrange.Space(16f),
            Progress.Build(runtime, 100f, 100f, "Completed").Px(width: 400f));
    }

    // ── Skeleton ────────────────────────────────────────────────────────────

    private static Box Bone(BibuiRuntime runtime, float width, float height, float radius)
    {
        Box bone = runtime.Box();
        bone.Px(width, height);
        bone.Style.BackgroundColor = UiColor.SurfaceRaised;
        bone.Style.CornerRadius = radius;
        return bone;
    }

    private static UiNode SkeletonPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "Loading placeholder"),
        Arrange.Space(16f),
        Bone(runtime, 400f, 16f, 4f),
        Arrange.Space(8f),
        Bone(runtime, 400f, 16f, 4f),
        Arrange.Space(8f),
        Bone(runtime, 280f, 16f, 4f),
        Arrange.Space(16f),
        Caption(runtime, "With circle avatar"),
        Arrange.Space(16f),
        Bone(runtime, 80f, 80f, 40f));

    // ── RatioBox ────────────────────────────────────────────────────────────

    // A raised box `width` wide and as tall as the ratio makes it, its label in the middle.
    private static Box AspectBox(BibuiRuntime runtime, float width, float ratio, string label)
    {
        Box box = runtime.Box();
        box.Px(width, width / ratio);
        box.Style.BackgroundColor = UiColor.SurfaceRaised;
        box.Style.BorderColor = UiColor.Border;
        box.Style.BorderWidth = 1f;
        box.Style.CornerRadius = 6f;
        CentredLabel(runtime, box, label, ComponentSize.Sm, TextColor.Secondary);
        return box;
    }

    private static UiNode RatioBoxPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "A width, and the height the ratio gives it"),
        Arrange.Space(16f),
        AspectBox(runtime, 400f, 16f / 9f, "16:9, 400 wide"),
        Arrange.Space(16f),
        AspectBox(runtime, 200f, 1f, "1:1, 200 wide"));

    // ── KeyboardShortcut ────────────────────────────────────────────────────

    private static UiNode KeyboardShortcutPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "One key cap per key, in the order they are pressed"),
        Arrange.Space(20f),
        Arrange.Row(
            KeyboardShortcut.Build(runtime, "⌘", "K"),
            Arrange.Space(12f),
            KeyboardShortcut.Build(runtime, "Ctrl", "Shift", "P"),
            Arrange.Space(12f),
            KeyboardShortcut.Build(runtime, "Escape"),
            Arrange.Space(12f),
            KeyboardShortcut.Build(runtime, "↑", "↓")));

    // ── Table and PagedTable ────────────────────────────────────────────────

    private const float TableRow = 44f;

    // A table `width` wide in a bordered box: a raised header row of bold,
    // muted column names, then a row per entry with a rule between rows. The
    // columns share the width equally.
    private static Box Table(BibuiRuntime runtime, string[] columns, IReadOnlyList<string[]> rows, float width)
    {
        Box table = runtime.Box();
        table.ChildLayoutAxis = LayoutAxis.Y;
        table.SizeX = SemanticSize.Pixels(width);
        table.SizeY = SemanticSize.ChildrenSum();
        table.Style.BorderColor = UiColor.Border;
        table.Style.BorderWidth = 1f;
        table.Style.CornerRadius = 6f;
        float column = width / columns.Length;
        using (runtime.PushParent(table))
        {
            // Squares the header's lower corners, under it: drawn first, out of the flow.
            Box square = runtime.Box();
            square.SizeX = SemanticSize.ParentPct(1f);
            square.SizeY = SemanticSize.Pixels(12f);
            square.Style.BackgroundColor = UiColor.SurfaceRaised;
            square.At(0f, TableRow - 6f);

            Box header = TableRowBox(runtime);
            header.Style.BackgroundColor = UiColor.SurfaceRaised;
            header.Style.CornerRadius = 6f;
            using (runtime.PushParent(header))
                foreach (var name in columns) Cell(runtime, name, column, TextColor.Secondary, FontWeight.Bold);
            for (int r = 0; r < rows.Count; r++)
            {
                Box row = TableRowBox(runtime);
                using (runtime.PushParent(row))
                {
                    // The rule between rows is along this one's top edge, out of the flow.
                    if (r > 0) Separator.Build(runtime).At(0f, 0f);
                    foreach (var value in rows[r]) Cell(runtime, value, column, TextColor.Primary, FontWeight.Regular);
                }
            }
        }
        return table;
    }

    private static Box TableRowBox(BibuiRuntime runtime)
    {
        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ParentPct(1f);
        row.SizeY = SemanticSize.Pixels(TableRow);
        return row;
    }

    // A cell `width` wide, its text down the middle, 12 px in from its left.
    private static void Cell(BibuiRuntime runtime, string text, float width, UiColor color, FontWeight weight)
    {
        Box cell = runtime.Box();
        cell.ChildLayoutAxis = LayoutAxis.Y;
        cell.SizeX = SemanticSize.Pixels(width);
        cell.SizeY = SemanticSize.ParentPct(1f);
        cell.Style.Padding = new EdgeInsets(0f, 12f, 0f, 12f);
        using (runtime.PushParent(cell))
        {
            Arrange.Spring(cell);
            using (runtime.PushFontFace(weight: weight))
                runtime.Text(text, ComponentSize.Sm, color);
            Arrange.Spring(cell);
        }
    }

    private static readonly string[][] TableRows =
    [
        ["Alice Johnson", "Active", "$1,200.00"],
        ["Bob Smith", "Inactive", "$480.00"],
        ["Carol White", "Active", "$3,450.00"],
        ["Dave Brown", "Pending", "$720.00"],
    ];

    private static UiNode TablePage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "Rows of values under named columns"),
        Arrange.Space(16f),
        Table(runtime, ["Name", "Status", "Amount"], TableRows, 560f));

    private static readonly string[][] People =
    [
        ["Alice Johnson",   "alice@example.com",  "Admin",  "Active"],
        ["Bob Smith",       "bob@example.com",    "Member", "Inactive"],
        ["Carol White",     "carol@example.com",  "Member", "Active"],
        ["Dave Brown",      "dave@example.com",   "Viewer", "Pending"],
        ["Eve Davis",       "eve@example.com",    "Member", "Active"],
        ["Frank Wilson",    "frank@example.com",  "Admin",  "Active"],
        ["Grace Lee",       "grace@example.com",  "Member", "Active"],
        ["Henry Martinez",  "henry@example.com",  "Viewer", "Inactive"],
        ["Iris Thompson",   "iris@example.com",   "Member", "Active"],
        ["Jack Anderson",   "jack@example.com",   "Member", "Pending"],
        ["Karen Taylor",    "karen@example.com",  "Admin",  "Active"],
        ["Liam Hernandez",  "liam@example.com",   "Viewer", "Active"],
        ["Mia Garcia",      "mia@example.com",    "Member", "Inactive"],
        ["Noah Martinez",   "noah@example.com",   "Member", "Active"],
        ["Olivia Robinson", "olivia@example.com", "Viewer", "Active"],
        ["Paul Clark",      "paul@example.com",   "Admin",  "Active"],
        ["Quinn Lewis",     "quinn@example.com",  "Member", "Pending"],
        ["Rosa Walker",     "rosa@example.com",   "Member", "Active"],
        ["Sam Hall",        "sam@example.com",    "Viewer", "Active"],
        ["Tina Allen",      "tina@example.com",   "Member", "Inactive"],
        ["Uma Young",       "uma@example.com",    "Admin",  "Active"],
        ["Victor King",     "victor@example.com", "Member", "Active"],
        ["Wendy Wright",    "wendy@example.com",  "Viewer", "Active"],
        ["Xavier Scott",    "xavier@example.com", "Member", "Pending"],
        ["Yara Torres",     "yara@example.com",   "Member", "Active"],
    ];

    private const int PageSize = 5;
    private const int PageCount = 5;

    // Five people a page, sorted by name, and a footer that reads the page
    // and pages with Previous and Next — each disabled at its end.
    private static UiNode PagedTablePage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        int page = Math.Clamp(state.PagedTablePage, 1, PageCount);
        var rows = People.OrderBy(p => p[0], StringComparer.Ordinal).Skip((page - 1) * PageSize).Take(PageSize).ToArray();
        float width = MathF.Min(660f, ShellLayout.PageWidth(runtime));   // narrower in a narrow window

        var previous = PageButton(runtime, "demo-pagedtable-prev", "Previous", primary: false, width: 72f, height: 32f,
            size: ComponentSize.Sm, radius: 4f, disabled: page <= 1);
        var next = PageButton(runtime, "demo-pagedtable-next", "Next", primary: false, width: 56f, height: 32f,
            size: ComponentSize.Sm, radius: 4f, disabled: page >= PageCount);
        if (previous.Clicked) set(state with { PagedTablePage = page - 1 });
        if (next.Clicked) set(state with { PagedTablePage = page + 1 });

        return Arrange.Column(
            Caption(runtime, "25 people sorted by name, five to a page"),
            Arrange.Space(16f),
            Table(runtime, ["Name", "Email", "Role", "Status"], rows, width),
            Arrange.Space(10f),
            Arrange.Row(
                Arrange.CentredY(runtime.Text($"Page {page} of {PageCount}", ComponentSize.Sm, TextColor.Secondary)),
                Arrange.Spring(),
                previous.Node,
                Arrange.Space(4f),
                next.Node,
                Arrange.Space(4f)).Px(width, 32f));
    }

    // ── FormField ───────────────────────────────────────────────────────────

    // A label (starred when required), a frame for the input, the field's
    // description, and its error in the destructive colour when it has one.
    private static Box FormField(BibuiRuntime runtime, string label, string description, string? error, bool required)
    {
        Box field = runtime.Box();
        field.ChildLayoutAxis = LayoutAxis.Y;
        field.SizeX = SemanticSize.ParentPct(1f);
        field.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(field))
        {
            using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(required ? label + " *" : label, ComponentSize.Sm);
            Advance(runtime, 12f * 1.4f + 4f, ComponentSize.Sm, FontWeight.Bold);
            Box frame = runtime.Box();
            frame.SizeX = SemanticSize.ParentPct(1f);
            frame.SizeY = SemanticSize.Pixels(40f);
            TextField.InputStyle(runtime, frame, focused: false, error: error is not null);
            runtime.Gap(4f);
            runtime.Text(description, ComponentSize.Sm, TextColor.Secondary);
            if (error is not null)
            {
                Advance(runtime, 12f * 1.4f + 4f, ComponentSize.Sm);
                runtime.Text(error, ComponentSize.Sm, UiColor.Destructive);
            }
        }
        return field;
    }

    private static UiNode FormFieldPage(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "A label, the input, a hint, and an error when there is one"),
        Arrange.Space(24f),
        FormField(runtime, "Display name", "Shown to the others in the workspace.", null, required: true),
        Arrange.Space(6f),
        DemoInputs.StaticInput(runtime, "demo-field-username", "Robin", 280f),
        Arrange.Space(24f),
        FormField(runtime, "Email", "Used to sign in and to reset the password.", "Another account uses this address.", required: true),
        Arrange.Space(6f),
        DemoInputs.StaticInput(runtime, "demo-field-email", "you@example.com", 280f)).FillX();

    // ── ListItem ────────────────────────────────────────────────────────────

    // A row 40 px tall across its parent: an icon and a label, on the raised
    // surface while active. A press is reported; disabled, it is swallowed.
    private static BibuiWidget ListItem(BibuiRuntime runtime, string id, string label, string icon, bool active, bool disabled = false)
    {
        Box row = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(row);
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ParentPct(1f);
        row.SizeY = SemanticSize.Pixels(40f);
        row.Style.CornerRadius = 6f;
        row.Style.Padding = new EdgeInsets(0f, 12f, 0f, 12f);
        if (active) row.Style.BackgroundColor = UiColor.SurfaceRaised;
        if (!disabled) row.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;

        UiColor ink = disabled ? TextColor.Disabled : TextColor.Primary;
        using (runtime.PushParent(row))
        {
            Box glyph = DownTheMiddle(runtime);
            using (runtime.PushParent(glyph))
            {
                Arrange.Spring(glyph);
                runtime.Icon(icon, 16f).Color = ink;
                Arrange.Spring(glyph);
            }
            runtime.Gap(8f);
            Box text = DownTheMiddle(runtime);
            using (runtime.PushParent(text))
            {
                Arrange.Spring(text);
                runtime.Text(label, ComponentSize.Md, ink);
                Arrange.Spring(text);
            }
        }
        return new BibuiWidget(row, interaction);
    }

    private static UiNode ListItemPage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        string? picked = null;
        UiNode Entry(string name, string icon)
        {
            var item = ListItem(runtime, $"demo-item-{name.ToLowerInvariant()}", name, icon, state.ActiveItem == name);
            if (item.Clicked) picked = name;
            return item.Node;
        }

        var page = Arrange.Column(
            Caption(runtime, "Rows with an icon and a label; one is selected"),
            Arrange.Space(8f),
            Entry("Dashboard", "home"),
            Arrange.Space(8f),
            Entry("Settings", "settings"),
            Arrange.Space(8f),
            Entry("Profile", "user"),
            Arrange.Space(8f),
            ListItem(runtime, "demo-item-billing", "Billing (disabled)", "file-text", active: false, disabled: true).Node,
            Arrange.Space(8f),
            runtime.TextFromId("demo-item-state", $"Active: {state.ActiveItem ?? "none"}", ComponentSize.Sm, TextColor.Secondary)).FillX();
        if (picked is not null) set(state with { ActiveItem = picked });
        return page;
    }

    // ── EmptyState ──────────────────────────────────────────────────────────

    private static UiNode EmptyStatePage(BibuiRuntime runtime)
    {
        var create = PageButton(runtime, "demo-empty-create", "New workspace", primary: true, height: 36f);
        Icon icon = runtime.Icon("folder", 48f);
        icon.Color = TextColor.Disabled;
        return Arrange.Column(
            Caption(runtime, "What a list shows before it has anything in it"),
            Arrange.Space(16f),
            Arrange.Column(
                Arrange.Spring(),
                Arrange.CentredX(icon),
                Arrange.Space(12f),
                Arrange.CentredX(Bold(runtime, "No workspaces yet", ComponentSize.Lg)),
                Arrange.Space(4f),
                Arrange.CentredX(runtime.Text("Make one to start adding files.", ComponentSize.Sm, TextColor.Secondary)),
                Arrange.Space(12f),
                Arrange.CentredX(create.Node),
                Arrange.Spring()).Px(400f, 160f));
    }

    // ── Toast ───────────────────────────────────────────────────────────────

    private const float ToastWidth = 380f;
    private const float ToastHeight = 100f;
    private const float ToastMargin = 16f;

    // A button that shows and hides the notification, what it is, and — while
    // it shows — the notification itself, floated over the page in the
    // window's lower-right corner.
    private static UiNode ToastPage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        bool visible = state.ToastVisible;

        // The notification is over the page, so it asks first.
        bool dismissed = visible && Notification(runtime, "demo-toast",
            "Workspace renamed", "It is now called fernway-design.", "Undo");

        var trigger = PageButton(runtime, "demo-toast-trigger", visible ? "Hide toast" : "Show toast", primary: true);
        if (trigger.Clicked || dismissed) set(state with { ToastVisible = !visible });

        return Arrange.Column(
            Caption(runtime, "Show it, then press Undo or the cross to close it"),
            Arrange.Space(12f),
            trigger.Node,
            Arrange.Space(12f),
            runtime.Text($"Toast: {(visible ? "visible" : "hidden")}", ComponentSize.Sm, TextColor.Secondary));
    }

    // A notification panel in a root of its own, drawn over everything: a
    // bell down the middle of its left edge, a title over a description and an
    // action, and a close mark in its upper-right corner. Whether the action
    // or the close mark was pressed.
    private static bool Notification(BibuiRuntime runtime, string id, string title, string description, string action)
    {
        // Opened in every frame it is built, so no press dismisses it.
        runtime.OpenDropDown(id);
        Box panel = runtime.BeginDropDown(id)!;
        panel.ChildLayoutAxis = LayoutAxis.X;
        panel.Px(ToastWidth, ToastHeight);
        panel.At(runtime.SurfaceWidth - ToastWidth - ToastMargin, runtime.SurfaceHeight - ToastHeight - ToastMargin);
        panel.Style.BackgroundColor = UiColor.SurfaceRaised;
        panel.Style.BorderColor = UiColor.Border;
        panel.Style.BorderWidth = 1f;
        panel.Style.CornerRadius = 8f;
        panel.Style.Padding = new EdgeInsets(12f, 12f, 12f, 12f);

        bool pressed = false;
        using (runtime.PushParent(panel))
        {
            Box close = runtime.BoxFromId(id + "-close", InteractionKinds.LeftPress);
            pressed |= runtime.InteractionsFor(close).Clicked;
            close.Px(16f, 16f);
            close.At(ToastWidth - 12f - 16f, 12f);
            close.Style.CornerRadius = 4f;
            close.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
            CentredIcon(runtime, close, "x", 14f, TextColor.Secondary);

            Box bell = DownTheMiddle(runtime);
            using (runtime.PushParent(bell))
            {
                Arrange.Spring(bell);
                runtime.Icon("bell", 18f).Color = UiColor.Border;
                Arrange.Spring(bell);
            }
            runtime.Gap(8f);

            Box text = runtime.Box();
            text.ChildLayoutAxis = LayoutAxis.Y;
            text.SizeX = text.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(text))
            {
                using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(title, ComponentSize.Md);
                Advance(runtime, 14f * 1.4f + 4f, ComponentSize.Md, FontWeight.Bold);
                runtime.Text(description, ComponentSize.Sm, TextColor.Secondary);
                Advance(runtime, 12f * 1.4f + 8f, ComponentSize.Sm);
                var undo = PageButton(runtime, id + "-action", action, primary: false, height: 26f,
                    size: ComponentSize.Sm, radius: 4f, padX: 6f);
                undo.Node.Style.BackgroundColor = null;
                pressed |= undo.Clicked;
            }
        }
        return pressed;
    }

    private static void CentredIcon(BibuiRuntime runtime, Box box, string name, float size, UiColor color)
    {
        box.ChildLayoutAxis = LayoutAxis.X;
        using (runtime.PushParent(box))
        {
            Arrange.Spring(box);
            Box column = DownTheMiddle(runtime);
            using (runtime.PushParent(column))
            {
                Arrange.Spring(column);
                runtime.Icon(name, size).Color = color;
                Arrange.Spring(column);
            }
            Arrange.Spring(box);
        }
    }
}
