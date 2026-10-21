using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.Runtime;

namespace Showcase;

/// <summary>
/// Charts drawn straight onto the canvas through <see cref="DrawContext"/>.
/// A draw callback runs at paint time, after layout, so it knows the rect it
/// fills, and resolves the theme colours it needs there too
/// (<see cref="DrawContext.Resolve(UiColor)"/>), so a theme changed between
/// build and paint is followed.
/// </summary>
internal static class Charts
{
    /// <summary>A minimal line with a soft fill under it and a dot at its newest value.</summary>
    public static Box Sparkline(BibuiRuntime rt, float[] values, UiColor color)
    {
        return new Box
        {
            Draw = (_, ctx) =>
            {
                Rgba c = ctx.Resolve(color);
                // The box clips, so the plot stops short of the right edge by
                // the dot at its newest value.
                var r = ctx.Logical with { Right = ctx.Logical.Right - 4f };
                (float min, float max) = Range(values, 0.15f);
                Span<(float X, float Y)> pts = stackalloc (float, float)[values.Length];
                for (int i = 0; i < values.Length; i++)
                    pts[i] = (r.Left + r.Width * i / (values.Length - 1),
                              r.Bottom - 2f - (r.Height - 6f) * (values[i] - min) / (max - min));
                ctx.DrawFilledPathGradient(pts, r.Top, r.Bottom, c.WithAlpha(70));
                ctx.DrawPolyline(pts, c, 1.75f);
                var (lx, ly) = pts[^1];
                ctx.DrawShadow(lx - 3f, ly - 3f, 6f, 6f, 3f, 0f, 0f, 8f, 1f, c.WithAlpha(160));
                ctx.DrawRRect(lx - 3f, ly - 3f, 6f, 6f, 3f, c, null, null);
            },
        };
    }

    /// <summary>What the area chart shows, and where the pointer is over it.</summary>
    public readonly record struct Series(float[] Values, float Min, float Max, int Ticks, string Unit, string Format);

    /// <summary>
    /// A wide area chart: dashed grid, a gradient under a two-pixel line, a
    /// pulsing head at the newest reading, and a crosshair where the pointer is.
    /// The retained box takes the press, so it knows when it is hovered, and
    /// draws the picture itself.
    /// </summary>
    public static (Box Node, int? Hover) AreaChart(BibuiRuntime rt, string id, Series s, UiColor color)
    {
        Box host = rt.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction i = rt.InteractionsFor(host);

        int? hover = null;
        if (i.Hovering && host.Extent.X > 0f)
        {
            float f = Math.Clamp((i.X - host.Rect.X) / host.Extent.X, 0f, 1f);
            hover = (int)MathF.Round(f * (s.Values.Length - 1));
        }

        int? h = hover;
        host.Draw = (_, ctx) =>
        {
            bool reduced = ctx.Theme.Motion.Easing == Bibui.Core.Theming.MotionEasing.None;
            Rgba line = ctx.Resolve(color);
            Rgba grid = ctx.Resolve(UiColor.Border);
            Rgba text = ctx.Resolve(UiColor.TextSecondary);
            Rgba surface = ctx.Resolve(UiColor.Surface);
            // The box clips, so the plot is inset by half a grid line top and
            // bottom, and on the right by the head at the newest reading.
            var r = ctx.Logical with { Top = ctx.Logical.Top + 1f, Right = ctx.Logical.Right - 6f, Bottom = ctx.Logical.Bottom - 1f };
            for (int t = 0; t <= s.Ticks; t++)
            {
                float y = r.Top + r.Height * t / s.Ticks;
                ctx.DrawEdge(r.Left, y, r.Right, y, 1f, grid, dashed: t > 0 && t < s.Ticks);
            }

            Span<(float X, float Y)> pts = stackalloc (float, float)[s.Values.Length];
            for (int k = 0; k < s.Values.Length; k++)
                pts[k] = (r.Left + r.Width * k / (s.Values.Length - 1),
                          r.Bottom - r.Height * Math.Clamp((s.Values[k] - s.Min) / (s.Max - s.Min), 0f, 1f));
            ctx.DrawFilledPathGradient(pts, r.Top, r.Bottom, line.WithAlpha(110));
            ctx.DrawPolyline(pts, line, 2f);

            // The newest reading breathes: a ring that grows and fades once a second.
            var (hx, hy) = pts[^1];
            float pulse = reduced ? 0f : ctx.Sawtooth(AnimKey.For(id, "pulse"), 1.6f);
            float pr = 4f + 10f * pulse;
            ctx.DrawArc(hx, hy, pr, 2f, line.WithAlpha((byte)(170 * (1f - pulse))), 0f, 360f);
            ctx.DrawRRect(hx - 4f, hy - 4f, 8f, 8f, 4f, line, surface, 2f);

            if (h is { } k2)
            {
                var (cx, cy) = pts[k2];
                ctx.DrawEdge(cx, r.Top, cx, r.Bottom, 1f, text.WithAlpha(140), dashed: true);
                ctx.DrawShadow(cx - 5f, cy - 5f, 10f, 10f, 5f, 0f, 0f, 12f, 2f, line.WithAlpha(200));
                ctx.DrawRRect(cx - 5f, cy - 5f, 10f, 10f, 5f, surface, line, 2.5f);
            }
        };
        host.FillX().FillY();
        return (host, hover);
    }

    /// <summary>The axis beside an area chart: its tick values, top to bottom, spread over the plot's height.</summary>
    public static void Axis(BibuiRuntime rt, Series s)
    {
        using (rt.PushParent(rt.ColumnFillY().Px(width: 44f)))
            for (int t = 0; t <= s.Ticks; t++)
            {
                if (t > 0) rt.Spring();
                float v = s.Max - (s.Max - s.Min) * t / s.Ticks;
                Ui.T(rt, v.ToString(s.Format, System.Globalization.CultureInfo.InvariantCulture), FontSize.Px(11f), UiColor.TextSecondary);
            }
    }

    /// <summary>A ring gauge: a track, an arc of <paramref name="value"/> 0..1, and nothing else.</summary>
    public static Box Ring(BibuiRuntime rt, float value, UiColor color, float size)
    {
        return new Box
        {
            Draw = (_, ctx) =>
            {
                Rgba c = ctx.Resolve(color), track = ctx.Resolve(UiColor.Border);
                var r = ctx.Logical;
                float cx = r.MidX, cy = r.MidY, rad = MathF.Min(r.Width, r.Height) / 2f - 4f;
                ctx.DrawArc(cx, cy, rad, 5f, track, 0f, 360f);
                ctx.DrawArc(cx, cy, rad, 5f, c, -90f, 360f * Math.Clamp(value, 0f, 1f));
            },
        }.Px(size, size);
    }

    private static (float Min, float Max) Range(ReadOnlySpan<float> values, float pad)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in values) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
        float span = MathF.Max(max - min, 1e-3f);
        return (min - span * pad, max + span * pad);
    }
}
