using Bibui.Core.Draw;


namespace Bibui.Core;

/// <summary>
/// bibui's icon set, and the icons an app adds to it. <c>runtime.Icon(name)</c>
/// draws any name here, built-in or registered.
/// </summary>
public static class Icons
{
    /// <summary>
    /// Adds an icon of the app's own under <paramref name="name"/>, so that
    /// <c>runtime.Icon(name)</c> draws it and <see cref="Icon.IsKnown"/> knows it.
    /// Call it at start-up, before the first frame: registration is not meant to
    /// race with drawing (reads stay safe while it happens, but a frame already
    /// built with an unknown name has already failed).
    ///
    /// <para><paramref name="svgPathData"/> is the <c>d</c> attribute of an SVG
    /// path, drawn the way the built-in (Lucide) icons are: on a 24×24 viewBox,
    /// stroked 2 units wide with round caps and joins, never filled, and scaled
    /// to the icon's size. An icon made of several elements is one path here:
    /// join their path data, each starting with an absolute <c>M</c>, and write
    /// a circle as two arcs. Supported commands: M L H V C S Q T A Z, absolute
    /// and relative.</para>
    ///
    /// <para>The path is parsed here, so bad data fails at start-up, not at
    /// paint. Names ignore case, as the built-in ones do.</para>
    /// </summary>
    /// <param name="name">The icon's name; not one of bibui's own.</param>
    /// <param name="svgPathData">Its SVG path data.</param>
    /// <exception cref="ArgumentException">
    /// The name is empty or one of bibui's own icons, or it is already
    /// registered with other path data. Registering the same name with the same
    /// path again does nothing, so a start-up routine may run twice; a different
    /// path is a clash between two parts of the app and throws rather than
    /// silently replacing an icon that is already drawn.
    /// </exception>
    /// <exception cref="FormatException">
    /// The path data does not parse, does not begin with a move, holds a number
    /// that is not finite, or draws nothing.
    /// </exception>
    public static void Register(string name, string svgPathData)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(svgPathData);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An icon needs a name.", nameof(name));
        if (IconRegistry.IsBuiltIn(name))
            throw new ArgumentException($"'{name}' is one of bibui's own icons; an app icon needs a name of its own.", nameof(name));

        try
        {
            SvgPathParser.ParseStrict(svgPathData);
        }
        catch (FormatException e)
        {
            throw new FormatException($"Icon '{name}': {e.Message}", e);
        }

        IconRegistry.Register(name, svgPathData);
    }

    /// <summary>True when <paramref name="name"/> is one of bibui's own icons (ignoring case).</summary>
    public static bool IsBuiltIn(string? name) => IconRegistry.IsBuiltIn(name);

    /// <summary>
    /// Every icon name <c>runtime.Icon</c> draws: the built-in set, then the
    /// app's registered icons, for a gallery or a picker.
    /// </summary>
    public static IReadOnlyList<string> Names => IconRegistry.All.ToArray();
}
