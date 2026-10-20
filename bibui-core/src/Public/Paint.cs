using Bibui.Core.Draw;

namespace Bibui.Core;

/// <summary>
/// The look of a draw: what fills it, what strokes it, and the effects on top.
/// A value, so a draw that wants a variation writes <c>paint with { … }</c>. A
/// <see cref="DrawContext"/> keeps a current paint (<see cref="DrawContext.PushPaint"/>),
/// so draws that share a look name it once.
///
/// <para>A shape is filled when <see cref="Fill"/> or <see cref="Shader"/> is
/// set, then stroked when <see cref="Stroke"/> is; a line or points are always
/// stroked; a texture is modulated by the fill colour's alpha. Lengths are
/// physical px, like every <see cref="DrawContext"/> draw.</para>
/// </summary>
public readonly record struct Paint
{
    public Paint() { }

    /// <summary>The fill colour; a shape with neither this nor a <see cref="Shader"/> is not filled.</summary>
    public Rgba? Fill { get; init; }

    /// <summary>A gradient the fill takes its colour from, in place of <see cref="Fill"/>.</summary>
    public RasterShader? Shader { get; init; }

    /// <summary>The stroke colour; a shape without one is not stroked.</summary>
    public Rgba? Stroke { get; init; }

    public float StrokeWidth { get; init; } = 1f;
    public StrokeCap Cap { get; init; }
    public StrokeJoin Join { get; init; }
    public DashPattern? Dash { get; init; }

    /// <summary>Blurs the fill's coverage by this sigma: a soft shadow. Zero for none.</summary>
    public float Blur { get; init; }

    /// <summary>How a texture is sampled when it is scaled.</summary>
    public FilterQuality Filter { get; init; }

    public bool Antialias { get; init; } = true;

    public static Paint Filled(Rgba color) => new() { Fill = color };

    public static Paint Stroked(Rgba color, float width) => new() { Stroke = color, StrokeWidth = width };

    // What the raster backend draws a fill and a stroke with.
    internal RasterPaint FillPaint() =>
        new(new PaintSpec { Color = Fill ?? Rgba.Black, Style = PaintStyle.Fill, IsAntialias = Antialias, FilterQuality = Filter })
        {
            Shader = Shader,
            MaskBlurSigma = Blur,
        };

    internal RasterPaint StrokePaint() =>
        new(new PaintSpec
        {
            Color = Stroke ?? Fill ?? Rgba.Black,
            Style = PaintStyle.Stroke,
            StrokeWidth = StrokeWidth,
            StrokeCap = Cap,
            StrokeJoin = Join,
            IsAntialias = Antialias,
            FilterQuality = Filter,
        })
        {
            Dash = Dash,
        };
}
