using Bibui.Core;

namespace Bibui.Core.Runtime;

/// <summary>
/// A separator: a line the theme's border width thick, in the border colour.
/// It claims nothing; it only divides.
/// </summary>
public static class Separator
{
    /// <summary>How thick the line is under the default theme; it is drawn
    /// <see cref="Bibui.Core.Theming.BorderTokens.Width"/> thick.</summary>
    public const float Thickness = 1f;

    /// <summary>
    /// Builds a separator under the parent in scope and returns its box.
    /// Horizontal, it is as wide as its parent; vertical, as tall — so a
    /// vertical one in a row the row's own height, which a row of centred
    /// items has to say. A caller that wants it shorter or longer sizes the
    /// box otherwise.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, bool vertical = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        float thickness = runtime.Theme.Borders.Width;
        Box line = runtime.Box();
        if (vertical)
        {
            line.SizeX = SemanticSize.Pixels(thickness);
            line.SizeY = SemanticSize.ParentPct(1f);
        }
        else
        {
            line.SizeX = SemanticSize.ParentPct(1f);
            line.SizeY = SemanticSize.Pixels(thickness);
        }
        line.Style.BackgroundColor = UiColor.Border;
        return line;
    }
}
