namespace Bibui.Core.Runtime.Platform;

/// <summary>
/// What the window is as a frame starts, handed to
/// <see cref="BibuiRuntime.BeginBuild"/>: the runtime lays the frame out on it
/// and paces animations to it. The window measures it; nothing in it refers
/// back to the window. A headless caller makes one of its own.
/// </summary>
/// <param name="Width">Framebuffer width in physical px.</param>
/// <param name="Height">Framebuffer height in physical px.</param>
/// <param name="Scale">Physical px per logical px.</param>
/// <param name="RefreshRate">The refresh rate of the display the window is on, in Hz.</param>
public readonly record struct WindowFrame(int Width, int Height, float Scale, float RefreshRate)
{
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

    /// <summary><see cref="Width"/> in logical px: what the frame is laid out in.</summary>
    public int LogicalWidth => LogicalSize(Width, Scale);

    /// <summary><see cref="Height"/> in logical px.</summary>
    public int LogicalHeight => LogicalSize(Height, Scale);
}
