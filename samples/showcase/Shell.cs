using Bibui;
using Bibui.Runtime;
using static Bibui.Arrange;
using static Showcase.Ui;
using Button = Bibui.Runtime.Button;

namespace Showcase;

// The frame around every page: the sidebar, the top bar, and the scrolling
// area the pages are built into.
internal sealed partial class App
{
    private static readonly (Page Page, string Icon, string Label, string Key)[] Nav =
    [
        (Page.Overview, "grid", "Overview", "1"),
        (Page.Targets, "telescope", "Targets", "2"),
        (Page.Telemetry, "activity", "Telemetry", "3"),
        (Page.Journal, "file-text", "Journal", "4"),
    ];

    // ── Sidebar ──────────────────────────────────────────────────────────────

    private Box Sidebar(BibuiRuntime rt, float open, float h)
    {
        // Ask first: the items and buttons inside, then nothing else claims here.
        var items = new List<Box>();
        foreach (var (page, icon, label, key) in Nav)
        {
            var item = NavItem(rt, "nav-" + label.ToLowerInvariant(), icon, label, key, Page == page, open);
            if (item.Clicked) { Page = page; SearchFocused = false; }
            items.Add((Box)item.Node);
        }
        var fold = IconButton(rt, "sidebar-fold", open > 0.5f ? "chevron-left" : "chevron-right", 30f);
        if (fold.Clicked) SidebarWanted = !SidebarWanted;
        var live = Switch.Build(rt, "live", Live, label: open > 0.6f ? "Live feed" : null, height: 20f);
        if (live.Clicked) Live = !Live;

        Box side = rt.BoxFromId("sidebar", InteractionKinds.Clip);
        side.ChildLayoutAxis = LayoutAxis.Y;
        side.Px(SidebarWidth(open), h);
        side.Style.Padding = new EdgeInsets(18f, 14f, 16f, 14f);
        side.Style.BackgroundColor = UiColor.Surface;
        side.Style.BorderRight = new EdgeBorder(rt.Theme.Borders.Width, UiColor.Border);

        float fade = Math.Clamp((open - 0.35f) / 0.65f, 0f, 1f);

        // The brand: a small orb painted with gradients, and the name beside it.
        var brand = Row(CentredY(Orb(rt, 34f)));
        if (fade > 0f)
        {
            var words = Column(Bold(rt, "Zenith", ComponentSize.Lg, UiColor.TextPrimary),
                T(rt, Data.SiteName + " observatory", FontSize.Px(11.5f), UiColor.TextSecondary));
            words.Style.Opacity = fade;
            Gap(brand, 12f);
            UiNode.PushIntoParent(brand, CentredY(words));
        }
        brand.FillX().Px(height: 44f);
        UiNode.PushIntoParent(side, brand);
        Gap(side, 26f);

        if (fade > 0f)
        {
            var eyebrow = Row(Space(12f), Eyebrow(rt, "Observe"));
            eyebrow.Style.Opacity = fade;
            UiNode.PushIntoParent(side, eyebrow);
        }
        else UiNode.PushIntoParent(side, rt.Box().Px(1f, 13f));
        Gap(side, 10f);
        foreach (var item in items)
        {
            UiNode.PushIntoParent(side, item);
            Gap(side, 4f);
        }

        Spring(side);

        if (fade > 0.6f)
        {
            var site = SiteCard(rt, live.Node);
            site.Style.Opacity = (fade - 0.6f) / 0.4f;
            UiNode.PushIntoParent(side, site);
        }
        else UiNode.PushIntoParent(side, CentredX(live.Node));
        Gap(side, 14f);
        UiNode.PushIntoParent(side, Rule(rt));
        Gap(side, 14f);

        // Who is on shift, and the fold.
        var you = Row(CentredY(Avatar.Build(rt, "IR", 32f, ink: UiColor.AccentForeground, fontSize: ComponentSize.Sm)));
        if (fade > 0f)
        {
            var who = Column(Bold(rt, "Ines Rowe", ComponentSize.Sm, UiColor.TextPrimary),
                T(rt, "Night lead", FontSize.Px(11.5f), UiColor.TextSecondary));
            who.Style.Opacity = fade;
            Gap(you, 10f);
            UiNode.PushIntoParent(you, CentredY(who));
            Spring(you);
            UiNode.PushIntoParent(you, CentredY(fold.Node));
        }
        you.FillX().Px(height: 36f);
        UiNode.PushIntoParent(side, you);
        if (fade <= 0f)
        {
            Gap(side, 10f);
            UiNode.PushIntoParent(side, CentredX(fold.Node));
        }
        return side;
    }

    private static BibuiWidget NavItem(BibuiRuntime rt, string id, string icon, string label, string key, bool selected, float open)
    {
        Box item = rt.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction i = rt.InteractionsFor(item);
        item.ChildLayoutAxis = LayoutAxis.X;
        item.FillX().Px(height: rt.Theme.Density.Of(40f));
        item.Style.Padding = EdgeInsets.Symmetric(horizontal: 13f, vertical: 0f);
        item.Style.CornerRadius = rt.Theme.Radii.Md + 2f;
        item.Style.BackgroundColor = selected
            ? UiColor.Accent.WithAlpha(0.16f + 0.06f * item.HotT)
            : UiColor.SurfaceRaisedHover.WithAlpha(item.HotT);

        UiNode.PushIntoParent(item, CentredY(Icon(rt, icon, 18f, selected ? UiColor.Accent : UiColor.TextSecondary)));
        float fade = Math.Clamp((open - 0.35f) / 0.65f, 0f, 1f);
        if (fade > 0f)
        {
            Gap(item, 12f);
            var words = Row(
                CentredY(Bold(rt, label, ComponentSize.Sm, selected ? UiColor.TextPrimary : UiColor.TextSecondary)),
                Spring(),
                CentredY(T(rt, key, FontSize.Px(11f), UiColor.TextDisabled)));
            words.FillX().FillY().Style.Opacity = fade;
            UiNode.PushIntoParent(item, words);
        }
        else if (i.Hovering)
        {
            Tooltip.Build(rt, label + "  ·  " + key, item);
        }
        return new BibuiWidget(item, i);
    }

    /// <summary>The site at a glance: dome, wind and humidity, and whether the feed is live.</summary>
    private Box SiteCard(BibuiRuntime rt, UiNode liveSwitch)
    {
        double m = Minutes;
        float wind = Data.Wind(m), hum = Data.Humidity(m);
        var card = Column(
            Row(Eyebrow(rt, "Site"), Spring(), Pill(rt, "Dome open", UiColor.Success, dot: true, height: 20f)).FillX(),
            Space(12f),
            Meter(rt, "Wind", $"{wind:0} km/h", wind / 40f, UiColor.Accent),
            Space(10f),
            Meter(rt, "Humidity", $"{hum:0} %", hum / 100f, UiColor.Accent),
            Space(14f),
            liveSwitch);
        card.FillX();
        card.Style = new Style
        {
            Padding = EdgeInsets.All(14f),
            CornerRadius = rt.Theme.Radii.Lg + 2f,
            BackgroundColor = UiColor.SurfaceRaised,
            BorderWidth = rt.Theme.Borders.Width,
            BorderColor = UiColor.Border,
        };
        return card;
    }

    private static Box Meter(BibuiRuntime rt, string label, string value, float share, UiColor color) => Column(
            Row(T(rt, label, FontSize.Px(12f), UiColor.TextSecondary), Spring(), Bold(rt, value, FontSize.Px(12f), UiColor.TextPrimary)).FillX(),
            Space(6f),
            Progress.Build(rt, Math.Clamp(share, 0f, 1f) * 100f, thickness: 4f, fill: color))
        .FillX();

    /// <summary>The brand mark: an orb lit from the upper left, in the accent.</summary>
    private static Box Orb(BibuiRuntime rt, float size)
    {
        return new Box
        {
            Draw = ctx =>
            {
                Rgba accent = ctx.Resolve(UiColor.Accent), deep = ctx.Resolve(UiColor.AccentActive);
                var r = ctx.Logical;
                float s = ctx.Scale;
                float rad = r.Width / 2f;
                ctx.DrawShadow(r.Left, r.Top, r.Width, r.Height, rad, 0f, 3f, 12f, 0f, accent.WithAlpha(110));
                var light = ctx.RadialGradient(new System.Numerics.Vector2((r.Left + r.Width * 0.32f) * s, (r.Top + r.Height * 0.28f) * s),
                    r.Width * s, [Rgba.White.WithAlpha(235), accent, deep], [0f, 0.42f, 1f]);
                ctx.DrawRoundRect(ctx.Physical(r.Left, r.Top, r.Width, r.Height), rad * s, fill: ctx.Paint(PaintSpec.Fill(Rgba.White), light));
                // A ring round it, tilted: an orbit.
                ctx.DrawArc(r.MidX, r.MidY, rad - 6f, 1.5f, Rgba.White.WithAlpha(150), 200f, 120f);
            },
        }.Px(size, size);
    }

    // ── Shared controls ──────────────────────────────────────────────────────

    /// <summary>A square button holding one icon, lit as the pointer comes over it.</summary>
    public static BibuiWidget IconButton(BibuiRuntime rt, string id, string icon, float size = 36f, bool on = false, string? tip = null)
    {
        Box b = rt.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction i = rt.InteractionsFor(b);
        b.ChildLayoutAxis = LayoutAxis.X;
        b.Px(size, size);
        b.Style.CornerRadius = rt.Theme.Radii.Md + 2f;
        b.Style.BorderWidth = rt.Theme.Borders.Width;
        b.Style.BorderColor = on ? UiColor.Accent.WithAlpha(0.6f) : UiColor.Border.WithAlpha(0.6f + 0.4f * b.HotT);
        b.Style.BackgroundColor = on
            ? UiColor.Accent.WithAlpha(0.14f + 0.1f * b.HotT)
            : UiColor.SurfaceRaisedHover.WithAlpha(b.HotT * 0.9f - 0.3f * b.ActiveT);
        Centred(b, CentredY(Icon(rt, icon, size * 0.46f, on ? UiColor.Accent : UiColor.TextPrimary)));
        if (tip is not null && i.Hovering) Tooltip.Build(rt, tip, b);
        return new BibuiWidget(b, i);
    }

    // ── Main column ──────────────────────────────────────────────────────────

    private static readonly string[] Subtitles =
    [
        "Tonight at a glance",
        "Tonight's queue, in the order the scheduler will take it",
        "The instruments, minute by minute",
        "Notes from the night, kept as Markdown",
    ];

    private Box Main(BibuiRuntime rt, float w, float h)
    {
        const float barH = 68f;
        var bar = TopBar(rt, w, barH);

        // The page scrolls inside a fixed frame; the bar beside it is the runtime's.
        Box wrap = rt.BoxFromId("page-wrap");
        wrap.ChildLayoutAxis = LayoutAxis.X;
        wrap.Px(w, h - barH);
        Box view = rt.BoxFromId("page", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, wrap);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.FillX().FillY();
        view.AllowOverflowY = true;

        const float pad = 28f;
        float cw = w - 2 * pad - Scrollbar.Lane;
        float ch = h - barH - 2 * pad;
        var page = Page switch
        {
            Page.Targets => TargetsPage(rt, cw, ch),
            Page.Telemetry => TelemetryPage(rt, cw, ch),
            Page.Journal => JournalPage(rt, cw, ch),
            _ => OverviewPage(rt, cw, ch),
        };
        page.Style.Padding = new EdgeInsets(pad - 4f, pad - Scrollbar.Lane, pad, pad);
        page.FillX();
        UiNode.PushIntoParent(view, page);

        using (rt.PushParent(wrap))
            Scrollbar.Build(rt, "page-bar", view);
        rt.InteractionsFor(view);   // after everything inside it

        return Column(bar, wrap).Px(w, h);
    }

    private Box TopBar(BibuiRuntime rt, float w, float barH)
    {
        // Ask, front to back.
        bool showSearch = w > 980f && Page is Page.Overview or Page.Targets;
        Box? field = showSearch ? SearchField(rt, 280f) : null;
        var pause = IconButton(rt, "live-toggle", Live ? "pause" : "play", tip: Live ? "Pause the feed  ·  Space" : "Resume the feed  ·  Space");
        if (pause.Clicked) Live = !Live;
        var mode = IconButton(rt, "mode", Dark ? "sun" : "moon", tip: Dark ? "Light mode  ·  L" : "Dark mode  ·  L");
        if (mode.Clicked) { Dark = !Dark; ThemeFileName = null; }
        var look = IconButton(rt, "appearance", "sliders", on: SettingsOpen, tip: "Appearance  ·  ,");
        if (look.Clicked) SettingsOpen = !SettingsOpen;

        var bar = Row(
            CentredY(Column(
                Bold(rt, Nav[(int)Page].Label, ComponentSize.Xl, UiColor.TextPrimary),
                Space(2f),
                T(rt, Subtitles[(int)Page], ComponentSize.Sm, UiColor.TextSecondary))),
            Spring());
        if (field is not null)
        {
            UiNode.PushIntoParent(bar, CentredY(field));
            Gap(bar, 16f);
        }
        UiNode.PushIntoParent(bar, CentredY(ClockChip(rt)));
        Gap(bar, 16f);
        UiNode.PushIntoParent(bar, CentredY(pause.Node));
        Gap(bar, 8f);
        UiNode.PushIntoParent(bar, CentredY(mode.Node));
        Gap(bar, 8f);
        UiNode.PushIntoParent(bar, CentredY(look.Node));
        bar.Px(w, barH);
        bar.Style = new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 28f, vertical: 0f),
            BorderBottom = new EdgeBorder(rt.Theme.Borders.Width, UiColor.Border),
            BackgroundColor = UiColor.Background,
        };
        return bar;
    }

    /// <summary>The clock: UTC and sidereal time, in a quiet chip.</summary>
    private Box ClockChip(BibuiRuntime rt)
    {
        double lst = Data.Lst(Clock);
        int lh = (int)lst, lm = (int)((lst - lh) * 60);
        var chip = Row(
            CentredY(Pill(rt, Live ? "LIVE" : "PAUSED", Live ? UiColor.Success : UiColor.TextSecondary, dot: true, height: 20f)),
            Space(10f),
            CentredY(Bold(rt, Utc, ComponentSize.Sm, UiColor.TextPrimary)),
            Space(6f),
            CentredY(T(rt, "UTC", FontSize.Px(11f), UiColor.TextSecondary)),
            Space(12f),
            CentredY(T(rt, $"LST {lh:00}:{lm:00}", ComponentSize.Sm, UiColor.TextSecondary)));
        chip.Px(height: 36f).SizeX = SemanticSize.ChildrenSum();
        chip.Style = new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 8f, vertical: 0f),
            CornerRadius = rt.Theme.Radii.Md + 2f,
            BorderWidth = rt.Theme.Borders.Width,
            BorderColor = UiColor.Border,
            BackgroundColor = UiColor.Surface,
        };
        return chip;
    }

    /// <summary>The search field: a press, or the slash key, gives it the keyboard.</summary>
    private Box SearchField(BibuiRuntime rt, float width)
    {
        Box field = rt.BoxFromId("search-box");
        TextField.InputStyle(rt, field, SearchFocused);
        field.ChildLayoutAxis = LayoutAxis.X;
        field.Px(width, 36f);
        var line = TextField.SingleLine(rt, "search", Search, SearchEdit, SearchFocused, placeholder: "Search targets");
        if (line.Pressed) { SearchFocused = true; NoteFocused = false; }
        UiNode.PushIntoParent(field, CentredY(Icon(rt, "search", 15f, UiColor.TextSecondary)));
        Gap(field, 8f);
        UiNode.PushIntoParent(field, Column(Spring(), line.Node.FillX(), Spring()).FillX().FillY());
        Gap(field, 8f);
        UiNode.PushIntoParent(field, CentredY(KeyboardShortcut.Build(rt, "/")));
        return field;
    }
}
