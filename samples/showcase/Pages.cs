using Bibui.Core;
using Bibui.Core.Runtime;
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
            var middle = rt.BeginParent(rt.Row());
            SkyCard(rt, skyW, midH);
            rt.Gap(Gutter);
            TelemetryCard(rt, "tel-main", telW, midH, Metric, true);
            rt.EndParent(middle);
            rt.Gap(Gutter);
            float qW = MathF.Round((cw - Gutter) * 0.6f), logW = cw - Gutter - qW;
            var bottom = rt.BeginParent(rt.Row());
            QueueCard(rt, qW, bottomH);
            rt.Gap(Gutter);
            LogCard(rt, logW, bottomH);
            rt.EndParent(bottom);
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

        float w = (cw - Gutter * (perRow - 1)) / perRow;
        var row = rt.BeginParent(rt.Row());
        int tile = 0;
        // Where the next tile goes: beside the last, or first in a new row under it.
        void Next()
        {
            if (tile > 0 && tile % perRow == 0)
            {
                rt.EndParent(row);
                rt.Gap(Gutter);
                row = rt.BeginParent(rt.Row());
            }
            else if (tile > 0) rt.Gap(Gutter);
            tile++;
        }

        Next();
        var kpi = BeginKpi(rt, w, h, "eye", "Seeing", $"{seeing[^1]:0.00}", "arcsec", Delta(seeing, "0.00", lowerIsBetter: true));
        Charts.Sparkline(rt, seeing, UiColor.Accent).Sized(Size.Fill, Size.Px(30f));
        EndKpi(rt, kpi);
        Next();
        kpi = BeginKpi(rt, w, h, "moon", "Sky darkness", $"{sky[^1]:0.00}", "mag/arcsec²", Delta(sky, "0.00", lowerIsBetter: false));
        Charts.Sparkline(rt, sky, UiColor.Token(c => c.Info)).Sized(Size.Fill, Size.Px(30f));
        EndKpi(rt, kpi);
        Next();
        kpi = BeginKpi(rt, w, h, "download", "Throughput", $"{tput[^1]:0.0}", "GB/h", Delta(tput, "0.0", lowerIsBetter: false));
        Charts.Sparkline(rt, tput, UiColor.Success).Sized(Size.Fill, Size.Px(30f));
        EndKpi(rt, kpi);
        Next();
        kpi = BeginKpi(rt, w, h, "list-checks", "Exposure tonight", $"{exposed / 60.0:0.0}", $"of {planned / 60.0:0} h", ($"{done} done", UiColor.Success));
        var exposure = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(34f)));
        rt.BeginCenterAcross();
        Charts.Ring(rt, (float)exposed / planned, UiColor.Accent, 30f);
        rt.EndCenterAcross();
        rt.Gap(10f);
        rt.BeginCenterAcross();
        T(rt, $"{100.0 * exposed / planned:0}% of the plan exposed", FontSize.Px(12f), UiColor.TextSecondary);
        rt.EndCenterAcross();
        rt.EndParent(exposure);
        EndKpi(rt, kpi);
        rt.EndParent(row);
    }

    private static (string Text, UiColor Color) Delta(float[] v, string format, bool lowerIsBetter)
    {
        float d = v[^1] - v[^9];
        bool better = lowerIsBetter ? d < 0 : d > 0;
        string sign = d >= 0 ? "+" : "−";
        return ($"{sign}{MathF.Abs(d).ToString(format, System.Globalization.CultureInfo.InvariantCulture)}",
            MathF.Abs(d) < 0.005f ? UiColor.TextSecondary : better ? UiColor.Success : UiColor.Token(c => c.Warning));
    }

    /// <summary>A KPI tile: what is built until <see cref="EndKpi"/> is its foot, a chart the tile's width.</summary>
    private static Box BeginKpi(BibuiRuntime rt, float w, float h, string icon, string label, string value, string unit,
        (string Text, UiColor Color) delta)
    {
        var tile = rt.BeginParent(rt.Column().Sized(Size.Px(w), Size.Px(h)));
        tile.Style = CardStyle(rt, 16f);
        var head = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(28f)));
        var badge = rt.BeginParent(rt.Row().Sized(Size.Px(28f), Size.Px(28f)));
        badge.Style = new Style
        {
            CornerRadius = rt.Theme.Radii.Md + 1f,
            BackgroundColor = UiColor.Accent.WithAlpha(0.14f),
        };
        rt.BeginCenterXY();
        Icon(rt, icon, 15f, UiColor.Accent);
        rt.EndCenterXY();
        rt.EndParent(badge);
        rt.Gap(10f);
        rt.BeginCenterAcross();
        T(rt, label, ComponentSize.Sm, UiColor.TextSecondary);
        rt.EndCenterAcross();
        rt.Spacer(Size.Fill);
        rt.BeginCenterAcross();
        Pill(rt, delta.Text, delta.Color, height: 20f);
        rt.EndCenterAcross();
        rt.EndParent(head);
        rt.Gap(12f);
        var reading = rt.BeginParent(rt.Row().Sized(y: Size.Px(36f)));
        Bold(rt, value, FontSize.Px(28f), UiColor.TextPrimary);
        rt.Gap(6f);
        var foot = BeginBottomY(rt);
        T(rt, unit, ComponentSize.Sm, UiColor.TextSecondary);
        EndBottomY(rt, foot, 4f);
        rt.EndParent(reading);
        rt.Spacer(Size.Fill);
        return tile;
    }

    private static void EndKpi(BibuiRuntime rt, Box tile) => rt.EndParent(tile);

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
        // These four sit over the targets but come after them in the card, and
        // ask order is z-order, so they are built (and ask) here and are moved
        // into the card below: the one build here out of the tree's order.
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

        var pal = new Sky.Palette(UiColor.Accent, UiColor.Success, UiColor.Token(c => c.Warning), UiColor.Rgba(205, 214, 245, 230));
        float radius = rt.Theme.Radii.Lg + 4f;
        bool reduced = rt.Theme.Motion.Easing == Bibui.Core.Theming.MotionEasing.None;

        Box card = rt.BoxFromId("sky", InteractionKinds.Clip);
        card.Sized(Size.Px(w), Size.Px(h));
        card.Style.CornerRadius = radius;
        card.Style.Shadow = rt.Theme.Elevation.Level(2);
        // The sky is the card's first child, so the hit boxes and labels built after it paint over it.
        Box canvas = rt.BoxFromId("sky-canvas", parent: card);
        canvas.Sized(Size.Px(w), Size.Px(h));
        rt.BeginParent(card);

        var marks = new List<Sky.Mark>();
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
            hit.Sized(Size.Px(30f), Size.Px(30f)).At(dome.Cx + x - 15f, dome.Cy + y - 15f);
            bool chosen = t.Id == Selected;
            marks.Add(new Sky.Mark(x, y, t.Status, hit.HotT, chosen));
            if (i.Hovering) Tooltip.Build(rt, $"{t.Name}  ·  alt {alt:0}°  ·  {Data.StatusName(t.Status)}", hit);
            if (Labels && (chosen || t.Status == TargetStatus.Imaging))
            {
                var tag = rt.BeginParent(rt.Row());
                tag.Style = new Style
                {
                    Padding = EdgeInsets.Symmetric(horizontal: 7f, vertical: 3f),
                    CornerRadius = 6f,
                    BackgroundColor = chosen ? UiColor.Accent : "#0B1030CC",
                };
                Bold(rt, t.Name, FontSize.Px(11.5f), chosen ? UiColor.AccentForeground : "#DCE3FA");
                rt.EndParent(tag);
                // Beside the mark, on whichever side has room.
                float tagW = 16f + 6.6f * t.Name.Length;
                bool left = dome.Cx + x + 22f + tagW > w - 12f;
                tag.At(dome.Cx + x + (left ? -22f - tagW : 22f), dome.Cy + y - 11f);
            }
        }

        // The sky is painted once the marks are known.
        Sky.PaintOn(canvas, dome, lst, [.. marks], from, pal, Grid, radius, reduced);

        // North up, east left: the sky seen from below.
        foreach (var (letter, x, y) in new[] { ("N", 0f, -1f), ("S", 0f, 1f), ("E", -1f, 0f), ("W", 1f, 0f) })
            Bold(rt, letter, FontSize.Px(12f), "#8E9AD0").At(dome.Cx + x * (R + 20f) - 4f, dome.Cy + y * (R + 20f) - 8f);

        // Top left: what this is. Top right: what to show.
        string lstText = $"LST {(int)lst:00}:{(int)((lst % 1) * 60):00}";
        var chip = rt.BeginParent(rt.Row().Sized(y: Size.Px(32f)).At(16f, 16f));
        chip.Style = Glass(10f, padX: 8f, padY: 0f);
        rt.BeginCenterAcross();
        Pill(rt, "ALL-SKY", UiColor.Accent, dot: true, height: 20f);
        rt.EndCenterAcross();
        rt.Gap(10f);
        rt.BeginCenterAcross();
        T(rt, w > 520f ? "Fisheye · north up · " + lstText : lstText, FontSize.Px(12f), "#AEB8DE");
        rt.EndCenterAcross();
        rt.EndParent(chip);
        var toggles = rt.BeginParent(rt.Row().At(w - 16f - 2 * 36f - 6f, 16f));
        // Built above, to ask before the targets.
        UiNode.PushIntoParent(toggles, grid.Node);
        rt.Gap(6f);
        UiNode.PushIntoParent(toggles, labels.Node);
        rt.EndParent(toggles);

        // The legend, bottom right, where the dome leaves room for it.
        if (w - (dome.Cx + R) > 170f)
        {
            var legend = rt.BeginParent(rt.Column().At(w - 16f - 128f, glass && !beside ? 64f : h - 16f - 106f));
            legend.Style = Glass(10f, padX: 12f, padY: 10f);
            LegendRow(rt, "Imaging", UiColor.Accent);
            rt.Gap(6f);
            LegendRow(rt, "Queued", "#CDD6F5");
            rt.Gap(6f);
            LegendRow(rt, "Complete", UiColor.Success);
            rt.Gap(6f);
            LegendRow(rt, "Weather hold", UiColor.Token(c => c.Warning));
            rt.EndParent(legend);
        }
        rt.EndParent(card);

        if (frost is { } g)
        {
            var (salt, saz) = Data.AltAz(sel.RaHours, sel.DecDeg, lst);
            float airmass = salt > 0 ? (float)(1.0 / Math.Sin((salt + 244.0 / (165.0 + 47.0 * Math.Pow(salt, 1.1))) * Math.PI / 180.0)) : 99f;
            g.ChildLayoutAxis = LayoutAxis.Y;
            g.SizeX = SemanticSize.Pixels(beside ? gw : w - 32f);
            g.SizeY = SemanticSize.ChildrenSum();
            g.Style = Glass(rt.Theme.Radii.Lg + 4f, padX: 16f, padY: 14f, blur: 18f, frosted: true,
                dark: rt.Theme.Colors.Mode == Bibui.Core.Theming.ThemeMode.Dark);
            rt.BeginParent(g);
            var eyebrow = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(22f)));
            Eyebrow(rt, "Selected target");
            rt.Spacer(Size.Fill);
            StatusPill(rt, sel.Status);
            rt.EndParent(eyebrow);
            rt.Gap(10f);
            Bold(rt, sel.Name, FontSize.Px(20f), UiColor.TextPrimary);
            rt.Gap(10f);
            T(rt, $"{sel.Code} · {Data.KindName(sel.Kind)} · {sel.Filter}", FontSize.Px(12f), UiColor.TextSecondary);
            rt.Gap(10f);
            var stats = rt.BeginParent(rt.Row().Sized(Size.Fill));
            Stat(rt, "Altitude", $"{salt:0.0}°");
            rt.Spacer(Size.Fill);
            Stat(rt, "Azimuth", $"{saz:0}°");
            rt.Spacer(Size.Fill);
            Stat(rt, "Airmass", airmass > 9 ? "—" : $"{airmass:0.00}");
            rt.Spacer(Size.Fill);
            Stat(rt, "Mag", $"{sel.Magnitude:0.0}");
            rt.EndParent(stats);
            rt.Gap(10f);
            Progress.Build(rt, sel.DoneMin, sel.PlannedMin, $"Exposure  {sel.DoneMin} / {sel.PlannedMin} min", thickness: 6f);
            rt.Gap(10f);
            var actions = rt.BeginParent(rt.Row());
            // Built above, to ask before the targets.
            UiNode.PushIntoParent(actions, slew.Node);
            rt.Gap(8f);
            UiNode.PushIntoParent(actions, open.Node);
            rt.EndParent(actions);
            rt.EndParent(g);
            // Acquired above, to ask before the targets, so moved into the card here.
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
        var row = rt.BeginParent(rt.Row().Sized(y: Size.Px(16f)));
        rt.BeginCenterAcross();
        var ring = rt.Box().Sized(Size.Px(8f), Size.Px(8f));
        ring.Style = new Style { CornerRadius = 4f, BorderWidth = 2f, BorderColor = color };
        rt.EndCenterAcross();
        rt.Gap(8f);
        rt.BeginCenterAcross();
        T(rt, label, FontSize.Px(11.5f), "#C3CCEB");
        rt.EndCenterAcross();
        rt.EndParent(row);
    }

    private static void Stat(BibuiRuntime rt, string label, string value)
    {
        var stat = rt.BeginParent(rt.Column());
        T(rt, label, FontSize.Px(11f), UiColor.TextSecondary);
        rt.Gap(2f);
        Bold(rt, value, ComponentSize.Md, UiColor.TextPrimary);
        rt.EndParent(stat);
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

    // The point each area chart's pointer was over last frame, -1 for none:
    // the reading above a chart is built before it asks, so it shows last
    // frame's answer.
    private readonly Dictionary<string, int> _chartHover = new();

    private void TelemetryCard(BibuiRuntime rt, string id, float w, float h, Metric metric, bool picker)
    {
        var chart = SeriesFor(metric);
        var v = chart.Series.Values;
        int hovered = _chartHover.GetValueOrDefault(id, -1);
        int at = hovered >= 0 ? hovered : v.Length - 1;
        int minsAgo = v.Length - 1 - at;
        string when = minsAgo == 0 ? "now" : $"{minsAgo} min ago";
        float avg = v.Average();

        var card = rt.BeginParent(rt.Column().Sized(Size.Px(w), Size.Px(h)));
        card.Style = CardStyle(rt);
        var header = BeginCardHeader(rt, metric switch { Metric.Temperature => "thermometer", Metric.Throughput => "download", _ => "eye" },
            chart.Title, null);
        if (picker)
        {
            rt.BeginCenterAcross();
            var group = rt.ToggleGroup(id + "-metric", MetricOptions,
                metric switch { Metric.Temperature => "temp", Metric.Throughput => "data", _ => "seeing" });
            if (group.Picked is { } p) Metric = p switch { "temp" => Metric.Temperature, "data" => Metric.Throughput, _ => Metric.Seeing };
            rt.EndCenterAcross();
        }
        EndCardHeader(rt, header);
        rt.Gap(10f);
        var reading = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Px(40f)));
        Bold(rt, v[at].ToString(chart.Format, System.Globalization.CultureInfo.InvariantCulture), FontSize.Px(30f), UiColor.TextPrimary);
        rt.Gap(6f);
        var foot = BeginBottomY(rt);
        T(rt, chart.Unit, ComponentSize.Md, UiColor.TextSecondary);
        EndBottomY(rt, foot, 5f);
        rt.Gap(14f);
        foot = BeginBottomY(rt);
        T(rt, when, ComponentSize.Sm, hovered < 0 ? UiColor.TextSecondary : UiColor.Accent);
        EndBottomY(rt, foot, 7f);
        rt.Spacer(Size.Fill);
        foot = BeginBottomY(rt);
        T(rt, $"90 min avg {avg.ToString(chart.Format, System.Globalization.CultureInfo.InvariantCulture)} {chart.Unit}", ComponentSize.Sm, UiColor.TextSecondary);
        EndBottomY(rt, foot, 7f);
        rt.EndParent(reading);
        rt.Gap(12f);
        var plotRow = rt.BeginParent(rt.Row().Sized(Size.Fill, Size.Fill));
        Charts.Axis(rt, chart.Series);
        rt.Gap(8f);
        var (_, hover) = Charts.AreaChart(rt, id + "-chart", chart.Series, chart.Color);
        _chartHover[id] = hover ?? -1;
        rt.EndParent(plotRow);
        rt.Gap(6f);
        var times = rt.BeginParent(rt.Row().Sized(Size.Fill));
        rt.Gap(52f);
        T(rt, "−90 min", FontSize.Px(11f), UiColor.TextSecondary);
        rt.Spacer(Size.Fill);
        T(rt, "−45", FontSize.Px(11f), UiColor.TextSecondary);
        rt.Spacer(Size.Fill);
        T(rt, "now", FontSize.Px(11f), UiColor.TextSecondary);
        rt.EndParent(times);
        rt.EndParent(card);
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
        // Ask order is z-order and follows the tree here: the filter's dropdown
        // in the header, then the header cells, then the rows, which ask
        // inside the clipping list so cannot take a press above it. A pick or
        // a sort shows in the rows and the count on the next frame.
        var rows = VisibleTargets().Where(t => Filter switch
        {
            "imaging" => t.Status == TargetStatus.Imaging,
            "queued" => t.Status == TargetStatus.Queued,
            "hold" => t.Status == TargetStatus.Hold,
            "done" => t.Status == TargetStatus.Done,
            _ => true,
        }).ToList();
        int imaging = Data.Targets.Count(t => t.Status == TargetStatus.Imaging);
        float inner = w - 2 * 18f;
        float fixedW = Columns.Sum(c => c.Width);
        float nameW = inner - fixedW - Scrollbar.Lane - 16f;

        var card = rt.BeginParent(rt.Column().Sized(Size.Px(w), Size.Px(h)));
        card.Style = CardStyle(rt);
        var header = BeginCardHeader(rt, "list-checks", "Target queue", $"{rows.Count} of {Data.Targets.Length} · {imaging} imaging");
        string filterLabel = Array.Find(Filters, f => f.Value == Filter).Label ?? "All targets";
        var select = Select.Begin(rt, "queue-filter", filterLabel, width: 150f);
        if (select.Open)
            foreach (var (value, label) in Filters)
                if (rt.SelectItem("queue-filter-" + value, label, selected: Filter == value).Clicked)
                {
                    Filter = value;
                    select.Close();
                }
        select.End();
        EndCardHeader(rt, header);
        rt.Gap(10f);

        var head = rt.BeginParent(rt.Row().Sized(Size.Fill));
        head.Style.Padding = new EdgeInsets(0f, 0f, 0f, 16f);
        head.Style.BorderBottom = new EdgeBorder(rt.Theme.Borders.Width, UiColor.Border);
        foreach (var (key, label, width) in Columns)
        {
            Box cell = rt.BoxFromId("col-" + key, InteractionKinds.LeftPress);
            Interaction i = rt.InteractionsFor(cell);
            if (i.Clicked)
            {
                if (Sort == key) SortDescending = !SortDescending;
                else { Sort = key; SortDescending = false; }
            }
            cell.Sized(Size.Px(width > 0 ? width : nameW), Size.Px(30f));
            bool sorted = Sort == key;
            rt.BeginParent(cell);
            rt.BeginCenterAcross();
            Bold(rt, label.ToUpperInvariant(), FontSize.Px(11f), sorted ? UiColor.TextPrimary : cell.HotT > 0.5f ? UiColor.TextPrimary : UiColor.TextSecondary);
            rt.EndCenterAcross();
            if (sorted)
            {
                rt.Gap(4f);
                rt.BeginCenterAcross();
                Icon(rt, SortDescending ? "chevron-down" : "chevron-up", 12f, UiColor.Accent);
                rt.EndCenterAcross();
            }
            rt.EndParent(cell);
        }
        rt.Gap(Scrollbar.Lane);
        rt.EndParent(head);

        // The rows scroll inside a frame of their own, with a bar beside them.
        double lst = Data.Lst(Clock);
        Box frame = rt.BoxFromId("queue-frame").Sized(Size.Fill, Size.Fill);
        Box list = rt.BoxFromId("queue-list", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, frame);
        list.ChildLayoutAxis = LayoutAxis.Y;
        list.Sized(Size.Fill, Size.Fill);
        list.AllowOverflowY = true;
        rt.BeginParent(list);
        foreach (var t in rows) QueueRow(rt, t, lst, nameW);
        if (rows.Count == 0)
        {
            rt.Gap(32f);
            rt.BeginCenterAcross();
            T(rt, "Nothing matches. Try another filter or search.", ComponentSize.Sm, UiColor.TextSecondary);
            rt.EndCenterAcross();
        }
        rt.EndParent(list);
        rt.BeginParent(frame);
        rt.Scrollbar("queue-bar", list);
        rt.EndParent(frame);
        rt.InteractionsFor(list);
        rt.EndParent(card);
    }

    private Box QueueRow(BibuiRuntime rt, Target t, double lst, float nameW)
    {
        Box row = rt.BoxFromId("row-" + t.Id, InteractionKinds.LeftPress | InteractionKinds.RightPress);
        Interaction i = rt.InteractionsFor(row);
        if (i.Clicked) Selected = t.Id;
        if (i.Clicked && i.ClickCount >= 2) { Selected = t.Id; Slew(); }
        if (i.RightPressed) { Selected = t.Id; MenuFor = t.Id; MenuX = i.X; MenuY = i.Y; rt.OpenDropDown("row-menu"); }

        bool chosen = t.Id == Selected;
        row.Sized(Size.Fill, Size.Px(rt.Theme.Density.Of(50f)));
        row.Style.CornerRadius = rt.Theme.Radii.Md;
        row.Style.Padding = new EdgeInsets(0f, 0f, 0f, 13f);
        row.Style.BackgroundColor = chosen
            ? UiColor.Accent.WithAlpha(0.13f + 0.05f * row.HotT)
            : UiColor.SurfaceRaisedHover.WithAlpha(0.8f * row.HotT);
        row.Style.BorderLeft = new EdgeBorder(3f, chosen ? UiColor.Accent : UiColor.Accent.WithAlpha(0f));

        var (alt, _) = Data.AltAz(t.RaHours, t.DecDeg, lst);
        float pct = (float)t.DoneMin / t.PlannedMin;

        // A cell: a column the row's height, what is built in it centred down it.
        Box Cell(float width) => rt.Column().Sized(Size.Px(width), Size.Fill);

        rt.BeginParent(row);
        var name = rt.BeginParent(Cell(nameW));
        rt.BeginCenter();
        Bold(rt, t.Name, ComponentSize.Sm, UiColor.TextPrimary);
        rt.Gap(2f);
        T(rt, t.Code, FontSize.Px(11.5f), UiColor.TextSecondary);
        rt.EndCenter();
        rt.EndParent(name);
        var kind = rt.BeginParent(Cell(84f));
        rt.BeginCenter();
        T(rt, Data.KindName(t.Kind), ComponentSize.Sm, UiColor.TextSecondary);
        rt.EndCenter();
        rt.EndParent(kind);
        var mag = rt.BeginParent(Cell(52f));
        rt.BeginCenter();
        T(rt, $"{t.Magnitude:0.0}", ComponentSize.Sm, UiColor.TextPrimary);
        rt.EndCenter();
        rt.EndParent(mag);
        var altitude = rt.BeginParent(Cell(58f));
        rt.BeginCenter();
        T(rt, alt > 0 ? $"{alt:0}°" : "set", ComponentSize.Sm, alt > 30 ? UiColor.TextPrimary : UiColor.TextSecondary);
        rt.EndCenter();
        rt.EndParent(altitude);
        var exposure = rt.BeginParent(Cell(132f));
        rt.BeginCenter();
        var progress = rt.BeginParent(rt.Row().Sized(y: Size.Px(20f)));
        var bar = rt.BeginParent(Cell(76f));
        rt.BeginCenter();
        Progress.Build(rt, pct * 100f, thickness: 5f,
            fill: t.Status == TargetStatus.Done ? UiColor.Success : UiColor.Accent);
        rt.EndCenter();
        rt.EndParent(bar);
        rt.Gap(10f);
        rt.BeginCenterAcross();
        T(rt, $"{pct * 100:0}%", FontSize.Px(12f), UiColor.TextSecondary);
        rt.EndCenterAcross();
        rt.EndParent(progress);
        rt.EndCenter();
        rt.EndParent(exposure);
        var status = rt.BeginParent(Cell(116f));
        rt.BeginCenter();
        StatusPill(rt, t.Status);
        rt.EndCenter();
        rt.EndParent(status);
        rt.EndParent(row);
        return row;
    }

    // ── Observing log ────────────────────────────────────────────────────────

    private void LogCard(BibuiRuntime rt, float w, float h, ComponentSize textSize = ComponentSize.Sm, bool night = false)
    {
        var t = SelectedTarget;
        var source = LogFor(t, night);

        var card = rt.BeginParent(rt.Column().Sized(Size.Px(w), Size.Px(h)));
        card.Style = CardStyle(rt);
        var header = BeginCardHeader(rt, "file-text", "Observing log", "Markdown");
        Badge.Build(rt, t.Code, BadgeVariant.Outline);
        EndCardHeader(rt, header);
        rt.Gap(6f);
        Box frame = rt.BoxFromId("log-frame").Sized(Size.Fill, Size.Fill);
        Box view = rt.BoxFromId("log-view", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, frame);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.Sized(Size.Fill, Size.Fill);
        view.AllowOverflowY = true;
        rt.BeginParent(view);
        var md = rt.Markdown("log-md", source, width: w - 2 * 18f - Scrollbar.Lane - 8f, textSize: textSize);
        rt.EndParent(view);
        if (md.TaskClicked) Markdown.ToggleTask(source, md.ToggledTask);
        rt.BeginParent(frame);
        rt.Scrollbar("log-bar", view);
        rt.EndParent(frame);
        rt.InteractionsFor(view);
        rt.EndParent(card);
    }
}
