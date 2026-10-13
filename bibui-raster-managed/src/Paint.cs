using System.Numerics;

namespace Bibui.Raster.Managed;

/// <summary>
/// How to paint a shape. A plain value: building one allocates nothing, and
/// the canvas reads the fields directly — there is no native paint object to
/// cache.
///
/// <para>The default is an antialiased fill in transparent black; use
/// <see cref="Fill"/> or <see cref="Stroke"/>.</para>
/// </summary>
public struct Paint
{
    /// <summary>Straight-alpha <c>0xAARRGGBB</c>. With a <see cref="Shader"/>, only its alpha is used, to modulate the shader.</summary>
    public uint Color;

    public PaintStyle Style;

    /// <summary>
    /// Stroke width in local units. 0 is a hairline: one device pixel wide
    /// whatever the transform. A stroke that maps to at most one device pixel
    /// is drawn as a hairline too, with its alpha scaled by the width.
    /// </summary>
    public float StrokeWidth;

    public LineCap Cap;
    public LineJoin Join;

    /// <summary>Miter limit; 0 means the default of 4.</summary>
    public float MiterLimit;

    /// <summary>
    /// Draw without antialiasing. Only honoured where it changes nothing a UI
    /// draws today (the stroke-as-hairline test); fills are always
    /// antialiased.
    /// </summary>
    public bool Aliased;

    /// <summary>Replaces <see cref="Color"/>'s RGB with a per-pixel colour (gradients). Null for a solid colour.</summary>
    public Shader? Shader;

    /// <summary>A dash pattern for strokes, applied before stroking (<see cref="Dasher"/>). Null for a solid stroke.</summary>
    public DashPattern? Dash;

    /// <summary>
    /// Gaussian sigma in device px applied to the shape's coverage before it is
    /// painted (soft shadows). Needs <see cref="Canvas.MaskBlur"/>. 0 for none.
    /// </summary>
    public float MaskBlurSigma;

    public static Paint Fill(uint argb) => new() { Color = argb };

    public static Paint Stroke(uint argb, float width) => new() { Color = argb, Style = PaintStyle.Stroke, StrokeWidth = width };

    public readonly byte Alpha => (byte)(Color >> 24);
}

/// <summary>
/// A per-pixel colour source: gradients and image patterns plug in here.
/// The canvas calls <see cref="Begin"/> once per draw with the
/// transform in force, then <see cref="ShadeSpan"/> for each run of pixels it
/// covers. The paint's alpha and the coverage are applied by the canvas.
/// </summary>
public abstract class Shader
{
    /// <summary>True when every colour the shader produces is opaque (lets a fully covered span be a copy).</summary>
    public virtual bool IsOpaque => false;

    /// <summary>One draw begins. <paramref name="localToDevice"/> maps the shader's coordinates to pixels.</summary>
    public abstract void Begin(in Matrix3x2 localToDevice);

    /// <summary>
    /// Premultiplied <c>0xAARRGGBB</c> colours for the pixel centres
    /// (<paramref name="x"/> + i + ½, <paramref name="y"/> + ½), i in [0, dst.Length).
    /// </summary>
    public abstract void ShadeSpan(int x, int y, Span<uint> dst);
}

/// <summary>A shader that is one colour everywhere. Mostly for tests of the shader path.</summary>
internal sealed class ColorShader : Shader
{
    private readonly uint _premul;

    public ColorShader(uint argb) => _premul = PixelMath.Premul(argb);

    public override bool IsOpaque => (_premul >> 24) == 255;

    public override void Begin(in Matrix3x2 localToDevice) { }

    public override void ShadeSpan(int x, int y, Span<uint> dst) => dst.Fill(_premul);
}

/// <summary>
/// Turns a stroked path into the outline that fills it: caps, joins
/// and miters. The output is filled nonzero, so it may overlap itself — the
/// fill is exact where it does. <see cref="PathStroker"/> is the implementation.
/// </summary>
public interface IPathStroker
{
    /// <summary>
    /// Appends to <paramref name="destination"/> the outline of
    /// <paramref name="source"/> stroked with <paramref name="paint"/>'s width,
    /// cap, join and miter limit, in the same (local) coordinates. For
    /// <see cref="PaintStyle.StrokeAndFill"/> the outline includes the path.
    /// The canvas has already applied any dash. <paramref name="resScale"/> is
    /// the transform's scale: curves are followed to a quarter of a device
    /// pixel, so to ¼ / <paramref name="resScale"/> in local units.
    /// </summary>
    void Stroke(Path source, in Paint paint, float resScale, Path destination); // + resScale; dashes applied by the canvas
}

/// <summary>Blurs a coverage mask in place: soft shadows, <see cref="Paint.MaskBlurSigma"/>.</summary>
public interface IMaskBlur
{
    /// <summary>
    /// Blurs <paramref name="mask"/> by a Gaussian of <paramref name="sigma"/>
    /// device px. The canvas has already outset the mask's bounds by
    /// <see cref="Margin"/> so the blur has room to spread.
    /// </summary>
    void Blur(CoverageMask mask, float sigma);

    /// <summary>How far, in px, a blur of <paramref name="sigma"/> spreads (typically 3σ).</summary>
    int Margin(float sigma);
}
