namespace Bibui.Core;

/// <summary>
/// The theme's standard treatments, opted into per node, and how a box's own
/// drawing is clipped. A node names the treatment rather than a colour, so
/// widening what one means reaches everything that already asked for it.
/// </summary>
[Flags]
public enum DrawFlags
{
    None = 0,

    /// <summary>An edge in the theme's border colour.</summary>
    Border = 1 << 0,

    /// <summary>
    /// The theme's hover border while the pointer is over it, and only where
    /// <see cref="Border"/> is also asked for. Follows what the ask reported,
    /// so a node that claims nothing is never hot.
    /// </summary>
    HotEffects = 1 << 1,

    /// <summary>
    /// The theme's accent while the box is held. Does nothing without
    /// <see cref="Border"/>, which is what it recolours.
    ///
    /// Also what makes <see cref="HotEffects"/> give way: hot is scaled by
    /// <c>1 - ActiveT</c> only where this and <see cref="Border"/> are both
    /// asked for, because a box that loses its hover treatment on press and
    /// puts nothing in its place just goes bare.
    /// </summary>
    ActiveEffects = 1 << 2,

    /// <summary>A fill in the theme's raised surface.</summary>
    Background = 1 << 3,

    /// <summary>
    /// The box's <see cref="Box.Draw"/> callback is not clipped to the box: its
    /// ink may reach past it, as a text's glyphs do — a glow, a highlight
    /// padded around a word, the soft end of a line. A box that claims
    /// <see cref="InteractionKinds.Clip"/>, this one or an ancestor, still cuts
    /// it; the callback can push a clip of its own for a margin. Layout and
    /// hit-testing take the box's rect, not its ink.
    /// </summary>
    Unclipped = 1 << 4,
}
