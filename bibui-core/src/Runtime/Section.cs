using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// A section: a small bold caption above what the caller builds into it, in a
/// column as wide as its parent. It claims nothing; it only groups.
/// </summary>
public static class Section
{
    /// <summary>The space under the caption, and the space a caller puts between children with <c>runtime.Gap</c>.</summary>
    public const float Spacing = 12f;

    /// <summary>
    /// Builds the section's column under the parent in scope, its caption
    /// already in it. Push the column to build its children into it.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, ReadOnlySpan<char> caption)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var column = runtime.Box();
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = SemanticSize.ParentPct(1f);
        column.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(column))
        {
            using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(caption, ComponentSize.Sm, TextColor.Secondary);
            runtime.Gap(Spacing);
        }
        return column;
    }
}
