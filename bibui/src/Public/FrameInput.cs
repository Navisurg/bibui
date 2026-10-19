namespace Bibui;

/// <summary>
/// Per-frame transient state: the frame's own clock. Structural inputs — the UI tree, theme, and surface — stay as
/// explicit render parameters; this record carries what changes frame to frame.
///
/// A frame is rendered from exactly one FrameInput, so every piece of
/// interaction state reaches both render paths (readback and immediate-mode)
/// by construction. To add new frame state: add a property here, capture it
/// in BibuiRuntime.CaptureFrameInput, and apply it in the painter.
/// </summary>
public sealed record FrameInput
{
    /// <summary>Static render with no interaction — verifier specs, snapshots.</summary>
    public static readonly FrameInput None = new();

    /// <summary>
    /// Seconds per frame for animation, from <c>BibuiRuntime.Dt</c>. Zero here
    /// and in <see cref="None"/>, which is what keeps verification renders
    /// deterministic — an animation asked for the first time arrives settled,
    /// and with no time passing it never leaves.
    ///
    /// This is the only place a frame's dt is stored on the render path. The
    /// drawing context holds this record and reads through it rather than
    /// copying the value into a field of its own.
    /// </summary>
    public float Dt { get; init; }

    /// <summary>
    /// Whether this frame snaps instead of animating, from
    /// <c>BibuiRuntime.ReducedMotion</c>: an eased value asked for here
    /// arrives at its target on the next frame.
    /// </summary>
    public bool ReducedMotion { get; init; }

    /// <summary>
    /// This frame's rates for exponential approach, from
    /// <c>BibuiRuntime.Rates</c>: what <c>DrawContext.Animate</c> steps by.
    /// All zero here and in <see cref="None"/>, like <see cref="Dt"/>: with no
    /// time passing, nothing moves.
    /// </summary>
    public MotionRates Rates { get; init; }
}
