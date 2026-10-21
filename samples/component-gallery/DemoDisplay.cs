using Bibui.Core;
using Bibui.Core.Runtime;

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
    private static readonly Bibui.Core.TextEdit.TextEditState NoEdit = new();

    private static readonly string LongFiller = "When content is genuinely long, the box stops at six lines and scrolls. "
        + string.Join(' ', Enumerable.Repeat("Filler sentence to fill many wrapped lines.", 30));

    // A paragraph in a dark box of the given width, around its lines: as tall
    // as they are, or the given rows tall and scrolling with the wheel to the
    // lines past them. Nothing edits it.
    private static void Paragraph(BibuiRuntime runtime, string id, string text, float width, int? rows = null)
    {
        Box box = runtime.Box();
        box.SizeX = SemanticSize.Pixels(width);
        box.SizeY = SemanticSize.ChildrenSum();
        box.Style.BackgroundColor = "#18181B";
        box.Style.CornerRadius = 6f;
        box.Style.Padding = EdgeInsets.All(8);
        runtime.BeginParent(box);
        runtime.BeginSizeY(rows is { } r ? TextField.Rows(runtime, r) : SemanticSize.ChildrenSum());
        runtime.MultilineTextField(id, GalleryHelpers.Held(id, text), NoEdit, focused: false, events: 0);
        runtime.EndSizeY();
        runtime.EndParent(box);
    }

    // The editable ellipsis: what an application would hold for it.
    private static readonly BibuiStr EllipsisText = BibuiStr.FromStrLit(
        "This is a very long label that gets truncated with an ellipsis character");
    private static readonly Bibui.Core.TextEdit.TextEditState EllipsisEdit = new();
    private static bool EllipsisFocused;

    // A line in a dark box of the given width, ending in "…" when it is wider
    // than the box. A press edits it, and then it scrolls rather than ending
    // in "…"; Enter or Escape stops editing.
    private static void EllipsisField(BibuiRuntime runtime, string id, float width)
    {
        Box box = runtime.Box();
        box.SizeX = SemanticSize.Pixels(width);
        box.SizeY = SemanticSize.ChildrenSum();
        box.Style.BackgroundColor = "#18181B";
        box.Style.CornerRadius = 6f;
        box.Style.Padding = EdgeInsets.All(8);
        box.Style.BorderWidth = 1f;
        box.Style.BorderColor = EllipsisFocused ? UiColor.BorderFocus : UiColor.Border;
        runtime.BeginParent(box);
        BibuiWidget line = runtime.EllipsisTextField(id, EllipsisText, EllipsisEdit, EllipsisFocused, events: TextField.SingleLineEvents);
        runtime.EndParent(box);
        if (line.Pressed) EllipsisFocused = true;
        if (EllipsisFocused)
            foreach (var ev in runtime.UnconsumedEvents)
                if (ev is { IsKeyDown: true, Key: Bibui.Core.Key.Escape } or { Kind: Bibui.Core.TextEdit.BibUiEventKind.Submit })
                {
                    EllipsisFocused = false;
                    ev.Eat();
                }
    }

    internal static void Build(BibuiRuntime runtime, string route, DisplayState state, Action<DisplayState> set)
    {
        switch (route)
        {
            case "Text": TextPage(runtime); break;
            case "Badge": BadgePage(runtime); break;
            case "Avatar": AvatarPage(runtime); break;
            case "Card": CardPage(runtime); break;
            case "Alert": AlertPage(runtime); break;
            case "Separator": SeparatorPage(runtime); break;
            case "Texture": DemoTexture.Build(runtime); break;
            case "Label": LabelPage(runtime); break;
            case "Progress": ProgressPage(runtime, state, set); break;
            case "Skeleton": SkeletonPage(runtime); break;
            case "Spinner": SpinnerPage(runtime); break;
            case "RatioBox": RatioBoxPage(runtime); break;
            case "KeyboardShortcut": KeyboardShortcutPage(runtime); break;
            case "Table": TablePage(runtime); break;
            case "Markdown": DemoMarkdown.Page(runtime); break;
            case "PagedTable": PagedTablePage(runtime, state, set); break;
            case "FormField": FormFieldPage(runtime); break;
            case "ListItem": ListItemPage(runtime, state, set); break;
            case "EmptyState": EmptyStatePage(runtime); break;
            case "Toast": ToastPage(runtime, state, set); break;
        }
    }

    // ── Shared by the pages below ───────────────────────────────────────────
    // Each page builds in place, in order, into the column the gallery pushes
    // for it, and so does each helper: one with a box of its own pushes it.

    private static readonly UiColor Info = UiColor.Token(c => c.Info);
    private static readonly UiColor InfoBg = UiColor.Token(c => c.InfoBg);
    private static readonly UiColor Warning = UiColor.Token(c => c.Warning);
    private static readonly UiColor WarningBg = UiColor.Token(c => c.WarningBg);

    // A small bold caption above a demo.
    private static Text Caption(BibuiRuntime runtime, string text)
        => Bold(runtime, text, ComponentSize.Sm, TextColor.Secondary);

    private static float LineHeight(BibuiRuntime runtime, ComponentSize size, FontWeight weight = FontWeight.Regular)
    {
        runtime.BeginFontFace(weight: weight);
        float height = runtime.Fonts.LineHeight(runtime.FontInScope(size));
        runtime.EndFontFace();
        return height;
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
        runtime.BeginParent(box);
        runtime.BeginCenterY();
        if (width is null) runtime.Text(label, size, ink);
        else
        {
            runtime.BeginCenterX();
            runtime.Text(label, size, ink);
            runtime.EndCenterX();
        }
        runtime.EndCenterY();
        runtime.EndParent(box);
        return new BibuiWidget(box, interaction);
    }

    // A label centred both ways in a box the caller has sized.
    private static void CentredLabel(BibuiRuntime runtime, Box box, string label, ComponentSize size, UiColor color)
    {
        box.ChildLayoutAxis = LayoutAxis.X;
        runtime.BeginParent(box);
        runtime.BeginCenterX();
        runtime.BeginCenterY();
        runtime.Text(label, size, color);
        runtime.EndCenterY();
        runtime.EndCenterX();
        runtime.EndParent(box);
    }

    // ── Text ────────────────────────────────────────────────────────────────

    private static void TextPage(BibuiRuntime runtime)
    {
        Bold(runtime, "Heading (Xxl, Bold)", ComponentSize.Xxl);
        runtime.Gap(10f);
        runtime.Text("Large text (Xl)", ComponentSize.Xl);
        runtime.Gap(10f);
        runtime.Text("Body text (Md) — the default", ComponentSize.Md);
        runtime.Gap(10f);
        runtime.Text("Small text (Sm)", ComponentSize.Sm);
        runtime.Gap(10f);
        runtime.Text("Secondary color", textColor: TextColor.Secondary);
        runtime.Gap(10f);
        runtime.Text("Accent / brand color", textColor: TextColor.Accent);
        runtime.Gap(10f);
        runtime.Text("Disabled / muted", textColor: TextColor.Disabled);
        runtime.Gap(18f);
        Bold(runtime, "Ellipsis overflow: press to edit, Enter or Escape to stop", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(10f);
        EllipsisField(runtime, "demo-text-ellipsis", 220f);
        runtime.Gap(10f);
        Bold(runtime, "Two lines tall, scrolling to the rest", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(10f);
        Paragraph(runtime, "demo-text-two-rows", "This paragraph wraps to multiple lines. The box is two lines tall, and the lines past the second are below its edge: scroll the wheel over it to bring them into view.", 280f, rows: 2);
        runtime.Gap(22f);
        Bold(runtime, "Hugging its text, or scrolling past a height", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(10f);
        Paragraph(runtime, "demo-text-hug-short", "Hi!", 320f);
        runtime.Gap(10f);
        Paragraph(runtime, "demo-text-hug-medium", "A medium-length message that wraps to a few lines. Notice the box hugs the text: it is as tall as its lines.", 320f);
        runtime.Gap(10f);
        Paragraph(runtime, "demo-text-six-rows", LongFiller, 320f, rows: 6);
    }

    // ── Badge ───────────────────────────────────────────────────────────────

    private static void BadgePage(BibuiRuntime runtime)
    {
        Bold(runtime, "Status indicators", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);
        var badges = runtime.BeginParent(runtime.Row());
        Badge.Build(runtime, "Default");
        runtime.Gap(8f);
        Badge.Build(runtime, "Primary", BadgeVariant.Primary);
        runtime.Gap(8f);
        Badge.Build(runtime, "Success", BadgeVariant.Success);
        runtime.Gap(8f);
        Badge.Build(runtime, "Destructive", BadgeVariant.Destructive);
        runtime.EndParent(badges);
    }

    // ── Label ───────────────────────────────────────────────────────────────

    private static void LabelPage(BibuiRuntime runtime)
    {
        Bold(runtime, "Form labels paired with inputs", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(12f);
        DemoInputs.FieldLabel(runtime, "Email address");
        runtime.Gap(12f);
        DemoInputs.StaticInput(runtime, "demo-label-email", "you@example.com", 280f);
        runtime.Gap(12f);
        DemoInputs.FieldLabel(runtime, "Password");
        runtime.Gap(12f);
        DemoInputs.StaticInput(runtime, "demo-label-password", "••••••••", 280f);
    }

    // ── Spinner ─────────────────────────────────────────────────────────────

    private static void SpinnerPage(BibuiRuntime runtime)
    {
        Bold(runtime, "Loading spinner", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(20f);
        var sizes = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(40f)));
        runtime.BeginCenterAcross();
        StepSpinner(runtime, ComponentSize.Sm, 16f);
        runtime.EndCenterAcross();
        runtime.Gap(20f);
        runtime.BeginCenterAcross();
        StepSpinner(runtime, ComponentSize.Md, 24f);
        runtime.EndCenterAcross();
        runtime.Gap(20f);
        runtime.BeginCenterAcross();
        StepSpinner(runtime, ComponentSize.Lg, 32f);
        runtime.EndCenterAcross();
        runtime.EndParent(sizes);
        runtime.Gap(20f);
        var loading = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(40f)));
        runtime.BeginCenterAcross();
        StepSpinner(runtime, ComponentSize.Sm, 16f);
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        runtime.BeginCenterAcross();
        runtime.Text("Loading data…", ComponentSize.Sm, TextColor.Secondary);
        runtime.EndCenterAcross();
        runtime.EndParent(loading);
    }

    // A spinner at one of the size steps, `diameter` across: drawn with the
    // step's stroke, not the one a diameter of its own would give it.
    private static Spinner StepSpinner(BibuiRuntime runtime, ComponentSize size, float diameter)
    {
        var spinner = runtime.Spinner(diameter);
        (spinner.Size, spinner.Diameter) = (size, null);
        return spinner;
    }

    // ── Avatar ──────────────────────────────────────────────────────────────

    private static void AvatarPage(BibuiRuntime runtime)
    {
        Caption(runtime, "User avatars with initials fallback");
        runtime.Gap(20f);
        var avatars = runtime.BeginParent(runtime.Row());
        Avatar.Build(runtime, "JD", 56f);
        runtime.Gap(12f);
        Avatar.Build(runtime, "AB", 56f);
        runtime.Gap(12f);
        Avatar.Build(runtime, "MK", 56f);
        runtime.Gap(12f);
        Avatar.Build(runtime, "TC", 56f);
        runtime.EndParent(avatars);
    }

    // ── Card ────────────────────────────────────────────────────────────────

    // A raised, bordered box: a bold title, a muted description, and a body
    // that wraps, each spaced as the reference card spaced them.
    private static void Card(BibuiRuntime runtime, string id, string title, string description, string? body, float width)
    {
        Box card = runtime.Column().Sized(Size.Px(width));
        card.Style.BackgroundColor = UiColor.SurfaceRaised;
        card.Style.BorderColor = UiColor.Border;
        card.Style.BorderWidth = 1f;
        card.Style.CornerRadius = 8f;
        card.Style.Padding = EdgeInsets.All(16f);
        runtime.BeginParent(card);
        Bold(runtime, title, ComponentSize.Lg);
        Advance(runtime, 16f * 1.8f, ComponentSize.Lg, FontWeight.Bold);
        runtime.Text(description, ComponentSize.Md, TextColor.Secondary);
        if (body is not null)
        {
            Advance(runtime, 14f * 1.8f + 18f - 14f * 0.4f, ComponentSize.Md);
            Box lines = runtime.Box();
            lines.SizeX = SemanticSize.ParentPct(1f);
            lines.SizeY = SemanticSize.ChildrenSum();
            runtime.BeginParent(lines);
            runtime.BeginSizeY(SemanticSize.ChildrenSum());
            runtime.MultilineTextField(id, GalleryHelpers.Held(id, body), NoEdit, focused: false, events: 0);
            runtime.EndSizeY();
            runtime.EndParent(lines);
        }
        runtime.EndParent(card);
    }

    private static void CardPage(BibuiRuntime runtime)
    {
        Caption(runtime, "Content container with title and description");
        runtime.Gap(16f);
        Card(runtime, "demo-card-workspace", "New workspace", "A place for one team's files and settings.",
            "Give it a name and choose who can see it. Both can be changed later.", 420f);
        runtime.Gap(16f);
        Card(runtime, "demo-card-inbox", "Inbox", "3 messages since you last looked.", null, 420f);
    }

    // ── Alert ───────────────────────────────────────────────────────────────

    // A banner 80 px tall across its parent: an icon down the middle of the
    // left edge in the variant's colour, and a bold title over a description.
    private static void Alert(BibuiRuntime runtime, string title, string description, UiColor accent, UiColor fill, string icon)
    {
        Box alert = runtime.Row().Sized(Size.Fill, Size.Px(80f));
        alert.Style.BackgroundColor = fill;
        alert.Style.BorderColor = accent;
        alert.Style.BorderWidth = 1f;
        alert.Style.CornerRadius = 6f;
        alert.Style.Padding = new EdgeInsets(0f, 12f, 0f, 12f);
        runtime.BeginParent(alert);
        runtime.BeginCenterAcross();
        runtime.Icon(icon, 20f).Color = accent;
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        var text = runtime.BeginParent(runtime.Column());
        runtime.Gap(12f);
        Bold(runtime, title, ComponentSize.Md);
        Advance(runtime, 14f * 1.4f + 4f, ComponentSize.Md, FontWeight.Bold);
        runtime.Text(description, ComponentSize.Sm, TextColor.Secondary);
        runtime.EndParent(text);
        runtime.EndParent(alert);
    }

    private static void AlertPage(BibuiRuntime runtime)
    {
        Alert(runtime, "Sync paused", "Files will sync again when the connection is back.", UiColor.Border, UiColor.Surface, "info");
        runtime.Gap(12f);
        Alert(runtime, "New version", "Restart to finish updating.", Info, InfoBg, "info");
        runtime.Gap(12f);
        Alert(runtime, "Storage almost full", "92% of the workspace's space is in use.", Warning, WarningBg, "alert-circle");
        runtime.Gap(12f);
        Alert(runtime, "Upload failed", "summary.png could not be sent. Try again.", UiColor.Destructive, UiColor.Surface, "alert-circle");
    }

    // ── Separator ───────────────────────────────────────────────────────────

    private static void SeparatorPage(BibuiRuntime runtime)
    {
        Caption(runtime, "Horizontal separator");
        runtime.Gap(16f);
        runtime.Text("Content above");
        runtime.Gap(16f);
        Separator.Build(runtime);
        runtime.Gap(16f);
        runtime.Text("Content below");
        runtime.Gap(16f);
        Caption(runtime, "Vertical separator (inline)");
        runtime.Gap(16f);
        var inline = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(24f)));
        runtime.BeginCenterAcross();
        runtime.Text("Left");
        runtime.EndCenterAcross();
        runtime.Gap(16f);
        runtime.BeginCenterAcross();
        Separator.Build(runtime, vertical: true).Sized(y: Size.Px(20f));
        runtime.EndCenterAcross();
        runtime.Gap(16f);
        runtime.BeginCenterAcross();
        runtime.Text("Right");
        runtime.EndCenterAcross();
        runtime.EndParent(inline);
    }

    // ── Progress ────────────────────────────────────────────────────────────

    private static void ProgressPage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        Caption(runtime, "Task completion indicator");
        runtime.Gap(16f);
        Progress.Build(runtime, state.ProgressValue, 100f, "Upload progress").Sized(Size.Px(400f));
        runtime.Gap(16f);
        runtime.Text($"{state.ProgressValue:0}% complete", ComponentSize.Sm, TextColor.Accent);
        runtime.Gap(16f);
        var buttons = runtime.BeginParent(runtime.Row());
        var minus = PageButton(runtime, "demo-progress-minus", "-10", primary: false, width: 60f);
        runtime.Gap(8f);
        var plus = PageButton(runtime, "demo-progress-plus", "+10", primary: true, width: 60f);
        if (minus.Clicked) set(state with { ProgressValue = Math.Max(0f, state.ProgressValue - 10f) });
        if (plus.Clicked) set(state with { ProgressValue = Math.Min(100f, state.ProgressValue + 10f) });
        runtime.EndParent(buttons);
        runtime.Gap(16f);
        Progress.Build(runtime, 100f, 100f, "Completed").Sized(Size.Px(400f));
    }

    // ── Skeleton ────────────────────────────────────────────────────────────

    private static void Bone(BibuiRuntime runtime, float width, float height, float radius)
    {
        Box bone = runtime.Box().Sized(Size.Px(width), Size.Px(height));
        bone.Style.BackgroundColor = UiColor.SurfaceRaised;
        bone.Style.CornerRadius = radius;
    }

    private static void SkeletonPage(BibuiRuntime runtime)
    {
        Caption(runtime, "Loading placeholder");
        runtime.Gap(16f);
        Bone(runtime, 400f, 16f, 4f);
        runtime.Gap(8f);
        Bone(runtime, 400f, 16f, 4f);
        runtime.Gap(8f);
        Bone(runtime, 280f, 16f, 4f);
        runtime.Gap(16f);
        Caption(runtime, "With circle avatar");
        runtime.Gap(16f);
        Bone(runtime, 80f, 80f, 40f);
    }

    // ── RatioBox ────────────────────────────────────────────────────────────

    // A raised box `width` wide and as tall as the ratio makes it, its label in the middle.
    private static void AspectBox(BibuiRuntime runtime, float width, float ratio, string label)
    {
        Box box = runtime.Box().Sized(Size.Px(width), Size.Px(width / ratio));
        box.Style.BackgroundColor = UiColor.SurfaceRaised;
        box.Style.BorderColor = UiColor.Border;
        box.Style.BorderWidth = 1f;
        box.Style.CornerRadius = 6f;
        CentredLabel(runtime, box, label, ComponentSize.Sm, TextColor.Secondary);
    }

    private static void RatioBoxPage(BibuiRuntime runtime)
    {
        Caption(runtime, "A width, and the height the ratio gives it");
        runtime.Gap(16f);
        AspectBox(runtime, 400f, 16f / 9f, "16:9, 400 wide");
        runtime.Gap(16f);
        AspectBox(runtime, 200f, 1f, "1:1, 200 wide");
    }

    // ── KeyboardShortcut ────────────────────────────────────────────────────

    private static void KeyboardShortcutPage(BibuiRuntime runtime)
    {
        Caption(runtime, "One key cap per key, in the order they are pressed");
        runtime.Gap(20f);
        var shortcuts = runtime.BeginParent(runtime.Row());
        KeyboardShortcut.Build(runtime, "⌘", "K");
        runtime.Gap(12f);
        KeyboardShortcut.Build(runtime, "Ctrl", "Shift", "P");
        runtime.Gap(12f);
        KeyboardShortcut.Build(runtime, "Escape");
        runtime.Gap(12f);
        KeyboardShortcut.Build(runtime, "↑", "↓");
        runtime.EndParent(shortcuts);
    }

    // ── Table and PagedTable ────────────────────────────────────────────────

    private const float TableRow = 44f;

    // A table `width` wide in a bordered box: a raised header row of bold,
    // muted column names, then a row per entry with a rule between rows. The
    // columns share the width equally.
    private static void Table(BibuiRuntime runtime, string[] columns, IReadOnlyList<string[]> rows, float width)
    {
        Box table = runtime.Column().Sized(Size.Px(width));
        table.Style.BorderColor = UiColor.Border;
        table.Style.BorderWidth = 1f;
        table.Style.CornerRadius = 6f;
        float column = width / columns.Length;
        runtime.BeginParent(table);
        // Squares the header's lower corners, under it: drawn first, out of the flow.
        Box square = runtime.Box().Sized(Size.Fill, Size.Px(12f));
        square.Style.BackgroundColor = UiColor.SurfaceRaised;
        square.At(0f, TableRow - 6f);

        Box header = runtime.Row().Sized(Size.Fill, Size.Px(TableRow));
        header.Style.BackgroundColor = UiColor.SurfaceRaised;
        header.Style.CornerRadius = 6f;
        runtime.BeginParent(header);
        foreach (var name in columns) Cell(runtime, name, column, TextColor.Secondary, FontWeight.Bold);
        runtime.EndParent(header);
        for (int r = 0; r < rows.Count; r++)
        {
            var row = runtime.BeginParent(runtime.Row().Sized(Size.Fill, Size.Px(TableRow)));
            // The rule between rows is along this one's top edge, out of the flow.
            if (r > 0) Separator.Build(runtime).At(0f, 0f);
            foreach (var value in rows[r]) Cell(runtime, value, column, TextColor.Primary, FontWeight.Regular);
            runtime.EndParent(row);
        }
        runtime.EndParent(table);
    }

    // A cell `width` wide, its text down the middle, 12 px in from its left.
    private static void Cell(BibuiRuntime runtime, string text, float width, UiColor color, FontWeight weight)
    {
        Box cell = runtime.Box();
        cell.ChildLayoutAxis = LayoutAxis.Y;
        cell.SizeX = SemanticSize.Pixels(width);
        cell.SizeY = SemanticSize.ParentPct(1f);
        cell.Style.Padding = new EdgeInsets(0f, 12f, 0f, 12f);
        runtime.BeginParent(cell);
        runtime.BeginCenter();
        runtime.BeginFontFace(weight: weight);
        runtime.Text(text, ComponentSize.Sm, color);
        runtime.EndFontFace();
        runtime.EndCenter();
        runtime.EndParent(cell);
    }

    private static readonly string[][] TableRows =
    [
        ["Alice Johnson", "Active", "$1,200.00"],
        ["Bob Smith", "Inactive", "$480.00"],
        ["Carol White", "Active", "$3,450.00"],
        ["Dave Brown", "Pending", "$720.00"],
    ];

    private static void TablePage(BibuiRuntime runtime)
    {
        Caption(runtime, "Rows of values under named columns");
        runtime.Gap(16f);
        Table(runtime, ["Name", "Status", "Amount"], TableRows, 560f);
    }

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
    private static void PagedTablePage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        int page = Math.Clamp(state.PagedTablePage, 1, PageCount);
        var rows = People.OrderBy(p => p[0], StringComparer.Ordinal).Skip((page - 1) * PageSize).Take(PageSize).ToArray();
        float width = MathF.Min(660f, ShellLayout.PageWidth(runtime));   // narrower in a narrow window

        Caption(runtime, "25 people sorted by name, five to a page");
        runtime.Gap(16f);
        Table(runtime, ["Name", "Email", "Role", "Status"], rows, width);
        runtime.Gap(10f);
        var footer = runtime.BeginParent(runtime.Row().Sized(Size.Px(width), Size.Px(32f)));
        runtime.BeginCenterAcross();
        runtime.Text($"Page {page} of {PageCount}", ComponentSize.Sm, TextColor.Secondary);
        runtime.EndCenterAcross();
        runtime.Spacer(Size.Fill);
        var previous = PageButton(runtime, "demo-pagedtable-prev", "Previous", primary: false, width: 72f, height: 32f,
            size: ComponentSize.Sm, radius: 4f, disabled: page <= 1);
        runtime.Gap(4f);
        var next = PageButton(runtime, "demo-pagedtable-next", "Next", primary: false, width: 56f, height: 32f,
            size: ComponentSize.Sm, radius: 4f, disabled: page >= PageCount);
        runtime.Gap(4f);
        if (previous.Clicked) set(state with { PagedTablePage = page - 1 });
        if (next.Clicked) set(state with { PagedTablePage = page + 1 });
        runtime.EndParent(footer);
    }

    // ── FormField ───────────────────────────────────────────────────────────

    // A label (starred when required), a frame for the input, the field's
    // description, and its error in the destructive colour when it has one.
    private static void FormField(BibuiRuntime runtime, string label, string description, string? error, bool required)
    {
        var field = runtime.BeginParent(runtime.Column().Sized(Size.Fill));
        Bold(runtime, required ? label + " *" : label, ComponentSize.Sm);
        Advance(runtime, 12f * 1.4f + 4f, ComponentSize.Sm, FontWeight.Bold);
        Box frame = runtime.Box().Sized(Size.Fill, Size.Px(40f));
        TextField.InputStyle(runtime, frame, focused: false, error: error is not null);
        runtime.Gap(4f);
        runtime.Text(description, ComponentSize.Sm, TextColor.Secondary);
        if (error is not null)
        {
            Advance(runtime, 12f * 1.4f + 4f, ComponentSize.Sm);
            runtime.Text(error, ComponentSize.Sm, UiColor.Destructive);
        }
        runtime.EndParent(field);
    }

    private static void FormFieldPage(BibuiRuntime runtime)
    {
        Caption(runtime, "A label, the input, a hint, and an error when there is one");
        runtime.Gap(24f);
        FormField(runtime, "Display name", "Shown to the others in the workspace.", null, required: true);
        runtime.Gap(6f);
        DemoInputs.StaticInput(runtime, "demo-field-username", "Robin", 280f);
        runtime.Gap(24f);
        FormField(runtime, "Email", "Used to sign in and to reset the password.", "Another account uses this address.", required: true);
        runtime.Gap(6f);
        DemoInputs.StaticInput(runtime, "demo-field-email", "you@example.com", 280f);
    }

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
        runtime.BeginParent(row);
        runtime.BeginCenterAcross();
        runtime.Icon(icon, 16f).Color = ink;
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        runtime.BeginCenterAcross();
        runtime.Text(label, ComponentSize.Md, ink);
        runtime.EndCenterAcross();
        runtime.EndParent(row);
        return new BibuiWidget(row, interaction);
    }

    private static void ListItemPage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        string? picked = null;
        void Entry(string name, string icon)
        {
            if (ListItem(runtime, $"demo-item-{name.ToLowerInvariant()}", name, icon, state.ActiveItem == name).Clicked)
                picked = name;
        }

        Caption(runtime, "Rows with an icon and a label; one is selected");
        runtime.Gap(8f);
        Entry("Dashboard", "home");
        runtime.Gap(8f);
        Entry("Settings", "settings");
        runtime.Gap(8f);
        Entry("Profile", "user");
        runtime.Gap(8f);
        ListItem(runtime, "demo-item-billing", "Billing (disabled)", "file-text", active: false, disabled: true);
        runtime.Gap(8f);
        runtime.TextFromId("demo-item-state", $"Active: {state.ActiveItem ?? "none"}", ComponentSize.Sm, TextColor.Secondary);
        if (picked is not null) set(state with { ActiveItem = picked });
    }

    // ── EmptyState ──────────────────────────────────────────────────────────

    private static void EmptyStatePage(BibuiRuntime runtime)
    {
        Caption(runtime, "What a list shows before it has anything in it");
        runtime.Gap(16f);
        var empty = runtime.BeginParent(runtime.Column().Sized(Size.Px(400f), Size.Px(160f)));
        runtime.Spacer(Size.Fill);
        runtime.BeginCenterAcross();
        runtime.Icon("folder", 48f).Color = TextColor.Disabled;
        runtime.EndCenterAcross();
        runtime.Gap(12f);
        runtime.BeginCenterAcross();
        Bold(runtime, "No workspaces yet", ComponentSize.Lg);
        runtime.EndCenterAcross();
        runtime.Gap(4f);
        runtime.BeginCenterAcross();
        runtime.Text("Make one to start adding files.", ComponentSize.Sm, TextColor.Secondary);
        runtime.EndCenterAcross();
        runtime.Gap(12f);
        runtime.BeginCenterAcross();
        PageButton(runtime, "demo-empty-create", "New workspace", primary: true, height: 36f);
        runtime.EndCenterAcross();
        runtime.Spacer(Size.Fill);
        runtime.EndParent(empty);
    }

    // ── Toast ───────────────────────────────────────────────────────────────

    private const float ToastWidth = 380f;
    private const float ToastHeight = 100f;
    private const float ToastMargin = 16f;

    // A button that shows and hides the notification, what it is, and — while
    // it shows — the notification itself, floated over the page in the
    // window's lower-right corner.
    private static void ToastPage(BibuiRuntime runtime, DisplayState state, Action<DisplayState> set)
    {
        bool visible = state.ToastVisible;

        // The notification is over the page, so it asks first.
        bool dismissed = visible && Notification(runtime, "demo-toast",
            "Workspace renamed", "It is now called fernway-design.", "Undo");

        Caption(runtime, "Show it, then press Undo or the cross to close it");
        runtime.Gap(12f);
        var trigger = PageButton(runtime, "demo-toast-trigger", visible ? "Hide toast" : "Show toast", primary: true);
        if (trigger.Clicked || dismissed) set(state with { ToastVisible = !visible });
        runtime.Gap(12f);
        runtime.Text($"Toast: {(visible ? "visible" : "hidden")}", ComponentSize.Sm, TextColor.Secondary);
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
        panel.Sized(Size.Px(ToastWidth), Size.Px(ToastHeight));
        panel.At(runtime.SurfaceWidth - ToastWidth - ToastMargin, runtime.SurfaceHeight - ToastHeight - ToastMargin);
        panel.Style.BackgroundColor = UiColor.SurfaceRaised;
        panel.Style.BorderColor = UiColor.Border;
        panel.Style.BorderWidth = 1f;
        panel.Style.CornerRadius = 8f;
        panel.Style.Padding = new EdgeInsets(12f, 12f, 12f, 12f);

        bool pressed = false;
        runtime.BeginParent(panel);
        Box close = runtime.BoxFromId(id + "-close", InteractionKinds.LeftPress);
        pressed |= runtime.InteractionsFor(close).Clicked;
        close.Sized(Size.Px(16f), Size.Px(16f));
        close.At(ToastWidth - 12f - 16f, 12f);
        close.Style.CornerRadius = 4f;
        close.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
        CentredIcon(runtime, close, "x", 14f, TextColor.Secondary);

        runtime.BeginCenterAcross();
        runtime.Icon("bell", 18f).Color = UiColor.Border;
        runtime.EndCenterAcross();
        runtime.Gap(8f);

        var text = runtime.BeginParent(runtime.Column());
        Bold(runtime, title, ComponentSize.Md);
        Advance(runtime, 14f * 1.4f + 4f, ComponentSize.Md, FontWeight.Bold);
        runtime.Text(description, ComponentSize.Sm, TextColor.Secondary);
        Advance(runtime, 12f * 1.4f + 8f, ComponentSize.Sm);
        var undo = PageButton(runtime, id + "-action", action, primary: false, height: 26f,
            size: ComponentSize.Sm, radius: 4f, padX: 6f);
        undo.Node.Style.BackgroundColor = null;
        pressed |= undo.Clicked;
        runtime.EndParent(text);
        runtime.EndParent(panel);
        return pressed;
    }

    private static void CentredIcon(BibuiRuntime runtime, Box box, string name, float size, UiColor color)
    {
        box.ChildLayoutAxis = LayoutAxis.X;
        runtime.BeginParent(box);
        runtime.BeginCenterX();
        runtime.BeginCenterY();
        runtime.Icon(name, size).Color = color;
        runtime.EndCenterY();
        runtime.EndCenterX();
        runtime.EndParent(box);
    }
}
