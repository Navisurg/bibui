using Bibui.Theming;
using Bibui.Renderer;

namespace Bibui.Renderer.Components;

internal static class SpinnerRenderer
{
    // ── Typed model ───────────────────────────────────────────────────────────

    internal sealed record SpinnerModel(
        string Id,
        string SizeStr,
        float? Diameter = null,
        float? StrokeWidth = null,
        UiColor? Color = null,
        UiColor? TrackColor = null);

    // ── FromNative ────────────────────────────────────────────────────────────

    internal static SpinnerModel FromNative(Spinner s) => new(
        Id:      s.Id ?? "",
        SizeStr: s.Size switch { ComponentSize.Sm => "sm", ComponentSize.Lg => "lg", ComponentSize.Xl => "xl", _ => "md" },
        Diameter:    s.Diameter,
        StrokeWidth: s.StrokeWidth,
        Color:       s.Color,
        TrackColor:  s.TrackColor);

    // ── Measure ───────────────────────────────────────────────────────────────

    /// <summary>The diameter of a spinner at the default size step.</summary>
    internal const float DefaultDiameter = 24f;

    /// <summary>The diameter this spinner draws at: an explicit one, or its size step.</summary>
    private static float DiameterOf(SpinnerModel comp)
        => comp.Diameter ?? comp.SizeStr switch { "sm" => 16f, "lg" => 32f, "xl" => 48f, _ => DefaultDiameter };

    private static float StrokeOf(SpinnerModel comp, float diameter)
        => comp.StrokeWidth ?? (comp.Diameter is null
            ? comp.SizeStr switch { "sm" => 2f, "lg" => 3f, "xl" => 4f, _ => 2.5f }
            : Math.Clamp(diameter / 9f, 1f, 4f));

    // ── Native render ─────────────────────────────────────────────────────────

    /// <summary>The diameter <paramref name="spinner"/> draws at.</summary>
    internal static float Diameter(Spinner spinner) => DiameterOf(FromNative(spinner));

    internal static void Draw(SpinnerModel comp, Frame frame, ThemeTokens theme, DrawContext ctx)
    {
        float diameter = DiameterOf(comp);
        float strokeW  = StrokeOf(comp, diameter);

        float x = frame.X;
        float y = frame.Y;
        float cx = x + diameter / 2f;
        float cy = y + diameter / 2f;
        float radius = (diameter - strokeW) / 2f;

        // One turn per ramp. The ramp asks for the next frame by never settling,
        // so the spinner keeps itself spinning and needs no clock to read.
        float rotationDeg = ctx.Sawtooth(AnimKey.For(comp.Id, "spin")) * 360f;

        // Track ring (muted full circle)
        ctx.DrawArc(cx, cy, radius, strokeW, comp.TrackColor?.Resolve(theme.Colors) ?? theme.Colors.Border,
            startAngle: 0f, sweepAngle: 360f);

        // Active arc (270° sweep, rotating)
        ctx.DrawArc(cx, cy, radius, strokeW, comp.Color?.Resolve(theme.Colors) ?? theme.Colors.TextPrimary,
            startAngle: rotationDeg - 90f, sweepAngle: 270f);
    }
}
