namespace Bibui.Core;

/// <summary>
/// The theme's standard treatments, opted into per node. A node names the
/// treatment rather than a colour, so widening what one means reaches
/// everything that already asked for it.
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
}
