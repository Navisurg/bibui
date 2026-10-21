using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;
using static Showcase.Ui;

namespace Showcase;

// The Appearance sheet, the context menu, and the pages that are not the overview.
internal sealed partial class App
{
    private static readonly ToggleOption[] ModeOptions = [new("Dark", "dark"), new("Light", "light")];

    private static readonly RadioOption[] DensityOptions =
        [new("Compact", "compact"), new("Default", "default"), new("Comfortable", "comfortable")];


    // ── Appearance ───────────────────────────────────────────────────────────

    private void BuildSettings(BibuiRuntime rt)
    {
        // Asked every frame, open or not, so closing glides as opening does.
        float shown = rt.Animate("settings", "shown", SettingsOpen ? 1f : 0f, rt.Rates.Toggle);
        if (shown <= 0.002f) return;

        const float width = 392f;
        float inner = width - 48f - Scrollbar.Lane - 4f;
        // A control asks while it is built, so it is built with the dialog's
        // panel (or something under it) as the parent in scope: that is how
        // the dialog knows the press is its own. Where it is pushed afterwards,
        // by PushIntoParent or CentredY, is free, so long as it stays inside.
        var sheet = Dialog.Begin(rt, "settings", shown, DialogSide.Right, width);

        // The sheet scrolls when the window is shorter than what is in it.
        var body = rt.Row().Sized(Size.Fill, Size.Fill);
        Box view = rt.BoxFromId("settings-view", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, body);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.Sized(Size.Fill, Size.Fill);
        view.AllowOverflowY = true;
        rt.BeginParent(view);

        var close = IconButton(rt, "settings-close", "x", 30f);
        var header = rt.BeginParent(rt.Row().Sized(Size.Px(inner), Size.Px(44f)));
        rt.BeginCenterAcross();
        Bold(rt, "Appearance", ComponentSize.Lg, UiColor.TextPrimary);
        rt.Gap(2f);
        T(rt, "One seed colour, the whole theme, live", ComponentSize.Sm, UiColor.TextSecondary);
        rt.EndCenterAcross();
        rt.Spacer(Size.Fill);
        CentredY(rt, close.Node);
        rt.EndParent(header);
        rt.Gap(22f);

        // Mode
        Section(rt, "Mode");
        var mode = rt.ToggleGroup("settings-mode", ModeOptions, Dark ? "dark" : "light");
        if (mode.Picked is { } m && (m == "dark") != Dark) { Dark = m == "dark"; ThemeFileName = null; }
        rt.Gap(22f);

        // Accent swatches: the seed every colour of the theme is generated from.
        Section(rt, "Accent");
        var swatches = rt.BeginParent(rt.Row().Sized(y: Size.Px(34f)));
        foreach (var (hex, name) in Accents)
        {
            if (swatches.FirstChild is not null) rt.Gap(4f);
            var sw = Swatch(rt, "swatch-" + name.ToLowerInvariant(), hex, Accent == hex && ThemeFileName is null, name);
            if (sw.Clicked) { Accent = hex; ThemeFileName = null; }
        }
        rt.EndParent(swatches);
        rt.Gap(10f);
        rt.Text($"Theme.FromSeed(\"{Accent}\", mode: {(Dark ? "Dark" : "Light")})", FontSize.Px(12f), UiColor.TextSecondary, FontSlot.Mono);
        rt.Gap(22f);

        // Theme files, or the generated theme.
        Section(rt, "Theme");
        string current = ThemeFileName is null ? "Generated from accent" : Title(ThemeFileName) + "  ·  themes/" + ThemeFileName + ".json";
        var select = Select.Begin(rt, "theme-file", current, width: inner);
        if (select.Open)
        {
            if (rt.SelectItem("theme-generated", "Generated from accent", selected: ThemeFileName is null).Clicked)
            { ThemeFileName = null; select.Close(); }
            foreach (var file in ThemeFiles)
                if (rt.SelectItem("theme-" + file, Title(file) + "  ·  themes/" + file + ".json", selected: ThemeFileName == file).Clicked)
                { ThemeFileName = file; select.Close(); }
        }
        select.End();
        rt.Gap(22f);

        // Shape and rhythm
        Section(rt, "Corner radius");
        var radiusColumn = rt.BeginParent(rt.Column().Sized(Size.Px(inner)));
        var radius = rt.Slider("settings-radius", Radius, 0f, 16f, step: 1f);
        if (MathF.Abs(radius.Value - Radius) > 0.01f) { Radius = radius.Value; ThemeFileName = null; }
        rt.EndParent(radiusColumn);
        rt.Gap(6f);
        rt.Text($"{Radius:0} px  ·  Sm {Radius * 2f / 3f:0.#}, Lg {Radius * 4f / 3f:0.#}", FontSize.Px(12f), UiColor.TextSecondary);
        rt.Gap(20f);
        Section(rt, "Density");
        var density = rt.RadioGroup("settings-density", DensityOptions, Density switch
        {
            Bibui.Core.Theming.Density.Compact => "compact",
            Bibui.Core.Theming.Density.Comfortable => "comfortable",
            _ => "default",
        });
        if (density.Picked is { } d)
        {
            Density = d switch { "compact" => Bibui.Core.Theming.Density.Compact, "comfortable" => Bibui.Core.Theming.Density.Comfortable, _ => Bibui.Core.Theming.Density.Default };
            ThemeFileName = null;
        }
        rt.Gap(22f);

        // Motion and the sky
        Section(rt, "Behaviour");
        var reduce = rt.Switch("settings-reduce", ReducedMotion, label: "Reduce motion");
        if (reduce.Clicked) ReducedMotion = !ReducedMotion;
        rt.Gap(12f);
        var grid = rt.Checkbox("settings-grid", Grid, label: "Alt-az grid on the sky");
        if (grid.Clicked) Grid = !Grid;
        rt.Gap(10f);
        var labels = rt.Checkbox("settings-labels", Labels, label: "Name the targets");
        if (labels.Clicked) Labels = !Labels;
        rt.Gap(24f);

        Section(rt, "Keyboard");
        foreach (var (keys, what) in Shortcuts)
        {
            var shortcut = rt.BeginParent(rt.Row().Sized(Size.Px(inner), Size.Px(30f)));
            CentredY(rt, T(rt, what, ComponentSize.Sm, UiColor.TextSecondary));
            rt.Spacer(Size.Fill);
            CentredY(rt, KeyboardShortcut.Build(rt, keys));
            rt.EndParent(shortcut);
        }

        rt.Gap(8f);
        rt.EndParent(view);
        rt.BeginParent(body);
        rt.Scrollbar("settings-bar", view);
        rt.EndParent(body);
        rt.InteractionsFor(view);

        if (sheet.End().Dismissed | close.Clicked) SettingsOpen = false;
    }

    private static readonly (string[] Keys, string What)[] Shortcuts =
    [
        (["/"], "Search targets"),
        (["↑", "↓"], "Move through the queue"),
        (["Enter"], "Slew to the selected target"),
        (["Space"], "Pause or resume the feed"),
        (["L"], "Light or dark"),
        (["["], "Fold the sidebar"),
        (["1", "–", "4"], "Pages"),
    ];

    private static void Section(BibuiRuntime rt, string label)
    {
        Eyebrow(rt, label);
        rt.Gap(10f);
    }

    private static string Title(string file) => string.Concat(char.ToUpperInvariant(file[0]).ToString(), file[1..]);

    /// <summary>A round colour swatch: it grows a ring as the pointer comes over it, and wears a check when chosen.</summary>
    private static BibuiWidget Swatch(BibuiRuntime rt, string id, string hex, bool chosen, string name)
    {
        Box b = rt.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction i = rt.InteractionsFor(b);
        b.Sized(Size.Px(34f), Size.Px(34f));
        b.Style.CornerRadius = 17f;
        b.Style.BorderWidth = chosen ? 2f : 1.5f * b.HotT;
        b.Style.BorderColor = chosen ? UiColor.TextPrimary : UiColor.BorderHover;
        b.Style.Padding = EdgeInsets.All(3.5f);
        rt.BeginParent(b);
        var dot = rt.Row().Sized(Size.Fill, Size.Fill);
        dot.Style.CornerRadius = 13.5f;
        dot.Style.BackgroundColor = hex;
        dot.Style.Shadow = BoxShadow.Glow(hex, blur: 4f + 10f * b.HotT, spread: 0f);
        if (chosen)
        {
            rt.BeginParent(dot);
            rt.BeginCenterX();
            rt.BeginCenterY();
            Icon(rt, "check", 15f, "#0B0D17");
            rt.EndCenterY();
            rt.EndCenterX();
            rt.EndParent(dot);
        }
        rt.EndParent(b);
        if (i.Hovering) Tooltip.Build(rt, name + "  " + hex, b);
        return new BibuiWidget(b, i);
    }

    // ── Context menu on a queue row ─────────────────────────────────────────

    private void BuildContextMenu(BibuiRuntime rt)
    {
        if (Array.Find(Data.Targets, x => x.Id == MenuFor) is not { } t) return;
        if (Menu.Begin(rt, "row-menu", MenuX, MenuY, 232f) is not { } menu) return;
        Menu.Label(rt, t.Name);
        bool close = false;
        if (rt.MenuItem("menu-slew", "Slew to target", "send", "Enter", disabled: Pointing == t.Id).Clicked) { Selected = t.Id; Slew(); close = true; }
        if (rt.MenuItem("menu-log", "Open observing log", "file-text", "4").Clicked) { Selected = t.Id; Page = Page.Journal; close = true; }
        if (rt.MenuItem("menu-copy", "Copy coordinates", "copy", null).Clicked)
        {
            rt.ClipboardSet($"{t.Code}  RA {t.RaHours:0.000}h  Dec {t.DecDeg:+0.00;-0.00}°");
            close = true;
        }
        Menu.Separator(rt);
        if (rt.MenuItem("menu-hold", "Hold for weather", "pause", null).Clicked) close = true;
        if (rt.MenuItem("menu-drop", "Drop from tonight", "trash-2", null, disabled: true).Clicked) close = true;
        menu.End();
        if (close) rt.CloseDropDown("row-menu");
    }

    // ── Targets ──────────────────────────────────────────────────────────────

    private void TargetsPage(BibuiRuntime rt, float cw, float ch)
    {
        float h = MathF.Max(ch, 520f);
        if (cw < 900f)
        {
            QueueCard(rt, cw, 480f);
            rt.Gap(Gutter);
            SkyCard(rt, cw, StackedSkyHeight(cw));
            rt.Gap(Gutter);
            LogCard(rt, cw, 460f);
            return;
        }
        float qW = MathF.Round((cw - Gutter) * 0.62f), sideW = cw - Gutter - qW;
        float skyH = MathF.Round(h * 0.52f);
        var cards = rt.BeginParent(rt.Row());
        QueueCard(rt, qW, h);
        rt.Gap(Gutter);
        var side = rt.BeginParent(rt.Column());
        SkyCard(rt, sideW, skyH, glass: false);
        rt.Gap(Gutter);
        LogCard(rt, sideW, h - skyH - Gutter);
        rt.EndParent(side);
        rt.EndParent(cards);
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    private void TelemetryPage(BibuiRuntime rt, float cw, float ch)
    {
        bool wide = cw >= 940f;
        float kpiH = 132f;
        float chartsH = MathF.Max(ch - kpiH - Gutter, 560f);
        KpiRow(rt, cw, kpiH, wide ? 4 : 2);
        rt.Gap(Gutter);
        if (wide)
        {
            float leftW = MathF.Round((cw - Gutter) * 0.62f), rightW = cw - Gutter - leftW;
            float half = (chartsH - Gutter) / 2f;
            var cards = rt.BeginParent(rt.Row());
            TelemetryCard(rt, "tel-seeing", leftW, chartsH, Metric.Seeing, false);
            rt.Gap(Gutter);
            var side = rt.BeginParent(rt.Column());
            TelemetryCard(rt, "tel-temp", rightW, half, Metric.Temperature, false);
            rt.Gap(Gutter);
            TelemetryCard(rt, "tel-data", rightW, half, Metric.Throughput, false);
            rt.EndParent(side);
            rt.EndParent(cards);
        }
        else
        {
            foreach (var (id, metric) in new[] { ("tel-seeing", Metric.Seeing), ("tel-temp", Metric.Temperature), ("tel-data", Metric.Throughput) })
            {
                TelemetryCard(rt, id, cw, 320f, metric, false);
                rt.Gap(Gutter);
            }
        }
    }

    // ── Journal ──────────────────────────────────────────────────────────────

    private static readonly RadioOption[] Ratings =
        [new("Excellent", "5"), new("Good", "4"), new("Fair", "3"), new("Poor", "2")];

    public string Rating = "4";
    public bool[] Checklist = [true, true, false, false];
    private static readonly string[] ChecklistItems =
        ["Dome flushed before dusk", "Focus run on the first target", "Flats at dawn", "Back up tonight's data"];
    public bool NightOpen = true;
    public readonly BibuiStr Note = BibuiStr.FromStrLit("Lantern's outer shell needs another 40 min of OIII. Kestrel is next; start it before 03:10.");
    public readonly TextEditState NoteEdit = new();
    public bool NoteFocused;

    private void JournalPage(BibuiRuntime rt, float cw, float ch)
    {
        float h = MathF.Max(ch, 560f);
        bool wide = cw >= 900f;
        float sideW = wide ? 320f : cw;
        float logW = wide ? cw - Gutter - sideW : cw;

        // The night card: a checklist, a rating, and a collapsible summary.
        float inner = sideW - 36f;
        var items = rt.BeginParent(rt.Column());
        for (int k = 0; k < ChecklistItems.Length; k++)
        {
            if (k > 0) rt.Gap(12f);
            var cb = rt.Checkbox("check-" + k, Checklist[k], label: ChecklistItems[k]);
            if (cb.Clicked) Checklist[k] = !Checklist[k];
        }
        rt.EndParent(items);
        var rating = rt.RadioGroup("rating", Ratings, Rating);
        if (rating.Picked is { } r) Rating = r;
        var summary = rt.Expander("night-summary", "Night summary", NightOpen);
        if (summary.Toggled) NightOpen = !NightOpen;
        if (summary.Body is { } body)
        {
            rt.BeginParent(body);
            foreach (var line in new[]
            {
                $"{Data.Targets.Count(t => t.DoneMin > 0)} targets exposed, {Data.Targets.Sum(t => t.DoneMin) / 60.0:0.0} h of data",
                $"Seeing {Data.Seeing(Minutes):0.00}″ now, best 0.71″ at 01:52",
                $"{SlewCount} slews from this console",
            })
            {
                rt.Text("·  " + line, ComponentSize.Sm, UiColor.TextSecondary);
                rt.Gap(6f);
            }
            rt.EndParent(body);
        }
        // A hand-over note: a multi-line field, four rows tall, that a press gives the keyboard.
        var noteBox = rt.Column().Sized(Size.Px(inner));
        TextField.InputStyle(rt, noteBox, NoteFocused);
        rt.BeginParent(noteBox);
        rt.BeginFontSize(ComponentSize.Sm);
        rt.BeginSizeY(TextField.Rows(rt, 4));
        var note = rt.MultilineTextField("note", Note, NoteEdit, NoteFocused, placeholder: "Hand-over note for the morning crew");
        rt.EndSizeY();
        rt.EndFontSize();
        rt.EndParent(noteBox);
        if (note.Pressed) { NoteFocused = true; SearchFocused = false; }

        var night = rt.BeginParent(rt.Column().Sized(Size.Px(sideW), Size.Px(wide ? h : 520f)));
        night.Style = CardStyle(rt);
        CardHeader(rt, "check-square", "Tonight", $"{Checklist.Count(c => c)} of {Checklist.Length} done");
        rt.Gap(14f);
        Place(rt, items);
        rt.Gap(22f);
        Eyebrow(rt, "Conditions");
        rt.Gap(10f);
        Place(rt, rating.Node);
        rt.Gap(20f);
        Eyebrow(rt, "Hand-over");
        rt.Gap(10f);
        Place(rt, noteBox);
        rt.Gap(18f);
        Separator.Build(rt);
        rt.Gap(6f);
        Place(rt, summary.Node);
        rt.EndParent(night);

        // Wide, the two cards side by side; narrow, stacked in the page's column.
        Box? cards = wide ? rt.BeginParent(rt.Row()) : null;
        LogCard(rt, logW, wide ? h : 640f, ComponentSize.Md, night: true);
        rt.Gap(Gutter);
        Place(rt, night);
        if (cards is not null) rt.EndParent(cards);
    }
}
