using System.Numerics;
using Bibui;
using Bibui.Draw;
using Bibui.Runtime;

namespace Showcase;

/// <summary>
/// The all-sky view: the dome as a fisheye camera sees it from below, north at
/// the top and east to the left. Everything here is painted through
/// <see cref="DrawContext"/> — gradients, a blurred galactic band, a few
/// hundred stars, the alt-az grid and the targets — and the labels, the target
/// hit boxes and the glass card on top are ordinary layout boxes.
/// </summary>
internal static class Sky
{
    /// <summary>Where the dome sits in its card: centre and radius, logical px from the card's corner.</summary>
    public readonly record struct Dome(float Cx, float Cy, float R);

    /// <summary>A point at (alt, az) on the dome, relative to its centre.</summary>
    public static (float X, float Y) Project(double alt, double az, float r)
    {
        float d = (float)((90.0 - alt) / 90.0) * r;
        double a = az * Math.PI / 180.0;
        return ((float)(-Math.Sin(a)) * d, (float)(-Math.Cos(a)) * d);
    }

    /// <summary>What the painter needs about one target.</summary>
    public readonly record struct Mark(float X, float Y, TargetStatus Status, float Hot, bool Selected);

    /// <summary>Colours the sky is painted in: tokens, resolved by the callback at paint.</summary>
    public readonly record struct Palette(UiColor Accent, UiColor Success, UiColor Warning, UiColor Muted);

    // The palette as the canvas wants it, resolved against the theme the frame is painted in.
    private readonly record struct Colours(Rgba Accent, Rgba Success, Rgba Warning, Rgba Muted);

    /// <summary>
    /// Makes <paramref name="box"/> paint the sky into its own rect. A box the
    /// build acquired: its draw callback is cleared with the rest of its build
    /// state when it is acquired again next frame.
    /// </summary>
    public static void PaintOn(Box box, Dome dome, double lst, Mark[] marks, (float X, float Y)? slewFrom, Palette pal, bool grid, float radius, bool reduced)
        => box.Draw = (_, ctx) => Paint(ctx, dome, lst, marks, slewFrom, pal, grid, radius, reduced);

    private static void Paint(DrawContext ctx, Dome d, double lst, Mark[] marks, (float X, float Y)? slewFrom, Palette palette,
        bool grid, float radius, bool reduced)
    {
        var pal = new Colours(ctx.Resolve(palette.Accent), ctx.Resolve(palette.Success),
            ctx.Resolve(palette.Warning), ctx.Resolve(palette.Muted));
        var L = ctx.Logical;
        float s = ctx.Scale;
        float R = d.R;
        float cx = L.Left + d.Cx, cy = L.Top + d.Cy;

        // The night behind the dome: a deep vertical wash, and a faint glow of the accent low down.
        ctx.DrawRRectGradient(L.Left, L.Top, L.Width, L.Height, radius,
            [(0f, "#0B1030"), (0.55f, "#070A1C"), (1f, "#04050D")],
            (0f, 0f), (0f, 1f), strokeColor: default, strokeWidth: null);
        var glow = RasterShader.Radial(new Vector2(cx * s, (cy + R * 0.9f) * s), R * 1.6f * s,
            [pal.Accent.WithAlpha(46), pal.Accent.WithAlpha(0)], [0f, 1f]);
        ctx.DrawRect(ctx.Physical(L.Left, L.Top, L.Width, L.Height), radius * s, new Paint { Fill = Rgba.White, Shader = glow });

        // The dome: lighter at the horizon, as a real sky is, darkest at the zenith.
        var dome = RasterShader.Radial(new Vector2(cx * s, cy * s), R * s,
            [DrawContext.Color("#060918"), DrawContext.Color("#0C1433"), DrawContext.Color("#1A2350")], [0f, 0.72f, 1f]);
        ctx.DrawRect(ctx.Physical(cx - R, cy - R, 2 * R, 2 * R), R * s, new Paint { Fill = Rgba.White, Shader = dome });

        // The galactic band: a chain of soft blots along it, blurred into one glow.
        var band = new Paint { Fill = new Rgba(170, 180, 255, 34), Blur = R * 0.07f * s };
        for (double theta = 0; theta < 360; theta += 5)
        {
            var (ra, dec) = Data.Band(theta);
            var (alt, az) = Data.AltAz(ra, dec, lst);
            if (alt < 2) continue;
            var (x, y) = Project(alt, az, R);
            float br = R * 0.09f;
            ctx.DrawRect(ctx.Physical(cx + x - br, cy + y - br, 2 * br, 2 * br), br * s, band);
        }

        // The alt-az grid: rings at 30° and 60°, spokes every 45°, ticks round the horizon.
        if (grid)
        {
            var gridColor = new Rgba(150, 165, 230, 46);
            var dashed = new Paint { Stroke = gridColor, StrokeWidth = 1f * s, Dash = new DashPattern(3f * s, 5f * s) };
            foreach (float alt in (ReadOnlySpan<float>)[30f, 60f])
            {
                float rr = R * (90f - alt) / 90f;
                // A ring of a size is the same path every frame, so it comes from the
                // frame's cache, keyed on what it is built from.
                float pr = rr * s;
                var ring = ctx.Path(CacheKey.Of([CacheKey.Bits(pr), 0x5C1]), pr,
                    static (r, p) => p.AddArc(new RectF(0f, 0f, 2f * r, 2f * r), 0f, 360f));
                ctx.PushTransform(Matrix3x2.CreateTranslation((cx - rr) * s, (cy - rr) * s));
                ctx.DrawPath(ring, dashed);
                ctx.PopTransform();
            }
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                ctx.DrawEdge(cx, cy, cx + (float)Math.Sin(a) * R, cy + (float)Math.Cos(a) * R, 1f, gridColor, dashed: true);
            }
            for (int k = 0; k < 72; k++)
            {
                double a = k * Math.PI / 36;
                float len = k % 9 == 0 ? 9f : 4f;
                float sx = (float)Math.Sin(a), sy = (float)Math.Cos(a);
                ctx.DrawEdge(cx + sx * R, cy + sy * R, cx + sx * (R + len), cy + sy * (R + len), 1f, new Rgba(170, 180, 240, 90));
            }
        }
        ctx.DrawArc(cx, cy, R, 1.5f, pal.Accent.WithAlpha(110), 0f, 360f);

        // Stars, binned by size so each bin is one draw.
        Span<Vector2> bins0 = stackalloc Vector2[Data.Stars.Length];
        Span<Vector2> bins1 = stackalloc Vector2[Data.Stars.Length];
        Span<Vector2> bins2 = stackalloc Vector2[Data.Stars.Length];
        int n0 = 0, n1 = 0, n2 = 0;
        foreach (var star in Data.Stars)
        {
            var (alt, az) = Data.AltAz(star.RaHours, star.DecDeg, lst);
            if (alt < 1) continue;
            var (x, y) = Project(alt, az, R);
            var p = new Vector2((cx + x) * s, (cy + y) * s);
            // Fainter towards the horizon: more air to see through.
            float b = star.Brightness * (float)Math.Clamp(alt / 25.0, 0.35, 1.0);
            if (b > 0.55f)
            {
                bins2[n2++] = p;
                var tint = star.Tint switch { 0 => new Rgba(190, 210, 255), 1 => new Rgba(255, 226, 190), _ => Rgba.White };
                ctx.DrawRect(new RectF(p.X - 5 * s, p.Y - 5 * s, p.X + 5 * s, p.Y + 5 * s), 5 * s,
                    new Paint { Fill = tint.WithAlpha(90), Blur = 2.5f * s });
            }
            else if (b > 0.18f) bins1[n1++] = p;
            else bins0[n0++] = p;
        }
        DrawDots(ctx, bins0[..n0], 1.4f * s, new Rgba(200, 212, 255, 165));
        DrawDots(ctx, bins1[..n1], 1.9f * s, new Rgba(228, 234, 255, 225));
        DrawDots(ctx, bins2[..n2], 2.6f * s, Rgba.White);

        // Where the telescope is going: a dashed curve from where it points now.
        Mark? selected = null;
        foreach (var m in marks) if (m.Selected) selected = m;
        if (slewFrom is { } from && selected is { } to && (MathF.Abs(from.X - to.X) + MathF.Abs(from.Y - to.Y)) > 4f)
        {
            float fx = cx + from.X, fy = cy + from.Y, tx = cx + to.X, ty = cy + to.Y;
            // Bow the curve away from the zenith, as a slew in alt-az swings.
            float mx = (fx + tx) / 2f, my = (fy + ty) / 2f;
            float nx = mx - cx, ny = my - cy, nl = MathF.Max(1f, MathF.Sqrt(nx * nx + ny * ny));
            float bow = MathF.Min(40f, MathF.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy)) * 0.25f);
            var q = (fx * s, fy * s, (mx + nx / nl * bow) * s, (my + ny / nl * bow) * s, tx * s, ty * s);
            var path = ctx.Path(CacheKey.Of([CacheKey.Bits(q.Item1), CacheKey.Bits(q.Item2), CacheKey.Bits(q.Item3),
                    CacheKey.Bits(q.Item4), CacheKey.Bits(q.Item5), CacheKey.Bits(q.Item6), 0x51E]), q,
                static (c, p) => { p.MoveTo(c.Item1, c.Item2); p.QuadTo(c.Item3, c.Item4, c.Item5, c.Item6); });
            ctx.DrawPath(path, new Paint
            {
                Stroke = pal.Accent.WithAlpha(200), StrokeWidth = 1.5f * s, Cap = StrokeCap.Round,
                Dash = new DashPattern(5f * s, 5f * s),
            });
        }

        // Targets: a ring in the colour of their state; the chosen one gets a turning reticle.
        foreach (var m in marks)
        {
            float x = cx + m.X, y = cy + m.Y;
            Rgba c = m.Status switch
            {
                TargetStatus.Imaging => pal.Accent,
                TargetStatus.Done => pal.Success,
                TargetStatus.Hold => pal.Warning,
                _ => pal.Muted,
            };
            float rr = 6f + 3f * m.Hot;
            if (m.Status == TargetStatus.Imaging || m.Hot > 0.01f)
                ctx.DrawShadow(x - rr, y - rr, 2 * rr, 2 * rr, rr, 0f, 0f, 14f, 2f, c.WithAlpha((byte)(120 + 80 * m.Hot)));
            ctx.DrawArc(x, y, rr, 2f, c, 0f, 360f);
            ctx.DrawRRect(x - 2f, y - 2f, 4f, 4f, 2f, c, null, null);

            if (m.Selected)
            {
                float spin = reduced ? 0f : ctx.Sawtooth(AnimKey.For("sky", "reticle"), 6f) * 360f;
                const float rr2 = 17f;
                for (int q = 0; q < 4; q++)
                    ctx.DrawArc(x, y, rr2, 2f, pal.Accent, spin + q * 90f + 12f, 56f);
                for (int q = 0; q < 4; q++)
                {
                    double a = q * Math.PI / 2;
                    float ux = (float)Math.Cos(a), uy = (float)Math.Sin(a);
                    ctx.DrawEdge(x + ux * (rr2 + 4f), y + uy * (rr2 + 4f), x + ux * (rr2 + 11f), y + uy * (rr2 + 11f), 2f, pal.Accent);
                }
            }
        }
    }

    private static void DrawDots(DrawContext ctx, ReadOnlySpan<Vector2> pts, float size, Rgba color)
    {
        if (pts.IsEmpty) return;
        ctx.DrawPoints(PointMode.Points, pts, new Paint { Stroke = color, StrokeWidth = size, Cap = StrokeCap.Round });
    }
}
