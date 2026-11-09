namespace Bibui.Text.FreeType;

/// <summary>
/// Vertical metrics of a strike in physical px, y growing down, so
/// <see cref="Ascent"/> is negative and <see cref="Descent"/> positive. A line's
/// em box is <c>Descent - Ascent</c>; the baseline of text whose box top is at
/// <c>top</c> is <c>top - Ascent</c>.
/// </summary>
public readonly record struct FontMetrics(float Ascent, float Descent, float Leading);

