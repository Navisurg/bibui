using Bibui.Core;
using Bibui.Core.Runtime;
using static Showcase.Ui;

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

    private void Sidebar(BibuiRuntime rt, float open, float h)
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
        var live = rt.Switch("live", Live, label: open > 0.6f ? "Live feed" : null, height: 20f);
        if (live.Clicked) Live = !Live;

        Box side = rt.BoxFromId("sidebar", InteractionKinds.Clip);
        side.ChildLayoutAxis = LayoutAxis.Y;
        side.Sized(Size.Px(SidebarWidth(open)), Size.Px(h));
        side.Style.Padding = new EdgeInsets(18f, 14f, 16f, 14f);
        side.Style.BackgroundColor = UiColor.Surface;
        side.Style.BorderRight = new EdgeBorder(rt.Theme.Borders.Width, UiColor.Border);

        float fade = Math.Clamp((open - 0.35f) / 0.65f, 0f, 1f);

        // The asked nodes above are linked in here, in their places.
        rt.BeginParent(side);
        // The brand: a small orb painted with gradients, and the name beside it.
        var brand = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(44f)));
        CentredY(rt, Orb(rt, 34f));
        if (fade > 0f)
        {
            rt.Gap(12f);
            rt.BeginCenterAcross();
            var name = rt.BeginParent(rt.Column());
            name.Style = new Style { Opacity = fade };
            Bold(rt, "Zenith", ComponentSize.Lg, UiColor.TextPrimary);
            T(rt, Data.SiteName + " observatory", FontSize.Px(11.5f), UiColor.TextSecondary);
            rt.EndParent(name);
            rt.EndCenterAcross();
        }
        rt.EndParent(brand);
        rt.Gap(26f);

        if (fade > 0f)
        {
            var heading = rt.BeginParent(rt.Row());
            heading.Style = new Style { Opacity = fade };
            rt.Gap(12f);
            Eyebrow(rt, "Observe");
            rt.EndParent(heading);
        }
        else rt.Box().Sized(Size.Px(1f), Size.Px(13f));
        rt.Gap(10f);
        foreach (var item in items)
        {
            Place(rt, item);
            rt.Gap(4f);
        }

        rt.Spacer(Size.Fill);

        if (fade > 0.6f) SiteCard(rt, live.Node).Style.Opacity = (fade - 0.6f) / 0.4f;
        else CentredX(rt, live.Node);
        rt.Gap(14f);
        Rule(rt);
        rt.Gap(14f);

        // Who is on shift, and the fold.
        var shift = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(36f)));
        CentredY(rt, Avatar.Build(rt, "IR", 32f, ink: UiColor.AccentForeground, fontSize: ComponentSize.Sm));
        if (fade > 0f)
        {
            rt.Gap(10f);
            rt.BeginCenterAcross();
            var lead = rt.BeginParent(rt.Column());
            lead.Style = new Style { Opacity = fade };
            Bold(rt, "Ines Rowe", ComponentSize.Sm, UiColor.TextPrimary);
            T(rt, "Night lead", FontSize.Px(11.5f), UiColor.TextSecondary);
            rt.EndParent(lead);
            rt.EndCenterAcross();
            rt.Spacer(Size.Fill);
            CentredY(rt, fold.Node);
        }
        rt.EndParent(shift);
        if (fade <= 0f)
        {
            rt.Gap(10f);
            CentredX(rt, fold.Node);
        }
        rt.EndParent(side);
    }

    private static BibuiWidget NavItem(BibuiRuntime rt, string id, string icon, string label, string key, bool selected, float open)
    {
        Box item = rt.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction i = rt.InteractionsFor(item);
        item.Sized(Size.Fill, Size.Px(rt.Theme.Density.Of(40f)));
        item.Style.Padding = EdgeInsets.Symmetric(horizontal: 13f, vertical: 0f);
        item.Style.CornerRadius = rt.Theme.Radii.Md + 2f;
        item.Style.BackgroundColor = selected
            ? UiColor.Accent.WithAlpha(0.16f + 0.06f * item.HotT)
            : UiColor.SurfaceRaisedHover.WithAlpha(item.HotT);

        float fade = Math.Clamp((open - 0.35f) / 0.65f, 0f, 1f);
        rt.BeginParent(item);
        CentredY(rt, Icon(rt, icon, 18f, selected ? UiColor.Accent : UiColor.TextSecondary));
        if (fade > 0f)
        {
            rt.Gap(12f);
            var text = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Fill));
            text.Style = new Style { Opacity = fade };
            CentredY(rt, Bold(rt, label, ComponentSize.Sm, selected ? UiColor.TextPrimary : UiColor.TextSecondary));
            rt.Spacer(Size.Fill);
            CentredY(rt, T(rt, key, FontSize.Px(11f), UiColor.TextDisabled));
            rt.EndParent(text);
        }
        rt.EndParent(item);
        if (fade <= 0f && i.Hovering) Tooltip.Build(rt, label + "  ·  " + key, item);
        return new BibuiWidget(item, i);
    }

    /// <summary>The site at a glance: dome, wind and humidity, and whether the feed is live.</summary>
    private Box SiteCard(BibuiRuntime rt, UiNode liveSwitch)
    {
        double m = Minutes;
        float wind = Data.Wind(m), hum = Data.Humidity(m);
        var card = rt.BeginParent(rt.Column().Sized(Size.Fill));
        card.Style = new Style
        {
            Padding = EdgeInsets.All(14f),
            CornerRadius = rt.Theme.Radii.Lg + 2f,
            BackgroundColor = UiColor.SurfaceRaised,
            BorderWidth = rt.Theme.Borders.Width,
            BorderColor = UiColor.Border,
        };
        var head = rt.BeginParent(rt.Row().Sized(Size.Fill));
        Eyebrow(rt, "Site");
        rt.Spacer(Size.Fill);
        Pill(rt, "Dome open", UiColor.Success, dot: true, height: 20f);
        rt.EndParent(head);
        rt.Gap(12f);
        Meter(rt, "Wind", $"{wind:0} km/h", wind / 40f, UiColor.Accent);
        rt.Gap(10f);
        Meter(rt, "Humidity", $"{hum:0} %", hum / 100f, UiColor.Accent);
        rt.Gap(14f);
        Place(rt, liveSwitch);
        rt.EndParent(card);
        return card;
    }

    /// <summary>A labelled bar, built into the column in scope.</summary>
    private static void Meter(BibuiRuntime rt, string label, string value, float share, UiColor color)
    {
        var row = rt.BeginParent(rt.Row().Sized(Size.Fill));
        T(rt, label, FontSize.Px(12f), UiColor.TextSecondary);
        rt.Spacer(Size.Fill);
        Bold(rt, value, FontSize.Px(12f), UiColor.TextPrimary);
        rt.EndParent(row);
        rt.Gap(6f);
        Progress.Build(rt, Math.Clamp(share, 0f, 1f) * 100f, thickness: 4f, fill: color);
    }

    /// <summary>The brand mark: an orb lit from the upper left, in the accent.</summary>
    private static Box Orb(BibuiRuntime rt, float size)
    {
        var orb = new Box
        {
            Draw = (_, ctx) =>
            {
                Rgba accent = ctx.Resolve(UiColor.Accent), deep = ctx.Resolve(UiColor.AccentActive);
                var r = ctx.Logical;
                float s = ctx.Scale;
                float rad = r.Width / 2f;
                var light = Bibui.Core.Draw.RasterShader.Radial(new System.Numerics.Vector2((r.Left + r.Width * 0.32f) * s, (r.Top + r.Height * 0.28f) * s),
                    r.Width * s, [Rgba.White.WithAlpha(235), accent, deep], [0f, 0.42f, 1f]);
                ctx.DrawRect(ctx.Physical(r.Left, r.Top, r.Width, r.Height), rad * s, new Paint { Fill = Rgba.White, Shader = light });
                // A ring round it, tilted: an orbit.
                ctx.DrawArc(r.MidX, r.MidY, rad - 6f, 1.5f, Rgba.White.WithAlpha(150), 200f, 120f);
            },
        }.Sized(Size.Px(size), Size.Px(size));
        // The glow under it falls outside the box, which clips what the draw
        // makes; a style shadow is the decoration's, drawn before the clip.
        orb.Style.CornerRadius = size / 2f;
        orb.Style.Shadow = new BoxShadow { OffsetY = 3f, Blur = 12f, Color = UiColor.Accent.WithAlpha(110f / 255f) };
        return orb;
    }

    // ── Shared controls ──────────────────────────────────────────────────────

    /// <summary>A square button holding one icon, lit as the pointer comes over it.</summary>
    public static BibuiWidget IconButton(BibuiRuntime rt, string id, string icon, float size = 36f, bool on = false, string? tip = null)
    {
        Box b = rt.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction i = rt.InteractionsFor(b);
        b.Sized(Size.Px(size), Size.Px(size));
        b.Style.CornerRadius = rt.Theme.Radii.Md + 2f;
        b.Style.BorderWidth = rt.Theme.Borders.Width;
        b.Style.BorderColor = on ? UiColor.Accent.WithAlpha(0.6f) : UiColor.Border.WithAlpha(0.6f + 0.4f * b.HotT);
        b.Style.BackgroundColor = on
            ? UiColor.Accent.WithAlpha(0.14f + 0.1f * b.HotT)
            : UiColor.SurfaceRaisedHover.WithAlpha(b.HotT * 0.9f - 0.3f * b.ActiveT);
        rt.BeginParent(b);
        rt.BeginCenterX();
        rt.BeginCenterY();
        Icon(rt, icon, size * 0.46f, on ? UiColor.Accent : UiColor.TextPrimary);
        rt.EndCenterY();
        rt.EndCenterX();
        rt.EndParent(b);
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

    private void Main(BibuiRuntime rt, float w, float h)
    {
        const float barH = 68f;
        var main = rt.BeginParent(rt.Column().Sized(Size.Px(w), Size.Px(h)));
        TopBar(rt, w, barH);

        // The page scrolls inside a fixed frame; the bar beside it is the runtime's.
        Box wrap = rt.BoxFromId("page-wrap");
        wrap.Sized(Size.Px(w), Size.Px(h - barH));
        Box view = rt.BoxFromId("page", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, wrap);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.Sized(Size.Fill, Size.Fill);
        view.AllowOverflowY = true;

        const float pad = 28f;
        float cw = w - 2 * pad - Scrollbar.Lane;
        float ch = h - barH - 2 * pad;
        var page = rt.BeginParent(rt.Column().Sized(Size.Fill));
        page.Style.Padding = new EdgeInsets(pad - 4f, pad - Scrollbar.Lane, pad, pad);
        if (Page == Page.Targets) TargetsPage(rt, cw, ch);
        else if (Page == Page.Telemetry) TelemetryPage(rt, cw, ch);
        else if (Page == Page.Journal) JournalPage(rt, cw, ch);
        else OverviewPage(rt, cw, ch);
        rt.EndParent(page);
        UiNode.PushIntoParent(view, page);

        rt.BeginParent(wrap);
        rt.Scrollbar("page-bar", view);
        rt.EndParent(wrap);
        rt.InteractionsFor(view);   // after everything inside it
        rt.EndParent(main);
    }

    private void TopBar(BibuiRuntime rt, float w, float barH)
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

        var bar = rt.BeginParent(rt.Row().Sized(Size.Px(w), Size.Px(barH)));
        bar.Style = new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 28f, vertical: 0f),
            BorderBottom = new EdgeBorder(rt.Theme.Borders.Width, UiColor.Border),
            BackgroundColor = UiColor.Background,
        };
        rt.BeginCenterAcross();
        Bold(rt, Nav[(int)Page].Label, ComponentSize.Xl, UiColor.TextPrimary);
        rt.Gap(2f);
        T(rt, Subtitles[(int)Page], ComponentSize.Sm, UiColor.TextSecondary);
        rt.EndCenterAcross();
        rt.Spacer(Size.Fill);
        if (field is not null)
        {
            CentredY(rt, field);
            rt.Gap(16f);
        }
        CentredY(rt, ClockChip(rt));
        rt.Gap(16f);
        CentredY(rt, pause.Node);
        rt.Gap(8f);
        CentredY(rt, mode.Node);
        rt.Gap(8f);
        CentredY(rt, look.Node);
        rt.EndParent(bar);
    }

    /// <summary>The clock: UTC and sidereal time, in a quiet chip.</summary>
    private Box ClockChip(BibuiRuntime rt)
    {
        double lst = Data.Lst(Clock);
        int lh = (int)lst, lm = (int)((lst - lh) * 60);
        var chip = rt.BeginParent(rt.Row().Sized(y: Size.Px(36f)));
        chip.Style = new Style
        {
            Padding = EdgeInsets.Symmetric(horizontal: 8f, vertical: 0f),
            CornerRadius = rt.Theme.Radii.Md + 2f,
            BorderWidth = rt.Theme.Borders.Width,
            BorderColor = UiColor.Border,
            BackgroundColor = UiColor.Surface,
        };
        CentredY(rt, Pill(rt, Live ? "LIVE" : "PAUSED", Live ? UiColor.Success : UiColor.TextSecondary, dot: true, height: 20f));
        rt.Gap(10f);
        CentredY(rt, Bold(rt, Utc, ComponentSize.Sm, UiColor.TextPrimary));
        rt.Gap(6f);
        CentredY(rt, T(rt, "UTC", FontSize.Px(11f), UiColor.TextSecondary));
        rt.Gap(12f);
        CentredY(rt, T(rt, $"LST {lh:00}:{lm:00}", ComponentSize.Sm, UiColor.TextSecondary));
        rt.EndParent(chip);
        return chip;
    }

    /// <summary>The search field: a press, or the slash key, gives it the keyboard.</summary>
    private Box SearchField(BibuiRuntime rt, float width)
    {
        Box field = rt.BoxFromId("search-box");
        TextField.InputStyle(rt, field, SearchFocused);
        field.Sized(Size.Px(width), Size.Px(36f));
        var line = rt.TextField("search", Search, SearchEdit, SearchFocused, placeholder: "Search targets");
        if (line.Pressed) { SearchFocused = true; NoteFocused = false; }
        rt.BeginParent(field);
        CentredY(rt, Icon(rt, "search", 15f, UiColor.TextSecondary));
        rt.Gap(8f);
        var input = rt.BeginParent(rt.Column().Sized(Size.Fill, Size.Fill));
        rt.BeginCenter();
        Place(rt, line.Node.Sized(Size.Fill));
        rt.EndCenter();
        rt.EndParent(input);
        rt.Gap(8f);
        CentredY(rt, KeyboardShortcut.Build(rt, "/"));
        rt.EndParent(field);
        return field;
    }
}
