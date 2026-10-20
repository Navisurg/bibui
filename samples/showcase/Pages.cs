using Bibui;
using Bibui.Runtime;
using static Showcase.Ui;

namespace Showcase;

// The four pages, each built into the page's column. Each is given the width
// and height it has, and sizes its cards from them: wide windows lay the cards
// side by side, narrow ones stack them and let the page scroll.
internal sealed partial class App
{
    private const float Gutter = 16f;

    public string Filter = "all";

    // ── Overview ─────────────────────────────────────────────────────────────

    private void OverviewPage(BibuiRuntime rt, float cw, float ch)
    {
        bool wide = cw >= 940f;
        float kpiH = 132f;
        KpiRow(rt, cw, kpiH, wide ? 4 : 2);
        rt.Gap(Gutter);
        if (wide)
        {
            float rest = MathF.Max(ch - kpiH - 2 * Gutter, 640f);
            float bottomH = MathF.Max(290f, MathF.Round(rest * 0.40f));
            float midH = rest - bottomH;
            float skyW = MathF.Round((cw - Gutter) * 0.62f), telW = cw - Gutter - skyW;
            using (rt.PushParent(rt.RowFit()))
            {
                SkyCard(rt, skyW, midH);
                rt.Gap(Gutter);
                TelemetryCard(rt, "tel-main", telW, midH, Metric, true);
            }
            rt.Gap(Gutter);
            float qW = MathF.Round((cw - Gutter) * 0.6f), logW = cw - Gutter - qW;
            using (rt.PushParent(rt.RowFit()))
            {
                QueueCard(rt, qW, bottomH);
                rt.Gap(Gutter);
                LogCard(rt, logW, bottomH);
            }
        }
        else
        {
            SkyCard(rt, cw, cw >= 2f * 150f + GlassW + 40f ? 440f : StackedSkyHeight(cw));
            rt.Gap(Gutter);
            TelemetryCard(rt, "tel-main", cw, 330f, Metric, true);
            rt.Gap(Gutter);
            QueueCard(rt, cw, 440f);
            rt.Gap(Gutter);
            LogCard(rt, cw, 460f);
        }
    }

    /// <summary>The KPI tiles, <paramref name="perRow"/> to a row, built into the column in scope.</summary>
    private void KpiRow(BibuiRuntime rt, float cw, float h, int perRow)
    {
        double m = Minutes;
        float[] Window(Func<double, float> f, int n = 32, double step = 2.0)
        {
            var v = new float[n];
            for (int k = 0; k < n; k++) v[k] = f(m - (n - 1 - k) * step);
            return v;
        }
        var seeing = Window(Data.Seeing);
        var sky = Window(Data.SkyBrightness);
        var tput = Window(Data.Throughput);
        int done = Data.Targets.Count(t => t.Status == TargetStatus.Done);
        int planned = Data.Targets.Sum(t => t.PlannedMin), exposed = Data.Targets.Sum(t => t.DoneMin);

        var exposure = rt.RowHeight(34f);
        using (rt.PushParent(exposure))
        {
            CentredY(rt, Charts.Ring(rt, (float)exposed / planned, UiColor.Accent, 30f));
            rt.Gap(10f);
            CentredY(rt, T(rt, $"{100.0 * exposed / planned:0}% of the plan exposed", FontSize.Px(12f), UiColor.TextSecondary));
        }
        float w = (cw - Gutter * (perRow - 1)) / perRow;
        Box[] tiles =
        [
            Kpi(rt, w, h, "eye", "Seeing", $"{seeing[^1]:0.00}", "arcsec", Delta(seeing, "0.00", lowerIsBetter: true), Charts.Sparkline(rt, seeing, UiColor.Accent)),
            Kpi(rt, w, h, "moon", "Sky darkness", $"{sky[^1]:0.00}", "mag/arcsec²", Delta(sky, "0.00", lowerIsBetter: false), Charts.Sparkline(rt, sky, UiColor.Token(c => c.Info))),
            Kpi(rt, w, h, "download", "Throughput", $"{tput[^1]:0.0}", "GB/h", Delta(tput, "0.0", lowerIsBetter: false), Charts.Sparkline(rt, tput, UiColor.Success)),
            Kpi(rt, w, h, "list-checks", "Exposure tonight", $"{exposed / 60.0:0.0}", $"of {planned / 60.0:0} h",
                ($"{done} done", UiColor.Success), exposure),
        ];
        for (int r = 0; r < tiles.Length; r += perRow)
        {
            if (r > 0) rt.Gap(Gutter);
            using (rt.PushParent(rt.RowFit()))
                for (int c = r; c < r + perRow && c < tiles.Length; c++)
                {
                    if (c > r) rt.Gap(Gutter);
                    Place(rt, tiles[c]);
                }
        }
    }

    private static (string Text, UiColor Color) Delta(float[] v, string format, bool lowerIsBetter)
    {
        float d = v[^1] - v[^9];
        bool better = lowerIsBetter ? d < 0 : d > 0;
        string sign = d >= 0 ? "+" : "−";
        return ($"{sign}{MathF.Abs(d).ToString(format, System.Globalization.CultureInfo.InvariantCulture)}",
            MathF.Abs(d) < 0.005f ? UiColor.TextSecondary : better ? UiColor.Success : UiColor.Token(c => c.Warning));
    }

    private static Box Kpi(BibuiRuntime rt, float w, float h, string icon, string label, string value, string unit,
        (string Text, UiColor Color) delta, Box foot)
    {
        var tile = rt.ColumnPx(w, h).Styled(CardStyle(rt, 16f));
        using (rt.PushParent(tile))
        {
            using (rt.PushParent(rt.RowFillX(28f)))
            {
                using (rt.PushParent(rt.RowPx(28f, 28f).Styled(new Style
                {
                    CornerRadius = rt.Theme.Radii.Md + 1f,
                    BackgroundColor = UiColor.Accent.WithAlpha(0.14f),
                })))
                using (rt.Centred())
                    CentredY(rt, Icon(rt, icon, 15f, UiColor.Accent));
                rt.Gap(10f);
                CentredY(rt, T(rt, label, ComponentSize.Sm, UiColor.TextSecondary));
                rt.Spring();
                CentredY(rt, Pill(rt, delta.Text, delta.Color, height: 20f));
            }
            rt.Gap(12f);
            using (rt.PushParent(rt.RowHeight(36f)))
            {
                Bold(rt, value, FontSize.Px(28f), UiColor.TextPrimary);
                rt.Gap(6f);
                BottomY(rt, T(rt, unit, ComponentSize.Sm, UiColor.TextSecondary), 4f);
            }
            rt.Spring();
            Place(rt, foot.FillX().Px(height: foot.SizeY.Kind == SizeKind.Pixels ? foot.SizeY.Value : 30f));
        }
        return tile;
    }

    // ── The sky card ─────────────────────────────────────────────────────────

    private const float GlassW = 318f, GlassH = 238f;

    /// <summary>
    /// The sky card: the painted dome, the target hit boxes over it, and the
    /// selected target on frosted glass. Wide, the glass sits beside the dome;
    /// narrow, under it; with <paramref name="glass"/> off, the dome has the card.
    /// </summary>
    private void SkyCard(BibuiRuntime rt, float w, float h, bool glass = true)
    {
        double lst = Data.Lst(Clock);
        var sel = SelectedTarget;

        // Where the dome goes: beside the glass, above it, or alone.
        float gw = MathF.Min(GlassW, w - 32f);
        bool beside = glass && (w - gw - 40f >= 2f * 110f || h < GlassH + 300f);
        Sky.Dome dome;
        if (!glass) { float r = MathF.Min(h / 2f - 30f, w / 2f - 30f); dome = new(w / 2f, h / 2f + 6f, r); }
        else if (beside)
        {
            float x0 = 16f + gw + 16f;
            float r = MathF.Max(40f, MathF.Min(h / 2f - 30f, (w - x0) / 2f - 32f));
            dome = new((x0 + w) / 2f, h / 2f, r);
        }
        else
        {
            float room = h - GlassH - 32f - 56f;
            float r = MathF.Min(room / 2f - 24f, w / 2f - 30f);
            dome = new(w / 2f, 56f + room / 2f, r);
        }
        float R = dome.R;

        // Ask, top first: the glass card's buttons, the glass card, the toggles, then the targets under them.
        BibuiWidget slew = default, open = default;
        Box? frost = null;
        if (glass)
        {
            slew = rt.Button("slew", Pointing == Selected ? "On target" : "Slew here", disabled: Pointing == Selected, size: ComponentSize.Sm);
            if (slew.Clicked) Slew();
            open = rt.Button("open-log", "Open log", ghost: true, size: ComponentSize.Sm);
            if (open.Clicked) Page = Page.Journal;
            frost = rt.BoxFromId("sky-glass", InteractionKinds.LeftPress);
            rt.InteractionsFor(frost);
        }
        var grid = rt.Toggle("sky-grid", Grid, icon: "grid");
        if (grid.Clicked) Grid = !Grid;
        var labels = rt.Toggle("sky-labels", Labels, icon: "tag");
        if (labels.Clicked) Labels = !Labels;

        var marks = new List<Sky.Mark>();
        var hits = new List<UiNode>();
        var tags = new List<UiNode>();
        (float X, float Y)? from = null;
        foreach (var t in Data.Targets)
        {
            var (alt, az) = Data.AltAz(t.RaHours, t.DecDeg, lst);
            if (alt < 3) continue;
            var (x, y) = Sky.Project(alt, az, R);
            if (t.Id == Pointing) from = (x, y);
            Box hit = rt.BoxFromId("sky-t-" + t.Id, InteractionKinds.LeftPress);
            Interaction i = rt.InteractionsFor(hit);
            if (i.Clicked) Selected = t.Id;
            if (i.Clicked && i.ClickCount >= 2) Slew();
            hit.Px(30f, 30f).At(dome.Cx + x - 15f, dome.Cy + y - 15f);
            hits.Add(hit);
            bool chosen = t.Id == Selected;
            marks.Add(new Sky.Mark(x, y, t.Status, hit.HotT, chosen));
            if (i.Hovering) Tooltip.Build(rt, $"{t.Name}  ·  alt {alt:0}°  ·  {Data.StatusName(t.Status)}", hit);
            if (Labels && (chosen || t.Status == TargetStatus.Imaging))
            {
                var tag = rt.RowFit().Styled(new Style
                {
                    Padding = EdgeInsets.Symmetric(horizontal: 7f, vertical: 3f),
                    CornerRadius = 6f,
                    BackgroundColor = chosen ? UiColor.Accent : "#0B1030CC",
                });
                using (rt.PushParent(tag))
                    Bold(rt, t.Name, FontSize.Px(11.5f), chosen ? UiColor.AccentForeground : "#DCE3FA");
                // Beside the mark, on whichever side has room.
                float tagW = 16f + 6.6f * t.Name.Length;
                bool left = dome.Cx + x + 22f + tagW > w - 12f;
                tags.Add(tag.At(dome.Cx + x + (left ? -22f - tagW : 22f), dome.Cy + y - 11f));
            }
        }

        var pal = new Sky.Palette(UiColor.Accent, UiColor.Success, UiColor.Token(c => c.Warning), UiColor.Rgba(205, 214, 245, 230));
        float radius = rt.Theme.Radii.Lg + 4f;
        bool reduced = rt.Theme.Motion.Easing == Bibui.Theming.MotionEasing.None;

        Box card = rt.BoxFromId("sky", InteractionKinds.Clip);
        card.Px(w, h);
        card.Style.CornerRadius = radius;
        card.Style.Shadow = rt.Theme.Elevation.Level(2);
        // The sky is the card's first child, so the hit boxes and labels pushed after it paint over it.
        Box canvas = rt.BoxFromId("sky-canvas", parent: card);
        canvas.Px(w, h);
        Sky.PaintOn(canvas, dome, lst, [.. marks], from, pal, Grid, radius, reduced);
        foreach (var hit in hits) UiNode.PushIntoParent(card, hit);
        foreach (var tag in tags) UiNode.PushIntoParent(card, tag);

        // North up, east left: the sky seen from below.
        foreach (var (letter, x, y) in new[] { ("N", 0f, -1f), ("S", 0f, 1f), ("E", -1f, 0f), ("W", 1f, 0f) })
            UiNode.PushIntoParent(card, Bold(rt, letter, FontSize.Px(12f), "#8E9AD0")
                .At(dome.Cx + x * (R + 20f) - 4f, dome.Cy + y * (R + 20f) - 8f));

        // Top left: what this is. Top right: what to show.
        string lstText = $"LST {(int)lst:00}:{(int)((lst % 1) * 60):00}";
        using (rt.PushParent(card))
        {
            using (rt.PushParent(rt.RowHeight(32f).Styled(Glass(10f, padX: 8f, padY: 0f)).At(16f, 16f)))
            {
                CentredY(rt, Pill(rt, "ALL-SKY", UiColor.Accent, dot: true, height: 20f));
                rt.Gap(10f);
                CentredY(rt, T(rt, w > 520f ? "Fisheye · north up · " + lstText : lstText, FontSize.Px(12f), "#AEB8DE"));
            }
            using (rt.PushParent(rt.RowFit().At(w - 16f - 2 * 36f - 6f, 16f)))
            {
                Place(rt, grid.Node);
                rt.Gap(6f);
                Place(rt, labels.Node);
            }

            // The legend, bottom right, where the dome leaves room for it.
            if (w - (dome.Cx + R) > 170f)
                using (rt.PushParent(rt.ColumnFit().Styled(Glass(10f, padX: 12f, padY: 10f))
                    .At(w - 16f - 128f, glass && !beside ? 64f : h - 16f - 106f)))
                {
                    LegendRow(rt, "Imaging", UiColor.Accent);
                    rt.Gap(6f);
                    LegendRow(rt, "Queued", "#CDD6F5");
                    rt.Gap(6f);
                    LegendRow(rt, "Complete", UiColor.Success);
                    rt.Gap(6f);
                    LegendRow(rt, "Weather hold", UiColor.Token(c => c.Warning));
                }
        }

        if (frost is { } g)
        {
            var (salt, saz) = Data.AltAz(sel.RaHours, sel.DecDeg, lst);
            float airmass = salt > 0 ? (float)(1.0 / Math.Sin((salt + 244.0 / (165.0 + 47.0 * Math.Pow(salt, 1.1))) * Math.PI / 180.0)) : 99f;
            g.ChildLayoutAxis = LayoutAxis.Y;
            g.SizeX = SemanticSize.Pixels(beside ? gw : w - 32f);
            g.SizeY = SemanticSize.ChildrenSum();
            g.Style = Glass(rt.Theme.Radii.Lg + 4f, padX: 16f, padY: 14f, blur: 18f, frosted: true,
                dark: rt.Theme.Colors.Mode == Bibui.Theming.ThemeMode.Dark);
            using (rt.PushParent(g))
            {
                using (rt.PushParent(rt.RowFillX(22f)))
                {
                    Eyebrow(rt, "Selected target");
                    rt.Spring();
                    StatusPill(rt, sel.Status);
                }
                rt.Gap(10f);
                Bold(rt, sel.Name, FontSize.Px(20f), UiColor.TextPrimary);
                rt.Gap(10f);
                T(rt, $"{sel.Code} · {Data.KindName(sel.Kind)} · {sel.Filter}", FontSize.Px(12f), UiColor.TextSecondary);
                rt.Gap(10f);
                using (rt.PushParent(rt.RowFillX()))
                {
                    Stat(rt, "Altitude", $"{salt:0.0}°");
                    rt.Spring();
                    Stat(rt, "Azimuth", $"{saz:0}°");
                    rt.Spring();
                    Stat(rt, "Airmass", airmass > 9 ? "—" : $"{airmass:0.00}");
                    rt.Spring();
                    Stat(rt, "Mag", $"{sel.Magnitude:0.0}");
                }
                rt.Gap(10f);
                Progress.Build(rt, sel.DoneMin, sel.PlannedMin, $"Exposure  {sel.DoneMin} / {sel.PlannedMin} min", thickness: 6f);
                rt.Gap(10f);
                using (rt.PushParent(rt.RowFit()))
                {
                    Place(rt, slew.Node);
                    rt.Gap(8f);
                    Place(rt, open.Node);
                }
            }
            // Beside the dome, centred down the card under its header chip; under the dome, at the foot.
            float gh = g.Extent.Y > 0 ? g.Extent.Y : GlassH;
            float gy = beside ? MathF.Max(60f, 24f + (h - gh) / 2f) : h - 16f - gh;
            UiNode.PushIntoParent(card, g.At(16f, MathF.Min(gy, h - 16f - gh)));
        }
    }

    /// <summary>The height a sky card with the glass under the dome needs at this width.</summary>
    private static float StackedSkyHeight(float w) => MathF.Min(w, 620f) - 60f + 56f + GlassH + 32f + 24f;

    /// <summary>Frosted glass: the sky behind it blurred, a translucent surface, a hairline of light.</summary>
    /// <remarks>
    /// The blur fills the rect's square corners under the rounded fill.
    /// The chips sit on the night sky in either mode, so they keep night colours;
    /// the selected-target card is <paramref name="frosted"/>: the theme's own
    /// surface, see-through, so it reads as part of the app in light mode too.
    /// </remarks>
    private static Style Glass(float radius, float padX, float padY, float blur = 10f, bool frosted = false, bool dark = true) => new()
    {
        Padding = EdgeInsets.Symmetric(horizontal: padX, vertical: padY),
        CornerRadius = radius,
        BackdropBlur = blur,
        BackgroundColor = frosted ? UiColor.Surface.WithAlpha(dark ? 0.62f : 0.84f) : "#0A0E2499",
        BorderWidth = 1f,
        BorderColor = frosted ? UiColor.Border.WithAlpha(0.8f) : "#FFFFFF1F",
    };

    private static void LegendRow(BibuiRuntime rt, string label, UiColor color)
    {
        using (rt.PushParent(rt.RowHeight(16f)))
        {
            CentredY(rt, rt.Box().Px(8f, 8f).Styled(new Style { CornerRadius = 4f, BorderWidth = 2f, BorderColor = color }));
            rt.Gap(8f);
            CentredY(rt, T(rt, label, FontSize.Px(11.5f), "#C3CCEB"));
        }
    }

    private static void Stat(BibuiRuntime rt, string label, string value)
    {
        using (rt.PushParent(rt.ColumnFit()))
        {
            T(rt, label, FontSize.Px(11f), UiColor.TextSecondary);
            rt.Gap(2f);
            Bold(rt, value, ComponentSize.Md, UiColor.TextPrimary);
        }
    }

    public static Box StatusPill(BibuiRuntime rt, TargetStatus s) => Pill(rt, Data.StatusName(s), s switch
    {
        TargetStatus.Imaging => UiColor.Accent,
        TargetStatus.Done => UiColor.Success,
        TargetStatus.Hold => UiColor.Token(c => c.Warning),
        _ => UiColor.TextSecondary,
    }, dot: s == TargetStatus.Imaging, height: 20f);

    // ── Telemetry ────────────────────────────────────────────────────────────

    private Chart SeriesFor(Metric metric, int n = 90)
    {
        double m = Minutes;
        var v = new float[n];
        Func<double, float> f = metric switch
        {
            Metric.Temperature => Data.SensorTemp,
            Metric.Throughput => Data.Throughput,
            _ => Data.Seeing,
        };
        for (int k = 0; k < n; k++) v[k] = f(m - (n - 1 - k));
        return metric switch
        {
            Metric.Temperature => new(new Charts.Series(v, -21f, -19f, 4, "°C", "0.0"), "Sensor temperature", "°C", "0.00", UiColor.Token(c => c.Info)),
            Metric.Throughput => new(new Charts.Series(v, 0f, 3f, 3, "GB/h", "0.0"), "Data throughput", "GB/h", "0.00", UiColor.Success),
            _ => new(new Charts.Series(v, 0.5f, 1.3f, 4, "″", "0.0"), "Seeing (FWHM)", "arcsec", "0.00", UiColor.Accent),
        };
    }

    private readonly record struct Chart(Charts.Series Series, string Title, string Unit, string Format, UiColor Color);

    private static readonly ToggleOption[] MetricOptions =
        [new("Seeing", "seeing"), new("Sensor", "temp"), new("Data", "data")];

    private void TelemetryCard(BibuiRuntime rt, string id, float w, float h, Metric metric, bool picker)
    {
        var chart = SeriesFor(metric);
        UiNode? pick = null;
        if (picker)
        {
            var group = rt.ToggleGroup(id + "-metric", MetricOptions,
                metric switch { Metric.Temperature => "temp", Metric.Throughput => "data", _ => "seeing" });
            if (group.Picked is { } p) Metric = p switch { "temp" => Metric.Temperature, "data" => Metric.Throughput, _ => Metric.Seeing };
            pick = group.Node;
        }
        var (plot, hover) = Charts.AreaChart(rt, id + "-chart", chart.Series, chart.Color);

        var v = chart.Series.Values;
        int at = hover ?? v.Length - 1;
        int minsAgo = v.Length - 1 - at;
        string when = minsAgo == 0 ? "now" : $"{minsAgo} min ago";
        float avg = v.Average();

        using (rt.PushParent(rt.ColumnPx(w, h).Styled(CardStyle(rt))))
        {
            CardHeader(rt, metric switch { Metric.Temperature => "thermometer", Metric.Throughput => "download", _ => "eye" },
                chart.Title, null, pick is null ? [] : [CentredY(rt, pick)]);
            rt.Gap(10f);
            using (rt.PushParent(rt.RowFillX(40f)))
            {
                Bold(rt, v[at].ToString(chart.Format, System.Globalization.CultureInfo.InvariantCulture), FontSize.Px(30f), UiColor.TextPrimary);
                rt.Gap(6f);
                BottomY(rt, T(rt, chart.Unit, ComponentSize.Md, UiColor.TextSecondary), 5f);
                rt.Gap(14f);
                BottomY(rt, T(rt, when, ComponentSize.Sm, hover is null ? UiColor.TextSecondary : UiColor.Accent), 7f);
                rt.Spring();
                BottomY(rt, T(rt, $"90 min avg {avg.ToString(chart.Format, System.Globalization.CultureInfo.InvariantCulture)} {chart.Unit}", ComponentSize.Sm, UiColor.TextSecondary), 7f);
            }
            rt.Gap(12f);
            using (rt.PushParent(rt.RowFill()))
            {
                Charts.Axis(rt, chart.Series);
                rt.Gap(8f);
                Place(rt, plot);
            }
            rt.Gap(6f);
            using (rt.PushParent(rt.RowFillX()))
            {
                rt.Gap(52f);
                T(rt, "−90 min", FontSize.Px(11f), UiColor.TextSecondary);
                rt.Spring();
                T(rt, "−45", FontSize.Px(11f), UiColor.TextSecondary);
                rt.Spring();
                T(rt, "now", FontSize.Px(11f), UiColor.TextSecondary);
            }
        }
    }

    // ── Target queue ─────────────────────────────────────────────────────────

    private static readonly (string Key, string Label, float Width)[] Columns =
    [
        ("name", "Target", 0f), ("kind", "Type", 84f), ("mag", "Mag", 52f), ("alt", "Alt", 58f),
        ("progress", "Exposure", 132f), ("priority", "Status", 116f),
    ];

    private static readonly (string Value, string Label)[] Filters =
        [("all", "All targets"), ("imaging", "Imaging"), ("queued", "Queued"), ("hold", "On hold"), ("done", "Complete")];

    private void QueueCard(BibuiRuntime rt, float w, float h)
    {
        // The filter's dropdown and the header cells ask before the rows.
        string filterLabel = Array.Find(Filters, f => f.Value == Filter).Label ?? "All targets";
        var select = Select.Begin(rt, "queue-filter", filterLabel, width: 150f);
        if (select.Open)
            foreach (var (value, label) in Filters)
                if (rt.SelectItem("queue-filter-" + value, label, selected: Filter == value).Clicked)
                {
                    Filter = value;
                    select.Close();
                }
        var filterNode = select.End();

        float inner = w - 2 * 18f;
        float fixedW = Columns.Sum(c => c.Width);
        float nameW = inner - fixedW - Scrollbar.Lane - 16f;

        var head = rt.RowFillX().Padded(new EdgeInsets(0f, 0f, 0f, 16f));
        head.Style.BorderBottom = new EdgeBorder(rt.Theme.Borders.Width, UiColor.Border);
        using (rt.PushParent(head))
        {
            foreach (var (key, label, width) in Columns)
            {
                Box cell = rt.BoxFromId("col-" + key, InteractionKinds.LeftPress);
                Interaction i = rt.InteractionsFor(cell);
                if (i.Clicked)
                {
                    if (Sort == key) SortDescending = !SortDescending;
                    else { Sort = key; SortDescending = false; }
                }
                cell.Px(width > 0 ? width : nameW, 30f);
                bool sorted = Sort == key;
                using (rt.PushParent(cell))
                {
                    CentredY(rt, Bold(rt, label.ToUpperInvariant(), FontSize.Px(11f),
                        sorted ? UiColor.TextPrimary : cell.HotT > 0.5f ? UiColor.TextPrimary : UiColor.TextSecondary));
                    if (sorted)
                    {
                        rt.Gap(4f);
                        CentredY(rt, Icon(rt, SortDescending ? "chevron-down" : "chevron-up", 12f, UiColor.Accent));
                    }
                }
            }
            rt.Gap(Scrollbar.Lane);
        }

        // The rows scroll inside a frame of their own, with a bar beside them.
        var rows = VisibleTargets().Where(t => Filter switch
        {
            "imaging" => t.Status == TargetStatus.Imaging,
            "queued" => t.Status == TargetStatus.Queued,
            "hold" => t.Status == TargetStatus.Hold,
            "done" => t.Status == TargetStatus.Done,
            _ => true,
        }).ToList();
        double lst = Data.Lst(Clock);
        Box frame = rt.BoxFromId("queue-frame").FillX().FillY();
        Box list = rt.BoxFromId("queue-list", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, frame);
        list.ChildLayoutAxis = LayoutAxis.Y;
        list.FillX().FillY();
        list.AllowOverflowY = true;
        foreach (var t in rows) UiNode.PushIntoParent(list, QueueRow(rt, t, lst, nameW));
        if (rows.Count == 0)
            using (rt.PushParent(list))
            {
                rt.Gap(32f);
                CentredX(rt, T(rt, "Nothing matches. Try another filter or search.", ComponentSize.Sm, UiColor.TextSecondary));
            }
        using (rt.PushParent(frame))
            rt.Scrollbar("queue-bar", list);
        rt.InteractionsFor(list);

        int imaging = Data.Targets.Count(t => t.Status == TargetStatus.Imaging);
        using (rt.PushParent(rt.ColumnPx(w, h).Styled(CardStyle(rt))))
        {
            CardHeader(rt, "list-checks", "Target queue", $"{rows.Count} of {Data.Targets.Length} · {imaging} imaging", filterNode);
            rt.Gap(10f);
            Place(rt, head);
            Place(rt, frame);
        }
    }

    private Box QueueRow(BibuiRuntime rt, Target t, double lst, float nameW)
    {
        Box row = rt.BoxFromId("row-" + t.Id, InteractionKinds.LeftPress | InteractionKinds.RightPress);
        Interaction i = rt.InteractionsFor(row);
        if (i.Clicked) Selected = t.Id;
        if (i.Clicked && i.ClickCount >= 2) { Selected = t.Id; Slew(); }
        if (i.RightPressed) { Selected = t.Id; MenuFor = t.Id; MenuX = i.X; MenuY = i.Y; rt.OpenDropDown("row-menu"); }

        bool chosen = t.Id == Selected;
        row.FillX().Px(height: rt.Theme.Density.Of(50f));
        row.Style.CornerRadius = rt.Theme.Radii.Md;
        row.Style.Padding = new EdgeInsets(0f, 0f, 0f, 13f);
        row.Style.BackgroundColor = chosen
            ? UiColor.Accent.WithAlpha(0.13f + 0.05f * row.HotT)
            : UiColor.SurfaceRaisedHover.WithAlpha(0.8f * row.HotT);
        row.Style.BorderLeft = new EdgeBorder(3f, chosen ? UiColor.Accent : UiColor.Accent.WithAlpha(0f));

        var (alt, _) = Data.AltAz(t.RaHours, t.DecDeg, lst);
        float pct = (float)t.DoneMin / t.PlannedMin;

        // A cell: a column the row's height, what is built in it centred down it.
        Box Cell(float width) => rt.ColumnFillY().Px(width: width);

        using (rt.PushParent(row))
        {
            using (rt.PushParent(Cell(nameW)))
            using (rt.Centred())
            {
                Bold(rt, t.Name, ComponentSize.Sm, UiColor.TextPrimary);
                rt.Gap(2f);
                T(rt, t.Code, FontSize.Px(11.5f), UiColor.TextSecondary);
            }
            using (rt.PushParent(Cell(84f)))
            using (rt.Centred())
                T(rt, Data.KindName(t.Kind), ComponentSize.Sm, UiColor.TextSecondary);
            using (rt.PushParent(Cell(52f)))
            using (rt.Centred())
                T(rt, $"{t.Magnitude:0.0}", ComponentSize.Sm, UiColor.TextPrimary);
            using (rt.PushParent(Cell(58f)))
            using (rt.Centred())
                T(rt, alt > 0 ? $"{alt:0}°" : "set", ComponentSize.Sm, alt > 30 ? UiColor.TextPrimary : UiColor.TextSecondary);
            using (rt.PushParent(Cell(132f)))
            using (rt.Centred())
            using (rt.PushParent(rt.RowHeight(20f)))
            {
                using (rt.PushParent(Cell(76f)))
                using (rt.Centred())
                    Progress.Build(rt, pct * 100f, thickness: 5f,
                        fill: t.Status == TargetStatus.Done ? UiColor.Success : UiColor.Accent);
                rt.Gap(10f);
                CentredY(rt, T(rt, $"{pct * 100:0}%", FontSize.Px(12f), UiColor.TextSecondary));
            }
            using (rt.PushParent(Cell(116f)))
            using (rt.Centred())
                StatusPill(rt, t.Status);
        }
        return row;
    }

    // ── Observing log ────────────────────────────────────────────────────────

    private void LogCard(BibuiRuntime rt, float w, float h, ComponentSize textSize = ComponentSize.Sm, bool night = false)
    {
        var t = SelectedTarget;
        var source = LogFor(t, night);

        Box frame = rt.BoxFromId("log-frame").FillX().FillY();
        Box view = rt.BoxFromId("log-view", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, frame);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.FillX().FillY();
        view.AllowOverflowY = true;
        MarkdownWidget md;
        using (rt.PushParent(view))
            md = rt.Markdown("log-md", source, width: w - 2 * 18f - Scrollbar.Lane - 8f, textSize: textSize);
        if (md.TaskClicked) Markdown.ToggleTask(source, md.ToggledTask);
        using (rt.PushParent(frame))
            rt.Scrollbar("log-bar", view);
        rt.InteractionsFor(view);

        using (rt.PushParent(rt.ColumnPx(w, h).Styled(CardStyle(rt))))
        {
            CardHeader(rt, "file-text", "Observing log", "Markdown", Badge.Build(rt, t.Code, BadgeVariant.Outline));
            rt.Gap(6f);
            Place(rt, frame);
        }
    }
}
