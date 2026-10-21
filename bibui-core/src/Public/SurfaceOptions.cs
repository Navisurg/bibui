namespace Bibui.Core;

public sealed class SurfaceOptions
{
    public int Width { get; init; } = 400;
    public int Height { get; init; } = 200;
    public float ScaleFactor { get; init; } = 1f;

    /// <summary>
    /// Physical pixels for <paramref name="logical"/> logical pixels at
    /// <paramref name="scale"/>: rounded half away from zero, as
    /// wp_fractional_scale_v1 asks of a toplevel's buffer (a 202-wide window at
    /// 1.25 is 253 wide, not banker's 252). The one rule every renderer and host
    /// sizes a surface by, so a host's buffer and the runtime's frame cannot
    /// disagree by a pixel.
    ///
    /// <para>A scale reaches here as a float (1.05 is 1.0499999…), so a product
    /// within 1/256 of a half counts as the half. Real scales come in 120ths,
    /// whose products are never nearer a half than 1/120 without being one.</para>
    /// </summary>
    public static int PhysicalSize(int logical, float scale) =>
        (int)Math.Floor(logical * (double)scale + 0.5 + 1.0 / 256);

    /// <summary>
    /// Logical pixels for a framebuffer of <paramref name="physical"/> pixels at
    /// <paramref name="scale"/>, the inverse of <see cref="PhysicalSize"/>:
    /// rounded, not truncated, so 1251 at 1.25 is the 1001 it was made from.
    /// </summary>
    public static int LogicalSize(int physical, float scale) =>
        (int)Math.Round(physical / (double)scale);
}
