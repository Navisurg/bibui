using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// A keyboard shortcut: a row of keycaps, each a small raised box with a
/// border around one key's name. It claims nothing; it only shows the keys.
/// </summary>
public static class KeyboardShortcut
{
    /// <summary>A keycap's height.</summary>
    public const float KeyHeight = 24f;

    private const float PadX = 8f;       // Spacing.Sm
    private const float KeyGap = 4f;
    private const float Radius = 4f;     // Radii.Sm

    /// <summary>
    /// Builds a row of keycaps, one for each of <paramref name="keys"/> in
    /// order, under the parent in scope and returns the row: as wide as the
    /// keycaps and the gaps between them, and one keycap tall.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, params ReadOnlySpan<string> keys)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ChildrenSum();
        row.SizeY = SemanticSize.Pixels(KeyHeight);
        using (runtime.Parent(row))
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) runtime.Gap(KeyGap);
                Key(runtime, keys[i]);
            }
        return row;
    }

    // One keycap: the key's name centred down a box as wide as it and its padding.
    private static void Key(BibuiRuntime runtime, ReadOnlySpan<char> key)
    {
        Box cap = runtime.Box();
        cap.ChildLayoutAxis = LayoutAxis.Y;
        cap.SizeX = SemanticSize.ChildrenSum();
        cap.SizeY = SemanticSize.Pixels(KeyHeight);
        Style style = cap.Style;
        style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: 0f);
        style.CornerRadius = Radius;
        style.BackgroundColor = UiColor.SurfaceRaised;
        style.BorderColor = UiColor.Border;
        style.BorderWidth = runtime.Theme.Borders.Width;
        runtime.BeginParent(cap);
        runtime.BeginCenter();
        runtime.Text(key, ComponentSize.Sm, TextColor.Primary);
        runtime.EndCenter();
        runtime.EndParent(cap);
    }
}
